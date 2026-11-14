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
/// among the importer's own; a file made, renamed, deleted, included or excluded in its folder writes its items file, each
/// undone in one step; an import is taken and dropped through Add Reference and the Dependencies; Add Existing Project adds
/// a .shproj; a test in a shared file is found from code; and a shared file opens with the C# of a project importing it.</summary>
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
        Write(folder);
        Trash.Folder = trash;
    }

    /// <summary>The solution of a shared project and the two projects importing it, in a folder.</summary>
    public static void Write(string folder)
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
            Assert.Equal("Shared project: its files are compiled into Shop.Api and Shop.Web, which import Shop.Shared.projitems", shared.Tip);
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

    [AvaloniaFact]
    public void A_file_its_items_file_leaves_out_shows_dimmed_and_include_and_exclude_write_the_items_file_each_undone_in_one_step()
    {
        string notes = TestFolders.Write(folder, "Shop.Shared/Notes.md", "# Notes\n");
        string helper = TestFolders.Write(folder, "Shop.Shared/Extra/Helper.cs", "namespace Shop.Shared.Extra;\n\npublic static class Helper\n{\n}\n");
        MainWindow window = Open();
        List<string> asked = Answered(window);
        try
        {
            ExplorerNode shared = Row(window, "Shop.Shared");
            Assert.Equal([("Extra", false), ("Models", false), ("Greeting.cs", false), ("Notes.md", true)], shared.Children!.Select(r => (r.Name, r.Unlisted)));
            Assert.True(shared.Children![3].IsDimmed, "A file the items file leaves out shows as one it names does");
            ExplorerNode left = Assert.Single(shared.Children![0].Children!);
            Assert.True(left is { Name: "Helper.cs", Unlisted: true }, $"Extra holds {left.Name}, unlisted {left.Unlisted}");

            Choose(window, helper);
            Ui.ChooseFromRowMenu(window, window.ExplorerView.Selected!, "Include in Project");
            string included = Items.Replace("Models\\Order.cs\" />", "Models\\Order.cs\" />\n    <Compile Include=\"$(MSBuildThisFileDirectory)Extra\\Helper.cs\" />", StringComparison.Ordinal);
            Ui.WaitFor(() => File.ReadAllText(ItemsFile) == included, () => $"Helper.cs written into the items file: {window.Status}\n{File.ReadAllText(ItemsFile)}");
            Ui.WaitFor(() => Row(window, "Shop.Shared").Children![0].Children!.Single() is { Unlisted: false, IsDimmed: false }, "Helper.cs shown as the shared project's own");
            Undo(window, Path.Combine(Shared, "Models"));
            Assert.Equal(Items, File.ReadAllText(ItemsFile));

            string greeting = Path.Combine(Shared, "Greeting.cs");
            Choose(window, greeting);
            Ui.ChooseFromRowMenu(window, window.ExplorerView.Selected!, "Exclude from Project");
            Ui.WaitFor(() => File.ReadAllText(ItemsFile) == Items.Replace("    <Compile Include=\"$(MSBuildThisFileDirectory)Greeting.cs\" />\n", string.Empty, StringComparison.Ordinal),
                () => $"Greeting.cs taken out of the items file: {window.Status}\n{File.ReadAllText(ItemsFile)}");
            Assert.True(File.Exists(greeting), "Exclude from Project deleted the file");
            Ui.WaitFor(() => Row(window, "Shop.Shared").Children!.Single(r => r.Name == "Greeting.cs").Unlisted, "Greeting.cs shown as left out");
            Undo(window, Path.Combine(Shared, "Models"));
            Assert.Equal(Items, File.ReadAllText(ItemsFile));
            Assert.Equal(["Undo", "Undo"], asked);
            Assert.True(File.Exists(notes));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_project_takes_and_drops_a_shared_projects_import_through_add_reference_and_its_dependencies_each_undone_in_one_step()
    {
        string api = Path.Combine(folder, "Shop.Api", "Shop.Api.csproj");
        string web = Path.Combine(folder, "Shop.Web", "Shop.Web.csproj");
        string imports = File.ReadAllText(api);
        string alone = imports.Replace("  <Import Project=\"..\\Shop.Shared\\Shop.Shared.projitems\" Label=\"Shared\" />\n", string.Empty, StringComparison.Ordinal);
        MainWindow window = Open();
        List<string> asked = Answered(window);
        try
        {
            Ui.ChooseFromRowMenu(window, Row(window, "Shop.Api"), "Add", "Reference…");
            AddReferenceDialog dialog = Assert.Single(window.OwnedWindows.OfType<AddReferenceDialog>());
            ReferenceChoice offered = Assert.Single(dialog.Choices, c => c.Name == "Shop.Shared");
            Assert.True(offered is { IsChosen: true, CanChoose: true } && offered.Detail.StartsWith("shared", StringComparison.Ordinal),
                $"Shop.Shared was offered chosen {offered.IsChosen}, saying {offered.Detail}, where Shop.Api imports it");
            offered.IsChosen = false;
            dialog.Accept();
            Ui.WaitFor(() => File.ReadAllText(api) == alone, () => $"Shop.Api's import taken out: {window.Status}\n{File.ReadAllText(api)}");
            Ui.WaitFor(() => window.Projects.Single(p => p.Path == api).Evaluated?.Inputs.Any(i => i.Path.EndsWith(".projitems", StringComparison.Ordinal)) == false,
                "Shop.Api evaluated without the import");
            Ui.WaitFor(() => Names(Row(window, "Shop.Api")).Contains("Program.cs") && !Dependencies(window, "Shop.Api").Any(r => r.Kind == NodeKind.SharedProject),
                () => $"Shop.Api's Dependencies without Shop.Shared: {string.Join(", ", Dependencies(window, "Shop.Api").Select(r => r.Name))}");
            Undo(window, Path.Combine(folder, "Shop.Api", "Program.cs"));
            Assert.Equal(imports, File.ReadAllText(api));

            Ui.WaitFor(() => Dependencies(window, "Shop.Web").Any(r => r.Kind == NodeKind.SharedProject), "Shop.Web's Dependencies naming Shop.Shared");
            foreach (ExplorerNode opened in (ExplorerNode[])[Row(window, "Shop.Web"), Row(window, "Shop.Web").Children![0]])
            {
                Ui.ClickRow(window, opened);
                Ui.Press(window, "RIGHT");
            }
            Ui.ClickRow(window, Dependencies(window, "Shop.Web").Single(r => r.Kind == NodeKind.SharedProject));
            Ui.Press(window, OperatingSystem.IsMacOS() ? "BACK_SPACE" : "DELETE");
            Ui.WaitFor(() => File.ReadAllText(web) == alone, () => $"Shop.Web's import taken out: {window.Status}\n{File.ReadAllText(web)}");
            Ui.WaitFor(() => window.Projects.Single(p => p.Path == web).Evaluated?.Inputs.Any(i => i.Path.EndsWith(".projitems", StringComparison.Ordinal)) == false,
                "Shop.Web evaluated without the import");
            Assert.Null(Ui.Await(window.SetReferencesAsync(web, [Path.Combine(Shared, "Shop.Shared.shproj")]), "the import added"));
            Assert.Equal(imports, File.ReadAllText(web));
            Assert.Equal(["Undo", "Remove Shared Project"], asked);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Add_existing_project_adds_a_shared_project_to_the_solution_shown_as_one()
    {
        string solution = Path.Combine(folder, "Shop.slnx");
        string listed = File.ReadAllText(solution);
        File.WriteAllText(solution, listed.Replace("  <Project Path=\"Shop.Shared/Shop.Shared.shproj\" Id=\"6a8c1f2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b\" />\n", string.Empty, StringComparison.Ordinal));
        MainWindow window = Open();
        try
        {
            Assert.DoesNotContain(window.ExplorerView.Tree.Root!.Children!, r => r.Name == "Shop.Shared");
            window.PickFiles = (_, kinds, _) =>
            {
                Assert.Contains("*.shproj", kinds!.SelectMany(k => k.Patterns ?? []));
                return Task.FromResult<IReadOnlyList<string>>([Path.Combine(Shared, "Shop.Shared.shproj")]);
            };
            Ui.ChooseFromRowMenu(window, window.ExplorerView.Tree.Root!, "Add", "Existing Project…");
            Ui.WaitFor(() => window.ExplorerView.Tree.Root!.Children!.Any(r => r is { Name: "Shop.Shared", Detail: "shared" }), () => $"Shop.Shared added as a shared project: {window.Status}");
            Assert.Contains("<Project Path=\"Shop.Shared/Shop.Shared.shproj\" />", File.ReadAllText(solution), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_test_in_a_shared_file_is_found_from_code_in_the_test_project_importing_it_and_one_written_there_comes_too()
    {
        TestFolders.Write(folder, "Shop.Tests/Shop.Tests.csproj", TestProjects.Library().With(
            $"  <ItemGroup>\n    <PackageReference Include=\"xunit\" Version=\"{TestPackages.XunitTwo}\" />\n  </ItemGroup>\n"
            + "  <Import Project=\"..\\Shop.Shared\\Shop.Shared.projitems\" Label=\"Shared\" />\n"));
        TestFolders.Write(folder, "Shop.slnx", File.ReadAllText(Path.Combine(folder, "Shop.slnx")).Replace("</Solution>", "  <Project Path=\"Shop.Tests/Shop.Tests.csproj\" />\n</Solution>", StringComparison.Ordinal));
        TestFolders.Write(folder, "Shop.Tests/OwnTests.cs", "using Xunit;\n\nnamespace Shop.Tests;\n\npublic class OwnTests\n{\n    [Fact]\n    public void Own() { }\n}\n");
        string checks = TestFolders.Write(folder, "Shop.Shared/Checks.cs", "using Xunit;\n\nnamespace Shop.Shared;\n\npublic class Checks\n{\n    [Fact]\n    public void Greets() { }\n}\n");
        File.WriteAllText(ItemsFile, Items.Replace("Models\\Order.cs\" />", "Models\\Order.cs\" />\n    <Compile Include=\"$(MSBuildThisFileDirectory)Checks.cs\" />", StringComparison.Ordinal));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.Perform(AppActions.ShowTests);
            Ui.WaitFor(() => Found(window) == "Shop.Shared.Checks.Greets, Shop.Tests.OwnTests.Own",
                () => $"the shared file's test beside the project's own, from code; the tree has [{Found(window)}]", TimeSpan.FromMinutes(1));
            File.WriteAllText(checks, File.ReadAllText(checks).Replace("public void Greets() { }", "public void Greets() { }\n\n    [Fact]\n    public void Waves() { }", StringComparison.Ordinal));
            Ui.WaitFor(() => Found(window) == "Shop.Shared.Checks.Greets, Shop.Shared.Checks.Waves, Shop.Tests.OwnTests.Own",
                () => $"the test written into the shared file; the tree has [{Found(window)}]");
        }
        finally
        {
            window.Close();
        }

        static string Found(MainWindow window) => string.Join(", ", window.Tests.Tree.Methods.Select(m => m.FullName ?? string.Empty).Order(StringComparer.Ordinal));
    }

    /// <summary>Agrees to every question the window asks, keeping their titles in order.</summary>
    private static List<string> Answered(MainWindow window)
    {
        List<string> asked = [];
        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(true);
        };
        return asked;
    }

    /// <summary>A project's Dependencies' rows, as they show now.</summary>
    private static List<ExplorerNode> Dependencies(MainWindow window, string project) => Row(window, project).Children![0].Children!;

    /// <summary>A row revealed and chosen in the Explorer.</summary>
    private static void Choose(MainWindow window, string path)
    {
        window.ExplorerView.Reveal(path);
        Ui.Settle();
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == path));
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
