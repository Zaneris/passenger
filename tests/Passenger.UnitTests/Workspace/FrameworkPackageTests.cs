using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A package referenced in some of a project's frameworks alone (<see cref="ProjectEdits.WithFrameworkPackage"/>),
/// as a vulnerable version they get through others is fixed: each framework's reference in the item group of its
/// condition, the project's own where it has one, else one of its own beside the project's references, every other
/// character as it was.</summary>
public class FrameworkPackageTests
{
    private const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n  <PropertyGroup>\r\n    <TargetFrameworks>net11.0;net10.0;net9.0</TargetFrameworks>\r\n"
        + "  </PropertyGroup>\r\n  <ItemGroup>\r\n    <PackageReference Include=\"Passenger.Charts\" Version=\"1.0.0\" />\r\n  </ItemGroup>\r\n"
        + "  <ItemGroup Condition=\" '$(TargetFramework)' == 'net11.0' \">\r\n    <PackageReference Include=\"Passenger.Vulnerable\" Version=\"1.1.0\" />\r\n  </ItemGroup>\r\n"
        + "  <ItemGroup Condition=\"'$(TargetFramework)'=='net9.0'\">\r\n    <PackageReference Include=\"Passenger.Other\" Version=\"2.0.0\" />\r\n  </ItemGroup>\r\n"
        + "  <ItemGroup>\r\n    <Compile Remove=\"Old/**\" />\r\n  </ItemGroup>\r\n</Project>\r\n";

    [Fact]
    public void A_framework_with_no_group_of_its_own_gets_one_after_the_last_group_of_references()
    {
        (string? text, string? why) = ProjectEdits.WithFrameworkPackage(Project, "Passenger.Vulnerable", "1.0.1", overriding: false, ["net10.0"]);
        Assert.Null(why);
        Assert.Equal(Project.Replace("<PackageReference Include=\"Passenger.Other\" Version=\"2.0.0\" />\r\n  </ItemGroup>\r\n",
            "<PackageReference Include=\"Passenger.Other\" Version=\"2.0.0\" />\r\n  </ItemGroup>\r\n"
            + "  <ItemGroup Condition=\"'$(TargetFramework)' == 'net10.0'\">\r\n    <PackageReference Include=\"Passenger.Vulnerable\" Version=\"1.0.1\" />\r\n  </ItemGroup>\r\n",
            StringComparison.Ordinal), text);
    }

    [Fact]
    public void A_framework_whose_group_the_project_has_takes_the_reference_after_its_last_one()
    {
        (string? text, _) = ProjectEdits.WithFrameworkPackage(Project, "Passenger.Vulnerable", "1.0.1", overriding: false, ["NET9.0"]);
        Assert.Equal(Project.Replace("<PackageReference Include=\"Passenger.Other\" Version=\"2.0.0\" />\r\n",
            "<PackageReference Include=\"Passenger.Other\" Version=\"2.0.0\" />\r\n    <PackageReference Include=\"Passenger.Vulnerable\" Version=\"1.0.1\" />\r\n",
            StringComparison.Ordinal), text);
    }

    [Fact]
    public void Under_central_management_the_reference_has_no_version_or_a_VersionOverride_and_a_framework_already_referencing_it_is_refused()
    {
        (string? central, _) = ProjectEdits.WithFrameworkPackage(Project, "Passenger.Vulnerable", null, overriding: false, ["net9.0"]);
        Assert.Contains("<PackageReference Include=\"Passenger.Other\" Version=\"2.0.0\" />\r\n    <PackageReference Include=\"Passenger.Vulnerable\" />\r\n", central, StringComparison.Ordinal);
        (string? overriding, _) = ProjectEdits.WithFrameworkPackage(Project, "Passenger.Vulnerable", "1.0.1", overriding: true, ["net9.0"]);
        Assert.Contains("<PackageReference Include=\"Passenger.Vulnerable\" VersionOverride=\"1.0.1\" />", overriding, StringComparison.Ordinal);
        Assert.Equal((null, "it references passenger.vulnerable for net11.0 already"),
            ProjectEdits.WithFrameworkPackage(Project, "passenger.vulnerable", "1.0.1", overriding: false, ["net11.0"]));
        Assert.Equal((null, "the project does not read as XML"), ProjectEdits.WithFrameworkPackage("<Project><ItemGroup", "Passenger.Vulnerable", "1.0.1", false, ["net10.0"]));
    }

    [Fact]
    public void A_project_with_no_references_takes_each_frameworks_group_before_its_end()
    {
        (string? text, _) = ProjectEdits.WithFrameworkPackage("<Project>\n  <PropertyGroup>\n  </PropertyGroup>\n</Project>\n", "Passenger.Vulnerable", "1.0.1", false, ["net9.0", "net10.0"]);
        Assert.Equal("<Project>\n  <PropertyGroup>\n  </PropertyGroup>\n"
            + "  <ItemGroup Condition=\"'$(TargetFramework)' == 'net9.0'\">\n    <PackageReference Include=\"Passenger.Vulnerable\" Version=\"1.0.1\" />\n  </ItemGroup>\n"
            + "  <ItemGroup Condition=\"'$(TargetFramework)' == 'net10.0'\">\n    <PackageReference Include=\"Passenger.Vulnerable\" Version=\"1.0.1\" />\n  </ItemGroup>\n</Project>\n", text);
    }
}
