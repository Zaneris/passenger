using Passenger.App.Testing;

namespace Passenger.Tests;

/// <summary>GUT's test classes as <see cref="GutScripts.Read"/> reads a project's GDScript before any run: a script
/// extending GutTest derives its class_name and res:// path as test classes, and a script or inner class extending one of
/// those, by name, by its path or by a path from its own folder, is one too. Its text alone: no window.</summary>
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
        Assert.Equal(["res://test/plain.gd"], Read("test/plain.gd", "extends 'res://addons/gut/test.gd'\n\n\nfunc test_adds():\n\tpass\n").Attributes.Order(StringComparer.Ordinal));
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
}
