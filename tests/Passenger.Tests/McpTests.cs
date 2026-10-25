using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Passenger as an agent's IDE with no window (<c>--mcp</c>), over the Model Context Protocol with the SDK's own
/// client: a project worked through it (its tests, packages, actions, publish, Git and the terminal).</summary>
public sealed class McpTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpTests(RestoredFolders folders)
        : base(folders)
    {
    }

    [Fact]
    public async Task An_agent_that_lists_the_tests_as_it_starts_gets_those_the_test_projects_code_declares()
    {
        // A test project as written, never built or restored: its tests come from its code, which the agent's first look waits for.
        string tests = PathNames.Canonical(TestFolders.Create("mcp-tests-"));
        DeleteAtEnd(tests);
        File.WriteAllText(Path.Combine(tests, "Sums.Tests.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <OutputType>Exe</OutputType>\n  </PropertyGroup>\n"
            + "  <ItemGroup>\n    <PackageReference Include=\"xunit.v3\" Version=\"3.2.2\" />\n  </ItemGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(tests, "SumTests.cs"),
            "using Xunit;\n\nnamespace Sums;\n\npublic class SumTests\n{\n    [Fact]\n    public void Two_and_two_make_four() => Assert.Equal(4, 2 + 2);\n\n"
            + "    [Fact]\n    public void Three_and_three_make_six() => Assert.Equal(6, 3 + 3);\n}\n");
        await using McpClient client = await Start(["--mcp", tests], languageServer: false);
        Assert.Equal(2, (await Call(client, McpTools.TestsList)).GetArrayLength());
        JsonElement test = Assert.Single((await Call(client, McpTools.TestsList, new() { ["filter"] = "two_make_four" })).EnumerateArray());
        Assert.Equal(("Sums.SumTests.Two_and_two_make_four", "notRun", "SumTests.cs:8"),
            (test.GetProperty("test").GetString(), test.GetProperty("state").GetString(), test.GetProperty("location").GetString()));
        Assert.Equal(1, (await Call(client, McpTools.TestsList, new() { ["limit"] = 1 })).GetArrayLength());
        Assert.Equal(0, (await Call(client, McpTools.TestsList, new() { ["state"] = "failed" })).GetArrayLength());
        JsonElement notRun = await Call(client, McpTools.TestsList, new() { ["state"] = test.GetProperty("state").GetString() });
        Assert.True(notRun.GetArrayLength() == 2 && notRun.EnumerateArray().All(t => t.GetProperty("state").GetString() == "notRun"),
            $"tests_list asked for the state it says the tests are in answered: {notRun}");
        CallToolResult wrong = await Result(client, McpTools.TestsList, new() { ["state"] = "broken" });
        Assert.True(wrong.IsError == true && Text(wrong).Contains("there are passed, failed, skipped and notRun", StringComparison.Ordinal), $"tests_list in no state answered: {Text(wrong)}");
    }

    /// <summary>A run refused for a reason of its own (here a launch profile's command named RetryFailed, which Passenger does
    /// not run) is answered with that reason alone, though the last build's errors are still in Problems; a run refused for
    /// the build it ran failing still names them.</summary>
    [Fact]
    public async Task An_agents_run_refused_for_anything_but_its_own_failed_build_names_no_build_errors()
    {
        Write("Program.cs", "int count = \"three\";\nConsole.WriteLine(count);\n");
        Directory.CreateDirectory(Path.Combine(Folder, "Properties"));
        File.WriteAllText(Path.Combine(Folder, "Properties", "launchSettings.json"), "{\n  \"profiles\": {\n    \"Jobs\": { \"commandName\": \"RetryFailed\" }\n  }\n}\n");
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        JsonElement built = await Call(client, McpTools.Build);
        JsonElement error = built.GetProperty("problems").EnumerateArray().FirstOrDefault(p => p.GetProperty("severity").GetString() == "error");
        Assert.True(!built.GetProperty("succeeded").GetBoolean() && error.ValueKind == JsonValueKind.Object, $"The build of a string given to an int did not fail with an error: {built}");
        string code = error.GetProperty("code").GetString()!;

        CallToolResult jobs = await Result(client, McpTools.Run, new() { ["configuration"] = "Sum: Jobs" });
        Assert.True(jobs.IsError == true && Text(jobs).Contains("Passenger does not know the RetryFailed command", StringComparison.Ordinal),
            $"The run of a profile Passenger does not run was answered: {Text(jobs)}");
        Assert.False(Text(jobs).Contains(code, StringComparison.Ordinal), $"A run refused before any build named the last build's errors: {Text(jobs)}");

        CallToolResult sum = await Result(client, McpTools.Run, new() { ["configuration"] = "Sum" });
        Assert.True(sum.IsError == true && Text(sum).Contains($"{code} ", StringComparison.Ordinal) && Text(sum).Contains("Program.cs:1:", StringComparison.Ordinal),
            $"A run refused for its own build's failure did not name that build's error: {Text(sum)}");
    }

    /// <summary>A package an agent installs or removes is in or out of the project file and Directory.Packages.props the
    /// editor holds once the tool answers, which names the version installed and the files it changed, so the agent's
    /// next look sees what it did, not the text from before.</summary>
    [Fact]
    public async Task An_agents_package_installed_and_removed_shows_in_the_open_project_file_as_each_tool_answers()
    {
        string library = PathNames.Canonical(TestFolders.Create("mcp-nuget-"));
        DeleteAtEnd(library);
        string feed = Directory.CreateDirectory(Path.Combine(library, "feed")).FullName;
        File.WriteAllBytes(Path.Combine(feed, "Passenger.Sample.1.0.0.nupkg"), StandInFeed.Package("Passenger.Sample", "1.0.0"));
        // Only the folder feed, and restores kept out of the person's own package cache.
        File.WriteAllText(Path.Combine(library, "nuget.config"),
            "<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"local\" value=\"feed\" />\n  </packageSources>\n"
            + "  <config>\n    <add key=\"globalPackagesFolder\" value=\"packages\" />\n  </config>\n</configuration>\n");
        File.WriteAllText(Path.Combine(library, "Lib.csproj"),
            TestProjects.Library());
        // Central package management: the version goes in Directory.Packages.props.
        File.WriteAllText(Path.Combine(library, "Directory.Packages.props"),
            "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n");
        await using McpClient client = await Start(["--mcp", library], languageServer: false);
        Dictionary<string, object?> project = new() { ["path"] = "Lib.csproj" };
        Dictionary<string, object?> versions = new() { ["path"] = "Directory.Packages.props" };
        Assert.DoesNotContain("Passenger.Sample", (await Call(client, McpTools.EditorText, project)).GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Passenger.Sample", (await Call(client, McpTools.EditorText, versions)).GetString(), StringComparison.Ordinal);
        JsonElement found = Assert.Single((await Call(client, McpTools.NugetSearch, new() { ["query"] = "Passenger" })).EnumerateArray());
        Assert.Equal(("Passenger.Sample", "1.0.0"), (found.GetProperty("id").GetString(), found.GetProperty("version").GetString()));

        JsonElement install = await Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample" });
        Assert.Equal(("Passenger.Sample", "1.0.0", "Lib"), (install.GetProperty("package").GetString(), install.GetProperty("version").GetString(), install.GetProperty("project").GetString()));
        Assert.Equal(["Directory.Packages.props", "Lib.csproj"], install.GetProperty("changed").EnumerateArray().Select(f => f.GetString()!).Order(StringComparer.Ordinal));
        string installed = (await Call(client, McpTools.EditorText, project)).GetString()!;
        Assert.True(installed.Contains("<PackageReference Include=\"Passenger.Sample\" />", StringComparison.Ordinal),
            $"nuget_install answered with the open project file still as it was:\n{installed}");
        string pinned = (await Call(client, McpTools.EditorText, versions)).GetString()!;
        Assert.True(pinned.Contains("<PackageVersion Include=\"Passenger.Sample\" Version=\"1.0.0\" />", StringComparison.Ordinal),
            $"nuget_install answered with the open Directory.Packages.props still as it was:\n{pinned}");

        JsonElement remove = await Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample", ["remove"] = true });
        Assert.Contains("Lib.csproj", remove.GetProperty("changed").EnumerateArray().Select(f => f.GetString()));
        Assert.True(!remove.TryGetProperty("version", out JsonElement left) || left.ValueKind == JsonValueKind.Null, $"nuget_install with remove answered a version: {remove}");
        string removed = (await Call(client, McpTools.EditorText, project)).GetString()!;
        Assert.False(removed.Contains("Passenger.Sample", StringComparison.Ordinal), $"nuget_install with remove answered with the open project file still holding the package:\n{removed}");
    }

    /// <summary>Git's tools on a repository, a merge stopped for a conflict among them: the agent finds it in vcs_status (what
    /// is under way, each side's branch, what each did to the file), resolves it with theirs, and its commit finishes the
    /// merge; and a command in the IDE's terminal.</summary>
    [Fact]
    public async Task An_agent_reads_and_commits_changes_resolves_a_merges_conflict_and_runs_a_command_in_the_ides_terminal()
    {
        string repository = TestFolders.Create("mcp-git-");
        try
        {
            File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\ntwo\nthree\n");
            TestGit.Init(repository);
            File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\n2\nthree\n");
            File.WriteAllText(Path.Combine(repository, "New.txt"), "new\n");

            await using McpClient client = await Start(["--mcp", repository]);
            JsonElement status = await Call(client, McpTools.VcsStatus);
            Assert.Equal("main", status.GetProperty("branch").GetString());
            Assert.Equal(["Notes.txt modified", "New.txt unversioned"], status.GetProperty("changes").EnumerateArray()
                .Select(c => $"{c.GetProperty("path").GetString()} {c.GetProperty("kind").GetString()}").Order(StringComparer.Ordinal).Reverse());
            string diff = (await Call(client, McpTools.VcsStatus, new() { ["path"] = "Notes.txt" })).GetString()!;
            Assert.Contains("-two\n+2\n", diff, StringComparison.Ordinal);

            // With no paths, every change but the new file goes in.
            Assert.EndsWith(" Two as a number", (await Call(client, McpTools.VcsCommit, new() { ["message"] = "Two as a number" })).GetString(), StringComparison.Ordinal);
            JsonElement log = await Call(client, McpTools.VcsLog, new() { ["count"] = 5 });
            Assert.Equal(["Two as a number", "First"], log.EnumerateArray().Select(c => c.GetProperty("subject").GetString()));
            Assert.Equal(["New.txt"], (await Call(client, McpTools.VcsStatus)).GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("path").GetString()));

            JsonElement stashed = Assert.Single((await Call(client, McpTools.VcsStash, new() { ["action"] = "push", ["message"] = "the new file" })).EnumerateArray());
            Assert.EndsWith("the new file", stashed.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(repository, "New.txt")), "Stash Changes left the new file in place");
            Assert.Empty((await Call(client, McpTools.VcsStash, new() { ["action"] = "pop", ["index"] = 0 })).EnumerateArray());
            Assert.Equal("new\n", File.ReadAllText(Path.Combine(repository, "New.txt")));

            // A branch an agent names is a name, never one of git's options: git switch --orphan=x would empty the tree.
            CallToolResult orphan = await Result(client, McpTools.VcsCheckout, new() { ["branch"] = "--orphan=x" });
            Assert.True(orphan.IsError, "vcs_checkout took the branch --orphan=x for an option, and checked out an empty branch");
            Assert.True(File.Exists(Path.Combine(repository, "Notes.txt")), "Checking out the branch --orphan=x emptied the working tree");
            Assert.Equal("main", (await Call(client, McpTools.VcsStatus)).GetProperty("branch").GetString());

            string notes = Path.Combine(repository, "Notes.txt");
            TestGit.Run(repository, "switch", "-q", "-c", "feature");
            File.WriteAllText(notes, "one\nTWO\nthree\n");
            TestGit.Run(repository, "commit", "-q", "-am", "Two in capitals");
            TestGit.Run(repository, "switch", "-q", "main");
            File.WriteAllText(notes, "one\nII\nthree\n");
            TestGit.Run(repository, "commit", "-q", "-am", "Two in Roman");
            ChildResult merging = await ChildProcesses.RunAsync(ChildProcesses.Command("git", ["merge", "feature"], repository), limit: TimeSpan.FromMinutes(1), cancel: Cancel);
            Assert.False(merging.Succeeded, $"The merge was to stop for its conflict: {merging.Output}");
            JsonElement conflicts = (await Call(client, McpTools.VcsStatus)).GetProperty("conflicts");
            Assert.Equal("Merging branch feature into branch main", conflicts.GetProperty("operation").GetString());
            Assert.Equal(("main", "feature"), (conflicts.GetProperty("yours").GetString(), conflicts.GetProperty("theirs").GetString()));
            JsonElement conflicted = Assert.Single(conflicts.GetProperty("files").EnumerateArray());
            Assert.Equal(("Notes.txt", "modified", "modified"), (conflicted.GetProperty("path").GetString(), conflicted.GetProperty("yours").GetString(), conflicted.GetProperty("theirs").GetString()));
            CallToolResult early = await Result(client, McpTools.VcsCommit, new() { ["message"] = "Half a merge" });
            Assert.True(early.IsError == true, "A commit was made with a file still in conflict");
            Assert.Equal("Notes.txt resolved; no conflicts are left", (await Call(client, McpTools.VcsResolve, new() { ["path"] = "Notes.txt", ["take"] = "theirs" })).GetString());
            Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(notes));
            JsonElement resolved = await Call(client, McpTools.VcsStatus);
            Assert.True(!resolved.TryGetProperty("conflicts", out JsonElement left) || left.ValueKind == JsonValueKind.Null, $"vcs_status still lists conflicts: {left}");
            await Call(client, McpTools.VcsCommit, new() { ["message"] = "Merge feature" });
            Assert.Equal(3, TestGit.Run(repository, "rev-list", "--parents", "-n", "1", "HEAD").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal("one\nTWO\nthree\n", TestGit.Run(repository, "show", "HEAD:Notes.txt"));

            // Windows' own PowerShell (5.1, where pwsh is not installed) has no &&, and sh has no PowerShell subexpressions.
            string command = OperatingSystem.IsWindows() ? "echo (6*7); ls" : "echo $((6*7)) && ls";
            string answer = (await Call(client, McpTools.TerminalRun, new() { ["command"] = command })).GetString()!;
            Assert.Contains("42", answer, StringComparison.Ordinal);
            Assert.Contains("Notes.txt", answer, StringComparison.Ordinal);
            string screen = (await Call(client, McpTools.Output, new() { ["of"] = "terminal" })).GetString()!;
            Assert.True(screen.Contains("42", StringComparison.Ordinal), $"output of terminal did not show what the command printed:\n{screen}");
            if (OperatingSystem.IsWindows())
            {
                // The shells' history is the run's (HISTFILE), never the person's.
                string history = Path.Combine(TestRun.Root, "history");
                Assert.True(File.Exists(history) && File.ReadAllText(history).Contains(command, StringComparison.Ordinal),
                    $"PowerShell kept the command typed into the terminal, {command}, elsewhere than the run's history ({history}): in the person's own");
            }
            else
            {
                // Past the scrollback's limit, where each line printed drops the oldest.
                int beyond = global::Passenger.Terminal.TerminalScreen.ScrollbackLimit + 100;
                await Call(client, McpTools.TerminalRun, new() { ["command"] = $"seq 1 {beyond}" });
                string past = (await Call(client, McpTools.TerminalRun, new() { ["command"] = "echo one; echo two" })).GetString()!;
                Assert.True(past.Contains("one\ntwo", StringComparison.Ordinal), $"past the scrollback's limit, terminal_run answered a command that printed one and two with:\n{past}");
            }
        }
        finally
        {
            TestFolders.Delete(repository);
        }
    }

    /// <summary>Commit and Push to a remote that refuses every push: the agent hears the refusal, in git's words, as the
    /// tool's error, and the commit stays made.</summary>
    [Fact]
    public async Task An_agents_push_the_remote_refuses_is_an_error_naming_the_refusal_and_the_commit_stays_made()
    {
        string root = TestFolders.Create("mcp-push-");
        try
        {
            string work = Repository(root, refusing: true);
            await using McpClient client = await Start(["--mcp", work]);
            CallToolResult refused = await Result(client, McpTools.VcsCommit, new() { ["message"] = "Two as a number", ["push"] = true });
            string said = Text(refused);
            Assert.True(refused.IsError == true, $"A push the remote refused was answered as done: {said}");
            Assert.Contains("pre-receive hook declined", said, StringComparison.Ordinal);
            Assert.Contains("pushes are closed here", said, StringComparison.Ordinal);
            Assert.Equal("Two as a number", TestGit.Run(work, "log", "-1", "--format=%s").Trim());
            Assert.True(TestGit.Run(Path.Combine(root, "remote.git"), "for-each-ref", "refs/heads").Trim().Length == 0, "The remote took the push it refused");
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    /// <summary>Started as the Finder or the Dock start it, Passenger runs the git its login shell's PATH finds, though the
    /// profile leaves a job running, and no git it starts waits on that job.</summary>
    [Fact]
    public async Task A_login_profile_that_leaves_a_job_running_holds_up_no_git_Passenger_starts()
    {
        if (OperatingSystem.IsWindows())
        {
            // A Windows program gets the person's whole environment, PATH included.
            return;
        }
        string home = TestFolders.Create("home-");
        string repository = TestFolders.Create("mcp-shell-");
        try
        {
            // As `some-daemon &` in a profile: the job keeps the shell's output open long after the shell has ended.
            string used = ProfileGit(home, "sleep 300 &\n");
            TestGit.Run(repository, "init", "-q");
            await using McpClient client = await Start(["--mcp", repository], environment: Launchd(home));
            await Call(client, McpTools.VcsStatus);
            Assert.True(File.Exists(used), "With a job its profile left running, Passenger never took the login shell's PATH, and ran a git other than the one it finds");

            // Each git start waits for the shell's environment while it is still being read.
            long began = Stopwatch.GetTimestamp();
            await Call(client, McpTools.VcsStatus);
            TimeSpan took = Stopwatch.GetElapsedTime(began);
            Assert.True(took < TimeSpan.FromSeconds(10).Scaled(),
                $"Git's status took {took.TotalSeconds:0.0} s the second time: the git it ran waited for the login shell's environment again, the profile's job holding up its read");
        }
        finally
        {
            TestFolders.Delete(home);
            TestFolders.Delete(repository);
        }
    }

    /// <summary>A call whose arguments its tool does not take is answered with what is wrong and every parameter the tool
    /// takes, before the tool runs.</summary>
    [Fact]
    public async Task An_agents_call_with_a_parameter_its_tool_lacks_or_misses_hears_which_and_what_the_tool_takes()
    {
        string folder = TestFolders.Create("mcp-parameters-");
        DeleteAtEnd(folder);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        CallToolResult named = await Result(client, McpTools.FindSymbol, new() { ["name"] = "Language" });
        Assert.True(named.IsError == true && Text(named) == "find_symbol has no parameter name, and needs query. Its parameters: query (string, required), kind (string), "
            + "limit (integer), exact (boolean)", $"find_symbol given name for query answered: {Text(named)}");
        CallToolResult typed = await Result(client, McpTools.EditorText, new() { ["path"] = "Program.cs", ["from"] = "first" });
        Assert.True(typed.IsError == true && Text(typed).StartsWith("editor_text takes an integer as from, not \"first\". Its parameters: path (string, required), from (integer)", StringComparison.Ordinal),
            $"editor_text given a word for a line answered: {Text(typed)}");
    }

    /// <summary>An agent's view of the IDE's actions: every one the IDE declares, listed with why it cannot run now, and run by
    /// its id as its shortcut would.</summary>
    [Fact]
    public async Task An_agent_lists_every_action_the_ide_declares_and_one_it_runs_says_why_when_it_cannot()
    {
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        string[] listed = [.. (await Call(client, McpTools.IdeActions)).EnumerateArray().Select(a => a.GetString()!)];
        HashSet<string> ids = [.. listed.Select(line => line[..line.IndexOf(": ", StringComparison.Ordinal)])];
        string[] declared = [.. typeof(AppActions).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!)];
        string[] missing = [.. declared.Where(id => !ids.Contains(id))];
        Assert.True(missing.Length == 0, $"ide_actions leaves out {string.Join(", ", missing)}");

        string hotReload = Assert.Single(listed, l => l.StartsWith($"{AppActions.ApplyHotReloadChanges}: ", StringComparison.Ordinal));
        Assert.Contains(" - cannot run: Nothing runs", hotReload, StringComparison.Ordinal);
        CallToolResult refused = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.ApplyHotReloadChanges });
        Assert.True(refused.IsError == true && Text(refused).Contains($"{AppActions.ApplyHotReloadChanges} did not run: Nothing runs", StringComparison.Ordinal),
            $"Apply Hot Reload Changes with nothing running answered: {Text(refused)}");
        string selectElement = Assert.Single(listed, l => l.StartsWith($"{AppActions.SelectElement}: ", StringComparison.Ordinal));
        Assert.Contains("cannot run: No Avalonia or WPF app is being debugged", selectElement, StringComparison.Ordinal);

        Assert.Equal($"Ran {AppActions.ShowLiveTree}", (await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ShowLiveTree })).GetString());
    }

    /// <summary>An agent's publish, build's with a project to publish: the project to a folder, answered with where it landed
    /// or why it did not.</summary>
    [Fact]
    public async Task An_agent_publishes_a_project_and_is_told_the_folder_it_landed_in_or_why_not()
    {
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        JsonElement published = await Call(client, McpTools.Build, new() { ["publish"] = "Sum" });
        Assert.True(published.GetProperty("succeeded").GetBoolean(), $"The publish ended: {published}");
        string landed = published.GetProperty("folder").GetString() ?? string.Empty;
        Assert.True(File.Exists(Path.Combine(landed, "Sum.dll")), $"The publish says it landed in {landed}, which holds no Sum.dll");
        Assert.StartsWith("Published Sum to ", published.GetProperty("outcome").GetString(), StringComparison.Ordinal);

        JsonElement missing = await Call(client, McpTools.Build, new() { ["publish"] = "Sum", ["profile"] = "Nightly" });
        Assert.False(missing.GetProperty("succeeded").GetBoolean(), $"A publish with no such profile ended: {missing}");
        Assert.Contains("has no publish profile Nightly", missing.GetProperty("outcome").GetString(), StringComparison.Ordinal);
    }

    /// <summary>A home whose login profile (zsh's and sh's) puts a git of its own first on the PATH, as Homebrew's shellenv
    /// does, and then does <paramref name="more"/>; the git notes that it ran in the file whose path this returns.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string ProfileGit(string home, string more = "")
    {
        string tools = Path.Combine(home, "tools");
        Directory.CreateDirectory(tools);
        string git = Path.Combine(tools, "git");
        string used = Path.Combine(home, "used");
        string real = global::Passenger.Core.Programs.Git()!;
        File.WriteAllText(git, $"#!/bin/sh\ntouch \"{used}\"\nexec {real} \"$@\"\n");
        File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(home, ".zprofile"), $"export PATH=\"{tools}:$PATH\"\n{more}");
        File.WriteAllText(Path.Combine(home, ".profile"), $"export PATH=\"{tools}:$PATH\"\n{more}");
        return used;
    }

    /// <summary>launchd's environment, as the Finder or the Dock start Passenger: the bare PATH, no terminal, no .NET or zsh
    /// settings of the person's, the home given.</summary>
    private static Dictionary<string, string?> Launchd(string home) => new()
    {
        ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin",
        ["HOME"] = home,
        ["SHELL"] = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/sh",
        ["TERM"] = null,
        ["ZDOTDIR"] = null,
        ["DOTNET_ROOT"] = null,
    };
}
