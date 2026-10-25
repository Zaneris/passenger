using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.History;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Local History over real files: every save and every change made elsewhere is a version, listed newest first
/// in the Local History window with its time and name ("External change"), the person's labels among them; a version
/// shows beside the file as it is now, Revert puts it back as one undoable step and a diff's arrow one change of it; a
/// folder's history lists its files' versions and brings back one deleted; a file's history follows it through a rename
/// in the Solution view; and the store keeps 5 days of activity and no more content than its limit.</summary>
public sealed class LocalHistoryTests : IDisposable
{
    private const string Original = "class Shop\n{\n    int count;\n\n    void Add()\n    {\n        count++;\n    }\n\n    int Total => count;\n}\n";

    // Each save changes another line of it, so a version against now differs in as many places as saves since.
    private static readonly string Saved1 = Original.Replace("    int count;", "    long int count;", StringComparison.Ordinal);
    private static readonly string Saved2 = Saved1.Replace("        count++;", "        count++; count++;", StringComparison.Ordinal);
    private static readonly string Saved3 = Saved2.Replace("    int Total", "    public int Total", StringComparison.Ordinal);

    private readonly string folder = PathNames.Canonical(TestFolders.Create("local-history-"));
    private readonly string trash = TestFolders.Create("trash-");
    private long clock;

    public LocalHistoryTests() => Trash.Folder = trash;

    public void Dispose()
    {
        Trash.Folder = null;
        TestFolders.Delete(folder);
        TestFolders.Delete(trash);
    }

    [AvaloniaFact]
    public void Three_saves_and_a_change_made_elsewhere_are_four_versions_newest_first_the_last_named_an_external_change()
    {
        string shop = Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        SaveThree(window);

        string elsewhere = Saved3.Replace("class Shop", "sealed class Shop", StringComparison.Ordinal);
        File.WriteAllText(shop, elsewhere);
        Ui.WaitFor(() => Text(window) == elsewhere, "the open file to take the change made elsewhere");

        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Assert.True(window.DockHost.IsShown(ToolWindowIds.LocalHistory), "Show History left the Local History window hidden");
        Assert.Equal(["Shop.cs"], window.LocalHistoryWindow.Titles);
        Ui.Landed(() => history.Rows.Count == 4, "the four versions: three saves and the change made elsewhere");
        Assert.Equal(["External change", "Change", "Change", "Change"], history.Rows.Select(r => r.Title));
        Assert.True(history.Rows.Zip(history.Rows.Skip(1)).All(p => p.First.Time >= p.Second.Time), "The versions are not listed newest first");

        Assert.Same(history.Rows[0], history.Selected);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Saved3, "the file as saved last, before the external change");
        Assert.Equal([new DiffHunk(0, 1, 0, 1)], history.Diff!.Hunks);
        window.Close();
    }

    [AvaloniaFact]
    public void An_old_versions_diff_shows_what_changed_since_beside_the_open_file()
    {
        Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        SaveThree(window);

        Ui.ChooseFromEditorMenu(window, "Local History", "Show History…");
        HistoryTab history = window.LocalHistoryWindow.Shown!;
        Ui.Landed(() => history.Rows.Count == 3, "the three saves");
        history.Select(history.Rows[^1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Original, "the oldest version, the file before its first save");
        Assert.Same(window.EditorArea.Active!.Text, history.Diff!.Right.Document);
        Assert.Equal([new DiffHunk(2, 1, 2, 1), new DiffHunk(6, 1, 6, 1), new DiffHunk(9, 1, 9, 1)], history.Diff.Hunks);

        history.Select(history.Rows[1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Saved1, "the version after the first save");
        Assert.Equal([new DiffHunk(6, 1, 6, 1), new DiffHunk(9, 1, 9, 1)], history.Diff!.Hunks);
        window.Close();
    }

    /// <summary>A XAML file's version is coloured as the file is on both sides; its diff opens as Settings' Highlighting
    /// Differences says and keeps a change to it for every diff after; and shown again after another history's tab, it is
    /// the version still, coloured.</summary>
    [AvaloniaFact]
    public void A_XAML_files_version_is_coloured_on_both_sides_and_its_diff_highlights_as_Settings_says()
    {
        const string Xaml = "<Window xmlns=\"https://github.com/avaloniaui\">\n    <TextBlock Text=\"Hello\" />\n</Window>\n";
        Write("MainWindow.axaml", Xaml);
        string shop = Write("Shop.cs", Original);
        MainWindow window = Open("MainWindow.axaml");
        window.ApplySettings(window.Settings with { DiffHighlighting = DiffHighlighting.Lines });
        Save(window, 1, 21, "Hi, ");

        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Xaml, "the file before its save");
        DiffView diff = history.Diff!;
        Ui.WaitFor(() => Tagged(diff.Left) && Tagged(diff.Right), () => $"the element's name coloured as a tag on both sides: left {Tagged(diff.Left)}, right {Tagged(diff.Right)}");
        Assert.True(diff.Highlighting == DiffHighlighting.Lines, $"The history's diff opened highlighting {diff.Highlighting}, not as Settings has it (Lines)");

        Ui.ChooseFromMenu(diff.SettingsButton, "Characters");
        Assert.True(window.Settings.DiffHighlighting == DiffHighlighting.Characters,
            $"Characters, chosen in the history's diff, left Settings at {window.Settings.DiffHighlighting}");

        window.ShowLocalHistory(shop);
        Ui.Settle();
        Assert.Same(history, window.ShowLocalHistory(Path.Combine(folder, "MainWindow.axaml")));
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Xaml && history.Diff.Right.Document.Snapshot.LineCount > 1,
            "the version shown again beside the file once its tab is back");
        Ui.WaitFor(() => Tagged(history.Diff!.Left) && Tagged(history.Diff.Right), "the element's name coloured on both sides again");
        Assert.Equal(DiffHighlighting.Characters, history.Diff!.Highlighting);
        window.Close();

        static bool Tagged(CodeEditor pane) =>
            pane.Highlighter?.GetSpans(pane.Document.Snapshot, 1, 6).Any(s => s.Start == 1 && s.Kind == TokenKind.Tag) == true;
    }

    [AvaloniaFact]
    public void A_diff_of_a_file_that_fits_opens_at_its_top_though_its_change_is_near_its_end()
    {
        string lines = string.Concat(Enumerable.Range(1, 20).Select(i => $"// line {i}\n"));
        Write("Notes.cs", lines);
        MainWindow window = Open("Notes.cs");
        Save(window, 17, 3, "changed ");
        Save(window, 18, 3, "changed ");
        Ui.ChooseFromEditorMenu(window, "Local History", "Show History…");
        HistoryTab history = window.LocalHistoryWindow.Shown!;
        Ui.Landed(() => history.Rows.Count == 2, "the two saves");
        // Maximized, the window has room for the whole file; the older version's diff opens in that room.
        window.DockHost.ToggleMaximize(window.DockHost.Windows.Single(w => w.Id == ToolWindowIds.LocalHistory));
        Ui.Settle();
        history.Select(history.Rows[1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == lines, "the file before its first save");

        // The whole file fits the pane, so it opens at its top, its first line whole under the header.
        Passenger.Editor.CodeEditor right = history.Diff!.Right;
        Assert.True(right.Document.Snapshot.LineCount * right.LineHeight <= right.Bounds.Height, "The test's file is taller than the diff's pane");
        Assert.True(right.ScrollOffset.Y == 0 && history.Diff.Left.ScrollOffset.Y == 0,
            $"The diff of a file that fits opens scrolled {right.ScrollOffset.Y:0.#} px down, its first line under the header");
        window.Close();
    }

    [AvaloniaFact]
    public void Revert_puts_a_version_back_as_one_undoable_step_and_a_diffs_arrow_puts_back_one_change()
    {
        string shop = Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        SaveThree(window);
        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Ui.Landed(() => history.Rows.Count == 3, "the three saves");

        history.Select(history.Rows[1]);
        Ui.Settle();
        Task reverting = history.RevertAsync();
        Ui.WaitFor(() => reverting.IsCompleted, "the revert");
        Assert.Equal(Saved1, Text(window));
        Assert.Equal(Saved1, File.ReadAllText(shop));
        Assert.False(window.EditorArea.Active!.IsModified, "The revert was not written to the file");
        Ui.Landed(() => history.Rows.Count == 4 && history.Rows[0].Title.StartsWith("Revert to ", StringComparison.Ordinal), "the revert kept as a version of its own");

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(Saved3, Text(window));

        history.Select(history.Rows[^1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Original && history.Diff.Hunks.Count == 3, "the first version's three changes");
        history.Diff!.Revert(history.Diff.Hunks[1]);
        Ui.Settle();
        Assert.Equal(Saved3.Replace("count++; count++;", "count++;", StringComparison.Ordinal), Text(window));
        Assert.Equal([new DiffHunk(2, 1, 2, 1), new DiffHunk(9, 1, 9, 1)], history.Diff.Hunks);
        window.Close();
    }

    [AvaloniaFact]
    public void Revert_across_a_conversion_brings_back_the_versions_text_in_its_own_encoding()
    {
        const string Cafe = "public class Café { }\n";
        string path = Write("Cafe.cs", "class Café { }\n");
        MainWindow window = Open("Cafe.cs");
        Save(window, 0, 0, "public ");
        // Converted to Latin-1 from the status bar's encoding widget: é, two bytes in UTF-8, is one now.
        FilePropertiesTests.Click(window, "EncodingButton");
        FilePropertiesTests.Choose(window, "ISO-8859-1");
        FilePropertiesTests.Answer(window, "Cafe.cs: Reload or Convert to ISO-8859-1", "Convert");
        Assert.Equal(System.Text.Encoding.Latin1.GetBytes(Cafe), File.ReadAllBytes(path));

        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Ui.Landed(() => history.Rows.Count == 2, "the save and the conversion");
        history.Select(history.Rows[0]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Cafe, "the version before the conversion");
        Task reverting = history.RevertAsync();
        Ui.WaitFor(() => reverting.IsCompleted, "the revert");
        Assert.True(Text(window) == Cafe, $"The revert read the UTF-8 version in Latin-1: {Text(window)}");
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(Cafe), File.ReadAllBytes(path));
        Assert.Equal("UTF-8", FilePropertiesTests.Shown(window, "EncodingText"));
        Assert.False(window.EditorArea.Active!.IsModified, $"The revert was not written: {window.Status}");

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(Cafe, Text(window));
        Assert.Equal(System.Text.Encoding.Latin1.GetBytes(Cafe), File.ReadAllBytes(path));
        Assert.Equal("ISO-8859-1", FilePropertiesTests.Shown(window, "EncodingText"));
        window.Close();
    }

    /// <summary>A binary file's tab holds its bytes drawn as signs, none of the file's text: a revert to a version from
    /// when the file was text writes that version's bytes, and the tab shows them.</summary>
    [AvaloniaFact]
    public void Revert_of_a_file_open_as_its_bytes_to_a_version_from_when_it_was_text_writes_that_version()
    {
        string path = Write("notes.dat", "First\n");
        MainWindow window = Open("notes.dat");
        File.WriteAllText(path, "Second\n");
        Ui.WaitFor(() => Text(window) == "Second\n", "the tab to take the change made elsewhere");
        window.EditorArea.Close(window.EditorArea.Active!);
        Ui.Settle();

        // Written elsewhere as bytes no text holds, it opens as them.
        File.WriteAllBytes(path, [0x7F, (byte)'E', 0, 1, (byte)'\n']);
        window.OpenFile(path);
        Ui.Settle();
        Assert.True(window.EditorArea.Active is { IsBinary: true }, "notes.dat, now binary, did not open as its bytes' text");

        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Ui.Landed(() => history.Rows.Count == 1, "the change made elsewhere while it was text");
        history.Select(history.Rows[0]);
        Ui.Settle();
        Task reverting = history.RevertAsync();
        Ui.WaitFor(() => reverting.IsCompleted, "the revert");
        Assert.True(File.ReadAllText(path) == "First\n", $"The revert did not write the version back: {window.Status}");
        Ui.WaitFor(() => Text(window) == "First\n", "the tab to show the bytes the revert wrote");
        Assert.False(window.EditorArea.Active!.IsModified, "The revert left the binary file's tab modified");
        window.Close();
    }

    [AvaloniaFact]
    public void Put_label_puts_a_label_among_the_versions_that_shows_the_file_as_it_was_then()
    {
        Write("Shop.cs", Original);
        MainWindow window = Open("Shop.cs");
        Save(window, 2, 4, "long ");
        Ui.ChooseFromEditorMenu(window, "Local History", "Put Label…");
        Assert.IsType<NamePopup>(window.Popup);
        window.KeyTextInput("Before cleanup");
        Ui.Press(window, "ENTER");
        Assert.False(window.IsPopupOpen, "The label's name was refused");
        Assert.Equal("Label Before cleanup created", window.Status);
        Save(window, 6, 8, "count++; ");

        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Ui.Landed(() => history.Rows.Count == 3, "the two saves and the label between them");
        Assert.Equal(["Change", "Before cleanup", "Change"], history.Rows.Select(r => r.Title));
        Assert.True(history.Rows[1].IsLabel, "The label is listed as a change, not a label");
        history.Select(history.Rows[1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Saved1, "the file as it was when the label was put");
        window.Close();
    }

    [AvaloniaFact]
    public void What_a_git_checkout_changes_is_kept_under_Riders_name_for_it_not_as_an_external_change()
    {
        string shop = Write("Shop.cs", Original);
        TestGit.Init(folder);
        TestGit.Run(folder, "switch", "-q", "-c", "side");
        File.WriteAllText(shop, Saved1);
        TestGit.Run(folder, "commit", "-q", "-am", "Long");
        TestGit.Run(folder, "switch", "-q", "main");
        MainWindow window = Open("Shop.cs");
        Ui.WaitFor(() => window.Vcs.Repository is not null, "the folder's repository");

        Task<VcsOutcome> checkout = window.Vcs.CheckoutAsync("side");
        Ui.WaitFor(() => checkout.IsCompleted && Text(window) == Saved1, "side checked out and the open file following it");
        Assert.True(checkout.Result.Succeeded, checkout.Result.Message);
        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Ui.WaitFor(() => history.Rows.Count == 1, "the checkout's change to the file");
        Assert.Equal("Git checkout", history.Rows[0].Title);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Original, "the file as main had it");
        window.Close();
    }

    [AvaloniaFact]
    public void A_folders_history_lists_its_files_versions_and_brings_back_a_file_deleted_in_the_explorer()
    {
        string a = Write("src/A.cs", "class A\n{\n}\n");
        string b = Write("src/B.cs", "class B\n{\n}\n");
        Write("Notes.txt", "notes\n");
        MainWindow window = Open("src/A.cs");
        Save(window, 0, 0, "sealed ");
        window.OpenFile(b);
        Save(window, 0, 0, "sealed ");
        window.OpenFile(Path.Combine(folder, "Notes.txt"));
        Save(window, 0, 5, " kept");
        const string savedB = "sealed class B\n{\n}\n";
        Assert.Equal(savedB, File.ReadAllText(b));

        window.Confirm = (_, _, _) => Task.FromResult(true);
        Ui.ChooseFromRowMenu(window, Show(window, b), "Edit", "Delete…");
        Ui.WaitFor(() => !File.Exists(b), "B.cs deleted");

        Ui.ChooseFromRowMenu(window, Show(window, Path.Combine(folder, "src")), "Local History", "Show History…");
        HistoryTab history = window.LocalHistoryWindow.Shown!;
        Assert.Equal("src", window.LocalHistoryWindow.Titles.Single());
        Ui.Landed(() => history.Rows.Count == 3, "A.cs's save, B.cs's save and B.cs's deletion");
        Assert.Equal(["Delete B.cs", "Modify B.cs", "Modify A.cs"], history.Rows.Select(r => r.Title));

        Ui.Landed(() => history.Files.Count == 1, "the file the deletion took");
        Assert.Equal((PathNames.Canonical(b), ChangeKind.Deleted), (history.Files[0].Path, history.Files[0].Kind));
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == savedB, "B.cs as it was deleted");
        Task reverting = history.RevertAsync();
        Ui.WaitFor(() => reverting.IsCompleted, "the revert");
        Assert.Equal(savedB, File.ReadAllText(b));

        history.Select(history.Rows.First(r => r.Title == "Modify A.cs"));
        Ui.Landed(() => history.Files.Count == 2, "A.cs and B.cs changed since");
        Assert.Equal([(PathNames.Canonical(a), ChangeKind.Modified), (PathNames.Canonical(b), ChangeKind.Modified)], history.Files.Select(f => (f.Path, f.Kind)));
        window.Close();
    }

    [AvaloniaFact]
    public void A_files_history_follows_it_through_a_rename_in_the_solution_view()
    {
        Write("Shop/Shop.csproj", TestProjects.Library());
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"Shop/Shop.csproj\" />\n</Solution>\n");
        string order = Write("Shop/Order.cs", Original);
        MainWindow window = Open("Shop/Order.cs");
        Save(window, 2, 4, "long ");

        Ui.ChooseFromRowMenu(window, Show(window, order), "Edit", "Rename…");
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        popup.Text = string.Empty;
        window.KeyTextInput("Purchase.cs");
        Ui.Press(window, "ENTER");
        Assert.False(window.IsPopupOpen, $"The new name was refused: {popup.Problem}");
        string purchase = Path.Combine(folder, "Shop", "Purchase.cs");
        Assert.True(File.Exists(purchase));
        Save(window, 6, 8, "count++; ");

        Ui.ChooseFromRowMenu(window, Show(window, purchase), "Local History", "Show History…");
        HistoryTab history = window.LocalHistoryWindow.Shown!;
        Ui.Landed(() => history.Rows.Count == 3, "the saves under both names and the rename between them");
        Assert.Equal(["Change", "Rename Order.cs to Purchase.cs", "Change"], history.Rows.Select(r => r.Title));
        history.Select(history.Rows[^1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Original, "the file as it was under its old name");
        window.Close();
    }

    /// <summary>A language server's edit that makes a file over one there (a create with overwrite, then its text), as a
    /// refactoring's answer is applied: the file is written as a save writes it, so the history keeps what it held, which
    /// it never saw open, and Undo puts that back.</summary>
    [AvaloniaFact]
    public void A_file_a_servers_edit_makes_over_one_there_keeps_what_it_held_in_the_history_and_undo_puts_it_back()
    {
        Write("Other.cs", "class Other\n{\n}\n");
        string notes = Write("Notes.cs", Original);
        MainWindow window = Open("Other.cs");
        const string Made = "class Notes\n{\n}\n";
        string uri = Passenger.Lsp.DocumentUris.FromPath(notes);
        System.Text.Json.JsonElement edit = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            documentChanges = new object[]
            {
                new { kind = "create", uri, options = new { overwrite = true } },
                new { textDocument = new { uri, version = (int?)null }, edits = new[] { new { range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } }, newText = Made } } },
            },
        });
        Passenger.App.CSharp.WorkspaceEdits.ApplyAsCommand(window.EditorArea, edit, "Move to File");
        Assert.Equal(Made, File.ReadAllText(notes));

        window.Confirm = (_, _, _) => Task.FromResult(true);
        Ui.ClickRow(window, Show(window, notes));
        Ui.Run(window, EditorActions.Undo);
        Assert.True(File.ReadAllText(notes) == Original, $"Undo left the file made anew as '{File.ReadAllText(notes)}', not as it was: {window.Status}");

        Ui.ChooseFromRowMenu(window, Show(window, notes), "Local History", "Show History…");
        HistoryTab history = window.LocalHistoryWindow.Shown!;
        Ui.Landed(() => history.Rows.Count > 1, "the file's versions");
        history.Select(history.Rows[^1]);
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == Original, "the file as it was before the edit made it anew");

        new FileInfo(notes).IsReadOnly = true;
        try
        {
            Passenger.App.CSharp.WorkspaceEditFailed refused = Assert.Throws<Passenger.App.CSharp.WorkspaceEditFailed>(
                () => Passenger.App.CSharp.WorkspaceEdits.ApplyAsCommand(window.EditorArea, edit, "Move to File"));
            Assert.Equal("Notes.cs is read-only", refused.Message);
            Assert.Equal(Original, File.ReadAllText(notes));
        }
        finally
        {
            new FileInfo(notes).IsReadOnly = false;
        }
        window.Close();
    }

    [AvaloniaFact]
    public void The_store_keeps_days_of_activity_as_Riders_does_and_no_more_content_than_its_limit()
    {
        // Twelve saves six hours apart are three days: a store keeping one day keeps the last four.
        string kept = TestFolders.Create("history-state-");
        Write("Shop.cs", Original);
        Set(new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero));
        HistoryKeeping day = new(Days: 1) { Clock = Now };
        MainWindow window = Open("Shop.cs", kept, day);
        for (int save = 1; save <= 12; save++)
        {
            Set(Now() + TimeSpan.FromHours(6));
            SaveEdit(window, new TextEdit(0, 0, $"// {save}\n"));
        }
        HistoryTab history = ShowFromFileMenu(window, "Show History…");
        Ui.Landed(() => history.Rows.Count is > 0 and < 12, "the oldest saves pruned as the days turned");
        window.Close();

        MainWindow again = Open("Shop.cs", kept, day);
        history = ShowFromFileMenu(again, "Show History…");
        Ui.Landed(() => history.Rows.Count == 4, "the last day's four saves");
        history.Select(history.Rows[^1]);
        string eighth = string.Concat(Enumerable.Range(1, 8).Reverse().Select(n => $"// {n}\n")) + Original;
        Ui.Landed(() => history.Diff?.Left.Document.Snapshot.ToString() == eighth, "the oldest kept version, the file after the eighth save");
        Assert.Equal(4, File.ReadAllLines(Path.Combine(again.LocalHistory!.Root, "changes.log")).Length);
        again.Close();

        string small = TestFolders.Create("history-state-");
        HistoryKeeping bytes = new(Days: 5, Bytes: 16_000) { Clock = Now };
        MainWindow third = Open("Shop.cs", small, bytes);
        Random random = new(9);
        for (int save = 1; save <= 8; save++)
        {
            // Each a day apart, so the store prunes as each day turns.
            Set(Now() + TimeSpan.FromHours(25));
            string noise = Convert.ToHexString(random.GetItems<byte>(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), 3000));
            SaveEdit(third, new TextEdit(0, third.EditorArea.CodeEditor.Document.Snapshot.Length, $"// {save}\n{noise}\n"));
        }
        history = ShowFromFileMenu(third, "Show History…");
        Ui.Landed(() => history.Rows.Count is >= 1 and < 8, "the oldest versions pruned past the store's limit");
        Assert.StartsWith("// 7\n", Version(history, 0), StringComparison.Ordinal);
        third.Close();
    }

    private DateTimeOffset Now() => new(Interlocked.Read(ref clock), TimeSpan.Zero);

    private void Set(DateTimeOffset now) => Interlocked.Exchange(ref clock, now.UtcTicks);

    private static string Version(HistoryTab history, int row)
    {
        history.Select(history.Rows[row]);
        Ui.Settle();
        return history.Diff!.Left.Document.Snapshot.ToString();
    }

    /// <summary>Three saves of three edits, each to a line of its own.</summary>
    private static void SaveThree(MainWindow window)
    {
        Save(window, 2, 4, "long ");
        Save(window, 6, 8, "count++; ");
        Save(window, 9, 4, "public ");
        Assert.Equal(Saved3, Text(window));
    }

    /// <summary>Types at a place in the file shown and saves it with Save's keys.</summary>
    private static void Save(MainWindow window, int line, int column, string text)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        editor.GoTo(new TextPosition(line, column));
        window.KeyTextInput(text);
        Ui.Settle();
        Ui.Run(window, AppActions.Save);
        Assert.False(window.EditorArea.Active!.IsModified, "The save did not write the file");
    }

    /// <summary>An edit made in the file shown, as a paste would make it, then saved with Save's keys.</summary>
    private static void SaveEdit(MainWindow window, TextEdit edit)
    {
        window.EditorArea.CodeEditor.Focus();
        window.EditorArea.CodeEditor.ApplyEdits([edit]);
        Ui.Settle();
        Ui.Run(window, AppActions.Save);
        Assert.False(window.EditorArea.Active!.IsModified, "The save did not write the file");
    }

    /// <summary>A window on the folder with a file open and the keys in it; with no C# language server, which Local History
    /// asks nothing of, so a folder holding a project starts and restores none.</summary>
    private MainWindow Open(string file, string? state = null, HistoryKeeping? keeping = null)
    {
        MainWindow window = state is null ? new() { Width = 1200, Height = 800, StartsLanguageServer = false }
            : new() { Width = 1200, Height = 800, StateFolder = state, HistoryKeeping = keeping!, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile(Path.Combine(folder, file));
        window.EditorArea.CodeEditor.Focus();
        Ui.Settle();
        return window;
    }

    /// <summary>File, Local History, and one of its items, as the menu bar runs it.</summary>
    private static HistoryTab ShowFromFileMenu(MainWindow window, string item)
    {
        NativeMenu menu = Ui.Submenu(Ui.Submenu(window.MenuBar, "File"), "Local History");
        NativeMenuItem chosen = menu.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == item)
            ?? throw new InvalidOperationException($"File, Local History has no {item}: [{string.Join(", ", menu.Items.OfType<NativeMenuItem>().Select(i => i.Header))}]");
        Assert.True(chosen.IsEnabled, $"File, Local History, {item} is disabled");
        chosen.Command!.Execute(null);
        Ui.Settle();
        return window.LocalHistoryWindow.Shown ?? throw new InvalidOperationException($"{item} showed no history: {window.Status}");
    }

    /// <summary>Opens the Explorer's folders down to a file or folder, as a person does to get to it, and gives its row.</summary>
    private static ExplorerNode Show(MainWindow window, string path)
    {
        window.ExplorerView.Reveal(PathNames.Canonical(path));
        Ui.Settle();
        return window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Path == PathNames.Canonical(path))
            ?? throw new InvalidOperationException($"The Explorer does not show {path}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
    }

    private static string Text(MainWindow window) => window.EditorArea.Active!.Text.Snapshot.ToString();

    private string Write(string relative, string text) => TestFolders.Write(folder, relative, text);
}
