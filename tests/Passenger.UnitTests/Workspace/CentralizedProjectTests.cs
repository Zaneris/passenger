using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A project file's text put in central package management's form (<see cref="ProjectEdits.Centralized"/>): each
/// version its package references write taken out, kept as a VersionOverride where Directory.Packages.props holds another,
/// and named to pin there where it holds none, every other character as it was.</summary>
public class CentralizedProjectTests
{
    private const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n  <ItemGroup>\r\n"
        + "    <PackageReference Include=\"coverlet.collector\" Version=\"6.0.4\" />\r\n"
        + "    <PackageReference Include=\"xunit\" Version=\"2.9.3\" PrivateAssets=\"all\" />\r\n"
        + "    <PackageReference Include=\"Microsoft.NET.Test.Sdk\"\r\n                      Version = '17.14.1' />\r\n"
        + "    <PackageReference Include=\"Humanizer\">\r\n      <Version>2.14.1</Version>\r\n    </PackageReference>\r\n"
        + "    <PackageReference Include=\"Central.Only\" />\r\n  </ItemGroup>\r\n</Project>\r\n";

    [Fact]
    public void A_project_put_in_central_management_takes_its_versions_out_keeping_every_other_character() => Cases.All(
        ("a reference takes no version an override where the central one differs and names what to pin", () =>
        {
            Dictionary<string, string> central = new(StringComparer.OrdinalIgnoreCase) { ["XUNIT"] = "2.9.3", ["coverlet.collector"] = "1.0.0" };
            (string text, IReadOnlyList<(string Id, string Version)> pinned) = ProjectEdits.Centralized(Project, central, overrides: true);
            Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\">\r\n  <ItemGroup>\r\n"
                + "    <PackageReference Include=\"coverlet.collector\" VersionOverride=\"6.0.4\" />\r\n"
                + "    <PackageReference Include=\"xunit\" PrivateAssets=\"all\" />\r\n"
                + "    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" />\r\n"
                + "    <PackageReference Include=\"Humanizer\">\r\n    </PackageReference>\r\n"
                + "    <PackageReference Include=\"Central.Only\" />\r\n  </ItemGroup>\r\n</Project>\r\n", text);
            Assert.Equal(new[] { ("Microsoft.NET.Test.Sdk", "17.14.1"), ("Humanizer", "2.14.1") }, pinned);
        }),
        ("where no reference may override the central version wins and only the packages given are changed", () =>
        {
            Dictionary<string, string> central = new(StringComparer.OrdinalIgnoreCase) { ["coverlet.collector"] = "1.0.0" };
            (string text, IReadOnlyList<(string Id, string Version)> pinned) = ProjectEdits.Centralized(Project, central, overrides: false, only: ["coverlet.collector", "Humanizer"]);
            Assert.Contains("<PackageReference Include=\"coverlet.collector\" />", text, StringComparison.Ordinal);
            Assert.Contains("<PackageReference Include=\"xunit\" Version=\"2.9.3\" PrivateAssets=\"all\" />", text, StringComparison.Ordinal);
            Assert.Contains("Version = '17.14.1'", text, StringComparison.Ordinal);
            Assert.DoesNotContain("<Version>", text, StringComparison.Ordinal);
            Assert.Equal(new[] { ("Humanizer", "2.14.1") }, pinned);
        }),
        ("a package referenced twice is pinned once and text that is no XML is left as it was", () =>
        {
            string twice = "<Project>\n  <ItemGroup Condition=\"'$(A)' == ''\">\n    <PackageReference Include=\"Dapper\" Version=\"2.1.0\" />\n  </ItemGroup>\n"
                + "  <ItemGroup Condition=\"'$(A)' != ''\">\n    <PackageReference Include=\"dapper\" Version=\"2.0.0\" />\n  </ItemGroup>\n</Project>\n";
            (string text, IReadOnlyList<(string Id, string Version)> pinned) = ProjectEdits.Centralized(twice, new Dictionary<string, string>(), overrides: true);
            Assert.Equal(new[] { ("Dapper", "2.1.0") }, pinned);
            Assert.Contains("<PackageReference Include=\"dapper\" VersionOverride=\"2.0.0\" />", text, StringComparison.Ordinal);
            (string broken, IReadOnlyList<(string Id, string Version)> none) = ProjectEdits.Centralized("<Project><PackageReference", new Dictionary<string, string>(), overrides: true);
            Assert.Equal("<Project><PackageReference", broken);
            Assert.Empty(none);
        }));

}
