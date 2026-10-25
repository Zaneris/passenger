using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>The words that changed inside changed lines, marked over a real working tree: in Show Diff's two sides (by
/// words at first, the diff's Settings choosing lines, characters or nothing, which the next diff keeps), in a change
/// bar's popup and the file beside it (Highlight Words turning them off), in the merge window's three panes, and
/// quickly in a large file changed all over.</summary>
[Collection(Alone.Name)]
public sealed class DiffWordsTests : IDisposable
{
    private const string Original = "class Shop\n{\n    int count;\n\n    void Add() { count++; }\n}\n";

    private readonly string folder = TestFolders.Create("words-");

    public DiffWordsTests() => TestFolders.Prepare(() =>
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original);
        TestGit.Init(folder);
    }, folder);

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_line_with_one_word_changed_marks_only_that_word_on_both_sides_of_the_diff_and_in_the_change_popup()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Replace(editor, "int count", "int", "long");
        Ui.Landed(() => Marks(window).Count == 1, "a change bar on the changed line");
        string now = Text(window);

        Task<DiffWindow?> opening = window.ShowDiffAsync(window.EditorArea.Active!.Path);
        Ui.Landed(() => opening.IsCompleted, "the diff open");
        DiffWindow diff = opening.Result!;
        DiffView view = diff.View;
        Assert.Equal(DiffHighlighting.Words, view.Highlighting);
        Assert.Equal([new DiffHunk(2, 1, 2, 1)], view.Hunks);
        Assert.Equal(["int"], Marked(Original, view.Words.Left));
        Assert.Equal(["long"], Marked(now, view.Words.Right));
        Assert.All(view.Words.Left.Concat(view.Words.Right), w => Assert.Equal(ChangeMarkKind.Modified, w.Kind));
        diff.Close();

        ChangePopup popup = ClickBar(window, editor, 2);
        Assert.Equal("    int count;", popup.Before);
        Assert.Equal(["int"], Marked(popup.Before!, popup.WordsShown));
        Assert.Equal(["long"], Marked(now, editor.InlineChanges));

        popup.HighlightToggle.IsChecked = false;
        Ui.Settle();
        Assert.Empty(popup.WordsShown);
        Assert.Empty(editor.InlineChanges);
        Assert.False(window.Settings.ChangePopupWords);
        Ui.Press(window, "Escape");
        Assert.False(window.IsPopupOpen);
        ChangePopup again = ClickBar(window, editor, 2);
        Assert.False(again.HighlightToggle.IsChecked);
        Assert.Empty(again.WordsShown);
        Assert.Empty(editor.InlineChanges);

        again.HighlightToggle.IsChecked = true;
        Ui.Settle();
        Assert.Equal(["long"], Marked(now, editor.InlineChanges));
        Ui.Press(window, "Escape");
        Assert.Empty(editor.InlineChanges);
        window.Close();
    }

    [AvaloniaFact]
    public void A_word_added_and_punctuation_changed_are_marked_as_themselves_and_the_diffs_settings_choose_lines_characters_or_nothing()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Replace(editor, "void Add() { count++; }", "void Add() { count++; }", "public void Add() { count += 2; }");
        Ui.Landed(() => Marks(window).Count == 1, "a change bar on the changed line");
        string now = Text(window);

        Task<DiffWindow?> opening = window.ShowDiffAsync(window.EditorArea.Active!.Path);
        Ui.Landed(() => opening.IsCompleted, "the diff open");
        DiffWindow diff = opening.Result!;
        DiffView view = diff.View;
        // "public " is new (green, on the file's side alone); "++" became " += 2".
        Assert.Equal(["++"], Marked(Original, view.Words.Left));
        Assert.Equal(["public ", " += 2"], Marked(now, view.Words.Right));
        Assert.Equal([ChangeMarkKind.Added, ChangeMarkKind.Modified], view.Words.Right.Select(w => w.Kind));

        Ui.ChooseFromMenu(view.SettingsButton, "Lines");
        Assert.Equal(DiffHighlighting.Lines, view.Highlighting);
        Assert.Empty(view.Words.Left);
        Assert.Empty(view.Words.Right);
        Assert.Single(view.Right.ChangeMarks);

        Ui.ChooseFromMenu(view.SettingsButton, "Characters");
        Ui.Settle();
        Assert.Equal(["public ", " ", "= 2"], Marked(now, view.Words.Right));

        Ui.ChooseFromMenu(view.SettingsButton, "None");
        Assert.Empty(view.Right.ChangeMarks);
        Assert.Empty(view.Left.ChangeMarks);
        Assert.Empty(view.Words.Right);
        Assert.Equal(DiffHighlighting.None, window.Settings.DiffHighlighting);
        diff.Close();

        // The next diff opens as chosen last; Ctrl+Shift+D (Cmd+Shift+D), Rider's key, opens its settings.
        opening = window.ShowDiffAsync(window.EditorArea.Active!.Path);
        Ui.Landed(() => opening.IsCompleted, "the diff open again");
        diff = opening.Result!;
        Assert.Equal(DiffHighlighting.None, diff.View.Highlighting);
        diff.View.Right.Focus();
        Ui.Press(diff, OperatingSystem.IsMacOS() ? "meta shift D" : "control shift D");
        Assert.True(diff.View.SettingsButton.ContextMenu?.IsOpen, "Ctrl+Shift+D (Cmd+Shift+D) did not open the diff's settings");
        diff.View.SettingsButton.ContextMenu!.Close();
        diff.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void The_merge_window_marks_the_words_each_side_changed_in_a_conflict_and_in_the_base_it_shows_as_the_result()
    {
        Git("switch", "-q", "-c", "feature");
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("int count", "short count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Short");
        Git("switch", "-q", "main");
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("int count", "long count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Long");
        ChildResult merged = Task.Run(() => ChildProcesses.RunAsync(ChildProcesses.Command("git", ["merge", "feature"], folder), limit: TimeSpan.FromMinutes(1))).GetAwaiter().GetResult();
        Assert.False(merged.Succeeded, "The merge was to stop for its conflict");

        MainWindow window = Open();
        Task<bool> merging = window.MergeAsync(Path.Combine(window.Folder!, "Shop.cs"));
        Ui.Landed(() => window.Frontmost is MergeWindow, "the merge window");
        MergeView view = ((MergeWindow)window.Frontmost).View;
        Assert.Equal(["long"], Marked(view.Left.Document.Snapshot.ToString(), view.Left.InlineChanges));
        Assert.Equal(["short"], Marked(view.Right.Document.Snapshot.ToString(), view.Right.InlineChanges));
        Assert.Equal(["int"], Marked(view.Result.Document.Snapshot.ToString(), view.Result.InlineChanges));
        Assert.All(view.Left.InlineChanges.Concat(view.Right.InlineChanges).Concat(view.Result.InlineChanges), w => Assert.Equal(ChangeMarkKind.Conflict, w.Kind));

        view.Accept(0, MergeSide.Left);
        Ui.Settle();
        Assert.Empty(view.Left.InlineChanges);
        Assert.Empty(view.Result.InlineChanges);
        Assert.Equal(["short"], Marked(view.Right.Document.Snapshot.ToString(), view.Right.InlineChanges));
        window.Confirm = (_, _, _) => Task.FromResult(true);
        ((MergeWindow)window.Frontmost).Close();
        Ui.Landed(() => merging.IsCompleted, "the merge window closed");
        window.Close();
    }

    [AvaloniaFact]
    public void A_20000_line_file_changed_on_every_twelfth_line_opens_its_diff_with_every_word_marked_quickly()
    {
        string generated = Path.Combine(folder, "Generated.txt");
        string[] lines = [.. Enumerable.Range(0, 20000).Select(i => $"    int value{i} = Compute({i}, \"label {i}\");")];
        File.WriteAllText(generated, string.Join("\n", lines) + "\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Generated");
        File.WriteAllText(generated, string.Join("\n", lines.Select((l, i) => i % 12 == 5 ? l.Replace("Compute", "Measure", StringComparison.Ordinal) : l)) + "\n");
        MainWindow window = Open();
        window.OpenFile(generated);
        Ui.Landed(() => Marks(window).Count == 1667, "a bar for each line changed");

        // The comparison the bars came from is the diff's; its 1,667 changes' words are worked out off the window's thread.
        Stopwatch opened = Stopwatch.StartNew();
        Task<DiffWindow?> opening = window.ShowDiffAsync(window.EditorArea.Active!.Path);
        Ui.Landed(() => opening.IsCompleted, "the diff open");
        DiffView view = opening.Result!.View;
        Ui.Settle();
        Assert.True(view.Words.Right.Count == 1667, $"Once its work had landed the diff marked {view.Words.Right.Count} words of the 1,667 changed");
        opened.Stop();
        Assert.True(opened.Elapsed < TimeSpan.FromSeconds(5).Scaled(), $"The diff of 1,667 changes took {opened.Elapsed.TotalMilliseconds:F0} ms to open with its words marked");
        Assert.All(Marked(view.Right.Document.Snapshot.ToString(), view.Words.Right), w => Assert.Equal("Measure", w));

        opening.Result.Close();
        window.Perform(AppActions.CloseTab);
        File.WriteAllText(generated, string.Join("\n", lines.Select(l => l.Replace("int", "long", StringComparison.Ordinal))) + "\n");
        window.OpenFile(generated);
        Ui.Landed(() => Marks(window).Count == 1, "one bar over the whole rewrite");
        opened.Restart();
        opening = window.ShowDiffAsync(window.EditorArea.Active!.Path);
        Ui.Landed(() => opening.IsCompleted, "the rewrite's diff open");
        opened.Stop();
        Assert.True(opened.Elapsed < TimeSpan.FromSeconds(5).Scaled(), $"The diff of a 20,000-line rewrite took {opened.Elapsed.TotalMilliseconds:F0} ms to open");
        Assert.Empty(opening.Result!.View.Words.Right);
        opening.Result.Close();
        window.Close();
    }

    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile(Path.Combine(folder, "Shop.cs"));
        Ui.Settle();
        return window;
    }

    /// <summary>Selects a word inside a stretch of the open file and types over it, as the person would.</summary>
    private static void Replace(CodeEditor editor, string within, string word, string with)
    {
        string text = editor.Document.Snapshot.ToString();
        int at = text.IndexOf(within, StringComparison.Ordinal) + within.IndexOf(word, StringComparison.Ordinal);
        editor.Select(new TextRange(at, word.Length));
        ((Window)TopLevel.GetTopLevel(editor)!).KeyTextInput(with);
        Ui.Settle();
    }

    /// <summary>Clicks a line's change bar and gives the popup it opens.</summary>
    private static ChangePopup ClickBar(MainWindow window, CodeEditor editor, int line)
    {
        Ui.Click(editor, editor.GutterColumnPoint(GutterColumn.Changes, line), settle: false);
        Ui.Landed(() => window.Popup is ChangePopup, "the change's popup");
        return (ChangePopup)window.Popup!;
    }

    /// <summary>The text each mark covers.</summary>
    private static string[] Marked(string text, IEnumerable<InlineChange> marks) => [.. marks.Select(m => text.Substring(m.Range.Start, m.Range.Length))];

    private static IReadOnlyList<ChangeMark> Marks(MainWindow window) => window.EditorArea.Active?.ChangeMarks ?? [];

    private static string Text(MainWindow window) => window.EditorArea.Active!.Text.Snapshot.ToString();

    private string Git(params string[] arguments) => TestGit.Run(folder, arguments);
}
