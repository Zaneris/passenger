using Passenger.App.Testing;
using Passenger.Workspace;

namespace Passenger.UnitTests.Godot;

/// <summary>What a project's GDScript declares for GUT, as each script's read gives it from its text and the test classes
/// the project's other scripts passed on.</summary>
public sealed class GutScriptsTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "game");

    private static string Script(string name) => Path.Combine(Folder, "test", name);

    private static readonly string Base = Path.Combine(Folder, "tools", "spin_tester.gd");

    /// <summary>A base outside the folder GUT's config names, as its class and by its path, with one test of its own.</summary>
    private static readonly HashSet<string> Bases =
        ["SpinTester", "res://tools/spin_tester.gd", GutBases.Entry("SpinTester", "test_inherited", Base), GutBases.Entry("res://tools/spin_tester.gd", "test_inherited", Base)];

    private static FileTests Read(string text, string name = "test_spinner.gd", GutScripts.Config? config = null) =>
        GutScripts.Read(text, Folder, Script(name), Bases, config).Tests;

    /// <summary>A script's GUT tests are those it declares and inherits, each on its line.</summary>
    [Fact]
    public void A_scripts_gut_tests_are_those_its_file_and_bases_declare() => Cases.All(
        (nameof(Every_test_a_script_tells_is_among_the_methods_its_file_has), Every_test_a_script_tells_is_among_the_methods_its_file_has),
        (nameof(An_inherited_test_is_the_scripts_that_runs_it_on_the_line_of_its_class),
            An_inherited_test_is_the_scripts_that_runs_it_on_the_line_of_its_class),
        (nameof(An_inner_class_runs_what_it_inherits_under_its_own_name), An_inner_class_runs_what_it_inherits_under_its_own_name),
        (nameof(A_script_passes_on_what_it_inherits_beside_what_it_declares_by_its_name_and_path),
            A_script_passes_on_what_it_inherits_beside_what_it_declares_by_its_name_and_path),
        (nameof(A_script_outside_guts_folders_tells_no_tests_and_passes_them_on_all_the_same),
            A_script_outside_guts_folders_tells_no_tests_and_passes_them_on_all_the_same),
        (nameof(A_function_declared_twice_is_passed_on_once_rather_than_ending_the_read),
            A_function_declared_twice_is_passed_on_once_rather_than_ending_the_read));

    /// <summary>The tree keeps a runner's test of a file only where the file's methods name it: left out, the row shows and
    /// nothing runs it, and a run of its script takes it out of what GUT runs.</summary>
    private void Every_test_a_script_tells_is_among_the_methods_its_file_has() => Cases.Each<string>(
    [
        "extends SpinTester\n\n\nfunc test_own():\n\tpass\n",
        "extends SpinTester\n",
        "extends GutTest\n\n\nclass TestSpins:\n\textends SpinTester\n",
        "extends GutTest\n\n\nclass TestSpins extends SpinTester:\n\tfunc test_own():\n\t\tpass\n",
    ], Every_test_a_script_tells_is_among_the_methods_its_file_has);

    private void Every_test_a_script_tells_is_among_the_methods_its_file_has(string text)
    {
        FileTests read = Read(text);
        Assert.Contains(read.Tests, t => t.Method == "test_inherited");
        Assert.All(read.Tests, t => Assert.Contains(t.Method, read.Methods));
    }

    private void An_inherited_test_is_the_scripts_that_runs_it_on_the_line_of_its_class()
    {
        DeclaredTest test = Assert.Single(Read("# A spinner's tests.\nextends SpinTester\n").Tests);
        Assert.Equal(("test.test_spinner", "test_inherited", 1), (test.Type, test.Method, test.Line));
    }

    private void An_inner_class_runs_what_it_inherits_under_its_own_name()
    {
        DeclaredTest test = Assert.Single(Read("extends GutTest\n\n\nclass TestSpins:\n\textends SpinTester\n").Tests);
        Assert.Equal(("test.test_spinner+TestSpins", 3), (test.Type, test.Line));
    }

    /// <summary>Each level of a chain passes on what it has, so the project's reads come to every test however deep.</summary>
    private void A_script_passes_on_what_it_inherits_beside_what_it_declares_by_its_name_and_path()
    {
        FileTests read = Read("class_name TurnTester\nextends SpinTester\n\n\nfunc test_turns():\n\tpass\n", "turn_tester.gd");
        Assert.Equal([("test_inherited", Base), ("test_turns", Script("turn_tester.gd"))], GutBases.TestsOf(read.Attributes, "TurnTester"));
        Assert.Equal([("test_inherited", Base), ("test_turns", Script("turn_tester.gd"))], GutBases.TestsOf(read.Attributes, "res://test/turn_tester.gd"));
    }

    /// <summary>A base outside the folders GUT runs holds no tests of its own there, and still passes them on.</summary>
    private void A_script_outside_guts_folders_tells_no_tests_and_passes_them_on_all_the_same()
    {
        GutScripts.Config config = new(["res://elsewhere/"], false, "test_", GodotFileKinds.Script, []);
        FileTests read = Read("class_name TurnTester\nextends SpinTester\n\n\nfunc test_turns():\n\tpass\n", "turn_tester.gd", config);
        Assert.Empty(read.Tests);
        Assert.Equal(["test_inherited", "test_turns"], GutBases.TestsOf(read.Attributes, "TurnTester").Select(t => t.Method));
    }

    private void A_function_declared_twice_is_passed_on_once_rather_than_ending_the_read()
    {
        FileTests read = Read("class_name TurnTester\nextends GutTest\n\n\nfunc test_turns():\n\tpass\n\n\nfunc test_turns():\n\tpass\n", "turn_tester.gd");
        Assert.Equal(["test_turns"], read.PassedOn.Keys);
    }
}
