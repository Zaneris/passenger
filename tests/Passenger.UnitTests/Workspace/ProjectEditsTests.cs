using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A project's file following a folder moved: every path it names from its folder names where that is now, the
/// moved project's paths out of it and another project's into it alike, and what names no file of its own (an SDK's
/// import, a property) stays as written.</summary>
public sealed class ProjectEditsTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_project_moved_deeper_follows_its_imports_and_hint_paths_and_another_projects_import_into_it_follows_it()
    {
        const string App = """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="..\..\build\common.props" Condition="Exists('..\..\build\common.props')" />
              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
              <Import Project="$(MSBuildThisFileDirectory)..\..\build\common.props" />
              <ImportGroup>
                <Import Project="../../build/targets/*.targets" />
              </ImportGroup>
              <Import Project="App.props" />
              <ItemGroup>
                <Reference Include="Native">
                  <HintPath>..\..\lib\Native.dll</HintPath>
                </Reference>
                <Reference Include="Other" HintPath="..\..\lib\Other.dll" />
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>

            """;
        const string Tool = """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="..\App\App.props" />
              <Import Project="..\..\build\common.props" />
              <ItemGroup>
                <Reference Include="Shared" HintPath="..\App\lib\Shared.dll" />
              </ItemGroup>
            </Project>

            """;
        const string Lib = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <Import Project=\"..\\..\\build\\common.props\" />\n</Project>\n";
        Write("src/App/App.csproj", App);
        Write("src/Tool/Tool.csproj", Tool);
        Write("src/Lib/Lib.csproj", Lib);
        string from = Path.Combine(root, "src", "App");
        string to = Path.Combine(root, "src", "apps", "App");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        Directory.Move(from, to);
        (string, string)[] moves = [(from, to)];

        string app = Path.Combine(to, "App.csproj");
        Assert.True(ProjectEdits.MoveItems(app, moves), "The moved project did not change, so every path it names outside its folder is one level short");
        Assert.Equal(App.Replace("\"..\\..\\build", "\"..\\..\\..\\build", StringComparison.Ordinal)
            .Replace("'..\\..\\build", "'..\\..\\..\\build", StringComparison.Ordinal)
            .Replace("\"../../build/targets", "\"../../../build/targets", StringComparison.Ordinal)
            .Replace("$(MSBuildThisFileDirectory)..\\..\\build", "$(MSBuildThisFileDirectory)..\\..\\..\\build", StringComparison.Ordinal)
            .Replace("..\\..\\lib\\", "..\\..\\..\\lib\\", StringComparison.Ordinal)
            .Replace("..\\Lib\\Lib.csproj", "..\\..\\Lib\\Lib.csproj", StringComparison.Ordinal), File.ReadAllText(app));

        string tool = Path.Combine(root, "src", "Tool", "Tool.csproj");
        Assert.True(ProjectEdits.MoveItems(tool, moves), "Tool's import of a file in the moved folder did not follow it, so Tool no longer builds");
        Assert.Equal(Tool.Replace("..\\App\\", "..\\apps\\App\\", StringComparison.Ordinal), File.ReadAllText(tool));

        string lib = Path.Combine(root, "src", "Lib", "Lib.csproj");
        Assert.False(ProjectEdits.MoveItems(lib, moves), "A project naming nothing in the moved folder was written");
        Assert.Equal(Lib, File.ReadAllText(lib));
    }

    /// <summary>A file a project names, renamed to a name holding an ampersand, is named escaped, so the project still reads
    /// as XML and names it; and one already named escaped is found by its name and followed as it moves.</summary>
    [Fact]
    public void A_file_whose_name_holds_an_ampersand_is_named_escaped_as_it_moves_and_the_project_still_reads_as_xml()
    {
        const string App = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="A.cs" />
                <Compile Include="A.Designer.cs" DependentUpon="A.cs" />
                <Compile Include='Tom&amp;Jerry.cs' />
                <None Include="Tom&amp;Jerry.json">
                  <DependentUpon>Tom&amp;Jerry.cs</DependentUpon>
                </None>
              </ItemGroup>
            </Project>

            """;
        Write("App/App.csproj", App);
        string folder = Path.Combine(root, "App");
        string project = Path.Combine(folder, "App.csproj");

        Assert.True(ProjectEdits.MoveItems(project, [(Path.Combine(folder, "A.cs"), Path.Combine(folder, "A&B.cs"))]), "The project did not follow A.cs renamed to A&B.cs");
        string renamed = App.Replace("\"A.cs\"", "\"A&amp;B.cs\"", StringComparison.Ordinal);
        Assert.Equal(renamed, File.ReadAllText(project));
        Assert.Equal(["A&B.cs", "A.Designer.cs", "Tom&Jerry.cs", "Tom&Jerry.json", "A&B.cs", "Tom&Jerry.cs"], Named(project));

        Assert.True(ProjectEdits.MoveItems(project, [(Path.Combine(folder, "Tom&Jerry.cs"), Path.Combine(folder, "Cartoons", "Tom&Jerry.cs"))]),
            "The project did not follow Tom&Jerry.cs, which it names as Tom&amp;Jerry.cs, into Cartoons, so it names a file no longer there");
        Assert.Equal(renamed.Replace("'Tom&amp;Jerry.cs'", "'Cartoons/Tom&amp;Jerry.cs'", StringComparison.Ordinal)
            .Replace(">Tom&amp;Jerry.cs<", ">Cartoons/Tom&amp;Jerry.cs<", StringComparison.Ordinal), File.ReadAllText(project));
        Assert.Equal(["A&B.cs", "A.Designer.cs", "Cartoons/Tom&Jerry.cs", "Tom&Jerry.json", "A&B.cs", "Cartoons/Tom&Jerry.cs"], Named(project));
    }

    [Fact]
    public void Files_left_out_of_a_projects_build_are_removed_once_in_an_item_group_of_their_own()
    {
        const string Game = """
            <Project Sdk="Godot.NET.Sdk/4.7.2">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                <Compile Remove="script_templates/**" />
              </ItemGroup>
            </Project>

            """;
        Write("Game.csproj", Game);
        string project = Path.Combine(root, "Game.csproj");

        Assert.False(ProjectEdits.RemovesCompiled(project, "script_templates"), "A removal under a condition was read as every build's");
        Assert.Null(ProjectEdits.RemoveCompiled(project, "script_templates"));
        Assert.True(ProjectEdits.RemovesCompiled(project, "script_templates"), "The removal written was not read back");
        string removed = Game.Replace("""
              </ItemGroup>
            </Project>
            """, """
              </ItemGroup>
              <ItemGroup>
                <Compile Remove="script_templates/**" />
              </ItemGroup>
            </Project>
            """, StringComparison.Ordinal);
        Assert.Equal(removed, File.ReadAllText(project));
        Assert.Null(ProjectEdits.RemoveCompiled(project, "script_templates"));
        Assert.True(removed == File.ReadAllText(project), "The files were removed again, so each write of the templates adds an item group to the project");
    }

    [Fact]
    public void A_removal_with_a_condition_of_its_own_leaves_the_files_in_other_builds_so_one_for_every_build_is_added()
    {
        const string Game = """
            <Project Sdk="Godot.NET.Sdk/4.7.2">
              <ItemGroup>
                <Compile Remove="script_templates/**" Condition="'$(Configuration)' == 'Release'" />
              </ItemGroup>
            </Project>

            """;
        Write("Game.csproj", Game);
        string project = Path.Combine(root, "Game.csproj");

        Assert.False(ProjectEdits.RemovesCompiled(project, "script_templates"), "A removal under its own condition was read as every build's, so Debug builds the templates");
        Assert.Null(ProjectEdits.RemoveCompiled(project, "script_templates"));
        Assert.Equal(Game.Replace("""
              </ItemGroup>
            </Project>
            """, """
              </ItemGroup>
              <ItemGroup>
                <Compile Remove="script_templates/**" />
              </ItemGroup>
            </Project>
            """, StringComparison.Ordinal), File.ReadAllText(project));
    }

    /// <summary>A reference into a folder MSBuild would misread is written so it reads back, and read back for what follows.</summary>
    [Fact]
    public void A_reference_to_a_project_in_a_folder_of_msbuilds_own_characters_is_read_back_as_that_project()
    {
        Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n");
        Write("odd;100%41$@/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n");
        string app = Path.Combine(root, "App", "App.csproj");
        string lib = Path.Combine(root, "odd;100%41$@", "Lib.csproj");

        Assert.Null(ProjectEdits.AddReference(app, lib));
        Assert.Equal([@"..\odd%3B100%2541%24%40\Lib.csproj"], Named(app));
        Assert.NotNull(ProjectEdits.AddReference(app, lib));
        Assert.Null(ProjectEdits.RemoveReference(app, lib));
        Assert.Empty(Named(app));
    }

    [Fact]
    public void A_folder_of_msbuilds_own_characters_is_left_out_by_a_glob_msbuild_reads_and_once_only()
    {
        Write("Game.csproj", "<Project Sdk=\"Godot.NET.Sdk/4.7.2\">\n</Project>\n");
        string project = Path.Combine(root, "Game.csproj");
        const string Folder = "game;one%41/script_templates";

        Assert.False(ProjectEdits.RemovesCompiled(project, Folder), "The folder is left out by nothing yet");
        Assert.Null(ProjectEdits.RemoveCompiled(project, Folder));
        Assert.Contains("<Compile Remove=\"game%3Bone%252541/script_templates/**\" />", File.ReadAllText(project), StringComparison.Ordinal);
        Assert.True(ProjectEdits.RemovesCompiled(project, Folder), "The removal written was not read back, so each write of the templates adds another");
    }

    /// <summary>A path written escaped is read as the folder it names, and one moving into a folder of such a name is
    /// written so MSBuild reads it: what follows a move once followed none of these.</summary>
    [Fact]
    public void A_move_follows_paths_written_escaped_and_escapes_the_paths_it_writes()
    {
        const string App = """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="..\old%3Bone\common.props" Condition="Exists('..\old%3Bone\common.props')" />
              <ItemGroup>
                <ProjectReference Include="..\old%3Bone\Lib.csproj" />
                <Compile Remove="../old%3Bone/gen/**" />
                <None Include="..\old%3Bone\a.txt;..\old%3Bone\b.txt" />
                <None Include="$(Shared)\old%3Bone\c.txt" />
                <Reference Include="Native">
                  <HintPath>..\old%3Bone\Native.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>

            """;
        Write("App/App.csproj", App);
        Write("old;one/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n");
        string from = Path.Combine(root, "old;one");
        string to = Path.Combine(root, "new@two%");
        Directory.Move(from, to);

        Assert.True(ProjectEdits.MoveItems(Path.Combine(root, "App", "App.csproj"), [(from, to)]), "No path followed the folder, so each names a folder no longer there");
        Assert.Equal("""
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="..\new%40two%25\common.props" Condition="Exists('..\new%40two%25\common.props')" />
              <ItemGroup>
                <ProjectReference Include="..\new%40two%25\Lib.csproj" />
                <Compile Remove="../new%40two%2525/gen/**" />
                <None Include="..\old%3Bone\a.txt;..\old%3Bone\b.txt" />
                <None Include="$(Shared)\old%3Bone\c.txt" />
                <Reference Include="Native">
                  <HintPath>..\new%40two%25\Native.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>

            """, File.ReadAllText(Path.Combine(root, "App", "App.csproj")));
    }

    [Fact]
    public void A_folder_with_a_quote_in_its_name_is_written_escaped_only_inside_a_condition()
    {
        const string App = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <Import Project=\"..\\old\\common.props\" Condition=\"Exists('..\\old\\common.props')\" />\n</Project>\n";
        Write("App/App.csproj", App);
        Write("old/common.props", "<Project />\n");
        string from = Path.Combine(root, "old");
        string to = Path.Combine(root, "Bob's");
        Directory.Move(from, to);

        Assert.True(ProjectEdits.MoveItems(Path.Combine(root, "App", "App.csproj"), [(from, to)]), "The import did not follow its folder");
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <Import Project=\"..\\Bob's\\common.props\" Condition=\"Exists('..\\Bob%27s\\common.props')\" />\n</Project>\n",
            File.ReadAllText(Path.Combine(root, "App", "App.csproj")));
    }

    /// <summary>What an item names beside its file is read and written escaped as the file is.</summary>
    [Fact]
    public void A_file_named_beside_an_item_follows_it_through_a_folder_of_msbuilds_own_characters()
    {
        const string App = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="Forms\Main.Designer.cs" DependentUpon="Main.cs" />
              </ItemGroup>
            </Project>

            """;
        Write("App.csproj", App);
        Write("Forms/Main.Designer.cs", "// designer\n");
        Write("Forms/Main.cs", "// code\n");
        string from = Path.Combine(root, "Forms", "Main.cs");
        string to = Path.Combine(root, "Forms", "a;b", "Main.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);

        Assert.True(ProjectEdits.MoveItems(Path.Combine(root, "App.csproj"), [(from, to)]), "The designer file's DependentUpon names a file no longer there");
        Assert.Contains("DependentUpon=\"a%3Bb/Main.cs\"", File.ReadAllText(Path.Combine(root, "App.csproj")), StringComparison.Ordinal);
    }

    /// <summary>The paths a project's items name, as XML reads them: each Include, then each DependentUpon.</summary>
    private static string[] Named(string project)
    {
        System.Xml.Linq.XDocument read = System.Xml.Linq.XDocument.Parse(File.ReadAllText(project));
        return [.. read.Descendants().Select(e => (string?)e.Attribute("Include")).OfType<string>(),
            .. read.Descendants().Select(e => (string?)e.Attribute("DependentUpon") ?? (e.Name.LocalName == "DependentUpon" ? e.Value : null)).OfType<string>()];
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
