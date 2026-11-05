using Passenger.App.Testing;

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

    /// <summary>The tree keeps a runner's test of a file only where the file's methods name it: left out, the row shows and
    /// nothing runs it, and a run of its script takes it out of what GUT runs.</summary>
    [Theory]
    [InlineData("extends SpinTester\n\n\nfunc test_own():\n\tpass\n")]
    [InlineData("extends SpinTester\n")]
    [InlineData("extends \"res://tools/spin_tester.gd\"\n")]
    [InlineData("extends GutTest\n\n\nclass TestSpins:\n\textends SpinTester\n")]
    [InlineData("extends GutTest\n\n\nclass TestSpins extends SpinTester:\n\tfunc test_own():\n\t\tpass\n")]
    public void Every_test_a_script_tells_is_among_the_methods_its_file_has(string text)
    {
        FileTests read = Read(text);
        Assert.Contains(read.Tests, t => t.Method == "test_inherited");
        Assert.All(read.Tests, t => Assert.Contains(t.Method, read.Methods));
    }

    [Fact]
    public void An_inherited_test_is_the_scripts_that_runs_it_on_the_line_of_its_class()
    {
        DeclaredTest test = Assert.Single(Read("# A spinner's tests.\nextends SpinTester\n").Tests);
        Assert.Equal(("test.test_spinner", "test_inherited", 1), (test.Type, test.Method, test.Line));
    }

    /// <summary>A test's code and the line GUT gives of a failure are the declaring script's, so a test inherited has to say it.</summary>
    [Fact]
    public void An_inherited_test_says_the_script_declaring_it_and_one_declared_again_is_the_scripts_own()
    {
        Assert.Equal(Base, Assert.Single(Read("extends SpinTester\n").Tests).DeclaredIn);
        Assert.Equal(Base, Assert.Single(Read("extends GutTest\n\n\nclass TestSpins:\n\textends SpinTester\n").Tests).DeclaredIn);
        Assert.Null(Assert.Single(Read("extends SpinTester\n\n\nfunc test_inherited():\n\tpass\n").Tests).DeclaredIn);
    }

    [Fact]
    public void An_inner_class_runs_what_it_inherits_under_its_own_name()
    {
        DeclaredTest test = Assert.Single(Read("extends GutTest\n\n\nclass TestSpins:\n\textends SpinTester\n").Tests);
        Assert.Equal(("test.test_spinner+TestSpins", 3), (test.Type, test.Line));
    }

    [Fact]
    public void A_test_a_script_declares_again_is_its_own_once_on_its_own_line()
    {
        DeclaredTest test = Assert.Single(Read("extends SpinTester\n\n\nfunc test_inherited():\n\tpass\n").Tests);
        Assert.Equal(3, test.Line);
    }

    [Fact]
    public void A_script_of_no_test_class_inherits_no_tests()
    {
        Assert.Empty(Read("extends Node\n").Tests);
    }

    /// <summary>Each level of a chain passes on what it has, so the project's reads come to every test however deep.</summary>
    [Fact]
    public void A_script_passes_on_what_it_inherits_beside_what_it_declares_by_its_name_and_path()
    {
        FileTests read = Read("class_name TurnTester\nextends SpinTester\n\n\nfunc test_turns():\n\tpass\n", "turn_tester.gd");
        Assert.Equal([("test_inherited", Base), ("test_turns", Script("turn_tester.gd"))], GutBases.TestsOf(read.Attributes, "TurnTester"));
        Assert.Equal([("test_inherited", Base), ("test_turns", Script("turn_tester.gd"))], GutBases.TestsOf(read.Attributes, "res://test/turn_tester.gd"));
    }

    /// <summary>A base outside the folders GUT runs holds no tests of its own there, and still passes them on.</summary>
    [Fact]
    public void A_script_outside_guts_folders_tells_no_tests_and_passes_them_on_all_the_same()
    {
        GutScripts.Config config = new(["res://elsewhere/"], false, "test_", ".gd", []);
        FileTests read = Read("class_name TurnTester\nextends SpinTester\n\n\nfunc test_turns():\n\tpass\n", "turn_tester.gd", config);
        Assert.Empty(read.Tests);
        Assert.Equal(["test_inherited", "test_turns"], GutBases.TestsOf(read.Attributes, "TurnTester").Select(t => t.Method));
    }

    /// <summary>A row of a test inherited stands on the code its base passes on, though the base tells no test of its own there.</summary>
    [Fact]
    public void A_script_keeps_the_code_of_each_test_it_declares_for_those_extending_it_outside_guts_folders_too()
    {
        GutScripts.Config elsewhere = new(["res://elsewhere/"], false, "test_", ".gd", []);
        string Written(string said) => $"class_name TurnTester\nextends SpinTester\n\n\nfunc test_turns():\n\tassert_eq(1, {said})\n";
        FileTests read = Read(Written("1"), "turn_tester.gd", elsewhere);
        Assert.Equal(["test_turns"], read.PassedOn.Keys);
        Assert.Equal(Assert.Single(Read(Written("1"), "turn_tester.gd").Tests, t => t.Method == "test_turns").Code, read.PassedOn["test_turns"]);
        Assert.True(read.PassedOn["test_turns"] != Read(Written("2"), "turn_tester.gd", elsewhere).PassedOn["test_turns"],
            "A change to a test's body left the code it passes on as it was, so the rows of the scripts running it never say their results are old");
        Assert.True(read.SameAs(Read(Written("2"), "turn_tester.gd", elsewhere)), "Typing in a test's body reads as a change of what the file declares, so each key has the project's scripts read again");
    }

    [Fact]
    public void A_function_declared_twice_is_passed_on_once_rather_than_ending_the_read()
    {
        FileTests read = Read("class_name TurnTester\nextends GutTest\n\n\nfunc test_turns():\n\tpass\n\n\nfunc test_turns():\n\tpass\n", "turn_tester.gd");
        Assert.Equal(["test_turns"], read.PassedOn.Keys);
    }
}
