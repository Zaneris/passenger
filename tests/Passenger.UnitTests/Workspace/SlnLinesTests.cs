using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A classic .sln read by one grammar (<see cref="SlnLines"/>): the Solution view and its edits find the same projects,
/// folders and files in it, a file written by hand included.</summary>
public sealed class SlnLinesTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_project_whose_block_has_no_end_leaves_the_next_project_to_be_shown_and_taken_out()
    {
        string solution = Write("Shop.sln", "Microsoft Visual Studio Solution File, Format Version 12.00\r\n"
            + "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"A\", \"A\\A.csproj\", \"{A1000000-0000-0000-0000-000000000001}\"\r\n"
            + "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"B\", \"B\\B.csproj\", \"{A1000000-0000-0000-0000-000000000002}\"\r\n"
            + "EndProject\r\nGlobal\r\nEndGlobal\r\n");
        Assert.Equal([At("A", "A.csproj"), At("B", "B.csproj")], SolutionModel.Load(solution)!.Projects.Select(p => p.Path));

        string? refused = SolutionEdits.RemoveProject(solution, At("B", "B.csproj"));
        Assert.True(refused is null, $"Taking out a project the Solution view shows said '{refused}'");
        Assert.Equal([At("A", "A.csproj")], SolutionModel.Load(solution)!.Projects.Select(p => p.Path));
    }

    [Fact]
    public void A_folders_files_and_nesting_written_in_lower_case_are_read_and_followed_alike()
    {
        string solution = Write("Shop.sln", "Microsoft Visual Studio Solution File, Format Version 12.00\n"
            + "Project(\"{2150e333-8fdc-42a3-9474-1a3956d46de8}\") = \"docs\", \"docs\", \"{b1000000-0000-0000-0000-00000000000a}\"\n"
            + "\tProjectSection(SolutionItems) = preProject\n\t\tREADME.md = README.md  \n\tEndProjectSection\nEndProject\n"
            + "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"inner\", \"inner\", \"{B1000000-0000-0000-0000-00000000000B}\"\nEndProject\n"
            + "Global\n\tGlobalSection(NestedProjects) = preSolution\n\t\t{B1000000-0000-0000-0000-00000000000B} = {B1000000-0000-0000-0000-00000000000A}\n"
            + "\tEndGlobalSection\nEndGlobal\n");
        SolutionFolder docs = Assert.IsType<SolutionFolder>(Assert.Single(SolutionModel.Load(solution)!.Entries));
        Assert.Equal(["README.md", "inner"], docs.Children.Select(c => c.Name));

        Assert.Null(SolutionEdits.RemoveFile(solution, "/docs/", At("README.md")));
        Assert.Null(SolutionEdits.AddFile(solution, "/docs/inner/", At("README.md")));
        SolutionFolder moved = Assert.IsType<SolutionFolder>(Assert.Single(SolutionModel.Load(solution)!.Entries));
        SolutionFolder inner = Assert.IsType<SolutionFolder>(Assert.Single(moved.Children));
        Assert.Equal(At("README.md"), Assert.IsType<SolutionItem>(Assert.Single(inner.Children)).Path);
    }

    private string At(params string[] parts) => Path.Combine([root, .. parts]);

    private string Write(string relative, string text)
    {
        string path = At(relative);
        File.WriteAllText(path, text);
        return path;
    }
}
