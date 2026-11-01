using Passenger.Core;
using Passenger.Xaml.Projects;

namespace Passenger.UnitTests.Xaml;

/// <summary>A multi-targeted project's XAML is read against the restore of the framework chosen: its packages' compile
/// assets, its shared frameworks and its implicit usings, wherever the restore names its targets by their long names; with
/// none chosen, or one the restore lacks, against its first target, as a project of one framework is.</summary>
public sealed class ProjectInputsTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData("net10.0", "net11.0")]
    [InlineData(".NETCoreApp,Version=v10.0", ".NETCoreApp,Version=v11.0")]
    public void The_framework_chosen_reads_its_own_targets_packages_shared_frameworks_and_usings(string first, string second)
    {
        string project = Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string assets = Assets(first, second);
        Write("App/obj/Debug/net10.0/App.GlobalUsings.g.cs", "global using global::Ten;\n");
        Write("App/obj/Debug/net11.0/App.GlobalUsings.g.cs", "global using global::Eleven;\n");

        ProjectInputs eleven = ProjectInputs.Read(project, null, new ProjectEvaluation(assets, null, new Dictionary<string, string>()) { TargetFramework = "net11.0" });
        Assert.Equal("net11.0", eleven.TargetFramework);
        Assert.Equal("net11.0", eleven.ChosenFramework);
        Assert.Contains(eleven.References, r => r.EndsWith("Eleven.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(eleven.References, r => r.EndsWith("Ten.dll", StringComparison.Ordinal));
        Assert.Equal("2.0.0", eleven.PackageVersions["Pack"]);
        Assert.Equal([FrameworkNames.Wpf], eleven.FrameworkReferences);
        Assert.Equal(["Eleven"], eleven.ImplicitUsings);

        ProjectInputs unchosen = ProjectInputs.Read(project, null, new ProjectEvaluation(assets, null, new Dictionary<string, string>()));
        Assert.Equal("net10.0", unchosen.TargetFramework);
        Assert.Null(unchosen.ChosenFramework);
        Assert.Contains(unchosen.References, r => r.EndsWith("Ten.dll", StringComparison.Ordinal));
        Assert.Equal("1.0.0", unchosen.PackageVersions["Pack"]);
        Assert.Equal(["Microsoft.NETCore.App"], unchosen.FrameworkReferences);
        Assert.Equal(["Ten"], unchosen.ImplicitUsings);

        ProjectInputs missing = ProjectInputs.Read(project, null, new ProjectEvaluation(assets, null, new Dictionary<string, string>()) { TargetFramework = "net12.0" });
        Assert.Equal("net10.0", missing.TargetFramework);
    }

    [Fact]
    public void A_restore_whose_members_are_of_other_kinds_reads_as_far_as_it_can_rather_than_throw()
    {
        string project = Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string assets = Write("App/obj/project.assets.json", """
            { "targets": { "net10.0": { "Pack/1.0.0": { "type": 3, "compile": [] } } }, "libraries": { "Pack/1.0.0": { "path": 7 } },
              "packageFolders": [], "project": { "frameworks": { "net10.0": { "frameworkReferences": "none", "downloadDependencies": {} } } } }
            """);
        ProjectInputs inputs = ProjectInputs.Read(project, null, new ProjectEvaluation(assets, null, new Dictionary<string, string>()));
        Assert.Equal("net10.0", inputs.TargetFramework);
        Assert.Equal("1.0.0", inputs.PackageVersions["Pack"]);
        Assert.Empty(inputs.FrameworkReferences);
    }

    [Fact]
    public void A_build_is_of_a_framework_by_its_folder_under_the_output_alone()
    {
        string bin = Path.Combine(root, "net11.0", "App", "bin");
        Assert.True(ProjectInputs.InFolderOf(Path.Combine(bin, "Debug", "net10.0", "App.dll"), "net10.0", bin));
        Assert.True(ProjectInputs.InFolderOf(Path.Combine(bin, "Debug", "NET10.0", "osx-arm64", "App.dll"), "net10.0", bin));
        Assert.True(ProjectInputs.InFolderOf(Path.Combine(bin, "debug_net10.0", "App.dll"), "net10.0", bin));
        Assert.False(ProjectInputs.InFolderOf(Path.Combine(bin, "Debug", "net10.0", "App.dll"), "net11.0", bin));
        Assert.False(ProjectInputs.InFolderOf(Path.Combine(bin, "Debug", "net10.0-windows", "App.dll"), "net10.0", bin));
    }

    /// <summary>A restore of two frameworks, each with its own version of a package and its own shared framework.</summary>
    private string Assets(string first, string second)
    {
        string cache = Path.Combine(root, "packages");
        Write("packages/pack/1.0.0/lib/net10.0/Ten.dll", string.Empty);
        Write("packages/pack/2.0.0/lib/net11.0/Eleven.dll", string.Empty);
        string folders = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { [cache] = new { } });
        return Write("App/obj/project.assets.json", $$"""
            {
              "version": 3,
              "targets": {
                "{{first}}": { "Pack/1.0.0": { "type": "package", "compile": { "lib/net10.0/Ten.dll": {} } } },
                "{{second}}": { "Pack/2.0.0": { "type": "package", "compile": { "lib/net11.0/Eleven.dll": {} } } },
                "{{second}}/osx-arm64": { "Pack/2.0.0": { "type": "package" } }
              },
              "libraries": {
                "Pack/1.0.0": { "type": "package", "path": "pack/1.0.0" },
                "Pack/2.0.0": { "type": "package", "path": "pack/2.0.0" }
              },
              "packageFolders": {{folders}},
              "project": {
                "frameworks": {
                  "net10.0": { "targetAlias": "net10.0", "frameworkReferences": { "Microsoft.NETCore.App": {} } },
                  "net11.0": { "targetAlias": "net11.0", "frameworkReferences": { "{{FrameworkNames.Wpf}}": {} } }
                }
              }
            }
            """);
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
}
