using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Testing;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Godot 4 game's GUT tests in the Unit Tests window: found in its GDScript before any run, run through GUT's
/// command line on the Godot found, the project imported first (GutTest is a global class), each test's outcome read back
/// from GUT's JUnit XML, a failure shown at its line, the gutter marking the tests by how they ran, and debugged, a
/// breakpoint in a test stopping in the GDScript debugger. GUT here is a stand-in the test writes, which takes GUT's
/// command line and writes its results and output as GUT does, so the test fetches nothing and carries none of GUT.
/// Godot runs headless, its user folders the run's. Without a Godot 4 each test skips saying why. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotGutTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("gut-"));

    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public void Dispose() => TestFolders.Delete(folder);

    private const string Tests = """
        extends GutTest


        func test_adds():
        	assert_eq(1 + 1, 2, "adds")


        func test_spins():
        	var turns := 2
        	print("spinning ", turns)
        	assert_eq(turns, 3, "two turns are not three")

        """;

    /// <summary>GUT's test class, as GUT names it, the assertions the tests make, and where a failed one was.</summary>
    private const string GutTest = """
        class_name GutTest
        extends Node

        var failures: Array = []


        func assert_eq(got, expected, text := "") -> void:
        	if got != expected:
        		fail(str("[", got, "] expected to equal [", expected, "]:  ", text))


        func fail(text: String) -> void:
        	var line := -1
        	for frame in get_stack():
        		if str(frame.function).begins_with("test_"):
        			line = frame.line
        	print("    [Failed]:  ", text)
        	print("      at line ", line)
        	failures.append(str(text, "\n      at line ", line))

        """;

    /// <summary>GUT's command line: the scripts -gtest names, the tests -gunit_test_name picks, a JUnit file as GUT's.</summary>
    private const string CommandLine = """
        extends SceneTree


        func _initialize() -> void:
        	var options := {}
        	for argument in OS.get_cmdline_args():
        		if argument.begins_with("-g") and argument.contains("="):
        			options[argument.get_slice("=", 0)] = argument.substr(argument.find("=") + 1)
        	var suites := ""
        	var failed := 0
        	var total := 0
        	for path in str(options.get("-gtest", "")).split(",", false):
        		var script: GDScript = load(path)
        		print("\n", path)
        		var test = script.new()
        		root.add_child(test)
        		var cases := ""
        		var ran := 0
        		var fails := 0
        		for method in script.get_script_method_list():
        			var name: String = method.name
        			if not name.begins_with("test_") or (options.has("-gunit_test_name") and not name.contains(options["-gunit_test_name"])):
        				continue
        			print("* ", name)
        			test.failures.clear()
        			test.call(name)
        			ran += 1
        			var status := "pass"
        			var failure := ""
        			if not test.failures.is_empty():
        				status = "fail"
        				fails += 1
        				failure = "<failure message=\"failed\"><![CDATA[%s]]></failure>" % test.failures[0]
        			cases += "<testcase name=\"%s\" assertions=\"1\" status=\"%s\" classname=\"%s\" time=\"0.001\" >\n%s</testcase>\n" % [name, status, path.trim_prefix("res://"), failure]
        		suites += "<testsuite name=\"%s\" tests=\"%d\" failures=\"%d\" skipped=\"0\" time=\"0.001\" >\n%s</testsuite>\n" % [path.trim_prefix("res://"), ran, fails, cases]
        		print("%d/%d passed." % [ran - fails, ran])
        		total += ran
        		failed += fails
        		test.free()
        	var file := FileAccess.open(options["-gjunit_xml_file"], FileAccess.WRITE)
        	file.store_string("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<testsuites name=\"GutTests\" failures=\"%d\" tests=\"%d\" >\n%s</testsuites>" % [failed, total, suites])
        	file.close()
        	quit(1 if failed > 0 else 0)

        """;

    private string TestsPath => Path.Combine(folder, "test", "test_spinner.gd");

    /// <summary>Both tests are in the tree from the GDScript before any run; Run All Tests imports the project, passes one
    /// and fails the other at its assertion's line, linked, with what it printed; the open file's gutter marks each test by
    /// how it ran; Debug of the failing one stops at a breakpoint inside it in the GDScript debugger, and Resume lets the run
    /// end with its failure.</summary>
    [AvaloniaFact]
    public void A_GUT_test_is_found_in_GDScript_runs_on_the_Godot_found_fails_at_its_line_and_stops_at_a_breakpoint()
    {
        Assert.SkipWhen(godot is not { WhyNotScripts: null }, godot is null
            ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4 to run these tests"
            : $"The Godot found cannot run a Godot 4 project: {godot.WhyNotScripts}");
        Make();
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        try
        {
            window.OpenFolder(folder);
            Ui.WaitFor(() => Method(window, "test_spins") is not null && Method(window, "test_adds") is not null,
                () => $"both tests shown from the GDScript before any run ({string.Join(", ", window.Tests.Tree.Methods.Select(m => m.Name))}; {window.Tests.Tree.EmptyText})");
            Assert.Equal("test.test_spinner.test_spins", Method(window, "test_spins")!.FullName);

            Assert.True(window.Perform(AppActions.RunAllTests));
            Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "test_spins")?.State is TestState.Passed or TestState.Failed,
                () => $"the run of both tests ({window.BottomPanel.Tests.Status}; {window.BottomPanel.Build.Document.Snapshot})", TimeSpan.FromMinutes(3));
            TestNode spins = Method(window, "test_spins")!;
            Assert.True(Method(window, "test_adds")!.State == TestState.Passed, $"The passing test did not pass:\n{Method(window, "test_adds")!.Details}");
            Assert.True(spins.State == TestState.Failed, $"The failing test did not fail:\n{spins.Details}");
            int assertion = Line("assert_eq(turns, 3");
            Assert.Contains($"test_spinner.gd:line {assertion + 1}", spins.Details, StringComparison.Ordinal);
            Assert.Contains("two turns are not three", spins.Details, StringComparison.Ordinal);
            Assert.Contains("spinning 2", spins.Details, StringComparison.Ordinal);

            Assert.Null(window.OpenFile(TestsPath));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.WaitFor(() => editor.TestMarks.Contains(new TestMark(Line("func test_spins"), TestMarkKind.Failed))
                    && editor.TestMarks.Contains(new TestMark(Line("func test_adds"), TestMarkKind.Passed)),
                () => $"the gutter to mark each test by how it ran ({string.Join(", ", editor.TestMarks)})");

            int turns = Line("var turns := 2");
            editor.GoTo(new TextPosition(turns, 1));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Task<string?> debugged = window.Tests.RunAsync(window.Folder!, [spins], debug: true);
            Ui.WaitFor(() => window.Debugger.Session is { Engine: DebugEngine.GDScript, State: DebugState.Paused }
                    && editor.LineMarks.Contains(new LineMark(turns, LineMarkKind.ExecutionPoint)),
                () => $"the stop in the test ({window.Debugger.Session?.Engine} {window.Debugger.Session?.State}; {window.BottomPanel.Tests.Status}; {window.Debugger.Output()})",
                TimeSpan.FromMinutes(3));
            Ui.Run(window, AppActions.Resume);
            Ui.Launched(window, debugged, "the debugged run to end", TimeSpan.FromMinutes(2));
            Assert.True(spins.State == TestState.Failed, $"The debugged test did not fail at its assertion:\n{spins.Details}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The editor's line (from zero) of the tests' first line holding <paramref name="code"/>.</summary>
    private static int Line(string code) => Tests.Split('\n').ToList().FindIndex(l => l.Contains(code, StringComparison.Ordinal));

    private static TestNode? Method(MainWindow window, string name) => window.Tests.Tree.Methods.FirstOrDefault(m => m.Name == name);

    /// <summary>A Godot 4 game of GDScript alone that carries GUT (the stand-in), with its tests under test/.</summary>
    private void Make()
    {
        string features = string.Join('.', (godot!.Version ?? "4.7").Split('.').Take(2));
        TestFolders.Write(folder, "project.godot", $"config_version=5\n\n[application]\n\nconfig/name=\"Spinner\"\nconfig/features=PackedStringArray(\"{features}\")\n");
        TestFolders.Write(folder, "addons/gut/gut_cmdln.gd", CommandLine);
        TestFolders.Write(folder, "addons/gut/test.gd", GutTest);
        TestFolders.Write(folder, "test/test_spinner.gd", Tests);
    }
}
