using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Testing;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Godot 4 C# game's GdUnit4 tests in the Unit Tests window, as a person who added GdUnit4 to the game finds
/// them: shown from the code before any build, run through GdUnit4's VSTest adapter, which starts the tests that need the
/// engine on the Godot Passenger found (given as GODOT_BIN), a failure shown at its assertion's line, and debugged, a
/// breakpoint in such a test stopping in the Godot the adapter starts. Its packages come from the Godot's own and the
/// package cache tests/GodotGraphs fills, never nuget.org; Godot runs headless (the run settings' GdUnit4 parameters),
/// its user folders the run's. Without a Godot 4 .NET build each test skips saying why. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GdUnit4Tests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("gdunit4-"));

    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>The tests: one that runs without the engine, and one that needs it, which fails at its assertion.</summary>
    private const string Tests = """
        namespace Spinner.Tests;

        using GdUnit4;
        using static GdUnit4.Assertions;

        [TestSuite]
        public class SpinnerTest
        {
            [TestCase]
            public void Adds()
            {
                AssertThat(1 + 1).IsEqual(2);
            }

            [TestCase]
            [RequireGodotRuntime]
            public void SpinsInGodot()
            {
                Godot.Node node = new();
                int turns = 2;
                AssertThat(turns).IsEqual(3);
                node.Free();
            }
        }

        """;

    private string TestsPath => Path.Combine(folder, "test", "SpinnerTest.cs");

    /// <summary>Both tests are in the tree before any build; Run All Tests passes the one without the engine and fails the
    /// one Godot ran at its assertion's line, which its details link; Debug of it stops at a breakpoint inside it in
    /// Godot's process, then at its failed assertion, and the run ends with its failure.</summary>
    [AvaloniaFact]
    public void A_GdUnit4_test_is_found_from_code_runs_on_the_Godot_found_fails_at_its_line_and_stops_at_a_breakpoint_in_Godot()
    {
        Assert.SkipWhen(godot is not { WhyNot: null, Version: not null, Packages: not null }, godot is null
            ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build to run these tests"
            : $"The Godot found cannot run C#: {godot.WhyNot}");
        Make();
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        try
        {
            window.OpenFolder(folder);
            Ui.WaitFor(() => Method(window, "SpinsInGodot") is not null && Method(window, "Adds") is not null,
                () => $"both tests shown from the code before any build ({string.Join(", ", window.Tests.Tree.Methods.Select(m => m.Name))}; {window.Tests.Tree.EmptyText})");

            Assert.True(window.Perform(AppActions.RunAllTests));
            Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "SpinsInGodot")?.State is TestState.Passed or TestState.Failed,
                () => $"the run of both tests ({window.BottomPanel.Tests.Status}; {window.BottomPanel.Build.Document.Snapshot})", TimeSpan.FromMinutes(5));
            TestNode spins = Method(window, "SpinsInGodot")!;
            Assert.True(Method(window, "Adds")!.State == TestState.Passed, $"The test without the engine did not pass:\n{Method(window, "Adds")!.Details}");
            Assert.True(spins.State == TestState.Failed, $"The test Godot ran did not fail at its assertion:\n{spins.Details}");
            int assertion = Line("AssertThat(turns).IsEqual(3);");
            Assert.Contains($"SpinnerTest.cs:line {assertion + 1}", spins.Details, StringComparison.Ordinal);
            Assert.Contains("'3' but is '2'", spins.Details, StringComparison.Ordinal);

            Assert.Null(window.OpenFile(TestsPath));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            int turns = Line("int turns = 2;");
            editor.GoTo(new TextPosition(turns, 8));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Task<string?> debugged = window.Tests.RunAsync(window.Folder!, [spins], debug: true);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.BottomPanel.Debug.Frames.Count > 0,
                () => $"the stop in the test Godot runs ({window.BottomPanel.Tests.Status}; {window.Debugger.Output()})", TimeSpan.FromMinutes(5));
            Assert.Contains(new LineMark(turns, LineMarkKind.ExecutionPoint), editor.LineMarks);
            Assert.Contains("SpinsInGodot", window.BottomPanel.Debug.Frames[0].Name, StringComparison.Ordinal);
            string stopped = window.Debugger.Session!.ProcessStarted is { IsCompletedSuccessfully: true } started ? Processes.Describe(started.Result) : "no process";
            Assert.True(stopped.Contains(godot!.Executable, StringComparison.Ordinal), $"The test stopped in {stopped}, not in the Godot GdUnit4's adapter started");
            // Just My Code: the assertion GdUnit4 catches outside the test's code stops it there, as a failed assertion does.
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Paused, StopReason: "exception" } && editor.LineMarks.Contains(new LineMark(assertion, LineMarkKind.ExecutionPoint)),
                () => $"the stop at the failed assertion ({window.Debugger.Session?.State}, {window.Debugger.Session?.StopReason}; {window.Debugger.Output()})", TimeSpan.FromMinutes(1));
            Ui.Run(window, AppActions.Resume);
            Ui.Launched(window, debugged, "the debugged run to end", TimeSpan.FromMinutes(2));
            Assert.Equal(TestState.Failed, spins.State);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The editor's line (from zero) of the first line of the tests holding <paramref name="code"/>.</summary>
    private static int Line(string code) => Tests.Split('\n').ToList().FindIndex(l => l.Contains(code, StringComparison.Ordinal));

    private static TestNode? Method(MainWindow window, string name) => window.Tests.Tree.Methods.FirstOrDefault(m => m.Name == name);

    /// <summary>A Godot 4 C# game, Spinner, with GdUnit4's API and adapter beside VSTest's SDK, its run settings giving
    /// Godot --headless, and its tests, restored.</summary>
    private void Make()
    {
        GodotTests.Game.Make(folder, godot!.Version!, godot.Packages!, "using Godot;\n\npublic partial class Main : Node\n{\n}\n",
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://Main.cs\" id=\"1\"]\n\n[node name=\"Main\" type=\"Node\"]\nscript = ExtResource(\"1\")\n");
        File.WriteAllText(Path.Combine(folder, "Spinner.csproj"), $"""
            <Project Sdk="Godot.NET.Sdk/{godot.Version}">
              <PropertyGroup>
                <TargetFramework>{TestProjects.Framework}</TargetFramework>
                <EnableDynamicLoading>true</EnableDynamicLoading>
                <RunSettingsFilePath>$(MSBuildProjectDirectory)/.runsettings</RunSettingsFilePath>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="{AvaloniaApp.Metadata("TestSdkVersion")}" />
                <PackageReference Include="gdUnit4.api" Version="{AvaloniaApp.Metadata("GdUnit4ApiVersion")}" />
                <PackageReference Include="gdUnit4.test.adapter" Version="{AvaloniaApp.Metadata("GdUnit4AdapterVersion")}" />
              </ItemGroup>
            </Project>

            """);
        File.WriteAllText(Path.Combine(folder, ".runsettings"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<RunSettings>\n  <GdUnit4>\n    <Parameters>--headless</Parameters>\n  </GdUnit4>\n</RunSettings>\n");
        Directory.CreateDirectory(Path.GetDirectoryName(TestsPath)!);
        File.WriteAllText(TestsPath, Tests);
        Restore.Run(Path.Combine(folder, "Spinner.csproj"));
    }
}
