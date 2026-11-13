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

    /// <summary>What a project gets only through another: each package its references reach that it names nowhere, with
    /// the packages it names and the projects it references that bring it, a project another brings in passed over as a
    /// bringer of its own.</summary>
    [Fact]
    public void A_package_the_project_never_names_is_transitive_brought_in_by_the_packages_and_projects_that_reach_it()
    {
        RestoredVersions restored = Read("""
            {
              "targets": {
                "net10.0": {
                  "Vendor.Charts/2.0.0": { "type": "package", "dependencies": { "Vendor.Drawing": "1.0.0", "Json.Tiny": "3.0.0" } },
                  "Vendor.Drawing/1.0.0": { "type": "package", "dependencies": { "Json.Tiny": "3.0.0" } },
                  "Json.Tiny/3.1.0": { "type": "package" },
                  "Serilog/4.0.0": { "type": "package" },
                  "Lib/1.0.0": { "type": "project", "dependencies": { "Core": "1.0.0", "Serilog": "4.0.0" } },
                  "Core/1.0.0": { "type": "project", "dependencies": { "Polly": "8.0.0" } },
                  "Polly/8.0.0": { "type": "package" }
                }
              },
              "project": { "frameworks": { "net10.0": { "dependencies": { "Vendor.Charts": { "version": "[2.0.0, )" } } } } }
            }
            """);
        IReadOnlyDictionary<string, IReadOnlyList<TransitiveUse>> transitive = restored.Transitive();
        Assert.False(transitive.ContainsKey("Vendor.Charts"), "A package the project names was taken as brought in by another");
        Assert.Equal(["Vendor.Charts"], Assert.Single(transitive["vendor.drawing"]).BroughtBy);
        TransitiveUse json = Assert.Single(transitive["Json.Tiny"]);
        Assert.Equal(("3.1.0", (string?)null), (json.Version, json.Framework));
        Assert.Equal(["Vendor.Charts"], json.BroughtBy);
        Assert.Equal(["Lib"], Assert.Single(transitive["Serilog"]).BroughtBy);
        Assert.Equal(["Lib"], Assert.Single(transitive["Polly"]).BroughtBy);
        Assert.False(transitive.ContainsKey("Core"), "A project was taken for a package");
    }

    [Fact]
    public void Each_framework_says_its_own_transitive_version_and_one_named_in_a_framework_is_not_transitive_there()
    {
        RestoredVersions restored = Read("""
            {
              "targets": {
                "net10.0": { "Vendor.Charts/2.0.0": { "type": "package", "dependencies": { "Json.Tiny": "3.0.0" } }, "Json.Tiny/3.0.0": { "type": "package" } },
                "net11.0": { "Vendor.Charts/2.0.0": { "type": "package", "dependencies": { "Json.Tiny": "3.0.0" } }, "Json.Tiny/3.2.0": { "type": "package" } },
                "net9.0": { "Vendor.Charts/2.0.0": { "type": "package", "dependencies": { "Json.Tiny": "3.0.0" } }, "Json.Tiny/3.0.0": { "type": "package" } }
              },
              "project": { "frameworks": {
                "net10.0": { "dependencies": { "Vendor.Charts": {} } },
                "net11.0": { "dependencies": { "Vendor.Charts": {} } },
                "net9.0": { "dependencies": { "Vendor.Charts": {}, "Json.Tiny": {} } } } }
            }
            """);
        IReadOnlyList<TransitiveUse> uses = restored.Transitive()["Json.Tiny"];
        Assert.Equal([("net10.0", "3.0.0"), ("net11.0", "3.2.0")], uses.Select(u => (u.Framework, u.Version)).Order());
        Assert.Empty(RestoredVersions.None.Transitive());
    }
}
