using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A word chosen in a file no language server reads (Markdown, plain text), by a double-click or by keys,
/// lights its other places as whole words in its case, each marked on the scrollbar; a caret resting with nothing chosen,
/// or part of a word chosen, lights nothing. In a file of two hundred thousand places the window's thread pays for the
/// thousand around the word chosen, as in a file of two thousand: the text is searched off it.</summary>
public sealed class WordPlacesTests : IDisposable
{
    private const string Notes = "# Notes\n\nEach word chosen lights its other places.\n\n- a word in a list\n"
        + "- words, wordy and sword are others\n- Word with a capital is another\n- the last word\n";

    private readonly string folder = TestFolders.Create("word-places-");

    public void Dispose() => TestFolders.Delete(folder);

    internal void A_word_chosen_in_markdown_or_plain_text_lights_its_other_places_and_marks_each_on_the_scrollbar()
    {
        File.WriteAllText(Path.Combine(folder, "notes.md"), Notes);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), Notes);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile(Path.Combine(folder, "notes.md"));
        CodeEditor editor = window.EditorArea.CodeEditor;

        Ui.DoubleClick(editor, Middle(editor, Notes.IndexOf("word chosen", StringComparison.Ordinal) + 2));
        Ui.WaitFor(() => editor.UsageHighlights.Count == 3, () => $"word's three places lit, not [{Lit(editor)}]");
        Assert.Equal([2, 4, 7], editor.UsageHighlights.Select(u => Line(editor, u.Range.Start)));
        Assert.All(editor.UsageHighlights, u => Assert.True(Text(editor, u.Range) == "word" && u.ByText, $"Lit: [{Lit(editor)}]"));
        List<CodeEditor.StripeMark> marks = [.. editor.StripeMarks.Where(m => m.Kind is StripeMarkKind.Usage or StripeMarkKind.WriteUsage)];
        Assert.Equal(editor.UsageHighlights.Select(u => Ui.RowOf(editor, u.Range.Start)), marks.Select(m => m.Row));
        Assert.All(marks, m => Assert.Equal("Same word", m.Message));

        Ui.Click(editor, Middle(editor, Notes.IndexOf("list", StringComparison.Ordinal) + 1));
        Assert.Empty(editor.UsageHighlights);
        Assert.DoesNotContain(editor.StripeMarks, m => m.Kind is StripeMarkKind.Usage or StripeMarkKind.WriteUsage);
        Ui.Settle();
        Assert.True(editor.UsageHighlights.Count == 0, $"A caret resting with nothing chosen lit [{Lit(editor)}]");

        window.OpenFile(Path.Combine(folder, "notes.txt"));
        editor = window.EditorArea.CodeEditor;
        editor.Focus();
        int words = Notes.IndexOf("words,", StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(new Selection(words, words + 4));
        Ui.Settle();
        Assert.True(editor.UsageHighlights.Count == 0, $"Part of a word chosen (word of words) lit [{Lit(editor)}]");

        // Word Left with Shift, by the system's keymap: a move back stops at a word's start on every system.
        editor.Selections = SelectionSet.Single(Notes.IndexOf("word in", StringComparison.Ordinal) + 4);
        Keystroke wordLeft = Keystroke.Parse(Ui.Shortcut(EditorActions.WordLeft));
        Ui.Press(window, new Keystroke(wordLeft.Key, wordLeft.Modifiers | KeyModifiers.Shift).Notation());
        Assert.Equal("word", Text(editor, editor.Selections.Primary.Range));
        Ui.WaitFor(() => editor.UsageHighlights.Count == 3, () => $"word's three places lit in plain text, chosen by keys, not [{Lit(editor)}]");
        Assert.Equal([2, 4, 7], editor.UsageHighlights.Select(u => Line(editor, u.Range.Start)));
        window.Close();
    }

    /// <summary>The same word chosen in a file of 2,000 places and in one of 200,000, in turns: in both the thousand around
    /// it are lit, those on screen among them, and the window's thread allocates for the large what it does for the small.</summary>
    internal void A_word_written_200_000_times_lights_the_thousand_around_it_costing_the_windows_thread_what_2_000_do()
    {
        const string Words = "word, sword and word\n";
        File.WriteAllText(Path.Combine(folder, "small.txt"), string.Concat(Enumerable.Repeat(Words, 1_000)));
        File.WriteAllText(Path.Combine(folder, "large.txt"), string.Concat(Enumerable.Repeat(Words, 100_000)));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile(Path.Combine(folder, "small.txt"));
        long blink = Ui.BlinkBytes(window.EditorArea.CodeEditor);
        long small = long.MaxValue;
        long large = long.MaxValue;
        // Taken in turns, so what the process takes on meanwhile weighs on both alike.
        for (int round = 0; round < 3; round++)
        {
            small = Math.Min(small, Choose(window, "small.txt", 500, blink));
            large = Math.Min(large, Choose(window, "large.txt", 50_000, blink));
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"Lighting a word's places allocated {small:N0} bytes on the window's thread among 2,000 and {large:N0} among 200,000");
        Assert.True(large <= small + (64 * 1024),
            $"Lighting a word's places allocated {large:N0} bytes on the window's thread in a file of 200,000 of them, against {small:N0} in one of 2,000: the file was searched or lit whole there");
        window.Close();
    }

    /// <summary>The word at a line's start double-clicked, nothing lit before: what the window's thread allocated until its
    /// places were lit, but for the caret's blinks meanwhile.</summary>
    private long Choose(MainWindow window, string name, int line, long blink)
    {
        window.OpenFile(Path.Combine(folder, name));
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        editor.GoTo(new TextPosition(line, 5));
        Ui.Settle();
        Assert.True(editor.UsageHighlights.Count == 0, $"{name} had [{editor.UsageHighlights.Count}] places lit before its word was chosen");
        int at = editor.Document.Snapshot.GetLineStart(line);
        GC.Collect(1, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        long blinks = editor.Blinks;
        long before = GC.GetAllocatedBytesForCurrentThread();
        Ui.DoubleClick(editor, Middle(editor, at + 2));
        Ui.WaitFor(() => editor.UsageHighlights.Count == TextSearch.MaxMatches, () => $"{TextSearch.MaxMatches} places lit in {name}, not {editor.UsageHighlights.Count}");
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before - ((editor.Blinks - blinks) * blink);

        Assert.Contains(new TextRange(at, 4), editor.UsageHighlights.Select(u => u.Range));
        (int first, int last) = editor.LinesShown;
        TextSnapshot text = editor.Document.Snapshot;
        for (int shown = first; shown <= last && shown < text.LineCount - 1; shown++)
        {
            int start = text.GetLineStart(shown);
            Assert.True(editor.UsageHighlights.Any(u => u.Range.Start == start) && editor.UsageHighlights.Any(u => u.Range.Start == start + 16),
                $"Line {shown} of {name}, on screen, has a place of the word unlit, though the thousand lit are those around the one chosen on line {line}");
        }
        Assert.Equal(TextSearch.MaxMatches, editor.StripeMarks.Count(m => m.Kind == StripeMarkKind.Usage));
        return bytes;
    }

    private static Point Middle(CodeEditor editor, int offset) => editor.PointOf(offset) + new Point(1, editor.LineHeight / 2);

    private static string Text(CodeEditor editor, TextRange range) => editor.Document.Snapshot.GetText(range);

    private static int Line(CodeEditor editor, int offset) => editor.Document.Snapshot.GetLineFromOffset(offset);


    /// <summary>What is lit, each as its text and line.</summary>
    private static string Lit(CodeEditor editor) =>
        string.Join(", ", editor.UsageHighlights.Select(u => $"{Text(editor, u.Range)}@{Line(editor, u.Range.Start)}"));
}
