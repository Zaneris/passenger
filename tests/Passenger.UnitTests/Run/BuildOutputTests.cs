using Passenger.App.Run;
using Passenger.Text;

namespace Passenger.UnitTests.Run;

/// <summary>Where a build's line puts its problem: a file only where its origin is one (a place, a folder or an extension),
/// a tool's origin ("CSC", "EXEC", "MSBUILD") naming no file but the project the line ends with, and a multi-targeted
/// project's inner build's framework where the line names one.</summary>
public class BuildOutputTests
{
    [Fact]
    public void A_builds_problem_is_its_file_and_place_else_its_projects() => Cases.All(
        ("a tools problem names no file and is the projects", () => Cases.Each<(string, string)>(
        [
            ("CSC : error CS5001: Program does not contain a static 'Main' method suitable for an entry point [/src/App/App.csproj]", "CS5001"),
            ("MSBUILD : error MSB1009: Project file does not exist. [/src/App/App.csproj]", "MSB1009"),
        ], row => A_tools_problem_names_no_file_and_is_the_projects(row.Item1, row.Item2))),
        ("a files problem keeps its file and place", () => Cases.Each<(string, string, int, int)>(
        [
            ("/src/App/Program.cs(3,9): error CS1002: ; expected [/src/App/App.csproj]", "/src/App/Program.cs", 2, 8),
            ("Dockerfile(4): warning DL3008: Pin versions [/src/App/App.csproj]", "Dockerfile", 3, 0),
            ("App.csproj : warning NU1603: App depends on Lib (>= 1.0.0) [/src/App/App.csproj]", "App.csproj", 0, 0),
        ], row => A_files_problem_keeps_its_file_and_place(row.Item1, row.Item2, row.Item3, row.Item4))),
        ("a problem of a multi targeted projects inner build is its projects and names that framework", A_problem_of_a_multi_targeted_projects_inner_build_is_its_projects_and_names_that_framework));

    private void A_tools_problem_names_no_file_and_is_the_projects(string line, string code)
    {
        Problem? problem = BuildOutput.Parse(line);
        Assert.NotNull(problem);
        Assert.True(problem.Path is null, $"The tool's name was taken as the file {problem.Path}, which Go to would open and an agent would be sent to.");
        Assert.Equal(code, problem.Code);
        Assert.Equal("App", problem.Project);
        Assert.Equal("App", problem.Where);
    }

    private void A_files_problem_keeps_its_file_and_place(string line, string path, int lineNumber, int column)
    {
        Problem? problem = BuildOutput.Parse(line);
        Assert.NotNull(problem);
        Assert.EndsWith(path, problem.Path);
        Assert.Equal(new TextPosition(lineNumber, column), problem.Position);
    }

    private void A_problem_of_a_multi_targeted_projects_inner_build_is_its_projects_and_names_that_framework()
    {
        Problem? problem = BuildOutput.Parse("/src/App/MainWindow.axaml(9,5): error AVLN2000: Unable to resolve property Note [/src/App/App.csproj::TargetFramework=net10.0]");
        Assert.NotNull(problem);
        Assert.Equal(("App", "net10.0"), (problem.Project, problem.Framework));
        Assert.Equal("Unable to resolve property Note", problem.Message);
        Assert.True(BuildOutput.Parse("/src/App/Program.cs(3,9): error CS1002: ; expected [/src/App/App.csproj]") is { Framework: null },
            "A build of one framework's problem was said to be of an inner build's framework");
    }
}
