using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App.Debugging;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The Explorer's file operations: New Class writes a class in its folder's namespace and opens it; a rename
/// moves the file or folder and its open tabs with it, its project including and removing what it did; a delete goes to
/// the Trash and closes its tab, and a project's leaves the solution; a name already there is refused with
/// why.</summary>
public sealed class ExplorerFilesTests : IDisposable
{
    private readonly string folder = TestFolders.Create("files-");
    private readonly string trash = TestFolders.Create("trash-");

    public ExplorerFilesTests()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.csproj"), TestProjects.Library());
        Directory.CreateDirectory(Path.Combine(folder, "Models"));
        Trash.Folder = trash;
    }

    public void Dispose()
    {
        Trash.Folder = null;
        TestFolders.Delete(folder);
        TestFolders.Delete(trash);
    }

    [AvaloniaFact]
    public void New_class_rename_and_delete_change_the_disk_the_tabs_and_the_explorer()
    {
        MainWindow window = Open();
        string models = PathNames.Canonical(Path.Combine(folder, "Models"));

        Assert.Null(window.CreateClass(models, "Order"));
        string order = Path.Combine(models, "Order.cs");
        Assert.Equal("namespace Shop.Models;\n\npublic class Order\n{\n}\n", File.ReadAllText(order));
        Assert.Equal("Order.cs", window.EditorArea.Active?.Name);
        Assert.Contains(window.ExplorerView.Tree.Rows, r => r.Name == "Order.cs");
        Assert.Equal("Order.cs is already there", window.CreateFile(models, "Order.cs", string.Empty));

        Assert.Null(window.Rename(order, "Purchase.cs"));
        string purchase = Path.Combine(models, "Purchase.cs");
        Assert.True(File.Exists(purchase));
        Assert.False(File.Exists(order));
        Assert.Equal(["Purchase.cs"], window.EditorArea.Documents.Select(d => d.Name));

        Assert.Null(window.Rename(models, "Domain"));
        Assert.Equal(PathNames.Canonical(Path.Combine(folder, "Domain", "Purchase.cs")), Assert.Single(window.EditorArea.Documents).Path);

        window.Confirm = (_, _, _) => Task.FromResult(true);
        string moved = Path.Combine(folder, "Domain", "Purchase.cs");
        Task<bool> deleting = window.DeleteAsync(PathNames.Canonical(moved));
        Assert.True(Ui.Await(deleting, "the delete"));
        Assert.False(File.Exists(moved));
        Assert.True(File.Exists(Path.Combine(trash, "Purchase.cs")));
        Assert.Empty(window.EditorArea.Documents);
        Assert.DoesNotContain(window.ExplorerView.Tree.Rows, r => r.Name == "Purchase.cs");

        string domain = PathNames.Canonical(Path.Combine(folder, "Domain"));
        File.WriteAllText(Path.Combine(domain, "Alpha.cs"), "class Alpha { }\n");
        File.WriteAllText(Path.Combine(domain, "Beta.cs"), "class Beta { }\n");
        window.OpenFile(Path.Combine(domain, "Alpha.cs"));
        window.OpenFile(Path.Combine(domain, "Beta.cs"));
        window.OpenFile(Path.Combine(domain, "Alpha.cs"));
        Ui.Settle();
        Assert.Null(window.Rename(domain, "Models"));
        Assert.Equal(PathNames.Canonical(Path.Combine(folder, "Models", "Alpha.cs")), window.EditorArea.Active?.Path);
        Assert.Equal(2, window.EditorArea.Documents.Count);

        // Typed as a person types it: the folder first, and the file's .cs by habit.
        string project = PathNames.Canonical(folder);
        Assert.Null(window.CreateType(project, "Shapes/Circle.cs", "Class"));
        string circle = Path.Combine(project, "Shapes", "Circle.cs");
        Assert.True(File.Exists(circle), $"Shapes/Circle.cs made no Shapes/Circle.cs, but {string.Join(", ", Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(project, f)))}");
        Assert.Equal("namespace Shop.Shapes;\n\npublic class Circle\n{\n}\n", File.ReadAllText(circle));
        Assert.Equal(PathNames.Canonical(circle), window.EditorArea.Active?.Path);

        Assert.Equal("Circle.Shape is not a C# name", window.CreateType(project, "Circle.Shape", "Class"));
        window.Close();
    }

    [AvaloniaFact]
    public void Delete_on_a_project_in_its_own_folder_moves_that_folder_to_the_Trash_only_once_asked_again_and_one_beside_its_solution_leaves_the_folder()
    {
        string library = Directory.CreateDirectory(Path.Combine(folder, "Lib")).FullName;
        File.Copy(Path.Combine(folder, "Shop.csproj"), Path.Combine(library, "Lib.csproj"));
        string solution = Path.Combine(folder, "Shop.slnx");
        File.WriteAllText(solution, "<Solution>\n  <Project Path=\"Shop.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");
        MainWindow window = Open();
        List<string> asked = Answer(window);

        Ui.ClickRow(window, Assert.Single(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.Project && r.Name == "Lib"));
        Ui.Press(window, "BACK_SPACE");
        Ui.WaitFor(() => Finished(window, "Moved"), "the project's folder to go");
        Assert.Equal("Moved Lib to the Trash", window.Status);
        Assert.Equal(["Remove Project", "Delete Project Files"], asked);
        Assert.False(Directory.Exists(library), $"The project's own folder stayed after the second question was answered, holding [{string.Join(", ", Left(library))}]");
        Assert.True(File.Exists(Path.Combine(trash, "Lib", "Lib.csproj")), "The project's folder is not in the Trash");
        string left = File.ReadAllText(solution);
        Assert.Contains("Shop.csproj", left, StringComparison.Ordinal);
        Assert.DoesNotContain("Lib.csproj", left, StringComparison.Ordinal);

        // The project and the solution share the repository's top folder, which Delete must never offer to trash.
        asked.Clear();
        Ui.ClickRow(window, Assert.Single(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.Project));
        Ui.Press(window, "BACK_SPACE");
        Ui.WaitFor(() => Finished(window, "Removed"), "the project's removal");
        Assert.Equal("Removed Shop from Shop", window.Status);
        Assert.DoesNotContain("Shop.csproj", File.ReadAllText(solution), StringComparison.Ordinal);
        Assert.Equal(["Remove Project"], asked);
        Assert.True(File.Exists(Path.Combine(folder, "Shop.csproj")), "The project's file went with it");
        Assert.True(Directory.Exists(Path.Combine(folder, "Models")), "The folder beside the solution went to the Trash");
        Assert.True(Directory.EnumerateFileSystemEntries(trash).Select(Path.GetFileName).SequenceEqual(["Lib"]),
            $"Removing the project beside its solution put [{string.Join(", ", Directory.EnumerateFileSystemEntries(trash).Select(Path.GetFileName))}] in the Trash, where only Lib was");
        Assert.DoesNotContain(window.ExplorerView.Tree.Rows, r => r.Kind == NodeKind.Project);
        window.Close();
    }

    [AvaloniaFact]
    public void A_delete_the_Trash_refuses_keeps_the_open_file_and_its_unsaved_edit_and_one_it_takes_goes_with_them_kept_in_Local_History()
    {
        string notes = PathNames.Canonical(Path.Combine(folder, "Notes.txt"));
        File.WriteAllText(notes, "groceries\n");
        MainWindow window = Open();
        window.Autosaves = false;
        window.OpenFile(notes);
        window.EditorArea.CodeEditor.Focus();
        window.KeyTextInput("milk, ");
        Ui.Settle();
        window.Confirm = (_, _, _) => Task.FromResult(true);
        // A Trash that is a file takes nothing, as one on another volume or a locked file would refuse.
        string full = Path.Combine(trash, "full");
        File.WriteAllText(full, string.Empty);
        Trash.Folder = full;

        Ui.ClickRow(window, Assert.Single(window.ExplorerView.Tree.Rows, r => r.Name == "Notes.txt"));
        Ui.Press(window, "BACK_SPACE");
        Assert.StartsWith("Notes.txt could not be deleted", window.Status, StringComparison.Ordinal);
        Assert.True(File.Exists(notes), "The file left although the Trash refused it");
        OpenDocument open = Assert.Single(window.EditorArea.Documents);
        Assert.Equal("milk, groceries\n", open.Text.Snapshot.ToString());
        Assert.True(File.ReadAllText(notes) == "milk, groceries\n" || open.IsModified, "The unsaved edit was marked saved without being written, so it would be lost");

        Trash.Folder = trash;
        window.EditorArea.CodeEditor.Focus();
        window.KeyTextInput("eggs, ");
        Ui.Settle();
        Assert.True(window.EditorArea.Active!.IsModified);
        // The Explorer's keys back by Cmd+1, not a click on its row, which so soon after the first is a double click opening it.
        Ui.Run(window, AppActions.FocusExplorer);
        Assert.True(window.ExplorerView.Rows().IsKeyboardFocusWithin && window.ExplorerView.SelectedNodes.Select(n => n.Name).SequenceEqual(["Notes.txt"]),
            $"Focus Explorer left the keys with {window.FocusManager?.GetFocusedElement()?.GetType().Name}, [{string.Join(", ", window.ExplorerView.SelectedNodes.Select(n => n.Name))}] chosen");
        Ui.Press(window, "BACK_SPACE");
        Ui.WaitFor(() => !File.Exists(notes) && window.EditorArea.Documents.Count == 0, "Notes.txt deleted and its tab closed");
        string trashed = File.ReadAllText(Path.Combine(trash, "Notes.txt"));
        Assert.True(trashed == "milk, eggs, groceries\n", $"The Trash has Notes.txt without what was typed into it before the delete, so the edit is lost: '{trashed}'");
        Passenger.App.History.LocalHistory history = window.LocalHistory!;
        Ui.WaitFor(() => history.Snapshot().Events.Any(e => e.Kind == Passenger.App.History.HistoryKind.Deleted && e.Path == notes), "Local History to keep the deletion");
        string? kept = history.Snapshot().Events.Last(e => e.Kind == Passenger.App.History.HistoryKind.Deleted && e.Path == notes).Before is { } hash
            && history.Content(hash) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        Assert.True(kept == "milk, eggs, groceries\n", $"Local History kept Notes.txt as deleted without what was typed into it: '{kept}'");
        window.Close();
    }

    [AvaloniaFact]
    public void A_folder_renamed_keeps_out_of_the_build_the_files_its_project_removed_by_name_or_by_glob_and_undo_puts_the_project_back()
    {
        // Old.cs and Stale.cs no longer compile, so the project removes them: one by its name, the other by its folder's glob.
        string project = Path.Combine(folder, "Shop.csproj");
        string written = $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n"
            + "  <ItemGroup>\n    <Compile Remove=\"Legacy\\Old.cs\" />\n    <Compile Remove=\"Legacy\\Generated\\**\" />\n    <Folder Include=\"Legacy\\Generated\\\" />\n  </ItemGroup>\n</Project>\n";
        File.WriteAllText(project, written);
        string legacy = PathNames.Canonical(Path.Combine(folder, "Legacy"));
        Directory.CreateDirectory(Path.Combine(legacy, "Generated"));
        File.WriteAllText(Path.Combine(legacy, "Kept.cs"), "namespace Shop.Legacy;\n\npublic class Kept\n{\n}\n");
        File.WriteAllText(Path.Combine(legacy, "Old.cs"), "this was C# once\n");
        File.WriteAllText(Path.Combine(legacy, "Generated", "Stale.cs"), "and so was this\n");
        MainWindow window = Open();
        List<string> asked = Answer(window);

        window.ExplorerView.Reveal(legacy);
        Ui.Settle();
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == legacy));
        Ui.Run(window, AppActions.Rename);
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        popup.Text = string.Empty;
        window.KeyTextInput("Archive");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => !window.IsPopupOpen, $"the rename to finish: {popup.Problem}");
        Assert.True(Directory.Exists(Path.Combine(folder, "Archive")), $"Legacy was not renamed: {window.Status}");
        Assert.Equal(written.Replace("Legacy\\", "Archive\\", StringComparison.Ordinal), File.ReadAllText(project));

        string archive = PathNames.Canonical(Path.Combine(folder, "Archive"));
        window.ExplorerView.Reveal(archive);
        Ui.Settle();
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == archive));
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(["Undo"], asked);
        Assert.True(Directory.Exists(legacy), $"Undo did not rename Archive back: {window.Status}");
        Assert.Equal(written, File.ReadAllText(project));
        window.Close();
    }

    [AvaloniaFact]
    public void A_file_renamed_in_the_explorer_keeps_its_tab_in_both_splits_its_undo_its_bookmark_and_its_breakpoint()
    {
        string models = PathNames.Canonical(Path.Combine(folder, "Models"));
        string order = Path.Combine(models, "Order.cs");
        File.WriteAllText(order, "namespace Shop.Models;\n\npublic class Order\n{\n    public int Total;\n}\n");
        MainWindow window = Open();
        window.Autosaves = false;
        window.OpenFile(order);
        EditorArea area = window.EditorArea;
        OpenDocument document = Assert.Single(area.Documents);
        area.CodeEditor.GoTo(new TextPosition(4, 21));
        window.KeyTextInput(" // the sum");
        area.CodeEditor.GoTo(new TextPosition(4, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Ui.Run(window, AppActions.ToggleBookmark);
        Assert.True(window.Perform(AppActions.SplitRight));
        Ui.Settle();

        bool heard = false;
        window.FolderWatcher!.Changed += (_, changes) => heard |= changes.Paths.Any(p => Path.GetFileName(p) is "Order.cs" or "Purchase.cs");
        Assert.Null(window.Rename(order, "Purchase.cs"));
        // The folder watcher hears of the move after it, and must not take it for the file deleted.
        Ui.WaitFor(() => heard, "the folder watcher to hear of the move");
        Ui.Settle();
        string purchase = Path.Combine(models, "Purchase.cs");
        Assert.False(File.Exists(order), "Order.cs is still on disk after its rename");
        Assert.Equal("namespace Shop.Models;\n\npublic class Order\n{\n    public int Total; // the sum\n}\n", File.ReadAllText(purchase));
        Assert.True(ReferenceEquals(document, Assert.Single(area.Documents)), "The renamed file was closed and opened again, losing its undo and carets");
        Assert.Equal(purchase, document.Path);
        Assert.True(area.Groups.Count == 2, "The rename closed the file's second split");
        Assert.All(area.Groups, group => Assert.Same(document, Assert.Single(group.Tabs).Document));
        Assert.Equal(["Purchase.cs", "Purchase.cs"], window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("tabName")).Select(t => t.Text));
        Assert.Empty(window.Debugger.Breakpoints.In(order));
        Assert.Equal(4, Assert.Single(window.Debugger.Breakpoints.In(purchase)).Line);
        Assert.Contains(new LineMark(4, LineMarkKind.Breakpoint), area.CodeEditor.LineMarks);
        Assert.Equal(purchase, Assert.Single(window.Bookmarks.All).Path);
        Assert.Equal([4], document.Bookmarks);

        area.CodeEditor.GoTo(new TextPosition(0, 0));
        area.CodeEditor.Focus();
        Ui.Press(window, "Enter");
        Assert.Equal(5, Assert.Single(window.Debugger.Breakpoints.In(purchase)).Line);
        area.CodeEditor.TryExecute(EditorActions.Undo);
        area.CodeEditor.TryExecute(EditorActions.Undo);
        Ui.Settle();
        Assert.Equal("namespace Shop.Models;\n\npublic class Order\n{\n    public int Total;\n}\n", document.Text.Snapshot.ToString());
        Assert.Equal(4, Assert.Single(window.Debugger.Breakpoints.In(purchase)).Line);
        window.Close();
    }

    /// <summary>The Explorer's menu and keys are the window's actions: a row's Rename and Delete run from its menu and
    /// from the keys of the keymap chosen, Visual Studio's Ctrl+R, R (⌘R, R on macOS) and Delete, VS Code's F2 and
    /// Delete, as Rider's RenameElement and $Delete are bound in each; F2 renames nothing in Visual Studio's, where
    /// Rider gives it none. Open In's file manager shows the row's file there (headless, it opens nothing).</summary>
    [AvaloniaFact]
    public void The_explorers_rename_delete_and_reveal_run_from_its_menu_and_from_each_keymaps_keys()
    {
        foreach (string name in (string[])["One.txt", "Two.txt", "Three.txt", "Four.txt"])
        {
            File.WriteAllText(Path.Combine(folder, name), name);
        }
        MainWindow window = Open();
        List<string> asked = Answer(window);
        try
        {
            Ui.ChooseFromRowMenu(window, Row(window, "One.txt"), "Edit", "Rename…");
            Renamed(window, "Uno.txt");
            Ui.ChooseFromRowMenu(window, Row(window, "Uno.txt"), "Edit", "Delete…");
            Gone(window, "Uno.txt", "the delete from the row's menu");
            Ui.ChooseFromRowMenu(window, Row(window, "Four.txt"), "Open In", FileManager.Name);
            Assert.Equal([Row(window, "Four.txt").Path!], window.ShownInFileManager);

            Pick(window, Row(window, "Two.txt"));
            Ui.Press(window, "F2");
            Assert.False(window.Popup is NamePopup, "F2 renamed a row in Visual Studio's keymap, which gives Rename Ctrl+R, R");
            Ui.Press(window, OperatingSystem.IsMacOS() ? "meta R , R" : "control R , R");
            Renamed(window, "Dos.txt");
            Pick(window, Row(window, "Dos.txt"));
            Ui.Press(window, "DELETE");
            Gone(window, "Dos.txt", "Delete in Visual Studio's keymap");

            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VSCode });
            Ui.Settle();
            Pick(window, Row(window, "Three.txt"));
            Ui.Press(window, "F2");
            Renamed(window, "Tres.txt");
            Pick(window, Row(window, "Tres.txt"));
            Ui.Press(window, "DELETE");
            Gone(window, "Tres.txt", "Delete in VS Code's keymap");
        }
        finally
        {
            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VisualStudio });
            window.Close();
        }

        void Renamed(MainWindow window, string name)
        {
            NamePopup popup = window.Popup as NamePopup ?? throw new Xunit.Sdk.XunitException($"No rename was asked for, to {name}: the status bar says '{window.Status}'");
            popup.Text = string.Empty;
            window.KeyTextInput(name);
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => !window.IsPopupOpen, $"the rename to {name} to finish: {popup.Problem}");
            Assert.True(File.Exists(Path.Combine(folder, name)), $"Nothing was renamed {name}: {window.Status}");
        }

        void Gone(MainWindow window, string name, string what)
        {
            // The delete asks first, which Answer agrees to: nothing asked means the keys deleted nothing.
            Ui.Settle();
            Assert.True(window.Status?.StartsWith("Moved", StringComparison.Ordinal) == true || asked.Count > 0 && asked[^1] == "Delete",
                $"{what} did not delete {name}: the status bar says '{window.Status}'");
            asked.Clear();
            Ui.WaitFor(() => Finished(window, "Moved"), $"{what} to move {name} to the Trash");
            Assert.True(!File.Exists(Path.Combine(folder, name)) && File.Exists(Path.Combine(trash, name)), $"{what} left {name} out of the Trash: '{window.Status}'");
        }
    }

    [AvaloniaFact]
    public void The_explorers_keys_open_and_close_a_folder_go_up_from_its_file_and_open_a_file_on_enter()
    {
        File.WriteAllText(Path.Combine(folder, "Models", "Order.cs"), "class Order { }\n");
        MainWindow window = Open();
        ExplorerNode order = Row(window, Path.Combine("Models", "Order.cs"));
        ExplorerNode models = window.ExplorerView.Tree.Rows.Holder(order)!;
        TreeKeysCheck.Press(window, window.ExplorerView.Tree.Rows, models, "the Explorer");
        Ui.Press(window, "Down");
        Ui.Press(window, "Enter");
        Assert.True(window.EditorArea.Active?.Path == order.Path, $"Enter on a file in the Explorer did not open it: the status bar says '{window.Status}'");
        window.Close();
    }

    /// <summary>A file's row, opened to and chosen in the Explorer by its name in the folder.</summary>
    private ExplorerNode Row(MainWindow window, string name)
    {
        string path = PathNames.Canonical(Path.Combine(folder, name));
        window.ExplorerView.Reveal(path);
        Ui.Settle();
        return window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Path == path) ?? throw new InvalidOperationException($"The Explorer shows no row for {name}");
    }

    /// <summary>Chooses a row with the keys in the Explorer, as Focus Explorer and the arrows would: a click there, on the
    /// place another row was just clicked at, would be a double click, which opens the file.</summary>
    private static void Pick(MainWindow window, ExplorerNode row)
    {
        window.ExplorerView.Choose(row);
        window.ExplorerView.FocusRows();
        Ui.Settle();
        Assert.True(window.Actions.Context().ToolWindow?.Id == ToolWindowIds.Explorer, $"The Explorer did not take the keys on {row.Name}");
    }

    private MainWindow Open() => Ui.OpenFolder(folder, languageServer: false);

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

    /// <summary>What a folder holds, by its paths inside it, for a failure to name.</summary>
    private static IEnumerable<string> Left(string path) =>
        Directory.Exists(path) ? Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(path, p)) : [];

    /// <summary>Whether the status bar says the work ended, as it does or with why it could not.</summary>
    private static bool Finished(MainWindow window, string done) =>
        window.Status is { } status && (status.StartsWith(done, StringComparison.Ordinal) || status.Contains("could not", StringComparison.Ordinal));
}
