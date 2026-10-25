using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

public class CompleteRequest
{
    public List<TableDto> Tables { get; set; } = new();
    public string Code { get; set; } = "";
    public int Position { get; set; }
}

/// <summary>
/// Uses Roslyn's real completion engine. Your tables and code are wrapped in a normal class/method
/// (tables become typed locals) so completion sees exactly the types your query will run against.
/// </summary>
static class Intellisense
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    static readonly Lazy<MefHostServices> Host = new(() => MefHostServices.Create(new[]
    {
        Assembly.Load("Microsoft.CodeAnalysis.Workspaces"),
        Assembly.Load("Microsoft.CodeAnalysis.CSharp.Workspaces"),
        Assembly.Load("Microsoft.CodeAnalysis.Features"),
        Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features"),
    }));

    static readonly Lazy<MetadataReference[]> Refs = new(() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Where(a =>
            {
                var n = a.GetName().Name ?? "";
                return n.StartsWith("System") || n == "netstandard" || n == "mscorlib" || n == "Microsoft.CSharp";
            })
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToArray());

    // Most recent result, kept so the "detail" endpoint can describe the highlighted item.
    static AdhocWorkspace lastWs;
    static Document lastDoc;
    static CompletionService lastSvc;
    static CompletionList lastList;

    public static async Task<object> CompleteAsync(CompleteRequest req)
    {
        await Gate.WaitAsync();
        try
        {
            var code = req.Code ?? "";
            var pos = Math.Clamp(req.Position, 0, code.Length);
            var (text, offset) = BuildSource(req.Tables, code, pos);

            lastWs?.Dispose();
            var ws = new AdhocWorkspace(Host.Value);
            lastWs = ws;

            var project = ws.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Create(), "Playground", "Playground", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
                metadataReferences: Refs.Value));
            var doc = ws.AddDocument(project.Id, "Playground.cs", SourceText.From(text));

            var svc = CompletionService.GetService(doc);
            if (svc == null) return new { items = Array.Empty<object>() };

            var list = await svc.GetCompletionsAsync(doc, offset);
            lastDoc = doc; lastSvc = svc; lastList = list;

            var items = list.Items
                .Select((it, i) => new { it, i })
                .OrderBy(x => x.it.SortText, StringComparer.Ordinal)
                .Take(400)
                .Select(x => new
                {
                    i = x.i,
                    text = x.it.DisplayText,
                    filter = x.it.FilterText,
                    kind = x.it.Tags.FirstOrDefault() ?? ""
                })
                .ToList();
            return new { items };
        }
        catch (Exception ex)
        {
            return new { items = Array.Empty<object>(), error = ex.Message };
        }
        finally { Gate.Release(); }
    }

    public static async Task<string> DetailAsync(int i)
    {
        await Gate.WaitAsync();
        try
        {
            if (lastList == null || lastSvc == null || lastDoc == null || i < 0 || i >= lastList.Items.Length) return "";
            var d = await lastSvc.GetDescriptionAsync(lastDoc, lastList.Items[i]);
            return d?.Text ?? "";
        }
        catch { return ""; }
        finally { Gate.Release(); }
    }

    public static async Task WarmUpAsync()
    {
        const string c = "var x = new List<int>();\nx.";
        await CompleteAsync(new CompleteRequest { Code = c, Position = c.Length });
    }

    static (string text, int offset) BuildSource(List<TableDto> tables, string code, int pos)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System; using System.Collections.Generic; using System.Linq; using System.Text; using System.Globalization;");

        var locals = new StringBuilder();
        foreach (var t in tables ?? new List<TableDto>())
        {
            // Be forgiving: half-edited tables just get skipped instead of breaking completion.
            if (!Playground.Ident.IsMatch(t.Name ?? "") || t.Columns == null || t.Columns.Count == 0) continue;
            var cols = t.Columns
                .Where(c => Playground.Ident.IsMatch(c.Name ?? "") && Playground.AllowedTypes.Contains(c.Type ?? ""))
                .GroupBy(c => c.Name).Select(g => g.First()).ToList();
            if (cols.Count == 0) continue;

            var cn = Playground.ClassName(t.Name);
            sb.Append("public class ").Append(cn).Append(" { ");
            foreach (var c in cols)
                sb.Append("public ").Append(c.Type).Append(' ').Append(c.Name).Append(" { get; set; } ");
            sb.AppendLine("}");
            locals.Append("List<").Append(cn).Append("> ").Append(t.Name).Append(" = null; ");
        }

        sb.AppendLine("public static class __Playground { public static void __Run() {");
        sb.AppendLine(locals.ToString());
        var offset = sb.Length + pos;
        sb.Append(code);
        sb.AppendLine();
        sb.AppendLine("} }");
        return (sb.ToString(), offset);
    }
}
