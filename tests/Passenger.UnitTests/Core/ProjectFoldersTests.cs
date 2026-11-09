using Passenger.Core;
using Passenger.Workspace;
using Passenger.Xaml.Localization;

namespace Passenger.UnitTests.Core;

/// <summary>A project's own folders are found one way, for its C# and XAML files and its resources alike: its build output,
/// dependencies, hidden folders and another project's passed over, loaded or not, a build folder's name in another letter
/// case as its folder compares names; and the walk of the open folder passes over build output by the same reading.</summary>
public sealed class ProjectFoldersTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_projects_own_folders_pass_over_build_output_dependencies_hidden_folders_and_another_projects()
    {
        string project = Path.Join(root, "Shop");
        foreach (string folder in (string[])["Pages/Admin", "bin/Debug", "obj", "node_modules/left-pad", ".vs", "Tests", "Tools", "Bin"])
        {
            Directory.CreateDirectory(Path.Join(project, folder));
        }
        File.WriteAllText(Path.Join(project, "Tests", "Tests.csproj"), "<Project />");
        File.WriteAllText(Path.Join(project, "Tools", "Tools.fsproj"), "<Project />");
        bool caseIgnored = PathNames.CaseIgnoredIn(project);
        string[] expected = [project, Path.Join(project, "Pages"), Path.Join(project, "Pages", "Admin"), .. caseIgnored ? (string[])[] : [Path.Join(project, "Bin")]];

        Assert.Equal(expected.Order(StringComparer.Ordinal), ProjectFolders.Under(project).Order(StringComparer.Ordinal));
        Assert.True(ProjectFolders.Holds(project, Path.Join(project, "Pages", "Admin", "Users.cs")), "A file in the project's own folder is not its own");
        Assert.False(ProjectFolders.Holds(project, Path.Join(project, "Tests", "Shop", "Cart.cs")), "A file of the nested Tests project counts as Shop's too");
        Assert.False(ProjectFolders.Holds(project, Path.Join(project, "Tools", "Tool.cs")), "A nested F# project's folder counts as Shop's");
        Assert.False(ProjectFolders.Holds(project, Path.Join(project, "obj", "Debug", "Shop.AssemblyInfo.cs")), "The build's output counts as the project's own");
        Assert.True(ProjectFolders.Holds(project, Path.Join(project, "Bin", "Data.cs")) != caseIgnored,
            caseIgnored ? "Bin, where the build writes bin in a folder that ignores case, counts as the project's own" : "Bin, a folder of its own beside bin where case is told apart, is passed over");
        Assert.False(ProjectFolders.Holds(project, Path.Join(root, "Other", "A.cs")), "A file outside the project counts as its own");
    }

    [Fact]
    public void The_folders_walk_and_a_projects_files_pass_over_build_output_in_another_letter_case_as_its_own_folders_do()
    {
        string project = Path.Join(root, "Shop");
        string[] written = ["Pages/Index.cs", "Bin/Debug/Built.cs", "Obj/Shop.AssemblyInfo.cs", "Node_Modules/pad/index.js", "Tools/Tool.cs"];
        foreach (string file in written)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(project, file))!);
            File.WriteAllText(Path.Join(project, file), string.Empty);
        }
        File.WriteAllText(Path.Join(project, "Shop.csproj"), "<Project />");
        File.WriteAllText(Path.Join(project, "Tools", "Tools.csproj"), "<Project />");
        bool caseIgnored = PathNames.CaseIgnoredIn(project);
        string[] output = ["Bin/Debug/Built.cs", "Obj/Shop.AssemblyInfo.cs", "Node_Modules/pad/index.js"];

        foreach (string folder in (string[])["Bin", "Obj", "Node_Modules"])
        {
            string path = Path.Join(project, folder);
            Assert.True(IgnoredFolders.Is(path) == !ProjectFolders.Enters(path) && IgnoredFolders.Is(path) == caseIgnored,
                $"The folder's walk {(IgnoredFolders.Is(path) ? "passes over" : "enters")} {folder} where a project's wildcards {(ProjectFolders.Enters(path) ? "enter" : "pass over")} it");
        }
        string[] listed = [.. FolderWalk.Below(root).Select(f => f.Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal)];
        string[] expected = [.. written.Except(caseIgnored ? output : []).Append("Shop.csproj").Append("Tools/Tools.csproj").Select(f => "Shop/" + f).Order(StringComparer.Ordinal)];
        Assert.Equal(expected, listed);
        string[] own = [.. FolderWalk.ProjectFiles(ProjectModel.Unevaluated(Path.Join(project, "Shop.csproj")), ".cs").Select(f => Path.GetRelativePath(project, f).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal)];
        Assert.True(own.SequenceEqual(caseIgnored ? ["Pages/Index.cs"] : ["Bin/Debug/Built.cs", "Obj/Shop.AssemblyInfo.cs", "Pages/Index.cs"]),
            $"Shop's own C# files are [{string.Join(", ", own)}], where Tools holds a project of its own though no solution names it");
    }

    /// <summary>Only a project's build output and dependencies are read in another letter case: an Artifacts folder of an
    /// RPG's items, a Packages folder or a testresults folder is the person's code, though artifacts, packages NuGet
    /// restored into and TestResults written as such are passed over.</summary>
    [Fact]
    public void Build_output_in_another_letter_case_is_passed_over_where_case_is_ignored_and_the_folders_other_names_only_as_written()
    {
        string game = Path.Join(root, "Game");
        string[] written = ["Bin/Debug/Built.cs", "Artifacts/Sword.tscn", "testresults/Summary.md",
            "Packages/Newtonsoft.Json.13.0.1/Newtonsoft.Json.13.0.1.nupkg", "Lib/packages/Newtonsoft.Json.13.0.1/Newtonsoft.Json.13.0.1.nupkg"];
        foreach (string file in written)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(game, file))!);
            File.WriteAllText(Path.Join(game, file), string.Empty);
        }
        bool caseIgnored = PathNames.CaseIgnoredIn(game);

        Assert.True(IgnoredFolders.Is(Path.Join(game, "Bin")) == caseIgnored,
            caseIgnored ? "Bin, the build's bin where case is ignored, is walked as the person's code" : "Bin, a folder of the person's where case is told apart, is passed over");
        Assert.True(IgnoredFolders.Is(Path.Join(game, "Lib", "packages")), "A packages folder NuGet restored into is walked as the person's code");
        foreach (string folder in (string[])["Artifacts", "testresults", "Packages"])
        {
            Assert.False(IgnoredFolders.Is(Path.Join(game, folder)), $"{folder}, a folder of the person's, is passed over as build output");
            Assert.False(IgnoredFolders.AnyIn(root, Path.Join("Game", folder, "x")), $"A file in {folder}, a folder of the person's, is passed over as build output");
        }
        string[] listed = [.. FolderWalk.Below(root).Select(f => f.Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal)];
        string[] expected = [.. written.Where(f => !f.StartsWith("Lib/", StringComparison.Ordinal) && !(caseIgnored && f.StartsWith("Bin/", StringComparison.Ordinal)))
            .Select(f => "Game/" + f).Order(StringComparer.Ordinal)];
        Assert.Equal(expected, listed);
    }

    [Fact]
    public void A_resource_is_found_by_its_embedded_name_only_in_the_projects_own_folders()
    {
        string project = Path.Join(root, "Shop");
        foreach (string folder in (string[])["Pages", "Tests"])
        {
            Directory.CreateDirectory(Path.Join(project, folder));
            File.WriteAllText(Path.Join(project, folder, "Strings.resx"), "<root />");
        }
        File.WriteAllText(Path.Join(project, "Tests", "Tests.csproj"), "<Project />");

        Assert.Equal([Path.Join(project, "Pages", "Strings")], ResourceFiles.Named(project, "Shop", "Shop.Pages.Strings"));
        Assert.True(ResourceFiles.Named(project, "Shop", "Shop.Tests.Strings").Count == 0,
            "Shop.Tests.Strings was found in the nested Tests project's folder, as if Shop's own");
    }
}
