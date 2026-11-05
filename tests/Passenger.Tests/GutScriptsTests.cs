using Passenger.App.Testing;

namespace Passenger.Tests;

/// <summary>GUT's test classes as <see cref="GutScripts.Read"/> reads a project's GDScript before any run: a script
/// extending GutTest derives its class_name and res:// path as test classes, and a script or inner class extending one of
/// those, by name, by its path or by a path from its own folder, is one too; and a .gutconfig.json's choice of the scripts
/// GUT runs. Its text alone: no window.</summary>
[Collection(Beside.Name)]
public sealed class GutScriptsTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "gut-game");

    private static Passenger.App.Testing.FileTests Read(string script, string text, IReadOnlySet<string>? bases = null) =>
        GutScripts.Read(text, Folder, Path.Combine(Folder, script.Replace('/', Path.DirectorySeparatorChar)), bases).Tests;

    private static readonly HashSet<string> Bases = new(StringComparer.Ordinal) { "BaseTest", "res://test/base_test.gd" };

    [Fact]
    public void A_script_extending_gut_test_derives_its_class_name_and_path_as_test_classes()
    {
        Passenger.App.Testing.FileTests read = Read("test/base_test.gd", "class_name BaseTest\nextends GutTest\n\n\nfunc helper():\n\tpass\n");
        Assert.Equal(["BaseTest", "res://test/base_test.gd"], read.Attributes.Order(StringComparer.Ordinal));
        Assert.Empty(read.Tests);
        Assert.Equal(["res://test/plain.gd", GutBases.Entry("res://test/plain.gd", "test_adds", Path.Combine(Folder, "test", "plain.gd"))], Read("test/plain.gd", "extends 'res://addons/gut/test.gd'\n\n\nfunc test_adds():\n\tpass\n").Attributes.Order(StringComparer.Ordinal));
        Assert.Empty(Read("test/node.gd", "class_name Spinner\nextends Node\n\n\nfunc test_spins():\n\tpass\n").Attributes);
    }

    [Fact]
    public void A_script_extending_the_projects_own_test_class_by_name_or_path_holds_tests_and_derives_its_own()
    {
        foreach (string extends in new[] { "extends BaseTest", "extends \"res://test/base_test.gd\"", "extends 'base_test.gd'", "extends \"../test/base_test.gd\"" })
        {
            string script = extends.Contains("../", StringComparison.Ordinal) ? "unit/test_math.gd" : "test/test_math.gd";
            Passenger.App.Testing.FileTests read = Read(script, $"{extends}\n\n\nfunc test_adds():\n\tpass\n", Bases);
            Assert.True(read.Tests.Count == 1 && read.Tests[0].Method == "test_adds", $"{extends} in {script} is no test class: {read.Tests.Count} tests");
            Assert.Contains($"res://{script}", read.Attributes);
            Assert.Empty(Read(script, $"{extends}\n\n\nfunc test_adds():\n\tpass\n").Tests);
        }
    }

    [Fact]
    public void A_class_name_and_extends_on_one_line_are_read_either_way_round()
    {
        Passenger.App.Testing.FileTests named = Read("test/test_math.gd", "class_name MathTest extends BaseTest\n\n\nfunc test_adds():\n\tpass\n", Bases);
        Assert.Single(named.Tests);
        Assert.Contains("MathTest", named.Attributes);
        Passenger.App.Testing.FileTests extended = Read("test/test_sums.gd", "extends GutTest class_name SumsTest\n\n\nfunc test_sums():\n\tpass\n");
        Assert.Single(extended.Tests);
        Assert.Contains("SumsTest", extended.Attributes);
    }

    [Fact]
    public void An_inner_class_extending_the_projects_own_test_class_holds_tests_of_its_own()
    {
        Passenger.App.Testing.FileTests read = Read("test/test_misc.gd", "extends GutTest\n\n\nclass TestOutput:\n\textends BaseTest\n\n\tfunc test_prints():\n\t\tpass\n", Bases);
        Assert.Equal(["test.test_misc+TestOutput.test_prints"], read.Tests.Select(t => $"{t.Type}.{t.Method}"));
    }

    [Fact]
    public void A_gut_config_takes_the_scripts_under_its_folders_with_its_prefix_and_suffix_and_those_it_names()
    {
        GutScripts.Config config = GutScripts.Config.Read("""
            {
              // GUT's own repository's, trimmed
              "dirs": ["res://test/unit", "res://test/integration/"],
              "prefix": "test_",
              "tests": ["res://scenes/check_scene.gd"],
            }
            """)!;
        Assert.True(config.Takes(Folder, Path.Combine(Folder, "test", "unit", "test_math.gd")));
        Assert.True(config.Takes(Folder, Path.Combine(Folder, "test", "integration", "test_scene.gd")));
        Assert.True(config.Takes(Folder, Path.Combine(Folder, "scenes", "check_scene.gd")));
        Assert.False(config.Takes(Folder, Path.Combine(Folder, "test", "unit", "helpers.gd")), "A script without GUT's prefix was taken");
        Assert.False(config.Takes(Folder, Path.Combine(Folder, "test", "unit", "deep", "test_deep.gd")), "A subfolder's script was taken without include_subdirs");
        Assert.False(config.Takes(Folder, Path.Combine(Folder, "templates", "test_.gd")), "A script outside GUT's folders was taken");
        Assert.True(GutScripts.Config.Read("{\"dirs\": [\"res://test/\"], \"include_subdirs\": true, \"prefix\": \"spec_\", \"suffix\": \"_spec.gd\"}")!
            .Takes(Folder, Path.Combine(Folder, "test", "deep", "spec_math_spec.gd")));
        Assert.Null(GutScripts.Config.Read("{\"log_level\": 1}"));
        Assert.Null(GutScripts.Config.Read("not json"));
    }

    [Fact]
    public void A_script_the_config_does_not_take_holds_no_tests_and_still_derives_its_test_class()
    {
        GutScripts.Config config = GutScripts.Config.Read("{\"dirs\": [\"res://test/unit/\"]}")!;
        Passenger.App.Testing.FileTests outside = GutScripts.Read("class_name BaseTest\nextends GutTest\n\n\nfunc test_in_the_base():\n\tpass\n", Folder,
            Path.Combine(Folder, "test", "tools", "base_test.gd"), config: config).Tests;
        Assert.Empty(outside.Tests);
        Assert.Contains("BaseTest", outside.Attributes);
        Passenger.App.Testing.FileTests inside = GutScripts.Read("extends BaseTest\n\n\nfunc test_adds():\n\tpass\n", Folder,
            Path.Combine(Folder, "test", "unit", "test_math.gd"), Bases, config).Tests;
        Assert.Single(inside.Tests);
    }

    [Fact]
    public void A_gut_configs_pre_run_script_is_read_and_a_text_naming_none_says_so()
    {
        Assert.Equal("res://hooks/pre.gd", GutScripts.PreRunScript("{\n  // the project's own\n  \"pre_run_script\": \"res://hooks/pre.gd\",\n}\n"));
        Assert.Null(GutScripts.PreRunScript("{\"pre_run_script\": \"\"}"));
        Assert.Null(GutScripts.PreRunScript("{\"dirs\": [\"res://test/\"]}"));
        Assert.Null(GutScripts.PreRunScript("not json"));
    }

    /// <summary>The whole of a function goes into what its result is compared by: its last line, and an inner class's too.</summary>
    [Fact]
    public void A_tests_code_changes_with_a_literal_on_its_last_line_and_not_with_the_tests_around_it()
    {
        ulong Code(string text, string method) => Read("test/test_math.gd", text).Tests.Single(t => t.Method == method).Code;
        const string Script = "extends GutTest\n\n\nfunc test_adds():\n\tprepare()\n\tassert_eq(got, \"a b\")\n\n\nfunc test_turns():\n\tassert_eq(turns, 2)\n";
        Assert.True(Code(Script, "test_adds") != Code(Script.Replace("a b", "ab", StringComparison.Ordinal), "test_adds"), "A test whose expected text lost a space reads as unchanged, so its old result stands");
        Assert.Equal(Code(Script, "test_turns"), Code(Script.Replace("a b", "ab", StringComparison.Ordinal), "test_turns"));
    }

    private const string Failed = """
        <testsuites><testsuite name="test/test_spinner.gd">
          <testcase name="test_turns" status="fail" time="0"><failure><![CDATA[[1] expected to equal [2]:
              at line 9]]></failure></testcase>
        </testsuite></testsuites>
        """;

    /// <summary>GUT counts a failure's line in the function that failed, so its link goes to the script declaring that: the
    /// deriving script's line of that number is any other code, or past its end.</summary>
    [Fact]
    public void A_failure_of_a_test_a_script_inherits_links_the_script_declaring_it()
    {
        string spinner = Path.Combine(Folder, "test", "test_spinner.gd");
        string tester = Path.Combine(Folder, "tools", "spin_tester.gd");
        TestItem inherited = new(Path.Combine(Folder, "project.godot"), "test.test_spinner.test_turns", "test_turns") { Path = spinner, DeclaredIn = tester, Line = 1 };
        TestOutcome told = Assert.Single(GutRunner.Outcomes(Failed, inherited.Project, Folder, [inherited], string.Empty));
        Assert.Equal($"   at test.test_spinner.test_turns() in {tester}:line 9", told.StackTrace);
        TestOutcome own = Assert.Single(GutRunner.Outcomes(Failed, inherited.Project, Folder, [inherited with { DeclaredIn = null }], string.Empty));
        Assert.Equal($"   at test.test_spinner.test_turns() in {spinner}:line 9", own.StackTrace);
    }
}
