using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A package reference added to a project's file goes where every build sees it: after the last reference no
/// condition holds, else in an item group of its own, never into a group only one configuration or framework reads.</summary>
public sealed class AddPackageTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_package_reference_added_goes_where_every_build_sees_it() => Cases.All(
        ("a package goes after the last unconditioned reference never into a configuration's group", () =>
        {
            string project = Write("""
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.0.0" />
                  </ItemGroup>
                  <ItemGroup Condition="'$(Configuration)'=='Debug'">
                    <PackageReference Include="Debug.Only" Version="1.0.0" />
                  </ItemGroup>
                </Project>

                """);

            Assert.Null(ProjectEdits.AddPackage(project, "Aspire.Hosting.Redis", "9.0.0"));

            Assert.Equal("""
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Serilog" Version="4.0.0" />
                    <PackageReference Include="Aspire.Hosting.Redis" Version="9.0.0" />
                  </ItemGroup>
                  <ItemGroup Condition="'$(Configuration)'=='Debug'">
                    <PackageReference Include="Debug.Only" Version="1.0.0" />
                  </ItemGroup>
                </Project>

                """, File.ReadAllText(project));
        }),
        ("a package where every reference is conditioned goes in an item group of its own", () =>
        {
            string project = Write("""
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup Condition="'$(Configuration)'=='Debug'">
                    <PackageReference Include="Debug.Only" Version="1.0.0" />
                  </ItemGroup>
                  <ItemGroup>
                    <PackageReference Include="Net11.Only" Version="1.0.0" Condition="'$(TargetFramework)'=='net11.0'" />
                  </ItemGroup>
                  <Choose>
                    <When Condition="'$(OS)'=='Windows_NT'">
                      <ItemGroup>
                        <PackageReference Include="Windows.Only" Version="1.0.0" />
                      </ItemGroup>
                    </When>
                  </Choose>
                </Project>

                """);

            Assert.Null(ProjectEdits.AddPackage(project, "Aspire.Hosting.Redis", "9.0.0"));

            string text = File.ReadAllText(project);
            Assert.True(text.EndsWith("""
                  </Choose>
                  <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.Redis" Version="9.0.0" />
                  </ItemGroup>
                </Project>

                """, StringComparison.Ordinal), $"The package was not added in an item group of its own, which every build reads:\n{text}");
        }));

    private string Write(string text)
    {
        string path = Path.Combine(root, "AppHost.csproj");
        File.WriteAllText(path, text);
        return path;
    }
}
