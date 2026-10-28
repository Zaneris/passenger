using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The editor as quick on long text as on short: a line of a million characters (minified code, a program's
/// output with no breaks) is typed in and moved through at the cost of a short one, as only what is on screen of it is
/// laid out; carets stop at a thousand, and say so; and the sideways scroll is as wide as what is there.</summary>
public sealed class LongTextTests : IDisposable
{
    private readonly string folder = TestFolders.Create("long-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Typing_and_moving_on_a_line_of_a_million_characters_costs_what_it_does_on_a_short_line()
    {
        StringBuilder text = new("short line above\n[");
        for (int i = 0; text.Length < 1_000_000; i++)
        {
            text.Append("\"word").Append(i).Append("\", ");
        }
        text.Append("]\nshort line below, a little longer than the one above\n");
        string source = text.ToString();
        EditorRig rig = new(source);
        rig.Editor.Highlighter = new Numbers();
        int onShort = source.IndexOf("little", StringComparison.Ordinal);
        int onLong = source.Length / 2;

        (long Formatted, long Coloured) shortCost = default;
        (long Formatted, long Coloured) longCost = default;
        for (int round = 0; round < 3; round++)
        {
            shortCost = Keys(rig, onShort);
            longCost = Keys(rig, onLong + (round * 64));
        }

        // The whole line would be a million characters a key; what is on screen of it is a few hundred.
        Assert.True(longCost.Formatted <= 4096, $"A key on the long line formatted {longCost.Formatted} characters of it (a short line's key: {shortCost.Formatted}).");
        Assert.True(longCost.Coloured <= 4096, $"A key on the long line had {longCost.Coloured} characters coloured (a short line's key: {shortCost.Coloured}).");
    }

    /// <summary>A key in the middle of a line of a million characters of words, tabs, CJK, accented letters and emoji,
    /// whose columns only their graphemes' widths place, reads and allocates what a key in such a line of ten thousand
    /// does: the line's measure is carried through the edit, read again only around it.</summary>
    [AvaloniaFact]
    public void A_key_in_a_line_of_a_million_characters_measures_and_allocates_what_one_in_ten_thousand_does()
    {
        KeysInTheMiddle small = new(10_000, softWrap: false);
        KeysInTheMiddle large = new(1_000_000, softWrap: false);
        // Taken in turns, so what the process takes on meanwhile (fonts it meets, code compiled again) weighs on both alike.
        for (int round = 0; round < 4; round++)
        {
            small.Round();
            large.Round();
        }
        (long smallBytes, long smallRead) = small.Close();
        (long largeBytes, long largeRead) = large.Close();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A key read {smallRead:N0} characters to measure in a line of 10,000 and {largeRead:N0} in one of a million, allocating {smallBytes:N0} and {largeBytes:N0} bytes on the UI thread");
        Assert.True(largeRead <= smallRead + 256,
            $"A key in a line of a million characters read {largeRead:N0} of them to measure the line, against {smallRead:N0} in a line of 10,000: the line was measured again whole");
        Assert.True(largeBytes <= smallBytes + 2048,
            $"A key in a line of a million characters allocated {largeBytes:N0} bytes on the UI thread, against {smallBytes:N0} in a line of 10,000: the line's measure was made again");
    }

    /// <summary>With soft wrap on, a key in the middle of a line of a million characters of words, tabs, CJK, accented
    /// letters and emoji reads and allocates what a key in such a line of ten thousand does: the line's rows are carried
    /// through the edit, wrapped again only from the row before it to the first row after it that starts where it did.</summary>
    [AvaloniaFact]
    public void A_key_in_a_soft_wrapped_line_of_a_million_characters_wraps_and_allocates_what_one_in_ten_thousand_does()
    {
        KeysInTheMiddle small = new(10_000, softWrap: true);
        KeysInTheMiddle large = new(1_000_000, softWrap: true);
        for (int round = 0; round < 4; round++)
        {
            small.Round();
            large.Round();
        }
        (long smallBytes, long smallRead) = small.Close();
        (long largeBytes, long largeRead) = large.Close();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"With soft wrap, a key read {smallRead:N0} characters to wrap and measure in a line of 10,000 and {largeRead:N0} in one of a million, allocating {smallBytes:N0} and {largeBytes:N0} bytes on the UI thread");
        Assert.True(largeRead <= smallRead + 256,
            $"With soft wrap, a key in a line of a million characters read {largeRead:N0} of them to wrap the line, against {smallRead:N0} in a line of 10,000: the line was wrapped again whole");
        Assert.True(largeBytes <= smallBytes + 2048,
            $"With soft wrap, a key in a line of a million characters allocated {largeBytes:N0} bytes on the UI thread, against {smallBytes:N0} in a line of 10,000: the line's rows were made again");
    }

    /// <summary>Random edits to two long lines (typing, deleting, replacing a selection, several carets, a long insertion,
    /// Undo and Redo) of tabs, CJK, Arabic, emoji with their joiners and modifiers, flags, combining marks, a grapheme
    /// longer than any read whole and lone surrogates, one of them printable ASCII until an edit puts more in: after every
    /// edit each column of both lines is where a fresh editor of the same text, scrolled alike, draws it.</summary>
    [AvaloniaFact]
    public void Edits_to_long_lines_leave_every_column_where_a_fresh_editor_draws_it()
    {
        Random random = new(548);
        string[] pieces =
        [
            "a", "b", "z", "word", " ", "  ", "\t", "中", "文字", "é", "é", "́", "👍", "👍🏽", "👨‍👩‍👧", "‍", "🇨🇦",
            "\U0001F1E8", "ب", "سلام", "\uD83D", "\uDE00", "x" + new string('́', 70), ",", ";", ".",
        ];
        string Pieces(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => pieces[random.Next(pieces.Length)]));
        StringBuilder text = new("first\n");
        while (text.Length < 6000)
        {
            text.Append(Pieces(1));
        }
        // Far enough apart that only one of the long lines is on screen at a time.
        text.Append('\n').Append(string.Concat(Enumerable.Repeat("short\n", 60)));
        int plainLine = 62;
        text.Append(string.Concat(Enumerable.Range(0, 700).Select(i => $"w{i} "))).Append("\nlast\n");
        EditorRig rig = new(text.ToString());
        int[] lines = [1, plainLine];
        for (int step = 0; step < 240; step++)
        {
            TextSnapshot snapshot = rig.Document.Snapshot;
            LineSpan line = snapshot.GetLine(lines[random.Next(lines.Length)]);
            int At() => line.Start + random.Next(line.Length + 1);
            int kind = random.Next(20);
            string did;
            if (line.Length < 4000)
            {
                rig.PlaceCaret(At());
                rig.Window.KeyTextInput(Pieces(400));
                did = "a long insertion to keep the line long";
            }
            else if (kind < 6)
            {
                rig.PlaceCaret(At());
                string typed = Pieces(random.Next(1, 4));
                rig.Window.KeyTextInput(typed);
                did = $"typed {Escaped(typed)}";
            }
            else if (kind < 10)
            {
                rig.PlaceCaret(At());
                string key = new[] { "BACK_SPACE", "DELETE", "alt BACK_SPACE", "alt DELETE" }[random.Next(4)];
                for (int i = random.Next(1, 4); i > 0; i--)
                {
                    rig.Press(key);
                }
                did = key;
            }
            else if (kind < 13)
            {
                int from = At();
                int to = Math.Min(line.End, from + random.Next(300));
                rig.Editor.Selections = SelectionSet.Single(new Selection(from, to));
                string typed = random.Next(2) == 0 ? string.Empty : Pieces(random.Next(1, 4));
                if (typed.Length == 0)
                {
                    rig.Press("BACK_SPACE");
                }
                else
                {
                    rig.Window.KeyTextInput(typed);
                }
                did = $"replaced {from - line.Start}..{to - line.Start} with {Escaped(typed)}";
            }
            else if (kind < 15)
            {
                List<Selection> carets = [.. Enumerable.Range(0, random.Next(2, 5)).Select(_ => Selection.At(At())).DistinctBy(s => s.Caret)];
                rig.Editor.Selections = SelectionSet.Create(carets, 0);
                string typed = Pieces(1);
                rig.Window.KeyTextInput(typed);
                did = $"typed {Escaped(typed)} at {carets.Count} carets";
            }
            else if (kind < 16)
            {
                rig.PlaceCaret(At());
                rig.Window.KeyTextInput(Pieces(random.Next(100, 200)));
                did = "a long insertion";
            }
            else if (kind < 19)
            {
                rig.Press("meta Z");
                did = "Undo";
            }
            else
            {
                rig.Press("meta shift Z");
                did = "Redo";
            }
            Ui.Settle();
            AssertColumnsMatchAFreshEditor(rig, lines, random, $"step {step}: {did}");
        }
    }

    /// <summary>Random edits to long soft-wrapped lines (typing, deleting, replacing a selection, several carets on a line,
    /// long insertions, Undo and Redo) of words, tabs, CJK, Arabic, emoji with joiners and modifiers, flags, combining
    /// marks, a grapheme of 71 characters, lone surrogates and inlay hints, one line indented and one printable ASCII until
    /// an edit puts more in: after every edit each column of the long lines is drawn on the row, and at the place, where a
    /// fresh editor of the same text and hints draws it, so the rows carried through edits are those wrapping anew makes.</summary>
    [AvaloniaFact]
    public void Edits_to_long_soft_wrapped_lines_leave_every_column_on_the_row_a_fresh_editor_wraps_it_onto()
    {
        Random random = new(578);
        string[] pieces =
        [
            "a", "b", "z", "word", "wrapping", " ", " ", "  ", "\t", "中", "文字", "é", "e\u0301", "́", "👍", "👍🏽", "👨‍👩‍👧", "‍", "🇨🇦",
            "\U0001F1E8", "ب", "سلام", "\uD83D", "\uDE00", "x" + new string('́', 70), ",", ";", ".", "(", "[",
        ];
        string Pieces(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => pieces[random.Next(pieces.Length)]));
        StringBuilder text = new("first\n");
        while (text.Length < 3000)
        {
            text.Append(Pieces(1));
        }
        text.Append("\n        ");
        while (text.Length < 6000)
        {
            text.Append(Pieces(1));
        }
        text.Append('\n').Append(string.Concat(Enumerable.Range(0, 500).Select(i => $"w{i} "))).Append("\nlast\n");
        EditorRig rig = new(text.ToString());
        rig.Window.Width = 640;
        rig.Editor.SoftWrap = true;
        int[] lines = [1, 2, 3];
        List<InlayHint> hints = [];
        foreach (int number in lines[..2])
        {
            LineSpan line = rig.Document.Snapshot.GetLine(number);
            for (int i = 0; i < 12; i++)
            {
                int at = line.Start + random.Next(line.Length + 1);
                if (at == line.End || !char.IsLowSurrogate(rig.Document.Snapshot[at]))
                {
                    hints.Add(new InlayHint(at, i % 3 == 0 ? ": int" : "name:", PadLeft: i % 2 == 0, PadRight: i % 4 == 1, Kind: i % 3 == 0 ? InlayHintKind.Type : InlayHintKind.Parameter));
                }
            }
        }
        rig.Editor.SetInlayHints([.. hints.OrderBy(h => h.Offset)]);
        Ui.Settle();
        for (int step = 0; step < 160; step++)
        {
            TextSnapshot snapshot = rig.Document.Snapshot;
            LineSpan line = snapshot.GetLine(lines[random.Next(lines.Length)]);
            int At() => line.Start + random.Next(line.Length + 1);
            int kind = random.Next(20);
            string did;
            if (line.Length < 2500)
            {
                rig.PlaceCaret(At());
                rig.Window.KeyTextInput(Pieces(300));
                did = "a long insertion to keep the line long";
            }
            else if (kind < 6)
            {
                rig.PlaceCaret(At());
                string typed = Pieces(random.Next(1, 4));
                rig.Window.KeyTextInput(typed);
                did = $"typed {Escaped(typed)}";
            }
            else if (kind < 10)
            {
                rig.PlaceCaret(At());
                string key = new[] { "BACK_SPACE", "DELETE", "alt BACK_SPACE", "alt DELETE" }[random.Next(4)];
                for (int i = random.Next(1, 4); i > 0; i--)
                {
                    rig.Press(key);
                }
                did = key;
            }
            else if (kind < 13)
            {
                int from = At();
                int to = Math.Min(line.End, from + random.Next(300));
                rig.Editor.Selections = SelectionSet.Single(new Selection(from, to));
                string typed = random.Next(2) == 0 ? string.Empty : Pieces(random.Next(1, 4));
                if (typed.Length == 0)
                {
                    rig.Press("BACK_SPACE");
                }
                else
                {
                    rig.Window.KeyTextInput(typed);
                }
                did = $"replaced {from - line.Start}..{to - line.Start} with {Escaped(typed)}";
            }
            else if (kind < 15)
            {
                List<Selection> carets = [.. Enumerable.Range(0, random.Next(2, 5)).Select(_ => Selection.At(At())).DistinctBy(s => s.Caret)];
                rig.Editor.Selections = SelectionSet.Create(carets, 0);
                string typed = Pieces(1);
                rig.Window.KeyTextInput(typed);
                did = $"typed {Escaped(typed)} at {carets.Count} carets";
            }
            else if (kind < 16)
            {
                rig.PlaceCaret(At());
                rig.Window.KeyTextInput(Pieces(random.Next(100, 200)));
                did = "a long insertion";
            }
            else if (kind < 19)
            {
                rig.Press("meta Z");
                did = "Undo";
            }
            else
            {
                rig.Press("meta shift Z");
                did = "Redo";
            }
            Ui.Settle();
            AssertRowsMatchAFreshEditor(rig, lines, $"step {step}: {did}");
        }
    }

    [AvaloniaFact]
    public void A_click_far_along_a_line_of_a_million_characters_puts_the_caret_on_the_character_clicked()
    {
        string line = string.Concat(Enumerable.Range(0, 100_000).Select(i => $"{i % 10}abcdefgh "));
        EditorRig rig = new("first\n" + line + "\nlast");
        int target = 6 + 500_003;
        // The view follows a caret a little past the target to the right, so the target is on screen.
        rig.Editor.Selections = SelectionSet.Single(target + 40);
        Ui.Settle();
        Point at = rig.Editor.TranslatePoint(rig.Editor.PointOf(target) + new Point(2, 4), rig.Window)!.Value;
        rig.Window.MouseDown(at, MouseButton.Left);
        rig.Window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
        Assert.Equal(target, rig.Caret);

        rig.Press("UP");
        Assert.Equal(new TextPosition(0, 5), rig.Editor.CaretPosition);
        rig.Press("DOWN");
        Assert.Equal(target, rig.Caret);
    }

    [AvaloniaFact]
    public void Once_the_longest_line_is_deleted_there_is_nothing_to_scroll_sideways_to()
    {
        EditorRig rig = new("short\n" + new string('a', 5000) + "\nshort");
        rig.PlaceCaret(6 + 5000);
        Ui.Settle();
        Assert.True(rig.Editor.ScrollOffset.X > 1000, "The view should have followed the caret to the long line's end.");

        rig.Editor.Selections = SelectionSet.Single(new Selection(6, 6 + 5001));
        rig.Press("BACK_SPACE");
        rig.Editor.ScrollTo(new Vector(100_000, 0));
        Ui.Settle();
        Assert.True(rig.Editor.ScrollOffset.X < 1, $"With every line short, the view still scrolls sideways to {rig.Editor.ScrollOffset.X:F0}.");
    }

    [AvaloniaFact]
    public void Select_all_occurrences_of_a_word_written_five_thousand_times_makes_a_thousand_carets_and_says_so()
    {
        string path = Path.Combine(folder, "words.txt");
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("cat sat\n", 5000)));
        MainWindow window = Ui.OpenFolder(folder, width: 1000, height: 700);
        window.OpenFile(path);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(4000, 1));
        editor.Focus();

        Ui.Press(window, "alt shift SEMICOLON");
        Assert.Equal(SelectionSet.MaxCount, editor.Selections.Count);
        Assert.Equal("Cannot create more than 1,000 carets", window.Status);
        // The word the caret was on is among them, as the primary.
        Assert.Equal(new TextPosition(4000, 3), editor.CaretPosition);

        Ui.Press(window, "alt shift PERIOD");
        Assert.Equal(SelectionSet.MaxCount, editor.Selections.Count);

        Ui.Type(window, "D", "d");
        Assert.Equal(SelectionSet.MaxCount, editor.Document.Snapshot.ToString().Split('\n').Count(l => l == "d sat"));
        window.Close();
    }

    /// <summary>A key with a thousand carets costs the UI thread what the carets cost, not the file's length: in eight
    /// thousand methods it allocates on the UI thread under 512 bytes a caret more than in a thousand (the text's deeper
    /// tree, about 200), where a copy of the larger file's text would be 4 MB more.</summary>
    [AvaloniaFact]
    public void A_key_with_a_thousand_carets_costs_the_same_in_eight_thousand_methods_as_in_a_thousand()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        long few = CaretKeys(window, "Few.cs", 1000);
        long many = CaretKeys(window, "Many.cs", 8000);
        TestContext.Current.TestOutputHelper?.WriteLine($"A key with 1,000 carets allocated {few:N0} bytes on the UI thread in 1,000 methods, {many:N0} in 8,000");
        Assert.True(many <= few + (512L * SelectionSet.MaxCount),
            $"A key with 1,000 carets allocated {many:N0} bytes on the UI thread in a file of 8,000 methods, against {few:N0} in one of 1,000: it copied what the whole file has");
        window.Close();
    }

    /// <summary>Left, Right and Backspace with a thousand carets, each far along a long line after accented letters (a
    /// letter and its combining mark), read no copy of the text around each caret: Left and Right allocate on the UI
    /// thread under 512 bytes for each caret (its place in the carets' sets), and Backspace under 256 bytes for each caret
    /// more than typing a letter. A step back over a grapheme copied the 1,024 characters before every caret to find it,
    /// and Backspace a block of the line besides: 2.2 KB for each caret, and 2.7 KB more than a letter typed.</summary>
    [AvaloniaFact]
    public void Left_Right_and_Backspace_with_a_thousand_carets_copy_nothing_around_each_caret()
    {
        StringBuilder text = new();
        for (int i = 0; i < SelectionSet.MaxCount; i++)
        {
            text.Append("    int total").Append(i).Append(" = ");
            for (int k = 0; k < 130; k++)
            {
                text.Append("value + ");
            }
            text.Append("cafe\u0301e\u0301e\u0301;\n");
        }
        string path = Path.Combine(folder, "Long.cs");
        File.WriteAllText(path, text.ToString());
        MainWindow window = new() { Width = 1000, Height = 500, Autosaves = false, StartsLanguageServer = false };
        window.Show();
        Assert.Null(window.OpenFile(path));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        TextSnapshot snapshot = editor.Document.Snapshot;
        Assert.True(snapshot.GetLine(0).Length > 1024, $"The lines are {snapshot.GetLine(0).Length} characters long");
        editor.Selections = SelectionSet.Create([.. Enumerable.Range(0, SelectionSet.MaxCount).Select(line => Selection.At(snapshot.GetLine(line).End - 1))], 0);
        editor.Focus();
        Ui.Settle();
        long left = Least(() => Ui.Press(window, "LEFT"));
        Assert.Equal(snapshot.GetLine(0).End - 7, editor.Selections.Primary.Caret);
        long right = Least(() => Ui.Press(window, "RIGHT"));
        Assert.Equal(snapshot.GetLine(0).End - 1, editor.Selections.Primary.Caret);
        long typed = Least(() => Ui.Type(window, "x", "x"));
        for (int i = 0; i < 3; i++)
        {
            Ui.Press(window, "BACK_SPACE");
        }
        long back = Least(() => Ui.Press(window, "BACK_SPACE"));
        TestContext.Current.TestOutputHelper?.WriteLine($"With {SelectionSet.MaxCount:N0} carets, Left allocated {left:N0} bytes on the UI thread, Right {right:N0}, a letter typed {typed:N0} and Backspace {back:N0}");
        Assert.Equal(SelectionSet.MaxCount, editor.Selections.Count);
        Assert.EndsWith("value + caf;", editor.Document.Snapshot.GetLineText(0), StringComparison.Ordinal);
        Assert.True(left < 512L * SelectionSet.MaxCount, $"Left with {SelectionSet.MaxCount:N0} carets allocated {left:N0} bytes on the UI thread, {left / SelectionSet.MaxCount:N0} for each caret: the text before each was copied");
        Assert.True(right < 512L * SelectionSet.MaxCount, $"Right with {SelectionSet.MaxCount:N0} carets allocated {right:N0} bytes on the UI thread, {right / SelectionSet.MaxCount:N0} for each caret: the text after each was copied");
        Assert.True(back - typed < 256L * SelectionSet.MaxCount,
            $"Backspace with {SelectionSet.MaxCount:N0} carets allocated {back:N0} bytes on the UI thread, against {typed:N0} for a letter typed: {(back - typed) / SelectionSet.MaxCount:N0} for each caret more, the text before each copied");
        window.Close();

        // The least a key and its frame allocate on the UI thread, of three.
        static long Least(Action key)
        {
            long least = long.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                key();
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return least;
        }
    }

    /// <summary>The least a key allocates on the UI thread, with its frame, with a caret in each of a file's first thousand
    /// methods.</summary>
    private long CaretKeys(MainWindow window, string name, int methods)
    {
        StringBuilder text = new("namespace Wide;\n\npublic sealed class Wide\n{\n");
        for (int i = 0; i < methods; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"    /// <summary>Method {i}.</summary>\n    public int Method{i}(int count)\n    {{\n        int label = count;\n        return label + {i};\n    }}\n\n");
        }
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, text.Append("}\n").ToString());
        Assert.Null(window.OpenFile(path));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        string written = editor.Document.Snapshot.ToString();
        List<Selection> carets = [];
        for (int at = written.IndexOf("int label", StringComparison.Ordinal); at >= 0 && carets.Count < SelectionSet.MaxCount; at = written.IndexOf("int label", at + 1, StringComparison.Ordinal))
        {
            carets.Add(Selection.At(at + "int label".Length));
        }
        editor.Selections = SelectionSet.Create(carets, 0);
        editor.Focus();
        Ui.Settle();
        long least = long.MaxValue;
        for (int i = 0; i < 12; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.KeyTextInput(i % 2 == 0 ? "x" : "y");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            Ui.Settle();
        }
        Assert.Equal(SelectionSet.MaxCount, editor.Document.Snapshot.ToString().Split('\n').Count(l => l.StartsWith("        int labelxyxyxyxyxyxy = count;", StringComparison.Ordinal)));
        return least;
    }

    /// <summary>Twelve keys at an offset; what they had formatted and coloured, a key.</summary>
    private static (long Formatted, long Coloured) Keys(EditorRig rig, int offset)
    {
        rig.PlaceCaret(offset);
        Ui.Settle();
        string[] keys = ["x", "y", "LEFT", "LEFT", "RIGHT", "alt LEFT", "alt RIGHT", "shift RIGHT", "shift LEFT", "RIGHT", "BACK_SPACE", "BACK_SPACE"];
        long formatted = rig.Editor.CharactersFormatted;
        long coloured = rig.Editor.CharactersColoured;
        foreach (string key in keys)
        {
            if (key.Length == 1)
            {
                rig.Type(key);
            }
            else
            {
                rig.Press(key);
            }
        }
        return ((rig.Editor.CharactersFormatted - formatted) / keys.Length, (rig.Editor.CharactersColoured - coloured) / keys.Length);
    }

    /// <summary>What a key costs in the middle of a line of words, tabs, CJK, accented letters and emoji: characters read to
    /// measure or wrap it and bytes allocated on the UI thread, typing a letter, typing a CJK character and Backspace each,
    /// the least of the rounds, summed. Unwrapped, the line repeats 32 characters, and the caret stands as far into a step
    /// of its columns in either line, so what is on screen, and the window of the line laid out for it, are the same.
    /// Wrapped, it is words at random, whose rows an edit moves for a few rows only (in 32 characters repeated, a wider
    /// word can move every row after it for good), ending in the same words in either line, the caret among them: the
    /// rows fall in with each other long before it, so they are the same rows.</summary>
    private sealed class KeysInTheMiddle
    {
        private const string Unit = "word\t中文 café 👍🏽, xyz plain ok ";
        private const int Tail = 8192;

        private readonly string text;
        private readonly EditorRig rig;
        private readonly Action[] keys;
        private readonly long[] bytes;
        private readonly long[] read;

        public KeysInTheMiddle(int length, bool softWrap)
        {
            Assert.Equal(32, Unit.Length);
            StringBuilder line = new("above\n");
            int middle;
            if (softWrap)
            {
                line.Append(Words(579, length - Tail));
                middle = line.Length + (Tail / 2) + 200;
                line.Append(Words(578, Tail));
            }
            else
            {
                line.Insert(line.Length, Unit, length / Unit.Length);
                middle = "above\n".Length + (length / 2 / 256 * 256) + 200;
            }
            text = line.Append("\nbelow\n").ToString();
            rig = new EditorRig(text);
            rig.Editor.Highlighter = new Numbers();
            rig.Editor.SoftWrap = softWrap;
            rig.PlaceCaret(middle);
            Ui.Settle();
            keys = [() => rig.Type("x"), () => rig.Type("中"), () => rig.Press("BACK_SPACE"), () => rig.Press("BACK_SPACE")];
            bytes = [.. keys.Select(_ => long.MaxValue)];
            read = [.. keys.Select(_ => long.MaxValue)];
        }

        /// <summary>Each key once, in this line's window, which is made the one the keys go to.</summary>
        public void Round()
        {
            rig.Window.Activate();
            rig.Editor.Focus();
            Ui.Settle();
            int[] grown = [1, 2, 1, 0];
            for (int k = 0; k < keys.Length; k++)
            {
                // Arrays of runs Avalonia splits go back to its pool only when finalized: a key starts with them back.
                GC.Collect(1, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                long measured = rig.Editor.CharactersMeasured + rig.Editor.CharactersWrapped;
                long before = GC.GetAllocatedBytesForCurrentThread();
                keys[k]();
                bytes[k] = Math.Min(bytes[k], GC.GetAllocatedBytesForCurrentThread() - before);
                read[k] = Math.Min(read[k], rig.Editor.CharactersMeasured + rig.Editor.CharactersWrapped - measured);
                Assert.True(rig.Document.Snapshot.Length == text.Length + grown[k], "A key in the middle of a long line went to another window's editor");
            }
        }

        public (long Bytes, long Read) Close()
        {
            Assert.Equal(text, rig.Text);
            rig.Window.Close();
            return (bytes.Sum(), read.Sum());
        }

        /// <summary>Words, tabs, CJK, accented letters, emoji and digits, as many characters as asked, chosen at random from a seed.</summary>
        private static string Words(int seed, int length)
        {
            Random random = new(seed);
            string[] words = ["word", "xyz", "plain", "ok", "中文", "文字", "café", "👍🏽", "data", "12", "wrapping", "a"];
            string[] gaps = [" ", " ", " ", ", ", "\t", "; "];
            StringBuilder made = new(length);
            while (made.Length < length - 16)
            {
                made.Append(words[random.Next(words.Length)]).Append(gaps[random.Next(gaps.Length)]);
            }
            return made.Append('z', length - made.Length - 1).Append(' ').ToString();
        }
    }

    /// <summary>Every column of the long lines around the caret, their ends and others at random are drawn where a fresh
    /// editor of the same text, with the same carets and scrolled alike, draws them.</summary>
    private static void AssertColumnsMatchAFreshEditor(EditorRig rig, int[] lines, Random random, string where)
    {
        EditorRig fresh = new(rig.Text);
        try
        {
            fresh.Editor.Selections = rig.Editor.Selections;
            fresh.Editor.ScrollTo(rig.Editor.ScrollOffset);
            Ui.Settle();
            // A hop far away and back lays a long line's kept window out again for what is shown, as the fresh editor's is.
            Vector shown = fresh.Editor.ScrollOffset;
            rig.Editor.ScrollTo(new Vector(shown.X > 20_000 ? 0 : 1e9, shown.Y));
            Ui.Settle();
            rig.Editor.ScrollTo(shown);
            Ui.Settle();
            Assert.Equal(shown, rig.Editor.ScrollOffset);
            TextSnapshot text = rig.Document.Snapshot;
            int caret = rig.Caret;
            foreach (int number in lines)
            {
                LineSpan line = text.GetLine(number);
                List<int> offsets = [line.Start, line.End, .. Enumerable.Range(0, 40).Select(_ => line.Start + random.Next(line.Length + 1))];
                for (int offset = Math.Max(line.Start, caret - 300); offset <= Math.Min(line.End, caret + 300); offset++)
                {
                    offsets.Add(offset);
                }
                foreach (int offset in offsets)
                {
                    Point mine = rig.Editor.PointOf(offset) + rig.Editor.ScrollOffset;
                    Point theirs = fresh.Editor.PointOf(offset) + fresh.Editor.ScrollOffset;
                    Assert.True(mine == theirs, $"Column {offset - line.Start} of line {number + 1} is drawn at {mine.X}, where a fresh editor draws it at {theirs.X} ({where}).");
                }
            }
        }
        finally
        {
            fresh.Window.Close();
            rig.Editor.Focus();
            Ui.Settle();
        }
    }

    /// <summary>Every column of the long lines is drawn on the row, and at the place, where a fresh editor of the same text
    /// and hints, as wide and wrapping, draws it; the carets set aside first, as one standing where a row wraps leans.</summary>
    private static void AssertRowsMatchAFreshEditor(EditorRig rig, int[] lines, string where)
    {
        rig.PlaceCaret(0);
        EditorRig fresh = new(rig.Text);
        try
        {
            fresh.Window.Width = rig.Window.Width;
            fresh.Editor.SoftWrap = true;
            fresh.Editor.SetInlayHints(rig.Editor.InlayHints);
            Ui.Settle();
            Assert.True(fresh.Editor.RowsShown == rig.Editor.RowsShown,
                $"The edited editor shows {rig.Editor.RowsShown} rows, a fresh one {fresh.Editor.RowsShown} for the same text ({where}).");
            TextSnapshot text = rig.Document.Snapshot;
            foreach (int number in lines)
            {
                LineSpan line = text.GetLine(number);
                for (int offset = line.Start; offset <= line.End; offset++)
                {
                    Point mine = rig.Editor.PointOf(offset) + rig.Editor.ScrollOffset;
                    Point theirs = fresh.Editor.PointOf(offset) + fresh.Editor.ScrollOffset;
                    Assert.True(mine == theirs, $"Column {offset - line.Start} of line {number + 1} is drawn at {mine}, where a fresh editor draws it at {theirs} ({where}).");
                }
            }
        }
        finally
        {
            fresh.Window.Close();
            rig.Editor.Focus();
            Ui.Settle();
        }
    }

    private static string Escaped(string text) => string.Concat(text.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}"));

    /// <summary>Colours each run of digits in what it is asked for, reading only that.</summary>
    private sealed class Numbers : IHighlighter
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<SyntaxSpan> GetSpans(TextSnapshot text, int start, int length)
        {
            string read = text.GetText(start, length);
            List<SyntaxSpan> spans = [];
            for (int i = 0; i < read.Length; i++)
            {
                if (char.IsAsciiDigit(read[i]))
                {
                    int from = i;
                    while (i + 1 < read.Length && char.IsAsciiDigit(read[i + 1]))
                    {
                        i++;
                    }
                    spans.Add(new SyntaxSpan(start + from, i - from + 1, TokenKind.Number));
                }
            }
            return spans;
        }
    }
}
