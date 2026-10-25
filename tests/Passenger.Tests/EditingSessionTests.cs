using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A long session of random keys in a large indented file, checked after every key against a plain model of what
/// they do: a string, a caret and a remembered column, knowing Enter's indentation, Backspace's levels, Home's stop at the
/// text and Join Lines (no brackets or quotes are typed). Now and then carets are added for one key, each checked. With
/// soft wrap on in a narrow editor the model, knowing no rows, checks each move by what a move among rows must do, and the
/// rows are a fresh editor's. Any difference is the editor, the rope, the line index or the wraps getting it wrong.</summary>
public sealed class EditingSessionTests
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789.,;:+-*/=!?";

    /// <summary>What <see cref="AnyKey"/> types: the model's letters and the brackets and quotes the editor's typing help
    /// closes, steps over and wraps a selection in.</summary>
    private const string TypedAlphabet = Alphabet + "(){}[]\"'";

    /// <summary>The narrow editor's width, about thirty-five characters of text, which wraps the longer of the session's
    /// lines and more of them as Join Lines makes them longer.</summary>
    private const double WrappedWidth = 380;

    /// <summary>The long sessions' window height, about fifteen lines, which Up and Down scroll all the more. Each key draws
    /// a frame into a new bitmap that the runtime counts as memory pressure, inducing a full collection every few frames:
    /// at the rig's 700 a session took more than twice as long, drawing and collecting.</summary>
    private const double SessionHeight = 300;

    /// <summary>The keys of Rider's macOS keymap <see cref="AnyKey"/> presses, Undo and Redo among them twice as often.</summary>
    private static readonly (string Keys, string Name)[] EditingKeys =
    [
        ("ENTER", "Enter"), ("shift ENTER", "Start New Line"), ("meta ENTER", "Start New Line Before"), ("BACK_SPACE", "Backspace"),
        ("DELETE", "Delete"), ("alt BACK_SPACE", "Delete Word Left"), ("alt DELETE", "Delete Word Right"), ("LEFT", "Left"), ("RIGHT", "Right"),
        ("UP", "Up"), ("DOWN", "Down"), ("shift LEFT", "Shift+Left"), ("shift RIGHT", "Shift+Right"), ("shift UP", "Shift+Up"),
        ("shift DOWN", "Shift+Down"), ("alt LEFT", "Word Left"), ("alt RIGHT", "Word Right"), ("alt shift LEFT", "Select Word Left"),
        ("alt shift RIGHT", "Select Word Right"), ("meta LEFT", "Line Start"), ("meta RIGHT", "Line End"), ("meta shift LEFT", "Select to Line Start"),
        ("meta shift RIGHT", "Select to Line End"), ("TAB", "Tab"), ("shift TAB", "Shift+Tab"), ("control D", "Duplicate"), ("meta shift L", "Delete Line"),
        ("alt UP", "Move Line Up"), ("alt DOWN", "Move Line Down"), ("control shift J", "Join Lines"), ("control shift U", "Toggle Case"),
        ("control K", "Cut to Line End"), ("meta Z", "Undo"), ("meta Z", "Undo"), ("meta shift Z", "Redo"), ("meta X", "Cut"), ("meta C", "Copy"),
        ("meta V", "Paste"), ("meta V", "Paste"), ("alt shift DOWN", "Clone Caret Below"), ("alt shift UP", "Clone Caret Above"), ("ESCAPE", "Escape"),
        ("alt shift PERIOD", "Select Next Occurrence"), ("meta SLASH", "Line Comment"), ("control meta SLASH", "Block Comment"), ("PAGE_DOWN", "Page Down"),
        ("PAGE_UP", "Page Up"), ("control M , M", "Toggle Fold"), ("control M , S", "Fold"), ("control M , E", "Unfold"), ("control M , A", "Fold All"),
        ("control M , X", "Unfold All"),
    ];

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_long_session_of_keys_leaves_the_text_and_caret_where_a_plain_model_says(int seed) => Session(seed, wrapped: false);

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_long_session_of_keys_with_soft_wrap_on_edits_as_the_model_says_moves_by_rows_and_wraps_as_a_fresh_editor_would(int seed) =>
        Session(seed, wrapped: true);

    /// <summary>The keys the model leaves out, at several carets, in folds and wrapped rows, in LF and CRLF files: selections,
    /// word and text moves, Tab, line and comment commands, case, cut, copy, paste (of either break), folding, Undo, Redo.
    /// Whatever a key does: no key fails, no caret leaves the text, every break stays the file's, the line index reads its
    /// lines, an undo gives back the text before its step and a redo the text after it, the rows are a fresh editor's, the
    /// folds in runs are those carried one by one, and the file undoes to itself and redoes to the last text.</summary>
    [AvaloniaTheory]
    [InlineData(3, "\n")]
    [InlineData(8, "\n")]
    [InlineData(3, "\r\n")]
    [InlineData(8, "\r\n")]
    public void A_session_of_every_editing_key_at_several_carets_and_in_folds_never_fails_a_key_and_undoes_to_the_file_as_opened(int seed, string ending)
    {
        Random random = new(seed);
        string initial = StartingText(random, lines: 400).Replace("\n", ending, StringComparison.Ordinal);
        EditorRig rig = new(initial);
        // Carried through each edit as the open file carries them, the editor told of them, and kept as the editor folds them.
        CarriedFolds folds = new();
        rig.Document.Changed += (_, change) =>
        {
            if (folds.Regions.Count > 0)
            {
                folds.Through(change.Edits);
                rig.Editor.SetFoldRegions(folds.Regions);
            }
        };
        rig.Editor.FoldsToggled += (_, regions) => folds.Set(regions);
        rig.Window.Width = WrappedWidth;
        rig.Editor.SoftWrap = true;
        Ui.Settle();
        // The text before each undo step began and after it was last undone, by the step, as the document keeps it.
        Dictionary<object, string> beforeStep = new(ReferenceEqualityComparer.Instance);
        Dictionary<object, string> afterStep = new(ReferenceEqualityComparer.Instance);
        string said = ending == "\n" ? "LF" : "CRLF";
        for (int step = 0; step < 1500; step++)
        {
            string was = rig.Text;
            object? undone = rig.Document.NextUndo;
            object? redone = rig.Document.NextRedo;
            string key = AnyKey(random, rig, folds, ending);
            string where = $"seed {seed}, {said}, step {step}, after {key}";
            folds.Check(where);
            string text = rig.Text;
            Assert.All(rig.Editor.Selections.Items, s => Assert.True(Math.Max(s.Anchor, s.Caret) <= text.Length, $"A caret was left past the text's end ({where})"));
            int? stray = StrayBreak(text, ending);
            Assert.True(stray is null, $"A break other than the file's {said} is at {stray} ({where})");
            if (key == "Undo")
            {
                Assert.True(undone is null ? text == was : text == beforeStep[undone], $"Undo did not give back the text before the step it undid ({where})");
                if (undone is not null)
                {
                    afterStep[undone] = was;
                }
            }
            else if (key == "Redo")
            {
                Assert.True(redone is null ? text == was : text == afterStep[redone], $"Redo did not give back the text the step it redid had made ({where})");
            }
            else if (rig.Document.NextUndo is { } begun && !ReferenceEquals(begun, undone))
            {
                beforeStep[begun] = was;
            }
            if (step % 50 == 0)
            {
                AssertLinesMatch(rig.Document.Snapshot, text, random, where, ending);
                AssertRowsMatchAFreshEditor(rig, random, where);
            }
        }

        while (rig.Document.CanRedo)
        {
            rig.Editor.TryExecute(EditorActions.Redo);
        }
        string final = rig.Text;
        while (rig.Document.CanUndo)
        {
            rig.Editor.TryExecute(EditorActions.Undo);
        }
        Assert.True(initial == rig.Text, $"Undoing everything did not give back the file as opened (seed {seed}).");
        while (rig.Document.CanRedo)
        {
            rig.Editor.TryExecute(EditorActions.Redo);
        }
        Assert.True(final == rig.Text, $"Redoing everything did not give back the session's last text (seed {seed}).");
    }

    /// <summary>One key, a click, a caret added, a paste of text with breaks of either kind, or folds set; what it was. No
    /// click lands between a CRLF's halves, where a person's cannot.</summary>
    private static string AnyKey(Random random, EditorRig rig, CarriedFolds folds, string ending)
    {
        string text = rig.Text;
        int roll = random.Next(100);
        if (roll < 35)
        {
            char typed = random.Next(5) == 0 ? ' ' : TypedAlphabet[random.Next(TypedAlphabet.Length)];
            rig.Type(typed.ToString());
            return $"typing '{typed}'";
        }
        if (roll < 40)
        {
            int offset = Placed(text, random.Next(text.Length + 1));
            rig.PlaceCaret(offset);
            return $"a click at {offset}";
        }
        if (roll < 43)
        {
            int offset = Placed(text, random.Next(text.Length + 1));
            if (!rig.Editor.Selections.Items.Any(s => s.Start <= offset && offset <= s.End))
            {
                rig.AddCaret(offset);
            }
            return $"a caret added at {offset}";
        }
        if (roll < 48)
        {
            string[] pieces = ["a", "bc", "\r\n", "\n", "    ", "x y", "\t"];
            string pasted = string.Concat(Enumerable.Range(0, random.Next(1, 6)).Select(_ => pieces[random.Next(pieces.Length)]));
            rig.Window.Clipboard!.SetTextAsync(pasted).GetAwaiter().GetResult();
            rig.Press("meta V");
            Ui.Settle();
            string said = pasted.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
            Assert.True(StrayBreak(rig.Text, ending) is null, $"Pasting '{said}' left a break other than the file's in it");
            return $"a paste of '{said}'";
        }
        if (roll < 50)
        {
            folds.Set(Folds(random, rig.Document.Snapshot));
            rig.Editor.SetFoldRegions(folds.Regions);
            return "folds set";
        }
        (string keys, string name) = EditingKeys[random.Next(EditingKeys.Length)];
        rig.Press(keys);
        Ui.Settle();
        return name;
    }

    /// <summary>An offset moved off the middle of a CRLF, to its end.</summary>
    private static int Placed(string text, int offset) =>
        offset > 0 && offset < text.Length && text[offset - 1] == '\r' && text[offset] == '\n' ? offset + 1 : offset;

    /// <summary>Where the first break other than the file's own is in a text (a lone CR or LF in a CRLF file, a CR in an LF
    /// one), or null.</summary>
    private static int? StrayBreak(string text, string ending)
    {
        for (int i = 0; i < text.Length; i++)
        {
            bool stray = ending == "\n"
                ? text[i] == '\r'
                : text[i] == '\r' ? i + 1 >= text.Length || text[i + 1] != '\n' : text[i] == '\n' && (i == 0 || text[i - 1] != '\r');
            if (stray)
            {
                return i;
            }
        }
        return null;
    }

    /// <summary>Regions of a few lines here and there, some inside others, about half of them folded.</summary>
    private static List<FoldRegion> Folds(Random random, TextSnapshot text)
    {
        List<FoldRegion> made = [];
        int line = 0;
        while (line < text.LineCount - 3)
        {
            line += random.Next(1, 30);
            int last = line + random.Next(1, 8);
            if (last >= text.LineCount)
            {
                break;
            }
            made.Add(new FoldRegion(text.GetLine(line).End, text.GetLine(last).End, "...", Folded: random.Next(2) == 0));
            if (random.Next(3) == 0 && last - line > 2)
            {
                made.Add(new FoldRegion(text.GetLine(line + 1).End, text.GetLine(last - 1).End, "...", Folded: random.Next(2) == 0));
            }
            line = last + 1;
        }
        return [.. made.OrderBy(r => r.Start)];
    }

    /// <summary>Fold regions carried through each edit as the open file carries them, in a <see cref="FoldList"/>'s runs, and
    /// region by region beside them, the plain model the runs have to agree with.</summary>
    private sealed class CarriedFolds
    {
        private List<FoldRegion> plain = [];

        public IReadOnlyList<FoldRegion> Regions { get; private set; } = [];

        public void Set(IReadOnlyList<FoldRegion> regions)
        {
            Regions = regions;
            plain = [.. regions];
        }

        public void Through(IReadOnlyList<TextEdit> edits)
        {
            Regions = FoldList.Of(Regions).Through(edits);
            plain = [.. plain.Select(r => r with
            {
                Start = TextEditing.MapOffset(r.Start, edits, afterInsertions: true),
                End = TextEditing.MapOffset(r.End, edits, afterInsertions: true),
            })];
        }

        public void Check(string where) =>
            Assert.True(Regions.SequenceEqual(plain), $"The folds carried in runs are not those carried one by one ({where}).");
    }

    private static void Session(int seed, bool wrapped)
    {
        Random random = new(seed);
        string initial = StartingText(random, lines: 2500);
        EditorRig rig = new(initial);
        rig.Window.Height = SessionHeight;
        Ui.Settle();
        if (wrapped)
        {
            rig.Window.Width = WrappedWidth;
            rig.Editor.SoftWrap = true;
            Ui.Settle();
            Assert.True(rig.Editor.RowsShown > rig.Document.Snapshot.LineCount, $"No line of the session wraps in an editor {WrappedWidth} wide (seed {seed}).");
        }
        Model model = new(initial);

        for (int step = 0; step < 2500; step++)
        {
            string key = Act(random, rig, model, $"seed {seed}, step {step}", wrapped);
            string where = $"seed {seed}, step {step}, after {key}";
            AssertTextIs(rig, model, where);
            Assert.True(model.Caret == rig.Caret, $"The caret is at {rig.Caret}, the model says {model.Caret} ({where}).");
            if (step % 100 == 0)
            {
                // The whole text as well, as the rope's pieces put together give it.
                Assert.True(model.Text == rig.Text, $"The text put together differs from the model ({where}).");
                AssertLinesMatch(rig.Document.Snapshot, model.Text, random, where);
                if (wrapped)
                {
                    AssertRowsMatchAFreshEditor(rig, random, where);
                }
            }
        }

        string final = rig.Text;
        while (rig.Document.CanUndo)
        {
            rig.Editor.TryExecute(EditorActions.Undo);
        }
        Assert.True(initial == rig.Text, $"Undoing everything did not give back the file as opened (seed {seed}).");
        while (rig.Document.CanRedo)
        {
            rig.Editor.TryExecute(EditorActions.Redo);
        }
        Assert.True(final == rig.Text, $"Redoing everything did not give back the session's last text (seed {seed}).");
    }

    private static string Act(Random random, EditorRig rig, Model model, string step, bool wrapped)
    {
        int roll = random.Next(100);
        if (wrapped && roll is >= 62 and < 90)
        {
            return MoveAmongRows(rig, model, roll, step);
        }
        switch (roll)
        {
            case < 40:
                char typed = random.Next(5) == 0 ? ' ' : Alphabet[random.Next(Alphabet.Length)];
                rig.Type(typed.ToString());
                model.Type([model.Caret], typed);
                return $"typing '{typed}'";
            case < 47:
                rig.Press("ENTER");
                model.Enter([model.Caret]);
                return "Enter";
            case < 57:
                rig.Press("BACK_SPACE");
                model.Backspace();
                return "Backspace";
            case < 62:
                rig.Press("DELETE");
                model.Delete();
                return "Delete";
            case < 67:
                rig.Press("LEFT");
                model.Horizontal(-1);
                return "Left";
            case < 72:
                rig.Press("RIGHT");
                model.Horizontal(1);
                return "Right";
            case < 78:
                rig.Press("UP");
                model.Vertical(-1);
                return "Up";
            case < 84:
                rig.Press("DOWN");
                model.Vertical(1);
                return "Down";
            case < 87:
                rig.Press("meta LEFT");
                model.LineStart();
                return "Cmd+Left";
            case < 90:
                rig.Press("meta RIGHT");
                model.LineEnd();
                return "Cmd+Right";
            case < 92:
                rig.Press("control shift J");
                model.JoinLines([model.Caret]);
                return "Join Lines";
            case < 96:
                return SeveralCarets(random, rig, model, step);
            default:
                int offset = random.Next(model.Text.Length + 1);
                rig.PlaceCaret(offset);
                model.Jump(offset);
                return $"a click at {offset}";
        }
    }

    /// <summary>A move in wrapped text, where the plain model knows no rows: it goes where a move among rows must (Left and
    /// Right no more than a step along the text, onto the row beside at most; Up and Down to the row above or below; Home
    /// and End along the caret's line, to the row before or after at most), and the model takes the caret from there.</summary>
    private static string MoveAmongRows(EditorRig rig, Model model, int roll, string step)
    {
        (string key, string name) = roll switch
        {
            < 67 => ("LEFT", "Left"),
            < 72 => ("RIGHT", "Right"),
            < 78 => ("UP", "Up"),
            < 84 => ("DOWN", "Down"),
            < 87 => ("meta LEFT", "Cmd+Left"),
            _ => ("meta RIGHT", "Cmd+Right"),
        };
        TextSnapshot text = rig.Document.Snapshot;
        int before = rig.Caret;
        int line = text.GetLineFromOffset(before);
        double from = CaretRow(rig);
        int rows = rig.Editor.RowsShown;
        rig.Press(key);
        int after = rig.Caret;
        double to = CaretRow(rig);
        string where = $"{step}, after {name} from {before} to {after}, row {from} to {to}";
        switch (name)
        {
            // A step that stays at its offset crosses a wrap there, to the end of the row before or the start of the next.
            case "Left":
                Assert.True(
                    (after == before - 1 && to - from is -1 or 0) || (after == before && (before == 0 ? to == from : to == from - 1)),
                    $"Left went neither a character back nor across a wrap ({where}).");
                break;
            case "Right":
                Assert.True(
                    (after == before + 1 && to - from is 0 or 1) || (after == before && (before == text.Length ? to == from : to == from + 1)),
                    $"Right went neither a character on nor across a wrap ({where}).");
                break;
            case "Up":
                Assert.True(from == 0 ? after == 0 : to == from - 1, $"Up did not go to the row above ({where}).");
                break;
            case "Down":
                Assert.True(from == rows - 1 ? after == text.Length : to == from + 1, $"Down did not go to the row below ({where}).");
                break;
            default:
                Assert.True(text.GetLineFromOffset(after) == line, $"{name} left the caret's line ({where}).");
                Assert.True(name == "Cmd+Left" ? to - from is -1 or 0 : to - from is 0 or 1, $"{name} went further than the row beside ({where}).");
                break;
        }
        model.Jump(after);
        return name;
    }

    /// <summary>The row the caret is drawn on.</summary>
    private static double CaretRow(EditorRig rig) =>
        Math.Round((rig.Editor.PointOf(rig.Caret).Y + rig.Editor.ScrollOffset.Y) / rig.Editor.LineHeight);

    /// <summary>The rows a session's edits left are those a fresh editor makes of the same text at the same width: every
    /// wrap carried through every edit as if worked out anew.</summary>
    private static void AssertRowsMatchAFreshEditor(EditorRig rig, Random random, string where)
    {
        EditorRig fresh = new(rig.Text);
        try
        {
            fresh.Window.Width = WrappedWidth;
            fresh.Editor.SoftWrap = true;
            fresh.Editor.SetFoldRegions(rig.Editor.FoldRegions);
            Ui.Settle();
            Assert.True(fresh.Editor.RowsShown == rig.Editor.RowsShown,
                $"The session's editor shows {rig.Editor.RowsShown} rows, a fresh one {fresh.Editor.RowsShown} for the same text ({where}).");
            for (int i = 0; i < 20; i++)
            {
                int offset = random.Next(rig.Document.Snapshot.Length + 1);
                if (offset == rig.Caret)
                {
                    continue;
                }
                Point mine = rig.Editor.PointOf(offset) + rig.Editor.ScrollOffset;
                Point theirs = fresh.Editor.PointOf(offset) + fresh.Editor.ScrollOffset;
                Assert.True(mine == theirs, $"Offset {offset} is drawn at {mine}, where a fresh editor draws it at {theirs} ({where}).");
            }
        }
        finally
        {
            fresh.Window.Close();
            // The fresh editor took the keys; the session's next keys are its editor's.
            rig.Editor.Focus();
            Ui.Settle();
        }
    }

    /// <summary>Carets added on the caret's line and the lines beside it, often in a run of spaces, then one key pressed at
    /// them all; every caret has to land where the model says before Escape leaves the last one added.</summary>
    private static string SeveralCarets(Random random, EditorRig rig, Model model, string step)
    {
        List<int> carets = [model.Caret];
        for (int i = random.Next(1, 4); i > 0; i--)
        {
            int offset = model.Nearby(random);
            if (!carets.Contains(offset))
            {
                rig.AddCaret(offset);
                carets.Add(offset);
            }
        }
        string key;
        int[] landed;
        switch (random.Next(3))
        {
            case 0:
                rig.Press("ENTER");
                (key, landed) = ("Enter", model.Enter(carets));
                break;
            case 1:
                rig.Press("control shift J");
                (key, landed) = ("Join Lines", model.JoinLines(carets));
                break;
            default:
                char typed = Alphabet[random.Next(Alphabet.Length)];
                rig.Type(typed.ToString());
                (key, landed) = ($"typing '{typed}'", model.Type(carets, typed));
                break;
        }
        key += $" at carets {string.Join(", ", carets)}";
        AssertTextIs(rig, model, $"{step}, after {key}");
        string expected = string.Join(", ", landed.Distinct().Order());
        string actual = string.Join(", ", rig.Editor.Selections.Items.Select(s => s.IsEmpty ? $"{s.Caret}" : $"{s.Anchor}-{s.Caret}"));
        Assert.True(expected == actual, $"The carets are at {actual}, the model says {expected} ({step}, after {key}).");
        rig.Press("ESCAPE");
        return key + ", then Escape";
    }

    /// <summary>Lines of words at no, four or eight spaces' indentation with runs of spaces between them, and some lines
    /// empty or blank but for their indentation, as C# leaves them.</summary>
    private static string StartingText(Random random, int lines)
    {
        StringBuilder text = new();
        for (int line = 0; line < lines; line++)
        {
            int indent = 4 * random.Next(3);
            int kind = random.Next(10);
            if (kind == 1)
            {
                text.Append(' ', Math.Max(4, indent));
            }
            else if (kind > 1)
            {
                text.Append(' ', indent);
                for (int word = random.Next(1, 6); word > 0; word--)
                {
                    for (int i = random.Next(1, 8); i > 0; i--)
                    {
                        text.Append(Alphabet[random.Next(Alphabet.Length)]);
                    }
                    if (word > 1)
                    {
                        text.Append(' ', random.Next(1, 4));
                    }
                }
            }
            if (line < lines - 1)
            {
                text.Append('\n');
            }
        }
        return text.ToString();
    }

    /// <summary>The editor's text is the model's, every character.</summary>
    private static void AssertTextIs(EditorRig rig, Model model, string where)
    {
        int at = rig.DifferenceFrom(model.Text);
        Assert.True(at < 0, $"The text differs from the model from offset {at} ({where}).");
    }

    private static void AssertLinesMatch(TextSnapshot snapshot, string text, Random random, string where, string ending = "\n")
    {
        string[] lines = text.Split(ending);
        Assert.True(lines.Length == snapshot.LineCount, $"The editor counts {snapshot.LineCount} lines, the text has {lines.Length} ({where}).");
        for (int i = 0; i < 10; i++)
        {
            int line = random.Next(lines.Length);
            Assert.True(lines[line] == snapshot.GetLineText(line), $"Line {line} reads differently through the line index ({where}).");
        }
    }

    /// <summary>What the keys do, written the simplest way: a string, a caret, and the column a run of Up and Down presses
    /// keeps returning to. A command at several carets takes them with the primary, the last added, last.</summary>
    private sealed class Model(string text)
    {
        private int? column;

        public string Text { get; private set; } = text;
        public int Caret { get; private set; }

        public int[] Type(IReadOnlyList<int> carets, char c) => AtEachCaret(carets, (at, _) => at, _ => c.ToString());

        /// <summary>Enter at every caret: a line break and the line's whole indentation (from inside it too, so the text
        /// moved down keeps it), the spaces after the caret going, up to the next caret.</summary>
        public int[] Enter(IReadOnlyList<int> carets) => AtEachCaret(
            carets,
            (at, next) =>
            {
                int end = at;
                while (end < next && Text[end] == ' ')
                {
                    end++;
                }
                return end;
            },
            at => "\n" + new string(' ', IndentAt(LineStartOf(at))));

        /// <summary>Join Lines at every caret: its line and the next become one. In a run of joined lines the blank ones
        /// go and the others meet with one space, the first keeping its indentation and the last its trailing spaces; a
        /// caret lands where the next line with text begins on the joined line, or at its end.</summary>
        public int[] JoinLines(IReadOnlyList<int> carets)
        {
            string[] lines = Text.Split('\n');
            (int Line, int Column)[] at = [.. carets.Select(PositionOf)];
            HashSet<int> joins = [.. at.Select(p => p.Line).Where(l => l < lines.Length - 1)];
            if (joins.Count == 0)
            {
                // Nothing changes, so a lone caret keeps the column it remembered.
                Caret = carets[^1];
                column = carets.Count > 1 ? null : column;
                return [.. carets];
            }
            List<string> result = [];
            int[] lineAfter = new int[lines.Length];
            Func<int, int>[] columnAfter = new Func<int, int>[lines.Length];
            for (int first = 0; first < lines.Length;)
            {
                int last = first;
                while (joins.Contains(last))
                {
                    last++;
                }
                for (int line = first; line <= last; line++)
                {
                    lineAfter[line] = result.Count;
                }
                if (last == first)
                {
                    result.Add(lines[first]);
                    columnAfter[first] = c => c;
                }
                else
                {
                    result.Add(Join(lines, first, last, columnAfter));
                }
                first = last + 1;
            }
            Text = string.Join('\n', result);
            int[] starts = new int[result.Count];
            for (int line = 1; line < result.Count; line++)
            {
                starts[line] = starts[line - 1] + result[line - 1].Length + 1;
            }
            return Land([.. at.Select(p => starts[lineAfter[p.Line]] + columnAfter[p.Line](p.Column))]);
        }

        public void Backspace()
        {
            if (Caret > 0)
            {
                int lineStart = LineStartOf(Caret);
                int lead = Caret - lineStart;
                // In the indentation, the spaces back to the level before go.
                int from = lead > 0 && IndentAt(lineStart) >= lead ? lineStart + (lead - 1) / 4 * 4 : Caret - 1;
                Text = Text.Remove(from, Caret - from);
                Caret = from;
            }
            column = null;
        }

        public void Delete()
        {
            if (Caret < Text.Length)
            {
                Text = Text.Remove(Caret, 1);
            }
            column = null;
        }

        public void Horizontal(int step)
        {
            Caret = Math.Clamp(Caret + step, 0, Text.Length);
            column = null;
        }

        public void Vertical(int step)
        {
            (int line, int col) = PositionOf(Caret);
            column ??= col;
            int target = line + step;
            string[] lines = Text.Split('\n');
            if (target < 0)
            {
                Caret = 0;
            }
            else if (target >= lines.Length)
            {
                Caret = Text.Length;
            }
            else
            {
                Caret = LineStart(lines, target) + Math.Min(column.Value, lines[target].Length);
            }
        }

        /// <summary>Home: to the line's text, or to its start from there or on a line of only spaces.</summary>
        public void LineStart()
        {
            int start = LineStartOf(Caret);
            int words = start + IndentAt(start);
            Caret = Caret == words || words == LineEndOf(start) ? start : words;
            column = null;
        }

        public void LineEnd()
        {
            Caret = LineEndOf(Caret);
            column = null;
        }

        public void Jump(int offset)
        {
            Caret = offset;
            column = null;
        }

        /// <summary>A random place on the caret's line or the line above or below it, half the time moved on to the
        /// next space there, where the spaces two carets take at Enter meet.</summary>
        public int Nearby(Random random)
        {
            int start = LineStartOf(Caret);
            int roll = random.Next(10);
            if (roll >= 8 && start > 0)
            {
                start = LineStartOf(start - 1);
            }
            else if (roll is >= 5 and < 8 && LineEndOf(start) < Text.Length)
            {
                start = LineEndOf(start) + 1;
            }
            int offset = start + random.Next(LineEndOf(start) - start + 1);
            int space = Text.IndexOf(' ', offset, LineEndOf(start) - offset);
            return space >= 0 && random.Next(2) == 0 ? space + random.Next(2) : offset;
        }

        /// <summary>Lines first to last made one; <paramref name="columnAfter"/> learns where a caret on each lands.</summary>
        private static string Join(string[] lines, int first, int last, Func<int, int>[] columnAfter)
        {
            StringBuilder joined = new();
            int[] textAt = new int[last - first + 1];
            bool started = false;
            bool lastFollows = false;
            for (int line = first; line <= last; line++)
            {
                string words = lines[line].Trim(' ');
                textAt[line - first] = -1;
                if (words.Length == 0)
                {
                    continue;
                }
                lastFollows = started;
                joined.Append(' ', started ? 1 : Indent(lines[line]));
                textAt[line - first] = joined.Length;
                joined.Append(words);
                started = true;
            }
            int end = joined.Length;
            int lastAt = textAt[^1];
            if (lastAt >= 0)
            {
                joined.Append(' ', lines[last].Length - lines[last].TrimEnd(' ').Length);
            }
            int next = end;
            for (int line = last - 1; line >= first; line--)
            {
                next = textAt[line + 1 - first] >= 0 ? textAt[line + 1 - first] : next;
                int landing = next;
                columnAfter[line] = _ => landing;
            }
            // A caret on the last line keeps its place in the text; in indentation that became a space it lands after it.
            int lastIndent = Indent(lines[last]);
            columnAfter[last] = lastAt < 0 ? _ => end : c => c < lastIndent && lastFollows ? lastAt : lastAt + c - lastIndent;
            return joined.ToString();
        }

        private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

        /// <summary>Each caret, from the first in the text on, replaces what lies from it to <paramref name="end"/> (given
        /// the next caret) with <paramref name="inserted"/>, landing after what it inserted.</summary>
        private int[] AtEachCaret(IReadOnlyList<int> carets, Func<int, int, int> end, Func<int, string> inserted)
        {
            int[] sorted = [.. carets.Order()];
            Dictionary<int, int> landed = [];
            StringBuilder result = new();
            int copied = 0;
            for (int i = 0; i < sorted.Length; i++)
            {
                result.Append(Text, copied, sorted[i] - copied).Append(inserted(sorted[i]));
                landed[sorted[i]] = result.Length;
                copied = end(sorted[i], i + 1 < sorted.Length ? sorted[i + 1] : Text.Length);
            }
            result.Append(Text, copied, Text.Length - copied);
            Text = result.ToString();
            return Land([.. carets.Select(c => landed[c])]);
        }

        private int[] Land(int[] landed)
        {
            Caret = landed[^1];
            column = null;
            return landed;
        }

        private int LineStartOf(int offset) => offset == 0 ? 0 : Text.LastIndexOf('\n', offset - 1) + 1;

        private int LineEndOf(int offset)
        {
            int at = Text.IndexOf('\n', offset);
            return at < 0 ? Text.Length : at;
        }

        private int IndentAt(int lineStart)
        {
            int end = lineStart;
            while (end < Text.Length && Text[end] == ' ')
            {
                end++;
            }
            return end - lineStart;
        }

        private (int Line, int Column) PositionOf(int offset)
        {
            int line = 0;
            int start = 0;
            for (int i = 0; i < offset; i++)
            {
                if (Text[i] == '\n')
                {
                    line++;
                    start = i + 1;
                }
            }
            return (line, offset - start);
        }

        private static int LineStart(string[] lines, int line)
        {
            int offset = 0;
            for (int i = 0; i < line; i++)
            {
                offset += lines[i].Length + 1;
            }
            return offset;
        }
    }
}
