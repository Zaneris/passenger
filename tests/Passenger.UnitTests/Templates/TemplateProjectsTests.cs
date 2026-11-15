using Passenger.App.Templates;

namespace Passenger.UnitTests.Templates;

/// <summary>The output type a template's project file builds, read from its text before dotnet new makes it: written, or
/// the .NET SDK's library where it writes none, and unknown wherever an SDK of its own or a condition decides (#1714).</summary>
public sealed class TemplateProjectsTests
{
    [Theory]
    // Aspire's and MAUI's service defaults, tagged Web API and Service.
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsAspireSharedProject>true</IsAspireSharedProject></PropertyGroup></Project>""")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>DOTNET_TFM</TargetFramework><UseMauiCore>true</UseMauiCore></PropertyGroup></Project>""")]
    [InlineData("""<Project><Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" /><Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" /></Project>""")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk/1.0.0"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>""")]
    public void A_project_of_the_dotnet_SDK_that_writes_no_program_builds_a_library(string project) =>
        Assert.True(TemplateProjects.OutputType(project) == "Library", $"A library would be offered a service's set-ups: {TemplateProjects.OutputType(project) ?? "null"}");

    [Theory]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""", "Exe")]
    [InlineData("""<Project Sdk="Aspire.AppHost.Sdk/13.6.5"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""", "Exe")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType Condition="'$(X)' == ''">Exe</OutputType><OutputType>Exe</OutputType></PropertyGroup></Project>""", "Exe")]
    public void A_project_writes_the_program_it_builds(string project, string type) =>
        Assert.Equal(type, TemplateProjects.OutputType(project));

    [Theory]
    // The web, worker and Razor SDKs set their own.
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk.Worker"></Project>""")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><Sdk Name="Other.Sdk" /></Project>""")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk;Other.Sdk"></Project>""")]
    // An import or a condition decides it.
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><Import Project="../Common.props" /></Project>""")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType Condition=" $(XUnitVersion) != 'v2' ">Exe</OutputType></PropertyGroup></Project>""")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup Condition="'$(Configuration)' == 'Debug'"><OutputType>Exe</OutputType></PropertyGroup></Project>""")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Library</OutputType><OutputType Condition="'$(X)' == ''">Exe</OutputType></PropertyGroup></Project>""")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><!--#if (Exe)--><OutputType>Exe</OutputType><!--#endif--></PropertyGroup></Project>""")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><Target Name="T"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Target></Project>""")]
    // No project.
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk">""")]
    [InlineData("""<Solution><Project Path="A/A.csproj" /></Solution>""")]
    public void A_project_whose_SDK_or_a_condition_decides_what_it_builds_is_not_known(string project) =>
        Assert.True(TemplateProjects.OutputType(project) is null, $"What the template makes would be taken as known: {TemplateProjects.OutputType(project)}");

    [Theory]
    [InlineData("Api.csproj", true)]
    [InlineData("App/App.FSPROJ", true)]
    [InlineData("Lib.vbproj", true)]
    [InlineData("frontend/frontend.esproj", false)]
    [InlineData("App.sln", false)]
    public void A_primary_output_is_a_project_by_its_extension(string path, bool project) =>
        Assert.Equal(project, TemplateProjects.IsProject(path));
}
