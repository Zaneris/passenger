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
