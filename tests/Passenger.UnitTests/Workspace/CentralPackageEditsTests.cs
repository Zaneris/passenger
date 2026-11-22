using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A package at a version written under central package management as dotnet add package writes it
/// (<see cref="ProjectEdits.WithCentralPackage"/>, <see cref="ProjectEdits.WithPackageVersion"/>): the project's reference
/// with no version, else its VersionOverride, and Directory.Packages.props' PackageVersion, every other character as it was.</summary>
public class CentralPackageEditsTests
{
    private const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n  <ItemGroup>\r\n"
        + "    <PackageReference Include=\"xunit\" />\r\n  </ItemGroup>\r\n"
        + "  <ItemGroup Condition=\"'$(A)' == ''\">\r\n    <PackageReference Include=\"Dapper\" VersionOverride=\"2.0.0\" />\r\n  </ItemGroup>\r\n"
        + "  <ItemGroup>\r\n    <PackageReference Include=\"Humanizer\">\r\n      <VersionOverride>2.14.1</VersionOverride>\r\n    </PackageReference>\r\n"
        + "  </ItemGroup>\r\n</Project>\r\n";

    [Fact]
    public void A_package_at_a_version_is_written_under_central_management_where_its_version_stands_every_other_character_kept() => Cases.All(
        ("a package not referenced is added with no version after the last reference every build sees", () =>
        {
            (string text, bool overridden) = ProjectEdits.WithCentralPackage(Project, "Passenger.Sample", "1.0.0");
            Assert.False(overridden);
            Assert.Equal(Project.Replace("      <VersionOverride>2.14.1</VersionOverride>\r\n    </PackageReference>\r\n",
                "      <VersionOverride>2.14.1</VersionOverride>\r\n    </PackageReference>\r\n    <PackageReference Include=\"Passenger.Sample\" />\r\n", StringComparison.Ordinal), text);
            (string alone, _) = ProjectEdits.WithCentralPackage("<Project>\n</Project>\n", "Passenger.Sample", "1.0.0");
            Assert.Equal("<Project>\n  <ItemGroup>\n    <PackageReference Include=\"Passenger.Sample\" />\n  </ItemGroup>\n</Project>\n", alone);
        }),
        ("a reference with a VersionOverride takes the version there and one without is left to the central file", () =>
        {
            (string attribute, bool overrides) = ProjectEdits.WithCentralPackage(Project, "dapper", "2.1.0");
            Assert.True(overrides);
            Assert.Equal(Project.Replace("VersionOverride=\"2.0.0\"", "VersionOverride=\"2.1.0\"", StringComparison.Ordinal), attribute);
            (string element, bool overridesToo) = ProjectEdits.WithCentralPackage(Project, "Humanizer", "3.0.0");
            Assert.True(overridesToo);
            Assert.Equal(Project.Replace("<VersionOverride>2.14.1</VersionOverride>", "<VersionOverride>3.0.0</VersionOverride>", StringComparison.Ordinal), element);
            Assert.Equal((Project, false), ProjectEdits.WithCentralPackage(Project, "XUnit", "2.9.3"));
            Assert.Equal(("<Project><ItemGroup", false), ProjectEdits.WithCentralPackage("<Project><ItemGroup", "xunit", "2.9.3"));
        }),
        ("a central version is set where the file holds one for the package and none is set where it holds none", () =>
        {
            const string Props = "<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"xunit\" Version=\"2.9.3\" />\n"
                + "    <PackageVersion Include=\"Humanizer\">\n      <Version>2.14.1</Version>\n    </PackageVersion>\n  </ItemGroup>\n</Project>\n";
            Assert.Equal(Props.Replace("2.9.3", "3.0.0", StringComparison.Ordinal), ProjectEdits.WithPackageVersion(Props, "XUNIT", "3.0.0"));
            Assert.Equal(Props.Replace("<Version>2.14.1</Version>", "<Version>3.1.0</Version>", StringComparison.Ordinal), ProjectEdits.WithPackageVersion(Props, "Humanizer", "3.1.0"));
            Assert.Null(ProjectEdits.WithPackageVersion(Props, "Dapper", "2.1.0"));
        }),
        ("a version kept in a property names it and the property is set where its line sets it every other character kept", () =>
        {
            Assert.Equal("$(Linq2DbVersion)", ProjectEdits.VersionWritten(Shared, MSBuildNames.PackageVersion, "LINQ2DB.PostgreSQL"));
            Assert.Equal("$(AvaloniaVersion)", ProjectEdits.VersionWritten(
                "<Project>\n  <ItemGroup>\n    <PackageReference Include=\"Avalonia\">\n      <Version>$(AvaloniaVersion)</Version>\n    </PackageReference>\n  </ItemGroup>\n</Project>\n",
                MSBuildNames.PackageReference, "avalonia"));
            Assert.Equal("Linq2DbVersion", ProjectEdits.PropertyNamed(" $(Linq2DbVersion) "));
            Assert.Null(ProjectEdits.PropertyNamed("[$(Linq2DbVersion)]"));
            Assert.Equal((Shared.Replace("> 6.5.0 <", "> 6.5.1 <", StringComparison.Ordinal), null), ProjectEdits.WithPropertyAt(Shared, "Linq2DbVersion", 4, "6.5.1"));
        }),
        ("a property set under a condition as another property or not at the line is refused saying why", () =>
        {
            Assert.Equal((null, "it is set under a condition"), ProjectEdits.WithPropertyAt(Shared, "EfVersion", 8, "11.0.0"));
            Assert.Equal((null, "it is set as $(Linq2DbVersion), not a version"), ProjectEdits.WithPropertyAt(Shared, "Derived", 11, "6.5.1"));
            Assert.Equal((null, "no property group sets it at line 5"), ProjectEdits.WithPropertyAt(Shared, "Linq2DbVersion", 5, "6.5.1"));
        }),
        ("of two central versions of a package the one at the line given is read and set", () =>
        {
            Assert.Equal("2.1.89", ProjectEdits.VersionWritten(Shared, MSBuildNames.PackageVersion, "Dapper", 17));
            Assert.Equal("2.1.86", ProjectEdits.VersionWritten(Shared, MSBuildNames.PackageVersion, "Dapper"));
            Assert.Equal(Shared.Replace("2.1.89", "2.2.0", StringComparison.Ordinal), ProjectEdits.WithPackageVersion(Shared, "Dapper", "2.2.0", 17));
        }));

    private const string Shared = "<Project>\r\n\r\n  <PropertyGroup>\r\n    <Linq2DbVersion> 6.5.0 </Linq2DbVersion>\r\n  </PropertyGroup>\r\n\r\n"
        + "  <PropertyGroup Condition=\"'$(Ci)' == 'true'\">\r\n    <EfVersion>10.0.12</EfVersion>\r\n  </PropertyGroup>\r\n"
        + "  <PropertyGroup>\r\n    <Derived>$(Linq2DbVersion)</Derived>\r\n  </PropertyGroup>\r\n\r\n"
        + "  <ItemGroup>\r\n    <PackageVersion Include=\"linq2db.PostgreSQL\" Version=\"$(Linq2DbVersion)\" />\r\n"
        + "    <PackageVersion Include=\"Dapper\" Version=\"2.1.86\" Condition=\"'$(A)' == ''\" />\r\n"
        + "    <PackageVersion Include=\"Dapper\" Version=\"2.1.89\" Condition=\"'$(A)' != ''\" />\r\n  </ItemGroup>\r\n\r\n</Project>\r\n";

}
