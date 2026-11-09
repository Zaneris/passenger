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

    /// <summary>A main scene's script that runs nothing of GoDotTest's.</summary>
    public const string Plain = "using Godot;\n\npublic partial class Main : Node\n{\n    public override void _Ready() => GD.Print(\"playing\");\n}\n";

    /// <summary>The test scene's script GoDotTest's README writes, which runs the tests in its scene.</summary>
    public const string TestScene = """
        using System.Reflection;
        using Chickensoft.GoDotTest;
        using Godot;

        public partial class Tests : Node2D
        {
            public override void _Ready() => _ = GoTest.RunTests(Assembly.GetExecutingAssembly(), this);
        }

        """;

    /// <summary>Writes the game into a folder, unrestored: its main scene's script <paramref name="main"/> (one running its
    /// tests by default), and where given a test scene under test/ whose root runs <paramref name="tests"/>.</summary>
    public static void Make(string folder, GodotFound godot, string main = Main, string? tests = null, string testCode = Tests)
    {
        GodotTests.Game.Make(folder, godot.Version!, godot.Packages!, main, Scene);
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
        File.WriteAllText(TestsPath(folder), testCode);
        if (tests is not null)
        {
            File.WriteAllText(Path.Combine(folder, "test", "Tests.cs"), tests);
            File.WriteAllText(Path.Combine(folder, "test", "Tests.tscn"),
                "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://test/Tests.cs\" id=\"1\"]\n\n[node name=\"Tests\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n");
        }
    }

    /// <summary>Why the tests skip with the Godot found, or null where it runs a C# game.</summary>
    public static string? Skipped(GodotFound? godot) => godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build to run these tests"
        : godot is not { WhyNot: null, Version: not null, Packages: not null } ? $"The Godot found cannot run C#: {godot.WhyNot}" : null;
}

/// <summary>A Godot 4 C# game's GoDotTest tests in the Unit Tests window, as a person who added GoDotTest to the game finds
/// them: shown from the code before any build, run inside the game on the Godot found (headless, its user folders the
/// run's), the game imported first where it never was, a failure shown with its exception at its line and what the test
/// printed, the gutter marking each test by how it ran, and debugged under .NET's debugger, a breakpoint in a test stopping
/// there. Without a Godot 4 .NET build each test skips saying why. Debugged, so run alone.</summary>
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

    /// <summary>Two tests, the second loading the game's icon, which a game loads only once Godot has imported it.</summary>
    private const string LoadsIcon = """
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
            }

            [Test]
            public void Spins()
            {
                if (ResourceLoader.Load<Texture2D>("res://icon.svg") is null)
                {
                    throw new InvalidOperationException("the icon was never imported");
                }
            }
        }

        """;

    /// <summary>A game never opened in Godot's editor, whose test loads an asset with no .import beside it, is imported before
    /// its tests run: the test loads the asset and passes, and the asset's .import is left for the game.</summary>
    [AvaloniaFact]
    public void A_game_never_imported_is_imported_before_its_tests_run_and_a_test_loading_its_asset_passes()
    {
        Assert.SkipWhen(GoDotTestGame.Skipped(godot) is not null, GoDotTestGame.Skipped(godot) ?? string.Empty);
        GoDotTestGame.Make(folder, godot!, testCode: LoadsIcon);
        string icon = Path.Combine(folder, "icon.svg");
        File.WriteAllText(icon, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\"><rect width=\"16\" height=\"16\" fill=\"#478cbf\"/></svg>\n");
        Restore.Run(Path.Combine(folder, "Spinner.csproj"));
        MainWindow window = Opened();
        try
        {
            Assert.False(File.Exists(icon + ".import"), "Opening the game's folder made a .import beside icon.svg before its tests ran");
            Ui.Launched(window, window.Tests.RunAsync(window.Folder!, [Method(window, "Spins")!]), "the game's import and the run of its test", TimeSpan.FromMinutes(5));
            Assert.True(Method(window, "Spins")!.State == TestState.Passed,
                $"The test loading the game's icon did not pass: {Method(window, "Spins")!.Details} ({window.BottomPanel.Tests.Status}; {window.BottomPanel.Build.Document.Snapshot})");
            Assert.True(File.Exists(icon + ".import"), "The import before the tests left no .import beside icon.svg for the game");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Three tests of a suite, the third writing a file and failing, which a run of the suite would run unasked.</summary>
    private const string ThreeOfASuite = """
        namespace Spinner.Tests;

        using System;
        using System.IO;
        using Chickensoft.GoDotTest;
        using Godot;

        public class SpinnerTest : TestClass
        {
            public SpinnerTest(Node testScene) : base(testScene) { }

            [Test]
            public void Adds()
            {
            }

            [Test]
            public void Spins()
            {
            }

            [Test]
            public void Stops()
            {
                File.WriteAllText(ProjectSettings.GlobalizePath("res://stops-ran"), "ran");
                throw new InvalidOperationException("the test nobody chose");
            }
        }

        """;

    /// <summary>Two of a suite's three tests chosen run those two: the third never runs, so neither its side effects nor its
    /// failure come of a run that did not ask for it.</summary>
    [AvaloniaFact]
    public void Two_tests_of_a_suites_three_run_and_the_third_does_not()
    {
        Assert.SkipWhen(GoDotTestGame.Skipped(godot) is not null, GoDotTestGame.Skipped(godot) ?? string.Empty);
        GoDotTestGame.Make(folder, godot!, testCode: ThreeOfASuite);
        Restore.Run(Path.Combine(folder, "Spinner.csproj"));
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        try
        {
            window.OpenFolder(folder);
            Ui.WaitFor(() => Method(window, "Stops") is not null && Method(window, "Spins") is not null && Method(window, "Adds") is not null,
                () => $"the three tests shown ({string.Join(", ", window.Tests.Tree.Methods.Select(m => m.Name))}; {window.Tests.Tree.EmptyText})");
            Ui.Launched(window, window.Tests.RunAsync(window.Folder!, [Method(window, "Adds")!, Method(window, "Spins")!]), "the run of two of the three", TimeSpan.FromMinutes(5));
            Assert.True(Method(window, "Adds")!.State == TestState.Passed && Method(window, "Spins")!.State == TestState.Passed,
                $"The chosen tests did not pass: {Method(window, "Adds")!.State}, {Method(window, "Spins")!.State} ({window.BottomPanel.Tests.Status}; {window.BottomPanel.Build.Document.Snapshot})");
            Assert.False(File.Exists(Path.Combine(folder, "stops-ran")), "The test nobody chose ran");
            Assert.True(Method(window, "Stops")!.State == TestState.NotRun, $"The test nobody chose has a result: {Method(window, "Stops")!.Details}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Two classes of one name in two namespaces, each test saying it ran by a file of its own.</summary>
    private const string TwoOfOneName = """
        namespace Spinner.Tests
        {
            using System.IO;
            using Chickensoft.GoDotTest;
            using Godot;

            public class SpinnerTest : TestClass
            {
                public SpinnerTest(Node testScene) : base(testScene) { }

                [Test]
                public void Spins() => File.WriteAllText(ProjectSettings.GlobalizePath("res://first-ran"), "ran");
            }
        }

        namespace Spinner.Others
        {
            using System.IO;
            using Chickensoft.GoDotTest;
            using Godot;

            public class SpinnerTest : TestClass
            {
                public SpinnerTest(Node testScene) : base(testScene) { }

                [Test]
                public void Spins() => File.WriteAllText(ProjectSettings.GlobalizePath("res://second-ran"), "ran");
            }
        }

        """;

    /// <summary>A test of one of two classes GoDotTest knows by one name is refused saying why, Godot left unstarted, where a
    /// run by name would have run the other class's; both chosen together run, as a suite takes them.</summary>
    [AvaloniaFact]
    public void A_test_of_one_of_two_classes_of_one_name_is_refused_saying_why_and_both_chosen_run_together()
    {
        Assert.SkipWhen(GoDotTestGame.Skipped(godot) is not null, GoDotTestGame.Skipped(godot) ?? string.Empty);
        GoDotTestGame.Make(folder, godot!, testCode: TwoOfOneName);
        Restore.Run(Path.Combine(folder, "Spinner.csproj"));
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        TestNode? Named(string fullName) => window.Tests.Tree.Methods.FirstOrDefault(m => m.FullName == fullName);
        try
        {
            window.OpenFolder(folder);
            Ui.WaitFor(() => Named("Spinner.Tests.SpinnerTest.Spins") is not null && Named("Spinner.Others.SpinnerTest.Spins") is not null,
                () => $"both classes' tests shown ({string.Join(", ", window.Tests.Tree.Methods.Select(m => m.FullName))}; {window.Tests.Tree.EmptyText})");
            TestNode second = Named("Spinner.Others.SpinnerTest.Spins")!;
            Ui.Launched(window, window.Tests.RunAsync(window.Folder!, [second]), "the refused run", TimeSpan.FromMinutes(5));
            Assert.True(second.State == TestState.Skipped, $"The test GoDotTest cannot run alone was told as {second.State}: {second.Details}");
            Assert.Contains("Spinner.Tests.SpinnerTest", second.Details, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(folder, "first-ran")) || File.Exists(Path.Combine(folder, "second-ran")), "Godot ran a test of a run that was refused");

            Ui.Launched(window, window.Tests.RunAsync(window.Folder!, [Named("Spinner.Tests.SpinnerTest.Spins")!, second]), "the run of both", TimeSpan.FromMinutes(5));
            Assert.True(File.Exists(Path.Combine(folder, "first-ran")) && File.Exists(Path.Combine(folder, "second-ran")),
                $"Both classes chosen did not both run ({window.BottomPanel.Tests.Status}; {window.BottomPanel.Build.Document.Snapshot})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Two tests, one ending a moment after it starts and the next waiting until the test lets it go (a file in the
    /// game's folder), under a main scene giving GoDotTest's tests two minutes each.</summary>
    private const string Stepping = """
        namespace Spinner.Tests;

        using System.Threading.Tasks;
        using Chickensoft.GoDotTest;
        using Godot;

        public class SpinnerTest : TestClass
        {
            public SpinnerTest(Node testScene) : base(testScene) { }

            [Test]
            public async Task Adds() => await Task.Delay(300);

            [Test]
            public async Task Spins()
            {
                for (int waited = 0; waited < 1100 && !FileAccess.FileExists("res://let-go"); waited++)
                {
                    await TestScene.ToSignal(TestScene.GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
                }
            }
        }

        """;

    /// <summary>The main scene's script running the tests with two minutes for each, where GoDotTest gives ten seconds.</summary>
    private const string Patient = """
        using System.Reflection;
        using Chickensoft.GoDotTest;
        using Godot;

        public partial class Main : Node
        {
            public override void _Ready()
            {
                if (TestEnvironment.From(OS.GetCmdlineArgs()).ShouldRunTests)
                {
                    GoTest.TimeoutMilliseconds = 120000;
                    CallDeferred(nameof(RunTests));
                }
            }

            private void RunTests() => _ = GoTest.RunTests(Assembly.GetExecutingAssembly(), this);
        }

        """;

    /// <summary>Run All Tests tells the first test passed, with the time it ran, while the next is still running, and that
    /// one once the test lets it go; debugged, the run tells them so too, from the debug session's console.</summary>
    [AvaloniaFact]
    public void Each_test_is_told_as_it_ends_with_its_time_while_a_later_one_still_runs_and_so_under_Debug()
    {
        Assert.SkipWhen(GoDotTestGame.Skipped(godot) is not null, GoDotTestGame.Skipped(godot) ?? string.Empty);
        GoDotTestGame.Make(folder, godot!, main: Patient, testCode: Stepping);
        Restore.Run(Path.Combine(folder, "Spinner.csproj"));
        MainWindow window = Opened();
        try
        {
            Assert.True(window.Perform(AppActions.RunAllTests));
            // The run is work under way until Godot ends, which a wait for the IDE to be quiet would wait out: the loop runs instead.
            Assert.True(Ui.Pumped(() => Method(window, "Adds")?.State == TestState.Passed && Method(window, "Spins")?.State == TestState.Running, TimeSpan.FromMinutes(5)),
                $"Adds was not told passed while Spins still ran ({Method(window, "Adds")?.State}, {Method(window, "Spins")?.State}; {window.BottomPanel.Tests.Status})");
            TimeSpan adds = Method(window, "Adds")!.Duration;
            Assert.True(adds >= TimeSpan.FromMilliseconds(250), $"Adds, which waits 0.3 s, was told as running {adds.TotalMilliseconds} ms");
            File.WriteAllText(Path.Combine(folder, "let-go"), "go");
            Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Spins")?.State == TestState.Passed,
                () => $"Spins to pass once let go ({Method(window, "Spins")?.State}; {window.BottomPanel.Tests.Status})", TimeSpan.FromMinutes(2));
            Assert.True(Method(window, "Adds")!.State == TestState.Passed && Method(window, "Adds")!.Duration == adds,
                $"Adds was told again otherwise at the run's end: {Method(window, "Adds")!.State}, {Method(window, "Adds")!.Duration.TotalMilliseconds} ms");
            Assert.True(Method(window, "Spins")!.Duration > TimeSpan.Zero, "Spins was told with no time");

            File.Delete(Path.Combine(folder, "let-go"));
            Task<string?> debugged = window.Tests.RunAsync(window.Folder!, debug: true, build: false);
            Assert.True(Ui.Pumped(() => Method(window, "Adds")?.State == TestState.Passed && Method(window, "Spins")?.State == TestState.Running
                    && window.Debugger.Session is not null, TimeSpan.FromMinutes(5)),
                $"Adds was not told passed under Debug while Spins still ran ({Method(window, "Adds")?.State}, {Method(window, "Spins")?.State}; {window.BottomPanel.Tests.Status}; {window.Debugger.Output()})");
            File.WriteAllText(Path.Combine(folder, "let-go"), "go");
            Ui.Launched(window, debugged, "the debugged run to end", TimeSpan.FromMinutes(2));
            Assert.True(Method(window, "Spins")!.State == TestState.Passed, $"Spins did not pass under Debug once let go: {Method(window, "Spins")!.Details}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A game whose main scene runs nothing of GoDotTest's, its tests run by a test scene under test/ as GoDotTest's
    /// README writes one, runs them from that scene: one passes and the other fails.</summary>
    [AvaloniaFact]
    public void A_game_whose_tests_run_from_a_test_scene_runs_them_from_it()
    {
        Assert.SkipWhen(GoDotTestGame.Skipped(godot) is not null, GoDotTestGame.Skipped(godot) ?? string.Empty);
        GoDotTestGame.Make(folder, godot!, main: GoDotTestGame.Plain, tests: GoDotTestGame.TestScene);
        Restore.Run(Path.Combine(folder, "Spinner.csproj"));
        MainWindow window = Opened();
        try
        {
            Assert.True(window.Perform(AppActions.RunAllTests));
            Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Spins")?.State is TestState.Passed or TestState.Failed,
                () => $"the run of both tests from the test scene ({window.BottomPanel.Tests.Status}; {window.BottomPanel.Build.Document.Snapshot})", TimeSpan.FromMinutes(5));
            Assert.True(Method(window, "Adds")!.State == TestState.Passed && Method(window, "Spins")!.State == TestState.Failed,
                $"The test scene's run told Adds {Method(window, "Adds")!.State} and Spins {Method(window, "Spins")!.State}: {window.BottomPanel.Tests.Status}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The run settings' GoDotTestScene parameter chooses the scene: one the game lacks is refused before Godot
    /// starts, Run All Tests greying with why; ci/Runner.tscn, named there, runs the tests, where test/Tests.tscn, which
    /// GoDotTest's convention would choose, runs none of them.</summary>
    [AvaloniaFact]
    public void The_run_settings_choose_the_test_scene_one_the_game_lacks_refused_before_godot_starts()
    {
        Assert.SkipWhen(GoDotTestGame.Skipped(godot) is not null, GoDotTestGame.Skipped(godot) ?? string.Empty);
        GoDotTestGame.Make(folder, godot!, main: GoDotTestGame.Plain, tests: GoDotTestGame.TestScene.Replace("this)", "this, predicate: _ => false)", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.Combine(folder, "ci"));
        File.WriteAllText(Path.Combine(folder, "ci", "Runner.cs"), GoDotTestGame.TestScene.Replace("class Tests", "class Runner", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "ci", "Runner.tscn"),
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://ci/Runner.cs\" id=\"1\"]\n\n[node name=\"Runner\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n");
        string project = Path.Combine(folder, "Spinner.csproj");
        File.WriteAllText(project, File.ReadAllText(project).Replace("</PropertyGroup>", "  <RunSettingsFilePath>$(MSBuildProjectDirectory)/godot.runsettings</RunSettingsFilePath>\n  </PropertyGroup>", StringComparison.Ordinal));
        string settings = Path.Combine(folder, "godot.runsettings");
        File.WriteAllText(settings, RunSettings("res://ci/Missing.tscn"));
        Restore.Run(project);
        MainWindow window = Opened();
        try
        {
            string missing = Refusals.GoDotTestStart("Spinner", new GoDotTests.Start("res://ci/Missing.tscn", GoDotTests.StartProblem.ChosenMissing) { Settings = settings })!;
            Ui.WaitFor(() => window.Actions.WhyNot(AppActions.RunAllTests, window.Actions.Context()) == missing,
                () => $"Run All Tests to grey saying the scene named is not the game's ({window.Actions.WhyNot(AppActions.RunAllTests, window.Actions.Context())})", TimeSpan.FromMinutes(2));

            File.WriteAllText(settings, RunSettings("res://ci/Runner.tscn"));
            Assert.Null(Ui.Await(window.Tests.RunAsync(window.Folder!), "the run from the scene the run settings name", TimeSpan.FromMinutes(5)));
            Assert.True(Method(window, "Adds")!.State == TestState.Passed && Method(window, "Spins")!.State == TestState.Failed,
                $"The scene the run settings name told Adds {Method(window, "Adds")!.State} and Spins {Method(window, "Spins")!.State}: {window.BottomPanel.Tests.Status}");
        }
        finally
        {
            window.Close();
        }

        static string RunSettings(string scene) =>
            $"<RunSettings>\n  <TestRunParameters>\n    <Parameter name=\"{GoDotTests.SceneParameter}\" value=\"{scene}\" />\n  </TestRunParameters>\n</RunSettings>\n";
    }

    /// <summary>A game no scene of which runs GoDotTest's tests is said to never run them before Godot starts: Run All Tests
    /// greys saying so, and a run asked for anyway starts no Godot and says the same.</summary>
    [AvaloniaFact]
    public void A_game_that_never_runs_its_tests_is_said_so_before_godot_starts_and_run_all_greys()
    {
        Assert.SkipWhen(GoDotTestGame.Skipped(godot) is not null, GoDotTestGame.Skipped(godot) ?? string.Empty);
        GoDotTestGame.Make(folder, godot!, main: GoDotTestGame.Plain);
        MainWindow window = Opened();
        List<string> started = [];
        EventHandler<ChildStart> heard = (_, child) =>
        {
            if (child.Command.Contains(godot!.Executable, StringComparison.Ordinal))
            {
                lock (started)
                {
                    started.Add(child.Command);
                }
            }
        };
        ChildProcesses.Started += heard;
        try
        {
            string never = Refusals.GoDotTestNeverRuns("Spinner");
            Ui.WaitFor(() => window.Actions.WhyNot(AppActions.RunAllTests, window.Actions.Context()) == never,
                () => $"Run All Tests to grey saying the game never runs its tests ({window.Actions.WhyNot(AppActions.RunAllTests, window.Actions.Context())})");
            Assert.Equal(never, Ui.Await(window.Tests.RunAsync(window.Folder!, build: false), "a run asked for anyway", TimeSpan.FromMinutes(2)));
            lock (started)
            {
                Assert.True(started.Count == 0, $"Godot was started for a game that never runs its tests: {string.Join("; ", started)}");
            }
        }
        finally
        {
            ChildProcesses.Started -= heard;
            window.Close();
        }
    }

    /// <summary>The game's folder open, both its tests shown from the code before any build.</summary>
    private MainWindow Opened()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        window.OpenFolder(folder);
        Ui.WaitFor(() => Method(window, "Spins") is not null && Method(window, "Adds") is not null,
            () => $"both tests shown from the code before any build ({string.Join(", ", window.Tests.Tree.Methods.Select(m => m.Name))}; {window.Tests.Tree.EmptyText})");
        return window;
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
