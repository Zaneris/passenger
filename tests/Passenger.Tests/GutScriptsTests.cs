using Passenger.App.Testing;

namespace Passenger.Tests;

/// <summary>GUT's test classes as <see cref="GutScripts.Read"/> reads a project's GDScript before any run: a script
/// extending GutTest derives its class_name and res:// path as test classes, and a script extending one of those, by
/// name, by its path or by a path from its own folder, is one too; and a .gutconfig.json's choice of the scripts GUT
/// runs. Its text alone: no window.</summary>
[Collection(Beside.Name)]
public sealed class GutScriptsTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "gut-game");

    private static Passenger.App.Testing.FileTests Read(string script, string text, IReadOnlySet<string>? bases = null) =>
        GutScripts.Read(text, Folder, Path.Combine(Folder, script.Replace('/', Path.DirectorySeparatorChar)), bases).Tests;

    private static readonly HashSet<string> Bases = new(StringComparer.Ordinal) { "BaseTest", "res://test/base_test.gd" };

    /// <summary>A GUT script derives its test classes and tests, and a config its scripts.</summary>
    [Fact]
    public void Gut_scripts_derive_their_tests_and_configs()
    {
        Flow.Step(nameof(A_script_extending_gut_test_derives_its_class_name_and_path_as_test_classes),
            A_script_extending_gut_test_derives_its_class_name_and_path_as_test_classes);
        Flow.Step(nameof(A_script_extending_the_projects_own_test_class_by_name_or_path_holds_tests_and_derives_its_own),
            A_script_extending_the_projects_own_test_class_by_name_or_path_holds_tests_and_derives_its_own);
        Flow.Step(nameof(A_gut_config_takes_the_scripts_under_its_folders_with_its_prefix_and_suffix_and_those_it_names),
            A_gut_config_takes_the_scripts_under_its_folders_with_its_prefix_and_suffix_and_those_it_names);
    }

    private void A_script_extending_gut_test_derives_its_class_name_and_path_as_test_classes()
    {
        Passenger.App.Testing.FileTests read = Read("test/base_test.gd", "class_name BaseTest\nextends GutTest\n\n\nfunc helper():\n\tpass\n");
        Assert.Equal(["BaseTest", "res://test/base_test.gd"], read.Attributes.Order(StringComparer.Ordinal));
        Assert.Empty(read.Tests);
        Assert.Equal(["res://test/plain.gd", GutBases.Entry("res://test/plain.gd", "test_adds", Path.Combine(Folder, "test", "plain.gd"))], Read("test/plain.gd", "extends 'res://addons/gut/test.gd'\n\n\nfunc test_adds():\n\tpass\n").Attributes.Order(StringComparer.Ordinal));
        Assert.Empty(Read("test/node.gd", "class_name Spinner\nextends Node\n\n\nfunc test_spins():\n\tpass\n").Attributes);
    }

    private void A_script_extending_the_projects_own_test_class_by_name_or_path_holds_tests_and_derives_its_own()
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

    private void A_gut_config_takes_the_scripts_under_its_folders_with_its_prefix_and_suffix_and_those_it_names()
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
}
