using System.Text.Json;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Mcp;
using Passenger.App.Testing;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Godot 4 C# game, Spinner, whose main scene's script runs GoDotTest's tests when Godot is given --run-tests,
/// and two tests of its own, one failing: GoDotTest from the package cache tests/GodotGraphs fills, Godot's packages from
/// the Godot found, never nuget.org.</summary>
public static class GoDotTestGame
{
    public const string Tests = """
        namespace Spinner.Tests;

        using System;
        using Chickensoft.GoDotTest;
        using Godot;

        public class SpinnerTest : TestClass
        {
            public SpinnerTest(Node testScene) : base(testScene) { }

            [Test]
            public void Adds()
            {
                if (1 + 1 != 2)
                {
                    throw new InvalidOperationException("one and one are not two");
                }
            }

            [Test]
            public void Spins()
            {
                int turns = 2;
                GD.Print($"spinning {turns}");
                if (turns != 3)
                {
                    throw new InvalidOperationException("two turns are not three");
                }
            }
        }

        """;

    private const string Main = """
        using System.Reflection;
        using Chickensoft.GoDotTest;
        using Godot;

        public partial class Main : Node
        {
            public override void _Ready()
            {
                if (TestEnvironment.From(OS.GetCmdlineArgs()).ShouldRunTests)
                {
                    CallDeferred(nameof(RunTests));
                }
            }

            private void RunTests() => _ = GoTest.RunTests(Assembly.GetExecutingAssembly(), this);
        }

        """;

    private const string Scene = "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://Main.cs\" id=\"1\"]\n\n"
        + "[node name=\"Main\" type=\"Node\"]\nscript = ExtResource(\"1\")\n";

    /// <summary>The tests' file in a game's folder.</summary>
    public static string TestsPath(string folder) => Path.Combine(folder, "test", "SpinnerTest.cs");

    /// <summary>The editor's line (from zero) of the tests' first line holding <paramref name="code"/>.</summary>
    public static int Line(string code) => Tests.Split('\n').ToList().FindIndex(l => l.Contains(code, StringComparison.Ordinal));

    /// <summary>Writes the game into a folder, unrestored.</summary>
    public static void Make(string folder, GodotFound godot)
    {
        GodotTests.Game.Make(folder, godot.Version!, godot.Packages!, Main, Scene);
        File.WriteAllText(Path.Combine(folder, "Spinner.csproj"), $"""
            <Project Sdk="Godot.NET.Sdk/{godot.Version}">
              <PropertyGroup>
                <TargetFramework>{TestProjects.Framework}</TargetFramework>
                <EnableDynamicLoading>true</EnableDynamicLoading>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Chickensoft.GoDotTest" Version="{AvaloniaApp.Metadata("GoDotTestVersion")}" />
              </ItemGroup>
            </Project>

            """);
        Directory.CreateDirectory(Path.GetDirectoryName(TestsPath(folder))!);
        File.WriteAllText(TestsPath(folder), Tests);
    }

    /// <summary>Why the tests skip with the Godot found, or null where it runs a C# game.</summary>
    public static string? Skipped(GodotFound? godot) => godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build to run these tests"
        : godot is not { WhyNot: null, Version: not null, Packages: not null } ? $"The Godot found cannot run C#: {godot.WhyNot}" : null;
}

/// <summary>A Godot 4 C# game's GoDotTest tests in the Unit Tests window, as a person who added GoDotTest to the game finds
/// them: shown from the code before any build, run inside the game on the Godot found (headless, its user folders the
/// run's), a failure shown with its exception at its line and what the test printed, the gutter marking each test by how
/// it ran, and debugged under .NET's debugger, a breakpoint in a test stopping there. Without a Godot 4 .NET build each
/// test skips saying why. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GoDotTestTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("godottest-"));

    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(folder + "-home");
    }

    /// <summary>Both tests are in the tree before any build; Run All Tests passes one and fails the other with its exception
    /// at its line, linked, and what it printed; the gutter marks each; Debug of the failing one stops at a breakpoint inside
    /// it in Godot's process, and going on lets the run end with its failure.</summary>
    [AvaloniaFact]
    public void A_GoDotTest_test_is_found_from_code_runs_in_the_game_fails_at_its_line_and_stops_at_a_breakpoint_under_Debug()
    {
        Assert.SkipWhen(GoDotTestGame.Skipped(godot) is not null, GoDotTestGame.Skipped(godot) ?? string.Empty);
        GoDotTestGame.Make(folder, godot!);
        Restore.Run(Path.Combine(folder, "Spinner.csproj"));
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        try
        {
            window.OpenFolder(folder);
            Ui.WaitFor(() => Method(window, "Spins") is not null && Method(window, "Adds") is not null,
                () => $"both tests shown from the code before any build ({string.Join(", ", window.Tests.Tree.Methods.Select(m => m.Name))}; {window.Tests.Tree.EmptyText})");

            Assert.True(window.Perform(AppActions.RunAllTests));
            Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Spins")?.State is TestState.Passed or TestState.Failed,
                () => $"the run of both tests ({window.BottomPanel.Tests.Status}; {window.BottomPanel.Build.Document.Snapshot})", TimeSpan.FromMinutes(5));
            TestNode spins = Method(window, "Spins")!;
            Assert.True(Method(window, "Adds")!.State == TestState.Passed, $"The passing test did not pass:\n{Method(window, "Adds")!.Details}\n{window.BottomPanel.Tests.Status}");
            Assert.True(spins.State == TestState.Failed, $"The failing test did not fail:\n{spins.Details}\n{window.BottomPanel.Tests.Status}");
            Assert.Contains("System.InvalidOperationException: two turns are not three", spins.Details, StringComparison.Ordinal);
            Assert.Contains($"SpinnerTest.cs:line {GoDotTestGame.Line("throw new InvalidOperationException(\"two turns") + 1}", spins.Details, StringComparison.Ordinal);
            Assert.Contains("spinning 2", spins.Details, StringComparison.Ordinal);

            Assert.Null(window.OpenFile(GoDotTestGame.TestsPath(folder)));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.WaitFor(() => editor.TestMarks.Contains(new TestMark(GoDotTestGame.Line("public void Spins"), TestMarkKind.Failed))
                    && editor.TestMarks.Contains(new TestMark(GoDotTestGame.Line("public void Adds"), TestMarkKind.Passed)),
                () => $"the gutter to mark each test by how it ran ({string.Join(", ", editor.TestMarks)})");

            int turns = GoDotTestGame.Line("int turns = 2;");
            editor.GoTo(new TextPosition(turns, 8));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Task<string?> debugged = window.Tests.RunAsync(window.Folder!, [spins], debug: true);
            Ui.WaitFor(() => window.Debugger.Session is { Engine: DebugEngine.Managed, State: DebugState.Paused } && editor.LineMarks.Contains(new LineMark(turns, LineMarkKind.ExecutionPoint)),
                () => $"the stop in the test the game runs ({window.Debugger.Session?.Engine} {window.Debugger.Session?.State}; {window.BottomPanel.Tests.Status}; {window.Debugger.Output()})",
                TimeSpan.FromMinutes(5));
            Assert.Contains("Spins", window.BottomPanel.Debug.Frames[0].Name, StringComparison.Ordinal);
            string stopped = window.Debugger.Session!.ProcessStarted is { IsCompletedSuccessfully: true } started ? Processes.Describe(started.Result) : "no process";
            Assert.True(stopped.Contains(godot!.Executable, StringComparison.Ordinal), $"The test stopped in {stopped}, not in the game's Godot");
            // Just My Code: the exception GoDotTest catches outside the test's code stops it at its throw, as a failure does.
            for (int stops = 0; stops < 3 && !debugged.IsCompleted; stops++)
            {
                Ui.Run(window, AppActions.Resume);
                Ui.WaitFor(() => debugged.IsCompleted || window.Debugger.Session is { State: DebugState.Paused },
                    () => $"the debugged run to end or stop again ({window.Debugger.Session?.State}; {window.Debugger.Output()})", TimeSpan.FromMinutes(2));
            }
            Ui.Launched(window, debugged, "the debugged run to end", TimeSpan.FromMinutes(2));
            Assert.True(spins.State == TestState.Failed, $"The debugged test did not fail with its exception:\n{spins.Details}");
        }
        finally
        {
            window.Close();
        }
    }

    private static TestNode? Method(MainWindow window, string name) => window.Tests.Tree.Methods.FirstOrDefault(m => m.Name == name);
}

/// <summary>The GoDotTest game McpGoDotTestTests' agents test: <see cref="GoDotTestGame"/>, restored.</summary>
public sealed class GoDotTestFolder : IAsyncLifetime
{
    public string Folder { get; } = PathNames.Canonical(TestFolders.Create("godottest-agent-"));

    public GodotFound? Godot { get; } = GodotExecutables.Find(null);

    public ValueTask InitializeAsync() => GoDotTestGame.Skipped(Godot) is not null ? ValueTask.CompletedTask
        : new(TestFolders.PrepareAsync(async () =>
        {
            GoDotTestGame.Make(Folder, Godot!);
            await Restore.RunAsync(Folder);
        }, Folder));

    public ValueTask DisposeAsync()
    {
        TestFolders.Delete(Folder);
        TestFolders.Delete(Folder + "-home");
        return ValueTask.CompletedTask;
    }
}

/// <summary>An agent testing a Godot game's GoDotTest tests: tests_list names both from the code before any build, and
/// tests_run runs them in the game, its answer the tally and the failure with its exception at its line; debugged, it
/// stops at a breakpoint in the test and goes on to the run's end. Without a Godot 4 .NET build each skips saying why.</summary>
[Collection(Alone.Name)]
public sealed class McpGoDotTestTests : McpRig, IClassFixture<GoDotTestFolder>
{
    private readonly GoDotTestFolder game;

    public McpGoDotTestTests(GoDotTestFolder game)
    {
        this.game = game;
        Assert.SkipWhen(GoDotTestGame.Skipped(game.Godot) is not null, GoDotTestGame.Skipped(game.Godot) ?? string.Empty);
    }

    [Fact]
    public async Task An_agent_lists_a_games_GoDotTest_tests_runs_them_in_the_game_and_debugs_one_to_a_breakpoint()
    {
        await using McpClient client = await Start(["--mcp", game.Folder], languageServer: false,
            environment: new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = game.Folder + "-home" });
        string listed = Text(await Result(client, McpTools.TestsList));
        Assert.True(listed.Contains("Spinner.Tests.SpinnerTest.Adds", StringComparison.Ordinal) && listed.Contains("Spinner.Tests.SpinnerTest.Spins", StringComparison.Ordinal),
            $"tests_list did not name both GoDotTest tests from the code: {listed}");

        JsonElement ran = await Call(client, McpTools.TestsRun);
        Assert.True(ran.GetProperty("summary").GetString()!.StartsWith("Tests passed: 1, failed: 1 of 2", StringComparison.Ordinal), $"tests_run told the game's tests ran otherwise: {ran}");
        JsonElement failure = Assert.Single(ran.GetProperty("failures").EnumerateArray());
        Assert.Equal("Spinner.Tests.SpinnerTest.Spins", failure.GetProperty("test").GetString());
        string details = failure.GetProperty("details").GetString()!;
        Assert.True(details.Contains("two turns are not three", StringComparison.Ordinal)
            && details.Contains($"SpinnerTest.cs:line {GoDotTestGame.Line("throw new InvalidOperationException(\"two turns") + 1}", StringComparison.Ordinal),
            $"tests_run's failure lacks its exception at its line: {details}");

        int turns = GoDotTestGame.Line("int turns = 2;") + 1;
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "test/SpinnerTest.cs", ["line"] = turns });
        JsonElement paused = await Call(client, McpTools.TestsRun, new() { ["filter"] = "Spins", ["debug"] = true });
        JsonElement stop = paused.GetProperty("debugger");
        Assert.True(stop.GetProperty("state").GetString() == "paused" && stop.GetProperty("location").GetString()!.StartsWith($"{Path.Combine("test", "SpinnerTest.cs")}:{turns}:", StringComparison.Ordinal),
            $"tests_run with debug did not stop at the breakpoint in the test: {paused}");
        List<string> answers = [];
        for (int stops = 0; stop.GetProperty("state").GetString() == "paused" && stops < 4; stops++)
        {
            stop = await Call(client, McpTools.DebugContinue);
            answers.Add(stop.GetRawText());
        }
        Assert.True(stop.GetProperty("state").GetString() == "not debugging", $"Going on from the stops did not end the debugged run:\n{string.Join("\n", answers)}");
    }
}
