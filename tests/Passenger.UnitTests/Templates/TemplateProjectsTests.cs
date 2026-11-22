using Passenger.App.Templates;

namespace Passenger.UnitTests.Templates;

/// <summary>The output type a template's project file builds, read from its text before dotnet new makes it: written, or
/// the .NET SDK's library where it writes none, and unknown wherever an SDK of its own or a condition decides (#1714).</summary>
public sealed class TemplateProjectsTests
{
    [Fact]
    public void A_project_template_writes_the_program_it_builds_where_nothing_else_decides_it() => Cases.All(
        ("a project of the dotnet SDK that writes no program builds a library", () => Cases.Each<string>(
        [
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsAspireSharedProject>true</IsAspireSharedProject></PropertyGroup></Project>""",
            """<Project><Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" /><Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" /></Project>""",
        ], A_project_of_the_dotnet_SDK_that_writes_no_program_builds_a_library)),
        ("a project writes the program it builds", () => Cases.Each<(string, string)>(
        [
            ("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""", "Exe"),
            ("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType Condition="'$(X)' == ''">Exe</OutputType><OutputType>Exe</OutputType></PropertyGroup></Project>""", "Exe"),
        ], row => A_project_writes_the_program_it_builds(row.Item1, row.Item2))),
        ("a project whose SDK or a condition decides what it builds is not known", () => Cases.Each<string>(
        [
            """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""",
            """<Project Sdk="Microsoft.NET.Sdk"><Import Project="../Common.props" /></Project>""",
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup Condition="'$(Configuration)' == 'Debug'"><OutputType>Exe</OutputType></PropertyGroup></Project>""",
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><!--#if (Exe)--><OutputType>Exe</OutputType><!--#endif--></PropertyGroup></Project>""",
            """<Project Sdk="Microsoft.NET.Sdk">""",
        ], A_project_whose_SDK_or_a_condition_decides_what_it_builds_is_not_known)),
        ("a primary output is a project by its extension", () => Cases.Each<(string, bool)>(
        [
            ("App/App.FSPROJ", true),
            ("frontend/frontend.esproj", false),
        ], row => A_primary_output_is_a_project_by_its_extension(row.Item1, row.Item2))));

    private void A_project_of_the_dotnet_SDK_that_writes_no_program_builds_a_library(string project) =>
        Assert.True(TemplateProjects.OutputType(project) == "Library", $"A library would be offered a service's set-ups: {TemplateProjects.OutputType(project) ?? "null"}");

    private void A_project_writes_the_program_it_builds(string project, string type) =>
        Assert.Equal(type, TemplateProjects.OutputType(project));

    private void A_project_whose_SDK_or_a_condition_decides_what_it_builds_is_not_known(string project) =>
        Assert.True(TemplateProjects.OutputType(project) is null, $"What the template makes would be taken as known: {TemplateProjects.OutputType(project)}");

    private void A_primary_output_is_a_project_by_its_extension(string path, bool project) =>
        Assert.Equal(project, TemplateProjects.IsProject(path));
}
