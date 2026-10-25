using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Solution folders as the Solution view shows and edits them: an icon of their own, the files they list,
/// folders before projects by name at every level, and New Solution Folder, Rename, Existing Project, Existing Item and
/// Remove from their menus and keys, and a project's Rename, each changing the .sln or .slnx on disk and nothing else in
/// it, which still builds.</summary>
public sealed class SolutionFoldersTests : IDisposable
{
    private const string FolderType = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";

    private static readonly string Library = TestProjects.Library();

    private static readonly string Executable = TestProjects.Console();

    /// <summary>As Visual Studio writes a .sln: a byte order mark, a blank first line and CRLF; the folders declared after
    /// the project at the top, the Solution Items' files out of order.</summary>
    private const string Sln = "﻿\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n# Visual Studio Version 17\r\nVisualStudioVersion = 17.10.35013.160\r\nMinimumVisualStudioVersion = 10.0.40219.1\r\n"
        + "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"Shop.Web\", \"src\\Shop.Web\\Shop.Web.csproj\", \"{A1000000-0000-0000-0000-000000000001}\"\r\nEndProject\r\n"
        + "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"Solution Items\", \"Solution Items\", \"{B1000000-0000-0000-0000-000000000001}\"\r\n"
        + "\tProjectSection(SolutionItems) = preProject\r\n\t\tREADME.md = README.md\r\n\t\t.editorconfig = .editorconfig\r\n\tEndProjectSection\r\nEndProject\r\n"
        + "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"src\", \"src\", \"{B1000000-0000-0000-0000-000000000002}\"\r\nEndProject\r\n"
        + "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"Shop.Core\", \"src\\Shop.Core\\Shop.Core.csproj\", \"{A1000000-0000-0000-0000-000000000002}\"\r\nEndProject\r\n"
        + "Global\r\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\r\n\t\tDebug|Any CPU = Debug|Any CPU\r\n\t\tRelease|Any CPU = Release|Any CPU\r\n\tEndGlobalSection\r\n"
        + "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\r\n"
        + "\t\t{A1000000-0000-0000-0000-000000000001}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\r\n\t\t{A1000000-0000-0000-0000-000000000001}.Debug|Any CPU.Build.0 = Debug|Any CPU\r\n"
        + "\t\t{A1000000-0000-0000-0000-000000000001}.Release|Any CPU.ActiveCfg = Release|Any CPU\r\n\t\t{A1000000-0000-0000-0000-000000000001}.Release|Any CPU.Build.0 = Release|Any CPU\r\n"
        + "\t\t{A1000000-0000-0000-0000-000000000002}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\r\n\t\t{A1000000-0000-0000-0000-000000000002}.Debug|Any CPU.Build.0 = Debug|Any CPU\r\n"
        + "\t\t{A1000000-0000-0000-0000-000000000002}.Release|Any CPU.ActiveCfg = Release|Any CPU\r\n\t\t{A1000000-0000-0000-0000-000000000002}.Release|Any CPU.Build.0 = Release|Any CPU\r\n"
        + "\tEndGlobalSection\r\n\tGlobalSection(SolutionProperties) = preSolution\r\n\t\tHideSolutionNode = FALSE\r\n\tEndGlobalSection\r\n"
        + "\tGlobalSection(NestedProjects) = preSolution\r\n\t\t{A1000000-0000-0000-0000-000000000002} = {B1000000-0000-0000-0000-000000000002}\r\n\tEndGlobalSection\r\n"
        + "\tGlobalSection(ExtensibilityGlobals) = postSolution\r\n\t\tSolutionGuid = {C1000000-0000-0000-0000-000000000001}\r\n\tEndGlobalSection\r\nEndGlobal\r\n";

    private const string Slnx = "<Solution>\n  <Project Path=\"src/Shop.Web/Shop.Web.csproj\" />\n"
        + "  <Folder Name=\"/Solution Items/\">\n    <File Path=\"README.md\" />\n    <File Path=\".editorconfig\" />\n  </Folder>\n"
        + "  <Folder Name=\"/src/\">\n    <Project Path=\"src/Shop.Core/Shop.Core.csproj\" />\n  </Folder>\n</Solution>\n";

    private readonly string folder = PathNames.Canonical(TestFolders.Create("solution-folders-"));

    public SolutionFoldersTests()
    {
        Write("src/Shop.Web/Shop.Web.csproj", Executable);
        Write("src/Shop.Web/Program.cs", "System.Console.WriteLine(\"Shop\");\n");
        Write("src/Shop.Core/Shop.Core.csproj", Library);
        Write("src/Shop.Core/Order.cs", "namespace Shop.Core;\n\npublic class Order\n{\n}\n");
        Write("tools/Importer/Importer.csproj", Executable);
        Write("tools/Importer/Program.cs", "System.Console.WriteLine(\"Import\");\n");
        Write("docs/notes.md", "# Notes\n");
        Write("README.md", "# Shop\n");
        Write(".editorconfig", "root = true\n");
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaTheory]
    [InlineData("sln")]
    [InlineData("slnx")]
    public void A_solution_folder_has_its_own_icon_lists_its_files_and_sorts_before_the_projects_by_name(string format)
    {
        string solution = WriteSolution(format);
        MainWindow window = Open();
        ExplorerTree tree = window.ExplorerView.Tree;
        ExplorerNode items = Row(window, NodeKind.SolutionFolder, "Solution Items");
        Assert.True(ReferenceEquals(Resource(IconKeys.SolutionFolder), items.Icon), "A closed solution folder is drawn as a folder on disk is, so it passes for one");
        Assert.NotSame(Resource(TokenKeys.File_Folder), items.IconBrush);
        Assert.Same(Resource(TokenKeys.File_SolutionFolder), items.IconBrush);

        Ui.ClickRow(window, items);
        Ui.Press(window, "RIGHT");
        Ui.ClickRow(window, Row(window, NodeKind.SolutionFolder, "src"));
        Ui.Press(window, "RIGHT");
        Assert.True(ReferenceEquals(Resource(IconKeys.SolutionFolderOpen), items.Icon), "An open solution folder still shows the closed one's icon");
        // The solution declares the project first and README.md before .editorconfig: the view sorts by kind, then name.
        Assert.Equal(
            ["Shop", "  Solution Items", "    .editorconfig", "    README.md", "  src", "    Shop.Core", "  Shop.Web"],
            tree.Rows.Select(r => new string(' ', r.Depth * 2) + r.Name));
        Assert.Equal(
            [NodeKind.Solution, NodeKind.SolutionFolder, NodeKind.File, NodeKind.File, NodeKind.SolutionFolder, NodeKind.Project, NodeKind.Project],
            tree.Rows.Select(r => r.Kind));

        Ui.DoubleClick(Ui.Row(window.ExplorerView, Row(window, NodeKind.File, "README.md")));
        Assert.Equal(PathNames.Canonical(Path.Combine(folder, "README.md")), window.EditorArea.Active?.Path);

        List<string> asked = Answer(window);
        Ui.ClickRow(window, Row(window, NodeKind.File, ".editorconfig"));
        Ui.Press(window, "BACK_SPACE");
        Assert.Equal(["Remove Item"], asked);
        Assert.DoesNotContain(tree.Rows, r => r.Name == ".editorconfig");
        Assert.True(File.Exists(Path.Combine(folder, ".editorconfig")), "Removing a solution item deleted its file");
        string left = File.ReadAllText(solution);
        Assert.DoesNotContain(".editorconfig", left, StringComparison.Ordinal);
        Assert.Contains("README.md", left, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("sln")]
    [InlineData("slnx")]
    public void A_solution_folder_added_renamed_given_a_project_and_a_file_and_removed_changes_only_its_own_lines(string format)
    {
        bool slnx = format == "slnx";
        string solution = WriteSolution(format);
        byte[] original = File.ReadAllBytes(solution);
        MainWindow window = Open();
        List<string> asked = Answer(window);
        IReadOnlyList<string> pick = [];
        window.PickFiles = (_, _, _) => Task.FromResult(pick);
        ExplorerTree tree = window.ExplorerView.Tree;
        // Each change to the file is read again, making new rows, which lands before the next step finds one.
        int heard = 0;
        window.FolderWatcher!.Changed += (_, changes) => heard += changes.Paths.Contains(PathNames.Canonical(solution)) ? 1 : 0;
        void Edit(Action edit)
        {
            int before = heard;
            edit();
            Ui.WaitFor(() => heard > before, "the folder watcher to hear of the change to the solution");
            Ui.Settle();
        }

        Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Solution, "Shop"), "Add", "New Solution Folder…");
        Edit(() => Name(window, "Tools"));
        ExplorerNode tools = Row(window, NodeKind.SolutionFolder, "Tools");
        Assert.Equal(1, tools.Depth);
        Assert.Same(tools, window.ExplorerView.Selected);
        string text = Read(solution, "a solution folder added");
        if (slnx)
        {
            Assert.Contains("    <Project Path=\"src/Shop.Core/Shop.Core.csproj\" />\n  </Folder>\n  <Folder Name=\"/Tools/\" />\n</Solution>\n", text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Matches(new Regex($"^Project\\(\"{Regex.Escape(FolderType)}\"\\) = \"Tools\", \"Tools\", \"\\{{[0-9A-F-]{{36}}\\}}\"\r$", RegexOptions.Multiline), text);
        }

        Ui.ClickRow(window, Row(window, NodeKind.SolutionFolder, "Tools"));
        Ui.Run(window, AppActions.Rename);
        Edit(() => Name(window, "Utilities"));
        Assert.DoesNotContain(tree.Rows, r => r.Name == "Tools");
        // Each change reads the solution again, so the folder's row is a new one each time.
        ExplorerNode Utilities() => Row(window, NodeKind.SolutionFolder, "Utilities");
        Assert.Same(Utilities(), window.ExplorerView.Selected);
        text = Read(solution, "the solution folder renamed");
        Assert.DoesNotContain("Tools", text, StringComparison.Ordinal);
        Assert.Contains(slnx ? "<Folder Name=\"/Utilities/\" />" : "\"Utilities\", \"Utilities\"", text, StringComparison.Ordinal);

        pick = [Path.Combine(folder, "tools", "Importer", "Importer.csproj")];
        Ui.ClickRow(window, Utilities());
        Ui.Run(window, AppActions.ShowContextMenu);
        Edit(() => Ui.Choose(window.ExplorerView.OpenMenu, "Add", "Existing Project…"));
        Assert.True(Utilities().IsExpanded, "The folder a project was added to stayed closed, hiding it");
        Assert.Equal(2, Row(window, NodeKind.Project, "Importer").Depth);
        text = Read(solution, "a project added to the solution folder");
        string? importer = null;
        if (slnx)
        {
            Assert.Contains("  <Folder Name=\"/Utilities/\">\n    <Project Path=\"tools/Importer/Importer.csproj\" />\n  </Folder>\n", text, StringComparison.Ordinal);
        }
        else
        {
            importer = Assert.Single(Regex.Matches(text, "\"Importer\", \"tools\\\\Importer\\\\Importer.csproj\", \"(\\{[0-9A-F-]{36}\\})\"\r\n")).Groups[1].Value;
            // A project the configurations do not name is skipped when the solution builds.
            Assert.Contains($"\t\t{importer}.Debug|Any CPU.Build.0 = Debug|Any CPU\r\n", text, StringComparison.Ordinal);
            Assert.Contains($"\t\t{importer}.Release|Any CPU.Build.0 = Release|Any CPU\r\n", text, StringComparison.Ordinal);
            Assert.Contains($"\t\t{importer} = {Id(text, "Utilities")}\r\n", text, StringComparison.Ordinal);
        }

        pick = [Path.Combine(folder, "docs", "notes.md")];
        Ui.RightClick(Ui.Row(window.ExplorerView, Utilities()));
        Edit(() => Ui.Choose(window.ExplorerView.OpenMenu, "Add", "Existing Item…"));
        Assert.Equal(PathNames.Canonical(Path.Combine(folder, "docs", "notes.md")), window.ExplorerView.Selected?.Path);
        Ui.ChooseFromRowMenu(window, Utilities(), "Add", "New Solution Folder…");
        Edit(() => Name(window, "Scripts"));
        Assert.Equal(
            ["  Utilities", "    Scripts", "    Importer", "    notes.md"],
            tree.Rows.SkipWhile(r => r.Name != "Utilities").TakeWhile((r, i) => i == 0 || r.Depth > 1).Select(r => new string(' ', r.Depth * 2) + r.Name));
        text = Read(solution, "a file and a folder added to the solution folder");
        if (slnx)
        {
            Assert.Contains("  <Folder Name=\"/Utilities/\">\n    <File Path=\"docs/notes.md\" />\n    <Project Path=\"tools/Importer/Importer.csproj\" />\n  </Folder>\n  <Folder Name=\"/Utilities/Scripts/\" />\n</Solution>\n", text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("\"Utilities\", \"Utilities\", \"" + Id(text, "Utilities") + "\"\r\n\tProjectSection(SolutionItems) = preProject\r\n\t\tdocs\\notes.md = docs\\notes.md\r\n\tEndProjectSection\r\nEndProject\r\n", text, StringComparison.Ordinal);
            Assert.Contains($"\t\t{Id(text, "Scripts")} = {Id(text, "Utilities")}\r\n", text, StringComparison.Ordinal);
        }
        // One build of every kind of change so far, each of which a later one built on.
        Builds(solution, "solution folders, a project and a file were added and a folder renamed");
        Assert.True(File.Exists(Path.Combine(folder, "tools", "Importer", "bin", "Debug", TestProjects.Framework, "Importer.dll")),
            "The project added to the solution folder was not built with the solution");

        Ui.ClickRow(window, Utilities());
        Edit(() => Ui.Press(window, "BACK_SPACE"));
        Assert.Equal(["Remove Solution Folder"], asked);
        Assert.DoesNotContain(tree.Rows, r => r.Name is "Utilities" or "Importer" or "notes.md" or "Scripts");
        Assert.True(File.Exists(Path.Combine(folder, "tools", "Importer", "Importer.csproj")), "Removing the solution folder deleted its project's files");
        Assert.True(File.Exists(Path.Combine(folder, "docs", "notes.md")), "Removing the solution folder deleted its file");
        Assert.True(original.AsSpan().SequenceEqual(File.ReadAllBytes(solution)),
            $"With the folder removed, the solution file is not as it was before it was added:\n{Read(solution, "the solution folder removed")}");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("sln")]
    [InlineData("slnx")]
    public void A_project_renamed_takes_its_solution_entry_and_the_references_to_it_along_so_the_solution_builds_and_undo_and_redo_take_it_all(string format)
    {
        // Shop.Web uses Shop.Core's Order through a reference.
        string web = Path.Combine(folder, "src", "Shop.Web", "Shop.Web.csproj");
        Write("src/Shop.Web/Shop.Web.csproj", Executable.Replace("</Project>", "  <ItemGroup>\n    <ProjectReference Include=\"..\\Shop.Core\\Shop.Core.csproj\" />\n  </ItemGroup>\n</Project>", StringComparison.Ordinal));
        Write("src/Shop.Web/Program.cs", "System.Console.WriteLine(new Shop.Core.Order());\n");
        string solution = WriteSolution(format);
        byte[] original = File.ReadAllBytes(solution);
        string referencing = File.ReadAllText(web);
        string projects = Path.Combine(folder, "src", "Shop.Core");
        MainWindow window = Open();
        List<string> asked = Answer(window);
        int heard = 0;
        window.FolderWatcher!.Changed += (_, changes) => heard += changes.Paths.Contains(PathNames.Canonical(solution)) ? 1 : 0;
        void Edit(Action edit)
        {
            int before = heard;
            edit();
            Ui.WaitFor(() => heard > before, "the folder watcher to hear of the change to the solution");
            Ui.Settle();
        }
        IEnumerable<string> ProjectFiles() => Directory.EnumerateFiles(projects, "*.csproj").Select(Path.GetFileName).Order(StringComparer.Ordinal)!;

        Ui.ClickRow(window, Row(window, NodeKind.SolutionFolder, "src"));
        Ui.Press(window, "RIGHT");
        Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.Core"));
        Ui.Run(window, AppActions.Rename);
        Passenger.App.Vcs.NamePopup popup = Assert.IsType<Passenger.App.Vcs.NamePopup>(window.Popup);
        Assert.Equal("Shop.Core", popup.Text);
        popup.Text = string.Empty;
        Edit(() => Name(window, "Shop.Domain"));
        Assert.Equal(["Shop.Domain.csproj"], ProjectFiles());
        string renamed = Read(solution, "a project was renamed");
        Assert.Equal(format == "slnx"
            ? Slnx.Replace("src/Shop.Core/Shop.Core.csproj", "src/Shop.Core/Shop.Domain.csproj", StringComparison.Ordinal)
            : Sln[1..].Replace("\"Shop.Core\", \"src\\Shop.Core\\Shop.Core.csproj\"", "\"Shop.Domain\", \"src\\Shop.Core\\Shop.Domain.csproj\"", StringComparison.Ordinal),
            renamed);
        Assert.Equal(referencing.Replace("Shop.Core.csproj", "Shop.Domain.csproj", StringComparison.Ordinal), File.ReadAllText(web));
        Assert.Contains(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.Project && r.Name == "Shop.Domain");
        BuildsApart(solution, "a project was renamed");
        // The language server's restore of the project renamed has the solution read again, its rows made anew.
        Quiet(window);

        Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.Domain"));
        Edit(() => Ui.Run(window, Passenger.Editor.EditorActions.Undo));
        Assert.Equal(["Undo"], asked);
        Assert.True(ProjectFiles().SequenceEqual(["Shop.Core.csproj"]), $"Undo left [{string.Join(", ", ProjectFiles())}]: {window.Status}");
        Assert.True(original.AsSpan().SequenceEqual(File.ReadAllBytes(solution)), $"Undo did not put the solution back as it was:\n{Read(solution, "the rename was undone")}");
        Assert.Equal(referencing, File.ReadAllText(web));
        Quiet(window);

        Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.Core"));
        Edit(() => Ui.Run(window, Passenger.Editor.EditorActions.Redo));
        Assert.Equal(["Undo", "Redo"], asked);
        Assert.True(ProjectFiles().SequenceEqual(["Shop.Domain.csproj"]), $"Redo left [{string.Join(", ", ProjectFiles())}]: {window.Status}");
        Assert.Equal(renamed, Read(solution, "the rename was redone"));
        Assert.Equal(referencing.Replace("Shop.Core.csproj", "Shop.Domain.csproj", StringComparison.Ordinal), File.ReadAllText(web));

        Edit(() => Assert.Null(window.Rename(PathNames.Canonical(Path.Combine(folder, "README.md")), "Shop.md")));
        Assert.Equal(renamed.Replace(format == "slnx" ? "<File Path=\"README.md\" />" : "\t\tREADME.md = README.md", format == "slnx" ? "<File Path=\"Shop.md\" />" : "\t\tShop.md = Shop.md", StringComparison.Ordinal),
            Read(solution, "a file it lists was renamed"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_solution_folder_added_to_a_Windows_1252_sln_keeps_every_other_byte_and_Local_History_keeps_the_sln_as_it_was()
    {
        // A .sln an older Visual Studio wrote with no byte order mark, a comment in the system's code page, Windows-1252.
        string solution = Path.Combine(folder, "Shop.sln");
        string comment = "# Visual Studio Version 17 \u0096 Café\r\n";
        byte[] original = Encoding.Latin1.GetBytes(Sln[1..].Replace("# Visual Studio Version 17\r\n", comment, StringComparison.Ordinal));
        File.WriteAllBytes(solution, original);
        MainWindow window = Open();
        Answer(window);
        bool heard = false;
        window.FolderWatcher!.Changed += (_, changes) => heard |= changes.Paths.Contains(PathNames.Canonical(solution));

        Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Solution, "Shop"), "Add", "New Solution Folder…");
        Name(window, "Tools");
        Ui.WaitFor(() => heard, "the folder watcher to hear of the change to the solution");
        Ui.Settle();

        string written = Encoding.Latin1.GetString(File.ReadAllBytes(solution));
        Assert.True(written.Contains(comment, StringComparison.Ordinal), $"Adding a solution folder rewrote the .sln's Windows-1252 comment:\n{written}");
        string without = Regex.Replace(written, $"Project\\(\"{Regex.Escape(FolderType)}\"\\) = \"Tools\", \"Tools\", \"\\{{[0-9A-F-]{{36}}\\}}\"\r\nEndProject\r\n", string.Empty);
        Assert.Equal(Encoding.Latin1.GetString(original), without);

        string path = PathNames.Canonical(solution);
        Ui.WaitFor(() => window.LocalHistory!.Snapshot().Events.Any(e => e.Path == path && e.Before is { } hash && window.LocalHistory.Content(hash) is { } before && before.AsSpan().SequenceEqual(original)),
            "Local History to keep the .sln as it was before the solution folder went in");
        window.Close();
    }

    [AvaloniaFact]
    public void A_project_added_to_a_slnx_and_removed_again_changes_only_its_own_line_every_quote_and_short_tag_kept()
    {
        const string Written = "<?xml version='1.0' encoding='utf-8'?>\n<!-- The shop, as a person wrote it. -->\n<Solution>\n"
            + "\t<Project Path='src/Shop.Web/Shop.Web.csproj'/>\n"
            + "\t<Folder Name='/Solution Items/'>\n\t\t<File Path='README.md'   />\n\t</Folder>\n"
            + "\t<Folder Name=\"/src/\" >\n\t\t<Project Path=\"src/Shop.Core/Shop.Core.csproj\"/>\n\t</Folder>\n</Solution>\n";
        string solution = Path.Combine(folder, "Shop.slnx");
        File.WriteAllBytes(solution, new UTF8Encoding(false).GetBytes(Written));
        Restore.Run(solution);
        MainWindow window = Open();
        Answer(window);
        window.PickFiles = (_, _, _) => Task.FromResult<IReadOnlyList<string>>([Path.Combine(folder, "tools", "Importer", "Importer.csproj")]);
        int heard = 0;
        window.FolderWatcher!.Changed += (_, changes) => heard += changes.Paths.Contains(PathNames.Canonical(solution)) ? 1 : 0;
        void Edit(Action edit)
        {
            int before = heard;
            edit();
            Ui.WaitFor(() => heard > before, "the folder watcher to hear of the change to the solution");
            Ui.Settle();
        }

        Edit(() => Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Solution, "Shop"), "Add", "Existing Project…"));
        string added = Written.Replace("\t<Project Path='src/Shop.Web/Shop.Web.csproj'/>\n",
            "\t<Project Path='src/Shop.Web/Shop.Web.csproj'/>\n\t<Project Path=\"tools/Importer/Importer.csproj\" />\n", StringComparison.Ordinal);
        string text = Read(solution, "a project added");
        Assert.True(text == added, $"Adding a project to the .slnx changed more than the line it added:\n{text}");

        Ui.ClickRow(window, Row(window, NodeKind.Project, "Importer"));
        Edit(() => Ui.Press(window, "BACK_SPACE"));
        text = Read(solution, "the project removed");
        Assert.True(text == Written, $"With the project removed again, the .slnx is not as it was written:\n{text}");
        window.Close();
    }

    [AvaloniaFact]
    public void Removing_a_solution_folder_names_the_projects_folders_and_files_that_go_with_it()
    {
        string solution = WriteSolution("slnx");
        File.WriteAllText(solution, Slnx.Replace("  <Folder Name=\"/src/\">", "  <Folder Name=\"/src/tools/\">\n    <File Path=\"docs/notes.md\" />\n  </Folder>\n  <Folder Name=\"/src/\">", StringComparison.Ordinal));
        MainWindow window = Open();
        List<string> questions = [];
        window.Confirm = (_, question, _) =>
        {
            questions.Add(question);
            return Task.FromResult(false);
        };
        Ui.ClickRow(window, Row(window, NodeKind.SolutionFolder, "src"));
        Ui.Press(window, "BACK_SPACE");
        Assert.Equal(["Remove the solution folder src from Shop, and with it the project Shop.Core, the solution folder tools and the file notes.md? Their files stay on disk."], questions);
        Assert.Contains("/src/", File.ReadAllText(solution), StringComparison.Ordinal);
        Assert.Contains(window.ExplorerView.Tree.Rows, r => r.Name == "src");
        window.Close();
    }

    [AvaloniaFact]
    public void Build_selected_projects_on_a_solution_folder_builds_the_projects_under_it_and_no_other()
    {
        WriteSolution("sln");
        MainWindow window = Open();
        Ui.ChooseFromRowMenu(window, Row(window, NodeKind.SolutionFolder, "src"), "Build Selected Projects");
        Assert.False(window.IsBuilding);
        Assert.True(File.Exists(Path.Combine(folder, "src", "Shop.Core", "bin", "Debug", TestProjects.Framework, "Shop.Core.dll")), $"The project in the solution folder was not built: {Ui.Said(window)}");
        Assert.False(File.Exists(Path.Combine(folder, "src", "Shop.Web", "bin", "Debug", TestProjects.Framework, "Shop.Web.dll")),
            "Building the solution folder's projects built Shop.Web, which is outside it, too");
        window.Close();
    }

    private string WriteSolution(string format)
    {
        string solution = Path.Combine(folder, $"Shop.{format}");
        File.WriteAllBytes(solution, new UTF8Encoding(false).GetBytes(format == "slnx" ? Slnx : Sln));
        Restore.Run(solution);
        return solution;
    }

    private MainWindow Open() => Ui.OpenFolder(folder);

    /// <summary>dotnet build of the solution as it is on disk now, which fails the test naming what was done to it.</summary>
    private static void Builds(string solution, string after)
    {
        ChildResult built = Task.Run(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", solution]), limit: Restore.Limit)).GetAwaiter().GetResult();
        Assert.True(built.Succeeded, $"Once {after}, dotnet build no longer builds the solution:\n{built.Output}{built.Error}");
    }

    /// <summary>Waits until the folder watcher has heard nothing for a while and what it heard has landed.</summary>
    private static void Quiet(MainWindow window)
    {
        DateTime last = DateTime.UtcNow;
        void Seen(object? sender, FolderChanges changes) => last = DateTime.UtcNow;
        window.FolderWatcher!.Changed += Seen;
        try
        {
            Ui.WaitFor(() => DateTime.UtcNow - last > TimeSpan.FromSeconds(1.5), "the folder watcher to hear the last of what was written");
        }
        finally
        {
            window.FolderWatcher!.Changed -= Seen;
        }
    }

    /// <summary>dotnet build of a copy of the folder as it is on disk now, its build output left out: a project renamed is
    /// restored anew, which the window's language server does in the folder itself meanwhile, and two restores of one
    /// project at once fail.</summary>
    private void BuildsApart(string solution, string after)
    {
        string copy = PathNames.Canonical(TestFolders.Create("solution-copy-"));
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(folder, file);
            if (!relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(copy, relative))!);
                File.Copy(file, Path.Combine(copy, relative));
            }
        }
        try
        {
            Builds(Path.Combine(copy, Path.GetFileName(solution)), after);
        }
        finally
        {
            TestFolders.Delete(copy);
        }
    }

    /// <summary>The solution file's text, which must keep its byte order mark and line endings whatever was changed.</summary>
    private static string Read(string solution, string after)
    {
        byte[] bytes = File.ReadAllBytes(solution);
        string text = Encoding.UTF8.GetString(bytes);
        if (Path.GetExtension(solution) == ".sln")
        {
            Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), $"Once {after}, the .sln lost its byte order mark");
            Assert.False(Regex.IsMatch(text, "[^\r]\n"), $"Once {after}, the .sln has lines that no longer end in CRLF, so every line shows as changed:\n{text}");
            return text[1..];
        }
        return text;
    }

    /// <summary>The id a .sln gives the folder of a name.</summary>
    private static string Id(string text, string name) =>
        Regex.Match(text, $"\\) = \"{name}\", \"{name}\", \"(\\{{[0-9A-F-]{{36}}\\}})\"").Groups[1].Value;

    private static ExplorerNode Row(MainWindow window, NodeKind kind, string name) =>
        window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Kind == kind && r.Name == name)
        ?? throw new InvalidOperationException($"The Explorer shows no {kind} {name}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");

    private static object Resource(string key)
    {
        Application app = Application.Current!;
        return app.TryFindResource(key, app.ActualThemeVariant, out object? value) && value is not null ? value : throw new InvalidOperationException($"The theme has no {key}");
    }

    /// <summary>Agrees to every question the window asks, keeping their titles in order.</summary>
    private static List<string> Answer(MainWindow window)
    {
        List<string> asked = [];
        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(true);
        };
        return asked;
    }

    /// <summary>Types a name into the name box open and presses Enter, as a person does.</summary>
    private static void Name(MainWindow window, string name)
    {
        Assert.IsType<Passenger.App.Vcs.NamePopup>(window.Popup);
        window.KeyTextInput(name);
        Ui.Press(window, "ENTER");
        Assert.False(window.IsPopupOpen, $"The name {name} was refused: {(window.Popup as Passenger.App.Vcs.NamePopup)?.Problem}");
    }

    private void Write(string relative, string text) => TestFolders.Write(folder, relative, text);
}
