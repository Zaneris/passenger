using System.Diagnostics;
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
[Collection(Alone.Name)]
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

        List<double> shortTimes = [];
        List<double> longTimes = [];
        (long Formatted, long Coloured) shortCost = default;
        (long Formatted, long Coloured) longCost = default;
        // Taken in turns, so a machine that slows down meanwhile slows both alike.
        for (int round = 0; round < 3; round++)
        {
            shortCost = Keys(rig, onShort, shortTimes);
            longCost = Keys(rig, onLong + (round * 64), longTimes);
        }

        // The whole line would be a million characters a key; what is on screen of it is a few hundred.
        Assert.True(longCost.Formatted <= 4096, $"A key on the long line formatted {longCost.Formatted} characters of it (a short line's key: {shortCost.Formatted}).");
        Assert.True(longCost.Coloured <= 4096, $"A key on the long line had {longCost.Coloured} characters coloured (a short line's key: {shortCost.Coloured}).");
        double shortKey = Median(shortTimes);
        double longKey = Median(longTimes);
        Assert.True(longKey <= 10 * shortKey, $"A key on the long line took {longKey:F2} ms, against {shortKey:F2} ms on a short line.");
    }

    /// <summary>A key in the middle of a line of a million characters of words, tabs, CJK, accented letters and emoji,
    /// whose columns only their graphemes' widths place, reads and allocates what a key in such a line of ten thousand
    /// does: the line's measure is carried through the edit, read again only around it.</summary>
    [AvaloniaFact]
    public void A_key_in_a_line_of_a_million_characters_measures_and_allocates_what_one_in_ten_thousand_does()
    {
        (long smallBytes, long smallRead) = KeysInTheMiddle(10_000);
        (long largeBytes, long largeRead) = KeysInTheMiddle(1_000_000);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A key read {smallRead:N0} characters to measure in a line of 10,000 and {largeRead:N0} in one of a million, allocating {smallBytes:N0} and {largeBytes:N0} bytes on the UI thread");
        Assert.True(largeRead <= smallRead + 256,
            $"A key in a line of a million characters read {largeRead:N0} of them to measure the line, against {smallRead:N0} in a line of 10,000: the line was measured again whole");
        Assert.True(largeBytes <= smallBytes + 2048,
            $"A key in a line of a million characters allocated {largeBytes:N0} bytes on the UI thread, against {smallBytes:N0} in a line of 10,000: the line's measure was made again");
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

    /// <summary>A key with a thousand carets costs the UI thread what the carets cost, not the file's length: eight
    /// thousand methods type as quickly as a thousand.</summary>
    [AvaloniaFact]
    public void A_key_with_a_thousand_carets_costs_the_same_in_eight_thousand_methods_as_in_a_thousand()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        double few = CaretKeys(window, "Few.cs", 1000);
        double many = CaretKeys(window, "Many.cs", 8000);
        TestContext.Current.TestOutputHelper?.WriteLine($"A key with 1,000 carets: {few:F1} ms in 1,000 methods, {many:F1} ms in 8,000");
        Assert.True(many <= (2 * few) + 5, $"A key with 1,000 carets took the UI thread {many:F1} ms in a file of 8,000 methods, against {few:F1} ms in one of 1,000");
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

    /// <summary>The median time a key takes the UI thread, to its frame, with a caret in each of a file's first thousand
    /// methods.</summary>
    private double CaretKeys(MainWindow window, string name, int methods)
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
        List<double> times = [];
        for (int i = 0; i < 12; i++)
        {
            long began = Stopwatch.GetTimestamp();
            window.KeyTextInput(i % 2 == 0 ? "x" : "y");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            times.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
            Ui.Settle();
        }
        Assert.Equal(SelectionSet.MaxCount, editor.Document.Snapshot.ToString().Split('\n').Count(l => l.StartsWith("        int labelxyxyxyxyxyxy = count;", StringComparison.Ordinal)));
        return Median(times);
    }

    /// <summary>Twelve keys at an offset, each timed; what they had formatted and coloured, a key.</summary>
    private static (long Formatted, long Coloured) Keys(EditorRig rig, int offset, List<double> times)
    {
        rig.PlaceCaret(offset);
        Ui.Settle();
        string[] keys = ["x", "y", "LEFT", "LEFT", "RIGHT", "alt LEFT", "alt RIGHT", "shift RIGHT", "shift LEFT", "RIGHT", "BACK_SPACE", "BACK_SPACE"];
        long formatted = rig.Editor.CharactersFormatted;
        long coloured = rig.Editor.CharactersColoured;
        foreach (string key in keys)
        {
            long at = Stopwatch.GetTimestamp();
            if (key.Length == 1)
            {
                rig.Type(key);
            }
            else
            {
                rig.Press(key);
            }
            times.Add(Stopwatch.GetElapsedTime(at).TotalMilliseconds);
        }
        return ((rig.Editor.CharactersFormatted - formatted) / keys.Length, (rig.Editor.CharactersColoured - coloured) / keys.Length);
    }

    /// <summary>What a key costs in the middle of a line of words, tabs, CJK, accented letters and emoji: characters read to
    /// measure it and bytes allocated on the UI thread, typing a letter, typing a CJK character and Backspace each, the
    /// least of four rounds, summed. The line repeats 32 characters, and the caret stands as far into a step of its columns
    /// in either line, so what is on screen, and the window of the line laid out for it, are the same.</summary>
    private static (long Bytes, long Read) KeysInTheMiddle(int length)
    {
        const string Unit = "word\t中文 café 👍🏽, xyz plain ok ";
        Assert.Equal(32, Unit.Length);
        StringBuilder text = new("above\n");
        text.Insert(text.Length, Unit, length / Unit.Length);
        int middle = "above\n".Length + (length / 2 / 256 * 256) + 200;
        EditorRig rig = new(text.Append("\nbelow\n").ToString());
        rig.Editor.Highlighter = new Numbers();
        rig.PlaceCaret(middle);
        Ui.Settle();
        Action[] keys = [() => rig.Type("x"), () => rig.Type("中"), () => rig.Press("BACK_SPACE"), () => rig.Press("BACK_SPACE")];
        long[] bytes = [.. keys.Select(_ => long.MaxValue)];
        long[] read = [.. keys.Select(_ => long.MaxValue)];
        for (int round = 0; round < 4; round++)
        {
            for (int k = 0; k < keys.Length; k++)
            {
                long measured = rig.Editor.CharactersMeasured;
                long before = GC.GetAllocatedBytesForCurrentThread();
                keys[k]();
                bytes[k] = Math.Min(bytes[k], GC.GetAllocatedBytesForCurrentThread() - before);
                read[k] = Math.Min(read[k], rig.Editor.CharactersMeasured - measured);
            }
        }
        Assert.Equal(text.ToString(), rig.Text);
        rig.Window.Close();
        return (bytes.Sum(), read.Sum());
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

    private static string Escaped(string text) => string.Concat(text.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}"));

    private static double Median(List<double> times)
    {
        List<double> sorted = [.. times.Order()];
        return sorted[sorted.Count / 2];
    }

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
