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

    [Theory]
    [InlineData("elements")]
    [InlineData("a name")]
    [InlineData("nested projects")]
    public void Folders_nested_past_what_a_solution_holds_stand_in_the_deepest_and_lose_no_project(string how)
    {
        const int Deep = 5_000;
        string project = "<Project Path=\"a/A.csproj\" />";
        string solution = how switch
        {
            "elements" => Write("Deep.slnx", "<Solution>" + string.Concat(Enumerable.Repeat("<Folder Name=\"/f/\">", Deep)) + project
                + string.Concat(Enumerable.Repeat("</Folder>", Deep)) + "</Solution>"),
            "a name" => Write("Deep.slnx", "<Solution><Folder Name=\"/" + string.Concat(Enumerable.Repeat("f/", Deep)) + "\">" + project + "</Folder></Solution>"),
            _ => Write("Deep.sln", Nested(Deep)),
        };
        SmallStack.Run(() =>
        {
            SolutionModel read = SolutionModel.Load(solution)!;
            Assert.Equal(At("a", "A.csproj"), Assert.Single(read.Projects).Path);
            int depth = 0;
            for (SolutionFolder? folder = read.Entries.OfType<SolutionFolder>().Single(); folder is not null; folder = folder.Children.OfType<SolutionFolder>().SingleOrDefault())
            {
                depth++;
                Assert.Equal(depth * 2 + 1, folder.Path.Length);
            }
            Assert.Equal(256, depth);
        });
    }

    /// <summary>A classic solution of folders each in the one before and a project in the last.</summary>
    private static string Nested(int deep)
    {
        static string Id(int number) => $"{{{number:D8}-0000-0000-0000-000000000000}}";
        System.Text.StringBuilder text = new("Microsoft Visual Studio Solution File, Format Version 12.00\n");
        for (int folder = 0; folder < deep; folder++)
        {
            text.Append($"Project(\"{{2150E333-8FDC-42A3-9474-1A3956D46DE8}}\") = \"f\", \"f\", \"{Id(folder)}\"\nEndProject\n");
        }
        text.Append($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"A\", \"a/A.csproj\", \"{Id(deep)}\"\nEndProject\n");
        text.Append("Global\n\tGlobalSection(NestedProjects) = preSolution\n");
        for (int child = 1; child <= deep; child++)
        {
            text.Append($"\t\t{Id(child)} = {Id(child - 1)}\n");
        }
        return text.Append("\tEndGlobalSection\nEndGlobal\n").ToString();
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
