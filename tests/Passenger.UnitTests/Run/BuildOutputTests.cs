using Passenger.App.Run;
using Passenger.Text;

namespace Passenger.UnitTests.Run;

/// <summary>Where a build's line puts its problem: a file only where its origin is one (a place, a folder or an extension),
/// a tool's origin ("CSC", "EXEC", "MSBUILD") naming no file but the project the line ends with.</summary>
public class BuildOutputTests
{
    [Theory]
    [InlineData("CSC : error CS5001: Program does not contain a static 'Main' method suitable for an entry point [/src/App/App.csproj]", "CS5001")]
    [InlineData("EXEC : error XC0001: the command exited with code 1 [/src/App/App.csproj]", "XC0001")]
    [InlineData("MSBUILD : error MSB1009: Project file does not exist. [/src/App/App.csproj]", "MSB1009")]
    public void A_tools_problem_names_no_file_and_is_the_projects(string line, string code)
    {
        Problem? problem = BuildOutput.Parse(line);
        Assert.NotNull(problem);
        Assert.True(problem.Path is null, $"The tool's name was taken as the file {problem.Path}, which Go to would open and an agent would be sent to.");
        Assert.Equal(code, problem.Code);
        Assert.Equal("App", problem.Project);
        Assert.Equal("App", problem.Where);
    }

    [Theory]
    [InlineData("/src/App/Program.cs(3,9): error CS1002: ; expected [/src/App/App.csproj]", "/src/App/Program.cs", 2, 8)]
    [InlineData("Program.cs(3,9): error CS1002: ; expected [/src/App/App.csproj]", "Program.cs", 2, 8)]
    [InlineData("Dockerfile(4): warning DL3008: Pin versions [/src/App/App.csproj]", "Dockerfile", 3, 0)]
    [InlineData("App.csproj : warning NU1603: App depends on Lib (>= 1.0.0) [/src/App/App.csproj]", "App.csproj", 0, 0)]
    public void A_files_problem_keeps_its_file_and_place(string line, string path, int lineNumber, int column)
    {
        Problem? problem = BuildOutput.Parse(line);
        Assert.NotNull(problem);
        Assert.EndsWith(path, problem.Path);
        Assert.Equal(new TextPosition(lineNumber, column), problem.Position);
    }
}
