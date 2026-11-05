using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What a file-based program's project names, read from the project the .NET SDK made as MSBuild reads it.</summary>
public class ProgramProjectsTests
{
    [Fact]
    public void A_file_a_directive_includes_is_read_as_msbuild_reads_its_path()
    {
        string file = Path.GetFullPath("/game/per%cent/helpers.cs");

        (List<string> included, _) = ProgramProjects.Named($"<Project><ItemGroup><Compile Include=\"{file.Replace("%", "%25", StringComparison.Ordinal)}\" /></ItemGroup></Project>");

        Assert.Equal([file], included);
    }

    [Fact]
    public void A_glob_a_list_and_a_property_are_no_file_a_directive_named()
    {
        (List<string> included, _) = ProgramProjects.Named("<Project><ItemGroup><Compile Include=\"*.cs\" /><Compile Include=\"a.cs;b.cs\" /><Compile Include=\"$(Root)/a.cs\" /></ItemGroup></Project>");

        Assert.Empty(included);
    }
}
