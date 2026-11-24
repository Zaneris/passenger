using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A file-based program's project as the .NET SDK's answer gives it, and what it names, read from the project the
/// SDK made as MSBuild reads it.</summary>
public class ProgramProjectsTests
{
    [Fact]
    public void A_file_based_programs_project_is_where_the_sdk_builds_it_and_its_directives_name_the_files_msbuild_reads() => Cases.All(
        ("an answer naming no place has the project where that sdk builds it", An_answer_naming_no_place_has_the_project_where_that_sdk_builds_it),
        ("a file a directive includes is read as msbuild reads its path", A_file_a_directive_includes_is_read_as_msbuild_reads_its_path),
        ("an include is the file msbuild compiles where a reference is the file the sdk found", An_include_is_the_file_msbuild_compiles_where_a_reference_is_the_file_the_sdk_found),
        ("a glob a list and a property are no file a directive named", A_glob_a_list_and_a_property_are_no_file_a_directive_named));

    /// <summary>The answer of an SDK that names no place for the project (.NET 10's first), which builds it at app.csproj
    /// beside app.cs, and of one that names its place.</summary>
    private void An_answer_naming_no_place_has_the_project_where_that_sdk_builds_it()
    {
        string program = Path.GetFullPath("/repo/tools/mcp/server.cs");

        ProgramParams unplaced = ProgramProjects.Answered(program, """{"$type":"Project","Version":1,"Content":"<Project />","Diagnostics":[]}""");
        ProgramParams placed = ProgramProjects.Answered(program,
            $$"""{"$type":"Project","Version":1,"Content":"<Project />","ProjectPath":{{System.Text.Json.JsonSerializer.Serialize(program + ".csproj")}},"Diagnostics":[]}""");

        Assert.Equal((Path.GetFullPath("/repo/tools/mcp/server.csproj"), "<Project />", null), (unplaced.Project, unplaced.Content, unplaced.Problem));
        Assert.Equal(program + ".csproj", placed.Project);
    }

    private void A_file_a_directive_includes_is_read_as_msbuild_reads_its_path()
    {
        string file = Path.GetFullPath("/game/per%cent/helpers.cs");

        (List<string> included, _) = ProgramProjects.Named($"<Project><ItemGroup><Compile Include=\"{file.Replace("%", "%25", StringComparison.Ordinal)}\" /></ItemGroup></Project>");

        Assert.Equal([file], included);
    }

    /// <summary>The project the SDK makes of <c>#:include per%2525cent/helpers.cs</c>, which builds per%cent/helpers.cs, and of
    /// <c>#:ref ../per%2525cent/lib.cs</c>, which it opens at per%25cent/lib.cs: each decoded once and written as it stands.</summary>
    private void An_include_is_the_file_msbuild_compiles_where_a_reference_is_the_file_the_sdk_found()
    {
        string written = Path.GetFullPath("/game/per%25cent/helpers.cs");
        string program = Path.GetFullPath("/game/per%25cent/lib.cs");

        (List<string> included, List<string> referenced) = ProgramProjects.Named($"""
            <Project>
              <ItemGroup>
                <Compile Include="{written}" FileBasedProgramsFromIncludeDirective="true" />
                <ProjectReference Include="{program}.csproj" FileBasedProgramsFromRefDirective="{program}" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal([Path.GetFullPath("/game/per%cent/helpers.cs")], included);
        Assert.Equal([program], referenced);
    }

    private void A_glob_a_list_and_a_property_are_no_file_a_directive_named()
    {
        (List<string> included, _) = ProgramProjects.Named("<Project><ItemGroup><Compile Include=\"*.cs\" /><Compile Include=\"a.cs;b.cs\" /><Compile Include=\"$(Root)/a.cs\" /></ItemGroup></Project>");

        Assert.Empty(included);
    }
}
