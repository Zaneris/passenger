using Passenger.App.Templates;

namespace Passenger.UnitTests.Templates;

/// <summary>A new project's name is refused where a part of the namespace its C# code is given would be a C# keyword,
/// naming the keyword, and taken otherwise; a solution's, which names no code, and an F# or VB project's are not held to it.</summary>
public class ProjectNamesTests
{
    [Theory]
    [InlineData("class", "class")]
    [InlineData("int.Core", "int")]
    [InlineData("My.namespace.Tests", "namespace")]
    [InlineData("Shop.void", "void")]
    public void A_project_name_with_a_CSharp_keyword_for_a_part_of_its_namespace_is_refused_naming_it(string name, string keyword)
    {
        string? problem = ProjectNames.Problem(name, "Project", namesCode: true);
        Assert.True(problem is not null && problem.Contains($"'{keyword}'", StringComparison.Ordinal),
            $"The project name {name} was {(problem is null ? "taken" : $"refused saying '{problem}'")}, so its template writes namespace {name}; code that doesn't build");
    }

    [Theory]
    [InlineData("Class")]
    [InlineData("My.Core")]
    [InlineData("Hello")]
    [InlineData("record")]
    [InlineData("var.Tools")]
    [InlineData("My-App")]
    [InlineData("2048")]
    public void A_project_name_whose_namespace_has_no_keyword_is_taken(string name)
    {
        string? problem = ProjectNames.Problem(name, "Project", namesCode: true);
        Assert.True(problem is null, $"The project name {name} was refused, saying '{problem}', though its namespace builds");
    }

    [Fact]
    public void A_solutions_name_and_a_project_whose_template_writes_no_CSharp_are_not_held_to_CSharps_keywords()
    {
        Assert.Null(ProjectNames.Problem("class", "Solution", namesCode: false));
        Assert.Null(ProjectNames.Problem("int.Core", "Project", namesCode: false));
        Assert.Equal("Project name cannot be empty", ProjectNames.Problem(string.Empty, "Project", namesCode: true));
        Assert.Equal("Project name contains invalid characters", ProjectNames.Problem("a/b", "Project", namesCode: true));
    }
}
