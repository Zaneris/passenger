using System.Xml.Linq;
using Passenger.App.MSBuild;

namespace Passenger.Tests;

/// <summary>A package's version placed in Directory.Packages.props, as completing a package in a project under central
/// package management and a project given packages there place it: the file still reads as XML, the new PackageVersion
/// in an ItemGroup, its id and version escaped, whether the file is written on one line or many.</summary>
[Collection(Beside.Name)]
public sealed class CentralVersionTests
{
    [Fact]
    public void A_version_placed_in_a_one_line_props_file_with_package_versions_goes_beside_them_on_their_line()
    {
        const string Props = "<Project><ItemGroup><PackageVersion Include=\"Alpha\" Version=\"1.0.0\" /><PackageVersion Include=\"Zulu\" Version=\"1.0.0\" /></ItemGroup></Project>";

        Assert.Equal("<Project><ItemGroup><PackageVersion Include=\"Alpha\" Version=\"1.0.0\" /><PackageVersion Include=\"Mike\" Version=\"2.0.0\" />"
            + "<PackageVersion Include=\"Zulu\" Version=\"1.0.0\" /></ItemGroup></Project>", Placed(Props, "Mike", "2.0.0"));
        Assert.Equal("<Project><ItemGroup><PackageVersion Include=\"Able\" Version=\"2.0.0\" /><PackageVersion Include=\"Alpha\" Version=\"1.0.0\" />"
            + "<PackageVersion Include=\"Zulu\" Version=\"1.0.0\" /></ItemGroup></Project>", Placed(Props, "Able", "2.0.0"));
    }

    [Fact]
    public void A_version_placed_in_a_one_line_props_file_without_package_versions_goes_in_an_item_group_inside_the_project()
    {
        Assert.Equal("<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>"
            + "<ItemGroup><PackageVersion Include=\"Mike\" Version=\"2.0.0\" /></ItemGroup></Project>",
            Placed("<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>", "Mike", "2.0.0"));
    }

    [Fact]
    public void A_version_placed_in_a_props_file_of_many_lines_takes_its_own_line_with_the_others_indent()
    {
        const string Props = "<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"Alpha\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>\n";

        Assert.Equal("<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"Alpha\" Version=\"1.0.0\" />\n    <PackageVersion Include=\"Mike\" Version=\"2.0.0\" />\n"
            + "  </ItemGroup>\n</Project>\n", Placed(Props, "Mike", "2.0.0"));
        Assert.Equal("<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"Able\" Version=\"2.0.0\" />\n    <PackageVersion Include=\"Alpha\" Version=\"1.0.0\" />\n"
            + "  </ItemGroup>\n</Project>\n", Placed(Props, "Able", "2.0.0"));
        Assert.Equal("<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"Mike\" Version=\"2.0.0\" />\n  </ItemGroup>\n</Project>\n",
            Placed("<Project>\n</Project>\n", "Mike", "2.0.0"));
    }

    [Fact]
    public void A_version_whose_words_xml_would_read_as_markup_is_written_escaped()
    {
        string placed = Placed("<Project></Project>", "Odd&Id", "1.0.0-\"a\"<b>");
        XElement version = XDocument.Parse(placed).Descendants("PackageVersion").Single();
        Assert.True((string?)version.Attribute("Include") == "Odd&Id" && (string?)version.Attribute("Version") == "1.0.0-\"a\"<b>",
            $"The id and version read back as {version}, not as given: {placed}");
    }

    /// <summary>The props file with the package's version placed, held to reading as XML with it in an ItemGroup.</summary>
    private static string Placed(string props, string id, string version)
    {
        string placed = CentralPackages.Pinned(props, [(id, version)]);
        XDocument read;
        try
        {
            read = XDocument.Parse(placed);
        }
        catch (System.Xml.XmlException unread)
        {
            throw new Xunit.Sdk.XunitException($"{id} {version} placed in\n{props}\nleft a file that no longer reads as XML ({unread.Message}):\n{placed}");
        }
        Assert.True(read.Root?.Name.LocalName == "Project" && read.Root.Elements("ItemGroup").Elements("PackageVersion").Any(v => (string?)v.Attribute("Include") == id),
            $"{id} {version} placed in\n{props}\nis in no ItemGroup of the project:\n{placed}");
        return placed;
    }
}
