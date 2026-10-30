using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Mcp;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Passenger as an agent's IDE with no window (<c>--mcp</c>), over the Model Context Protocol with the SDK's own
/// client: a project worked through it (its tests, packages, actions, publish, Git and the terminal), a folder given it in
/// other letter case, and a start or a load that runs long.</summary>
public sealed class McpTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpTests(RestoredFolders folders)
        : base(folders)
    {
    }

    [Gate]
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

    /// <summary>A project whose global.json asks for a .NET SDK that is not installed: workspace says so, and run
    /// refuses in the same words rather than finding no executable project.</summary>
    [Fact]
    public async Task An_agent_where_global_json_asks_for_an_SDK_not_installed_hears_so_from_workspace_and_run()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-no-sdk-"));
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "global.json"), MissingSdkTests.Pinned);
        File.WriteAllText(Path.Combine(folder, "App.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"App\");\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        const string Missing = "The .NET SDK 1.0.100 that global.json asks for is not installed: install it, or have global.json name one that is";
        JsonElement workspace = await Call(client, McpTools.Workspace);
        Assert.True(workspace.TryGetProperty("sdkMissing", out JsonElement missing) && missing.GetString() == Missing,
            $"workspace, where global.json asks for an SDK not installed, answered: {workspace}");
        CallToolResult run = await Result(client, McpTools.Run);
        Assert.True(run.IsError == true && Text(run).Contains(Missing, StringComparison.Ordinal), $"run without the SDK answered: {Text(run)}");
    }

    /// <summary>A folder whose .slnx does not read: workspace names it with where its reader stopped and why, and no
    /// solution open.</summary>
    [Fact]
    public async Task An_agent_on_a_folder_whose_solution_does_not_read_hears_where_and_why_from_workspace()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-unread-solution-"));
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "App.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"App\");\n");
        File.WriteAllText(Path.Combine(folder, "Shop.slnx"), "<Solution>\n  <Project Path=\"App.csproj\" />\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        JsonElement workspace = await Call(client, McpTools.Workspace);
        Assert.True(workspace.TryGetProperty("unreadSolution", out JsonElement unread)
            && unread.GetString() == "Shop.slnx:3:1: Unexpected end of file has occurred. The following elements are not closed: Solution"
            && !workspace.TryGetProperty("solution", out _),
            $"workspace, where Shop.slnx is cut short, answered: {workspace}");
    }

    /// <summary>tests_run answers with the run it made alone: after a run of every test, one failing, a run of the passing
    /// one by its name tells no failure, while tests_list still says the other failed when it last ran.</summary>
    [Fact]
    public async Task An_agents_run_of_a_passing_test_after_a_failure_answers_no_failure_of_a_test_it_did_not_run()
    {
        string tests = PathNames.Canonical(TestFolders.Create("mcp-run-tests-"));
        DeleteAtEnd(tests);
        await TestFolders.PrepareAsync(async () =>
        {
            File.WriteAllText(Path.Combine(tests, "Sums.Tests.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n" +
                "    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.5\" />\n  </ItemGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(tests, "SumTests.cs"),
                "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class SumTests\n{\n    [Fact]\n    public void Two_and_two_make_four() => Assert.Equal(4, 2 + 2);\n\n" +
                "    [Fact]\n    public void Two_and_two_make_five() => Assert.Equal(5, 2 + 2);\n}\n");
            await Restore.RunAsync(tests);
        }, tests);
        await using McpClient client = await Start(["--mcp", tests], languageServer: false);
        JsonElement all = await Call(client, McpTools.TestsRun);
        Assert.StartsWith("Tests passed: 1, failed: 1 of 2", all.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal("Sums.Tests.SumTests.Two_and_two_make_five", Assert.Single(all.GetProperty("failures").EnumerateArray()).GetProperty("test").GetString());

        JsonElement four = await Call(client, McpTools.TestsRun, new() { ["filter"] = "make_four" });
        Assert.StartsWith("Tests passed: 1, failed: 0 of 1", four.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.True(four.GetProperty("failures").GetArrayLength() == 0, $"A run of one passing test answered failures of tests it did not run, as if they had failed in it: {four}");
        JsonElement failed = Assert.Single((await Call(client, McpTools.TestsList, new() { ["state"] = "failed" })).EnumerateArray());
        Assert.Equal("Sums.Tests.SumTests.Two_and_two_make_five", failed.GetProperty("test").GetString());
    }

    /// <summary>A failing test an agent writes into a test file the IDE has read, run at once by its class's name, runs with
    /// the class's others and is told among the failures: what a run runs is chosen once its build has ended.</summary>
    [Fact]
    public async Task An_agents_test_written_and_run_at_once_by_its_classs_name_runs_and_its_failure_is_told()
    {
        string tests = PathNames.Canonical(TestFolders.Create("mcp-written-tests-"));
        DeleteAtEnd(tests);
        const string Two = "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class SumTests\n{\n    [Fact]\n    public void Two_and_two_make_four() => Assert.Equal(4, 2 + 2);\n\n" +
            "    [Fact]\n    public void Three_and_three_make_six() => Assert.Equal(6, 3 + 3);\n";
        await TestFolders.PrepareAsync(async () =>
        {
            File.WriteAllText(Path.Combine(tests, "Sums.Tests.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n" +
                "    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.5\" />\n  </ItemGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(tests, "SumTests.cs"), Two + "}\n");
            await Restore.RunAsync(tests);
        }, tests);
        await using McpClient client = await Start(["--mcp", tests], languageServer: false);
        Assert.Equal(2, (await Call(client, McpTools.TestsList, new() { ["filter"] = "SumTests" })).GetArrayLength());

        File.WriteAllText(Path.Combine(tests, "SumTests.cs"), Two + "\n    [Fact]\n    public void Written_just_now() => Assert.Equal(5, 2 + 2);\n}\n");
        JsonElement run = await Call(client, McpTools.TestsRun, new() { ["filter"] = "Sums.Tests.SumTests" });
        Assert.True(run.GetProperty("summary").GetString()!.StartsWith("Tests passed: 2, failed: 1 of 3", StringComparison.Ordinal),
            $"A run by its class's name of a test written just before it left the test out: {run}");
        Assert.Equal("Sums.Tests.SumTests.Written_just_now", Assert.Single(run.GetProperty("failures").EnumerateArray()).GetProperty("test").GetString());
    }

    /// <summary>A VSTest test that reads its standard input reads its end at once, never the agent's requests, which are
    /// Passenger's standard input: its run passes, and the agent's next call is answered.</summary>
    [Fact]
    public async Task An_agents_test_that_reads_standard_input_reads_its_end_and_the_agents_next_call_is_answered()
    {
        string tests = PathNames.Canonical(TestFolders.Create("mcp-stdin-tests-"));
        DeleteAtEnd(tests);
        await TestFolders.PrepareAsync(async () =>
        {
            File.WriteAllText(Path.Combine(tests, "Input.Tests.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n" +
                "    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.5\" />\n  </ItemGroup>\n</Project>\n");
            // A read that waits is ended by the test's own limit, so a run that reads the agent's pipe fails rather than hangs.
            File.WriteAllText(Path.Combine(tests, "InputTests.cs"), """
                using System;
                using System.Threading.Tasks;
                using Xunit;

                namespace Input.Tests;

                public class InputTests
                {
                    [Fact]
                    public void Reads_no_input()
                    {
                        Task<string> read = Task.Run(() => Console.In.ReadLine());
                        Assert.True(read.Wait(TimeSpan.FromSeconds(20)), "Reading standard input waited for input that never came");
                        Assert.True(read.Result is null, $"Standard input gave: {read.Result}");
                    }
                }

                """);
            await Restore.RunAsync(tests);
        }, tests);
        await using McpClient client = await Start(["--mcp", tests], languageServer: false);
        JsonElement run = await Call(client, McpTools.TestsRun, new() { ["filter"] = "Reads_no_input" });
        Assert.True(run.GetProperty("summary").GetString()!.StartsWith("Tests passed: 1, failed: 0 of 1", StringComparison.Ordinal),
            $"A test reading its standard input did not read its end at once: {run}");
        JsonElement passed = Assert.Single((await Call(client, McpTools.TestsList, new() { ["filter"] = "Reads_no_input" })).EnumerateArray());
        Assert.Equal("passed", passed.GetProperty("state").GetString());
    }

    /// <summary>An agent's own Passenger opens its folder as it starts, while its window is still being built: the first C#
    /// answer comes from the language server started then, which loaded the projects as the project host evaluated and built
    /// them then, and the window, once built, takes all of it up. Of what its opening asked of the built window before (an
    /// evaluation, a design-time build, a language server's start and load), it asks none: the window's build is no
    /// longer the first answer's to wait out.</summary>
    [Gate]
    [Fact]
    public async Task An_agents_own_passenger_opens_its_folder_while_its_window_is_built_which_takes_it_up_and_does_none_of_it_again()
    {
        string folder = PathNames.Canonical(await TakeFolderAsync());
        await using McpClient client = await Start(["--mcp", folder]);
        string answered = (await Call(client, McpTools.SymbolInfo, new() { ["path"] = "Helper.cs", ["line"] = 3, ["column"] = 23 })).GetString()!;
        Assert.StartsWith("int Helper.Twice(int value)", answered, StringComparison.Ordinal);

        string logged = Path.Combine(OwnState, "logs", "passenger.log");
        string log = string.Empty;
        await Until(() => (log = Ui.Shared(logged)).Contains(") loaded ", StringComparison.Ordinal) && log.Contains("as MSBuild evaluates them", StringComparison.Ordinal),
            "the log to say the language server loaded the project and the Solution view showed it");
        int begun = log.IndexOf($"Opening {folder} as Passenger starts, ahead of its window", StringComparison.Ordinal);
        int built = log.IndexOf("for an agent, with no window shown: its window built", StringComparison.Ordinal);
        Assert.True(begun >= 0 && built > begun, $"The folder's opening was not begun before the window was built:\n{log}");
        string[] ahead = [.. Regex.Matches(log, $@"C# language server \(process (\d+)\) started ahead for {Regex.Escape(folder)} ")
            .Select(m => m.Groups[1].Value)];
        string[] loaded = [.. Regex.Matches(log, @"C# language server \(process (\d+)\) loaded ").Select(m => m.Groups[1].Value)];
        Assert.True(ahead.Length == 1 && loaded.SequenceEqual(ahead) && log.Contains($"C# language server (process {ahead[0]}) started ahead taken up for {folder}", StringComparison.Ordinal)
            && !log.Contains($"C# language server (process {ahead[0]}) started for", StringComparison.Ordinal),
            $"The answer came from a language server the window started once built, not the one started as Passenger did:\n{log}");
        Assert.False(Regex.IsMatch(log, @"evaluated \d+ of the \d+ projects of"), $"The window evaluated the folder's project again once built:\n{log}");
        Assert.False(log.Contains("at design time in", StringComparison.Ordinal), $"The window built the folder's project at design time again once built:\n{log}");
    }

    /// <summary>An agent's own Passenger, its window built over the folder's head start as <c>--mcp</c> builds it, whose
    /// settings as its C# starts are not the defaults the server started ahead has (solution-wide analysis turned on before
    /// its projects were found) takes that server up, telling it the settings, and starts no second one: Problems' Solution
    /// tab lists the warning of a file nobody opened.</summary>
    [AvaloniaFact]
    public void An_agents_own_passenger_whose_settings_are_not_the_defaults_takes_up_the_server_started_ahead_and_starts_no_second()
    {
        string folder = PathNames.Canonical(Folder);
        LaunchOptions launch = LaunchOptions.Parse([folder]);
        MainWindow window = new() { Width = 1200, Height = 800, AgentsAllowed = false, AgentsOwn = true, HeadStart = HeadStart.Begin(launch, languageServer: true) };
        window.Show();
        window.ApplySettings(window.Settings with { SolutionWideAnalysis = true });
        window.Start(launch);
        ProblemsModel problems = window.BottomPanel.Problems;
        Ui.WaitFor(() => problems.SolutionProblems?.Any(p => p.Code == "CS0219") == true,
            () => $"the warning of Helper.cs, a file nobody opened, listed by solution-wide analysis on as C# started (listed: {string.Join("; ", problems.SolutionProblems?.Select(p => p.Code) ?? [])})",
            TimeSpan.FromMinutes(2));
        problems.Shown = ProblemsTab.Solution;
        Assert.Contains("Helper.cs", problems.Rows.Where(r => r.Problem is null).Select(r => r.Label));

        string logged = Path.Combine(TestRun.Settings, "logs", "passenger.log");
        string log = string.Empty;
        Regex second = new($@"C# language server \(process \d+\) started for {Regex.Escape(folder)} ");
        Ui.WaitFor(() => (log = Ui.Shared(logged)).Contains($"started ahead taken up for {folder}", StringComparison.Ordinal) || second.IsMatch(log),
            "the log to say which language server the window took");
        string[] ahead = [.. Regex.Matches(log, $@"C# language server \(process (\d+)\) started ahead for {Regex.Escape(folder)} ").Select(m => m.Groups[1].Value)];
        Assert.True(ahead.Length == 1 && log.Contains($"C# language server (process {ahead[0]}) started ahead taken up for {folder}", StringComparison.Ordinal),
            $"The language server started ahead with the default settings was not taken up by a window whose settings differ:\n{Ours(log, folder)}");
        Assert.False(second.IsMatch(log),
            $"A second language server started beside the one started ahead, loading the same project again:\n{Ours(log, folder)}");
        window.Close();

        static string Ours(string log, string folder) => string.Join('\n', log.Split('\n').Where(line => line.Contains(folder, StringComparison.Ordinal)));
    }

    /// <summary>A load that runs past its limit (the agent's Passenger's own, 2 s) is said to be still loading, in the
    /// workspace's state and the status bar, and never given up: once the projects load, the language server is ready, the
    /// status bar says so, and solution-wide analysis lists the warning of a file nobody opened.</summary>
    [Fact]
    public async Task A_load_past_its_limit_is_said_to_be_still_loading_and_once_it_loads_the_solution_is_ready_and_analysed()
    {
        string folder = await TakeFolderAsync();
        HoldDesignTimeBuilds(folder);
        // Opened once Passenger runs: the folder it starts on has the server's start wait for the builds, not its load.
        string empty = PathNames.Canonical(TestFolders.Create("mcp-empty-"));
        DeleteAtEnd(empty);
        await using McpClient client = await Start(["--mcp", empty], environment: new() { [global::Passenger.App.CSharp.CSharpService.SlowLoadVariable] = "2000" });
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["solutionWideAnalysis"] = true } });
        await Call(client, McpTools.Open, new() { ["path"] = folder });

        const string still = "Still loading 1 project after 2 seconds; the language server's log says where it is";
        string state = string.Empty;
        Assert.True(await Waiting.UntilAsync(async () => (state = await LanguageServerState(client)) == still, TimeSpan.FromMinutes(2), Cancel, TimeSpan.FromMilliseconds(100)),
            $"The language server's state past the limit was not that it is still loading but \"{state}\".{Said()}");
        string status = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["filter"] = "#StatusText" }));
        Assert.True(status.Contains(still, StringComparison.Ordinal), $"The status bar did not warn that the load runs long: {status}");

        ReleaseDesignTimeBuilds(folder);
        Assert.True(await Waiting.UntilAsync(async () => (state = await LanguageServerState(client)) == "ready", TimeSpan.FromMinutes(2), Cancel, TimeSpan.FromMilliseconds(100)),
            $"The language server was not ready once its load ended past the limit: \"{state}\".{Said()}");
        status = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["filter"] = "#StatusText" }));
        Assert.True(status.Contains("C# ready: 1 project loaded after ", StringComparison.Ordinal), $"The status bar did not say the late load ended: {status}");
        string[] solution = [.. (await Call(client, McpTools.Diagnostics)).GetProperty("solution").EnumerateArray()
            .Select(p => $"{p.GetProperty("code").GetString()} {p.GetProperty("path").GetString()}:{p.GetProperty("line").GetInt32()}")];
        Assert.True(solution.Contains("CS0219 Helper.cs:5"), $"Solution-wide analysis did not start once the late load ended: {string.Join("; ", solution)}");
    }

    /// <summary>An agent's Passenger started on a folder whose design-time build is held, its head start waiting for every
    /// build before its language server loads anything, says past its limit (its own, 2 s) that C# is still starting and
    /// for what, in the workspace's state and the status bar; once the build ends C# is ready, the status bar says so, and
    /// solution-wide analysis turned on while it started lists the warning of a file nobody opened.</summary>
    [Fact]
    public async Task A_head_start_past_its_limit_is_said_to_be_still_starting_and_once_its_builds_end_the_solution_is_ready_and_analysed()
    {
        string folder = await TakeFolderAsync();
        HoldDesignTimeBuilds(folder);
        await using McpClient client = await Start(["--mcp", folder], environment: new() { [global::Passenger.App.CSharp.CSharpService.SlowLoadVariable] = "2000" });

        const string still = "Still starting C# for 1 project after 2 seconds, waiting for its design-time builds";
        string state = string.Empty;
        Assert.True(await Waiting.UntilAsync(async () => (state = await LanguageServerState(client)) == still, TimeSpan.FromMinutes(2), Cancel, TimeSpan.FromMilliseconds(100)),
            $"The language server's state past the limit was not that it is still starting but \"{state}\".{Said()}");
        string status = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["filter"] = "#StatusText" }));
        Assert.True(status.Contains(still, StringComparison.Ordinal), $"The status bar did not warn that the start runs long: {status}");
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["solutionWideAnalysis"] = true } });

        ReleaseDesignTimeBuilds(folder);
        Assert.True(await Waiting.UntilAsync(async () => (state = await LanguageServerState(client)) == "ready", TimeSpan.FromMinutes(2), Cancel, TimeSpan.FromMilliseconds(100)),
            $"The language server was not ready once the builds its start waited for ended: \"{state}\".{Said()}");
        status = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["filter"] = "#StatusText" }));
        Assert.True(status.Contains("C# ready: 1 project loaded after ", StringComparison.Ordinal), $"The status bar did not say the late start ended: {status}");
        string[] solution = [.. (await Call(client, McpTools.Diagnostics)).GetProperty("solution").EnumerateArray()
            .Select(p => $"{p.GetProperty("code").GetString()} {p.GetProperty("path").GetString()}:{p.GetProperty("line").GetInt32()}")];
        Assert.True(solution.Contains("CS0219 Helper.cs:5"), $"Solution-wide analysis turned on as C# started did not reach its server: {string.Join("; ", solution)}");
    }

    /// <summary>A load past its limit holds the IDE's background work only until its warning is said: a screenshot of the
    /// folder, which waits for that work, is taken once it is, the load still going on, where it waited out its own limit
    /// and was taken saying work was still pending.</summary>
    [Fact]
    public async Task A_load_past_its_limit_holds_no_background_work_once_its_warning_is_said()
    {
        string folder = await TakeFolderAsync();
        HoldDesignTimeBuilds(folder);
        string picture = Path.Combine(OwnState, "loading.png");
        Process passenger = StartPassenger([folder, "--screenshot", picture, "--size", "1000x700"],
            environment: new() { [global::Passenger.App.CSharp.CSharpService.SlowLoadVariable] = "2000" });
        try
        {
            Assert.True(await Waiting.Within(passenger.WaitForExitAsync(Cancel), TimeSpan.FromMinutes(3), Cancel), $"The screenshot of a folder whose load runs long never ended.{Said()}");
            string said = Ui.Shared(Path.Combine(OwnState, "logs", "passenger.log"));
            Assert.True(said.Contains("had not loaded 1 project after 2 seconds, and goes on loading it", StringComparison.Ordinal), $"The load was never said to run long.{Said()}");
            Assert.True(passenger.ExitCode == 0 && File.Exists(picture),
                $"The screenshot of a folder whose load was said to run long ended with exit code {passenger.ExitCode}, the load holding background work past its warning.{Said()}");
        }
        finally
        {
            ReleaseDesignTimeBuilds(folder);
        }
    }

    /// <summary>Holds the folder's design-time builds, which the language server loads the projects from, until
    /// <see cref="ReleaseDesignTimeBuilds"/>.</summary>
    private static void HoldDesignTimeBuilds(string folder) => File.WriteAllText(Path.Combine(folder, "Directory.Build.targets"), """
        <Project>
          <Target Name="HeldLoad" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
            <Exec Condition="'$(OS)' != 'Windows_NT'" Command="for i in `seq 3000`; do test -f released &amp;&amp; break; sleep 0.1; done" />
            <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command &quot;for (%24i = 0; %24i -lt 3000 -and -not (Test-Path released); %24i++) { Start-Sleep -Milliseconds 100 }&quot;" />
          </Target>
        </Project>
        """);

    private static void ReleaseDesignTimeBuilds(string folder) => File.WriteAllText(Path.Combine(folder, "released"), string.Empty);

    /// <summary>The language server's state as workspace gives it: ready, or what it is doing.</summary>
    private async Task<string> LanguageServerState(McpClient client) => (await Call(client, McpTools.Workspace)).GetProperty("languageServer").GetString()!;

    /// <summary>A file-based program (<c>dotnet run app.cs</c>) is a project to an agent: workspace lists it by its C# file,
    /// build builds it and run runs it, the code of the file it includes with it.</summary>
    [Gate]
    [Fact]
    public async Task An_agent_builds_and_runs_a_file_based_program_as_a_project_of_its_own()
    {
        string program = PathNames.Canonical(TestFolders.Create("mcp-program-"));
        DeleteAtEnd(program);
        // What it builds lies in its folder, which the test removes, rather than in the SDK's cache of the person's.
        File.WriteAllText(Path.Combine(program, "Directory.Build.props"), "<Project>\n  <PropertyGroup>\n    <UseArtifactsOutput>true</UseArtifactsOutput>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(program, "app.cs"), "#!/usr/bin/env dotnet\n#:property PublishAot=false\n#:include helper.cs\nConsole.WriteLine(Helper.Greet());\n");
        File.WriteAllText(Path.Combine(program, "helper.cs"), "static class Helper\n{\n    public static string Greet() => \"Hello from helper.cs\";\n}\n");
        await using McpClient client = await Start(["--mcp", program], languageServer: false);
        JsonElement workspace = await Call(client, McpTools.Workspace);
        JsonElement project = Assert.Single(workspace.GetProperty("projects").EnumerateArray());
        Assert.True(project.GetProperty("path").GetString() == "app.cs" && project.GetProperty("outputType").GetString() == "Exe",
            $"The folder's program is not a project of its own: {workspace}");
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The program did not build: {built}");
        JsonElement ran = await Call(client, McpTools.Run, new() { ["waitSeconds"] = 60 });
        Assert.True(ran.GetProperty("output").GetString()?.Contains("Hello from helper.cs\n", StringComparison.Ordinal) == true, $"The program's run did not print its greeting: {ran}");
    }

    /// <summary>A run refused for a reason of its own (here a launch profile's command named RetryFailed, which Passenger does
    /// not run) is answered with that reason alone, though the last build's errors are still in Problems; a run refused for
    /// the build it ran failing still names them.</summary>
    [Gate]
    [Fact]
    public async Task An_agents_run_refused_for_anything_but_its_own_failed_build_names_no_build_errors()
    {
        await TakeFolderAsync();
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

    /// <summary>In a project that restores with a lock file, the files nuget_install names are all it changed: the lock file
    /// its restore made, then rewrote for the next version, so an agent committing them leaves a locked restore whole.</summary>
    [Fact]
    public async Task An_agents_package_install_in_a_project_restoring_with_a_lock_file_names_the_lock_file_among_the_files_it_changed()
    {
        string library = PathNames.Canonical(TestFolders.Create("mcp-nuget-lock-"));
        DeleteAtEnd(library);
        string feed = Directory.CreateDirectory(Path.Combine(library, "feed")).FullName;
        File.WriteAllBytes(Path.Combine(feed, "Passenger.Sample.1.0.0.nupkg"), StandInFeed.Package("Passenger.Sample", "1.0.0"));
        File.WriteAllBytes(Path.Combine(feed, "Passenger.Sample.1.1.0.nupkg"), StandInFeed.Package("Passenger.Sample", "1.1.0"));
        File.WriteAllText(Path.Combine(library, "nuget.config"),
            "<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"local\" value=\"feed\" />\n  </packageSources>\n"
            + "  <config>\n    <add key=\"globalPackagesFolder\" value=\"packages\" />\n  </config>\n</configuration>\n");
        File.WriteAllText(Path.Combine(library, "Lib.csproj"), TestProjects.Library("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>"));
        string lockFile = Path.Combine(library, "packages.lock.json");
        await using McpClient client = await Start(["--mcp", library], languageServer: false);

        JsonElement made = await Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample", ["version"] = "1.0.0" });
        string[] changed = [.. made.GetProperty("changed").EnumerateArray().Select(f => f.GetString()!).Order(StringComparer.Ordinal)];
        Assert.True(changed.SequenceEqual(["Lib.csproj", "packages.lock.json"]) && File.Exists(lockFile),
            $"nuget_install answered that it changed [{string.Join(", ", changed)}], where its restore made packages.lock.json too ({(File.Exists(lockFile) ? "there" : "not there")})");

        JsonElement moved = await Call(client, McpTools.NugetInstall, new() { ["project"] = "Lib", ["id"] = "Passenger.Sample", ["version"] = "1.1.0" });
        changed = [.. moved.GetProperty("changed").EnumerateArray().Select(f => f.GetString()!).Order(StringComparer.Ordinal)];
        string locked = File.ReadAllText(lockFile);
        Assert.True(changed.SequenceEqual(["Lib.csproj", "packages.lock.json"]) && locked.Contains("\"resolved\": \"1.1.0\"", StringComparison.Ordinal),
            $"nuget_install's update answered that it changed [{string.Join(", ", changed)}], the lock file now holding:\n{locked}");
    }

    /// <summary>Git's tools on a repository, a merge stopped for a conflict among them: the agent finds it in vcs_status (what
    /// is under way, each side's branch, what each did to the file), resolves it with theirs, and its commit finishes the
    /// merge; and a command in the IDE's terminal.</summary>
    [Gate]
    [Fact]
    public async Task An_agent_reads_and_commits_changes_resolves_a_merges_conflict_and_runs_a_command_in_the_ides_terminal()
    {
        string repository = TestFolders.Create("mcp-git-");
        try
        {
            File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\ntwo\nthree\n");
            await TestGit.InitAsync(repository);
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
            await TestGit.RunAsync(repository, "switch", "-q", "-c", "feature");
            File.WriteAllText(notes, "one\nTWO\nthree\n");
            await TestGit.RunAsync(repository, "commit", "-q", "-am", "Two in capitals");
            await TestGit.RunAsync(repository, "switch", "-q", "main");
            File.WriteAllText(notes, "one\nII\nthree\n");
            await TestGit.RunAsync(repository, "commit", "-q", "-am", "Two in Roman");
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
            Assert.Equal(3, (await TestGit.RunAsync(repository, "rev-list", "--parents", "-n", "1", "HEAD")).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal("one\nTWO\nthree\n", await TestGit.RunAsync(repository, "show", "HEAD:Notes.txt"));

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

    /// <summary>An agent's amend with no paths changes the last commit's message alone: a rename and a change staged in the
    /// index (as the IDE's own rename and another program's git add leave them) go into no commit and stay changes.</summary>
    [Fact]
    public async Task An_agents_amend_with_no_paths_rewords_the_last_commit_and_leaves_what_the_index_holds_out_of_it()
    {
        string repository = TestFolders.Create("mcp-amend-");
        DeleteAtEnd(repository);
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\n");
        File.WriteAllText(Path.Combine(repository, "Shop.txt"), "shop\n");
        await TestGit.InitAsync(repository);
        await TestGit.RunAsync(repository, "mv", "Shop.txt", "Store.txt");
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\ntwo\n");
        await TestGit.RunAsync(repository, "add", "Notes.txt");

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        Dictionary<string, object?> amend = new() { ["message"] = "First, reworded", ["paths"] = Array.Empty<string>(), ["amend"] = true };
        Assert.EndsWith(" First, reworded", (await Call(client, McpTools.VcsCommit, amend)).GetString(), StringComparison.Ordinal);
        Assert.Equal("1", (await TestGit.RunAsync(repository, "rev-list", "--count", "HEAD")).Trim());
        string held = (await TestGit.RunAsync(repository, "show", "--name-status", "--format=", "HEAD")).Trim().Replace("\r", string.Empty, StringComparison.Ordinal);
        Assert.True(held == "A\tNotes.txt\nA\tShop.txt", $"vcs_commit's amend with no paths took what the index holds into the last commit, which now holds [{held}]");
        Assert.Equal("one\n", await TestGit.RunAsync(repository, "show", "HEAD:Notes.txt"));
        Assert.Equal(["Notes.txt modified", "Store.txt renamed"], (await Call(client, McpTools.VcsStatus)).GetProperty("changes").EnumerateArray()
            .Select(c => $"{c.GetProperty("path").GetString()} {c.GetProperty("kind").GetString()}").Order(StringComparer.Ordinal));
    }

    /// <summary>vcs_status with the path of a binary file changed since the last commit answers as git's diff does, that the
    /// binary files differ, never its old bytes as lines deleted; the file put back as committed has no changes.</summary>
    [Fact]
    public async Task An_agents_diff_of_a_changed_binary_file_says_the_binary_files_differ()
    {
        string repository = TestFolders.Create("mcp-binary-");
        DeleteAtEnd(repository);
        byte[] image = [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 1, 2, 3];
        string path = Path.Combine(repository, "image.png");
        File.WriteAllBytes(path, image);
        await TestGit.InitAsync(repository);
        File.WriteAllBytes(path, [.. image, 4, 5]);

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        Dictionary<string, object?> asked = new() { ["path"] = "image.png" };
        string diff = (await Call(client, McpTools.VcsStatus, asked)).GetString()!;
        Assert.True(diff == "Binary files a/image.png and b/image.png differ", $"vcs_status of a changed binary file answered:\n{diff}");
        File.WriteAllBytes(path, image);
        Assert.Equal("No changes", (await Call(client, McpTools.VcsStatus, asked)).GetString());
    }

    /// <summary>vcs_status with the path of a file whose only change is its line separators says which changed on how many
    /// lines, and of one whose only change is its mode (where git keeps modes) says the mode as git's diff does, where each
    /// answered no changes.</summary>
    [Fact]
    public async Task An_agents_diff_of_a_file_changed_only_in_its_line_separators_or_its_mode_says_what_changed()
    {
        string repository = TestFolders.Create("mcp-separators-");
        DeleteAtEnd(repository);
        File.WriteAllText(Path.Combine(repository, "lines.txt"), "one\ntwo\nthree\n");
        File.WriteAllText(Path.Combine(repository, "run.sh"), "echo run\n");
        await TestGit.InitAsync(repository);
        File.WriteAllText(Path.Combine(repository, "lines.txt"), "one\r\ntwo\r\nthree\r\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(repository, "run.sh"), File.GetUnixFileMode(Path.Combine(repository, "run.sh")) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        Assert.Equal("Line separators changed: 3 lines from LF to CRLF", (await Call(client, McpTools.VcsStatus, new() { ["path"] = "lines.txt" })).GetString());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("old mode 100644\nnew mode 100755", (await Call(client, McpTools.VcsStatus, new() { ["path"] = "run.sh" })).GetString());
        }
    }

    /// <summary>An agent's edit the IDE could not save (its file read-only) is never committed as the disk's older text:
    /// vcs_commit is refused naming the file, and nothing is committed.</summary>
    [Fact]
    public async Task An_agents_commit_with_an_edit_whose_save_was_refused_is_refused_naming_the_file()
    {
        string repository = TestFolders.Create("mcp-unsaved-");
        DeleteAtEnd(repository);
        string notes = Path.Combine(repository, "Notes.txt");
        File.WriteAllText(notes, "one\ntwo\n");
        await TestGit.InitAsync(repository);
        File.WriteAllText(notes, "one\n2\n");
        File.SetAttributes(notes, FileAttributes.ReadOnly);

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        string edited = Text(await Call(client, McpTools.Edit, new() { ["path"] = "Notes.txt", ["oldText"] = "2\n", ["newText"] = "two, edited\n" }));
        Assert.Contains("not saved: Notes.txt is read-only", edited, StringComparison.Ordinal);
        CallToolResult refused = await Result(client, McpTools.VcsCommit, new() { ["message"] = "The disk's text" });
        Assert.True(refused.IsError == true && Text(refused).Contains("Notes.txt could not be saved: Notes.txt is read-only", StringComparison.Ordinal),
            $"vcs_commit with Notes.txt's edit unsaved answered: {Text(refused)}");
        Assert.Equal("First", (await TestGit.RunAsync(repository, "log", "-1", "--format=%s")).Trim());
        File.SetAttributes(notes, FileAttributes.Normal);
    }

    /// <summary>vcs_status with the path of a file git mv renamed, one line of it changed since, answers that one line, as
    /// the file was where the last commit has it.</summary>
    [Fact]
    public async Task An_agents_diff_of_a_file_git_mv_renamed_is_its_one_changed_line_from_where_the_last_commit_has_it()
    {
        string repository = TestFolders.Create("mcp-renamed-");
        DeleteAtEnd(repository);
        File.WriteAllText(Path.Combine(repository, "Foo.txt"), "line 1\nline 2\nline 3\nline 4\n");
        await TestGit.InitAsync(repository);

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        await TestGit.RunAsync(repository, "mv", "Foo.txt", "Bar.txt");
        File.WriteAllText(Path.Combine(repository, "Bar.txt"), "line 1\nline 2 changed\nline 3\nline 4\n");
        string diff = (await Call(client, McpTools.VcsStatus, new() { ["path"] = "Bar.txt" })).GetString()!.Replace("\r", string.Empty, StringComparison.Ordinal);
        Assert.True(diff.Contains("-line 2\n+line 2 changed\n", StringComparison.Ordinal) && !diff.Contains("+line 1", StringComparison.Ordinal),
            $"vcs_status of Bar.txt, git mv'd from Foo.txt with one line changed, answered:\n{diff}");
    }

    /// <summary>A repository git refuses as another user's (as on a FAT or network drive) is said to be refused, in git's
    /// words naming safe.directory, by vcs_status and the window's status bar, never as no repository; Passenger sets no
    /// safe.directory itself, and once the person's configuration names it the next read takes the repository.</summary>
    [Fact]
    public async Task A_repository_git_refuses_as_another_users_is_said_to_be_refused_in_gits_words_naming_safe_directory()
    {
        string repository = TestFolders.Create("mcp-dubious-");
        string home = TestFolders.Create("mcp-dubious-config-");
        DeleteAtEnd(repository);
        DeleteAtEnd(home);
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\n");
        await TestGit.InitAsync(repository);
        string global = Path.Combine(home, "gitconfig");
        File.WriteAllText(global, string.Empty);

        await using McpClient client = await Start(["--mcp", repository], languageServer: false,
            environment: new() { ["GIT_TEST_ASSUME_DIFFERENT_OWNER"] = "1", ["GIT_CONFIG_GLOBAL"] = global });
        foreach (Dictionary<string, object?>? arguments in new Dictionary<string, object?>?[] { null, new() { ["path"] = "Notes.txt" } })
        {
            CallToolResult refused = await Result(client, McpTools.VcsStatus, arguments);
            string said = Text(refused);
            Assert.True(refused.IsError == true && said.Contains("detected dubious ownership", StringComparison.Ordinal) && said.Contains("safe.directory", StringComparison.Ordinal),
                $"vcs_status {JsonSerializer.Serialize(arguments)} over a repository git refuses as another user's answered, rather than git's reason: {said}");
        }
        string bar = string.Empty;
        await Waiting.UntilAsync(async () => (bar = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["control"] = "#StatusText" })))
            .Contains("Git refuses this folder's repository: detected dubious ownership", StringComparison.Ordinal), TimeSpan.FromSeconds(10), Cancel, TimeSpan.FromMilliseconds(200));
        Assert.True(bar.Contains("Git refuses this folder's repository: detected dubious ownership", StringComparison.Ordinal),
            $"The window's status bar, over a repository git refuses, does not say why: {bar}");
        Assert.True(File.ReadAllText(global).Length == 0, $"Passenger wrote to the person's Git configuration: {File.ReadAllText(global)}");

        // As the person would, having read git's words.
        File.WriteAllText(global, "[safe]\n\tdirectory = *\n");
        Assert.Equal("main", (await Call(client, McpTools.VcsStatus)).GetProperty("branch").GetString());
    }

    /// <summary>With no git on the PATH, a folder's repository is never taken for none: the Version control menu greys "Git is
    /// not installed" where Create Git Repository… would be, the status bar saying why and how to install it, and
    /// vcs_status answers that git is not installed.</summary>
    [Fact]
    public async Task With_no_git_on_the_path_a_repository_is_said_to_need_git_in_the_version_control_menu_never_taken_for_none()
    {
        string repository = TestFolders.Create("mcp-no-git-");
        string bare = TestFolders.Create("mcp-no-git-path-");
        DeleteAtEnd(repository);
        DeleteAtEnd(bare);
        File.WriteAllText(Path.Combine(repository, "Notes.txt"), "one\n");
        await TestGit.InitAsync(repository);

        // macOS's /bin holds no git, as an empty folder holds none elsewhere; a terminal's TERM, so no login shell's PATH is read.
        await using McpClient client = await Start(["--mcp", repository], languageServer: false,
            environment: new() { ["PATH"] = OperatingSystem.IsMacOS() ? "/bin" : bare, ["TERM"] = "dumb" });
        CallToolResult status = await Result(client, McpTools.VcsStatus);
        Assert.True(status.IsError == true && Text(status).Contains("git is not installed", StringComparison.Ordinal),
            $"vcs_status over a repository with no git on the PATH answered, rather than that git is not installed: {Text(status)}");

        await Call(client, McpTools.AppClick, new() { ["app"] = "ide", ["control"] = "#BranchButton" });
        Dictionary<string, object?> items = new() { ["app"] = "ide", ["filter"] = "MenuItem" };
        string menu = string.Empty;
        await Waiting.UntilAsync(async () => (menu = Text(await Call(client, McpTools.AppTree, items))).StartsWith("MenuItem", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10), Cancel, TimeSpan.FromMilliseconds(200));
        Assert.True(menu.Contains("MenuItem \"Git is not installed\"", StringComparison.Ordinal) && menu.Contains("[disabled]", StringComparison.Ordinal)
            && !menu.Contains("Create Git Repository", StringComparison.Ordinal),
            $"The Version control menu over a repository with no git on the PATH holds, rather than \"Git is not installed\" greyed: {menu}");
        string bar = Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["control"] = "#StatusText" }));
        Assert.True(bar.Contains("git is not installed", StringComparison.Ordinal),
            $"The status bar, once the Version control menu greyed Git with no git on the PATH, does not say why: {bar}");
    }

    /// <summary>vcs_stash lists each stash with its commit, and an agent's drop by that commit takes that stash wherever it
    /// now is, though another program stashed after the list was read; asked again, it is refused as gone.</summary>
    [Fact]
    public async Task An_agents_drop_of_a_stash_by_its_commit_takes_that_stash_though_another_program_stashed_since_the_list()
    {
        string repository = TestFolders.Create("mcp-stash-");
        DeleteAtEnd(repository);
        string notes = Path.Combine(repository, "Notes.txt");
        File.WriteAllText(notes, "one\n");
        await TestGit.InitAsync(repository);
        File.AppendAllText(notes, "older\n");
        await TestGit.RunAsync(repository, "stash", "push", "-m", "Older");

        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        JsonElement older = Assert.Single((await Call(client, McpTools.VcsStash)).EnumerateArray());
        string commit = older.GetProperty("commit").GetString()!;
        Assert.StartsWith(commit, (await TestGit.RunAsync(repository, "rev-parse", "stash@{0}")).Trim(), StringComparison.Ordinal);
        File.AppendAllText(notes, "newer\n");
        await TestGit.RunAsync(repository, "stash", "push", "-m", "Newer");

        Dictionary<string, object?> drop = new() { ["action"] = "drop", ["commit"] = commit };
        string[] left = [.. (await Call(client, McpTools.VcsStash, drop)).EnumerateArray().Select(s => s.GetProperty("message").GetString()!)];
        Assert.True(left is ["On main: Newer"], $"vcs_stash's drop of Older by its commit, after another program stashed Newer, left [{string.Join(", ", left)}]");
        CallToolResult again = await Result(client, McpTools.VcsStash, drop);
        Assert.True(again.IsError == true && Text(again).Contains("No stash in the list has commit", StringComparison.Ordinal), $"A drop of a stash gone from the list answered: {Text(again)}");
        Assert.Equal("On main: Newer", (await TestGit.RunAsync(repository, "stash", "list", "--format=%gs")).Trim());
    }

    /// <summary>Commit and Push to a remote that refuses every push: the agent hears the refusal, in git's words, as the
    /// tool's error, and the commit stays made.</summary>
    [Gate]
    [Fact]
    public async Task An_agents_push_the_remote_refuses_is_an_error_naming_the_refusal_and_the_commit_stays_made()
    {
        string root = TestFolders.Create("mcp-push-");
        try
        {
            string work = await RepositoryAsync(root, refusing: true);
            await using McpClient client = await Start(["--mcp", work]);
            CallToolResult refused = await Result(client, McpTools.VcsCommit, new() { ["message"] = "Two as a number", ["push"] = true });
            string said = Text(refused);
            Assert.True(refused.IsError == true, $"A push the remote refused was answered as done: {said}");
            Assert.Contains("pre-receive hook declined", said, StringComparison.Ordinal);
            Assert.Contains("pushes are closed here", said, StringComparison.Ordinal);
            Assert.Equal("Two as a number", (await TestGit.RunAsync(work, "log", "-1", "--format=%s")).Trim());
            Assert.True((await TestGit.RunAsync(Path.Combine(root, "remote.git"), "for-each-ref", "refs/heads")).Trim().Length == 0, "The remote took the push it refused");
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
            string used = await ProfileGitAsync(home, "sleep 300 &\n");
            await TestGit.RunAsync(repository, "init", "-q");
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

    /// <summary>Started as the Finder starts it, over a login shell that fails with no terminal (as an rc's <c>exec tmux</c>
    /// does), Passenger's log says the shell's environment was not read, naming the shell, in its words; a shell that skips
    /// that step where its environment is being read, as profiles do for the variables that say so, gives Passenger its
    /// PATH, those variables kept from the programs Passenger starts.</summary>
    [Fact]
    public async Task A_login_shell_that_fails_without_a_terminal_is_logged_in_its_words_and_one_that_skips_it_while_read_gives_its_path()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A Windows program gets the person's whole environment, PATH included");
            return;
        }
        string home = TestFolders.Create("home-");
        string repository = TestFolders.Create("mcp-shell-fails-");
        DeleteAtEnd(home);
        DeleteAtEnd(repository);
        await TestGit.RunAsync(repository, "init", "-q");
        string fails = Path.Combine(home, "fails-without-terminal.sh");
        Executable(fails, "#!/bin/sh\necho 'open terminal failed: not a terminal' >&2\nexit 1\n");
        Dictionary<string, string?> environment = Launchd(home);
        environment["SHELL"] = fails;
        string log = Path.Combine(OwnState, "logs", "passenger.log");
        await using (McpClient client = await Start(["--mcp", repository], languageServer: false, environment: environment))
        {
            await Call(client, McpTools.VcsStatus);
            string said = string.Empty;
            await Waiting.UntilAsync(() => Task.FromResult((said = File.Exists(log) ? Ui.Shared(log) : string.Empty)
                .Contains("open terminal failed: not a terminal", StringComparison.Ordinal)), TimeSpan.FromSeconds(10), Cancel, TimeSpan.FromMilliseconds(200));
            string? line = said.Split('\n').FirstOrDefault(l => l.Contains("The login shell's environment was not read", StringComparison.Ordinal));
            Assert.True(line?.Contains($"{fails} -l -i: it exited with 1: open terminal failed: not a terminal", StringComparison.Ordinal) == true,
                $"Over a login shell that fails with no terminal, Passenger's log does not say so, naming it, in its words: {line ?? said[Math.Max(0, said.Length - 2000)..]}");
        }

        // As oh-my-zsh's tmux autostart, failing with no terminal unless read; past it, no system profile moves its tools down.
        string tools = Path.Combine(home, "tools");
        Directory.CreateDirectory(tools);
        string seen = Path.Combine(home, "git-environment");
        Executable(Path.Combine(tools, "git"), $"#!/bin/sh\nenv > \"{seen}\"\nexec {(await global::Passenger.Core.Programs.GitAsync())!} \"$@\"\n");
        string skips = Path.Combine(home, "skips-while-read.sh");
        Executable(skips, "#!/bin/sh\nif [ \"$VSCODE_RESOLVING_ENVIRONMENT\" != 1 ] || [ \"$INTELLIJ_ENVIRONMENT_READER\" != 1 ]; then\n"
            + $"  echo 'open terminal failed: not a terminal' >&2\n  exit 1\nfi\nPATH=\"{tools}:$PATH\"; export PATH\nshift 3\nexec /bin/sh -c \"$1\"\n");
        environment["SHELL"] = skips;
        await using (McpClient client = await Start(["--mcp", repository], languageServer: false, environment: environment))
        {
            await Call(client, McpTools.VcsStatus);
        }
        Assert.True(File.Exists(seen), "Over a login shell that skips what needs a terminal while its environment is read, Passenger never took its PATH");
        string[] gitHad = File.ReadAllLines(seen);
        Assert.True(!gitHad.Any(v => v.StartsWith("VSCODE_RESOLVING_ENVIRONMENT=", StringComparison.Ordinal) || v.StartsWith("INTELLIJ_ENVIRONMENT_READER=", StringComparison.Ordinal)),
            $"The variables set while the login shell's environment was read reached the git Passenger started: {string.Join(", ", gitHad.Where(v => v.Contains("ENVIRONMENT", StringComparison.Ordinal)))}");
    }

    /// <summary>A script at <paramref name="path"/> that only its owner may change, as a shell or a tool on the PATH is.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Executable(string path, string script)
    {
        File.WriteAllText(path, script);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>A call whose arguments its tool does not take is answered with what is wrong and every parameter the tool
    /// takes, before the tool runs.</summary>
    [Gate]
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
    [Gate]
    [Fact]
    public async Task An_agent_lists_every_action_the_ide_declares_and_one_it_runs_says_why_when_it_cannot()
    {
        await TakeFolderAsync();
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
        string restart = Assert.Single(listed, l => l.StartsWith($"{AppActions.RestartCSharpServer}: Restart C# Language Server [IDE]", StringComparison.Ordinal));
        Assert.Contains($" - cannot run: {Refusals.NoCSharp}", restart, StringComparison.Ordinal);

        Assert.Equal($"Ran {AppActions.ShowLiveTree}", (await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ShowLiveTree })).GetString());
    }

    /// <summary>debug_state of processes says each program's command line as Attach to Process shows it, its secrets masked:
    /// a program given a password and an API key on its command line is listed by its name with neither.</summary>
    [Fact]
    public async Task An_agents_list_of_running_programs_masks_the_secrets_their_command_lines_hold()
    {
        const string Password = "hunter2-on-the-command-line";
        const string ApiKey = "sk-live-123456";
        string folder = PathNames.Canonical(TestFolders.Create("mcp-processes-"));
        DeleteAtEnd(folder);
        ProcessStartInfo start = ChildProcesses.Command(Debugging.Helpers.Debuggees.Apphost("LoopingConsoleApp"), ["sleep", $"--Password={Password}", $"--ApiKey={ApiKey}"]);
        start.RedirectStandardOutput = true;
        using Process looping = ChildProcesses.Start(start);
        try
        {
            Assert.Equal("looping", looping.StandardOutput.ReadLine());
            await using McpClient client = await Start(["--mcp", folder], languageServer: false);
            JsonElement program = Assert.Single((await Call(client, McpTools.DebugState, new() { ["of"] = "processes" })).EnumerateArray(),
                p => p.GetProperty("processId").GetInt32() == looping.Id);
            Assert.Equal("LoopingConsoleApp", program.GetProperty("program").GetString());
            string line = program.GetProperty("commandLine").GetString()!;
            Assert.True(line.EndsWith($" sleep --Password={ContainerSecrets.Masked} --ApiKey={ContainerSecrets.Masked}", StringComparison.Ordinal)
                && !line.Contains(Password, StringComparison.Ordinal) && !line.Contains(ApiKey, StringComparison.Ordinal),
                $"debug_state gave an agent the program's secrets: {line}");
        }
        finally
        {
            Processes.Kill(looping.Id);
        }
    }

    /// <summary>An agent's Passenger started on a solution filter beside a file-based program, then given the solution and the
    /// filter again: while the filter is open, workspace names it as the solution and marks unloaded the project it leaves
    /// out, the program loaded with its run configuration; with the solution, nothing is marked.</summary>
    [Fact]
    public async Task An_agent_on_a_solution_filter_is_told_the_filter_and_the_project_it_leaves_out_and_keeps_the_program_beside_it()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-filter-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "App/App.csproj", TestProjects.Console());
        TestFolders.Write(folder, "App/Program.cs", "System.Console.WriteLine(\"app\");\n");
        TestFolders.Write(folder, "Lib/Lib.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "App.slnf", "{\n  \"solution\": {\n    \"path\": \"Shop.slnx\",\n    \"projects\": [ \"App\\\\App.csproj\" ]\n  }\n}\n");
        TestFolders.Write(folder, "scripts/tool.cs", "#!/usr/bin/env dotnet\nSystem.Console.WriteLine(\"tool\");\n");
        string filter = Path.Combine(folder, "App.slnf");
        await using McpClient client = await Start(["--mcp", filter], languageServer: false);
        Told(await Call(client, McpTools.Open, new() { ["path"] = "App.slnf" }), filter, "Passenger started on the filter");
        Told(await Call(client, McpTools.Open, new() { ["path"] = "Shop.slnx" }), Path.Combine(folder, "Shop.slnx"), "the solution opened");
        Told(await Call(client, McpTools.Open, new() { ["path"] = "App.slnf" }), filter, "the filter opened over the solution");

        static void Told(JsonElement workspace, string solution, string after)
        {
            string[] projects = [.. workspace.GetProperty("projects").EnumerateArray()
                .Select(p => p.GetProperty("name").GetString() + (p.TryGetProperty("unloaded", out JsonElement marked) && marked.GetBoolean() ? " unloaded" : string.Empty))];
            string[] configurations = [.. workspace.GetProperty("runConfigurations").EnumerateArray().Select(c => c.GetString()!)];
            Assert.True(workspace.GetProperty("solution").GetString() == solution
                && projects.SequenceEqual(["App", solution.EndsWith(".slnf", StringComparison.Ordinal) ? "Lib unloaded" : "Lib", "tool"]) && configurations.Contains("tool"),
                $"After {after}, the workspace answered: {workspace}, where an agent builds and tests what it names and marks loaded");
        }
    }

    /// <summary>An agent's Passenger started on its folder in lower case, from a working folder and a PWD spelling it so (an
    /// agent started in <c>~/documents/app</c>): its workspace answer and the restore and build it makes name the folder as
    /// the disk does, so no build through the disk's spelling restores, compiles or deletes what this one wrote.</summary>
    [Fact]
    public async Task An_agents_passenger_given_its_folder_in_other_letter_case_answers_restores_and_builds_it_as_the_disk_names_it()
    {
        Assert.SkipWhen(PathNames.Comparison == StringComparison.Ordinal, PathsTests.CaseTold);
        string root = PathNames.Canonical(TestFolders.Create("mcp-case-"));
        DeleteAtEnd(root);
        string folder = Path.Combine(root, "Shop Front");
        string project = DiskSpelled.Write(folder);
        string spelled = folder.ToLowerInvariant();
        await using McpClient client = await Start(["--mcp", spelled], spelled, new() { ["PWD"] = spelled }, languageServer: false);
        JsonElement workspace = await Call(client, McpTools.Workspace);
        Assert.True(workspace.GetProperty("folder").GetString() == folder && workspace.GetProperty("solution").GetString() == Path.Combine(folder, "Shop.slnx"),
            $"Started on {spelled}, Passenger answered the workspace: {workspace}");
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The build ended: {built}");
        DiskSpelled.Check(spelled, folder, project);
    }

    /// <summary>An agent's publish, build's with a project to publish: the project to a folder, answered with where it landed
    /// or why it did not.</summary>
    [Gate]
    [Fact]
    public async Task An_agent_publishes_a_project_and_is_told_the_folder_it_landed_in_or_why_not()
    {
        await TakeFolderAsync();
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
    private static async Task<string> ProfileGitAsync(string home, string more = "")
    {
        string tools = Path.Combine(home, "tools");
        Directory.CreateDirectory(tools);
        string git = Path.Combine(tools, "git");
        string used = Path.Combine(home, "used");
        string real = (await global::Passenger.Core.Programs.GitAsync())!;
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
