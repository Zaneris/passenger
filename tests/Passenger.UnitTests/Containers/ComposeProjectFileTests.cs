using Passenger.App.Containers;

namespace Passenger.UnitTests.Containers;

/// <summary>A Docker Compose project's (.dcproj) properties are read from its text as written, each where a PropertyGroup
/// sets it, under MSBuild's namespace or none: its compose files, the service it launches and that service's address, and
/// its Compose project's name, which a project naming none leaves to Compose; and the files its ItemGroups name.</summary>
public sealed class ComposeProjectFileTests
{
    private const string Shop = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="15.0" Sdk="Microsoft.Docker.Sdk">
          <PropertyGroup Label="Globals">
            <ProjectVersion>2.1</ProjectVersion>
            <DockerTargetOS>Linux</DockerTargetOS>
            <DockerComposeProjectName> my-shop </DockerComposeProjectName>
            <DockerComposeBaseFilePath>deploy\shop</DockerComposeBaseFilePath>
            <AdditionalComposeFilePaths>deploy/db.yml; deploy/cache.yml;</AdditionalComposeFilePaths>
            <DockerLaunchAction>LaunchBrowser</DockerLaunchAction>
            <DockerServiceUrl>{Scheme}://localhost:{ServicePort}/swagger</DockerServiceUrl>
            <DockerServiceName>web</DockerServiceName>
          </PropertyGroup>
          <ItemGroup>
            <None Include="deploy\shop.yml" />
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public void A_projects_properties_are_read_as_written_its_compose_project_named_by_DockerComposeProjectName()
    {
        ComposeProjectFile read = ComposeProjectFile.Read(Shop) ?? throw new Xunit.Sdk.XunitException("The .dcproj did not read");
        Assert.Equal("my-shop", read.ProjectName);
        Assert.Equal(@"deploy\shop", read.BaseFilePath);
        Assert.Equal(["deploy/db.yml", "deploy/cache.yml"], read.AdditionalFiles);
        Assert.Equal(("web", "{Scheme}://localhost:{ServicePort}/swagger", true), (read.ServiceName, read.ServiceUrl, read.LaunchesBrowser));
    }

    [Fact]
    public void A_project_of_MSBuilds_old_namespace_naming_nothing_takes_compose_s_defaults_and_its_own_name_for_none()
    {
        ComposeProjectFile read = ComposeProjectFile.Read("""
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <DockerComposeProjectName></DockerComposeProjectName>
                <DockerLaunchAction>None</DockerLaunchAction>
              </PropertyGroup>
              <ItemGroup>
                <DockerComposeProjectName Include="not a property" />
              </ItemGroup>
            </Project>
            """) ?? throw new Xunit.Sdk.XunitException("The .dcproj did not read");
        Assert.True(read.ProjectName is null, $"A .dcproj naming no Compose project named it {read.ProjectName}, where Compose names it from its files");
        Assert.Equal(("docker-compose", 0, string.Empty, false), (read.BaseFilePath, read.AdditionalFiles.Count, read.ServiceName, read.LaunchesBrowser));
    }

    [Theory]
    [InlineData("<DockerLaunchBrowser>True</DockerLaunchBrowser>", true)]
    [InlineData("<DockerLaunchBrowser>false</DockerLaunchBrowser>", false)]
    [InlineData("<DockerLaunchAction>None</DockerLaunchAction><DockerLaunchBrowser>true</DockerLaunchBrowser>", false)]
    [InlineData("<DockerLaunchAction>launchbrowser</DockerLaunchAction><DockerLaunchBrowser>false</DockerLaunchBrowser>", true)]
    [InlineData("<DockerLaunchAction>LaunchWCFTestClient</DockerLaunchAction>", false)]
    [InlineData("", false)]
    public void DockerLaunchAction_says_whether_the_browser_opens_and_DockerLaunchBrowser_says_only_where_it_is_not_set(string properties, bool opens)
    {
        ComposeProjectFile read = ComposeProjectFile.Read($"<Project Sdk=\"Microsoft.Docker.Sdk\"><PropertyGroup>{properties}</PropertyGroup></Project>")
            ?? throw new Xunit.Sdk.XunitException("The .dcproj did not read");
        Assert.True(read.LaunchesBrowser == opens, $"A .dcproj of {properties} {(opens ? "opened no browser" : "opened the browser")}");
    }

    [Fact]
    public void Its_items_are_the_files_its_ItemGroups_name_with_their_metadata_and_none_MSBuild_would_expand_or_hide()
    {
        ComposeProjectFile read = ComposeProjectFile.Read("""
            <Project Sdk="Microsoft.Docker.Sdk">
              <ItemGroup>
                <None Include="docker-compose.override.yml" DependentUpon="docker-compose.yml" />
                <None Include="docker-compose.yml;.dockerignore" />
                <Content Include="docker\api%20one\Dockerfile">
                  <Link>Dockerfiles\api.Dockerfile</Link>
                </Content>
                <None Include="**\*.env" />
                <None Include="$(SolutionDir)shared.yml" />
                <None Include="hidden.yml" Visible="false" />
                <PackageReference Include="Not.A.File" Version="1.0.0" />
                <Folder Include="secrets\" />
              </ItemGroup>
            </Project>
            """) ?? throw new Xunit.Sdk.XunitException("The .dcproj did not read");
        Assert.Equal(
            [
                new ComposeItem("None", "docker-compose.override.yml", null, "docker-compose.yml"),
                new ComposeItem("None", "docker-compose.yml"),
                new ComposeItem("None", ".dockerignore"),
                new ComposeItem("Content", @"docker\api one\Dockerfile", @"Dockerfiles\api.Dockerfile"),
                new ComposeItem("Folder", @"secrets\"),
            ],
            read.Items);
    }

    [Fact]
    public void Text_that_is_no_XML_and_an_entity_a_DTD_declares_read_as_no_project()
    {
        Assert.Null(ComposeProjectFile.Read("<Project><PropertyGroup>"));
        Assert.Null(ComposeProjectFile.Read("""
            <!DOCTYPE Project [<!ENTITY name "my-shop">]>
            <Project><PropertyGroup><DockerComposeProjectName>&name;</DockerComposeProjectName></PropertyGroup></Project>
            """));
    }
}
