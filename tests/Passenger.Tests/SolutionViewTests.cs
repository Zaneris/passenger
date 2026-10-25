using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Terminal;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The Solution view's file commands: Cut, Copy and Paste by their keys and its Edit menu, and drags, move or copy
/// files and folders into another folder or another project's, an open file's tab, undo and edits going with it; Add,
/// Reference... adds and takes out a project reference, however the others are laid out, which the Dependencies, the
/// build and the language server follow, the rest of the project file as it was; Add, Class/Interface... writes each kind
/// of type; Open In, Terminal starts a shell in the folder; and Tools' Compare shows two files' differences.</summary>
public sealed class SolutionViewTests : IDisposable
{
    private static readonly string Library = TestProjects.Library();

    /// <summary>As Visual Studio writes one: a byte order mark, CRLF, and a blank line between its groups.</summary>
    private const string Executable = $"﻿<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <PropertyGroup>\r\n    <OutputType>Exe</OutputType>\r\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\r\n  </PropertyGroup>\r\n\r\n</Project>\r\n";

    private const string Order = "namespace Shop.App.Models;\n\npublic class Order\n{\n    public int Total;\n}\n";

    private readonly string folder = PathNames.Canonical(TestFolders.Create("solution-view-"));
    private readonly string trash = TestFolders.Create("trash-");

    public SolutionViewTests()
    {
        Write("Shop.App/Shop.App.csproj", Executable);
        Write("Shop.App/Program.cs", "System.Console.WriteLine(\"Shop\");\n");
        Write("Shop.App/Models/Order.cs", Order);
        Write("Shop.App/Models/Lines/Line.cs", "namespace Shop.App.Models.Lines;\n\npublic class Line\n{\n}\n");
        Write("Shop.Core/Shop.Core.csproj", Library);
        Write("Shop.Core/Catalog/Product.cs", "namespace Shop.Core.Catalog;\n\npublic class Product\n{\n}\n");
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"Shop.App/Shop.App.csproj\" />\n  <Project Path=\"Shop.Core/Shop.Core.csproj\" />\n</Solution>\n");
        Trash.Folder = trash;
    }

    public void Dispose()
    {
        Trash.Folder = null;
        TestFolders.Delete(folder);
        TestFolders.Delete(trash);
    }

    [AvaloniaFact]
    public void A_file_cut_and_pasted_into_another_projects_folder_moves_there_with_its_open_tab_its_undo_and_its_edit()
    {
        string order = Path.Combine(folder, "Shop.App", "Models", "Order.cs");
        MainWindow window = Open();
        window.Autosaves = false;
        window.OpenFile(order);
        OpenDocument document = Assert.Single(window.EditorArea.Documents);
        window.EditorArea.CodeEditor.GoTo(new TextPosition(4, 21));
        window.KeyTextInput(" // the sum");
        Ui.Settle();

        Ui.ClickRow(window, Show(window, order));
        Ui.Run(window, EditorActions.Cut);
        Assert.True(Show(window, order).IsCut, "The file cut is not dimmed, so nothing shows it waits to be pasted");
        Assert.Equal(order, Ui.Clipboard(window));

        string catalog = Path.Combine(folder, "Shop.Core", "Catalog");
        Ui.ClickRow(window, Show(window, catalog));
        Ui.Run(window, EditorActions.Paste);
        string moved = Path.Combine(catalog, "Order.cs");
        Assert.Equal("Moved Order.cs to Catalog", window.Status);
        Assert.False(File.Exists(order), "The file cut and pasted is still where it was");
        Assert.Equal(Order.Replace("Total;", "Total; // the sum", StringComparison.Ordinal), File.ReadAllText(moved));
        Assert.True(ReferenceEquals(document, Assert.Single(window.EditorArea.Documents)), "The moved file's tab was closed and opened again, losing its undo");
        Assert.Equal(moved, document.Path);
        Assert.Equal("Order.cs", window.EditorArea.Active?.Name);
        ExplorerNode row = Assert.Single(window.ExplorerView.Tree.Rows, r => r.Path == moved);
        Assert.Same(row, window.ExplorerView.Selected);
        Assert.False(row.IsCut);
        Assert.DoesNotContain(window.ExplorerView.Tree.Rows, r => r.Path == order);

        Ui.Run(window, EditorActions.Paste);
        Assert.StartsWith("Nothing to paste", window.Status, StringComparison.Ordinal);

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(Order, document.Text.Snapshot.ToString());
        window.Close();
    }

    /// <summary>Every file of a folder of 400 chosen and cut allocates on the UI thread, for each file, under twice what every
    /// file of a folder of 40 does, each row dimmed and a file's dependent going with it.</summary>
    [AvaloniaFact]
    public void Cutting_every_file_of_a_folder_of_four_hundred_costs_each_file_about_what_it_does_in_a_folder_of_forty()
    {
        WriteItems("Few", 40);
        WriteItems("Many", 400);
        MainWindow window = Open();
        long few = CutEvery(window, "Few", 40);
        long many = CutEvery(window, "Many", 400);
        TestContext.Current.TestOutputHelper?.WriteLine($"Cutting each file allocated {few:N0} bytes among 40, {many:N0} among 400.");
        Assert.True(many < few * 2,
            $"Cutting every file of a folder of 400 allocated {many / 1024:N0} KB on the UI thread for each, of a folder of 40 {few / 1024:N0} KB: each file read its whole folder again");
        window.Close();
    }

    /// <summary>A folder of the app's of some files, and a component whose code-behind nests under it.</summary>
    private void WriteItems(string name, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Write($"Shop.App/{name}/Item{i}.cs", $"namespace Shop.App.{name};\n\npublic class Item{i}\n{{\n}}\n");
        }
        Write($"Shop.App/{name}/Card.razor", "<p>Card</p>\n");
        Write($"Shop.App/{name}/Card.razor.cs", $"namespace Shop.App.{name};\n\npublic partial class Card\n{{\n}}\n");
    }

    /// <summary>What choosing every row of a folder and cutting them allocates on the UI thread for each file, every row then
    /// dimmed and the component's code-behind with them.</summary>
    private long CutEvery(MainWindow window, string name, int count)
    {
        string at = Path.Combine(folder, "Shop.App", name);
        Show(window, Path.Combine(at, "Item0.cs"));
        ExplorerNode[] Rows() => [.. window.ExplorerView.Tree.Rows.Where(r => r.Path is { } path && PathNames.IsUnder(path, at))];
        Assert.Equal(count + 1, Rows().Length);
        Ui.ClickRow(window, Rows()[0]);
        Ui.Click(Ui.Row(window.ExplorerView, Rows()[^1]), modifiers: Avalonia.Input.RawInputModifiers.Shift);
        Assert.Equal(count + 1, window.ExplorerView.SelectedNodes.Count);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Ui.Run(window, EditorActions.Cut);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.StartsWith("Cut ", window.Status, StringComparison.Ordinal);
        Assert.All(Rows(), row => Assert.True(row.IsCut, $"{row.Name} was cut but is not dimmed"));
        ExplorerNode card = Assert.Single(Rows(), r => r.Name == "Card.razor");
        Ui.ClickRow(window, card);
        Ui.Press(window, "RIGHT");
        ExplorerNode behind = Assert.Single(window.ExplorerView.Tree.Rows, r => r.Path == Path.Combine(at, "Card.razor.cs"));
        Assert.True(behind.IsCut, "The component was cut but its code-behind, which goes with it, is not dimmed");
        return allocated / count;
    }

    /// <summary>A folder copied and pasted, and files dragged, in one window: what each leaves is checked before the next.</summary>
    [AvaloniaFact]
    public void A_folder_copied_and_pasted_copies_all_it_holds_named_as_Riders_Duplicate_asks_and_a_file_dragged_moves_or_with_the_copy_key_copies()
    {
        string models = Path.Combine(folder, "Shop.App", "Models");
        MainWindow window = Open();

        Ui.ChooseFromRowMenu(window, Show(window, models), "Edit", "Copy");
        Assert.False(Show(window, models).IsCut, "A copied folder is dimmed as a cut one is");
        Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.Core"));
        Ui.Run(window, EditorActions.Paste);
        string copy = Path.Combine(folder, "Shop.Core", "Models");
        Assert.Equal("Copied Models to Shop.Core", window.Status);
        Assert.Equal(Order, File.ReadAllText(Path.Combine(copy, "Order.cs")));
        Assert.True(File.Exists(Path.Combine(copy, "Lines", "Line.cs")), "The folder inside the folder copied was left behind");
        Assert.Equal(Order, File.ReadAllText(Path.Combine(models, "Order.cs")));
        Assert.Same(Row(window, NodeKind.Folder, "Models", copy), window.ExplorerView.Selected);

        Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Project, "Shop.App"), "Edit", "Paste");
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        Assert.Equal("Models", popup.Text);
        Ui.Press(window, "ENTER");
        Assert.Equal("Models is already there", popup.Problem);
        popup.Text = string.Empty;
        window.KeyTextInput("Archive");
        Ui.Press(window, "ENTER");
        Assert.False(window.IsPopupOpen, $"The copy's name was refused: {popup.Problem}");
        Assert.Equal(Order, File.ReadAllText(Path.Combine(folder, "Shop.App", "Archive", "Order.cs")));
        Assert.True(File.Exists(Path.Combine(folder, "Shop.App", "Archive", "Lines", "Line.cs")), "The duplicate left the folder inside behind");

        List<string> asked = [];
        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(false);
        };
        File.WriteAllText(Path.Combine(copy, "Order.cs"), "changed\n");
        Ui.ClickRow(window, Row(window, NodeKind.Project, "Shop.Core"));
        Ui.Run(window, EditorActions.Paste);
        Assert.Equal(["Files Already Exist"], asked);
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(copy, "Order.cs")));

        string order = Path.Combine(folder, "Shop.App", "Models", "Order.cs");
        string catalog = Path.Combine(folder, "Shop.Core", "Catalog");
        window.OpenFile(order);
        Ui.Settle();
        ExplorerNode file = Show(window, order);
        ExplorerNode target = Show(window, catalog);
        DropOverlay overlay = window.ExplorerView.GetVisualDescendants().OfType<DropOverlay>().Single();

        Point from = Ui.Centre(Ui.Row(window.ExplorerView, file), window);
        Point to = Ui.Centre(Ui.Row(window.ExplorerView, target), window);
        window.MouseDown(from, MouseButton.Left);
        Move(window, from, to);
        Assert.True(overlay.IsVisible, "Nothing lit up under the file dragged over the folder");
        window.MouseUp(to, MouseButton.Left);
        Ui.Settle();
        Assert.False(overlay.IsVisible, "The drop place stayed lit after the drop");
        string moved = Path.Combine(catalog, "Order.cs");
        Assert.Equal("Moved Order.cs to Catalog", window.Status);
        Assert.False(File.Exists(order), "The file dragged away is still where it was");
        Assert.Equal(Order, File.ReadAllText(moved));
        Assert.Equal(moved, Assert.Single(window.EditorArea.Documents).Path);

        string program = Path.Combine(folder, "Shop.App", "Program.cs");
        from = Ui.Centre(Ui.Row(window.ExplorerView, Show(window, moved)), window);
        to = Ui.Centre(Ui.Row(window.ExplorerView, Show(window, program)), window);
        window.MouseDown(from, MouseButton.Left);
        Move(window, from, to);
        window.MouseUp(to, MouseButton.Left, OperatingSystem.IsMacOS() ? RawInputModifiers.Alt : RawInputModifiers.Control);
        Ui.Settle();
        Assert.Equal("Copied Order.cs to Shop.App", window.Status);
        Assert.Equal(Order, File.ReadAllText(Path.Combine(folder, "Shop.App", "Order.cs")));
        Assert.True(File.Exists(moved), "The file copied by a drag left where it was");

        ExplorerNode lines = Show(window, Path.Combine(models, "Lines"));
        from = Ui.Centre(Ui.Row(window.ExplorerView, Show(window, models)), window);
        to = Ui.Centre(Ui.Row(window.ExplorerView, lines), window);
        window.MouseDown(from, MouseButton.Left);
        Move(window, from, to);
        Assert.False(overlay.IsVisible, "A folder dragged over a folder inside it lit that up as a place to drop it");
        window.MouseUp(to, MouseButton.Left);
        Ui.Settle();
        Assert.True(Directory.Exists(Path.Combine(models, "Lines")), "The folder went inside itself");
        window.Close();
    }

    [AvaloniaFact]
    public void Add_reference_adds_one_the_build_and_language_server_follow_and_remove_takes_it_out_leaving_the_project_file_as_it_was()
    {
        string solution = Path.Combine(folder, "Shop.slnx");
        string app = Path.Combine(folder, "Shop.App", "Shop.App.csproj");
        string core = Path.Combine(folder, "Shop.Core", "Shop.Core.csproj");
        Write("Shop.Core/Order.cs", "namespace Shop.Core;\n\npublic class Order\n{\n}\n");
        Write("Shop.App/Program.cs", "System.Console.WriteLine(new Shop.Core.Order());\n");
        Restore.Run(solution);
        byte[] original = File.ReadAllBytes(app);
        MainWindow window = Open();
        window.OpenFile(Path.Combine(folder, "Shop.App", "Program.cs"));
        ProblemsModel problems = window.BottomPanel.Problems;
        Ui.WaitFor(() => problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error), "the language server's error for the type of a project not referenced", TimeSpan.FromMinutes(2));

        Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Project, "Shop.App"), "Add", "Reference…");
        AddReferenceDialog dialog = DialogButtonsTests.Check(Assert.Single(window.OwnedWindows.OfType<AddReferenceDialog>()));
        ReferenceChoice offered = Assert.Single(dialog.Choices);
        Assert.Equal(("Shop.Core", core, false), (offered.Name, offered.Path, offered.IsChosen));
        Ui.Press(dialog, "SPACE");
        Assert.True(offered.IsChosen, "Space did not check the project chosen");
        // The dialog Enter closes takes no key's release.
        Heard(window, app, () => dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null));
        Assert.Empty(window.OwnedWindows.OfType<AddReferenceDialog>());
        Assert.Equal("Added a reference to Shop.Core to Shop.App", window.Status);
        string added = Encoding.UTF8.GetString(File.ReadAllBytes(app));
        Assert.Equal(
            $"﻿<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <PropertyGroup>\r\n    <OutputType>Exe</OutputType>\r\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\r\n  </PropertyGroup>\r\n\r\n"
                + "  <ItemGroup>\r\n    <ProjectReference Include=\"..\\Shop.Core\\Shop.Core.csproj\" />\r\n  </ItemGroup>\r\n\r\n</Project>\r\n",
            added);

        ExplorerNode dependencies = Dependencies(window, "Shop.App");
        Ui.ClickRow(window, dependencies);
        Ui.Press(window, "RIGHT");
        ExplorerNode reference = Assert.Single(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.ProjectReference);
        Assert.Equal(("Shop.Core", core), (reference.Name, reference.Referenced));
        Ui.WaitFor(() => !problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error), "the language server to find the type once the project references it", TimeSpan.FromMinutes(2));
        Builds(solution, "a reference was added");
        // What the build writes (a restore's .props among it) has the solution read again: once the folder is quiet.
        Quiet(window);

        List<string> asked = Answer(window);
        Ui.ClickRow(window, Assert.Single(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.ProjectReference));
        Heard(window, app, () => Ui.Press(window, OperatingSystem.IsMacOS() ? "BACK_SPACE" : "DELETE"));
        Assert.Equal(["Remove Reference"], asked);
        Assert.Equal("Removed the reference to Shop.Core from Shop.App", window.Status);
        Assert.True(original.AsSpan().SequenceEqual(File.ReadAllBytes(app)),
            $"With the reference removed, the project file is not as it was before it was added:\n{Encoding.UTF8.GetString(File.ReadAllBytes(app))}");
        Assert.DoesNotContain(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.ProjectReference);
        Ui.WaitFor(() => problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error), "the language server's error once the reference is gone", TimeSpan.FromMinutes(2));

        Heard(window, core, () => Assert.Null(window.SetReferences(core, [app])));
        Ui.ChooseFromRowMenu(window, Dependencies(window, "Shop.App"), "Add Reference…");
        dialog = Assert.Single(window.OwnedWindows.OfType<AddReferenceDialog>());
        Assert.False(Assert.Single(dialog.Choices).CanChoose, "A project that references this one was offered, which would make a cycle");
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        Ui.Settle();
        Assert.False(dialog.IsVisible, "Escape left the dialog open");
        Assert.True(original.AsSpan().SequenceEqual(File.ReadAllBytes(app)), "Cancelling the dialog changed the project file");
        window.Close();
    }

    [AvaloniaFact]
    public void Add_reference_beside_one_that_shares_its_line_with_its_groups_tags_writes_a_project_MSBuild_loads_and_remove_puts_it_back()
    {
        string app = Path.Combine(folder, "Shop.App", "Shop.App.csproj");
        string core = Path.Combine(folder, "Shop.Core", "Shop.Core.csproj");
        string lib = Path.Combine(folder, "Shop.Lib", "Shop.Lib.csproj");
        string data = Path.Combine(folder, "Shop.Data", "Shop.Data.csproj");
        Write("Shop.Lib/Shop.Lib.csproj", Library);
        Write("Shop.Data/Shop.Data.csproj", Library);
        // Shop.App's reference shares its line with its group's start and end, Shop.Core's with its group's end alone.
        string reference = "<ProjectReference Include=\"..\\Shop.Lib\\Shop.Lib.csproj\" />";
        string program = Library.Replace("    <TargetFramework>", "    <OutputType>Exe</OutputType>\n    <TargetFramework>", StringComparison.Ordinal);
        Write("Shop.App/Shop.App.csproj", program.Replace("</Project>", $"  <ItemGroup>{reference}</ItemGroup>\n</Project>", StringComparison.Ordinal));
        Write("Shop.Core/Shop.Core.csproj", Library.Replace("</Project>", $"  <ItemGroup>\n    {reference}</ItemGroup>\n</Project>", StringComparison.Ordinal));
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"Shop.App/Shop.App.csproj\" />\n  <Project Path=\"Shop.Core/Shop.Core.csproj\" />\n"
            + "  <Project Path=\"Shop.Data/Shop.Data.csproj\" />\n  <Project Path=\"Shop.Lib/Shop.Lib.csproj\" />\n</Solution>\n");
        string[] written = [File.ReadAllText(app), File.ReadAllText(core)];
        MainWindow window = Open();
        ProjectModel Project(string path) => window.Projects.First(p => p.Path == PathNames.Canonical(path));
        Ui.WaitFor(() => Project(app).IsEvaluated && Project(core).IsEvaluated, "MSBuild to evaluate the projects as written");

        Heard(window, app, () => Assert.Null(window.SetReferences(app, [lib, core])));
        Heard(window, core, () => Assert.Null(window.SetReferences(core, [lib, data])));
        Assert.Equal(program.Replace("</Project>", $"  <ItemGroup>{reference}<ProjectReference Include=\"..\\Shop.Core\\Shop.Core.csproj\" /></ItemGroup>\n</Project>", StringComparison.Ordinal),
            File.ReadAllText(app));
        Assert.Equal(Library.Replace("</Project>", $"  <ItemGroup>\n    {reference}<ProjectReference Include=\"..\\Shop.Data\\Shop.Data.csproj\" /></ItemGroup>\n</Project>", StringComparison.Ordinal),
            File.ReadAllText(core));
        foreach ((string project, string added) in new[] { (app, core), (core, data) })
        {
            Ui.WaitFor(() => Project(project).Problem is not null || Project(project).ProjectReferences.Count == 2, $"MSBuild to evaluate {Path.GetFileName(project)} again");
            Assert.True(Project(project) is { Problem: null } loaded && loaded.ProjectReferences.Contains(PathNames.Canonical(added)),
                $"MSBuild does not load {Path.GetFileName(project)} with the reference added: {Project(project).Problem}");
        }

        Heard(window, app, () => Assert.Null(window.SetReferences(app, [lib])));
        Heard(window, core, () => Assert.Null(window.SetReferences(core, [lib])));
        Assert.Equal<string>(written, [File.ReadAllText(app), File.ReadAllText(core)]);
        window.Close();
    }

    [AvaloniaFact]
    public void A_project_file_and_the_solution_open_with_unsaved_edits_keep_them_through_add_reference_and_new_solution_folder_with_no_question()
    {
        string app = Path.Combine(folder, "Shop.App", "Shop.App.csproj");
        string core = Path.Combine(folder, "Shop.Core", "Shop.Core.csproj");
        string solution = Path.Combine(folder, "Shop.slnx");
        // The project names a file of its own, which a rename makes it follow.
        Write("Shop.App/Shop.App.csproj", Executable.Replace("</Project>", "  <ItemGroup>\r\n    <None Update=\"notes.txt\" CopyToOutputDirectory=\"PreserveNewest\" />\r\n  </ItemGroup>\r\n\r\n</Project>", StringComparison.Ordinal));
        Write("Shop.App/notes.txt", "notes\n");
        MainWindow window = Open();
        List<string> conflicts = [];
        // Were it asked, Keep Memory Changes: what the person typed is what they would keep.
        window.AskConflict = document =>
        {
            conflicts.Add(document.Name);
            return Task.FromResult(false);
        };

        window.OpenFile(app);
        Typing.Replace(window, ">Exe<", ">WinExe<");
        OpenDocument project = window.EditorArea.Active!;
        Assert.True(project.IsModified);
        Heard(window, app, () => Assert.Null(window.SetReferences(app, [core])));
        Quiet(window);
        string written = File.ReadAllText(app);
        Assert.True(conflicts.Count == 0, $"Passenger's own Add Reference asked which to keep of {string.Join(", ", conflicts)}, as if another program had changed it");
        Assert.True(written.Contains("<OutputType>WinExe</OutputType>", StringComparison.Ordinal), $"The unsaved edit to the project file is not in it after Add Reference:\n{written}");
        Assert.True(written.Contains("<ProjectReference Include=\"..\\Shop.Core\\Shop.Core.csproj\" />", StringComparison.Ordinal), $"The reference added is not in the project file:\n{written}");
        Assert.Equal(written.TrimStart('﻿'), project.Text.Snapshot.ToString());
        Assert.False(project.IsModified, "The project file shows unsaved changes it does not have");

        window.OpenFile(solution);
        Typing.Replace(window, "<Solution>", "<Solution><!-- shop -->");
        OpenDocument slnx = window.EditorArea.Active!;
        Heard(window, solution, () => Assert.Null(window.AddSolutionFolder(null, "docs")));
        Quiet(window);
        written = File.ReadAllText(solution);
        Assert.True(conflicts.Count == 0, $"Passenger's own New Solution Folder asked which to keep of {string.Join(", ", conflicts)}");
        Assert.True(written.Contains("<Solution><!-- shop -->", StringComparison.Ordinal), $"The unsaved edit to the solution is not in it after New Solution Folder:\n{written}");
        Assert.True(written.Contains("<Folder Name=\"/docs/\" />", StringComparison.Ordinal), $"The folder added is not in the solution:\n{written}");
        Assert.Equal(written, slnx.Text.Snapshot.ToString());
        Assert.False(slnx.IsModified, "The solution shows unsaved changes it does not have");

        window.OpenFile(app);
        List<string> undoing = Answer(window);
        string notes = Path.Combine(folder, "Shop.App", "notes.txt");
        Heard(window, app, () => Assert.Null(window.Rename(notes, "readme.txt")));
        Quiet(window);
        Assert.Empty(conflicts);
        Assert.Contains("<None Update=\"readme.txt\"", project.Text.Snapshot.ToString(), StringComparison.Ordinal);
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(["Undo"], undoing);
        Assert.True(File.Exists(notes), $"Undo did not rename readme.txt back: {window.Status}");
        window.EditorArea.SaveAll();
        Assert.True(File.ReadAllText(app).Contains("<None Update=\"notes.txt\"", StringComparison.Ordinal),
            $"Undoing the rename left the project naming readme.txt, which is gone:\n{File.ReadAllText(app)}");
        window.Close();
    }

    /// <summary>A file, folder or class its folder refuses is refused in the popup saying why, with no IDE error; once the
    /// folder takes them, Add, Class/Interface... writes each kind of type in its folder's namespace.</summary>
    [AvaloniaFact]
    public void Add_refused_by_its_folder_says_why_with_no_ide_error_and_add_class_interface_writes_each_of_Riders_kinds_of_type_in_its_folders_namespace()
    {
        string models = Path.Combine(folder, "Shop.App", "Models");
        MainWindow window = Open();
        ExplorerNode row = Show(window, models);
        using (Unwritable(models))
        {
            foreach ((string item, string name, string made) in new[] { ("New File…", "Notes.txt", "Notes.txt"), ("New Folder…", "Drafts", "Drafts"), ("Class/Interface…", "Invoice", "Invoice.cs") })
            {
                Ui.ChooseFromRowMenu(window, row, "Add", item);
                NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
                window.KeyTextInput(name);
                Ui.Press(window, "ENTER");
                Assert.True(window.IsPopupOpen && popup.Problem == $"{made} could not be made: access denied",
                    $"{item} in a folder that refuses it {(window.IsPopupOpen ? $"said '{popup.Problem}'" : "closed its popup")}, not why {made} was not made");
                Assert.False(Path.Exists(Path.Combine(models, made)), $"{made} was made in a folder that refuses it");
                Ui.Press(window, "ESCAPE");
            }
        }
        Assert.Empty(IdeErrors.Errors);

        string[] declarations =
        [
            "public class Invoice\n{\n}\n",
            "public interface IInvoice\n{\n}\n",
            "public record InvoiceLine();\n",
            "public struct Money\n{\n}\n",
            "public enum Status\n{\n}\n",
        ];
        string[] names = ["Invoice", "IInvoice", "InvoiceLine", "Money", "Status"];
        for (int kind = 0; kind < names.Length; kind++)
        {
            Ui.ChooseFromRowMenu(window, Show(window, models), "Add", "Class/Interface…");
            NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
            Assert.Equal("Class", popup.Kind);
            for (int down = 0; down < kind; down++)
            {
                Ui.Press(window, "DOWN");
            }
            Assert.Equal(MainWindow.TypeKinds[kind], popup.Kind);
            window.KeyTextInput(names[kind]);
            Ui.Press(window, "ENTER");
            Assert.False(window.IsPopupOpen, $"The {popup.Kind} {names[kind]} was refused: {popup.Problem}");
            string written = Path.Combine(models, names[kind] + ".cs");
            Assert.Equal($"namespace Shop.App.Models;\n\n{declarations[kind]}", File.ReadAllText(written));
            Assert.Equal(written, window.EditorArea.Active?.Path);
        }
        Assert.Equal(["Class", "Interface", "Record", "Struct", "Enum"], MainWindow.TypeKinds);
        window.Close();
    }

    /// <summary>A folder nothing can be made in while this is held: read-only for its owner on macOS and Linux, and on
    /// Windows, whose read-only attribute a folder ignores, denied to this user for making files and folders.</summary>
    private static IDisposable Unwritable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Denied(path);
        }
        UnixFileMode mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Restored(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, mode);
            }
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IDisposable Denied(string path)
    {
        DirectoryInfo info = new(path);
        System.Security.AccessControl.FileSystemAccessRule deny = new(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.CreateDirectories,
            System.Security.AccessControl.AccessControlType.Deny);
        System.Security.AccessControl.DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        return new Restored(() =>
        {
            System.Security.AccessControl.DirectorySecurity back = info.GetAccessControl();
            back.RemoveAccessRule(deny);
            info.SetAccessControl(back);
        });
    }

    private sealed class Restored(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>Three of the Solution view's menus in one window: the solution's and a project's menus show the keys of the
    /// actions their items run, Compare With and Compare Files show two files' differences, and Open In, Terminal starts a
    /// shell in the folder in a tab of its own (sh's pwd is Unix's, so not on Windows).</summary>
    [AvaloniaFact]
    public void The_menus_show_their_actions_keys_compare_shows_two_files_differences_and_open_in_terminal_starts_a_shell_in_the_folder()
    {
        Write("Shop.App/Notes/a.txt", "one\ntwo\nthree\n");
        Write("Shop.App/Notes/b.txt", "one\n2\nthree\nfour\n");
        string a = Path.Combine(folder, "Shop.App", "Notes", "a.txt");
        string b = Path.Combine(folder, "Shop.App", "Notes", "b.txt");
        MainWindow window = Open();
        window.PickFiles = (_, _, _) => Task.FromResult<IReadOnlyList<string>>([b]);

        Keymap keymap = window.Actions.Keymap;
        Ui.RightClick(Ui.Row(window.ExplorerView, Row(window, NodeKind.Solution, "Shop")));
        Shortcut build = keymap.ShortcutsFor(AppActions.Build)[0];
        KeyGesture? shown = MenuItemOf(window, "Build Solution").InputGesture;
        window.ExplorerView.OpenMenu?.Close();
        Assert.True(shown is not null && shown.Key == build.First.Key && shown.KeyModifiers == build.First.Modifiers,
            $"The solution's Build Solution shows {shown?.ToString() ?? "no keys"}, not Build Solution's {build.Display()} as the main menu does");
        Ui.Settle();

        Ui.RightClick(Ui.Row(window.ExplorerView, Row(window, NodeKind.Project, "Shop.Core")));
        Shortcut chord = keymap.ShortcutsFor(AppActions.RunTests)[0];
        string? keys = MenuItemOf(window, "Run Unit Tests").GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "PART_InputGestureText")?.Text;
        window.ExplorerView.OpenMenu?.Close();
        Assert.True(keys == chord.Display(), $"A project's Run Unit Tests shows '{keys}' for its keys, not {chord.Display()} as the main menu does");
        Ui.Settle();

        Ui.ClickRow(window, Show(window, a));
        Ui.Press(window, OperatingSystem.IsMacOS() ? "meta D" : "control D");
        Ui.Landed(() => window.Frontmost is DiffWindow, "the diff window");
        DiffView view = ((DiffWindow)window.Frontmost).View;
        Assert.Equal("one\ntwo\nthree\n", view.Left.Document.Snapshot.ToString());
        Assert.Equal("one\n2\nthree\nfour\n", view.Right.Document.Snapshot.ToString());
        Assert.Equal([new DiffHunk(1, 1, 1, 1), new DiffHunk(3, 0, 3, 1)], view.Hunks);
        Assert.Equal("a.txt vs b.txt", window.Frontmost.Title);
        window.Frontmost.Close();
        Ui.Settle();

        ExplorerNode second = Show(window, b);
        Ui.ClickRow(window, Show(window, a));
        Ui.Click(Ui.Row(window.ExplorerView, second), modifiers: RawInputModifiers.Shift);
        Assert.Equal(2, window.ExplorerView.SelectedNodes.Count);
        window.ExplorerView.Rows().RaiseEvent(new ContextRequestedEventArgs());
        Ui.Settle();
        Ui.Choose(window.ExplorerView.OpenMenu ?? throw new Xunit.Sdk.XunitException("The menu key opened no menu on the two files"), "Tools", "Compare Files");
        Ui.Landed(() => window.Frontmost is DiffWindow, "the diff window of the two files chosen");
        view = ((DiffWindow)window.Frontmost).View;
        Assert.Equal("one\ntwo\nthree\n", view.Left.Document.Snapshot.ToString());
        Assert.Equal("one\n2\nthree\nfour\n", view.Right.Document.Snapshot.ToString());
        window.Frontmost.Close();
        Ui.Settle();

        if (!OperatingSystem.IsWindows())
        {
            string catalog = Path.Combine(folder, "Shop.Core", "Catalog");
            window.TerminalPanel.Shell = ("/bin/sh", []);
            Ui.ChooseFromRowMenu(window, Show(window, catalog), "Open In", "Terminal");
            Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the shell to start");
            Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the keys to go to the terminal");
            Ui.WaitFor(() => window.TerminalPanel.Active!.Screen.CursorColumn > 0, "the shell's prompt");
            Assert.Single(window.TerminalPanel.Tabs);
            window.KeyTextInput("pwd -P");
            Ui.Press(window, "ENTER");
            TerminalSession shell = window.TerminalPanel.Active!;
            // Read across the rows, as a long path wraps.
            Ui.WaitFor(() => string.Concat(shell.Screen.Lines.Select(l => l.Text())).Contains(catalog, StringComparison.Ordinal), $"pwd to print {catalog}", TimeSpan.FromSeconds(30));
        }
        window.Close();
    }

    /// <summary>Where the file system tells letter case apart (Linux), Models and models are two folders of a project, each
    /// showing its own files, and a rename of a file to the name of another in other letter case is refused before anything
    /// changes, the type it is named after too.</summary>
    [AvaloniaFact]
    public void Where_letter_case_tells_names_apart_Models_and_models_stay_two_folders_and_a_rename_onto_another_case_is_refused()
    {
        Assert.SkipWhen(PathNames.Comparison != StringComparison.Ordinal, "Where the file system ignores letter case, Models and models are one folder and order.cs is Order.cs");
        string app = Path.Combine(folder, "Shop.App", "Shop.App.csproj");
        string upper = Path.Combine(folder, "Shop.App", "Models");
        string lower = Path.Combine(folder, "Shop.App", "models");
        string order = Path.Combine(upper, "Order.cs");
        string sibling = Path.Combine(upper, "order.cs");
        Write("Shop.App/models/Invoice.cs", "namespace Shop.App.models;\n\npublic class Invoice\n{\n}\n");
        Write("Shop.App/Models/order.cs", "// Another file than Order.cs here.\n");
        MainWindow window = Open();
        Ui.WaitFor(() => window.Projects.First(p => p.Path == PathNames.Canonical(app)).IsEvaluated, "MSBuild to evaluate Shop.App");
        Show(window, order);
        Show(window, Path.Combine(lower, "Invoice.cs"));
        IReadOnlyList<string> Held(string path) =>
            [.. (Row(window, NodeKind.Folder, Path.GetFileName(path), path).Children ?? []).Select(c => c.Name).Order(StringComparer.Ordinal)];
        Assert.Equal(["Lines", "Order.cs", "order.cs"], Held(upper));
        Assert.Equal(["Invoice.cs"], Held(lower));

        Assert.Equal("order.cs is already there", window.Rename(order, "order.cs"));
        int type = File.ReadAllText(order).IndexOf("Order", StringComparison.Ordinal);
        Assert.Equal("order.cs is already there", Ui.Await(window.RenameWithTypeAsync(order, "order.cs", ("Order", type), renameType: true), "the rename with its type"));
        Assert.Equal(Order, File.ReadAllText(order));
        Assert.Equal("// Another file than Order.cs here.\n", File.ReadAllText(sibling));
        window.Close();
    }

    /// <summary>An item of the Explorer's open menu, by its title.</summary>
    private static MenuItem MenuItemOf(MainWindow window, string header)
    {
        ContextMenu menu = window.ExplorerView.OpenMenu ?? throw new InvalidOperationException("No menu opened on the row");
        return menu.Items.OfType<MenuItem>().FirstOrDefault(i => (string?)i.Header == header)
            ?? throw new InvalidOperationException($"The menu has no {header}: [{string.Join(", ", menu.Items.OfType<MenuItem>().Select(i => i.Header))}]");
    }

    [AvaloniaFact]
    public void Add_reference_to_a_Windows_1252_project_keeps_its_other_bytes_and_Local_History_keeps_the_project_as_it_was()
    {
        // An older project in the system's code page, as its declaration says, with a comment of Windows-1252's letters.
        string core = Path.Combine(folder, "Shop.Core", "Shop.Core.csproj");
        string app = Path.Combine(folder, "Shop.App", "Shop.App.csproj");
        string comment = "  <!-- Café \u0096 \u0093Core\u0094 -->\n";
        byte[] original = System.Text.Encoding.Latin1.GetBytes("<?xml version=\"1.0\" encoding=\"windows-1252\"?>\n" + Library.Replace("  <PropertyGroup>", comment + "  <PropertyGroup>", StringComparison.Ordinal));
        File.WriteAllBytes(core, original);
        MainWindow window = Open();
        Heard(window, core, () => Assert.Null(window.SetReferences(core, [app])));

        string written = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(core));
        Assert.True(written.Contains(comment, StringComparison.Ordinal), $"Adding a reference rewrote the project's Windows-1252 comment:\n{written}");
        Assert.Contains("<ProjectReference Include=\"..\\Shop.App\\Shop.App.csproj\" />", written, StringComparison.Ordinal);
        Ui.WaitFor(() => window.LocalHistory!.Snapshot().Events.Any(e => e.Path == core && e.Before is { } hash && window.LocalHistory.Content(hash) is { } before && before.AsSpan().SequenceEqual(original)),
            "Local History to keep the project as it was before the reference went in");
        window.Close();
    }

    private MainWindow Open() => Ui.OpenFolder(folder);

    /// <summary>Opens the Explorer's folders down to a file or folder, as a person does to get to it, and gives its row.</summary>
    private static ExplorerNode Show(MainWindow window, string path)
    {
        window.ExplorerView.Reveal(PathNames.Canonical(path));
        Ui.Settle();
        return window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Path == PathNames.Canonical(path))
            ?? throw new InvalidOperationException($"The Explorer does not show {path}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
    }

    private static ExplorerNode Row(MainWindow window, NodeKind kind, string name, string? path = null) =>
        window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Kind == kind && r.Name == name && (path is null || r.Path == path))
        ?? throw new InvalidOperationException($"The Explorer shows no {kind} {name}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");

    /// <summary>A project's Dependencies row, the project opened to show it.</summary>
    private static ExplorerNode Dependencies(MainWindow window, string project)
    {
        ExplorerNode row = Row(window, NodeKind.Project, project);
        if (!row.IsExpanded)
        {
            Ui.ClickRow(window, row);
            Ui.Press(window, "RIGHT");
        }
        return window.ExplorerView.Tree.Rows.First(r => r.Kind == NodeKind.Dependencies && r.Owner == row.Path);
    }

    /// <summary>A drag's moves from one point to another, in steps, the button held, as a hand moves a mouse.</summary>
    private static void Move(MainWindow window, Point from, Point to)
    {
        for (int step = 1; step <= 10; step++)
        {
            window.MouseMove(from + ((to - from) * (step / 10.0)), RawInputModifiers.LeftMouseButton);
        }
        Ui.Settle();
    }

    /// <summary>Does something that writes a file the folder watcher reads the solution again for (a project file, a
    /// restore's .props), and waits for it to hear of it: the rows are made again then, and one found before is gone.</summary>
    private static void Heard(MainWindow window, string path, Action change)
    {
        int heard = 0;
        void Count(object? sender, FolderChanges changes) => heard += changes.Paths.Contains(PathNames.Canonical(path)) ? 1 : 0;
        window.FolderWatcher!.Changed += Count;
        try
        {
            change();
            Ui.WaitFor(() => heard > 0, $"the folder watcher to hear of {Path.GetFileName(path)} written");
            Ui.Settle();
        }
        finally
        {
            window.FolderWatcher!.Changed -= Count;
        }
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

    /// <summary>dotnet build of the solution as it is on disk now, which fails the test naming what was done to it.</summary>
    private static void Builds(string solution, string after)
    {
        ChildResult built = Task.Run(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", solution]), limit: Restore.Limit)).GetAwaiter().GetResult();
        Assert.True(built.Succeeded, $"Once {after}, dotnet build no longer builds the solution:\n{built.Output}{built.Error}");
    }

    private void Write(string relative, string text) => TestFolders.Write(folder, relative, text);
}
