using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>The lock file a package change writes beside the project, found as NuGet's restore finds it, so the change
/// names, follows and takes back the file the restore wrote and not another.</summary>
public class LockFileTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "shop");
    private static readonly string Project = Path.Combine(Folder, "My Shop.csproj");

    [Fact]
    public void A_project_naming_no_lock_file_has_packages_lock_json_beside_it()
    {
        Assert.Equal(Path.Combine(Folder, "packages.lock.json"), ProjectModel.LockFileOf(Project, null, _ => false));
        Assert.Equal(Path.Combine(Folder, "packages.lock.json"), ProjectModel.LockFileOf(Project, "", _ => false));
    }

    [Fact]
    public void A_lock_file_named_for_the_project_file_is_taken_where_it_is_there_its_spaces_written_as_underscores()
    {
        string own = Path.Combine(Folder, "packages.My_Shop.lock.json");
        Assert.Equal(own, ProjectModel.LockFileOf(Project, null, path => path == own));
    }

    [Fact]
    public void The_lock_file_the_project_names_is_taken_from_its_folder_whatever_is_there()
    {
        string own = Path.Combine(Folder, "packages.My_Shop.lock.json");
        Assert.Equal(Path.Combine(Folder, "locks", "shop.json"), ProjectModel.LockFileOf(Project, "locks\\shop.json", path => path == own));
        string elsewhere = Path.Combine(Path.GetTempPath(), "locks", "shop.json");
        Assert.Equal(elsewhere, ProjectModel.LockFileOf(Project, elsewhere, _ => true));
    }
}
