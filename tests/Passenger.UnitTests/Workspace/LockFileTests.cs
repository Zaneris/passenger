using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>The lock file a package change writes beside the project, found as NuGet's restore finds it, so the change
/// names, follows and takes back the file the restore wrote and not another.</summary>
public class LockFileTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "shop");
    private static readonly string Project = Path.Combine(Folder, "My Shop.csproj");

    [Fact]
    public void The_lock_file_a_package_change_writes_is_found_as_NuGets_restore_finds_it() => Cases.All(
        ("a project naming no lock file has packages.lock.json beside it", () =>
        {
            Assert.Equal(Path.Combine(Folder, "packages.lock.json"), ProjectModel.LockFileOf(Project, null, _ => false));
        }),
        ("a lock file named for the project file is taken where it is there its spaces written as underscores", () =>
        {
            string own = Path.Combine(Folder, "packages.My_Shop.lock.json");
            Assert.Equal(own, ProjectModel.LockFileOf(Project, null, path => path == own));
        }),
        ("the lock file the project names is taken from its folder whatever is there", () =>
        {
            string own = Path.Combine(Folder, "packages.My_Shop.lock.json");
            Assert.Equal(Path.Combine(Folder, "locks", "shop.json"), ProjectModel.LockFileOf(Project, "locks\\shop.json", path => path == own));
            string elsewhere = Path.Combine(Path.GetTempPath(), "locks", "shop.json");
            Assert.Equal(elsewhere, ProjectModel.LockFileOf(Project, elsewhere, _ => true));
        }));

}
