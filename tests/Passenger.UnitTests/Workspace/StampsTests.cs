using Passenger.Core;
using Passenger.Host;

namespace Passenger.UnitTests.Workspace;

/// <summary>A folder's stamp changes only with what a project's wildcards take in or enter, its folders found as a project's
/// own are (<see cref="ProjectFolders.Enters"/>): build output, dependencies, hidden folders and the system's files passed
/// over, a build folder's name in another letter case as its folder compares names, a file of any other name counted.</summary>
public sealed class StampsTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_folders_stamp_holds_while_a_build_makes_its_output_dependencies_and_hidden_folders_and_changes_with_a_folder_of_its_own()
    {
        string project = Path.Join(root, "Shop");
        Directory.CreateDirectory(Path.Join(project, "Pages"));
        File.WriteAllText(Path.Join(project, "Shop.csproj"), "<Project />");
        Stamp stamp = Stamps.Folder(project);

        foreach (string folder in (string[])["bin/Debug", "obj", "node_modules", ".vs"])
        {
            Directory.CreateDirectory(Path.Join(project, folder));
        }
        File.WriteAllText(Path.Join(project, ".DS_Store"), string.Empty);
        Assert.True(Stamps.Holds(stamp), "A build's output, the dependencies, a hidden folder or the system's file changed the project's stamp, so every build evaluates it again");

        Directory.CreateDirectory(Path.Join(project, "Shared"));
        Assert.False(Stamps.Holds(stamp), "A folder come into the project changed no stamp, so its files stay unseen until it is evaluated for another reason");
    }

    [Fact]
    public void A_file_named_as_a_build_folder_is_counted_as_the_wildcards_take_it_in()
    {
        string project = Path.Join(root, "Tool");
        Directory.CreateDirectory(project);
        Stamp stamp = Stamps.Folder(project);

        File.WriteAllText(Path.Join(project, "obj"), string.Empty);

        Assert.False(Stamps.Holds(stamp), "A file named obj, which the project's wildcards take in, changed no stamp, so the project shows without it");
    }

    [Fact]
    public void A_build_folders_name_in_another_letter_case_is_counted_only_where_its_folder_tells_case_apart()
    {
        string project = Path.Join(root, "Shop");
        Directory.CreateDirectory(project);
        Stamp stamp = Stamps.Folder(project);
        bool caseIgnored = PathNames.CaseIgnoredIn(project);

        Directory.CreateDirectory(Path.Join(project, "Bin"));

        Assert.True(Stamps.Holds(stamp) == caseIgnored, caseIgnored
            ? "Bin, the build's bin where case is ignored, changed the project's stamp, so every build evaluates it again"
            : "Bin, a folder of the project's own where case is told apart, changed no stamp, so the files that come into it stay unseen");
        Assert.True(Stamps.Counts(Path.Join(project, "Bin"), folder: true) != caseIgnored,
            caseIgnored ? "The evaluation's walk enters Bin, the build's bin where case is ignored" : "The evaluation's walk passes over Bin, a folder of the project's own where case is told apart");
    }
}
