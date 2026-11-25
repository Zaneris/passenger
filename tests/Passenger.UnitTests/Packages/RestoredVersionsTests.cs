using System.Text;
using Passenger.Packages;

namespace Passenger.UnitTests.Packages;

/// <summary>The versions a project's restore resolved, read from its project.assets.json by the framework names the project
/// gives, as the NuGet window shows a range's version in force.</summary>
public class RestoredVersionsTests
{
    private static RestoredVersions Read(string json) => RestoredVersions.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void A_restores_versions_are_read_by_the_frameworks_the_project_names_with_what_it_gets_only_through_others() => Cases.All(
        ("a restore holds a package of any of its frameworks whatever brought it", () =>
        {
            RestoredVersions restored = Read("""
                { "targets": {
                    "net10.0": { "Vendor.Controls/1.0.0": { "type": "package", "dependencies": { "Avalonia": "12.1.5" } }, "Avalonia/12.1.5": { "type": "package" } },
                    "net11.0": { "Serilog/4.0.0": { "type": "package" }, "Shared/1.0.0": { "type": "project" } } } }
                """);
            Assert.True(restored.Holds("avalonia"), "A package only another package brings was not found in the restore");
            Assert.True(restored.Holds("Serilog"), "A package of the second framework was not found in the restore");
            Assert.False(restored.Holds("Shared"), "A project was taken for a package");
        }),
        ("each frameworks packages are read by the name the project gives it, a runtime's targets and projects passed over", () =>
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
        }),
        ("a file that is no assets file says nothing", () =>
        {
            Assert.Null(Read("{ \"targets\": ").Of(null, "Passenger.Sample"));
            Assert.Null(Read("{ \"targets\": [] }").Of(null, "Passenger.Sample"));
        }),
        ("a package the project never names is transitive brought in by the packages and projects that reach it", () =>
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
        }),
        ("each framework says its own transitive version and one named in a framework is not transitive there", () =>
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
        }),
        ("a package's dependencies are each framework's, with the range asked and the version resolved, a project among them left out", () =>
        {
            RestoredVersions restored = Read("""
                {
                  "targets": {
                    "net10.0": {
                      "Vendor.Charts/2.0.0": { "type": "package", "dependencies": { "Json.Tiny": "3.0.0", "Vendor.Drawing": "[1.0.0]", "Lib": "1.0.0" } },
                      "Vendor.Drawing/1.0.0": { "type": "package" },
                      "Json.Tiny/3.1.0": { "type": "package" },
                      "Lib/1.0.0": { "type": "project" }
                    },
                    "net11.0": { "Vendor.Charts/2.0.0": { "type": "package", "dependencies": { "Json.Tiny": "[3.0.0, )" } }, "Json.Tiny/3.0.0": { "type": "package" } }
                  }
                }
                """);
            Assert.Equal([new RestoredDependency("Json.Tiny", "3.0.0", "3.1.0"), new RestoredDependency("Vendor.Drawing", "[1.0.0]", "1.0.0")],
                restored.DependenciesOf("net10.0", "vendor.charts"));
            RestoredDependency newer = restored.DependenciesOf("net10.0", "Vendor.Charts")[0];
            Assert.True(newer is { Differs: true, AskedSaid: "3.0.0 or later", AskedBriefly: "≥ 3.0.0" }, $"Json.Tiny 3.1.0 for 3.0.0 asked is said {newer.Differs}, {newer.AskedSaid}, {newer.AskedBriefly}");
            RestoredDependency pinned = restored.DependenciesOf("net10.0", "Vendor.Charts")[1];
            Assert.True(pinned is { Differs: false, AskedSaid: "exactly 1.0.0", AskedBriefly: "= 1.0.0" }, $"Vendor.Drawing pinned and resolved at 1.0.0 is said {pinned.Differs}, {pinned.AskedSaid}, {pinned.AskedBriefly}");
            RestoredDependency own = Assert.Single(restored.DependenciesOf("net11.0", "Vendor.Charts"));
            Assert.True(own is { Version: "3.0.0", Differs: false, AskedSaid: "3.0.0 or later" }, $"net11.0's Json.Tiny is {own}");
            Assert.Empty(restored.DependenciesOf("net10.0", "Json.Tiny"));
            Assert.Empty(restored.DependenciesOf("net10.0", "Missing"));
        }));

}
