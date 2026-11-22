using Passenger.App.Packages;
using Passenger.Core;

namespace Passenger.UnitTests.Packages;

/// <summary>Which files a package folder makes a package's: those of a folder NuGet laid out as a package's, never a project's
/// source below a package folder it names itself.</summary>
public sealed class PackageFoldersTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Written(string path)
    {
        string file = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Empty);
        return file;
    }

    [Fact]
    public void A_package_folders_files_are_a_packages_and_a_projects_source_below_it_stays_its_own() => Cases.All(
        ("a project's source below the package folder it names stays its own beside a package there", () =>
        {
            string source = Written("src/App/Program.cs");
            string deeper = Written("src/App/Models/Order.cs");
            Written("kit/1.0.0/.nupkg.metadata");
            string restored = Written("kit/1.0.0/lib/Kit.cs");
            PackageFolders.Learn([root]);

            Assert.False(PackageFolders.Holds(source), "A project's own source below its package folder was taken for a package's, so it could not be edited or saved");
            Assert.False(PackageFolders.Holds(deeper), "A project's own source below its package folder was taken for a package's, so it could not be edited or saved");
            Assert.Null(PackageFolders.PackageOf(source));
            Assert.True(PackageFolders.Holds(restored), "A restored package's file was left writable, every project's copy of that version");
            Assert.Equal("kit 1.0.0", PackageFolders.PackageOf(restored));
        }),
        ("a package of a fallback folder with only its nuspec is a package", () =>
        {
            Written("lamp/2.0.0/lamp.nuspec");
            string restored = Written("lamp/2.0.0/content/lamp.css");
            PackageFolders.Learn([root]);

            Assert.True(PackageFolders.Holds(restored), "A package holding its nuspec was not taken for one");
        }),
        ("a file made in a package's folder or its version's own is the package's and in the package folder or an id's none", () =>
        {
            Written("tray/3.0.0/.nupkg.metadata");
            string content = Path.GetDirectoryName(Written("tray/3.0.0/content/tray.txt"))!;
            string version = Path.Combine(root, "tray", "3.0.0");
            PackageFolders.Learn([root]);

            Assert.Equal("tray 3.0.0", PackageFolders.PackageIn(content));
            Assert.Equal("tray 3.0.0", PackageFolders.PackageIn(version));
            Assert.Null(PackageFolders.PackageOf(version));
            Assert.Null(PackageFolders.PackageIn(Path.Combine(root, "tray")));
            Assert.Null(PackageFolders.PackageIn(root));
        }));

}
