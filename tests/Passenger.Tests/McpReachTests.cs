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
            // edit, which changes the file, is refused as a change is.
            string refusal = Refusals.OutOfReach(secret, folder, changes: tool == McpTools.Edit);
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
                string refusal = Refusals.OutOfReach(secret, folder, changes: tool == McpTools.Edit);
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
            Press(Question(window, "the question for the agent's edit"), AgentToolDialog.AllowForSession);
            CallToolResult edited = Await(editing, "the edit beside");
            Assert.True(edited.IsError == true && Text(edited).Contains(Refusals.OutOfReach(library, folder, changes: true), StringComparison.Ordinal),
                $"edit beside an untrusted folder answered: {Text(edited)}");
            Assert.Equal(written, File.ReadAllText(library));
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent's edit of its trusted solution naming a project file at the disk's root, one in a folder holding the
    /// open folder and a real project beside it the person never added reaches and lends nothing new: a file beside the
    /// solution and the real project's file are refused, that project's folder is never trusted, and the library the solution
    /// named before is still read and trusted with it; opened again by the person, the solution's real project is theirs.</summary>
    [AvaloniaFact]
    public void An_agents_edit_of_its_solution_reaches_and_lends_nothing_beside_what_the_person_had()
    {
        (string folder, string library, string beside) = SolutionBeside();
        string root = Path.GetDirectoryName(folder)!;
        string elsewhere = Path.GetDirectoryName(TestFolders.Write(root, "Other/Other.csproj", TestProjects.Library()))!;
        string kept = TestFolders.Write(root, "Other/Kept.cs", "namespace Other;\n");
        string libraryFolder = Path.GetDirectoryName(library)!;
        // Each trusted only as the solution lends it, in a process that trusts every other folder.
        using IDisposable askedLibrary = WorkspaceTrust.Ask(libraryFolder);
        using IDisposable askedElsewhere = WorkspaceTrust.Ask(elsewhere);
        MainWindow window = AgentsWindow(folder, languageServer: false);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId, folder])), "the agent attaching");
        try
        {
            Assert.True(WorkspaceTrust.Trusts(library), "The library the trusted solution names beside it was not trusted with it");
            Task<CallToolResult> editing = Waiting.Apart(() => Result(client, McpTools.Edit, new()
            {
                ["path"] = "App.slnx",
                ["oldText"] = "</Solution>",
                ["newText"] = "  <Project Path=\"/x.csproj\" />\n  <Project Path=\"../../x.csproj\" />\n  <Project Path=\"../Other/Other.csproj\" />\n</Solution>",
            }));
            Press(Question(window, "the question for the agent's edit"), AgentToolDialog.AllowForSession);
            CallToolResult edited = Await(editing, "the agent's edit of the solution");
            Assert.True(edited.IsError != true, $"The agent's edit of the solution answered: {Text(edited)}");
            Ui.WaitFor(() => window.Projects.Any(p => p.Name == "Other"), () => $"the solution read again as the agent wrote it: {string.Join(", ", window.Projects.Select(p => p.Name))}");

            foreach (string file in (string[])[beside, kept])
            {
                CallToolResult read = Await(Waiting.Apart(() => Result(client, McpTools.EditorText, new() { ["path"] = file })), $"the read of {file}");
                Assert.True(read.IsError == true && Text(read).Contains(Refusals.OutOfReach(file, folder, changes: false), StringComparison.Ordinal),
                    $"editor_text of {file}, which the agent's edit of the solution named the way to, answered: {Text(read)}");
            }
            Assert.Contains("public string Greet()", Text(Await(Waiting.Apart(() => Result(client, McpTools.EditorText, new() { ["path"] = library })), "the library's read")),
                StringComparison.Ordinal);
            Assert.Equal([libraryFolder], window.Session.Reached);
            Assert.False(WorkspaceTrust.Trusts(kept), "The project the agent's edit added beside the solution was trusted with it");
            Assert.True(WorkspaceTrust.Trusts(library), "The library the solution named before the agent's edit was no longer trusted with it");

            window.OpenFolder(Path.Combine(folder, "App.slnx"));
            Ui.WaitFor(() => window.Session.Reached.Contains(elsewhere), () => $"the person's open to reach the solution's real project; it reaches {string.Join(", ", window.Session.Reached)}");
            Assert.True(WorkspaceTrust.Trusts(kept), "The solution's real project was not trusted with it once the person opened it");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>A trusted folder's single-file AppHost naming a service beside it by path (Aspire 17's AddDotnetProject) puts
    /// the service's files in an agent's reach and lends them the folder's trust; an agent's edit of the AppHost naming another
    /// project beside it reaches and lends nothing new, the service named before still read and trusted; once the person saves
    /// the AppHost themselves, the project it names is theirs.</summary>
    [AvaloniaFact]
    public void An_agents_edit_of_an_AppHost_reaches_and_lends_nothing_beside_what_the_person_had()
    {
        string root = PathNames.Canonical(TestFolders.Create("mcp-apphost-"));
        DeleteAtEnd(root);
        string folder = Directory.CreateDirectory(Path.Combine(root, "App")).FullName;
        string appHost = TestFolders.Write(folder, "apphost.cs", $"#:sdk Aspire.AppHost.Sdk@{AspireApp.TemplatesVersion}\n\n"
            + "var builder = DistributedApplication.CreateBuilder(args);\nbuilder.AddDotnetProject(\"svc\", \"../Svc/Svc.csproj\");\nbuilder.Build().Run();\n");
        TestFolders.Write(root, "Svc/Svc.csproj", TestProjects.Library());
        string greeter = TestFolders.Write(root, "Svc/Greeter.cs", "namespace Svc;\n\npublic sealed class Greeter\n{\n}\n");
        string other = TestFolders.Write(root, "Other/Other.csproj", TestProjects.Library());
        string kept = TestFolders.Write(root, "Other/Kept.cs", "namespace Other;\n");
        string service = Path.GetDirectoryName(greeter)!;
        string elsewhere = Path.GetDirectoryName(kept)!;
        // Each trusted only as the open folder lends it, in a process that trusts every other folder.
        using IDisposable askedService = WorkspaceTrust.Ask(service);
        using IDisposable askedElsewhere = WorkspaceTrust.Ask(elsewhere);
        MainWindow window = AgentsWindow(folder, languageServer: false);
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId, folder])), "the agent attaching");
        try
        {
            Ui.WaitFor(() => window.Session.Reached.Contains(service, PathNames.Comparer),
                () => $"the service the AppHost names beside the folder to be reached; it reaches {string.Join(", ", window.Session.Reached)}");
            Assert.True(WorkspaceTrust.Trusts(greeter), "The service the trusted folder's AppHost names beside it was not trusted with it");
            Assert.Contains("class Greeter", Text(Await(Waiting.Apart(() => Result(client, McpTools.EditorText, new() { ["path"] = greeter })), "the service's read")),
                StringComparison.Ordinal);

            Task<CallToolResult> editing = Waiting.Apart(() => Result(client, McpTools.Edit, new()
            {
                ["path"] = "apphost.cs",
                ["oldText"] = "builder.Build().Run();",
                ["newText"] = "builder.AddDotnetProject(\"other\", \"../Other/Other.csproj\");\nbuilder.Build().Run();",
            }));
            Press(Question(window, "the question for the agent's edit"), AgentToolDialog.AllowForSession);
            CallToolResult edited = Await(editing, "the agent's edit of the AppHost");
            Assert.True(edited.IsError != true, $"The agent's edit of the AppHost answered: {Text(edited)}");
            Ui.WaitFor(() => window.Session.AspireFiles.AppHostsRunning(PathNames.Canonical(other)).Count > 0, "the AppHost read again as the agent wrote it");

            CallToolResult read = Await(Waiting.Apart(() => Result(client, McpTools.EditorText, new() { ["path"] = kept })), "the read of the project the agent named");
            Assert.True(read.IsError == true && Text(read).Contains(Refusals.OutOfReach(kept, folder, changes: false), StringComparison.Ordinal),
                $"editor_text of {kept}, which the agent's edit of the AppHost named the way to, answered: {Text(read)}");
            Assert.Equal([service], window.Session.Reached);
            Assert.False(WorkspaceTrust.Trusts(kept), "The project the agent's edit of the AppHost named beside the folder was trusted with it");
            Assert.Equal(Refusals.NamedByAgent("Other"), window.Session.WhyNotReached(PathNames.Canonical(other)));
            Assert.True(WorkspaceTrust.Trusts(greeter), "The service the AppHost named before the agent's edit was no longer trusted with it");

            window.OpenFile(appHost);
            Typing.Replace(window, "\"other\"", "\"others\"");
            Ui.Run(window, AppActions.SaveAll);
            Ui.WaitFor(() => window.Session.Reached.Contains(elsewhere, PathNames.Comparer),
                () => $"the person's save of the AppHost to reach the project it names; it reaches {string.Join(", ", window.Session.Reached)}");
            Assert.True(WorkspaceTrust.Trusts(kept), "The project the AppHost names was not trusted with the folder once the person saved it");
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

    /// <summary>The C# language server's folder of library code in the temporary folder it takes (on Linux one of the
    /// person's own, never the shared /tmp) is an agent's to read where it is the person's own, edit refusing a file there as
    /// read-only by where it lies; where a link stands in the temporary folder's place (as another user of a shared /tmp may
    /// leave one) leading to a folder of the person's, nothing through it is read or changed.</summary>
    [Fact]
    public async Task An_agent_reads_the_language_servers_library_code_only_where_its_folder_is_the_persons_own()
    {
        (string folder, string elsewhere, string secret) = WorkAndHome();
        string root = Path.GetDirectoryName(folder)!;
        string serverTemporary = Passenger.Lsp.RoslynServerFolder.Temporary(Path.Combine(OwnState, "cache")) ?? Path.Combine(root, "own-temporary");
        string own = Directory.CreateDirectory(Path.Combine(serverTemporary, "MetadataAsSource", "Library")).FullName;
        string stub = TestFolders.Write(own, "Greeter.cs", "namespace Library;\n\npublic class Greeter\n{\n}\n");
        await using (McpClient client = await Start(["--mcp", folder], environment: Temporary(Path.Combine(root, "own-temporary")), languageServer: false))
        {
            Assert.Contains("public class Greeter", (await Call(client, McpTools.EditorText, new() { ["path"] = stub })).GetString()!, StringComparison.Ordinal);
            CallToolResult edited = await Result(client, McpTools.Edit, new() { ["path"] = stub, ["oldText"] = "Greeter", ["newText"] = "Written" });
            Assert.True(edited.IsError == true && Text(edited).Contains("is read-only, so nothing changed", StringComparison.Ordinal), $"edit of the server's library code answered: {Text(edited)}");
        }
        if (OperatingSystem.IsWindows())
        {
            // The temporary folder is under the person's profile, which no other user writes.
            return;
        }
        string linked = Directory.CreateDirectory(Path.Combine(root, "shared-temporary")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(linked, "MetadataAsSource"), elsewhere);
        await using (McpClient client = await Start(["--mcp", folder], environment: Temporary(linked), languageServer: false))
        {
            string through = Path.Combine(linked, "MetadataAsSource", "id_stand_in");
            CallToolResult read = await Result(client, McpTools.EditorText, new() { ["path"] = through });
            Assert.True(read.IsError == true && Text(read).Contains(Refusals.OutOfReach(secret, folder, changes: false), StringComparison.Ordinal),
                $"editor_text through a link in the server's place answered: {Text(read)}");
            CallToolResult edited = await Result(client, McpTools.Edit, new() { ["path"] = through, ["oldText"] = "not a key", ["newText"] = "written" });
            Assert.True(edited.IsError == true && Text(edited).Contains(Refusals.OutOfReach(secret, folder, changes: true), StringComparison.Ordinal),
                $"edit through a link in the server's place answered: {Text(edited)}");
        }
        Assert.Equal(Key, File.ReadAllText(secret));

        // Windows takes its temporary folder from TMP and TEMP, every other system from TMPDIR.
        static Dictionary<string, string?> Temporary(string folder) => new() { ["TMPDIR"] = folder, ["TMP"] = folder, ["TEMP"] = folder };
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
