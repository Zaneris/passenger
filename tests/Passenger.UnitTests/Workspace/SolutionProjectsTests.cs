using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A project put in a .slnx: refused where its solution folder (or the solution's top) holds a project of its file
/// name already, whatever the letter case, as MSBuild refuses to load such a solution, the file left as it was; taken
/// in another solution folder.</summary>
public sealed class SolutionProjectsTests : IDisposable
{
    private const string Solution = "<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"src/Hello/Hello.csproj\" />\n  </Folder>\n"
        + "  <Project Path=\"a/Hello/Hello.csproj\" />\n</Solution>\n";

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData(null, "b/Hello/Hello.csproj", "the solution's top")]
    [InlineData(null, "b/hello/hello.csproj", "the solution's top")]
    [InlineData("/src/", "b/Hello/Hello.csproj", "the solution folder src")]
    public void A_project_of_a_name_its_solution_folder_holds_already_is_refused_and_the_file_left_as_it_was(string? folder, string project, string where)
    {
        string solution = Write("Shop.slnx", Solution);
        string? problem = SolutionEdits.AddProject(solution, folder, At(project));
        string name = Path.GetFileNameWithoutExtension(project);
        Assert.True(problem is not null && problem.Contains($"A project named {name} is in {where} already", StringComparison.Ordinal)
            && problem.Contains("another solution folder", StringComparison.Ordinal),
            $"Adding {project} to {where} beside a project named Hello {(problem is null ? "was taken" : $"said '{problem}'")}, and MSBuild refuses to load the solution");
        Assert.Equal(Solution, File.ReadAllText(solution));
    }

    [Fact]
    public void A_project_of_that_name_is_taken_in_another_solution_folder()
    {
        string solution = Write("Shop.slnx", Solution);
        Assert.Null(SolutionEdits.AddFolder(solution, null, "tests"));
        Assert.Null(SolutionEdits.AddProject(solution, "/tests/", At("b", "Hello", "Hello.csproj")));
        Assert.Equal(Solution.Replace("  <Project Path=\"a/Hello/Hello.csproj\" />\n",
            "  <Folder Name=\"/tests/\">\n    <Project Path=\"b/Hello/Hello.csproj\" />\n  </Folder>\n  <Project Path=\"a/Hello/Hello.csproj\" />\n", StringComparison.Ordinal),
            File.ReadAllText(solution));
    }

    private string At(params string[] parts) => Path.Combine([root, .. parts.SelectMany(p => p.Split('/'))]);

    private string Write(string relative, string text)
    {
        string path = At(relative);
        File.WriteAllText(path, text);
        return path;
    }
}
