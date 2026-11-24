using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Where a Yarn Plug'n'Play package stands, from its location in the data as Yarn writes it from the project's
/// folder: on Windows across drives and shares as on one drive, and on macOS and Linux as before.</summary>
public sealed class YarnPnpTests
{
    /// <summary>A package's location names its folder from the project's, whichever drive or share each is on.</summary>
    [Fact]
    public void A_packages_location_names_its_folder_from_the_projects_on_any_drive() => Cases.Each<(string Folder, string Location, bool Windows, string Located)>(
    [
        (@"D:\src\shop", "../../../C:/Users/ann/AppData/Local/Yarn/Berry/cache/eslint-npm-9.0.0-1a2b.zip/node_modules/eslint/", true,
            @"C:\Users\ann\AppData\Local\Yarn\Berry\cache\eslint-npm-9.0.0-1a2b.zip\node_modules\eslint"),
        (@"C:\src\shop", "./.yarn/cache/eslint-npm-9.0.0-1a2b.zip/node_modules/eslint/", true, @"C:\src\shop\.yarn\cache\eslint-npm-9.0.0-1a2b.zip\node_modules\eslint"),
        (@"\\server\share\shop", "../../../../C:/packages/eslint/", true, @"C:\packages\eslint"),
        ("/home/ann/shop", "../../../opt/packages/eslint/", false, "/opt/packages/eslint"),
    ], row =>
    {
        string located = YarnPnp.Located(row.Folder, row.Location, row.Windows);
        Assert.True(located == row.Located,
            $"The package at {row.Location} from {row.Folder} was looked for at {located}, where Yarn put it at {row.Located}, so the project's ESLint, Prettier or TypeScript there would go unfound");
    });
}
