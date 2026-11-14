using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A solution of a shared project (.shproj) and two projects importing its items: the Solution view shows the
/// shared project with its files in their folders and each importer naming it under its Dependencies, none of its files
/// among the importer's own; a file made, renamed and deleted in its folder writes its items file, each undone in one
/// step; and a shared file opens with the C# of a project importing it.</summary>
public sealed class ShprojTests : IDisposable
{
    private const string Items = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <HasSharedItems>true</HasSharedItems>
            <SharedGUID>6a8c1f2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b</SharedGUID>
          </PropertyGroup>
          <PropertyGroup Label="Configuration">
            <Import_RootNamespace>Shop.Shared</Import_RootNamespace>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="$(MSBuildThisFileDirectory)Greeting.cs" />
            <Compile Include="$(MSBuildThisFileDirectory)Models\Order.cs" />
          </ItemGroup>
        </Project>

        """;

    private const string Order = "namespace Shop.Shared.Models;\n\npublic sealed record Order(int Id, decimal Total);\n";

    private readonly string folder = PathNames.Canonical(TestFolders.Create("shproj-"));
    private readonly string trash = TestFolders.Create("trash-");

    public ShprojTests()
    {
        TestFolders.Write(folder, "Shop.slnx", """
            <Solution>
              <Project Path="Shop.Api/Shop.Api.csproj" />
              <Project Path="Shop.Shared/Shop.Shared.shproj" Id="6a8c1f2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b" />
              <Project Path="Shop.Web/Shop.Web.csproj" />
            </Solution>
            """);
        TestFolders.Write(folder, "Shop.Shared/Shop.Shared.shproj", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="14.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup Label="Globals">
                <ProjectGuid>{6A8C1F2E-3B4D-4E5F-8A9B-0C1D2E3F4A5B}</ProjectGuid>
              </PropertyGroup>
              <Import Project="$(MSBuildExtensionsPath32)\Microsoft\VisualStudio\v$(VisualStudioVersion)\CodeSharing\Microsoft.CodeSharing.Common.Default.props" />
              <Import Project="$(MSBuildExtensionsPath32)\Microsoft\VisualStudio\v$(VisualStudioVersion)\CodeSharing\Microsoft.CodeSharing.Common.props" />
              <Import Project="Shop.Shared.projitems" Label="Shared" />
              <Import Project="$(MSBuildExtensionsPath32)\Microsoft\VisualStudio\v$(VisualStudioVersion)\CodeSharing\Microsoft.CodeSharing.CSharp.targets" />
            </Project>
            """);
        TestFolders.Write(folder, "Shop.Shared/Shop.Shared.projitems", Items);
        TestFolders.Write(folder, "Shop.Shared/Greeting.cs", "namespace Shop.Shared;\n\npublic static class Greeting\n{\n    public static string Of(string name) => $\"Hello, {name}\";\n}\n");
        TestFolders.Write(folder, "Shop.Shared/Models/Order.cs", Order);
        foreach (string name in (string[])["Api", "Web"])
        {
            TestFolders.Write(folder, $"Shop.{name}/Shop.{name}.csproj",
                TestProjects.Console().With("  <Import Project=\"..\\Shop.Shared\\Shop.Shared.projitems\" Label=\"Shared\" />\n"));
            TestFolders.Write(folder, $"Shop.{name}/Program.cs", $"System.Console.WriteLine(Shop.Shared.Greeting.Of(\"{name}\"));\n");
        }
        Trash.Folder = trash;
    }

    public void Dispose()
    {
        Trash.Folder = null;
        TestFolders.Delete(folder);
        TestFolders.Delete(trash);
    }

    private string Shared => Path.Combine(folder, "Shop.Shared");

    private string ItemsFile => Path.Combine(Shared, "Shop.Shared.projitems");

    [AvaloniaFact]
    public void A_shared_project_shows_its_files_in_their_folders_and_each_project_importing_it_names_it_under_its_dependencies()
    {
        MainWindow window = Open();
        try
        {
            ExplorerNode shared = Row(window, "Shop.Shared");
            Assert.True(shared is { Kind: NodeKind.Project, Detail: "shared" }, $"Shop.Shared shows as a {shared.Kind} saying '{shared.Detail}', not as a shared project");
            Assert.Equal(["Models", "Greeting.cs"], shared.Children!.Select(r => r.Name));
            Assert.Equal(["Order.cs"], shared.Children![0].Children!.Select(r => r.Name));
            foreach (string importing in (string[])["Shop.Api", "Shop.Web"])
            {
                ExplorerNode project = Row(window, importing);
                Assert.True(project.Children!.Select(r => r.Name).SequenceEqual(["Dependencies", "Program.cs"]),
                    $"{importing} lists the shared files among its own: {string.Join(", ", project.Children!.Select(r => r.Name))}");
                ExplorerNode named = Assert.Single(project.Children![0].Children!, r => r.Kind == NodeKind.SharedProject);
                Assert.Equal(("Shop.Shared", Path.Combine(Shared, "Shop.Shared.shproj")), (named.Name, named.Referenced));
                Assert.False(named.HasChildren, "The shared project under Dependencies holds its files again, which the solution shows under the shared project");
            }

            string greeting = Path.Combine(Shared, "Greeting.cs");
            window.ExplorerView.Reveal(greeting);
            Ui.Settle();
            Assert.True(window.ExplorerView.Selected is { Path: { } chosen, Depth: 2 } && chosen == greeting,
                $"Greeting.cs was revealed at {window.ExplorerView.Selected?.Path} (depth {window.ExplorerView.Selected?.Depth}), not under Shop.Shared");

            // Find in Files on the shared project's row searches its files, each listed under it; on an importer's, none of them.
            SearchQuery query = new("namespace");
            FilesFound inShared = Ui.Await(window.FindInFilesAsync(query, window.ScopeOf([shared])!, CancellationToken.None), "the search of Shop.Shared");
            Assert.Equal([(greeting, "Shop.Shared"), (Path.Combine(Shared, "Models", "Order.cs"), "Shop.Shared")],
                inShared.Matches.Select(m => (m.Path, window.ProjectNameOf(m.Path))).Order());
            FilesFound inApi = Ui.Await(window.FindInFilesAsync(new SearchQuery("Greeting"), window.ScopeOf([Row(window, "Shop.Api")])!, CancellationToken.None), "the search of Shop.Api");
            Assert.Equal([Path.Combine(folder, "Shop.Api", "Program.cs")], inApi.Matches.Select(m => m.Path).Distinct());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_file_made_renamed_and_deleted_in_a_shared_project_writes_its_items_file_and_each_is_undone_in_one_step()
    {
        MainWindow window = Open();
        List<string> asked = [];
        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(true);
        };
        try
        {
            string models = Path.Combine(Shared, "Models");
            Assert.Null(window.CreateClass(models, "Customer"));
            string customer = Path.Combine(models, "Customer.cs");
            Assert.Equal("namespace Shop.Shared.Models;\n\npublic class Customer\n{\n}\n", File.ReadAllText(customer));
            Assert.Equal(Items.Replace("Models\\Order.cs\" />", "Models\\Order.cs\" />\n    <Compile Include=\"$(MSBuildThisFileDirectory)Models\\Customer.cs\" />", StringComparison.Ordinal),
                File.ReadAllText(ItemsFile));
            Ui.WaitFor(() => Names(Row(window, "Shop.Shared").Children![0]).SequenceEqual(["Customer.cs", "Order.cs"]),
                () => $"Customer.cs under Shop.Shared's Models; it holds {string.Join(", ", Names(Row(window, "Shop.Shared").Children![0]))}");
            Undo(window, models);
            Assert.False(File.Exists(customer), $"Undo left Customer.cs: {window.Status}");
            Assert.Equal(Items, File.ReadAllText(ItemsFile));

            string greeting = Path.Combine(Shared, "Greeting.cs");
            Assert.Null(Ui.Await(window.RenameAsync(greeting, "Welcome.cs"), "the rename"));
            Assert.Equal(Items.Replace("Greeting.cs", "Welcome.cs", StringComparison.Ordinal), File.ReadAllText(ItemsFile));
            Undo(window, models);
            Assert.True(File.Exists(greeting), $"Undo did not rename Welcome.cs back: {window.Status}");
            Assert.Equal(Items, File.ReadAllText(ItemsFile));

            string order = Path.Combine(models, "Order.cs");
            Assert.True(Ui.Await(window.DeleteAsync(order), "the delete"), window.Status);
            Assert.False(File.Exists(order));
            Assert.Equal(Items.Replace("    <Compile Include=\"$(MSBuildThisFileDirectory)Models\\Order.cs\" />\n", string.Empty, StringComparison.Ordinal), File.ReadAllText(ItemsFile));
            Undo(window, models);
            Assert.True(File.Exists(order), $"Undo did not put Order.cs back: {window.Status}");
            Assert.Equal(Order, File.ReadAllText(order));
            Assert.Equal(Items, File.ReadAllText(ItemsFile));
            Assert.Equal(["Undo", "Undo", "Delete", "Undo"], asked);
            Assert.True(File.ReadAllText(Path.Combine(folder, "Shop.Api", "Shop.Api.csproj")).EndsWith("Label=\"Shared\" />\n</Project>\n", StringComparison.Ordinal),
                "An importing project's file was written");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_shared_file_opens_with_the_completion_and_diagnostics_of_a_project_importing_it()
    {
        string greeting = TestFolders.Write(folder, "Shop.Shared/Greeting.cs",
            "namespace Shop.Shared;\n\npublic static class Greeting\n{\n    public static string Upper(string name) => name;\n\n    public static int Wrong => \"x\";\n}\n");
        Restore.Run(Path.Combine(folder, "Shop.Api", "Shop.Api.csproj"));
        Restore.Run(Path.Combine(folder, "Shop.Web", "Shop.Web.csproj"));
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.OpenFile(greeting);
            OpenDocument document = window.EditorArea.Active!;
            Ui.WaitFor(() => Errors(window, document) == "CS0029", () => $"the one error of Wrong, its string no int, as a project importing the file compiles it; it says {Errors(window, document)}",
                TimeSpan.FromMinutes(2));

            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "=> name");
            window.KeyTextInput(".");
            Ui.WaitFor(() => window.Assist.IsCompletionOpen && window.Assist.CompletionLabels.Contains("ToUpper"),
                () => $"string's members after name.; the list shows {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
        }
        finally
        {
            window.Close();
        }

        static string Errors(MainWindow window, OpenDocument document) =>
            string.Join("; ", window.CSharpService?.For(document)?.Diagnostics.Where(d => d.Severity == Passenger.Lsp.DiagnosticSeverity.Error).Select(d => d.Code) ?? ["nothing"]);
    }

    /// <summary>The folder open, its projects evaluated.</summary>
    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.WaitFor(() => window.Projects.Count == 2 && window.Projects.All(p => p.IsEvaluated), "the projects importing the shared project evaluated", TimeSpan.FromMinutes(1));
        return window;
    }

    /// <summary>The Solution view's row of a project, as it shows now.</summary>
    private static ExplorerNode Row(MainWindow window, string name) => window.ExplorerView.Tree.Root!.Children!.Single(r => r.Name == name);

    private static IEnumerable<string> Names(ExplorerNode node) => node.Children!.Select(r => r.Name);

    /// <summary>Undo from the Explorer, a row chosen in it.</summary>
    private static void Undo(MainWindow window, string chosen)
    {
        window.ExplorerView.Reveal(chosen);
        Ui.Settle();
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == chosen));
        Ui.Run(window, EditorActions.Undo);
    }
}
