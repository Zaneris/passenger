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

    /// <summary>The project the SDK makes of <c>#:include per%2525cent/helpers.cs</c>, which builds per%cent/helpers.cs, and of
    /// <c>#:ref ../per%2525cent/lib.cs</c>, which it opens at per%25cent/lib.cs: each decoded once and written as it stands.</summary>
    [Fact]
    public void An_include_is_the_file_msbuild_compiles_where_a_reference_is_the_file_the_sdk_found()
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

    [Fact]
    public void A_glob_a_list_and_a_property_are_no_file_a_directive_named()
    {
        (List<string> included, _) = ProgramProjects.Named("<Project><ItemGroup><Compile Include=\"*.cs\" /><Compile Include=\"a.cs;b.cs\" /><Compile Include=\"$(Root)/a.cs\" /></ItemGroup></Project>");

        Assert.Empty(included);
    }
}
