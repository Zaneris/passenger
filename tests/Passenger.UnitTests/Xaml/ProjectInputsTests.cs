using Passenger.Core;
using Passenger.Xaml.Projects;

namespace Passenger.UnitTests.Xaml;

/// <summary>A multi-targeted project's XAML is read against the restore of the framework chosen: its packages' compile
/// assets, its shared frameworks and its implicit usings; a restore or usings file it cannot read leaves it read as far
/// as it can. A path its file writes reads as MSBuild reads it, and a glob passes over build output, dependencies and
/// hidden folders.</summary>
public sealed class ProjectInputsTests : IDisposable
{
    private readonly string top = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    /// <summary>The folder of the check running, each its own (<see cref="Apart"/>).</summary>
    private string root = string.Empty;

    public void Dispose() => Directory.Delete(top, recursive: true);

    /// <summary>A check run in a folder of its own, as each was a test of its own.</summary>
    private Action Apart(Action check) => () =>
    {
        root = Directory.CreateDirectory(Path.Join(top, Guid.NewGuid().ToString("N"))).FullName;
        check();
    };

    [Fact]
    public void A_projects_inputs_are_read_for_the_framework_chosen_from_its_restore_packs_and_usings() => Cases.All(
        ("the framework chosen reads its own targets packages shared frameworks and usings", () => Cases.Each<(string, string)>(
        [
            ("net10.0", "net11.0"),
        ], row => Apart(() => The_framework_chosen_reads_its_own_targets_packages_shared_frameworks_and_usings(row.Item1, row.Item2))())),
        ("a global usings file that cannot be read leaves the project read without its implicit usings", Apart(A_global_usings_file_that_cannot_be_read_leaves_the_project_read_without_its_implicit_usings)),
        ("a restore whose members are of other kinds reads as far as it can rather than throw", Apart(A_restore_whose_members_are_of_other_kinds_reads_as_far_as_it_can_rather_than_throw)),
        ("a build is of a framework by its folder under the output alone", Apart(A_build_is_of_a_framework_by_its_folder_under_the_output_alone)));

    private void The_framework_chosen_reads_its_own_targets_packages_shared_frameworks_and_usings(string first, string second)
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

    /// <summary>A GlobalUsings.g.cs listed but not read (taken away by a clean between the two, here a link to no file; on
    /// Windows held by a build writing it) leaves the project read without its implicit usings.</summary>
    private void A_global_usings_file_that_cannot_be_read_leaves_the_project_read_without_its_implicit_usings()
    {
        string project = Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string assets = Assets("net10.0", "net11.0");
        string usings = Path.Combine(root, "App/obj/Debug/net10.0/App.GlobalUsings.g.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(usings)!);
        FileStream? held = null;
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(usings, "global using global::Ten;\n");
            held = new FileStream(usings, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        else
        {
            File.CreateSymbolicLink(usings, Path.Combine(root, "gone.cs"));
        }
        using (held)
        {
            ProjectInputs? read = null;
            Exception? thrown = Record.Exception(() => read = ProjectInputs.Read(project, null, new ProjectEvaluation(assets, null, new Dictionary<string, string>())));
            Assert.True(thrown is null, $"Reading the project threw {thrown?.GetType().Name} ({thrown?.Message}), so its XAML is never read and its preview never starts");
            Assert.True(read is { TargetFramework: "net10.0", ImplicitUsings.Count: 0 }, $"The project read as {read?.TargetFramework} with usings [{string.Join(", ", read?.ImplicitUsings ?? [])}]");
        }
    }

    private void A_restore_whose_members_are_of_other_kinds_reads_as_far_as_it_can_rather_than_throw()
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

    private void A_build_is_of_a_framework_by_its_folder_under_the_output_alone()
    {
        string bin = Path.Combine(root, "net11.0", "App", "bin");
        Assert.True(ProjectInputs.InFolderOf(Path.Combine(bin, "Debug", "net10.0", "App.dll"), "net10.0", bin));
        Assert.True(ProjectInputs.InFolderOf(Path.Combine(bin, "Debug", "NET10.0", "osx-arm64", "App.dll"), "net10.0", bin));
        Assert.True(ProjectInputs.InFolderOf(Path.Combine(bin, "debug_net10.0", "App.dll"), "net10.0", bin));
        Assert.False(ProjectInputs.InFolderOf(Path.Combine(bin, "Debug", "net10.0", "App.dll"), "net11.0", bin));
        Assert.False(ProjectInputs.InFolderOf(Path.Combine(bin, "Debug", "net10.0-windows", "App.dll"), "net10.0", bin));
    }

    [Fact]
    public void A_projects_items_and_references_are_the_files_msbuild_finds_however_written() => Cases.All(
        ("an item written escaped is the file msbuild finds", () => Cases.Each<(string, string)>(
        [
            ("per%25cent/a%3Bb.png", "per%cent/a;b.png"),
            ("per%2525cent/**", "per%cent/a;b.png"),
        ], row => Apart(() => An_item_written_escaped_is_the_file_msbuild_finds(row.Item1, row.Item2))())),
        ("a remove written escaped takes out the file it names and a property stays unread", Apart(A_remove_written_escaped_takes_out_the_file_it_names_and_a_property_stays_unread)),
        ("an items glob passes over build output dependencies and hidden folders", Apart(An_items_glob_passes_over_build_output_dependencies_and_hidden_folders)),
        ("a project referenced in two spellings of one path is one reference", Apart(A_project_referenced_in_two_spellings_of_one_path_is_one_reference)));

    private void An_item_written_escaped_is_the_file_msbuild_finds(string include, string found)
    {
        Write("App/per%cent/a;b.png", string.Empty);
        Write("App/per%cent/z", string.Empty);
        string project = Write("App/App.csproj", $"""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><AvaloniaResource Include="{include}" /></ItemGroup></Project>""");

        Assert.Contains(found, ProjectInputs.Read(project, null).Items("AvaloniaResource"));
    }

    private void A_remove_written_escaped_takes_out_the_file_it_names_and_a_property_stays_unread()
    {
        Write("App/per%cent/a.png", string.Empty);
        Write("App/per%cent/b.png", string.Empty);
        string project = Write("App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><AvaloniaResource Include="per%2525cent/**;$(Assets)/c.png" Remove="per%25cent/a.png" /></ItemGroup></Project>""");

        Assert.Equal(["per%cent/b.png"], ProjectInputs.Read(project, null).Items("AvaloniaResource"));
    }

    private void An_items_glob_passes_over_build_output_dependencies_and_hidden_folders()
    {
        foreach (string file in (string[])["App/Assets/a.png", "App/node_modules/pkg/b.png", "App/.git/c.png", "App/bin/d.png", "App/Assets/obj/e.png", "App/Assets/.cache/f.png"])
        {
            Write(file, string.Empty);
        }
        string project = Write("App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><AvaloniaResource Include="**/*.png" /></ItemGroup></Project>""");

        Assert.Equal(["Assets/a.png"], ProjectInputs.Read(project, null).Items("AvaloniaResource"));
    }

    /// <summary>The project file names its reference in one letter case, its restore in the disk's.</summary>
    private void A_project_referenced_in_two_spellings_of_one_path_is_one_reference()
    {
        Assert.SkipUnless(PathNames.CaseIgnoredIn(root), "Here lib and Lib are two folders");
        Write("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        string project = Write("App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\lib\Lib.csproj" /></ItemGroup></Project>""");
        Write("App/obj/project.assets.json", """
            {
              "version": 3,
              "targets": { "net10.0": { "Lib/1.0.0": { "type": "project" } } },
              "libraries": { "Lib/1.0.0": { "type": "project", "path": "../Lib/Lib.csproj", "msbuildProject": "../Lib/Lib.csproj" } }
            }
            """);

        Assert.Equal([Path.Combine(root, "Lib", "Lib.csproj")], ProjectInputs.Read(project, null).ProjectReferences);
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
