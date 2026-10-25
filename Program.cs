using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5055"); // localhost only: this app runs arbitrary C#
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var gate = new SemaphoreSlim(1, 1); // Console.SetOut is global, so run one query at a time

app.MapPost("/api/run", async (RunRequest req) =>
{
    await gate.WaitAsync();
    var originalOut = Console.Out;
    var consoleBuffer = new StringWriter();
    Console.SetOut(consoleBuffer);
    var sw = Stopwatch.StartNew();
    try
    {
        var work = Task.Run(() => Playground.RunAsync(req));
        var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(10)));
        if (finished != work)
        {
            return Results.Json(new
            {
                ok = false,
                error = "Timed out after 10 seconds (infinite loop?). If CPU stays busy, restart the app.",
                console = consoleBuffer.ToString(),
                elapsedMs = sw.ElapsedMilliseconds
            });
        }

        var outcome = await work;
        return Results.Json(new
        {
            ok = outcome.Error == null,
            result = outcome.Result,
            error = outcome.Error,
            console = consoleBuffer.ToString(),
            elapsedMs = sw.ElapsedMilliseconds
        });
    }
    finally
    {
        Console.SetOut(originalOut);
        gate.Release();
    }
});

app.MapPost("/api/complete", async (CompleteRequest req) => Results.Json(await Intellisense.CompleteAsync(req)));
app.MapGet("/api/complete/detail", async (int i) => Results.Json(new { text = await Intellisense.DetailAsync(i) }));
_ = Task.Run(Intellisense.WarmUpAsync); // load Roslyn's completion engine in the background so the first popup is fast

Console.WriteLine("LINQ Playground running at http://localhost:5055");
app.Run();

// ---------------------------------------------------------------- DTOs

public class RunRequest
{
    public List<TableDto> Tables { get; set; } = new();
    public string Code { get; set; } = "";
}

public class TableDto
{
    public string Name { get; set; } = "";
    public List<ColumnDto> Columns { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
}

public class ColumnDto
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
}

/// <summary>Script globals: holds each table's JSON so the generated prelude can deserialize it.</summary>
public class Globals
{
    public Dictionary<string, string> __Data;
}

public class Outcome
{
    public object Result { get; set; }
    public string Error { get; set; }
}

// ---------------------------------------------------------------- engine

static class Playground
{
    public static readonly Regex Ident = new(@"^[A-Za-z_][A-Za-z0-9_]*$");

    public static readonly HashSet<string> AllowedTypes = new()
    {
        "int", "int?", "long", "long?", "decimal", "decimal?", "double", "double?",
        "bool", "bool?", "string", "DateTime", "DateTime?"
    };

    static readonly Lazy<Assembly[]> References = new(() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .ToArray());

    public static async Task<Outcome> RunAsync(RunRequest req)
    {
        string prelude;
        Dictionary<string, string> data;
        try
        {
            (prelude, data) = BuildPrelude(req.Tables);
        }
        catch (Exception ex)
        {
            return new Outcome { Error = "Table setup error: " + ex.Message };
        }

        var options = ScriptOptions.Default
            .WithReferences(References.Value)
            .WithImports("System", "System.Collections.Generic", "System.Linq", "System.Text", "System.Globalization");

        try
        {
            // Submission 1: classes + table variables. Submission 2: your code (so error line numbers match the editor).
            var state = await CSharpScript.RunAsync(prelude, options, new Globals { __Data = data }, typeof(Globals));
            state = await state.ContinueWithAsync(req.Code);

            // Normalize inside the try: this is where deferred LINQ queries actually execute.
            return new Outcome { Result = Normalize(state.ReturnValue, 0) };
        }
        catch (CompilationErrorException ex)
        {
            return new Outcome { Error = string.Join("\n", ex.Diagnostics.Select(d => d.ToString())) };
        }
        catch (Exception ex)
        {
            var e = ex is AggregateException ae && ae.InnerException != null ? ae.InnerException : ex;
            return new Outcome { Error = $"{e.GetType().Name}: {e.Message}" };
        }
    }

    // Employees -> Employee, Categories -> Category, Status -> StatusItem, Data -> DataItem
    public static string ClassName(string table)
    {
        if (Regex.IsMatch(table, "ies$", RegexOptions.IgnoreCase) && table.Length > 3) return table[..^3] + "y";
        if (Regex.IsMatch(table, "[^s]s$", RegexOptions.IgnoreCase)) return table[..^1];
        return table + "Item";
    }

    static (string, Dictionary<string, string>) BuildPrelude(List<TableDto> tables)
    {
        var sb = new StringBuilder();
        var data = new Dictionary<string, string>();
        var seen = new HashSet<string>();

        foreach (var t in tables)
        {
            if (!Ident.IsMatch(t.Name ?? ""))
                throw new Exception($"'{t.Name}' is not a valid table name (letters, digits, underscore; can't start with a digit).");
            if (!seen.Add(t.Name))
                throw new Exception($"Duplicate table name '{t.Name}'.");
            if (t.Columns == null || t.Columns.Count == 0)
                throw new Exception($"Table '{t.Name}' needs at least one column.");
            t.Rows ??= new();

            var colSeen = new HashSet<string>();
            foreach (var c in t.Columns)
            {
                if (!Ident.IsMatch(c.Name ?? ""))
                    throw new Exception($"'{c.Name}' in table '{t.Name}' is not a valid column name.");
                if (!colSeen.Add(c.Name))
                    throw new Exception($"Duplicate column '{c.Name}' in table '{t.Name}'.");
                if (!AllowedTypes.Contains(c.Type ?? ""))
                    throw new Exception($"Unsupported type '{c.Type}' for {t.Name}.{c.Name}.");
            }

            var cn = ClassName(t.Name);
            sb.Append("public class ").Append(cn).Append(" { ");
            foreach (var c in t.Columns)
                sb.Append("public ").Append(c.Type).Append(' ').Append(c.Name).Append(" { get; set; } ");
            sb.AppendLine("}");

            var list = new List<Dictionary<string, object>>();
            for (int r = 0; r < t.Rows.Count; r++)
            {
                var row = t.Rows[r] ?? new List<string>();
                if (row.All(string.IsNullOrWhiteSpace)) continue; // ignore fully blank rows

                var d = new Dictionary<string, object>();
                for (int i = 0; i < t.Columns.Count; i++)
                {
                    var col = t.Columns[i];
                    d[col.Name] = ParseCell(i < row.Count ? row[i] : "", col.Type,
                        $"{t.Name}, row {r + 1}, column '{col.Name}'");
                }
                list.Add(d);
            }
            data[t.Name] = JsonSerializer.Serialize(list);

            sb.AppendLine(
                $"var {t.Name} = System.Text.Json.JsonSerializer.Deserialize<List<{cn}>>(__Data[\"{t.Name}\"], " +
                "new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });");
        }

        return (sb.ToString(), data);
    }

    static object ParseCell(string raw, string type, string where)
    {
        raw ??= "";
        bool nullable = type.EndsWith("?");
        var baseType = nullable ? type[..^1] : type;

        if (baseType == "string") return raw;

        var s = raw.Trim();
        if (s == "")
        {
            if (nullable) return null;
            throw new Exception($"{where}: empty value for non-nullable {type} (use a '?' type to allow null).");
        }

        var inv = CultureInfo.InvariantCulture;
        try
        {
            switch (baseType)
            {
                case "int": return (object)int.Parse(s, inv);
                case "long": return (object)long.Parse(s, inv);
                case "decimal": return (object)decimal.Parse(s, inv);
                case "double": return (object)double.Parse(s, inv);
                case "bool": return (object)bool.Parse(s);
                case "DateTime": return (object)DateTime.Parse(s, inv);
            }
        }
        catch (FormatException)
        {
            throw new Exception($"{where}: '{raw}' is not a valid {type}.");
        }
        catch (OverflowException)
        {
            throw new Exception($"{where}: '{raw}' is out of range for {type}.");
        }
        throw new Exception($"{where}: unsupported type {type}.");
    }

    // Turns any LINQ result into plain data (lists / dictionaries / primitives) the browser can render.
    static object Normalize(object v, int depth)
    {
        if (v == null) return null;
        if (depth > 6) return v.ToString();

        switch (v)
        {
            case string: case bool: case char: case sbyte: case byte: case short: case ushort:
            case int: case uint: case long: case ulong: case float: case double: case decimal:
                return v;
            case DateTime dt:
                return dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm:ss");
            case DateTimeOffset or TimeSpan or Guid or Enum:
                return v.ToString();
        }

        var t = v.GetType();

        // IGrouping<K, T> (result of GroupBy): show Key + Items
        var gi = t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IGrouping<,>));
        if (gi != null)
        {
            var key = gi.GetProperty("Key").GetValue(v);
            return new Dictionary<string, object>
            {
                ["Key"] = Normalize(key, depth + 1),
                ["Items"] = ((IEnumerable)v).Cast<object>().Select(x => Normalize(x, depth + 1)).ToList()
            };
        }

        if (v is IDictionary dict)
        {
            var d = new Dictionary<string, object>();
            foreach (DictionaryEntry e in dict) d[Convert.ToString(e.Key)] = Normalize(e.Value, depth + 1);
            return d;
        }

        if (v is ITuple tup)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < tup.Length; i++) d["Item" + (i + 1)] = Normalize(tup[i], depth + 1);
            return d;
        }

        if (v is IEnumerable en)
        {
            var list = new List<object>();
            foreach (var x in en)
            {
                list.Add(Normalize(x, depth + 1));
                if (list.Count >= 2000) break; // safety cap
            }
            return list;
        }

        var bf = BindingFlags.Public | BindingFlags.Instance;
        var o = new Dictionary<string, object>();
        foreach (var p in t.GetProperties(bf).Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
            o[p.Name] = Normalize(p.GetValue(v), depth + 1);
        foreach (var f in t.GetFields(bf))
            o[f.Name] = Normalize(f.GetValue(v), depth + 1);
        return o.Count == 0 ? v.ToString() : o;
    }
}
