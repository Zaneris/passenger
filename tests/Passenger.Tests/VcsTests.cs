using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Git over a real working tree: an edit grows a change bar (blank lines at the end too, and a large rewrite's
/// off the window's thread), which a rename keeps and a commit clears; Rollback Lines and Rollback put changes back; a
/// branch made from the toolbar is checked out, and going back reloads the open file; Commit, Stash and Unstash run one
/// at a time, a drop asked first and the log leaving the stash out; a status older than a commit never lands after it,
/// nor lists a save's temporary file, and the last commit's texts are read again only when HEAD moves.</summary>
public sealed class VcsTests : IDisposable
{
    private const string Original = "class Shop\n{\n    int count;\n\n    void Add() { count++; }\n}\n";

    private readonly string folder = TestFolders.Create("git-");

    public VcsTests() => TestFolders.Prepare(() =>
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original);
        TestGit.Init(folder);
    }, folder);

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Git's commands start off the window's thread, since a fork costs a large process tens of milliseconds: the
    /// working tree's status and branch as it opens, an edit's bars, a commit and the log start none of theirs on
    /// it.</summary>
    [AvaloniaFact]
    public void The_git_commands_the_window_asks_for_start_off_its_thread()
    {
        string tree = PathNames.Canonical(folder);
        List<string> onWindow = [];
        int started = 0;
        void Heard(object? sender, ChildStart start)
        {
            if (start.Folder.Length > 0 && PathNames.Canonical(start.Folder) == tree
                && Path.GetFileNameWithoutExtension(start.Command.Split(' ')[0].Trim('"')) == "git")
            {
                Interlocked.Increment(ref started);
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    lock (onWindow)
                    {
                        onWindow.Add(start.Command);
                    }
                }
            }
        }
        ChildProcesses.Started += Heard;
        try
        {
            MainWindow window = Open();
            Ui.Landed(() => window.Vcs.Branch == "main", "the branch read as the working tree opened");
            window.EditorArea.CodeEditor.GoTo(new TextPosition(2, 4));
            window.KeyTextInput("long ");
            Ui.Landed(() => Marks(window).Any(m => m.Kind == ChangeMarkKind.Modified && m.Line == 2), "a modified bar on line 3");
            window.Perform(AppActions.Commit);
            Ui.Landed(() => window.FocusManager?.GetFocusedElement() is TextBox, "the commit message to have the keys");
            window.KeyTextInput("Count with a long");
            Ui.Press(window, OperatingSystem.IsMacOS() ? "meta Enter" : "control Enter");
            Ui.Landed(() => Git("log", "-1", "--format=%s").Trim() == "Count with a long", "the commit");
            window.Perform(AppActions.ShowGit);
            Ui.Landed(() => window.GitLog.Rows.Count >= 2, "the log's two commits");
            window.Close();
        }
        finally
        {
            ChildProcesses.Started -= Heard;
        }
        Assert.True(started > 0, "No git command was heard starting in the working tree");
        lock (onWindow)
        {
            Assert.True(onWindow.Count == 0, $"{onWindow.Count} of the {started} git commands started on the window's thread: {string.Join("; ", onWindow.Distinct())}");
        }
    }

    /// <summary>An edit's change bar, and a commit from the Commit window, made by the message's own key (Cmd+Enter, Ctrl+Enter
    /// elsewhere) rather than a new line in it, which clears the bar.</summary>
    [AvaloniaFact]
    public void An_edit_shows_a_change_bar_and_committing_it_from_the_commit_window_clears_it()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal("main", window.Vcs.Branch);

        // Line 3 changed, and a line added after the last.
        editor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        Ui.Landed(() => Marks(window).Any(m => m.Kind == ChangeMarkKind.Modified && m.Line == 2), "a modified bar on line 3");

        CommitView commit = window.CommitView;
        window.Perform(AppActions.Commit);
        Ui.Landed(() => commit.Chosen.Any(c => c.Name == "Shop.cs" && c.Kind == ChangeKind.Modified), "Shop.cs listed as modified");
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Commit));
        Ui.Landed(() => window.FocusManager?.GetFocusedElement() is TextBox, "the commit message to have the keys");
        window.KeyTextInput("Count with a long");
        Ui.Press(window, OperatingSystem.IsMacOS() ? "meta Enter" : "control Enter");
        Ui.Landed(() => Git("log", "-1", "--format=%s").Trim() == "Count with a long",
            $"Committing from the message's own key: the log's last commit is '{Git("log", "-1", "--format=%s").Trim()}' and the message reads '{commit.Message}'");

        Assert.Contains("long int count;", File.ReadAllText(Path.Combine(folder, "Shop.cs")), StringComparison.Ordinal);
        Ui.Landed(() => Marks(window).Count == 0 && window.Vcs.Changes.Count == 0, "the bars and the changes cleared");
        Assert.Empty(commit.Rows);
        Assert.Empty(commit.Message);
        window.Close();
    }

    /// <summary>The Commit window's Show Diff names the keys it takes there, Compare Files' in Visual Studio's keymap
    /// (Duplicate's off macOS), and a diff's Settings Rider's Ctrl+Shift+D (Cmd+Shift+D); each key opens what it names, and
    /// a key the person sets for either is named and opens it in place of the keymap's.</summary>
    [AvaloniaFact]
    public void The_commit_windows_Show_Diff_and_a_diffs_Settings_name_and_take_the_keys_of_the_keymap_in_force()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("int count", "long count", StringComparison.Ordinal));
        string cmd = OperatingSystem.IsMacOS() ? "meta" : "control";
        MainWindow window = Open();
        try
        {
            window.Perform(AppActions.ShowCommit);
            CommitView commit = window.CommitView;
            Ui.Landed(() => commit.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "Shop.cs"), "Shop.cs listed as changed");
            Button showDiff = commit.GetVisualDescendants().OfType<Button>().Single(b => (ToolTip.GetTip(b) as string)?.StartsWith("Show Diff", StringComparison.Ordinal) == true);
            Assert.Equal($"Show Diff ({Shortcut.Parse($"{cmd} D").Display()})", ToolTip.GetTip(showDiff));

            DiffWindow diff = DiffFromCommit(window, $"{cmd} D");
            Assert.Equal($"Settings ({Shortcut.Parse($"{cmd} shift D").Display()})", ToolTip.GetTip(diff.View.SettingsButton));
            SettingsOpen(diff, $"{cmd} shift D");
            diff.Close();
            Ui.Settle();

            window.ApplySettings(window.Settings with
            {
                Shortcuts = new() { [AppActions.ShowDiff] = [$"{cmd} alt D"], [AppActions.DiffSettings] = [$"{cmd} alt shift D"] },
            });
            Ui.Settle();
            Assert.Equal($"Show Diff ({Shortcut.Parse($"{cmd} alt D").Display()})", ToolTip.GetTip(showDiff));
            diff = DiffFromCommit(window, $"{cmd} alt D");
            Assert.Equal($"Settings ({Shortcut.Parse($"{cmd} alt shift D").Display()})", ToolTip.GetTip(diff.View.SettingsButton));
            diff.View.Right.Focus();
            Ui.Press(diff, $"{cmd} shift D");
            Assert.False(diff.View.SettingsButton.ContextMenu?.IsOpen, $"{cmd} shift D still opened the diff's settings, the person having set {cmd} alt shift D");
            SettingsOpen(diff, $"{cmd} alt shift D");
            diff.Close();
        }
        finally
        {
            window.ApplySettings(new Passenger.App.Settings.AppSettings());
            window.Close();
        }

        static DiffWindow DiffFromCommit(MainWindow window, string keys)
        {
            ListBox changes = Find(window.CommitView, window.CommitView.Rows);
            Ui.Click(Ui.Row(changes, window.CommitView.Rows.OfType<CommitRow>().First(r => r.Change.Name == "Shop.cs")));
            Ui.Press(window, keys);
            Ui.Landed(() => window.Frontmost is DiffWindow, $"the diff {keys} in the Commit window opens, as its Show Diff names");
            return (DiffWindow)window.Frontmost;
        }

        static void SettingsOpen(DiffWindow diff, string keys)
        {
            diff.View.Right.Focus();
            Ui.Press(diff, keys);
            Assert.True(diff.View.SettingsButton.ContextMenu?.IsOpen, $"{keys}, which the diff's Settings names, did not open them");
            diff.View.SettingsButton.ContextMenu!.Close();
        }
    }

    [AvaloniaFact]
    public void A_renamed_file_keeps_its_change_bars_against_what_the_last_commit_has_where_it_was()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        Ui.Landed(() => Marks(window).Any(m => m.Kind == ChangeMarkKind.Modified && m.Line == 2), "a modified bar on line 3");

        Assert.Null(window.Rename(PathNames.Canonical(Path.Combine(folder, "Shop.cs")), "Store.cs"));
        editor.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("sealed ");
        Ui.Landed(() => Marks(window).Any(m => m.Line == 0), "a bar for the line typed after the rename, against the last commit's Shop.cs");
        Assert.Equal("Store.cs", window.EditorArea.Active?.Name);
        Assert.Contains(Marks(window), m => m.Kind == ChangeMarkKind.Modified && m.Line == 2);
        window.Close();
    }

    [AvaloniaFact]
    public void Rollback_lines_puts_the_change_under_the_caret_back_as_the_last_commit_has_it()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.GetLineStart(4), 0, "    // added\n")]);
        editor.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("public ");
        Ui.Landed(() => Marks(window).Count == 2, "two change bars");
        Assert.Contains(Marks(window), m => m.Kind == ChangeMarkKind.Added && m.Line == 4 && m.Count == 1);

        editor.GoTo(new TextPosition(4, 2));
        window.Perform(AppActions.RollbackLines);
        Ui.Landed(() => !Text(window).Contains("// added", StringComparison.Ordinal), "the added line gone");
        Assert.StartsWith("public class Shop", Text(window), StringComparison.Ordinal);
        editor.GoTo(new TextPosition(0, 3));
        window.Perform(AppActions.RollbackLines);
        Ui.Landed(() => Text(window) == Original, "the file as committed");
        Ui.Landed(() => Marks(window).Count == 0, "the bars cleared");
        window.Close();
    }

    [AvaloniaFact]
    public void A_new_branch_from_the_toolbar_is_checked_out_and_going_back_reloads_the_open_file()
    {
        MainWindow window = Open();
        Assert.Equal("main", window.Vcs.Branch);
        window.ShowNewBranch();
        Ui.Settle();
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        popup.Text = "feature/sale";
        Task submitted = popup.SubmitAsync();
        Ui.Landed(() => submitted.IsCompleted && window.Vcs.Branch == "feature/sale", "the new branch checked out");
        Assert.False(window.IsPopupOpen);

        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("count++", "count += 2", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Twice");
        window.Vcs.ReloadOpenFiles();
        Ui.Landed(() => Text(window).Contains("count += 2", StringComparison.Ordinal), "the branch's text");
        Task<VcsOutcome> checkout = window.Vcs.CheckoutAsync("main");
        Ui.Landed(() => checkout.IsCompleted, "the checkout done");
        Assert.True(checkout.Result.Succeeded, checkout.Result.Message);
        Assert.Equal("main", window.Vcs.Branch);
        Assert.Equal(Original, Text(window));
        Assert.False(window.EditorArea.Active!.IsModified);
        window.Close();
    }

    /// <summary>A save writes the new text beside the file and moves it over the file: Git reading the folder in that moment
    /// (a read under way as Save is pressed) lists the file saved as changed, never the temporary file.</summary>
    [AvaloniaFact]
    public void A_file_saved_as_git_reads_the_folder_is_listed_as_changed_and_its_saves_temporary_file_never_is()
    {
        MainWindow window = Open();
        string saving = Path.Combine(folder, "Shop.cs" + FileText.SaveSuffix);
        List<string> listed = [];
        window.Vcs.Changed += (_, _) => listed.AddRange(window.Vcs.Changes.Select(c => c.Name));
        window.EditorArea.CodeEditor.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("sealed ");
        _ = window.Vcs.RefreshAsync();
        window.Perform(AppActions.Save);

        // That read may find the temporary file; one made while it is there, as a save has it, finds it for certain.
        File.WriteAllText(saving, "sealed " + Original);
        _ = window.Vcs.RefreshAsync();
        Ui.Settle();
        Assert.Equal(["Shop.cs"], window.Vcs.Changes.Select(c => c.Name));
        Assert.DoesNotContain(Path.GetFileName(saving), listed);
        window.Close();
    }

    /// <summary>Rollback from the Commit window, the new file chosen by hand (unversioned files are not chosen at first),
    /// asks first, saying the new file is deleted, then puts back the changed file and deletes the new one, once another
    /// program reading them lets them go (Windows refuses git meanwhile).</summary>
    [AvaloniaFact]
    public void Rollback_puts_back_a_file_another_program_was_reading_and_deletes_a_new_one_it_held_once_they_are_let_go()
    {
        MainWindow window = Open();
        string shop = Path.Combine(folder, "Shop.cs");
        string added = Path.Combine(folder, "Prices.cs");
        File.WriteAllText(added, "class Prices { }\n");
        window.EditorArea.CodeEditor.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("sealed ");
        window.Perform(AppActions.Save);
        _ = window.Vcs.RefreshAsync();
        Ui.Landed(() => window.Vcs.Changes.Count == 2, "a changed file and a new one listed");
        Assert.DoesNotContain(window.CommitView.Chosen, c => c.Kind == ChangeKind.Unversioned);
        string? asked = null;
        window.Confirm = (title, text, yes) =>
        {
            asked = text;
            return Task.FromResult(true);
        };
        foreach (CommitRow row in window.CommitView.Rows.OfType<CommitRow>())
        {
            row.IsChosen = true;
        }

        // Another program reads both until git has tried once, which Windows refuses (git deletes the old file first).
        FileStream reading = new(shop, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileStream holding = new(added, FileMode.Open, FileAccess.Read, FileShare.Read);
        EventHandler<Passenger.Core.ChildEnd> letGo = (_, end) =>
        {
            if (end.Command.Contains(" restore ", StringComparison.Ordinal))
            {
                reading.Dispose();
                holding.Dispose();
            }
        };
        Passenger.Core.ChildProcesses.Ended += letGo;
        try
        {
            RollbackChosen(window);
            AssertNothingChanged(window);
        }
        finally
        {
            Passenger.Core.ChildProcesses.Ended -= letGo;
            reading.Dispose();
            holding.Dispose();
        }
        Assert.Contains("deleted", asked, StringComparison.Ordinal);
        Assert.Equal(Original, File.ReadAllText(shop));
        Assert.False(File.Exists(added), "The new file stayed after the program holding it let go");
        Ui.Landed(() => Text(window) == Original, "the open file at the last commit's text");
        Assert.Equal("Rolled back 2 files", window.Status);
        window.Close();
    }

    [AvaloniaFact]
    public void Show_diff_puts_the_last_commit_beside_the_open_file_and_its_arrow_puts_a_change_back()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.GetLineStart(4), 0, "    int total;\n")]);
        editor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        window.Perform(AppActions.Save);
        _ = window.Vcs.RefreshAsync();
        Ui.Landed(() => window.Vcs.Changes.Count == 1, "Shop.cs listed as changed");

        Task<DiffWindow?> opening = window.ShowDiffAsync(window.Vcs.Changes[0].Path);
        Ui.Landed(() => opening.IsCompleted, "the diff open");
        DiffWindow diff = opening.Result!;
        Assert.Same(diff, window.Frontmost);
        DiffView view = diff.View;
        Assert.Equal(Original, view.Left.Document.Snapshot.ToString());
        Assert.Same(window.EditorArea.Active!.Text, view.Right.Document);
        Assert.Equal([new DiffHunk(2, 1, 2, 1), new DiffHunk(4, 0, 4, 1)], view.Hunks);
        // Below the added line, the left side stands one line higher.
        Assert.Equal(5, view.MapLine(6, toLeft: true));

        view.Revert(view.Hunks[1]);
        Ui.Settle();
        Assert.DoesNotContain("int total;", Text(window), StringComparison.Ordinal);
        Assert.Equal([new DiffHunk(2, 1, 2, 1)], view.Hunks);
        Assert.True(window.EditorArea.Active!.IsModified);
        diff.Close();
        window.Close();
    }

    /// <summary>The IDE's keys work in a diff's window as in the main one, the diff keeping its own: Find Action opens over
    /// it and Escape closes that first, the keys back on the file's side; F7 is still the diff's next difference; an
    /// editor's key goes to the side with the keys, not to the main window's editor; and Escape then closes the window.</summary>
    [AvaloniaFact]
    public void The_IDEs_keys_work_in_a_diffs_window_on_the_side_with_them_and_Escape_still_closes_it()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        window.Perform(AppActions.Save);
        editor.GoTo(new TextPosition(0, 0));
        Task<DiffWindow?> opening = window.ShowDiffAsync(window.EditorArea.Active!.Path);
        Ui.Landed(() => opening.IsCompleted, "the diff open");
        DiffWindow diff = opening.Result!;
        DiffView view = diff.View;
        Ui.Landed(() => view.Right.IsFocused, "the diff's side of the file to have the keys");

        Ui.Run(diff, AppActions.FindAction);
        Control popup = Assert.IsType<Passenger.App.Navigation.ActionPopup>(window.Popup);
        Assert.True(diff.GetVisualDescendants().Contains(popup), "Find Action, pressed in the diff's window, opened over the main window");
        Ui.Press(diff, "ESCAPE");
        Assert.False(window.IsPopupOpen, "Escape left Find Action open over the diff");
        Assert.True(diff.IsVisible, "Escape closed the diff along with Find Action over it");
        Assert.True(view.Right.IsFocused, "Find Action, closed, left the diff's keys away from the file's side");

        Ui.Press(diff, "F7");
        Assert.Equal(2, view.Right.CaretPosition.Line);

        Ui.Run(diff, EditorActions.Duplicate);
        Assert.Equal("class Shop\n{\n    long int count;\n    long int count;\n\n    void Add() { count++; }\n}\n", Text(window));
        Assert.Equal(new TextPosition(0, 0), editor.CaretPosition);

        Ui.Press(diff, "ESCAPE");
        Assert.False(diff.IsVisible, "Escape, with nothing over the diff, left its window open");
        window.Close();
    }

    /// <summary>A diff's Next Difference is F7 everywhere and F8 too off the Mac, as Rider's Visual Studio keymap binds them
    /// there, Shift with either going back; the Mac's keymap leaves F8 to the IDE.</summary>
    [AvaloniaFact]
    public void A_diffs_next_and_previous_difference_are_F7_and_F8_off_the_Mac_and_F7_alone_on_it()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        editor.GoTo(new TextPosition(4, 4));
        window.KeyTextInput("public ");
        window.Perform(AppActions.Save);
        editor.GoTo(new TextPosition(0, 0));
        Task<DiffWindow?> opening = window.ShowDiffAsync(window.EditorArea.Active!.Path);
        Ui.Landed(() => opening.IsCompleted, "the diff open");
        DiffWindow diff = opening.Result!;
        DiffView view = diff.View;
        Ui.Landed(() => view.Right.IsFocused, "the diff's side of the file to have the keys");
        Assert.Equal([2, 4], view.Hunks.Select(h => h.NewStart));

        Ui.Press(diff, "F7");
        Assert.Equal(2, view.Right.CaretPosition.Line);
        Ui.Press(diff, "F8");
        if (OperatingSystem.IsMacOS())
        {
            Assert.True(view.Right.CaretPosition.Line == 2, "F8 stepped in a diff on the Mac, whose keymap leaves it to the IDE");
        }
        else
        {
            Assert.True(view.Right.CaretPosition.Line == 4, $"F8 left a diff's caret on line {view.Right.CaretPosition.Line + 1}, not at the next difference");
            Ui.Press(diff, "shift F8");
            Assert.True(view.Right.CaretPosition.Line == 2, $"Shift+F8 left a diff's caret on line {view.Right.CaretPosition.Line + 1}, not at the previous difference");
        }
        diff.Close();
        window.Close();
    }

    /// <summary>The diff's side of the open file takes the file's colours as they change: opened before the language server
    /// answered, the server's once it does; and once the server starts again, the new one's, which follow the text.</summary>
    [AvaloniaFact]
    public void A_diff_opened_before_the_language_server_answers_takes_its_colours_and_a_new_servers_when_it_starts_again()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.csproj"),
            TestProjects.Library());
        Restore.Run(folder);
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile(Path.Combine(folder, "Shop.cs"));
        Passenger.App.Workspace.OpenDocument shop = window.EditorArea.Active!;
        Ui.PumpUntil(() => window.Vcs.Repository is not null, "the working tree found");
        Task<DiffWindow?> opening = window.ShowDiffAsync(shop.Path);
        Ui.PumpUntil(() => opening.IsCompleted, "the diff open");
        DiffWindow diff = opening.Result!;
        CodeEditor right = diff.View.Right;
        Assert.True(right.Highlighter is not Passenger.Languages.CSharp.SemanticHighlighter, "The language server answered before the diff opened, so it had nothing to follow");

        // The use of count in Add() is coloured as a field by the server alone.
        int use = Original.IndexOf("count++", StringComparison.Ordinal);
        Ui.WaitFor(() => window.CSharpService?.IsReady == true && Field(shop.Highlighter, shop.Text.Snapshot, use), "the server's colour on the field in the editor");
        Assert.True(Field(right.Highlighter, right.Document.Snapshot, use),
            "The diff opened before the language server answered never took its colours: the field it colours in the editor is plain in the diff");

        int ended = window.CSharpService!.Session!.Server.ProcessId;
        Process.GetProcessById(ended).Kill();
        Ui.WaitFor(() => window.CSharpService.Session is { IsLoaded: true } session && session.Server.ProcessId != ended, "the language server started again");
        Ui.WaitFor(() => Field(shop.Highlighter, shop.Text.Snapshot, use), "the new server's colour on the field in the editor");
        right.Focus();
        diff.KeyTextInput("sealed ");
        Ui.Settle();
        int moved = use + "sealed ".Length;
        Assert.Equal(moved, shop.Text.Snapshot.ToString().IndexOf("count++", StringComparison.Ordinal));
        Ui.WaitFor(() => Field(shop.Highlighter, shop.Text.Snapshot, moved), "the editor's colour on the field moved along");
        Assert.True(Field(right.Highlighter, right.Document.Snapshot, moved),
            "The diff kept the colours of the language server that ended, which no longer follow the text: the field moved along is plain in the diff");
        diff.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void A_closed_diff_is_let_go_by_the_open_file_it_showed()
    {
        MainWindow window = Open();
        WeakReference<DiffView> closed = DiffAndClose(window);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(closed.TryGetTarget(out _), "The closed diff (its editors and the last commit's text) is still alive, held by the open file it showed");
        window.Close();
    }

    [AvaloniaFact]
    public void An_edit_in_a_diff_after_the_files_tab_closed_is_saved_with_the_others_and_as_the_diff_closes()
    {
        MainWindow window = Open();
        string shop = window.EditorArea.Active!.Path;
        Task<DiffWindow?> opening = window.ShowDiffAsync(shop);
        Ui.Landed(() => opening.IsCompleted, "the diff open");
        DiffWindow diff = opening.Result!;

        window.Perform(AppActions.CloseTab);
        Ui.Settle();
        Assert.Null(window.EditorArea.Active);
        CodeEditor right = diff.View.Right;
        right.Focus();
        right.GoTo(new TextPosition(4, 4));
        diff.KeyTextInput("public ");
        Ui.Settle();
        Assert.Contains("    public void Add()", right.Document.Snapshot.ToString(), StringComparison.Ordinal);

        window.Perform(AppActions.SaveAll);
        Assert.Contains("    public void Add()", File.ReadAllText(shop), StringComparison.Ordinal);
        right.GoTo(new TextPosition(2, 4));
        diff.KeyTextInput("long ");
        diff.Close();
        Ui.Settle();
        Assert.Contains("    long int count;", File.ReadAllText(shop), StringComparison.Ordinal);
        Assert.DoesNotContain(window.EditorArea.Documents, d => d.Path == shop);
        window.Close();
    }

    [AvaloniaFact]
    public void A_click_on_a_change_bar_shows_what_the_lines_were_and_rolls_them_back()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(4, 4));
        window.KeyTextInput("public ");
        Ui.Landed(() => Marks(window).Count == 1, "a change bar on line 5");

        Ui.Click(editor, editor.GutterColumnPoint(GutterColumn.Changes, 4), settle: false);
        Ui.Landed(() => window.Popup is ChangePopup, "the change's popup");
        ChangePopup popup = (ChangePopup)window.Popup!;
        Assert.Equal("    void Add() { count++; }", popup.Before);
        // The bar's click is the change's, not a breakpoint's.
        Assert.Empty(window.Debugger.Breakpoints.In(window.EditorArea.Active!.Path));

        Avalonia.Controls.Button rollback = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(popup)
            .OfType<Avalonia.Controls.Button>()
            .First(b => Avalonia.Controls.ToolTip.GetTip(b) as string == "Rollback");
        Ui.Click(rollback, settle: false);
        Ui.Landed(() => Text(window) == Original, "the line as committed");
        Assert.False(window.IsPopupOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Typing_in_a_20000_line_file_rewritten_since_the_last_commit_moves_its_bars_and_a_bar_opens_its_change_at_once()
    {
        // A generated file of 20,000 lines, every twelfth line of it generated anew since the last commit: 1,667 changes.
        string generated = Path.Combine(folder, "Generated.txt");
        string[] lines = [.. Enumerable.Range(0, 20000).Select(i => $"    int value{i} = {i};")];
        File.WriteAllText(generated, string.Join("\n", lines) + "\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Generated");
        File.WriteAllText(generated, string.Join("\n", lines.Select((l, i) => i % 12 == 5 ? l.Replace(";", " + 1;", StringComparison.Ordinal) : l)) + "\n");
        MainWindow window = Open();
        window.OpenFile(generated);
        CodeEditor editor = window.EditorArea.CodeEditor;
        VcsController vcs = window.Vcs;
        Ui.Landed(() => Marks(window).Count == 1667, "a bar for each line generated anew");

        // Typed in, the file is compared again with the last commit off the window's thread, in a few copies' memory.
        editor.GoTo(new TextPosition(0, 4));
        window.KeyTextInput("const ");
        Ui.Landed(() => Marks(window).Count == 1668, "a bar for the first line typed in");
        editor.GoTo(new TextPosition(8, 4));
        window.KeyTextInput("const ");
        Ui.Landed(() => Marks(window).Count == 1669, "a bar for the second line typed in");
        Assert.Equal([new ChangeMark(0, 1, ChangeMarkKind.Modified), new ChangeMark(5, 1, ChangeMarkKind.Modified), new ChangeMark(8, 1, ChangeMarkKind.Modified)], Marks(window).Take(3));
        Assert.True(vcs.LargestComparison < 16_000_000, $"Comparing the file with the last commit took {vcs.LargestComparison / 1e6:F0} MB");

        // A click on a bar opens what the lines were from the comparison its bars came from: nothing is compared again.
        int comparisons = vcs.Comparisons;
        Ui.Settle();
        Avalonia.Point at = editor.TranslatePoint(editor.GutterColumnPoint(GutterColumn.Changes, 8), window)!.Value;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        // Pressed raw: Ui.Click's look at the frame before it would count among what the click allocated.
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        long clicked = GC.GetAllocatedBytesForCurrentThread() - allocated;
        ChangePopup popup = Assert.IsType<ChangePopup>(window.Popup);
        Assert.Equal("    int value8 = 8;", popup.Before);
        Assert.True(clicked < 4_000_000, $"Opening the bar's popup took {clicked / 1e6:F0} MB on the window's thread");
        Assert.True(vcs.Comparisons == comparisons, $"Opening the bar's popup compared the file {vcs.Comparisons - comparisons} times again");

        Ui.Click(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(popup).OfType<Avalonia.Controls.Button>()
            .First(b => Avalonia.Controls.ToolTip.GetTip(b) as string == "Show Diff"), settle: false);
        Ui.Landed(() => window.Frontmost is DiffWindow, "the diff window");
        DiffWindow diff = (DiffWindow)window.Frontmost;
        Assert.Equal(1669, diff.View.Hunks.Count);
        Assert.True(vcs.Comparisons == comparisons, $"Show Diff compared the file {vcs.Comparisons - comparisons} times again");
        diff.Close();

        editor.GoTo(new TextPosition(0, 0));
        window.Perform(AppActions.RollbackLines);
        Assert.StartsWith("    int value0 = 0;\n    int value1 = 1;\n", Text(window), StringComparison.Ordinal);
        Assert.True(vcs.Comparisons == comparisons, $"Rollback Lines compared the file {vcs.Comparisons - comparisons} times again");
        Ui.Landed(() => Marks(window).Count == 1668, "the rolled back line's bar gone");
        window.Close();
    }

    [AvaloniaFact]
    public void Blank_lines_added_at_the_end_of_a_file_show_an_added_bar_and_a_difference_and_roll_back()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;

        // Two blank lines after the last, which git diff lists as added.
        editor.GoTo(new TextPosition(6, 0));
        Ui.Press(window, "ENTER");
        Ui.Press(window, "ENTER");
        Assert.Equal(Original + "\n\n", Text(window));
        // The first Enter's bar can land before the second's: the wait is for the bar over both.
        Ui.Landed(() => Marks(window).SequenceEqual([new ChangeMark(6, 2, ChangeMarkKind.Added)]), "a bar over both blank lines");
        Task<DiffWindow?> opening = window.ShowDiffAsync(window.EditorArea.Active!.Path);
        Ui.Landed(() => opening.IsCompleted, "the diff open");
        Assert.Equal([new DiffHunk(6, 0, 6, 2)], opening.Result!.View.Hunks);
        opening.Result.Close();

        editor.GoTo(new TextPosition(7, 0));
        window.Perform(AppActions.RollbackLines);
        Ui.Landed(() => Text(window) == Original, "the blank lines gone");
        Ui.Landed(() => Marks(window).Count == 0, "the bar gone");
        window.Close();
    }

    [AvaloniaFact]
    public void A_narrow_logs_rows_keep_room_for_each_commits_subject()
    {
        Git("commit", "-q", "--allow-empty", "-m", "Second commit's subject");
        // As wide as a 1024x768 desktop leaves a maximized window, the Git window under the editors with its branches.
        MainWindow window = Ui.OpenFolder(folder, width: 957, height: 699);
        GitLogView log = window.GitLog;
        window.Perform(AppActions.ShowGit);
        Ui.Landed(() => log.Rows.Count == 2, "the two commits");
        TextBlock subject = log.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Second commit's subject");
        Control row = (Control)subject.GetVisualParent()!.GetVisualParent()!;
        Assert.True(subject.Bounds.Width >= 100,
            $"The log's {row.Bounds.Width:0}-wide rows left the subject {subject.Bounds.Width:0} wide, the author and date taking the rest");
        window.Close();
    }

    [AvaloniaFact]
    public void The_log_lays_out_a_merge_in_two_lanes_and_shows_what_a_commit_did_to_a_file()
    {
        string shop = Path.Combine(folder, "Shop.cs");
        Git("switch", "-q", "-c", "side");
        File.WriteAllText(Path.Combine(folder, "Side.cs"), "class Side { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Side");
        Git("switch", "-q", "main");
        File.WriteAllText(shop, Original.Replace("count++", "count += 2", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Twice");
        Git("merge", "-q", "--no-ff", "-m", "Merge side", "side");

        MainWindow window = Open();
        GitLogView log = window.GitLog;
        window.Perform(AppActions.ShowGit);
        Ui.Landed(() => log.Rows.Count == 4, "the four commits");
        Assert.Equal(Git("log", "--all", "--topo-order", "--format=%H").Split('\n', StringSplitOptions.RemoveEmptyEntries), log.Rows.Select(r => r.Commit.Hash));
        GraphRow merge = log.Rows[0];
        Assert.Equal("Merge side", merge.Commit.Subject);
        Assert.Contains("main", merge.Commit.Refs);
        Assert.Equal(2, merge.Below.Count(l => l is not null));
        GraphRow first = log.Rows[^1];
        Assert.Equal(2, first.Above.Count(l => l == first.Commit.Hash));
        Assert.Empty(first.Below);

        CommitInfo twice = log.Rows.Select(r => r.Commit).First(c => c.Subject == "Twice");
        log.Select(twice);
        Task reload = log.LoadAsync();
        Ui.Landed(() => reload.IsCompleted, "the log read again");
        Assert.Equal(twice.Hash, log.Selected?.Hash);
        Ui.Landed(() => log.Files.Count == 1, "the commit's file");
        FileChange changed = Assert.Single(log.Files);
        Assert.Equal((PathNames.Canonical(shop), ChangeKind.Modified), (changed.Path, changed.Kind));
        Assert.Contains("Twice", log.Details, StringComparison.Ordinal);
        Task<DiffWindow?> opening = window.ShowCommitDiffAsync(twice, changed);
        Ui.Landed(() => opening.IsCompleted, "the commit's diff");
        DiffView diff = opening.Result!.View;
        Assert.Equal(Original, diff.Left.Document.Snapshot.ToString());
        Assert.Contains("count += 2", diff.Right.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Single(diff.Hunks);
        Assert.False(diff.IsEditable);
        opening.Result!.Close();

        log.FilterText = twice.Hash[..7];
        Ui.Settle();
        Assert.Equal(twice.Hash, Assert.Single(log.Rows).Commit.Hash);
        window.Close();
    }

    [AvaloniaFact]
    public void Update_project_brings_in_the_upstreams_commit_and_the_open_file_shows_it()
    {
        string clone = TestFolders.Create("git-clone-");
        Git("clone", "-q", "-c", "core.autocrlf=false", folder, clone);
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("int count", "long count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Long");

        MainWindow window = Ui.OpenFolder(clone);
        window.OpenFile(Path.Combine(clone, "Shop.cs"));
        Ui.Settle();
        Assert.Equal("main", window.Vcs.Branch);
        window.Perform(AppActions.UpdateProject);
        Ui.Landed(() => Text(window).Contains("long count", StringComparison.Ordinal), "the upstream's change in the open file");
        Ui.Landed(() => window.Status?.StartsWith("Updated", StringComparison.Ordinal) == true, "the update's end in the status bar");
        Assert.False(window.EditorArea.Active!.IsModified);
        window.Close();
    }

    [AvaloniaFact]
    public void Annotations_name_each_lines_commit_mark_a_typed_line_and_a_click_chooses_the_commit()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("count++", "count += 2", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Twice");
        string twice = Git("rev-parse", "HEAD").Trim();
        string first = Git("rev-parse", "HEAD~1").Trim();
        MainWindow window = Open();
        Passenger.App.Workspace.OpenDocument shop = window.EditorArea.Active!;
        Task annotating = window.ToggleAnnotationsAsync(shop);
        Ui.Landed(() => annotating.IsCompleted, "the blame");
        IReadOnlyList<BlameLine> blame = window.Vcs.BlameOf(shop)!;
        Assert.Equal(first, blame[0].Hash);
        Assert.Equal(twice, blame[4].Hash);
        Assert.Equal("Twice", blame[4].Summary);
        // A block is named at its first line only, by the day its commit was made where the person is.
        Assert.StartsWith(Git("log", "-1", "--format=%ad", "--date=format-local:%Y-%m-%d", first).Trim(), shop.GutterAnnotations[0], StringComparison.Ordinal);
        Assert.Null(shop.GutterAnnotations[1]);
        Assert.NotNull(shop.GutterAnnotations[4]);
        Assert.True(window.EditorArea.CodeEditor.IsInAnnotationColumn(new Point(4, 4)));

        window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(0, 0, "// new\n")]);
        Ui.Landed(() => window.Vcs.BlameOf(shop) is { Count: > 0 } now && !now[0].IsCommitted, "the typed line blamed");
        Assert.Equal("Not committed yet", shop.GutterAnnotations[0]);

        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.Click(editor, new Point(10, (5 * editor.LineHeight) + (editor.LineHeight / 2) - editor.ScrollOffset.Y), settle: false);
        Ui.Landed(() => window.GitLog.Selected?.Hash == twice, "the commit chosen in the log");

        Task closing = window.ToggleAnnotationsAsync(shop);
        Ui.Landed(() => closing.IsCompleted, "the annotations closed");
        Assert.Empty(shop.GutterAnnotations);
        Assert.False(editor.IsInAnnotationColumn(new Point(4, 4)));
        window.Close();
    }

    [AvaloniaFact]
    public void Annotations_opened_and_closed_again_and_again_blame_the_file_once_for_each_pause_in_typing()
    {
        MainWindow window = Open();
        Passenger.App.Workspace.OpenDocument shop = window.EditorArea.Active!;
        int blames = 0;
        EventHandler<Passenger.Core.ChildEnd> counted = (_, end) =>
        {
            if (end.Command.Contains(" blame ", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref blames);
            }
        };
        Passenger.Core.ChildProcesses.Ended += counted;
        try
        {
            for (int i = 0; i < 5; i++)
            {
                Task toggled = window.ToggleAnnotationsAsync(shop);
                Ui.Landed(() => toggled.IsCompleted, "the annotations opened or closed");
            }
            Ui.WaitFor(() => Volatile.Read(ref blames) == 3, "a blame for each of the three openings");
            Assert.NotEmpty(shop.GutterAnnotations);

            window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(0, 0, "// one\n")]);
            window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(0, 0, "// two\n")]);
            Ui.Landed(() => shop.GutterAnnotations is ["Not committed yet", ..], "the typed lines blamed");
            Ui.Hold(TimeSpan.FromSeconds(1.5), "past the pause's length again, so a second blame of the same pause would have run");
            Ui.Settle();
            Assert.True(Volatile.Read(ref blames) == 4, $"One pause in typing blamed the file {Volatile.Read(ref blames) - 3} times");
        }
        finally
        {
            Passenger.Core.ChildProcesses.Ended -= counted;
        }
        window.Close();
    }

    /// <summary>Annotations closed while their blame runs leave the gutter empty, and typing brings them back no more; and a
    /// large file git does not track is said so, with nothing shown.</summary>
    [AvaloniaFact]
    public void Closing_annotations_while_the_blame_runs_leaves_the_gutter_empty_and_a_large_file_git_does_not_track_shows_none()
    {
        MainWindow window = Open();
        Passenger.App.Workspace.OpenDocument shop = window.EditorArea.Active!;
        Task opening = window.ToggleAnnotationsAsync(shop);
        Assert.False(opening.IsCompleted, "The blame was read before the window's thread ran again");
        Task closing = window.ToggleAnnotationsAsync(shop);
        Ui.Landed(() => opening.IsCompleted && closing.IsCompleted, "the blame and the close");
        Assert.Empty(shop.GutterAnnotations);
        Assert.Null(window.Vcs.BlameOf(shop));

        window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(0, 0, "// new\n")]);
        Ui.Hold(TimeSpan.FromSeconds(1), "past the pause after typing, which would blame the file were the annotations open");
        Ui.Settle();
        Assert.Empty(shop.GutterAnnotations);

        // Larger than a pipe holds: git refuses the file before it reads the text it is given, so writing it fails.
        string notes = Path.Combine(folder, "Notes.txt");
        File.WriteAllText(notes, string.Concat(Enumerable.Repeat("A line of notes that nobody has committed yet.\n", 4500)));
        Assert.True(new FileInfo(notes).Length > 200_000);
        window.OpenFile(notes);
        Passenger.App.Workspace.OpenDocument opened = window.EditorArea.Active!;
        Task annotating = window.ToggleAnnotationsAsync(opened);
        Ui.Landed(() => annotating.IsCompleted, "the blame");
        Assert.True(annotating.IsCompletedSuccessfully, $"Annotating the file failed: {annotating.Exception?.InnerException}");
        Assert.Equal("Notes.txt is not tracked by Git", window.Status);
        Assert.Empty(opened.GutterAnnotations);
        Assert.False(window.Vcs.IsAnnotated(opened), "The annotations stayed open on a file with nothing to show");
        window.Close();
    }

    [AvaloniaFact]
    public void A_screenshot_of_a_working_tree_taken_with_no_wait_shows_its_branch_and_change_bars()
    {
        string shop = Path.Combine(folder, "Shop.cs");
        File.WriteAllText(shop, Original.Replace("int count", "long count", StringComparison.Ordinal));
        // Git's status made slower than the rest of the window's opening, as a large tree's is: only waiting for Git sees it.
        string hook = Path.Combine(folder, ".git", "slow-status.sh");
        File.WriteAllText(hook, "#!/bin/sh\nsleep 1\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Git("config", "core.fsmonitor", Unix(hook));
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        Assert.True(Screenshot.Prepare(window, LaunchOptions.Parse([folder, "--file", shop]), []), "The screenshot's window never settled");

        Assert.Equal("main", Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBlock>(window, "BranchName")?.Text);
        Assert.Equal([new ChangeMark(2, 1, ChangeMarkKind.Modified)], Marks(window));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Point bar = editor.TranslatePoint(editor.GutterColumnPoint(GutterColumn.Changes, 2), window)!.Value;
        Avalonia.Media.Color drawn = Ui.Pixel(window, bar);
        Assert.True(drawn == Modified(), $"The changed line's bar is drawn {drawn}, not the modified bar's colour");
        window.Close();
    }

    private static Avalonia.Media.Color Modified() =>
        Avalonia.Controls.ResourceNodeExtensions.TryFindResource(Avalonia.Application.Current!, TokenKeys.Vcs_Modified, Avalonia.Styling.ThemeVariant.Dark, out object? brush) && brush is Avalonia.Media.ISolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException("The theme has no modified bar's colour");

    [AvaloniaFact]
    public void Stashing_puts_every_change_away_unsaved_and_new_files_too_and_unstashing_brings_them_back()
    {
        MainWindow window = Open();
        Assert.Equal("main", window.Vcs.Branch);
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        string notes = Path.Combine(folder, "Notes.txt");
        File.WriteAllText(notes, "a new file\n");

        NamePopup stash = window.ShowStash()!;
        Ui.Settle();
        stash.Text = "Half-done count";
        Task stashing = stash.SubmitAsync();
        Ui.Landed(() => stashing.IsCompleted, "the stash made");
        Ui.Landed(() => Text(window) == Original, $"the file back as the commit has it ({window.Status})");
        Assert.False(File.Exists(notes));
        Assert.False(window.EditorArea.Active!.IsModified);
        Assert.Contains("Half-done count", Git("stash", "list"), StringComparison.Ordinal);

        Task<UnstashPopup?> showing = window.ShowUnstashAsync();
        Ui.Landed(() => showing.IsCompleted, "the stashes");
        UnstashPopup unstash = showing.Result!;
        Assert.Contains("Half-done count", Assert.Single(unstash.Stashes).Message, StringComparison.Ordinal);
        Task popping = unstash.PopAsync();
        Ui.Landed(() => popping.IsCompleted && Text(window).Contains("long int count", StringComparison.Ordinal), "the change back in the open file");
        Assert.True(File.Exists(notes));
        Assert.Empty(Git("stash", "list"));
        Assert.False(window.IsPopupOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Backspace_in_unstash_changes_keeps_every_stash_and_delete_asks_before_dropping_the_chosen_one_once()
    {
        File.AppendAllText(Path.Combine(folder, "Shop.cs"), "// older\n");
        Git("stash", "push", "-m", "Older");
        File.AppendAllText(Path.Combine(folder, "Shop.cs"), "// newer\n");
        Git("stash", "push", "-m", "Newer");
        MainWindow window = Open();
        Assert.Equal("main", window.Vcs.Branch);
        List<string> asked = [];
        bool agree = false;
        window.Confirm = (title, text, yes) =>
        {
            asked.Add($"{title} {text}");
            return Task.FromResult(agree);
        };
        Task<UnstashPopup?> showing = window.ShowUnstashAsync();
        Ui.Landed(() => showing.IsCompleted, "the stashes");
        UnstashPopup unstash = showing.Result!;
        Assert.Equal(2, unstash.Stashes.Count);
        Ui.Settle();
        Assert.True(window.FocusManager?.GetFocusedElement() is Avalonia.Controls.ListBoxItem { DataContext: StashInfo { Index: 0 } },
            $"Unstash Changes opened with {window.FocusManager?.GetFocusedElement()?.GetType().Name} taking the keys, not the newest stash's row");

        // Backspace, the key a Mac labels delete, does nothing at all.
        Ui.Press(window, "BACK_SPACE");
        Ui.Landed(() => !window.Vcs.IsBusy, "the end of whatever Backspace began");
        Assert.True(Stashes().Length == 2, $"Backspace in Unstash Changes dropped a stash, leaving [{string.Join(", ", Stashes())}]");
        Assert.Empty(asked);
        Assert.True(window.IsPopupOpen, "Backspace closed Unstash Changes");

        Ui.Press(window, "DELETE");
        Ui.Landed(() => !window.Vcs.IsBusy, "the end of whatever Delete began");
        Assert.Equal(2, Stashes().Length);
        string question = Assert.Single(asked);
        Assert.True(question.Contains("stash@{0}", StringComparison.Ordinal) && question.Contains("Newer", StringComparison.Ordinal), $"Drop asked \"{question}\"");
        Assert.True(window.IsPopupOpen, "Answering no to the drop closed Unstash Changes");

        agree = true;
        Avalonia.Controls.Button[] buttons = [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(unstash).OfType<Avalonia.Controls.Button>()];
        Assert.Equal("Drop Apply Pop", string.Join(' ', buttons.Select(b => b.Content)));
        window.KeyPress(Avalonia.Input.Key.Delete, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
        Assert.All(buttons, b => Assert.False(b.IsEnabled, $"{b.Content} could be pressed while a drop ran"));
        window.KeyPress(Avalonia.Input.Key.Delete, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
        // Raised, not clicked: a pointer never reaches a button that is off, and this checks its handler refuses too.
#pragma warning disable RS0030
        buttons[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
#pragma warning restore RS0030
        Assert.True(window.IsPopupOpen, "A second drop pressed while the first ran closed Unstash Changes before it ended");
        Ui.Landed(() => !window.IsPopupOpen, "the drop's end");
        Assert.Equal(2, asked.Count);
        Assert.Equal("On main: Older", Assert.Single(Stashes()));
        Assert.StartsWith("Dropped", window.Status, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void Stashing_a_change_and_a_new_file_leaves_the_git_log_as_it_was()
    {
        MainWindow window = Open();
        Assert.Equal("main", window.Vcs.Branch);
        window.Perform(AppActions.ShowGit);
        GitLogView log = window.GitLog;
        Ui.Landed(() => log.Rows.Count == 1, "the log");
        string before = Listed(log);

        // A stash with a new file in it is three commits of git's own under refs/stash.
        window.EditorArea.CodeEditor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "a new file\n");
        NamePopup stash = window.ShowStash()!;
        Ui.Settle();
        stash.Text = "Half-done count";
        Task stashing = stash.SubmitAsync();
        Ui.Landed(() => stashing.IsCompleted, "the stash made");
        Assert.Single(Stashes());

        Task reading = log.LoadAsync();
        Ui.Landed(() => reading.IsCompleted, "the log read again");
        Assert.True(Listed(log) == before, $"After one stash the log lists\n{Listed(log)}\nwhere it listed\n{before}");
        window.Close();

        static string Listed(GitLogView log) => string.Join('\n', log.Rows.Select(r => $"{r.Commit.Subject} ({string.Join(", ", r.Commit.Refs)})"));
    }

    [AvaloniaFact]
    public void Add_to_gitignore_keeps_the_files_byte_order_mark_its_windows_line_breaks_and_an_unsaved_edit_to_it()
    {
        // As Visual Studio's template writes one: a byte order mark and CRLF, its last line with no break.
        string ignore = Path.Combine(folder, ".gitignore");
        File.WriteAllBytes(ignore, [0xEF, 0xBB, 0xBF, .. "bin/\r\nobj/"u8]);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "scratch\n");
        MainWindow window = Open();
        window.OpenFile(ignore);
        Ui.Settle();
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(0);
        window.KeyTextInput("# out ");
        Ui.Settle();

        window.Perform(AppActions.ShowCommit);
        Avalonia.Controls.ListBox changes = Find(window.CommitView, window.CommitView.Rows);
        Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "notes.txt"), "the unversioned file listed");
        changes.SelectedItem = window.CommitView.Rows.OfType<CommitRow>().First(r => r.Change.Name == "notes.txt");
        Ui.ChooseFromMenu(changes, "Add to .gitignore");
        Assert.Equal([0xEF, 0xBB, 0xBF, .. "# out bin/\r\nobj/\r\nnotes.txt\r\n"u8], File.ReadAllBytes(ignore));
        Ui.Landed(() => window.Vcs.Changes.All(c => c.Name != "notes.txt"), "the ignored file gone from the changes");
        Passenger.App.Workspace.OpenDocument shown = window.EditorArea.Documents.Single(d => d.Name == ".gitignore");
        Assert.Equal("# out bin/\r\nobj/\r\nnotes.txt\r\n", shown.Text.Snapshot.ToString());
        Assert.False(shown.IsModified, "The open .gitignore shows unsaved changes it does not have");
        window.Close();
    }

    [AvaloniaFact]
    public void The_commit_and_log_menus_ignore_a_file_revert_a_commit_and_branch_from_one()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("count++", "count += 2", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Twice");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "scratch\n");
        MainWindow window = Open();
        Assert.Equal("main", window.Vcs.Branch);

        window.Perform(AppActions.ShowCommit);
        Avalonia.Controls.ListBox changes = Find(window.CommitView, window.CommitView.Rows);
        Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "notes.txt"), "the unversioned file listed");
        changes.SelectedItem = window.CommitView.Rows.OfType<CommitRow>().First(r => r.Change.Name == "notes.txt");
        Ui.ChooseFromMenu(changes, "Add to .gitignore");
        Assert.Equal("notes.txt\n", File.ReadAllText(Path.Combine(folder, ".gitignore")));
        Ui.Landed(() => window.Vcs.Changes.All(c => c.Name != "notes.txt"), "the ignored file gone from the changes");

        window.Perform(AppActions.ShowGit);
        GitLogView log = window.GitLog;
        Ui.Landed(() => log.Rows.Any(r => r.Commit.Subject == "Twice"), "the log");
        Avalonia.Controls.ListBox commits = Find(log, log.Rows);
        log.Select(log.Rows.First(r => r.Commit.Subject == "Twice").Commit);
        Ui.ChooseFromMenu(commits, "Revert Commit");
        Ui.Landed(() => Git("log", "-1", "--format=%s").Trim() == "Revert \"Twice\"", "the revert's commit");
        Ui.Landed(() => Text(window).Contains("count++", StringComparison.Ordinal), "the revert in the open file");
        // Git reads what the revert did (slower on Windows) before it takes another change; one asked for meanwhile is refused.
        Ui.Landed(() => !window.Vcs.IsBusy, "the revert's end");

        Ui.Landed(() => log.Rows.Any(r => r.Commit.Subject == "First"), "the first commit in the log");
        CommitInfo first = log.Rows.First(r => r.Commit.Subject == "First").Commit;
        log.Select(first);
        Ui.ChooseFromMenu(commits, "Copy Revision Number");
        Ui.WaitFor(() => Ui.Clipboard(window) == first.Hash, "the hash on the clipboard");
        Ui.ChooseFromMenu(commits, "New Branch…");
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        popup.Text = "from-first";
        Task named = popup.SubmitAsync();
        Ui.Landed(() => named.IsCompleted, "the new branch");
        Assert.True(popup.Problem is null, $"The new branch was refused: {popup.Problem}");
        Assert.Equal("from-first", window.Vcs.Branch);
        Assert.Equal(first.Hash, Git("rev-parse", "HEAD").Trim());
        window.Close();
    }

    [AvaloniaFact]
    public void Open_files_are_saved_before_a_checkout_or_a_rollback_and_take_what_git_puts_there_with_no_question()
    {
        File.WriteAllText(Path.Combine(folder, "Notes.cs"), "class Notes { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Notes");
        Git("switch", "-q", "-c", "other");
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("count++", "count += 5", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Fives");
        Git("switch", "-q", "main");
        MainWindow window = Open();
        int asked = 0;
        window.AskConflict = _ => Task.FromResult(asked++ < 0);
        window.OpenFile(Path.Combine(folder, "Notes.cs"));
        Ui.Settle();
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(0);
        window.KeyTextInput("sealed ");
        Assert.Equal("main", window.Vcs.Branch);

        Task<VcsOutcome> checkout = window.Vcs.CheckoutAsync("other");
        Ui.Landed(() => checkout.IsCompleted, "the checkout done");
        Assert.True(checkout.Result.Succeeded, checkout.Result.Message);
        Assert.Equal("sealed class Notes { }\n", File.ReadAllText(Path.Combine(folder, "Notes.cs")));
        window.OpenFile(Path.Combine(folder, "Shop.cs"));
        Ui.Landed(() => Text(window).Contains("count += 5", StringComparison.Ordinal), "the other branch's text");
        Assert.False(window.EditorArea.Active!.IsModified);

        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(0);
        window.KeyTextInput("// saved\n");
        window.Perform(AppActions.Save);
        window.KeyTextInput("// unsaved\n");
        Ui.Landed(() => window.Vcs.Changes.Any(c => c.Name == "Shop.cs"), "Shop.cs listed as changed");
        Task<VcsOutcome> rollback = window.Vcs.RollbackAsync([.. window.Vcs.Changes.Where(c => c.Name == "Shop.cs")]);
        Ui.Landed(() => rollback.IsCompleted, "the rollback done");
        string head = Git("show", "HEAD:Shop.cs");
        Assert.Equal(head, File.ReadAllText(Path.Combine(folder, "Shop.cs")));
        Ui.Landed(() => Text(window) == head, "the editor at HEAD");
        Assert.Equal(0, asked);
        window.Close();
    }

    [AvaloniaFact]
    public void A_file_name_with_brackets_is_that_file_alone_to_roll_back_and_to_commit()
    {
        File.WriteAllText(Path.Combine(folder, "[ab].cs"), "class Ab { }\n");
        File.WriteAllText(Path.Combine(folder, "a.cs"), "class A { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Brackets");
        File.WriteAllText(Path.Combine(folder, "[ab].cs"), "class Ab { int x; }\n");
        File.WriteAllText(Path.Combine(folder, "a.cs"), "class A { int x; }\n");
        MainWindow window = Open();
        Ui.Landed(() => window.Vcs.Changes.Count == 2, "both files listed as changed");

        Task<VcsOutcome> rollback = window.Vcs.RollbackAsync([.. window.Vcs.Changes.Where(c => c.Name == "[ab].cs")]);
        Ui.Landed(() => rollback.IsCompleted, "the rollback done");
        Assert.Equal("class Ab { }\n", File.ReadAllText(Path.Combine(folder, "[ab].cs")));
        Assert.Equal("class A { int x; }\n", File.ReadAllText(Path.Combine(folder, "a.cs")));

        File.WriteAllText(Path.Combine(folder, "[ab].cs"), "class Ab { int y; }\n");
        Ui.WaitFor(() => window.Vcs.Changes.Count == 2, "both files changed again");
        Task<CommitResult> commit = window.Vcs.CommitAsync([.. window.Vcs.Changes.Where(c => c.Name == "[ab].cs")], "Only brackets", amend: false, push: false);
        Ui.Landed(() => commit.IsCompleted, "the commit made");
        Assert.True(commit.Result.Succeeded, commit.Result.Commit.Message);
        Assert.Equal("[ab].cs", Git("show", "--name-only", "--format=", "HEAD").Trim());
        window.Close();
    }

    [AvaloniaFact]
    public void A_save_leaves_the_explorers_chosen_rows_the_git_log_and_go_to_file_as_they_were()
    {
        Directory.CreateDirectory(Path.Combine(folder, "Models"));
        File.WriteAllText(Path.Combine(folder, "Models", "Order.cs"), "class Order { }\n");
        File.WriteAllText(Path.Combine(folder, "Models", "Line.cs"), "class Line { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Models");
        MainWindow window = Open();
        window.Perform(AppActions.ShowGit);
        Ui.Landed(() => window.GitLog.Rows.Count == 2, "the log");
        Ui.Landed(() => window.FileIndex.Files.Count == 3, "the files for Go to File");
        // The log may be read once before the window's first look at the repository knows its history, and once after.
        Task known = window.Vcs.RefreshAsync();
        Ui.Landed(() => known.IsCompleted, "the repository's history");
        Passenger.App.Explorer.ExplorerTree tree = window.ExplorerView.Tree;
        tree.Expand(tree.Rows.First(r => r.Name == "Models"));
        Passenger.App.Explorer.ExplorerNode[] chosen = [.. tree.Rows.Where(r => r.Name is "Order.cs" or "Line.cs")];
        window.ExplorerView.Choose(chosen);
        int loads = window.GitLog.Loads;

        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(0);
        window.KeyTextInput("sealed ");
        window.Perform(AppActions.Save);
        Ui.Landed(() => window.Vcs.Changes.Any(c => c.Name == "Shop.cs"), "the save seen");
        Ui.Hold(TimeSpan.FromMilliseconds(400), "long enough for a refresh the save might set off to have run");
        Ui.Settle();

        Assert.Equal(chosen, window.ExplorerView.SelectedNodes);
        Assert.Equal(loads, window.GitLog.Loads);
        Assert.Equal(3, window.FileIndex.Files.Count);
        Assert.Empty(Directory.EnumerateFiles(folder, "*" + FileText.SaveSuffix, SearchOption.AllDirectories));
        window.Close();
    }

    [AvaloniaFact]
    public void A_file_chosen_in_the_commit_window_stays_chosen_through_a_refresh_and_rollback_names_and_rolls_back_it_alone()
    {
        foreach (string name in new[] { "A.cs", "B.cs", "C.cs" })
        {
            File.WriteAllText(Path.Combine(folder, name), $"class {name[0]} {{ }}\n");
        }
        Git("add", "-A");
        Git("commit", "-q", "-m", "Three");
        foreach (string name in new[] { "A.cs", "B.cs", "C.cs", "Shop.cs" })
        {
            File.AppendAllText(Path.Combine(folder, name), "// changed\n");
        }
        MainWindow window = Open();
        window.Perform(AppActions.ShowCommit);
        CommitView commit = window.CommitView;
        Ui.Landed(() => commit.Rows.OfType<CommitRow>().Count() == 4, "the four changed files listed");
        Assert.Equal(4, commit.Chosen.Count);
        Avalonia.Controls.ListBox changes = Find(commit, commit.Rows);
        CommitRow a = commit.Rows.OfType<CommitRow>().First(r => r.Change.Name == "A.cs");
        changes.SelectedItem = a;

        Task again = window.Vcs.RefreshAsync();
        Ui.Landed(() => again.IsCompleted, "the status read again");
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "new\n");
        Task newer = window.Vcs.RefreshAsync();
        Ui.Landed(() => newer.IsCompleted, "the status read with the new file");
        Assert.Contains(commit.Rows.OfType<CommitRow>(), r => r.Change.Name == "Notes.txt");
        Assert.Same(a, commit.Rows.OfType<CommitRow>().First(r => r.Change.Name == "A.cs"));
        Assert.Equal(["A.cs"], changes.SelectedItems!.OfType<CommitRow>().Select(r => r.Change.Name));

        File.Delete(Path.Combine(folder, "A.cs"));
        Task deleted = window.Vcs.RefreshAsync();
        Ui.Landed(() => deleted.IsCompleted, "the status read with A.cs deleted");
        Assert.Equal([("A.cs", ChangeKind.Deleted)], changes.SelectedItems!.OfType<CommitRow>().Select(r => (r.Change.Name, r.Change.Kind)));

        RollbackChosen(window);
        Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), "the Rollback Changes dialog");
        ConfirmDialog dialog = window.OwnedWindows.OfType<ConfirmDialog>().Single();
        string[] shown = [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text ?? string.Empty)];
        Assert.Contains("A.cs", shown);
        Assert.DoesNotContain(shown, t => t is "B.cs" or "C.cs" or "Shop.cs" or "Notes.txt");
        Ui.Click(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.Button>().First(b => (string?)b.Content == "Rollback"), settle: false);
        Ui.Landed(() => window.Vcs.Changes.All(c => c.Name != "A.cs"), "A.cs rolled back");
        Assert.Equal("class A { }\n", File.ReadAllText(Path.Combine(folder, "A.cs")));
        Assert.Equal(["B.cs", "C.cs", "Notes.txt", "Shop.cs"], window.Vcs.Changes.Select(c => c.Name).Order(StringComparer.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void Commit_and_stash_are_off_while_one_runs_so_a_second_press_makes_no_second()
    {
        MainWindow window = Open();
        File.AppendAllText(Path.Combine(folder, "Shop.cs"), "// more\n");
        window.Perform(AppActions.ShowCommit);
        CommitView commit = window.CommitView;
        _ = window.Vcs.RefreshAsync();
        Ui.Landed(() => commit.Chosen.Any(c => c.Name == "Shop.cs"), "Shop.cs listed");
        commit.Message = "More";
        Ui.Settle();
        Avalonia.Controls.Button[] buttons = [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(commit).OfType<Avalonia.Controls.Button>()
            .Where(b => b.Content is "Commit" or "Commit and Push…")];
        Assert.Equal(2, buttons.Length);
        Assert.All(buttons, b => Assert.True(b.IsEnabled));

        // Pressed twice, as a double-click does; the pre-commit hook holds the commit until the second press is made.
        string go = Path.Combine(folder, ".git", "go");
        HoldCommits(go);
        Avalonia.Controls.Button press = buttons.First(b => b.Content is "Commit");
        try
        {
            Ui.Click(press, settle: false);
            Assert.All(buttons, b => Assert.False(b.IsEnabled, "A commit can be pressed again while one runs"));
            // Raised, not clicked: a pointer never reaches a button that is off, and this checks its handler refuses too.
#pragma warning disable RS0030
            press.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
#pragma warning restore RS0030
            Assert.True(window.Status?.StartsWith("Already under way: Committing 1 file", StringComparison.Ordinal) == true,
                $"A second press while the commit runs should be told it is under way, but the status bar says: {window.Status}");
        }
        finally
        {
            File.WriteAllText(go, string.Empty);
        }
        Ui.Landed(() => window.Vcs.Changes.Count == 0 && window.Status?.StartsWith("1 file committed", StringComparison.Ordinal) == true, $"the commit ({window.Status})");
        Assert.Equal("2", Git("rev-list", "--count", "HEAD").Trim());
        // Git reads what the commit did (slower on Windows) before it takes another change; one asked for meanwhile is refused.
        Ui.Landed(() => !window.Vcs.IsBusy, "the commit's end");

        File.AppendAllText(Path.Combine(folder, "Shop.cs"), "// stashed\n");
        commit.Message = "Not now";
        NamePopup stash = window.ShowStash()!;
        Ui.Settle();
        stash.Text = "Later";
        Task first = stash.SubmitAsync();
        Task second = stash.SubmitAsync();
        Assert.All(buttons, b => Assert.False(b.IsEnabled, "A commit can be pressed while a stash runs"));
        Ui.Landed(() => first.IsCompleted && second.IsCompleted, "the stash made");
        Assert.Null(stash.Problem);
        Assert.Single(Git("stash", "list").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        window.Close();
    }

    [AvaloniaFact]
    public void A_commit_whose_hook_leaves_a_job_holding_gits_output_is_done_within_seconds()
    {
        // A post-commit hook that leaves a job running, as `daemon &` does, holding git's error output while it lives.
        string job = Path.Combine(folder, ".git", "job");
        string hook = Path.Combine(folder, ".git", "hooks", "post-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "#!/bin/sh\necho 'watching the build'\n(cd / && exec sleep 300) &\necho $! > .git/job\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        MainWindow window = Open();
        File.AppendAllText(Path.Combine(folder, "Shop.cs"), "// more\n");
        window.Perform(AppActions.ShowCommit);
        CommitView commit = window.CommitView;
        _ = window.Vcs.RefreshAsync();
        Ui.Landed(() => commit.Chosen.Any(c => c.Name == "Shop.cs"), "Shop.cs listed");
        commit.Message = "More";
        Task<bool> committed = commit.CommitAsync(push: false);
        Ui.WaitFor(() => committed.IsCompleted, "the commit to be done once git had exited, its hook's job still holding git's errors", TimeSpan.FromSeconds(20));
        Assert.True(committed.Result, window.Status);
        Assert.Equal("More", Git("log", "-1", "--format=%s").Trim());
        Ui.Landed(() => !window.Vcs.IsBusy && window.Vcs.Changes.Count == 0, $"the commit's end, with nothing left to commit ({window.Status})");
        // Ended with git's process group; Windows' git hooks run under MSYS, whose ids are its own.
        int left = Processes.ReadId(job);
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(left > 0, "The hook never ran: it wrote no job's id");
            Assert.True(Processes.Gone(left), $"The job the hook left ({Processes.Describe(left)}) outlived the commit that ran it");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void The_divider_under_a_commits_files_drags_from_anywhere_along_it_giving_its_details_the_room_they_give_up()
    {
        MainWindow window = Open();
        window.Perform(AppActions.ShowGit);
        GitLogView log = window.GitLog;
        Ui.Landed(() => log.Rows.Count == 1, "the log");
        log.Select(log.Rows[0].Commit);
        Ui.Landed(() => log.Files.Count == 1, "the commit's file");
        Avalonia.Controls.ListBox files = Find(log, log.Files);
        Avalonia.Controls.TextBox details = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(log).OfType<Avalonia.Controls.TextBox>().Single(t => t.Text == log.Details);
        (double filesHeight, double detailsHeight) = (files.Bounds.Height, details.Bounds.Height);

        // A quarter of the way along the line between them, not at its middle.
        Point grip = files.TranslatePoint(new Point(files.Bounds.Width / 4, files.Bounds.Height + 0.5), window)!.Value;
        window.MouseDown(grip, Avalonia.Input.MouseButton.Left);
        for (int step = 1; step <= 10; step++)
        {
            window.MouseMove(grip + new Point(0, 4 * step), Avalonia.Input.RawInputModifiers.LeftMouseButton);
        }
        window.MouseUp(grip + new Point(0, 40), Avalonia.Input.MouseButton.Left);
        Ui.Settle();
        Assert.True(Math.Abs(files.Bounds.Height - (filesHeight + 40)) < 2 && Math.Abs(details.Bounds.Height - (detailsHeight - 40)) < 2,
            $"The commit's files and its details were {filesHeight} and {detailsHeight} tall, and {files.Bounds.Height} and {details.Bounds.Height} once the line between them was dragged 40 px down");
        window.Close();
    }

    [AvaloniaFact]
    public void A_status_read_begun_before_a_commit_that_ends_after_it_leaves_the_commit_window_empty()
    {
        // One git status made slow, as a large tree's is: the file system monitor hook sleeps when asked to.
        string slow = Path.Combine(folder, ".git", "slow");
        string slept = Path.Combine(folder, ".git", "slept");
        string hook = Path.Combine(folder, ".git", "slow-status.sh");
        File.WriteAllText(hook, $"#!/bin/sh\nif [ -f '{Unix(slow)}' ]; then\n    rm '{Unix(slow)}'\n    sleep 3\n    touch '{Unix(slept)}'\nfi\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Git("config", "core.fsmonitor", Unix(hook));
        MainWindow window = Open();
        Assert.Equal("main", window.Vcs.Branch);
        CodeEditor editor = window.EditorArea.CodeEditor;
        CommitView commit = window.CommitView;
        window.Perform(AppActions.ShowCommit);
        editor.GoTo(new TextPosition(2, 4));
        window.KeyTextInput("long ");
        window.Perform(AppActions.Save);
        Ui.Landed(() => commit.Chosen.Any(c => c.Name == "Shop.cs"), "Shop.cs listed");

        // Saved again: that save's status is the slow one, and has read the index before the commit is made.
        editor.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("sealed ");
        File.WriteAllText(slow, string.Empty);
        window.Perform(AppActions.Save);
        // Not settled: the slow status is background work, which settling would wait out before the commit began.
        Ui.PumpUntil(() => !File.Exists(slow), "the save's status to begin", TimeSpan.FromSeconds(30));
        commit.Message = "Count with a long";
        Task<bool> committed = commit.CommitAsync(push: false);
        Ui.Landed(() => committed.IsCompleted, "the commit made");
        Assert.True(committed.Result, window.Status);

        // The slow status ends after the commit's own, and what it found before the commit never lands over it.
        Ui.Landed(() => File.Exists(slept), "the slow status ended");
        Assert.True(window.Vcs.Changes.Count == 0,
            $"The Commit window lists {string.Join(", ", window.Vcs.Changes.Select(c => c.Name))} after the commit: a status read before it landed after the commit's own");
        Assert.Empty(commit.Rows);
        window.Close();
    }

    [AvaloniaFact]
    public void Coming_back_to_the_window_reads_no_last_commit_text_again_and_head_moved_elsewhere_moves_every_files_bars_in_one_read()
    {
        // Prices.cs has characters of more than a byte before its change, and New.cs, open between the others, no commit.
        const string Prices = "// Prix en €, café crème 😀\nclass Prices\n{\n    decimal euro = 1m;\n}\n";
        File.WriteAllText(Path.Combine(folder, "Prices.cs"), Prices);
        File.WriteAllText(Path.Combine(folder, "A.cs"), "class A { }\n");
        File.WriteAllText(Path.Combine(folder, "B.cs"), "class B { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "More");
        File.WriteAllText(Path.Combine(folder, "Prices.cs"), Prices.Replace("1m", "2m", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("int count", "long count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Dearer");
        File.WriteAllText(Path.Combine(folder, "New.cs"), "class New { }\n");
        MainWindow window = Open();
        foreach (string name in new[] { "Prices.cs", "A.cs", "New.cs", "B.cs" })
        {
            window.OpenFile(Path.Combine(folder, name));
        }
        Ui.Settle();
        Task opened = window.Vcs.RefreshAsync();
        Ui.Landed(() => opened.IsCompleted && window.Vcs.Branch == "main", "the working tree read");

        int reads = window.Vcs.HeadReads;
        Task back = window.Vcs.RefreshAsync();
        Ui.Landed(() => back.IsCompleted, "the status read again");
        Assert.True(window.Vcs.HeadReads == reads, $"Coming back to the window read last-commit texts {window.Vcs.HeadReads - reads} times though HEAD had not moved");

        // HEAD moved back a commit, as in a terminal.
        Git("reset", "-q", "--soft", "HEAD~1");
        Ui.WaitFor(() => Bars(window, "Shop.cs").Count > 0 && Bars(window, "Prices.cs").Count > 0, "the bars against the commit HEAD moved to");
        Assert.Equal([new ChangeMark(2, 1, ChangeMarkKind.Modified)], Bars(window, "Shop.cs"));
        Assert.Equal([new ChangeMark(3, 1, ChangeMarkKind.Modified)], Bars(window, "Prices.cs"));
        Assert.All(new[] { "A.cs", "B.cs", "New.cs" }, name => Assert.Empty(Bars(window, name)));
        Assert.True(window.Vcs.HeadReads == reads + 1, $"HEAD moving read the five open files' texts in {window.Vcs.HeadReads - reads} reads, not one");
        window.Close();
    }

    private static IReadOnlyList<ChangeMark> Bars(MainWindow window, string name) =>
        window.EditorArea.Documents.First(d => d.Name == name).ChangeMarks;

    /// <summary>A path as the hook's shell and git's configuration take it on every system.</summary>
    private static string Unix(string path) => path.Replace('\\', '/');

    private static Avalonia.Controls.ListBox Find(Avalonia.Controls.Control view, object rows) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Avalonia.Controls.ListBox>().First(l => ReferenceEquals(l.ItemsSource, rows));

    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile(Path.Combine(folder, "Shop.cs"));
        Ui.Settle();
        return window;
    }

    /// <summary>Lets the window's background work land and finds Git listing nothing changed; failing, it says what is
    /// still listed, whether Git is still busy, what the status bar says (git's own words when a rollback failed) and what
    /// git finds on disk.</summary>
    private void AssertNothingChanged(MainWindow window)
    {
        Ui.Settle();
        string listed = string.Join(", ", window.Vcs.Changes.Select(c => $"{c.Name} ({c.Kind})"));
        Assert.True(window.Vcs.Changes.Count == 0, $"Once its work had landed Git still listed {listed} and was {(window.Vcs.IsBusy ? "still busy" : "idle")}, "
            + $"the status bar said \"{window.Status}\" and git status says \"{Git("status", "--porcelain").Trim()}\"");
    }

    /// <summary>Gives the repository a pre-commit hook that holds each commit until <paramref name="go"/> is there, a minute
    /// at most, so a commit is surely under way while a test acts.</summary>
    private void HoldCommits(string go)
    {
        string hook = Path.Combine(folder, ".git", "hooks", "pre-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        // Git runs a hook in the working tree's top folder, so the path from there is the hook's on every system.
        string wait = Path.GetRelativePath(folder, go).Replace('\\', '/');
        File.WriteAllText(hook, $"#!/bin/sh\ni=0\nwhile [ ! -f '{wait}' ] && [ $i -lt 600 ]; do sleep 0.1; i=$((i + 1)); done\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Presses the Commit window's Rollback, as the person would, the Commit window shown first.</summary>
    private static void RollbackChosen(MainWindow window)
    {
        window.ShowToolWindow(ToolWindowIds.Commit);
        Ui.Settle();
        Avalonia.Controls.Button rollback = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.CommitView)
            .OfType<Avalonia.Controls.Button>()
            .First(b => Avalonia.Controls.ToolTip.GetTip(b) as string == "Rollback");
        Ui.Click(rollback, settle: false);
    }

    /// <summary>Shows the open file's diff, typed in on its right side, and closes it: the diff gone, held by nothing here.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<DiffView> DiffAndClose(MainWindow window)
    {
        Task<DiffWindow?> opening = window.ShowDiffAsync(window.EditorArea.Active!.Path);
        Ui.Landed(() => opening.IsCompleted, "the diff open");
        DiffWindow diff = opening.Result!;
        Assert.Same(window.EditorArea.Active!.Text, diff.View.Right.Document);
        diff.View.Right.Focus();
        diff.KeyTextInput("// ");
        Ui.Landed(() => diff.View.Hunks.Count == 1, "the difference typed");
        diff.Close();
        Ui.Settle();
        Assert.Empty(window.OwnedWindows);
        return new WeakReference<DiffView>(diff.View);
    }

    private static IReadOnlyList<ChangeMark> Marks(MainWindow window) => window.EditorArea.Active?.ChangeMarks ?? [];

    private static string Text(MainWindow window) => window.EditorArea.Active!.Text.Snapshot.ToString();

    /// <summary>Whether colours give the word at an offset a field's colour.</summary>
    private static bool Field(IHighlighter? colours, TextSnapshot text, int at) =>
        colours?.GetSpans(text, at, 1).Any(s => s.Start == at && s.Kind == TokenKind.Field) == true;

    /// <summary>What each stash says, the newest first.</summary>
    private string[] Stashes() => Git("stash", "list", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private string Git(params string[] arguments) => TestGit.Run(folder, arguments);
}
