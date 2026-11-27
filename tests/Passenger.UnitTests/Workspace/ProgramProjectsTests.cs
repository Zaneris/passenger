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
        ("a glob a list and a property are no file a directive named", A_glob_a_list_and_a_property_are_no_file_a_directive_named),
        ("a directives value is read apart from its item metadata as the sdk reads it (#2333)", A_directives_value_is_read_apart_from_its_item_metadata_as_the_sdk_reads_it),
        ("a files project is found among many making nothing the nearest folder or the program it is", A_files_project_is_found_among_many_making_nothing_the_nearest_folder_or_the_program_it_is));

    /// <summary>A file's project is looked up at each key in it: among 200 projects as among one it makes nothing, where it
    /// made each project's folder from its path twice and its extension once; the nearest folder's project, a program
    /// whose file it is, and none for a project passed over.</summary>
    private void A_files_project_is_found_among_many_making_nothing_the_nearest_folder_or_the_program_it_is()
    {
        string root = OperatingSystem.IsWindows() ? @"C:\shop" : "/shop";
        static ProjectModel Project(string path) => new(path, Path.GetFileNameWithoutExtension(path), null, [], [], [], Exists: true);
        ProjectModel app = Project(Path.Combine(root, "App", "App.csproj"));
        ProjectModel nested = Project(Path.Combine(root, "App", "Tools", "Tools.csproj"));
        ProjectModel program = Project(Path.Combine(root, "Scripts", "run.cs"));
        IReadOnlyList<ProjectModel> one = [app];
        IReadOnlyList<ProjectModel> many = [.. Enumerable.Range(0, 197).Select(i => Project(Path.Combine(root, $"Lib{i}", $"Lib{i}.csproj"))), app, nested, program];
        string file = Path.Combine(root, "App", "Tools", "Tool.cs");
        Assert.Same(nested, ProjectModel.Owning(many, file));
        Assert.Same(app, ProjectModel.Owning(many, file, new HashSet<string> { nested.Path }));
        Assert.Same(program, ProjectModel.Owning(many, program.Path));
        long before = GC.GetAllocatedBytesForCurrentThread();
        ProjectModel.Owning(one, file);
        long few = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        ProjectModel.Owning(many, file);
        long all = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(few == 0 && all == 0, $"Finding a file's project allocated {few:N0} bytes among one project and {all:N0} among 200, at each key in the file");
        Assert.Equal(Path.Combine(root, "Other"), (app with { Path = Path.Combine(root, "Other", "App.csproj") }).Folder);
    }

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

    /// <summary>Each directive's value as .NET 11's SDK reads it, its project made by <c>dotnet run-api</c>: item metadata
    /// after a path or a package is its own, a quoted part a C# string's, and with no quote, a rest whose parts are not all
    /// <c>Name=Value</c> one value, white space and all; a quote left open names nothing.</summary>
    private void A_directives_value_is_read_apart_from_its_item_metadata_as_the_sdk_reads_it() => Cases.Each<(string Directive, string Value, string Written)>(
    [
        ("#:ref ./libs/library.cs Aliases=Library", "./libs/library.cs", "./libs/library.cs"),
        ("#:ref \"./my libs/library.cs\" Aliases=\"Lib One\"", "./my libs/library.cs", "\"./my libs/library.cs\""),
        ("#:package Humanizer@2.14.1 PrivateAssets=all", "Humanizer@2.14.1", "Humanizer@2.14.1"),
        ("#:ref ./libs/library.cs Aliases = Library", "./libs/library.cs Aliases = Library", "./libs/library.cs Aliases = Library"),
        ("#:include \"C:\\\\src\\\\a.cs\"", "C:\\src\\a.cs", "\"C:\\\\src\\\\a.cs\""),
        ("#:include \"open.cs", string.Empty, string.Empty),
    ], row =>
    {
        ProgramFiles.Directive read = ProgramFiles.Read(row.Directive)!;
        string written = row.Directive.Substring(read.Start, read.Length);
        Assert.True((read.Value, written) == (row.Value, row.Written),
            $"{row.Directive} was read as the value {read.Value} written as {written}, so a program finds and moves the wrong file");
    }, row => row.Directive);
}
