using System.Text.Json;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.State;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent with no window (<c>--mcp</c>) finding its way through C# code: the solution's own symbols found
/// without a source generator's, the name itself first, usages read by file with the symbol the place took, a generator's
/// place (a usage, a declaration) in a copy of its code with its line's text, a place with no symbol said to be one, or
/// the symbol its line declares taken; symbols named rather than placed, found exactly, each overload told apart by its
/// documentation id, an indexer by this[] or Item; a hover card as plain text, and a fix's Suppress and Configure choices
/// counted until asked for; calls on many files leaving open, in the IDE and the language server, only the file the agent
/// opened and the one it left unsaved; and a type a code action has just moved known at once in its new file.</summary>
public sealed class McpNavigationTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpNavigationTests(RestoredFolders folders)
        : base(folders)
    {
    }

    private const string Words = "using System.Text.RegularExpressions;\n\nstatic partial class Words\n{\n"
        + "    [GeneratedRegex(@\"\\w+\")]\n    private static partial Regex Word();\n\n"
        + "    public static int Count(string text) => Word().Count(text);\n\n"
        + "    public static int Count(string text, int from) => Word().Count(text[from..]);\n}\n";

    private const string Shapes = "using System.Text.Json.Serialization;\n\n[JsonSerializable(typeof(int))]\npartial class ShapeContext : JsonSerializerContext\n{\n}\n\n"
        + "static class UsesContext\n{\n    public static object Info => ShapeContext.Default.Int32;\n}\n";

    private const string Tally = "static class Tally\n{\n    public static int Of(string text) => Words.Count(text) + Words.Count(text, 1);\n\n"
        + "    public static int CountAll(string[] texts) => texts.Sum(Words.Count);\n}\n";

    /// <summary>A restored app and the library a solution filter leaves out of it, neither built.</summary>
    private async Task<string> AppWithoutItsLibraryAsync()
    {
        string folder = AppAndLibrary();
        await Restore.RunAsync(Path.Combine(folder, "Shop.slnx"));
        return folder;
    }

    /// <summary>An app and the library a solution filter leaves out of it, not yet restored.</summary>
    private string AppAndLibrary()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-unloaded-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "Lib/Lib.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Lib/Greeter.cs", "namespace Lib;\n\npublic sealed class Greeter(string who)\n{\n    public string Who { get; } = who;\n\n"
            + "    public string Greet(string greeting) => $\"{greeting}, {Who}\";\n}\n");
        TestFolders.Write(folder, "App/App.csproj", TestProjects.Console().With("  <ItemGroup>\n    <ProjectReference Include=\"../Lib/Lib.csproj\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "App/Program.cs", "Lib.Greeter greeter = new(\"Ada\");\nSystem.Console.WriteLine(greeter.Greet(\"Hello\") + greeter.Who);\n");
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "App.slnf", "{\n  \"solution\": {\n    \"path\": \"Shop.slnx\",\n    \"projects\": [ \"App\\\\App.csproj\" ]\n  }\n}\n");
        return folder;
    }

    /// <summary>A MAUI page's code-behind calls the InitializeComponent MAUI's source generator declares of its XAML: definition
    /// goes to that declaration in a copy of the generator's code the agent can read (the read-only copy Go to Declaration
    /// opens), at its line with that line's code, asked first thing of a folder never restored: the server's restore as it
    /// loads brings MAUI's build files and generator, so the answer waits for the projects' load after it.</summary>
    [Fact]
    public async Task Definition_of_a_maui_pages_initialize_component_answers_the_generators_declaration_in_a_copy_of_its_code()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-maui-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "MauiApp1.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <RootNamespace>MauiApp1</RootNamespace>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Maui.Controls" Version="{AvaloniaApp.Metadata("MauiVersion")}" />
              </ItemGroup>
            </Project>

            """);
        TestFolders.Write(folder, "MainPage.xaml", MauiXamlTests.Gallery.MainPage);
        TestFolders.Write(folder, "MainPage.xaml.cs", MauiXamlTests.Gallery.MainPageCode);
        await using McpClient client = await Start(["--mcp", folder]);

        JsonElement made = Assert.Single((await Call(client, McpTools.Definition, new() { ["path"] = "MainPage.xaml.cs", ["line"] = 9, ["column"] = 9 })).EnumerateArray());
        string copy = made.GetProperty("path").GetString()!;
        Assert.True(copy.EndsWith($"{Path.DirectorySeparatorChar}MainPage.xaml.sg.cs", StringComparison.Ordinal)
            && made.GetProperty("text").GetString() == "private partial void InitializeComponent();"
            && File.ReadAllLines(copy)[made.GetProperty("line").GetInt32() - 1].Contains("InitializeComponent()", StringComparison.Ordinal),
            $"definition of a MAUI page's InitializeComponent answered no place in the generator's code with its text: {made.GetRawText()}");
    }

    /// <summary>Two projects each given a package once loaded: the server loads both again and restores both, one restore
    /// failing (its package no feed holds), the other bringing code by the package's build files, which its project loads
    /// again with after. An agent's question of that code asked while the restores run waits for that load, whatever the
    /// other's restore did, so definition of the package's class goes to its declaration in the package.</summary>
    [Fact]
    public async Task Definition_of_what_a_restore_brings_waits_for_its_projects_load_again_beside_a_restore_that_fails()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-restored-"));
        DeleteAtEnd(folder);
        await using StandInFeed feed = new();
        feed.Add("Passenger.Sample", "1.0.0", new Dictionary<string, string>
        {
            ["build/Passenger.Sample.targets"] = "<Project>\n  <ItemGroup>\n    <Compile Include=\"$(MSBuildThisFileDirectory)Brought.cs\" />\n  </ItemGroup>\n</Project>\n",
            ["build/Brought.cs"] = "namespace Sample;\n\npublic static class Brought\n{\n    public static string Say() => \"brought\";\n}\n",
        });
        TaskCompletionSource answering = new(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.Hold("Passenger.Missing", answering.Task);
        // Only the stand-in, and restores kept out of the person's own package cache.
        TestFolders.Write(folder, "nuget.config", $"<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"stand-in\" value=\"{feed.Index}\" protocolVersion=\"3\" allowInsecureConnections=\"true\" />\n"
            + "  </packageSources>\n  <config>\n    <add key=\"globalPackagesFolder\" value=\"packages\" />\n  </config>\n</configuration>\n");
        TestFolders.Write(folder, "Uses/Uses.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Uses/Caller.cs", "namespace Uses;\n\npublic static class Caller\n{\n    public static string Call() => Sample.Brought.Say();\n}\n");
        TestFolders.Write(folder, "Lacks/Lacks.csproj", TestProjects.Library());
        await using McpClient client = await Start(["--mcp", folder]);
        Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Caller", ["exact"] = true })).EnumerateArray());

        TestFolders.Write(folder, "Uses/Uses.csproj", TestProjects.Library().With("  <ItemGroup>\n    <PackageReference Include=\"Passenger.Sample\" Version=\"1.0.0\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "Lacks/Lacks.csproj", TestProjects.Library().With("  <ItemGroup>\n    <PackageReference Include=\"Passenger.Missing\" Version=\"1.0.0\" />\n  </ItemGroup>\n"));
        Assert.True(await Waiting.UntilAsync(() => feed.Requests.Any(r => r.Path.StartsWith("/flat/passenger.missing/", StringComparison.Ordinal)), TimeSpan.FromMinutes(3), Cancel),
            $"Lacks's restore never asked the feed for its package{Said()}");
        Heard heard = new();
        Task<CallToolResult> asked = Result(client, McpTools.Definition, new() { ["path"] = "Uses/Caller.cs", ["line"] = 5, ["column"] = 43 }, heard: heard);
        // Told of the call a moment on, the agent's question waits on the restores; Lacks's then fails, no feed holding its package.
        Assert.True(await Waiting.UntilAsync(() => heard.Told.Count > 0 || asked.IsCompleted, TimeSpan.FromMinutes(1), Cancel), $"The agent's definition was never under way{Said()}");
        answering.SetResult();
        string said = Text(await asked);
        Assert.True(said.Contains($"{Path.DirectorySeparatorChar}Brought.cs", StringComparison.Ordinal) || said.Contains("/Brought.cs", StringComparison.Ordinal),
            $"definition of a class a restore brought, asked as it ran beside a restore that failed, answered: {said}");
    }

    /// <summary>The solution a filter leaves a library out of, as a fresh clone opens it: the assembly of the library is
    /// not there as C# loads the app, so nothing of it is known, and the first build, however quick, takes it in with no
    /// restart, what the agent asks next finding the library's types; each build after that brings what it added.</summary>
    [Fact]
    public async Task A_first_build_of_a_project_a_solution_filter_leaves_out_takes_its_assembly_in_and_each_build_after_it_what_it_added()
    {
        string folder = await AppWithoutItsLibraryAsync();
        await using McpClient client = await Start(["--mcp", Path.Combine(folder, "App.slnf")]);

        Assert.Contains("CS0246", await ErrorsAsync());
        await BuiltAsync();
        Assert.Empty(await ErrorsAsync());
        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 7), await DeclaredAsync());

        TestFolders.Write(folder, "Lib/Greeter.cs", File.ReadAllText(Path.Combine(folder, "Lib/Greeter.cs")).Replace("    public string Who", "    public string Twice() => Who + Who;\n\n    public string Who", StringComparison.Ordinal));
        await Call(client, McpTools.Edit, new() { ["path"] = "App/Program.cs", ["oldText"] = "greeter.Who);", ["newText"] = "greeter.Twice());" });
        Assert.Contains("CS1061", await ErrorsAsync());
        await BuiltAsync();
        Assert.Empty(await ErrorsAsync());

        async Task<string[]> ErrorsAsync() => [.. (await Call(client, McpTools.Diagnostics, new() { ["path"] = "App/Program.cs" })).EnumerateArray()
            .Where(d => d.GetProperty("severity").GetString() == "error").Select(d => d.GetProperty("code").GetString()!)];

        async Task BuiltAsync()
        {
            JsonElement built = await Call(client, McpTools.Build);
            Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The filter's app did not build with the project it leaves out: {built}");
        }

        async Task<(string Path, int Line)> DeclaredAsync()
        {
            JsonElement place = Assert.Single((await Call(client, McpTools.Definition, new() { ["path"] = "App/Program.cs", ["line"] = 2, ["column"] = 34 })).EnumerateArray());
            return (place.GetProperty("path").GetString()!, place.GetProperty("line").GetInt32());
        }
    }

    /// <summary>What build answers holds whenever the language server hears of the assembly it wrote: told as the build goes
    /// on, the server reads it only at the build's end, so what diagnostics answered between the two is no answer to ask
    /// after it, with nothing else said to the server since. (The server's own read of it may come first: nothing here asks
    /// what the ask under way answered.)</summary>
    [Fact]
    public async Task Diagnostics_asked_while_a_build_goes_on_do_not_outlast_it_once_it_has_answered_with_what_it_wrote()
    {
        string folder = await AppWithoutItsLibraryAsync();
        // The app's compile, after the library's, held once the test puts "hold" there until it puts "go" (ten minutes at most).
        TestFolders.Write(folder, "Directory.Build.targets", """
            <Project>
              <Target Name="Held" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true' And '$(MSBuildProjectName)' == 'App' And Exists('$(MSBuildThisFileDirectory)hold')">
                <Exec Condition="'$(OS)' != 'Windows_NT'" WorkingDirectory="$(MSBuildThisFileDirectory)" Command="rm -f hold; touch held; n=0; while [ ! -f go ] &amp;&amp; [ $n -lt 2400 ]; do sleep 0.25; n=`expr $n + 1`; done" />
                <Exec Condition="'$(OS)' == 'Windows_NT'" WorkingDirectory="$(MSBuildThisFileDirectory)" Command="powershell -NoProfile -Command &quot;Remove-Item hold; New-Item held -ItemType File | Out-Null; $n = 0; while (-not (Test-Path go) -and $n -lt 2400) { Start-Sleep -Milliseconds 250; $n++ }&quot;" />
              </Target>
            </Project>
            """);
        await using McpClient client = await Start(["--mcp", Path.Combine(folder, "App.slnf")]);
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["traceProtocols"] = true } });
        string trace = Path.Combine(OwnState, "logs", "lsp.log");
        await ErrorsAsync();
        await BuiltAsync(Call(client, McpTools.Build));

        TestFolders.Write(folder, "Lib/Greeter.cs", File.ReadAllText(Path.Combine(folder, "Lib/Greeter.cs")).Replace("    public string Who", "    public string Twice() => Who + Who;\n\n    public string Who", StringComparison.Ordinal));
        await Call(client, McpTools.Edit, new() { ["path"] = "App/Program.cs", ["oldText"] = "greeter.Who);", ["newText"] = "greeter.Twice());" });
        await ErrorsAsync();
        int told = Told(trace);
        TestFolders.Write(folder, "hold", string.Empty);
        Task<JsonElement> building = Call(client, McpTools.Build);
        await Until(() => File.Exists(Path.Combine(folder, "held")) && Told(trace) > told, "the build to reach the app's compile with the language server told of the library's assembly");
        await ErrorsAsync();
        TestFolders.Write(folder, "go", string.Empty);
        await BuiltAsync(building);

        string[] errors = await ErrorsAsync();
        Assert.True(errors.Length == 0, $"build answered, but diagnostics still said what it said before the build took in the method the library gained: {string.Join(", ", errors)}");

        async Task<string[]> ErrorsAsync() => [.. (await Call(client, McpTools.Diagnostics, new() { ["path"] = "App/Program.cs" })).EnumerateArray()
            .Where(d => d.GetProperty("severity").GetString() == "error").Select(d => d.GetProperty("code").GetString()!)];

        static async Task BuiltAsync(Task<JsonElement> asked)
        {
            JsonElement built = await asked;
            Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The filter's app did not build with the project it leaves out: {built}");
        }
    }

    /// <summary>How many times lsp.log's trace says the language server was told the library's reference assembly changed.</summary>
    private static int Told(string trace)
    {
        using FileStream read = new(trace, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(read);
        return reader.ReadToEnd().Split('\n').Count(l => l.Contains("] --> ", StringComparison.Ordinal)
            && l.Contains($"\"method\":\"{Passenger.Lsp.LspMethods.DidChangeWatchedFiles}\"", StringComparison.Ordinal) && l.Contains("/ref/Lib.dll\"", StringComparison.Ordinal));
    }

    /// <summary>Unload Project takes the project out of the language server: nothing is answered from the text the server
    /// read of it, so Go to Declaration from what references it finds the line where it is now, its symbols are found no
    /// more, and a file come under it is neither waited for nor brings it back; Reload Project has it loaded again.</summary>
    [Fact]
    public async Task Unload_Project_takes_a_project_out_of_the_language_server_until_Reload_Project_loads_it_again()
    {
        string folder = await AppWithoutItsLibraryAsync();
        await using McpClient client = await Start(["--mcp", Path.Combine(folder, "Shop.slnx")]);
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The solution did not build: {built}");
        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 7), await DeclaredAsync());
        Assert.NotEmpty(await FoundAsync("Greeter"));

        await ChosenAsync(AppActions.UnloadProject);
        Assert.Empty(await FoundAsync("Greeter"));
        TestFolders.Write(folder, "Lib/Greeter.cs", "// A line above.\n" + File.ReadAllText(Path.Combine(folder, "Lib/Greeter.cs")));
        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 8), await DeclaredAsync());

        TestFolders.Write(folder, "Lib/Fresh.cs", "namespace Lib;\n\npublic sealed class Fresh;\n");
        CallToolResult none = await Result(client, McpTools.SymbolInfo, new() { ["symbol"] = "Fresh" });
        Assert.True(none.IsError == true && Text(none).Contains("No symbol of the solution's own code is named Fresh", StringComparison.Ordinal),
            $"A file come under an unloaded project was waited for, or loaded the project again: {Text(none)}");

        await ChosenAsync(AppActions.ReloadProject);
        await Until(async () => (await FoundAsync("Fresh")).Length > 0, "Reload Project to load the project with the file come under it");

        async Task<(string Path, int Line)> DeclaredAsync()
        {
            JsonElement place = Assert.Single((await Call(client, McpTools.Definition, new() { ["path"] = "App/Program.cs", ["line"] = 2, ["column"] = 34 })).EnumerateArray());
            return (place.GetProperty("path").GetString()!, place.GetProperty("line").GetInt32());
        }

        async Task<JsonElement[]> FoundAsync(string name) => [.. (await Call(client, McpTools.FindSymbol, new() { ["query"] = name })).EnumerateArray()];

        // The Explorer's row chosen, which is what the action acts on.
        async Task ChosenAsync(string action)
        {
            await Call(client, McpTools.AppClick, new() { ["app"] = "ide", ["control"] = "ListBox#Rows/\"Lib\"" });
            await Call(client, McpTools.IdeAction, new() { ["action"] = action });
        }
    }

    /// <summary>A window keeping the folder's state takes up the language server started ahead, which loaded every project
    /// before anything read what the folder keeps: the project the person left unloaded is taken out of it, so the server
    /// holds what the window says is loaded. An agent's own Passenger keeps no state, so this window is built by hand.</summary>
    [AvaloniaFact]
    public void A_project_kept_unloaded_leaves_the_language_server_a_window_takes_up_from_its_head_start()
    {
        string folder = AppAndLibrary();
        Restore.Run(Path.Combine(folder, "Shop.slnx"));
        string lib = Path.Combine(folder, "Lib", "Lib.csproj");
        string state = PathNames.Canonical(TestFolders.Create("mcp-kept-"));
        DeleteAtEnd(state);
        TestFolders.Write(ProjectState.FolderOf(state, folder), "workspace.json", JsonSerializer.Serialize(new { version = 1, unloadedProjects = new[] { lib } }));
        LaunchOptions launch = LaunchOptions.Parse([Path.Combine(folder, "Shop.slnx")]);
        MainWindow window = new() { Width = 1200, Height = 800, AgentsAllowed = false, AgentsOwn = true, StateFolder = state, HeadStart = HeadStart.Begin(launch, languageServer: true) };
        try
        {
            window.Show();
            window.KeepLayout();
            window.Start(launch);
            Ui.WaitFor(() => window.CSharpService?.Session is { IsLoaded: true }, "the language server started ahead to load the projects", TimeSpan.FromMinutes(2));
            Assert.Equal(["App"], window.Session.Projects.Select(p => p.Name));
            Ui.WaitFor(() => Found("Greeter") == 0, () => $"Lib, kept unloaded, to leave the language server (it knows {Found("Greeter")} Greeter)", TimeSpan.FromMinutes(1));
        }
        finally
        {
            window.Close();
        }

        int Found(string name) => Ui.Await(window.CSharpService!.Session!.SymbolsNamedAsync(name, 5), $"the language server's symbols named {name}", TimeSpan.FromMinutes(1)).Symbols.Count;
    }

    /// <summary>A symbol of a project a solution filter leaves out, which C# reads only as its built assembly, is declared in
    /// that project's own file, never in the server's stub of the assembly: a method, a property and a primary constructor,
    /// and a method whose source takes another parameter than the assembly built before it; the framework's still a stub.</summary>
    [Fact]
    public async Task Go_to_declaration_into_a_project_a_solution_filter_leaves_out_opens_its_source_at_the_declaration()
    {
        string folder = await AppWithoutItsLibraryAsync();
        await using McpClient client = await Start(["--mcp", Path.Combine(folder, "App.slnf")]);
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The filter's app did not build with the project it leaves out: {built}");

        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 7), await DeclaredAsync(client, 2, 34));
        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 5), await DeclaredAsync(client, 2, 59));
        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 3), await DeclaredAsync(client, 1, 23));
        // Its source changed since its assembly was built: the method of that name, however many parameters it takes now.
        TestFolders.Write(folder, "Lib/Greeter.cs", File.ReadAllText(Path.Combine(folder, "Lib/Greeter.cs")).Replace("Greet(string greeting)", "Greet(string greeting, int times = 1)", StringComparison.Ordinal));
        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 7), await DeclaredAsync(client, 2, 34));
        // Beside an overload taking as many parameters of another type, the one the assembly's signature names, alone.
        TestFolders.Write(folder, "Lib/Greeter.cs", File.ReadAllText(Path.Combine(folder, "Lib/Greeter.cs")).Replace("    public string Greet(string greeting, int times = 1)",
            "    public string Greet(int times) => times.ToString();\n\n    public string Greet(string greeting)", StringComparison.Ordinal));
        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 9), await DeclaredAsync(client, 2, 34));
        (string console, _) = await DeclaredAsync(client, 2, 16);
        Assert.Contains("MetadataAsSource", console, StringComparison.Ordinal);

        async Task<(string Path, int Line)> DeclaredAsync(McpClient asking, int line, int column)
        {
            JsonElement place = Assert.Single((await Call(asking, McpTools.Definition, new() { ["path"] = "App/Program.cs", ["line"] = line, ["column"] = column })).EnumerateArray());
            return (place.GetProperty("path").GetString()!, place.GetProperty("line").GetInt32());
        }
    }

    [Fact]
    public async Task An_agent_finds_the_solutions_own_symbols_reads_usages_in_order_and_hears_when_no_symbol_is_where_it_asked()
    {
        await TakeFolderAsync();
        Write("Words.cs", Words);
        Write("Aaa.cs", Tally);
        Write("Shapes.cs", Shapes);
        await using McpClient client = await Start(["--mcp", Folder]);

        // The regex generator declares Words and Word again in a file on no disk, which the agent could never open.
        JsonElement[] found = [.. (await Call(client, McpTools.FindSymbol, new() { ["query"] = "Word" })).EnumerateArray()];
        Assert.True(found.Length > 0 && found.All(s => s.GetProperty("path").GetString() == "Words.cs"),
            $"find_symbol Word answered places beyond the solution's own files: {string.Join(", ", found.Select(s => s.GetRawText()))}");

        JsonElement used = await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 34 });
        (string Path, int Line, int Column, string Text)[] uses = [.. Used(used)];
        Assert.Equal(["Words.cs:6", "Words.cs:8", "Words.cs:10"], uses.Take(3).Select(u => $"{u.Path}:{u.Line}"));
        // The generator's declaration last, in a copy of its code the agent can read, at its line with that line's code.
        Assert.True(uses.Length == 4 && uses[3].Path.EndsWith($"{Path.DirectorySeparatorChar}RegexGenerator.g.cs", StringComparison.Ordinal)
            && uses[3].Text.Contains("Regex Word()", StringComparison.Ordinal) && File.ReadAllLines(uses[3].Path)[uses[3].Line - 1].Contains("Regex Word()", StringComparison.Ordinal),
            $"usages of a GeneratedRegex method answered no readable place in the generator's code: {string.Join(", ", uses.Select(u => $"{u.Path}:{u.Line} {u.Text}"))}");
        Assert.Equal("Words.Word() (method)", used.GetProperty("symbol").GetString());
        JsonElement made = Assert.Single((await Call(client, McpTools.Definition, new() { ["path"] = "Shapes.cs", ["line"] = 10, ["column"] = 48 })).EnumerateArray());
        Assert.True(made.GetProperty("path").GetString()!.EndsWith($"{Path.DirectorySeparatorChar}ShapeContext.g.cs", StringComparison.Ordinal)
            && made.GetProperty("text").GetString()!.Contains(" Default ", StringComparison.Ordinal),
            $"definition of a JSON context's Default answered no place in the generator's code with its text: {made.GetRawText()}");

        // By file, each named once, then line, whichever the server found first, after the symbol the place took.
        JsonElement counted = await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 8, ["column"] = 23 });
        Assert.Equal(["Aaa.cs:3:48", "Aaa.cs:5:67", "Words.cs:8:23"], Used(counted).Select(u => $"{u.Path}:{u.Line}:{u.Column}"));
        Assert.Equal(("Words.Count(string) (method)", 3, 2), (counted.GetProperty("symbol").GetString(), counted.GetProperty("count").GetInt32(), counted.GetProperty("files").GetArrayLength()));

        // A column off the name, on the line that declares it, still finds it.
        JsonElement fromStart = await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 8, ["column"] = 1 });
        Assert.Equal(counted.GetRawText(), fromStart.GetRawText());
        string declared = (await Call(client, McpTools.SymbolInfo, new() { ["path"] = "Words.cs", ["line"] = 10, ["column"] = 2 })).GetString()!;
        Assert.Contains("int Words.Count(string text, int from)", declared, StringComparison.Ordinal);

        CallToolResult nothing = await Result(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 2, ["column"] = 1 });
        Assert.True(nothing.IsError == true && Text(nothing).Contains("No symbol at Words.cs:2:1", StringComparison.Ordinal),
            $"usages where no symbol is answered: {Text(nothing)}");
    }

    /// <summary>usages says which files are generated code, as Roslyn tells it, and lists them after the person's: a source
    /// generator's, a .g.cs a tool wrote, and a file whose first comment says &lt;auto-generated&gt;; a file written by hand
    /// says nothing of it.</summary>
    [Fact]
    public async Task Usages_tell_an_agent_which_files_are_generated_code_and_list_them_after_the_persons()
    {
        await TakeFolderAsync();
        Write("Words.cs", Words);
        Write("Hand.cs", "static class Hand\n{\n    public static int Of(string text) => Words.Count(text);\n}\n");
        Write("Made.cs", "// <auto-generated/>\nstatic class Made\n{\n    public static int Of(string text) => Words.Count(text);\n}\n");
        Write("Tool.g.cs", "static class Tool\n{\n    public static int Of(string text) => Words.Count(text);\n}\n");
        await using McpClient client = await Start(["--mcp", Folder]);

        JsonElement counted = await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 8, ["column"] = 23 });
        Assert.Equal(["Hand.cs", "Words.cs", "Made.cs generated", "Tool.g.cs generated"], Files(counted));
        JsonElement word = await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 34 });
        string[] files = Files(word);
        Assert.True(files is ["Words.cs", var made] && made.EndsWith($"{Path.DirectorySeparatorChar}RegexGenerator.g.cs generated", StringComparison.Ordinal),
            $"usages of a GeneratedRegex method did not say its generator's file is generated code: {word.GetRawText()}");

        // Each file by its path, with "generated" after it where the answer says so.
        static string[] Files(JsonElement usages) => [.. usages.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("path").GetString() + (f.TryGetProperty("generated", out JsonElement made) && made.GetBoolean() ? " generated" : string.Empty))];
    }

    /// <summary>The code tools on a source generator's code copied, where usages led the agent, answer at once: its outline
    /// from its own text, and what needs the language server, which never holds a copy, refused in the window's words,
    /// naming the generator and the project.</summary>
    [Fact]
    public async Task An_agent_asking_about_a_generators_code_where_usages_led_it_is_answered_from_its_text_or_refused_at_once()
    {
        await TakeFolderAsync();
        Write("Words.cs", Words);
        await using McpClient client = await Start(["--mcp", Folder]);
        (string path, int line, int column, _) = Used(await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 34 }))[^1];
        Assert.EndsWith($"{Path.DirectorySeparatorChar}RegexGenerator.g.cs", path, StringComparison.Ordinal);

        const string Refused = $"RegexGenerator.g.cs is a source generator's code, made by RegexGenerator for Sum ({TestProjects.Framework}), copied to read: "
            + "the C# language server answers in the code it is made from";
        foreach ((string tool, Dictionary<string, object?> arguments) in new (string, Dictionary<string, object?>)[]
        {
            (McpTools.SymbolInfo, new() { ["path"] = path, ["line"] = line, ["column"] = column }),
            (McpTools.Diagnostics, new() { ["path"] = path }),
            (McpTools.Definition, new() { ["path"] = path, ["line"] = line, ["column"] = column }),
        })
        {
            System.Diagnostics.Stopwatch asked = System.Diagnostics.Stopwatch.StartNew();
            CallToolResult refused = await Result(client, tool, arguments);
            Assert.True(refused.IsError == true && Text(refused).Contains(Refused, StringComparison.Ordinal),
                $"{tool} on the generator's code copied answered after {asked.Elapsed.TotalSeconds:0.0} s: {Text(refused)}");
        }

        string outline = (await Call(client, McpTools.Outline, new() { ["path"] = path })).GetString()!;
        Assert.True(outline.StartsWith("Words (class, line ", StringComparison.Ordinal) && outline.Contains($"\n  Word() (method, line {line})\n", StringComparison.Ordinal),
            $"outline of the generator's code copied is not what its text declares, Word() on line {line}:\n{outline}");
    }

    /// <summary>hierarchy at a place in a source generator's code copied, where usages and definition led the agent, answers
    /// as in the code it is made from, the server asked of its own document: the regex generator's half of a partial method
    /// has that method's callers and what its body calls, and the JSON generator's half of a class what derives from it.</summary>
    [Fact]
    public async Task An_agent_follows_the_hierarchy_from_a_place_in_a_generators_code()
    {
        await TakeFolderAsync();
        Write("Words.cs", Words);
        Write("Shapes.cs", Shapes);
        Write("Deep.cs", "sealed class DeepContext : ShapeContext\n{\n}\n");
        await using McpClient client = await Start(["--mcp", Folder]);
        (string regex, int line, int column, _) = Used(await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 34 }))[^1];

        JsonElement callers = await Call(client, McpTools.Hierarchy, new() { ["path"] = regex, ["line"] = line, ["column"] = column });
        Assert.Equal(["Words.Count(string)", "Words.Count(string, int)"], callers.EnumerateArray().Select(c => c.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        JsonElement callee = Assert.Single((await Call(client, McpTools.Hierarchy, new() { ["path"] = regex, ["line"] = line, ["column"] = column, ["direction"] = "callees" })).EnumerateArray());
        Assert.True(callee.GetProperty("path").GetString() == regex && callee.GetProperty("name").GetString()!.EndsWith(".Instance", StringComparison.Ordinal),
            $"callees of the generator's half of Word answered no place in its code: {callee.GetRawText()}");

        string context = Assert.Single((await Call(client, McpTools.Definition, new() { ["path"] = "Shapes.cs", ["line"] = 10, ["column"] = 48 })).EnumerateArray()).GetProperty("path").GetString()!;
        int declared = Array.FindIndex(File.ReadAllLines(context), l => l.Contains("partial class ShapeContext", StringComparison.Ordinal)) + 1;
        JsonElement derived = await Call(client, McpTools.Hierarchy, new() { ["path"] = context, ["line"] = declared, ["column"] = 25, ["direction"] = "subtypes" });
        Assert.Equal(["DeepContext Deep.cs:1"], derived.EnumerateArray().Select(d => $"{d.GetProperty("name").GetString()} {d.GetProperty("path").GetString()}:{d.GetProperty("line").GetInt32()}"));
    }

    /// <summary>A partial member's two halves are one member to hierarchy: asked at the half that declares it or the half with
    /// its body, it answers the callers, bound to the one, and the callees, written in the other.</summary>
    [Fact]
    public async Task Either_half_of_a_partial_method_answers_its_callers_and_what_its_body_calls()
    {
        await TakeFolderAsync();
        Write("Halves.cs", "static partial class Halves\n{\n    public static partial int Twice(int value);\n\n    public static int Quad(int value) => Twice(Twice(value));\n}\n\n"
            + "static partial class Halves\n{\n    public static partial int Twice(int value) => Helper(value) * 2;\n\n    private static int Helper(int value) => value;\n}\n");
        await using McpClient client = await Start(["--mcp", Folder]);
        foreach (int line in new[] { 3, 10 })
        {
            Assert.Equal(["Halves.Quad(int)"], (await Call(client, McpTools.Hierarchy, new() { ["path"] = "Halves.cs", ["line"] = line, ["column"] = 31 }))
                .EnumerateArray().Select(c => c.GetProperty("name").GetString()));
            Assert.Equal(["Halves.Helper(int)"], (await Call(client, McpTools.Hierarchy, new() { ["path"] = "Halves.cs", ["line"] = line, ["column"] = 31, ["direction"] = "callees" }))
                .EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        }
    }

    [Fact]
    public async Task An_agent_names_a_symbol_in_place_of_its_place_finds_names_exactly_and_tells_overloads_apart_by_their_documentation_ids()
    {
        await TakeFolderAsync();
        Write("Words.cs", Words);
        Write("Aaa.cs", Tally);
        Write("Figures.cs", Figures);
        await using McpClient client = await Start(["--mcp", Folder]);

        // Go to Symbol's matches take in CountAll; an exact find, the two overloads alone, each by its id.
        Assert.Contains((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Count" })).EnumerateArray(), s => s.GetProperty("name").GetString() == "CountAll");
        Assert.Equal(["Count Words.cs:8 M:Words.Count(System.String)", "Count Words.cs:10 M:Words.Count(System.String,System.Int32)"],
            (await Call(client, McpTools.FindSymbol, new() { ["query"] = "Count", ["exact"] = true })).EnumerateArray()
                .Select(s => $"{s.GetProperty("name").GetString()} {s.GetProperty("path").GetString()}:{s.GetProperty("line").GetInt32()} {s.GetProperty("id").GetString()}"));

        string both = (await Call(client, McpTools.SymbolInfo, new() { ["symbol"] = "Words.Count" })).GetString()!;
        Assert.True(both.Contains("Words.Count(string) (method) at Words.cs:8:23, M:Words.Count(System.String)", StringComparison.Ordinal)
            && both.Contains("Words.Count(string, int) (method) at Words.cs:10:23, M:Words.Count(System.String,System.Int32)", StringComparison.Ordinal)
            && both.Contains("int Words.Count(string text, int from)", StringComparison.Ordinal), $"symbol_info of Words.Count answered:\n{both}");
        Assert.EndsWith("Documentation id: T:System.Text.RegularExpressions.Regex",
            (await Call(client, McpTools.SymbolInfo, new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 28 })).GetString(), StringComparison.Ordinal);

        Assert.Equal(["Aaa.cs:3:68", "Words.cs:10:23"], Used(await Call(client, McpTools.Usages, new() { ["symbol"] = "Words.Count(string, int)" }))
            .Select(u => $"{u.Path}:{u.Line}:{u.Column}"));
        CallToolResult ambiguous = await Result(client, McpTools.Usages, new() { ["symbol"] = "Count" });
        Assert.True(ambiguous.IsError == true && Text(ambiguous).Contains("Count names 2 symbols", StringComparison.Ordinal)
            && Text(ambiguous).Contains("M:Words.Count(System.String,System.Int32)", StringComparison.Ordinal), $"usages of a name two overloads share answered: {Text(ambiguous)}");
        JsonElement declared = Assert.Single((await Call(client, McpTools.Definition, new() { ["symbol"] = "M:Words.Count(System.String)" })).EnumerateArray());
        Assert.Equal(("Words.cs", 8), (declared.GetProperty("path").GetString(), declared.GetProperty("line").GetInt32()));
        Assert.Equal(["Words.Count(string)", "Words.Count(string, int)"],
            (await Call(client, McpTools.Hierarchy, new() { ["symbol"] = "Words.Word" })).EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        CallToolResult ofType = await Result(client, McpTools.Hierarchy, new() { ["symbol"] = "Words" });
        Assert.True(ofType.IsError == true && Text(ofType).Contains("Words is a type, which has no callers or callees: direction supertypes", StringComparison.Ordinal),
            $"hierarchy of a type, its direction left as callers, answered: {Text(ofType)}");
        Assert.Equal(["Figures.cs:3:28"], Placed(await Call(client, McpTools.Hierarchy, new() { ["symbol"] = "Square.Area", ["direction"] = "supertypes" })));
        Assert.Equal(["Figures.cs:8:28"], Placed(await Call(client, McpTools.Hierarchy, new() { ["symbol"] = "Shape.Area", ["direction"] = "subtypes" })));

        Assert.Equal("Renamed in 1 file: Aaa.cs", (await Call(client, McpTools.Rename, new() { ["symbol"] = "Tally.CountAll", ["newName"] = "All" })).GetString());
        Assert.Contains("public static int All(string[] texts)", File.ReadAllText(Path.Combine(Folder, "Aaa.cs")), StringComparison.Ordinal);
    }

    /// <summary>rename of a type answers once Git's index holds its file's move: git status, run the moment the answer comes,
    /// as an agent's shell runs it, shows the file renamed, never the old one deleted and the new one untracked.</summary>
    [Fact]
    public async Task An_agents_rename_of_a_type_answers_once_gits_index_holds_its_files_move()
    {
        string folder = await TakeFolderAsync();
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "bin/\nobj/\n");
        await TestGit.InitAsync(folder);
        await using McpClient client = await Start(["--mcp", folder]);
        string said = Text(await Call(client, McpTools.Rename, new() { ["symbol"] = "Helper", ["newName"] = "Doubler" }));
        string[] status = (await TestGit.RunAsync(folder, "status", "--porcelain", "--untracked-files=all")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(said.EndsWith("; renamed Helper.cs to Doubler.cs", StringComparison.Ordinal), $"The agent's rename of Helper to Doubler answered: {said}");
        Assert.True(status.Length == 2 && status[0].StartsWith('R') && status[0].EndsWith(" Helper.cs -> Doubler.cs", StringComparison.Ordinal) && status[1] == " M Program.cs",
            $"git status, run as rename answered, did not show Helper.cs renamed to Doubler.cs and Program.cs changed:\n{string.Join('\n', status)}");
    }

    private const string Figures = "abstract class Shape\n{\n    public abstract double Area();\n}\n\n"
        + "sealed class Square(double side) : Shape\n{\n    public override double Area() => side * side;\n}\n";

    /// <summary>Each hierarchy entry's place as "path:line:column".</summary>
    private static IEnumerable<string> Placed(JsonElement entries) =>
        entries.EnumerateArray().Select(e => $"{e.GetProperty("path").GetString()}:{e.GetProperty("line").GetInt32()}:{e.GetProperty("column").GetInt32()}");

    private const string Shelf = "sealed class Shelf\n{\n    private readonly string[] books = [\"a\", \"b\"];\n\n"
        + "    /// <summary>The book_at a place on the shelf, from a List&lt;string&gt; of them kept under <c>C:\\temp</c> in C:\\temp\\books.</summary>\n"
        + "    public string this[int at] => books[at];\n\n"
        + "    public int Size()\n    {\n        int unused = 1;\n        return books.Length;\n    }\n}\n";

    [Fact]
    public async Task An_agent_finds_a_name_itself_first_an_indexer_by_name_reads_a_card_as_plain_text_and_a_fixs_suppressions_counted()
    {
        await TakeFolderAsync();
        Write("Words.cs", Words);
        Write("Aaa.cs", Tally);
        Write("Shelf.cs", Shelf);
        await using McpClient client = await Start(["--mcp", Folder]);

        // The name itself before the names that start with it, each in the type that holds it, not a project's words.
        JsonElement[] counts = [.. (await Call(client, McpTools.FindSymbol, new() { ["query"] = "Count" })).EnumerateArray()];
        Assert.Equal(["Count Words", "Count Words", "CountAll Tally"],
            counts.Take(3).Select(s => $"{s.GetProperty("name").GetString()} {s.GetProperty("container").GetString()}"));

        // An indexer by this[] and by Item, its metadata's name, wherever a symbol is named.
        JsonElement indexer = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "this[]" })).EnumerateArray());
        Assert.Equal(("this[]", "Shelf", "Shelf.cs", 6, "P:Shelf.Item(System.Int32)"), (indexer.GetProperty("name").GetString(), indexer.GetProperty("container").GetString(),
            indexer.GetProperty("path").GetString(), indexer.GetProperty("line").GetInt32(), indexer.GetProperty("id").GetString()));
        Assert.Contains((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Item" })).EnumerateArray(), s => s.GetProperty("id").GetString() == "P:Shelf.Item(System.Int32)");
        JsonElement used = await Call(client, McpTools.Usages, new() { ["symbol"] = "Shelf.Item" });
        Assert.Equal(("Shelf.this[int] (property)", "Shelf.cs:6"), (used.GetProperty("symbol").GetString(), string.Join(", ", Used(used).Select(u => $"{u.Path}:{u.Line}"))));

        // The hover card's words, with none of Markdown's escapes or fences, a path's backslashes kept as code and in words.
        string card = (await Call(client, McpTools.SymbolInfo, new() { ["symbol"] = "Shelf.this[int]" })).GetString()!;
        Assert.True(card.Contains("string Shelf.this[int at] { get; }", StringComparison.Ordinal)
            && card.Contains(@"The book_at a place on the shelf, from a List<string> of them kept under `C:\temp` in C:\temp\books.", StringComparison.Ordinal)
            && !card.Contains("```", StringComparison.Ordinal), $"symbol_info of the indexer said:\n{card}");

        // A warning's Suppress and Configure choices are counted, and listed when the agent names that action.
        Dictionary<string, object?> unused = new() { ["path"] = "Shelf.cs", ["line"] = 10, ["column"] = 13 };
        string suppress = Assert.Single((await Call(client, McpTools.CodeActions, unused)).EnumerateArray().Select(a => a.GetString()!), a => a.StartsWith("Suppress", StringComparison.Ordinal));
        Assert.EndsWith(" choices: title lists them", suppress, StringComparison.Ordinal);
        string title = suppress[..suppress.IndexOf(" [", StringComparison.Ordinal)];
        string listed = Assert.Single((await Call(client, McpTools.CodeActions, new(unused) { ["title"] = title })).EnumerateArray()).GetString()!;
        Assert.Contains("Suppress CS0219 → in Source", listed, StringComparison.Ordinal);
    }

    /// <summary>A call holds its files open while it runs, then keeps the last few the agent asked about: after reads of
    /// many files the IDE lists open only the file the agent opened in the editor and the one its edit could not save (a
    /// read-only file), and the language server holds those and at most <see cref="PassengerTools.FilesKept"/> more, the
    /// most recently asked, so a long session holds no more than it is working on.</summary>
    [Fact]
    public async Task An_agents_calls_on_many_files_leave_open_only_the_file_it_opened_the_one_its_edit_left_unsaved_and_the_last_few_asked()
    {
        await TakeFolderAsync();
        string[] parts = [.. Enumerable.Range(1, 12).Select(i => $"Part{i:00}.cs")];
        foreach (string part in parts)
        {
            Write(part, $"static class {Path.GetFileNameWithoutExtension(part)}\n{{\n    public static int Of(int value) => Helper.Twice(value) + 1;\n}}\n");
        }
        Write("Locked.cs", "static class Locked\n{\n    public static int Of(int value) => value;\n}\n");
        File.SetAttributes(Path.Combine(Folder, "Locked.cs"), FileAttributes.ReadOnly);
        await using McpClient client = await Start(["--mcp", Folder]);
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["traceProtocols"] = true } });

        await Call(client, McpTools.Open, new() { ["path"] = "Program.cs" });
        foreach (string file in parts.Append("Program.cs").Append("Helper.cs"))
        {
            await Call(client, McpTools.Outline, new() { ["path"] = file });
            await Call(client, McpTools.Diagnostics, new() { ["path"] = file });
            await Call(client, McpTools.EditorText, new() { ["path"] = file, ["to"] = 2 });
            if (file.StartsWith("Part", StringComparison.Ordinal))
            {
                await Call(client, McpTools.SymbolInfo, new() { ["path"] = file, ["line"] = 3, ["column"] = 23 });
                await Call(client, McpTools.Usages, new() { ["path"] = file, ["line"] = 3, ["column"] = 44 });
            }
        }
        string edited = Text(await Call(client, McpTools.Edit, new() { ["path"] = "Locked.cs", ["oldText"] = "=> value;", ["newText"] = "=> value + 1;" }));
        Assert.Contains("not saved: Locked.cs is read-only", edited, StringComparison.Ordinal);

        string[] open = [.. (await Call(client, McpTools.Workspace)).GetProperty("openFiles").EnumerateArray().Select(f => f.GetString()!)];
        Assert.True(open.SequenceEqual(["Program.cs", "Locked.cs (unsaved)"]),
            $"The IDE lists open {string.Join(", ", open)}, where the agent left open only Program.cs and the unsaved Locked.cs");
        string trace = Path.Combine(OwnState, "logs", "lsp.log");
        string[] server = [];
        Assert.True(await Waiting.UntilAsync(() =>
            {
                server = ServerOpen(trace);
                return server.Contains("Program.cs") && server.Contains("Locked.cs") && server.Contains("Helper.cs") && server.Length <= 2 + PassengerTools.FilesKept
                    && !server.Contains(parts[0]);
            }, TimeSpan.FromSeconds(10).Scaled(), Cancel),
            $"The language server was last told open {string.Join(", ", server)}, where the agent left open Program.cs, Locked.cs and at most {PassengerTools.FilesKept} of the files it asked about last, Helper.cs among them");
    }

    /// <summary>A file an agent asks diagnostics of stays open in the language server after the call, so a second ask of it,
    /// unchanged, answers what the first did from the server's last pull, sending none (the protocol trace).</summary>
    [Fact]
    public async Task A_second_diagnostics_of_an_unchanged_file_answers_from_the_servers_last_pull_sending_none()
    {
        await TakeFolderAsync();
        await using McpClient client = await Start(["--mcp", Folder]);
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["traceProtocols"] = true } });
        string first = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" }));
        Assert.Contains("unused", first, StringComparison.Ordinal);
        string trace = Path.Combine(OwnState, "logs", "lsp.log");
        // The window's own pulls of the file just opened, and one a refresh of the server's as it read the file brings, come first.
        await Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" });
        int pulls = -1;
        for (int round = 0; round < 20 && Pulls(trace, "Helper.cs") != pulls; round++)
        {
            pulls = Pulls(trace, "Helper.cs");
            Waiting.For(TimeSpan.FromSeconds(1), "the trace of the first calls' pulls to settle");
        }
        string second = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" }));
        Assert.Equal(first, second);
        await TracedAsync(client, trace);
        Assert.True(Pulls(trace, "Helper.cs") == pulls,
            $"The second ask of Helper.cs, unchanged, sent the language server {Pulls(trace, "Helper.cs") - pulls} pulls of its diagnostics, where its last pull answered");
        Assert.Contains("Helper.cs", ServerOpen(trace));
    }

    /// <summary>A completion list changes nothing the language server answers, so an agent's diagnostics of a file asked again
    /// once the list for a place in it has come answer from the pull before the list, sending none (the protocol trace).</summary>
    [Fact]
    public async Task Diagnostics_asked_again_after_a_completion_answer_from_the_pull_before_it_sending_none()
    {
        await TakeFolderAsync();
        await using McpClient client = await Start(["--mcp", Folder]);
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["traceProtocols"] = true } });
        await Call(client, McpTools.Open, new() { ["path"] = "Helper.cs", ["line"] = 6, ["column"] = 16 });
        string first = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" }));
        Assert.Contains("unused", first, StringComparison.Ordinal);
        string trace = Path.Combine(OwnState, "logs", "lsp.log");
        // The window's own pulls of the file just opened, and one a refresh of the server's as it read the file brings, come first.
        await Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" });
        int pulls = -1;
        for (int round = 0; round < 20 && Pulls(trace, "Helper.cs") != pulls; round++)
        {
            pulls = Pulls(trace, "Helper.cs");
            Waiting.For(TimeSpan.FromSeconds(1), "the trace of the first calls' pulls to settle");
        }

        await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.Complete });
        Assert.True(await Waiting.UntilAsync(() => Answered(trace, Passenger.Roslyn.Extension.ExtensionMethods.Completion), TimeSpan.FromSeconds(30).Scaled(), Cancel),
            "Basic Completion at return value in Helper.cs had no completion list answered by the language server");
        string second = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" }));
        Assert.Equal(first, second);
        await TracedAsync(client, trace);
        Assert.True(Pulls(trace, "Helper.cs") == pulls,
            $"Diagnostics of Helper.cs asked again after a completion list, nothing changed, sent the language server {Pulls(trace, "Helper.cs") - pulls} pulls of them, where its last pull answered");
    }

    /// <summary>A type a code action moved to a file of its own is known at once to what the agent asks next of a file using
    /// it, each tool asked first after a move of its own: the language server takes the new file into the project a moment
    /// later, and answered before then, diagnostics said the type could not be found and the other tools answered without
    /// it, of another symbol or none.</summary>
    [Fact]
    public async Task What_an_agent_asks_at_once_after_a_code_action_moved_a_type_to_a_file_of_its_own_finds_the_type_there()
    {
        await TakeFolderAsync();
        string[] kinds = ["Circle", "Oval", "Star", "Ring", "Kite"];
        Write("Shapes.cs", "namespace Shop;\n\npublic class Square\n{\n    public int Side;\n}\n"
            + string.Concat(kinds.Select(k => $"\npublic class {k}\n{{\n    public double Radius;\n}}\n")));
        string garden = "namespace Shop;\n\npublic static class Garden\n{\n" + string.Concat(kinds.Select(k => $"    public static double Of{k}({k} shape) => shape.Radius;\n")) + "}\n";
        Write("Garden.cs", garden);
        await using McpClient client = await Start(["--mcp", Folder]);
        Assert.Empty(Errors(await Call(client, McpTools.Diagnostics, new() { ["path"] = "Garden.cs" })));

        string[] errors = Errors(await MovedThenAsync("Circle", used => Call(client, McpTools.Diagnostics, new() { ["path"] = "Garden.cs" })));
        Assert.True(errors.Length == 0, $"diagnostics of Garden.cs at once after Circle's move answered errors the language server has yet to clear, which an agent would fix: {string.Join("; ", errors)}");
        JsonElement declared = Assert.Single((await MovedThenAsync("Oval", used => Call(client, McpTools.Definition, used))).EnumerateArray());
        Assert.Equal(("Oval.cs", 3), (declared.GetProperty("path").GetString(), declared.GetProperty("line").GetInt32()));
        string card = (await MovedThenAsync("Star", used => Call(client, McpTools.SymbolInfo, used))).GetString()!;
        Assert.True(card.Contains("class Shop.Star", StringComparison.Ordinal), $"symbol_info of Star at once after its move answered of another symbol: {card}");
        JsonElement usages = await MovedThenAsync("Ring", used => Call(client, McpTools.Usages, used));
        Assert.Equal(("Ring (class)", "Garden.cs, Ring.cs"), (usages.GetProperty("symbol").GetString(), string.Join(", ", Used(usages).Select(u => u.Path).Distinct().Order(StringComparer.Ordinal))));
        JsonElement[] found = [.. (await MovedThenAsync("Kite", _ => Call(client, McpTools.FindSymbol, new() { ["query"] = "Kite" }))).EnumerateArray()];
        Assert.True(found.Any(f => f.GetProperty("kind").GetString() == "class" && f.GetProperty("path").GetString() == "Kite.cs"),
            $"find_symbol Kite at once after its move found no class Kite in Kite.cs: {string.Join(", ", found.Select(f => f.GetRawText()))}");

        // The light bulb's Move type to X.cs on the type's name, then at once the tool, at the type's use in Garden.cs.
        async Task<JsonElement> MovedThenAsync(string kind, Func<Dictionary<string, object?>, Task<JsonElement>> ask)
        {
            (int line, int column) = At(File.ReadAllText(Path.Combine(Folder, "Shapes.cs")), $"class {kind}", "class ".Length);
            Assert.Equal($"Applied 'Move type to {kind}.cs' to 2 files", (await Call(client, McpTools.ApplyCodeAction,
                new() { ["path"] = "Shapes.cs", ["line"] = line, ["column"] = column, ["title"] = $"Move type to {kind}.cs" })).GetString());
            (int usedLine, int usedColumn) = At(garden, $"({kind} shape", 1);
            return await ask(new() { ["path"] = "Garden.cs", ["line"] = usedLine, ["column"] = usedColumn });
        }

        static string[] Errors(JsonElement found) =>
            [.. found.EnumerateArray().Where(d => d.GetProperty("severity").GetString() == "error").Select(d => $"{d.GetProperty("code").GetString()} {d.GetProperty("message").GetString()}")];

        static (int Line, int Column) At(string text, string what, int into)
        {
            int offset = text.IndexOf(what, StringComparison.Ordinal) + into;
            return (text[..offset].Count(c => c == '\n') + 1, offset - text.LastIndexOf('\n', offset - 1));
        }
    }

    /// <summary>How many pulls of a file's diagnostics lsp.log's trace says the language server was sent.</summary>
    private static int Pulls(string trace, string file)
    {
        using FileStream read = new(trace, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(read);
        return reader.ReadToEnd().Split('\n').Count(l => l.Contains("] --> ", StringComparison.Ordinal)
            && l.Contains($"\"method\":\"{Passenger.Lsp.LspMethods.DocumentDiagnostic}\"", StringComparison.Ordinal) && l.Contains("/" + file + "\"", StringComparison.Ordinal));
    }

    /// <summary>Has every message sent the language server so far reach lsp.log, whose lines are written a moment after they
    /// cross: a hover card asked of Helper.cs now, its request found there after them.</summary>
    private async Task TracedAsync(McpClient client, string trace)
    {
        int hovers = Sent(trace, Passenger.Lsp.LspMethods.Hover);
        await Call(client, McpTools.SymbolInfo, new() { ["path"] = "Helper.cs", ["line"] = 3, ["column"] = 23 });
        Assert.True(await Waiting.UntilAsync(() => Sent(trace, Passenger.Lsp.LspMethods.Hover) > hovers, TimeSpan.FromSeconds(10).Scaled(), Cancel),
            "A hover card asked of Helper.cs never reached lsp.log's trace of what the language server was sent");
    }

    /// <summary>How many requests of a method lsp.log's trace says the server was sent.</summary>
    private static int Sent(string trace, string method)
    {
        using FileStream read = new(trace, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(read);
        return reader.ReadToEnd().Split('\n').Count(l => l.Contains("] --> ", StringComparison.Ordinal) && l.Contains($"\"method\":\"{method}\"", StringComparison.Ordinal));
    }

    /// <summary>Whether lsp.log's trace has the server's answer to a request of the method sent it.</summary>
    private static bool Answered(string trace, string method)
    {
        string[] lines;
        using (FileStream read = new(trace, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (StreamReader reader = new(read))
        {
            lines = reader.ReadToEnd().Split('\n');
        }
        IEnumerable<string> ids = lines.Where(l => l.Contains("] --> ", StringComparison.Ordinal) && l.Contains($"\"method\":\"{method}\"", StringComparison.Ordinal))
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, "\"id\":(\\d+)[,}]").Groups[1].Value).Where(id => id.Length > 0);
        return ids.Any(id => lines.Any(l => l.Contains("] <-- ", StringComparison.Ordinal) && l.Contains($"\"id\":{id},", StringComparison.Ordinal)
            && l.Contains("\"result\"", StringComparison.Ordinal)));
    }

    /// <summary>The names of the files the language server was told open and not since closed, by lsp.log's trace of what it
    /// was sent.</summary>
    private static string[] ServerOpen(string trace)
    {
        Dictionary<string, int> open = new(StringComparer.Ordinal);
        string text;
        using (FileStream read = new(trace, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (StreamReader reader = new(read))
        {
            text = reader.ReadToEnd();
        }
        foreach (string line in text.Split('\n'))
        {
            int change = !line.Contains("] --> ", StringComparison.Ordinal) ? 0
                : line.Contains("\"method\":\"textDocument/didOpen\"", StringComparison.Ordinal) ? 1
                : line.Contains("\"method\":\"textDocument/didClose\"", StringComparison.Ordinal) ? -1 : 0;
            if (change != 0 && System.Text.RegularExpressions.Regex.Match(line, "\"uri\":\"([^\"]+)\"") is { Success: true } uri)
            {
                open[uri.Groups[1].Value] = open.GetValueOrDefault(uri.Groups[1].Value) + change;
            }
        }
        return [.. open.Where(o => o.Value > 0).Select(o => Path.GetFileName(new Uri(o.Key).LocalPath)).Order(StringComparer.Ordinal)];
    }
}
