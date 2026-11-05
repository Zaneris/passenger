using System.Text;
using Passenger.Packages;

namespace Passenger.UnitTests.Packages;

/// <summary>The versions a project's restore resolved, read from its project.assets.json by the framework names the project
/// gives, as the NuGet window shows a range's version in force.</summary>
public class RestoredVersionsTests
{
    private static RestoredVersions Read(string json) => RestoredVersions.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    /// <summary>A restore lists what a package brings beside what the project names, in any of its frameworks.</summary>
    [Fact]
    public void A_restore_holds_a_package_of_any_of_its_frameworks_whatever_brought_it()
    {
        RestoredVersions restored = Read("""
            { "targets": {
                "net10.0": { "Vendor.Controls/1.0.0": { "type": "package", "dependencies": { "Avalonia": "12.1.5" } }, "Avalonia/12.1.5": { "type": "package" } },
                "net11.0": { "Serilog/4.0.0": { "type": "package" }, "Shared/1.0.0": { "type": "project" } } } }
            """);
        Assert.True(restored.Holds("avalonia"), "A package only another package brings was not found in the restore");
        Assert.True(restored.Holds("Serilog"), "A package of the second framework was not found in the restore");
        Assert.False(restored.Holds("Shared"), "A project was taken for a package");
        Assert.False(RestoredVersions.None.Holds("Avalonia"), "Nothing restored holds a package");
    }

    [Fact]
    public void Each_frameworks_packages_are_read_by_the_name_the_project_gives_it_a_runtimes_targets_and_projects_passed_over()
    {
        RestoredVersions restored = Read("""
            {
              "version": 4,
              "targets": {
                "net10.0": { "Passenger.Sample/1.0.0": { "type": "package" }, "Lib/1.0.0": { "type": "project" } },
                "net10.0/osx-arm64": { "Passenger.Sample/9.9.9": { "type": "package" } },
                "net11.0-windows7.0": { "Passenger.Sample/1.1.0": { "type": "package" } }
              },
              "project": { "frameworks": { "net10.0": { "targetAlias": "net10.0" }, "net11.0-windows7.0": { "targetAlias": "net11.0-windows" } } }
            }
            """);
        Assert.Equal("1.0.0", restored.Of("net10.0", "passenger.sample"));
        Assert.Equal("1.1.0", restored.Of("net11.0-windows", "Passenger.Sample"));
        Assert.Null(restored.Of("net10.0", "Lib"));
        Assert.Null(restored.Of(null, "Passenger.Sample"));
    }

    [Fact]
    public void A_project_of_one_framework_names_none_and_finds_its_one_targets_packages()
    {
        RestoredVersions restored = Read("""{ "targets": { "net10.0": { "Passenger.Sample/1.0.0": { "type": "package" } } } }""");
        Assert.Equal("1.0.0", restored.Of(null, "Passenger.Sample"));
        Assert.Null(restored.Of(null, "Passenger.Other"));
    }

    [Fact]
    public void A_file_that_is_no_assets_file_says_nothing()
    {
        Assert.Null(Read("{ \"targets\": ").Of(null, "Passenger.Sample"));
        Assert.Null(Read("[]").Of(null, "Passenger.Sample"));
        Assert.Null(Read("{ \"targets\": [] }").Of(null, "Passenger.Sample"));
    }
}
