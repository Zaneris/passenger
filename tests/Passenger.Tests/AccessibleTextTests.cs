using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Editor;
using Passenger.Terminal;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The code editor's and the terminal's text as a screen reader's bridge reads it (<see cref="AccessibleText"/>):
/// ranges, lines, words and characters, the caret and what is selected, where a range is drawn, and what a key changed,
/// held to the text the control shows after edits, soft wrap, folds and a scrollback past its limit; and each control's
/// automation peer as a reader meets it.</summary>
public sealed class AccessibleTextTests : IDisposable
{
    private readonly string folder = TestFolders.Create("accessible-text-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>A reader reads the editor as a document named by its file whose value follows the caret, its text by
    /// range, line, word and character with what a key changed, scrolls it into view and takes its caret, soft wrap and
    /// folds leaving the file's lines as they are, and reads the terminal's lines from its scrollback, told of output
    /// as the lines it changed.</summary>
    [AvaloniaFact]
    public void A_reader_reads_the_editors_and_the_terminals_text_as_the_file_and_the_scrollback_hold_it()
    {
        Flow.StepApart<AccessibleTextTests>("the editor's text reads by range line word and character and tells what a key changed", t => t.The_editors_text_reads_by_range_line_word_and_character_and_tells_what_a_key_changed());
        Flow.StepApart<AccessibleTextTests>("a reader scrolls the editor's text into view and takes its caret and hears its line change", t => t.A_reader_scrolls_the_editors_text_into_view_and_takes_its_caret_and_hears_its_line_change());
        Flow.StepApart<AccessibleTextTests>("soft wrap and folds leave the file's lines as they are and change only where they are drawn", t => t.Soft_wrap_and_folds_leave_the_files_lines_as_they_are_and_change_only_where_they_are_drawn());
        Flow.StepApart<AccessibleTextTests>("the editor is a document named by its file whose value follows the caret from line to line", t => t.The_editor_is_a_document_named_by_its_file_whose_value_follows_the_caret_from_line_to_line());
        Flow.StepApart<AccessibleTextTests>("the terminal's lines read from its scrollback as kept and output is told as the lines it changed", t => t.The_terminals_lines_read_from_its_scrollback_as_kept_and_output_is_told_as_the_lines_it_changed());
    }

    private void The_editors_text_reads_by_range_line_word_and_character_and_tells_what_a_key_changed()
    {
        const string Text = "class Shop\r\n{\r\n    int count = 10;\r\n}\r\n";
        EditorRig rig = new(Text);
        AccessibleText text = rig.Editor.AccessibleText;
        List<AccessibleTextChange> changes = [];
        int moves = 0;
        text.Changed += (_, change) => changes.Add(change);
        text.CaretMoved += (_, _) => moves++;

        Assert.True(text.Length == Text.Length && text.Read(0, Text.Length) == Text, $"The editor reads \"{text.Read(0, text.Length)}\" of {text.Length} characters");
        Assert.True(text.LineCount == 5 && text.Line(2) == new LineSpan(2, 15, 19, 2), $"Line 3 is {text.Line(2)} of {text.LineCount} lines");
        int count = Text.IndexOf("count", StringComparison.Ordinal);
        Assert.True(text.LineOf(count + 2) == 2 && text.LineAt(count + 2) == new TextRange(15, 19), $"\"count\" is on line {text.LineOf(count + 2)}");
        Assert.True(text.Read(text.WordAt(count + 2)) == "count ", $"The word in \"count\" reads \"{text.Read(text.WordAt(count + 2))}\"");
        Assert.True(text.Read(text.WordAt(17)) == "    ", $"The word in the indent reads \"{text.Read(text.WordAt(17))}\"");
        Assert.True(text.Read(text.CharacterAt(10)) == "\r\n" && text.CharacterAt(Text.Length) == new TextRange(Text.Length, 0),
            "A line break is not read as one character, or the text's end as none");

        rig.PlaceCaret(count);
        Ui.Settle();
        Assert.True(text.Caret == count && moves > 0, $"The caret reads at {text.Caret}, not {count}, after {moves} moves told");
        for (int i = 0; i < "count".Length; i++)
        {
            rig.Press("shift RIGHT");
        }
        Assert.True(text.Selected is [var chosen] && text.Read(chosen) == "count", $"What is selected reads {string.Join(", ", text.Selected.Select(text.Read))}");

        changes.Clear();
        rig.Type("total");
        Assert.True(changes.Count > 0 && changes[0] == new AccessibleTextChange(count, 5, 1) && changes.Skip(1).All(c => c.Removed == 0 && c.Inserted == 1),
            $"Typing over \"count\" was told as {string.Join(", ", changes)}");
        Assert.True(text.Read(text.LineAt(text.Caret)) == "    int total = 10;", $"After typing, the caret's line reads \"{text.Read(text.LineAt(text.Caret))}\"");
    }

    private void A_reader_scrolls_the_editors_text_into_view_and_takes_its_caret_and_hears_its_line_change()
    {
        string text = string.Concat(Enumerable.Range(0, 400).Select(i => $"line {i}\n"));
        EditorRig rig = new(text);
        AccessibleText model = rig.Editor.AccessibleText;
        int lines = 0;
        model.CaretLineChanged += (_, _) => lines++;
        TextRange shown = model.Shown;
        Assert.True(shown.Start == 0 && model.LineOf(shown.End) is > 10 and < 100, $"The editor shows {model.Read(shown).Count(c => c == '\n')} lines from {shown.Start}");

        int far = text.IndexOf("line 300", StringComparison.Ordinal);
        model.Show(new TextRange(far, 8));
        Ui.Settle();
        Assert.True(model.Shown.Contains(far) && rig.Caret == 0 && lines == 0,
            $"Showing line 300 shows {model.Read(model.LineAt(model.Shown.Start))} on, the caret at {rig.Caret} after {lines} line changes told");

        int next = text.IndexOf("line 301", StringComparison.Ordinal);
        model.Select(new TextRange(next + 5, 3));
        Ui.Settle();
        Assert.True(model.Selected is [var chosen] && model.Read(chosen) == "301" && model.Caret == next + 8 && lines == 1 && model.CaretLine == "line 301",
            $"Choosing \"301\" chose {string.Join(", ", model.Selected.Select(model.Read))}, the caret at {model.Caret} on \"{model.CaretLine}\" after {lines} line changes told");
        model.Select(new TextRange(next + 2, 0));
        rig.Type("x");
        Assert.True(model.CaretLine == "lixne 301" && lines == 2 && model.Selected is [{ Length: 0 }], $"Typing on the line told {lines} line changes and reads \"{model.CaretLine}\"");
    }

    private void Soft_wrap_and_folds_leave_the_files_lines_as_they_are_and_change_only_where_they_are_drawn()
    {
        string longLine = string.Join(' ', Enumerable.Range(0, 60).Select(i => $"word{i}"));
        string text = $"header\n{longLine}\nbody\n{{\n    folded();\n    away();\n}}\ntail\n";
        EditorRig rig = new(text);
        AccessibleText model = rig.Editor.AccessibleText;
        rig.Editor.SoftWrap = true;
        Ui.Settle();
        LineSpan wrapped = model.Line(1);
        Assert.True(model.LineCount == 9 && wrapped.Length == longLine.Length, $"Soft wrap made {model.LineCount} lines, the long one {wrapped.Length} long");
        IReadOnlyList<Rect> rows = model.BoundsOf(new TextRange(wrapped.Start, wrapped.Length));
        Assert.True(rows.Count >= 2 && rows.Zip(rows.Skip(1)).All(p => p.Second.Y > p.First.Y),
            $"The wrapped line is drawn in {rows.Count} rows by its bounds: {string.Join("; ", rows)}");
        Assert.True(model.OffsetAt(rows[1].TopLeft + new Point(1, 1)) is var at && at > wrapped.Start && at < wrapped.End,
            "A point on the wrapped line's second row finds no place in it");

        int open = text.IndexOf('{', StringComparison.Ordinal);
        int close = text.IndexOf('}', StringComparison.Ordinal);
        rig.Editor.SetFoldRegions([new FoldRegion(open + 1, close, "...", Folded: true)]);
        Ui.Settle();
        TextRange inside = model.LineAt(text.IndexOf("away", StringComparison.Ordinal));
        Assert.True(model.Read(inside) == "    away();" && model.BoundsOf(inside).Count == 0,
            $"A folded line reads \"{model.Read(inside)}\", drawn at {string.Join("; ", model.BoundsOf(inside))}, where it should read whole and be drawn nowhere");
        TextRange tail = model.LineAt(text.IndexOf("tail", StringComparison.Ordinal));
        Assert.True(model.BoundsOf(tail) is [var after] && after.Y > rows[^1].Y, $"The line after the fold is drawn at {string.Join("; ", model.BoundsOf(tail))}");
    }

    private void The_editor_is_a_document_named_by_its_file_whose_value_follows_the_caret_from_line_to_line()
    {
        string notes = Path.Combine(folder, "notes.txt");
        File.WriteAllText(notes, "first line\nsecond line\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(notes);
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(editor);
            Assert.True(peer.GetAutomationControlType() == AutomationControlType.Document && peer.GetName() == "notes.txt",
                $"The editor reads as a {peer.GetAutomationControlType()} named \"{peer.GetName()}\"");
            IValueProvider value = peer.GetProvider<IValueProvider>() ?? throw new Xunit.Sdk.XunitException("The editor gives no value to a reader that reads only that");
            Assert.True(value.Value == "first line" && !value.IsReadOnly, $"The editor's value reads \"{value.Value}\" (read-only {value.IsReadOnly})");
            List<string?> told = [];
            peer.PropertyChanged += (_, e) =>
            {
                if (e.Property == ValuePatternIdentifiers.ValueProperty)
                {
                    told.Add(e.NewValue as string);
                }
            };
            editor.Focus();
            Ui.Press(window, "RIGHT");
            Ui.Press(window, "DOWN");
            Assert.True(told is [.., "second line"] && told.Count(t => t == "first line") == 0,
                $"Moving along a line then to the next told the values {string.Join(", ", told.Select(t => $"\"{t}\""))}");

            value.SetValue("second line, edited");
            Assert.True(editor.Document.Snapshot.ToString() == "first line\nsecond line, edited\n" && value.Value == "second line, edited",
                $"Setting the value made the text \"{editor.Document.Snapshot}\" and the value \"{value.Value}\", where it should put the caret's line in its place");
        }
        finally
        {
            window.Close();
        }
    }

    private void The_terminals_lines_read_from_its_scrollback_as_kept_and_output_is_told_as_the_lines_it_changed()
    {
        TerminalSession session = new(40, 5);
        TerminalView view = new() { Session = session };
        Window window = new() { Width = 500, Height = 200, Content = view };
        AutomationProperties.SetName(view, "Local");
        window.Show();
        try
        {
            Ui.Settle();
            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(view);
            Assert.True(peer.GetAutomationControlType() == AutomationControlType.Document && peer.GetName() == "Local",
                $"The terminal reads as a {peer.GetAutomationControlType()} named \"{peer.GetName()}\"");
            AccessibleText text = view.AccessibleText;
            Assert.True(text.IsReadOnly, "The terminal's text reads as one typing changes");
            List<AccessibleTextChange> changes = [];
            text.Changed += (_, change) => changes.Add(change);

            session.Say("one\r\ntwo \u4E2D\r\n");
            Assert.True(text.Read(text.LineAt(0)) == "one" && text.Read(text.LineAt(text.Line(1).Start)) == "two \u4E2D",
                $"The terminal reads \"{text.Read(0, text.Length)}\"");
            Assert.True(changes is [{ Start: 0 }] && changes[0].Inserted == text.Length, $"Output was told as {string.Join(", ", changes)} of {text.Length} characters");
            Assert.True(text.LineOf(text.Caret) == 2 && text.Caret == text.Line(2).Start, $"The caret reads at {text.Caret}, on line {text.LineOf(text.Caret)}");
            IReadOnlyList<Rect> wide = text.BoundsOf(new TextRange(text.Line(1).Start + 4, 1));
            Assert.True(wide is [var cell] && cell.Width > 1.5 * text.BoundsOf(new TextRange(0, 1))[0].Width, $"The wide character is drawn at {string.Join("; ", wide)}");

            changes.Clear();
            int before = text.Length;
            session.Say(string.Concat(Enumerable.Range(0, TerminalScreen.ScrollbackLimit + 100).Select(i => $"line {i}\r\n")));
            int first = text.Read(text.LineAt(0)) is var oldest && oldest.StartsWith("line ", StringComparison.Ordinal) ? int.Parse(oldest[5..], System.Globalization.CultureInfo.InvariantCulture) : -1;
            Assert.True(text.LineCount == TerminalScreen.ScrollbackLimit + session.Screen.Rows && first > 0,
                $"Past the scrollback's limit the terminal reads {text.LineCount} lines, its first \"{text.Read(text.LineAt(0))}\"");
            Assert.True(changes.Count is > 0 and <= 2 && changes.Sum(c => c.Inserted - c.Removed) == text.Length - before,
                $"A burst of output was told as {changes.Count} changes: {string.Join(", ", changes.Take(4))}");
            Assert.True(text.Read(text.Line(text.LineOf(text.Caret) - 1).Start, 20).StartsWith($"line {TerminalScreen.ScrollbackLimit + 99}", StringComparison.Ordinal),
                $"The line above the caret reads \"{text.Read(text.LineAt(text.Line(text.LineOf(text.Caret) - 1).Start))}\"");

            changes.Clear();
            int moves = 0;
            text.CaretMoved += (_, _) => moves++;
            long above = session.Screen.FirstLine + text.LineOf(text.Caret) - 1;
            view.Select(above, 0, above, 3);
            Assert.True(changes.Count == 0 && moves == 1 && text.Selected is [var chosen] && text.Read(chosen) == "line",
                $"A selection made was told as {changes.Count} changes and {moves} moves, reading {string.Join(", ", text.Selected.Select(text.Read))}");

            int scrollbackStart = text.Line(0).Start;
            text.Select(new TextRange(scrollbackStart, 4));
            Ui.Settle();
            Assert.True(text.Selected is [var back] && back == new TextRange(scrollbackStart, 4) && text.Shown.Contains(scrollbackStart) && view.ScrolledBack > 0,
                $"Choosing the scrollback's first word chose {string.Join(", ", text.Selected)} and scrolled back {view.ScrolledBack} lines to show {text.Shown}");
            text.Select(new TextRange(text.Caret, 0));
            Assert.True(text.Selected.Count == 0 && view.SelectedText.Length == 0, $"An empty range left \"{view.SelectedText}\" selected");
        }
        finally
        {
            window.Close();
        }
    }
}
