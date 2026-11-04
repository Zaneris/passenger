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
/// breakpoint in a test stopping in the GDScript debugger, and in a C# game a breakpoint in the C# a test calls stopping
/// in .NET's. GUT here is a stand-in the test writes, which takes GUT's command line and writes its results and output as
/// GUT does, so the test fetches nothing and carries none of GUT. Godot runs headless, its user folders the run's. Without
/// a Godot 4 (a .NET build for C#) each test skips saying why. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GodotGutTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("gut-"));

    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(folder + "-home");
    }

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
    internal const string GutTest = """
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

    /// <summary>GUT's command line: the scripts -gtest names, the tests -gunit_test_name picks (every test where it is empty,
    /// as GUT takes it), a JUnit file as GUT's.</summary>
    internal const string CommandLine = """
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
        			if not name.begins_with("test_") or (str(options.get("-gunit_test_name", "")) != "" and not name.contains(options["-gunit_test_name"])):
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

    /// <summary>A C# class of the game's, which the GUT test below calls.</summary>
    private const string Turner = """
        using Godot;

        public partial class Turner : RefCounted
        {
            public int Turns(int count)
            {
                int twice = count * 2;
                return twice;
            }
        }

        """;

    private const string CallsCSharp = """
        extends GutTest


        func test_turns_twice():
        	var turner = load("res://Turner.cs").new()
        	assert_eq(turner.Turns(2), 4, "two turns twice are four")

        """;

    /// <summary>In a Godot 4 C# game, Debug of a GUT test that calls the game's C# stops at a breakpoint in that C# under
    /// .NET's debugger, in the Godot GUT's run starts, and going on lets the test pass.</summary>
    [AvaloniaFact]
    public void A_GUT_test_of_a_CSharp_game_stops_at_a_breakpoint_in_the_CSharp_it_calls()
    {
        Assert.SkipWhen(GoDotTestGame.Skipped(godot) is not null, GoDotTestGame.Skipped(godot) ?? string.Empty);
        GodotTests.Game.Make(folder, godot!.Version!, godot.Packages!, "using Godot;\n\npublic partial class Main : Node\n{\n}\n",
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://Main.cs\" id=\"1\"]\n\n[node name=\"Main\" type=\"Node\"]\nscript = ExtResource(\"1\")\n");
        TestFolders.Write(folder, "Turner.cs", Turner);
        TestFolders.Write(folder, "addons/gut/gut_cmdln.gd", CommandLine);
        TestFolders.Write(folder, "addons/gut/test.gd", GutTest);
        TestFolders.Write(folder, "test/test_turner.gd", CallsCSharp);
        Restore.Run(Path.Combine(folder, "Spinner.csproj"));
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        try
        {
            window.OpenFolder(folder);
            Ui.WaitFor(() => Method(window, "test_turns_twice") is not null,
                () => $"the test shown from the GDScript before any run ({string.Join(", ", window.Tests.Tree.Methods.Select(m => m.Name))}; {window.Tests.Tree.EmptyText})");
            TestNode turns = Method(window, "test_turns_twice")!;
            Assert.Null(window.OpenFile(Path.Combine(folder, "Turner.cs")));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            int twice = Turner.Split('\n').ToList().FindIndex(l => l.Contains("int twice", StringComparison.Ordinal));
            editor.GoTo(new TextPosition(twice, 8));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Task<string?> debugged = window.Tests.RunAsync(window.Folder!, [turns], debug: true);
            Ui.WaitFor(() => window.Debugger.Session is { Engine: DebugEngine.Managed, State: DebugState.Paused } && editor.LineMarks.Contains(new LineMark(twice, LineMarkKind.ExecutionPoint)),
                () => $"the stop in the C# the GUT test calls ({window.Debugger.Session?.Engine} {window.Debugger.Session?.State}; {window.BottomPanel.Tests.Status}; "
                    + $"{window.BottomPanel.Build.Document.Snapshot}; {window.Debugger.Output()})", TimeSpan.FromMinutes(5));
            Assert.Contains("Turns", window.BottomPanel.Debug.Frames[0].Name, StringComparison.Ordinal);
            Ui.Run(window, AppActions.Resume);
            Ui.Launched(window, debugged, "the debugged run to end", TimeSpan.FromMinutes(2));
            Assert.True(turns.State == TestState.Passed, $"The test that calls C# did not pass once let go:\n{turns.Details}\n{window.BottomPanel.Tests.Status}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Tests in scripts extending the project's own test class (a class_name extending GutTest), by its name, by
    /// its path and through a class extending it, and in an inner class extending it, are in the tree from the GDScript before
    /// any run, as GUT runs them; once the base extends a Node instead, they leave it. Needs no Godot: nothing runs.</summary>
    [AvaloniaFact]
    public void Tests_extending_the_projects_own_test_class_are_found_and_leave_once_it_is_no_test_class()
    {
        TestFolders.Write(folder, "project.godot", "config_version=5\n\n[application]\n\nconfig/name=\"Spinner\"\nconfig/features=PackedStringArray(\"4.7\")\n");
        TestFolders.Write(folder, "addons/gut/gut_cmdln.gd", CommandLine);
        TestFolders.Write(folder, "addons/gut/test.gd", GutTest);
        TestFolders.Write(folder, "test/tools/spin_tester.gd", "class_name SpinTester\nextends GutTest\n\n\nfunc spun():\n\treturn 2\n");
        TestFolders.Write(folder, "test/tools/turn_tester.gd", "class_name TurnTester extends SpinTester\n");
        TestFolders.Write(folder, "test/test_named.gd", "extends SpinTester\n\n\nfunc test_by_name():\n\tassert_eq(spun(), 2)\n");
        TestFolders.Write(folder, "test/test_path.gd", "extends \"res://test/tools/spin_tester.gd\"\n\n\nfunc test_by_path():\n\tassert_eq(spun(), 2)\n");
        TestFolders.Write(folder, "test/test_chain.gd", "extends TurnTester\n\n\nfunc test_through_a_chain():\n\tassert_eq(spun(), 2)\n");
        TestFolders.Write(folder, "test/test_inner.gd", "extends GutTest\n\n\nclass TestSpins:\n\textends SpinTester\n\n\tfunc test_in_an_inner_class():\n\t\tassert_eq(spun(), 2)\n");
        string[] derived = ["test_by_name", "test_by_path", "test_through_a_chain", "test_in_an_inner_class"];
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        try
        {
            window.OpenFolder(folder);
            Ui.WaitFor(() => derived.All(name => Method(window, name) is not null),
                () => $"every test extending the project's own test class shown before any run ({string.Join(", ", window.Tests.Tree.Methods.Select(m => m.FullName))}; {window.Tests.Tree.EmptyText})");
            Assert.Equal("test.test_inner+TestSpins.test_in_an_inner_class", Method(window, "test_in_an_inner_class")!.FullName);

            TestFolders.Write(folder, "test/tools/spin_tester.gd", "class_name SpinTester\nextends Node\n\n\nfunc spun():\n\treturn 2\n");
            Ui.WaitFor(() => derived.All(name => Method(window, name) is null),
                () => $"the tests to leave once their base extends a Node ({string.Join(", ", window.Tests.Tree.Methods.Select(m => m.FullName))})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Where the project's .gutconfig.json names GUT's folders, the tests are those of the scripts under them with
    /// GUT's prefix, as GUT runs them: not a template outside them nor a helper without the prefix, though a test class
    /// outside them still makes those extending it tests; and once the config names another folder, its tests come in.
    /// Needs no Godot: nothing runs.</summary>
    [AvaloniaFact]
    public void Where_a_gut_config_names_its_folders_the_tests_are_its_scripts_under_them()
    {
        TestFolders.Write(folder, "project.godot", "config_version=5\n\n[application]\n\nconfig/name=\"Spinner\"\nconfig/features=PackedStringArray(\"4.7\")\n");
        TestFolders.Write(folder, "addons/gut/gut_cmdln.gd", CommandLine);
        TestFolders.Write(folder, "addons/gut/test.gd", GutTest);
        TestFolders.Write(folder, GutScripts.ConfigFile, "{\n  \"dirs\": [\"res://test/unit/\"],\n  \"prefix\": \"test_\"\n}\n");
        TestFolders.Write(folder, "test/tools/spin_tester.gd", "class_name SpinTester\nextends GutTest\n\n\nfunc test_in_the_base():\n\tpass\n");
        TestFolders.Write(folder, "test/unit/test_spins.gd", "extends SpinTester\n\n\nfunc test_spins():\n\tpass\n");
        TestFolders.Write(folder, "test/unit/helpers.gd", "extends GutTest\n\n\nfunc test_helped():\n\tpass\n");
        TestFolders.Write(folder, "test/integration/test_turns.gd", "extends GutTest\n\n\nfunc test_turns():\n\tpass\n");
        TestFolders.Write(folder, "templates/test_.gd", "extends GutTest\n\n\nfunc test_template():\n\tpass\n");
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        try
        {
            window.OpenFolder(folder);
            string Shown() => string.Join(", ", window.Tests.Tree.Methods.Select(m => m.FullName));
            Ui.WaitFor(() => Method(window, "test_spins") is not null, () => $"the test under GUT's folder, extending a test class outside it ({Shown()}; {window.Tests.Tree.EmptyText})");
            Assert.True(window.Tests.Tree.Methods.Select(m => m.Name).Order(StringComparer.Ordinal).SequenceEqual(["test_spins"]),
                $"Tests listed outside GUT's folders or without its prefix: {Shown()}");

            TestFolders.Write(folder, GutScripts.ConfigFile, "{\n  \"dirs\": [\"res://test/unit/\", \"res://test/integration/\"],\n  \"prefix\": \"test_\"\n}\n");
            Ui.WaitFor(() => Method(window, "test_turns") is not null, () => $"the tests of the folder the config now names ({Shown()})");
            Assert.Null(Method(window, "test_template"));
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
