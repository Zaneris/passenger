using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What an agent's own Passenger (<c>--mcp</c>) knows of its folder: workspace names what each project builds, and
/// calls an Android app, which .NET for Android evaluates as a Library, the Exe it runs as, and a library stays a Library; a
/// file made while the folder's watcher heard nothing is found at the agent's next call; and a project's frameworks the
/// agent just edited, and a project it just added to the solution, are what the next call answers from.</summary>
[Collection(Beside.Name)]
public sealed class McpWorkspaceTests : McpRig
{
    /// <summary>An agent's workspace as one flow, each step one test's check in an instance of its own: an Android app
    /// called an exe as it runs where its evaluation says library, what each project builds named as the Solution view
    /// draws it, a file made unheard found at the next call after a quiet spell, and build, run and workspace right
    /// after an edit of frameworks, a project added to the solution or an import made beside a project answering from
    /// them as written.</summary>
    [Fact]
    public async Task An_agents_workspace_names_what_each_project_builds_as_written_and_finds_what_the_watcher_did_not_hear()
    {
        await Flow.StepApartAsync("workspace calls an android app an exe as it runs where its evaluation says library", () => new McpWorkspaceTests(), t => t.Workspace_calls_an_android_app_an_exe_as_it_runs_where_its_evaluation_says_library());
        await Flow.StepApartAsync("workspace names what each project builds as the solution view draws it", () => new McpWorkspaceTests(), t => t.Workspace_names_what_each_project_builds_as_the_solution_view_draws_it());
        await Flow.StepApartAsync("a file made while the watcher heard nothing is found at the agents next call after a quiet spell", () => new McpWorkspaceTests(), t => t.A_file_made_while_the_watcher_heard_nothing_is_found_at_the_agents_next_call_after_a_quiet_spell());
        await Flow.StepApartAsync("workspace build and run right after an edit of a projects frameworks each answer from the frameworks as edited", () => new McpWorkspaceTests(), t => t.Workspace_build_and_run_right_after_an_edit_of_a_projects_frameworks_each_answer_from_the_frameworks_as_edited());
        await Flow.StepApartAsync("workspace right after an edit adding a project to the solution lists it", () => new McpWorkspaceTests(), t => t.Workspace_right_after_an_edit_adding_a_project_to_the_solution_lists_it());
        await Flow.StepApartAsync("workspace right after an import was made beside a project lists what the import gives it", () => new McpWorkspaceTests(), t => t.Workspace_right_after_an_import_was_made_beside_a_project_lists_what_the_import_gives_it());
    }

    private async Task Workspace_calls_an_android_app_an_exe_as_it_runs_where_its_evaluation_says_library()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-android-app-"));
        DeleteAtEnd(folder);
        // An app's evaluation as .NET for Android leaves it, a .NET MAUI app's for its Android framework among them.
        TestFolders.Write(folder, "Phone/Phone.csproj", TestProjects.Library("<AndroidApplication>true</AndroidApplication>"));
        TestFolders.Write(folder, "Phone/MainActivity.cs", "class MainActivity\n{\n}\n");
        TestFolders.Write(folder, "Parts/Parts.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Parts/Part.cs", "class Part\n{\n}\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        const string Expected = "Parts: Library, Phone: Exe; runs Phone";
        string said = string.Empty;
        await Waiting.UntilAsync(async () => (said = Said(await Call(client, McpTools.Workspace))) == Expected, TimeSpan.FromSeconds(60), Cancel, TimeSpan.FromMilliseconds(200));
        Assert.True(said == Expected, $"workspace did not call the Android app an Exe, the run configuration it lists, and the library a Library: {said}");
    }

    /// <summary>workspace names what each project builds as the Solution view draws it, from its evaluation: a web API, a
    /// project of tests, a class library and a shared project.</summary>
    private async Task Workspace_names_what_each_project_builds_as_the_solution_view_draws_it()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-kinds-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "Shop.Api/Shop.Api.csproj", TestProjects.Web());
        TestFolders.Write(folder, "Shop.Tests/Shop.Tests.csproj", TestProjects.Library("<IsTestProject>true</IsTestProject>"));
        TestFolders.Write(folder, "Shop.Core/Shop.Core.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Shop.Shared/Shop.Shared.shproj", "<Project>\n  <Import Project=\"Shop.Shared.projitems\" Label=\"Shared\" />\n</Project>\n");
        TestFolders.Write(folder, "Shop.Shared/Shop.Shared.projitems", "<Project>\n</Project>\n");
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"Shop.Api/Shop.Api.csproj\" />\n  <Project Path=\"Shop.Tests/Shop.Tests.csproj\" />\n"
            + "  <Project Path=\"Shop.Core/Shop.Core.csproj\" />\n  <Project Path=\"Shop.Shared/Shop.Shared.shproj\" />\n</Solution>\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        const string Expected = "Shop.Api: web API, Shop.Core: class library, Shop.Shared: shared project, Shop.Tests: test project";
        string said = string.Empty;
        await Waiting.UntilAsync(async () => (said = Kinds(await Call(client, McpTools.Workspace))) == Expected, TimeSpan.FromSeconds(60), Cancel, TimeSpan.FromMilliseconds(200));
        Assert.True(said == Expected, $"workspace did not name what each project builds: {said}");
    }

    /// <summary>A file made while the folder's watcher heard nothing, as the system drops a change without a word, is found at
    /// the agent's next call once it has been away a while, as a person's window finds it coming in front, which an agent's
    /// own IDE never does: find_in_files, reading the folder's files as Go to File lists them, finds what it holds.</summary>
    private async Task A_file_made_while_the_watcher_heard_nothing_is_found_at_the_agents_next_call_after_a_quiet_spell()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-unheard-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "Shop.cs", "class Shop\n{\n}\n");
        await using McpClient client = await Start(["--mcp", folder], environment: new() { [WatchedTree.UnheardVariable] = "Unheard.cs" }, languageServer: false);
        Assert.Equal(["Shop.cs:1"], Found(await Call(client, McpTools.FindInFiles, new() { ["text"] = "class" })));

        TestFolders.Write(folder, "Unheard.cs", "class Unheard\n{\n}\n");
        await Waiting.ForAsync(MainWindow.AgentAway + TimeSpan.FromMilliseconds(250), "the agent away a while, as when it works through its own shell", Cancel);
        string[] found = Found(await Call(client, McpTools.FindInFiles, new() { ["text"] = "class" }));
        Assert.True(found.SequenceEqual(["Shop.cs:1", "Unheard.cs:1"]),
            $"find_in_files at the agent's next call found [{string.Join(", ", found)}], missing the file made while the folder's watcher heard nothing");
    }

    /// <summary>An agent's edit of a project's frameworks, each followed at once by another call, before the folder's watcher
    /// may have heard of it: workspace lists them as edited, build's publish takes the one now first and run builds and runs
    /// that one, where each answered from the project as it stood before the edit.</summary>
    private async Task Workspace_build_and_run_right_after_an_edit_of_a_projects_frameworks_each_answer_from_the_frameworks_as_edited()
    {
        const string Newer = "net11.0";
        string older = TestProjects.Framework;
        string folder = PathNames.Canonical(TestFolders.Create("mcp-frameworks-"));
        DeleteAtEnd(folder);
        await TestFolders.PrepareAsync(async () =>
        {
            File.WriteAllText(Path.Combine(folder, "App.csproj"), TestProjects.Console().Replace($"<TargetFramework>{older}</TargetFramework>",
                $"<TargetFrameworks>{older};{Newer}</TargetFrameworks>", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(folder, "Program.cs"),
                $"#if NET11_0_OR_GREATER\nSystem.Console.WriteLine(\"built for {Newer}\");\n#else\nSystem.Console.WriteLine(\"built for {older}\");\n#endif\n");
            await Restore.RunAsync(folder);
        }, folder);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        await ListedAsync(client, $"{older};{Newer}", "as the folder opened");

        List<string> stale = [];
        await EditFrameworks(client, $"{older};{Newer}", $"{Newer};{older}");
        string listed = Frameworks(await Call(client, McpTools.Workspace));
        if (listed != $"{Newer};{older}")
        {
            stale.Add($"workspace right after the edit listed App's frameworks as {listed}, as they stood before it");
        }
        // Each edit landed before the next, so a call answering from the project as it stood before an edit answers otherwise.
        await ListedAsync(client, $"{Newer};{older}", "once the folder's watcher heard of the edit");

        await EditFrameworks(client, $"{Newer};{older}", $"{older};{Newer}");
        JsonElement published = await Call(client, McpTools.Build, new() { ["publish"] = "App" });
        string landed = published.TryGetProperty("folder", out JsonElement into) ? into.GetString() ?? string.Empty : string.Empty;
        if (!published.GetProperty("succeeded").GetBoolean() || Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(landed))) != older)
        {
            stale.Add($"build's publish right after the edit put {older} first did not publish {older}, as though {Newer} were still first: {published}");
        }
        await ListedAsync(client, $"{older};{Newer}", "once the folder's watcher heard of the edit");

        await EditFrameworks(client, $"{older};{Newer}", $"{Newer};{older}");
        JsonElement ran = await Call(client, McpTools.Run, new() { ["waitSeconds"] = 120 });
        if (ran.GetProperty("output").GetString()?.Contains($"built for {Newer}", StringComparison.Ordinal) != true)
        {
            stale.Add($"run right after the edit put {Newer} first did not run {Newer}'s build, as though {older} were still first: {ran}");
        }
        Assert.True(stale.Count == 0, string.Join('\n', stale));
    }

    /// <summary>An agent's edit adding a project to the solution, which the folder's watcher never hears, is what its next
    /// call answers from: workspace lists the project added, where it listed the solution's projects as they stood before
    /// until the watcher heard of the write.</summary>
    private async Task Workspace_right_after_an_edit_adding_a_project_to_the_solution_lists_it()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-solution-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "App/App.csproj", TestProjects.Console());
        TestFolders.Write(folder, "App/Program.cs", "System.Console.WriteLine(\"app\");\n");
        TestFolders.Write(folder, "Lib/Lib.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Lib/Greeting.cs", "namespace Lib;\n\npublic static class Greeting\n{\n}\n");
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        await using McpClient client = await Start(["--mcp", folder], environment: new() { [WatchedTree.UnheardVariable] = "Shop.slnx" }, languageServer: false);
        Assert.Equal(["App"], Names(await Call(client, McpTools.Workspace)));

        string said = Text(await Call(client, McpTools.Edit, new()
        {
            ["path"] = "Shop.slnx",
            ["oldText"] = "  <Project Path=\"App/App.csproj\" />",
            ["newText"] = "  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />",
        }));
        Assert.True(said.StartsWith("Changed Shop.slnx", StringComparison.Ordinal) && said.Contains(", saved:", StringComparison.Ordinal),
            $"The edit adding Lib to the solution answered: {said}");
        string[] listed = Names(await Call(client, McpTools.Workspace));
        Assert.True(listed.SequenceEqual(["App", "Lib"]),
            $"workspace right after the edit adding Lib to the solution listed [{string.Join(", ", listed)}], as the solution stood before it");
    }

    /// <summary>An import made beside a project by the agent's own shell, which the folder's watcher never hears, is what its
    /// next call answers from: workspace lists the framework the import gives the project, where it listed the one before
    /// until the watcher heard of the import.</summary>
    private async Task Workspace_right_after_an_import_was_made_beside_a_project_lists_what_the_import_gives_it()
    {
        const string Newer = "net11.0";
        string folder = PathNames.Canonical(TestFolders.Create("mcp-import-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "App.csproj", TestProjects.Console().With("  <Import Project=\"Local.props\" Condition=\"Exists('Local.props')\" />\n"));
        TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(\"app\");\n");
        await using McpClient client = await Start(["--mcp", folder], environment: new() { [WatchedTree.UnheardVariable] = "Local.props" }, languageServer: false);
        await ListedAsync(client, TestProjects.Framework, "as the folder opened");

        TestFolders.Write(folder, "Local.props", $"<Project>\n  <PropertyGroup>\n    <TargetFramework>{Newer}</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        string listed = Frameworks(await Call(client, McpTools.Workspace));
        Assert.True(listed == Newer, $"workspace right after Local.props was made, giving App {Newer}, listed App's frameworks as {listed}, as they stood before it");
    }

    /// <summary>The names of the projects workspace lists, in order.</summary>
    private static string[] Names(JsonElement workspace) =>
        [.. workspace.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).Order(StringComparer.Ordinal)];

    /// <summary>The agent's edit of App.csproj's frameworks, saved.</summary>
    private async Task EditFrameworks(McpClient client, string from, string to)
    {
        string said = Text(await Call(client, McpTools.Edit, new() { ["path"] = "App.csproj", ["oldText"] = from, ["newText"] = to }));
        Assert.True(said.StartsWith("Changed App.csproj", StringComparison.Ordinal) && said.Contains(", saved:", StringComparison.Ordinal),
            $"The edit of App.csproj's frameworks to {to} answered: {said}");
    }

    /// <summary>Waits until workspace lists App's frameworks as given, however late the folder's watcher hears of them.</summary>
    private async Task ListedAsync(McpClient client, string frameworks, string when)
    {
        string listed = string.Empty;
        await Waiting.UntilAsync(async () => (listed = Frameworks(await Call(client, McpTools.Workspace))) == frameworks, TimeSpan.FromSeconds(60), Cancel,
            TimeSpan.FromMilliseconds(200));
        Assert.True(listed == frameworks, $"workspace never listed App's frameworks as {frameworks} {when}, listing {listed}");
    }

    /// <summary>The frameworks workspace lists for the folder's one project.</summary>
    private static string Frameworks(JsonElement workspace) =>
        string.Join(';', workspace.GetProperty("projects").EnumerateArray().Single().GetProperty("targetFrameworks").EnumerateArray().Select(f => f.GetString()));

    /// <summary>Where find_in_files found its matches, by file and line.</summary>
    private static string[] Found(JsonElement found) =>
        [.. found.GetProperty("matches").EnumerateArray().Select(m => $"{m.GetProperty("path").GetString()}:{m.GetProperty("line").GetInt32()}").Order(StringComparer.Ordinal)];

    /// <summary>Each project's kind by name.</summary>
    private static string Kinds(JsonElement workspace) =>
        string.Join(", ", workspace.GetProperty("projects").EnumerateArray()
            .Select(p => $"{p.GetProperty("name").GetString()}: {(p.TryGetProperty("kind", out JsonElement kind) ? kind.GetString() : "none")}").Order(StringComparer.Ordinal));

    /// <summary>Each project's output type by name, then the run configurations.</summary>
    private static string Said(JsonElement workspace) =>
        string.Join(", ", workspace.GetProperty("projects").EnumerateArray()
            .Select(p => $"{p.GetProperty("name").GetString()}: {(p.TryGetProperty("outputType", out JsonElement type) ? type.GetString() : "none")}").Order(StringComparer.Ordinal))
        + "; runs " + string.Join(", ", workspace.GetProperty("runConfigurations").EnumerateArray().Select(c => c.GetString()));
}
