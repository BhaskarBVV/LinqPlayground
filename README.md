# LINQ Playground

1. Install the .NET 8 SDK (or change `TargetFramework` in the .csproj to your version).
2. `dotnet run`
3. Open http://localhost:5055

Each table becomes a `List<T>` variable (e.g. table `Employees` -> `List<Employee> Employees`).
The last expression without a semicolon is displayed as the result.
This app executes arbitrary C#; it only listens on localhost - don't expose it.
