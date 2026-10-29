using System.Text;
using Passenger.Packages;

namespace Passenger.UnitTests.Packages;

/// <summary>The versions a project's restore resolved, read from its project.assets.json by the framework names the project
/// gives, as the NuGet window shows a range's version in force.</summary>
public class RestoredVersionsTests
{
    private static RestoredVersions Read(string json) => RestoredVersions.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

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
