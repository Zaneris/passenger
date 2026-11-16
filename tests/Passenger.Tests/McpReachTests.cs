using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What of the disk an agent reaches: the open folder's files and, where it is trusted, those of the projects its
/// solution names beside it, and outside them only what its places lead to, to read: a file elsewhere is refused by its
/// path, by <c>..</c> and through a link in the folder, left as it was, a breakpoint there told nothing of it, though a folder
/// elsewhere still opens as the workspace; a package's file and a source generator's code copied are read but never
/// changed; and a setting naming a program Passenger runs is the person's alone.</summary>
public sealed class McpReachTests : McpRig
{
    private const string Key = "-----BEGIN STAND-IN KEY-----\nnot a key, a stand-in for one\n";

    /// <summary>A folder of the agent's work and, beside it, a folder of the person's holding a stand-in key: both canonical.</summary>
    private (string Folder, string Elsewhere, string Secret) WorkAndHome()
    {
        string root = PathNames.Canonical(TestFolders.Create("mcp-reach-"));
        DeleteAtEnd(root);
        string folder = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        string elsewhere = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "notes on the STAND-IN\n");
        return (folder, elsewhere, TestFolders.Write(elsewhere, "id_stand_in", Key));
    }

    /// <summary>A file outside the open folder, named by its full path or by <c>..</c> from the folder, is refused to
    /// editor_text, edit and open in the one sentence saying what an agent reaches, and to find_in_files' directory and
    /// diagnostics; a share's path is never asked of the disk; the file is left as it was. Opening the folder it is in, the
    /// workspace the agent chooses, makes it the agent's to read.</summary>
    [Fact]
    public async Task An_agent_is_refused_a_file_outside_the_open_folder_by_its_path_or_by_dots_and_the_file_is_left_as_it_was()
    {
        (string folder, string elsewhere, string secret) = WorkAndHome();
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        string refusal = Refusals.OutOfReach(secret, folder, changes: false);
        string dots = Path.Combine("..", "home", "id_stand_in");
        List<string> answered = [];
        foreach ((string tool, Dictionary<string, object?> arguments) in new (string, Dictionary<string, object?>)[]
        {
            (McpTools.EditorText, new() { ["path"] = secret }),
            (McpTools.EditorText, new() { ["path"] = dots }),
            (McpTools.Edit, new() { ["path"] = secret, ["oldText"] = "not a key", ["newText"] = "written" }),
            (McpTools.Edit, new() { ["path"] = dots, ["oldText"] = "not a key", ["newText"] = "written" }),
            (McpTools.Open, new() { ["path"] = secret }),
            (McpTools.Open, new() { ["path"] = dots, ["line"] = 1 }),
            (McpTools.Outline, new() { ["path"] = dots }),
            (McpTools.Diagnostics, new() { ["path"] = secret }),
        })
        {
            CallToolResult refused = await Result(client, tool, arguments);
            if (refused.IsError != true || !Text(refused).Contains(refusal, StringComparison.Ordinal))
            {
                answered.Add($"{tool} {JsonSerializer.Serialize(arguments)}: {Text(refused)}");
            }
        }
        CallToolResult searched = await Result(client, McpTools.FindInFiles, new() { ["text"] = "STAND-IN", ["scope"] = "directory", ["directory"] = elsewhere });
        if (searched.IsError != true || !Text(searched).Contains(Refusals.OutOfReach(elsewhere, folder, changes: false), StringComparison.Ordinal))
        {
            answered.Add($"find_in_files in {elsewhere}: {Text(searched)}");
        }
        // Never asked of the disk, as asking would sign in to another machine's share as the person.
        const string Share = "//share.invalid/home/id_stand_in";
        foreach (string tool in new[] { McpTools.EditorText, McpTools.Open })
        {
            CallToolResult refused = await Result(client, tool, new() { ["path"] = Share });
            if (refused.IsError != true || !Text(refused).Contains(Refusals.NeverAsked(Share), StringComparison.Ordinal))
            {
                answered.Add($"{tool} {Share}: {Text(refused)}");
            }
        }
        Assert.True(answered.Count == 0, $"A file outside the open folder was not refused in the one sentence:\n{string.Join('\n', answered)}");
        Assert.Equal(Key, File.ReadAllText(secret));
        JsonElement workspace = await Call(client, McpTools.Workspace);
        Assert.True(!workspace.TryGetProperty("openFiles", out JsonElement open) || !open.GetRawText().Contains("id_stand_in", StringComparison.Ordinal),
            $"A file outside the folder was opened: {workspace}");

        // A breakpoint in a file outside, there or not, is set as named: nothing says the file is there or how long it is.
        string missing = Path.Combine(elsewhere, "missing.cs");
        List<JsonElement> placed = [];
        foreach (string file in new[] { secret, missing })
        {
            placed.Add(await Call(client, McpTools.BreakpointSet, new() { ["path"] = file, ["line"] = 99 }));
        }
        Assert.True(Unplaced(placed[0]) == Unplaced(placed[1]) && !placed[0].GetRawText().Contains("lines", StringComparison.Ordinal),
            $"breakpoint_set told a file outside the folder from one not there: {placed[0]} and {placed[1]}");
        // Each breakpoint as answered, its path aside.
        static string Unplaced(JsonElement breakpoints) => string.Join(", ", breakpoints.EnumerateArray().SelectMany(b => b.EnumerateObject().Where(p => p.Name != "path")).Select(p => p.ToString()));
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = secret, ["line"] = 99, ["remove"] = true });
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = missing, ["line"] = 99, ["remove"] = true });

        Assert.Equal(elsewhere, (await Call(client, McpTools.Open, new() { ["path"] = elsewhere })).GetProperty("folder").GetString());
        Assert.Contains("not a key", (await Call(client, McpTools.EditorText, new() { ["path"] = "id_stand_in" })).GetString()!, StringComparison.Ordinal);
    }

    /// <summary>A link in the open folder to a file outside it, and one to a folder outside it, lead nowhere an agent
    /// reaches: editor_text, edit and open through either are refused naming where the link leads, the file left as it was;
    /// find_in_files finds the text in the folder's own file and none through the link, and replace_in_files leaves the
    /// file the link leads to as it was, saying why.</summary>
    [Fact]
    public async Task An_agent_is_refused_a_file_outside_the_open_folder_through_a_link_in_it_and_finds_or_replaces_nothing_there()
    {
        (string folder, string elsewhere, string secret) = WorkAndHome();
        try
        {
            File.CreateSymbolicLink(Path.Combine(folder, "key.txt"), secret);
            Directory.CreateSymbolicLink(Path.Combine(folder, "keys"), elsewhere);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"A link in the folder to a file outside it takes a link this account may not make: {error.Message}");
        }
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        string refusal = Refusals.OutOfReach(secret, folder, changes: false);
        List<string> answered = [];
        foreach (string link in new[] { "key.txt", Path.Combine("keys", "id_stand_in"), Path.Combine(folder, "key.txt") })
        {
            foreach ((string tool, Dictionary<string, object?> arguments) in new (string, Dictionary<string, object?>)[]
            {
                (McpTools.EditorText, new() { ["path"] = link }),
                (McpTools.Edit, new() { ["path"] = link, ["oldText"] = "not a key", ["newText"] = "written" }),
                (McpTools.Open, new() { ["path"] = link }),
            })
            {
                CallToolResult refused = await Result(client, tool, arguments);
                if (refused.IsError != true || !Text(refused).Contains(refusal, StringComparison.Ordinal))
                {
                    answered.Add($"{tool} {link}: {Text(refused)}");
                }
            }
        }
        Assert.True(answered.Count == 0, $"A file outside the open folder was not refused through a link in it:\n{string.Join('\n', answered)}");
        Assert.Equal(Key, File.ReadAllText(secret));

        JsonElement found = await Call(client, McpTools.FindInFiles, new() { ["text"] = "STAND-IN" });
        Assert.True(found.GetProperty("count").GetInt32() == 1 && Assert.Single(found.GetProperty("matches").EnumerateArray()).GetProperty("path").GetString() == "Notes.txt",
            $"find_in_files answered more than the folder's own file, a link's leading out of it: {found}");
        string replaced = (await Call(client, McpTools.ReplaceInFiles, new() { ["text"] = "STAND-IN", ["replacement"] = "replaced" })).GetString()!;
        Assert.True(replaced.Contains($"not replaced: {Refusals.OutOfReach(secret, folder, changes: true)}", StringComparison.Ordinal),
            $"replace_in_files did not say it left the file a link leads to as it was: {replaced}");
        Assert.Equal(Key, File.ReadAllText(secret));
        Assert.Equal("notes on the replaced\n", File.ReadAllText(Path.Combine(folder, "Notes.txt")));
    }

    /// <summary>An app's folder whose solution names a library beside it (<c>../Lib/Lib.csproj</c>), with a file beside both
    /// that no project holds; each file's path, canonical.</summary>
    private (string Folder, string Library, string Beside) SolutionBeside()
    {
        string root = PathNames.Canonical(TestFolders.Create("mcp-beside-"));
        DeleteAtEnd(root);
        string folder = Directory.CreateDirectory(Path.Combine(root, "App")).FullName;
        TestFolders.Write(folder, "App.csproj", TestProjects.Console().With("  <ItemGroup>\n    <ProjectReference Include=\"../Lib/Lib.csproj\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "Program.cs", "System.Console.WriteLine(new Lib.Greeter().Greet());\n");
        TestFolders.Write(folder, "App.slnx", "<Solution>\n  <Project Path=\"App.csproj\" />\n  <Project Path=\"../Lib/Lib.csproj\" />\n</Solution>\n");
        TestFolders.Write(root, "Lib/Lib.csproj", TestProjects.Library());
        string library = TestFolders.Write(root, "Lib/Greeter.cs", "namespace Lib;\n\npublic sealed class Greeter\n{\n    public string Greet() => \"Hello\";\n}\n");
        return (folder, library, TestFolders.Write(root, "Notes/notes.txt", "notes beside the solution\n"));
    }

    /// <summary>A trusted folder's solution naming a project beside it puts that project's files in an agent's reach, to read
    /// and edit by <c>..</c> and by their full path as the folder's own; a file beside them no project holds is still refused.</summary>
    [Fact]
    public async Task An_agent_reads_and_edits_a_project_its_trusted_solution_names_beside_the_folder_and_nothing_else_there()
    {
        (string folder, string library, string beside) = SolutionBeside();
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        string relative = Path.Combine("..", "Lib", "Greeter.cs");
        Assert.Contains("public string Greet()", (await Call(client, McpTools.EditorText, new() { ["path"] = relative })).GetString()!, StringComparison.Ordinal);
        await Call(client, McpTools.Edit, new() { ["path"] = library, ["oldText"] = "\"Hello\"", ["newText"] = "\"Hello, beside\"" });
        Assert.Contains("\"Hello, beside\"", File.ReadAllText(library), StringComparison.Ordinal);
        CallToolResult refused = await Result(client, McpTools.EditorText, new() { ["path"] = Path.Combine("..", "Notes", "notes.txt") });
        Assert.True(refused.IsError == true && Text(refused).Contains(Refusals.OutOfReach(beside, folder, changes: false), StringComparison.Ordinal),
            $"editor_text of a file beside the solution's projects that none holds answered: {Text(refused)}");
    }

    /// <summary>A folder the person opened without trusting it lends an agent nothing beside it: the project its solution names
    /// there is refused to read and, once the person allowed edit, to edit, the file left as it was, while the folder's own
    /// files are read.</summary>
    [AvaloniaFact]
    public void An_agent_in_a_window_whose_folder_is_not_trusted_is_refused_the_project_its_solution_names_beside_it()
    {
        (string folder, string library, _) = SolutionBeside();
        string written = File.ReadAllText(library);
        using IDisposable asked = WorkspaceTrust.Ask(folder);
        MainWindow window = AgentsWindow(folder, languageServer: false);
        Ui.WaitFor(() => window.AskingTrust is { IsVisible: true }, "the trust question");
        Ui.ClickButton(window.AskingTrust!, WorkspaceTrustDialog.Untrusted);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId, folder])), "the agent attaching");
        try
        {
            Assert.Contains("Greeter", Text(Await(Waiting.Apart(() => Call(client, McpTools.EditorText, new() { ["path"] = "Program.cs" })), "the folder's own file")),
                StringComparison.Ordinal);
            string refusal = Refusals.OutOfReach(library, folder, changes: false);
            CallToolResult read = Await(Waiting.Apart(() => Result(client, McpTools.EditorText, new() { ["path"] = Path.Combine("..", "Lib", "Greeter.cs") })), "the read beside");
            Assert.True(read.IsError == true && Text(read).Contains(refusal, StringComparison.Ordinal), $"editor_text beside an untrusted folder answered: {Text(read)}");
            Task<CallToolResult> editing = Waiting.Apart(() => Result(client, McpTools.Edit, new() { ["path"] = library, ["oldText"] = "\"Hello\"", ["newText"] = "\"written\"" }));
            Press(Question(window, "the question for the agent's edit"), "Allow");
            CallToolResult edited = Await(editing, "the edit beside");
            Assert.True(edited.IsError == true && Text(edited).Contains(refusal, StringComparison.Ordinal), $"edit beside an untrusted folder answered: {Text(edited)}");
            Assert.Equal(written, File.ReadAllText(library));
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>A package's file, restored to a package folder outside the open folder, where definition leads, and a source
    /// generator's code copied outside it, where usages lead, are an agent's to read; edit refuses each as read-only, leaving
    /// it as it was.</summary>
    [Fact]
    public async Task An_agent_reads_a_packages_file_and_a_generators_code_outside_the_folder_where_its_places_lead_and_changes_neither()
    {
        string root = PathNames.Canonical(TestFolders.Create("mcp-reached-"));
        DeleteAtEnd(root);
        string folder = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        string packages = Path.Combine(root, "packages");
        await using StandInFeed feed = new();
        feed.Add("Passenger.Sample", "1.0.0", new Dictionary<string, string>
        {
            ["build/Passenger.Sample.targets"] = "<Project>\n  <ItemGroup>\n    <Compile Include=\"$(MSBuildThisFileDirectory)Brought.cs\" />\n  </ItemGroup>\n</Project>\n",
            ["build/Brought.cs"] = "namespace Sample;\n\npublic static class Brought\n{\n    public static string Say() => \"brought\";\n}\n",
        });
        // The stand-in alone, restoring into a package folder beside the open folder.
        TestFolders.Write(folder, "nuget.config", $"<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"stand-in\" value=\"{feed.Index}\" protocolVersion=\"3\" allowInsecureConnections=\"true\" />\n"
            + $"  </packageSources>\n  <config>\n    <add key=\"globalPackagesFolder\" value=\"{Passenger.Text.XmlEntities.Encode(packages)}\" />\n  </config>\n</configuration>\n");
        TestFolders.Write(folder, "Uses.csproj", TestProjects.Library().With("  <ItemGroup>\n    <PackageReference Include=\"Passenger.Sample\" Version=\"1.0.0\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "Caller.cs", "namespace Uses;\n\npublic static class Caller\n{\n    public static string Call() => Sample.Brought.Say();\n}\n");
        TestFolders.Write(folder, "Words.cs", "using System.Text.RegularExpressions;\n\nstatic partial class Words\n{\n"
            + "    [GeneratedRegex(@\"\\w+\")]\n    private static partial Regex Word();\n\n    public static int Count(string text) => Word().Count(text);\n}\n");
        await Restore.RunAsync(Path.Combine(folder, "Uses.csproj"));
        await using McpClient client = await Start(["--mcp", folder]);
        // Opened once the projects' evaluations, which name the package folder, have landed.
        await Call(client, McpTools.Open, new() { ["path"] = "Caller.cs" });

        string brought = Assert.Single((await Call(client, McpTools.Definition, new() { ["path"] = "Caller.cs", ["line"] = 5, ["column"] = 43 })).EnumerateArray())
            .GetProperty("path").GetString()!;
        Assert.True(PathNames.IsUnder(brought, PathNames.Canonical(packages)), $"definition of the package's class led to {brought}, not its file in {packages}");
        (string Path, int Line, int Column, string Text) copied = Used(await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 34 }))[^1];
        Assert.EndsWith($"{Path.DirectorySeparatorChar}RegexGenerator.g.cs", copied.Path, StringComparison.Ordinal);
        foreach ((string file, string text, string changed) in new[] { (brought, "public static string Say()", "\"brought\""), (copied.Path, "Regex Word()", "Word") })
        {
            string before = File.ReadAllText(file);
            string read = (await Call(client, McpTools.EditorText, new() { ["path"] = file })).GetString()!;
            Assert.True(read.Contains(text, StringComparison.Ordinal), $"editor_text of {file}, where the agent's places led, answered:\n{read}");
            CallToolResult edited = await Result(client, McpTools.Edit, new() { ["path"] = file, ["oldText"] = changed, ["newText"] = "written", ["all"] = true });
            Assert.True(edited.IsError == true && Text(edited).Contains("is read-only, so nothing changed", StringComparison.Ordinal), $"edit of {file} answered: {Text(edited)}");
            Assert.Equal(before, File.ReadAllText(file));
        }
    }

    /// <summary>settings_set refuses an agent each setting naming a program Passenger runs or code it loads, saying the
    /// person sets it, and keeps the one the person set.</summary>
    [Fact]
    public async Task Settings_naming_a_program_passenger_runs_are_refused_to_an_agent_and_kept_as_the_person_set_them()
    {
        (string folder, _, _) = WorkAndHome();
        KeepSettings(new JsonObject { ["terminalShell"] = "/bin/sh" });
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        string[] named = [.. AppSettings.NamingPrograms.Select(JsonNamingPolicy.CamelCase.ConvertName).Order(StringComparer.Ordinal)];
        Assert.Equal(["debugBrowser", "eslintPackage", "godotExecutable", "nodeInterpreter", "prettierPackage", "terminalShell"], named);
        Assert.All(Declared.Every.OfType<ProgramSetting>(), program => Assert.Contains(program.Member, named));
        List<string> answered = [];
        foreach (string name in named)
        {
            CallToolResult refused = await Result(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { [name] = "/tmp/elsewhere" } });
            if (refused.IsError != true || !Text(refused).Contains($"{name} names a program Passenger runs, or code it loads: the person sets it in Settings, never an agent", StringComparison.Ordinal))
            {
                answered.Add($"{name}: {Text(refused)}");
            }
        }
        Assert.True(answered.Count == 0, $"settings_set took a program's setting from an agent:\n{string.Join('\n', answered)}");
        JsonElement[] settings = [.. (await Call(client, McpTools.Settings)).GetProperty("settings").EnumerateArray()];
        Assert.Equal("/bin/sh", settings.Single(s => s.GetProperty("name").GetString() == "terminalShell").GetProperty("value").GetString());
        Assert.True(settings.Where(s => s.GetProperty("name").GetString() is { } name && name != "terminalShell" && named.Contains(name)).All(s => s.GetProperty("value").ValueKind == JsonValueKind.Null),
            $"A program's setting refused was kept all the same: {string.Join(", ", settings.Select(s => s.GetRawText()))}");
    }
}
