using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Visual mode, by characters, lines or a block: each caret a stretch from where it began to its cursor, shown as
/// the editor's own selections (a block as one per line), moved by normal mode's motions and acted on by its operators.</summary>
public sealed partial class Vim
{
    /// <summary>A visual stretch: where it began and where its cursor stands, both on a character, the cursor on its
    /// line's end after $ or an l past the last character.</summary>
    private readonly record struct Region(int Anchor, int Cursor)
    {
        public int Low => Math.Min(Anchor, Cursor);

        public int High => Math.Max(Anchor, Cursor);
    }

    /// <summary>How much a visual change took, for . to take as much again from the caret: the lines below its first,
    /// and the characters it took on one line or the column it ended at on its last; a block's width in columns.</summary>
    private readonly record struct VisualExtent(VimMode Mode, int Lines, int Columns, bool ToEnd);

    /// <summary>A change for . to make again: its keys after the count, the count, what its insert typed, and for a change
    /// made in visual mode how much it took.</summary>
    private sealed record ChangeMade(IReadOnlyList<string> Keys, int Count, string Inserted, VisualExtent? Visual = null);

    private static readonly HashSet<string> VisualOperators = ["d", "x", "<Del>", "X", "D", "c", "s", "C", "S", "R", "y", "Y", ">", "<", "=", "~", "u", "U", "J", "p", "P", "I", "A"];

    /// <summary>The stretches of visual mode, one per caret; a block's only one, its corners.</summary>
    private List<Region> regions = [];

    private int visualPrimary;

    /// <summary>The selections visual mode last gave the editor: while they stand, <see cref="regions"/> are what they show.</summary>
    private SelectionSet? shownVisual;

    /// <summary>The display column each region's cursor aims for going up and down, -1 for its own, int.MaxValue for
    /// its line's end.</summary>
    private int[] visualAims = [];

    /// <summary>A block's columns, from its anchor's and its cursor's (the cursor's kept on a short line), and whether it
    /// reaches every line's end ($).</summary>
    private (int Anchor, int Cursor, bool ToEnd) blockColumns;

    /// <summary>The last visual stretch, for gv: its mode, whether its cursor stood first, and a block's columns; its
    /// ends are the marks &lt; and &gt;.</summary>
    private (VimMode Mode, bool CursorFirst, (int Anchor, int Cursor, bool ToEnd) Block)? lastVisual;

    /// <summary>Where a block's insert session goes back to as it ends, its one cursor at the block's top left; null for
    /// any other session.</summary>
    private int? blockCorner;

    /// <summary>The visual extent of the change under way, kept with it for . to take again.</summary>
    private VisualExtent? changeExtent;

    public static bool IsVisual(VimMode mode) => mode is VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock;

    /// <summary>Where visual mode's cursors stand while the editor shows its selections, for their block carets; none
    /// otherwise.</summary>
    public IReadOnlyList<int> VisualCursors(SelectionSet selections) =>
        IsVisual(Mode) && shownVisual is { } shown && (ReferenceEquals(shown, selections) || shown.SameAs(selections)) ? [.. regions.Select(r => r.Cursor)] : [];

    /// <summary>The editor's selections changed by something other than Vim's keys (a drag, the keymap's Select All, a
    /// click): a selection in normal mode begins visual mode, and visual mode with every selection gone ends; true where
    /// the mode changed.</summary>
    public bool FollowSelections(TextSnapshot text, SelectionSet selections, IVimView? view = null)
    {
        breakEnds = (view ?? PlainView).KeepsFinalLineBreak;
        if (Mode == VimMode.Normal && selections.Items.Any(s => !s.IsEmpty))
        {
            pending.Clear();
            Mode = VimMode.Visual;
            Derive(text, selections);
            shownVisual = selections;
            return true;
        }
        if (!IsVisual(Mode) || (shownVisual is not null && shownVisual.SameAs(selections)))
        {
            return false;
        }
        pending.Clear();
        if (selections.Items.All(s => s.IsEmpty))
        {
            RememberVisual(text, view ?? PlainView);
            Mode = VimMode.Normal;
            shownVisual = null;
            return true;
        }
        // Selections moved from outside (a drag going on) are the stretches now, character by character.
        VimMode was = Mode;
        Mode = VimMode.Visual;
        Derive(text, selections);
        shownVisual = selections;
        return Mode != was;
    }

    /// <summary>Regions read back from selections not Vim's own: each its character stretch, the last character the
    /// one before its end.</summary>
    private void Derive(TextSnapshot text, SelectionSet selections)
    {
        regions = [.. selections.Items.Select(s => s.IsEmpty ? new Region(s.Caret, s.Caret)
            : s.Caret > s.Anchor ? new Region(s.Anchor, VimMotions.StepBack(text, s.Caret)) : new Region(VimMotions.StepBack(text, s.Anchor), s.Caret))];
        visualPrimary = selections.PrimaryIndex;
        visualAims = [.. regions.Select(_ => -1)];
        if (Mode == VimMode.VisualBlock)
        {
            Mode = VimMode.Visual;
        }
    }

    private VimResult EnterVisual(VimMode mode, int count, TextSnapshot text, SelectionSet carets, IVimView view)
    {
        Mode = mode;
        IReadOnlyList<Selection> from = mode == VimMode.VisualBlock ? [carets.Primary] : carets.Items;
        regions = [.. from.Select(s => new Region(s.Caret, s.Caret))];
        visualPrimary = mode == VimMode.VisualBlock ? 0 : carets.PrimaryIndex;
        visualAims = [.. regions.Select(_ => -1)];
        int tab = view.Indent.TabWidth;
        if (mode == VimMode.VisualBlock)
        {
            int column = VimMotions.ColumnOf(text, regions[0].Cursor, tab);
            blockColumns = (column, column, false);
        }
        if (count > 1)
        {
            // A count takes that many characters or lines from the caret.
            for (int i = 0; i < regions.Count; i++)
            {
                int cursor = regions[i].Cursor;
                regions[i] = regions[i] with
                {
                    Cursor = mode == VimMode.Visual
                        ? VimMotions.Right(text, cursor, count - 1, crossing: false, operating: true, breakEnds)?.Offset ?? cursor
                        : VimMotions.LinesAway(text, cursor, count - 1, breakEnds) is { } line ? VimMotions.OffsetAtColumn(text, line, VimMotions.ColumnOf(text, cursor, tab), tab) : cursor,
                };
            }
            if (mode == VimMode.VisualBlock)
            {
                blockColumns = blockColumns with { Cursor = VimMotions.ColumnOf(text, regions[0].Cursor, tab) };
            }
        }
        return ShowVisual(text, view);
    }

    /// <summary>The regions as the editor's selections: a character stretch with its last character, whole lines, or a
    /// block's part of each line it crosses.</summary>
    private VimResult ShowVisual(TextSnapshot text, IVimView view)
    {
        List<Selection> shown = [];
        int primary = visualPrimary;
        if (Mode == VimMode.VisualBlock)
        {
            Region r = regions[0];
            int top = text.GetLineFromOffset(r.Low);
            int bottom = text.GetLineFromOffset(r.High);
            int cursorLine = text.GetLineFromOffset(r.Cursor);
            (int left, int right) = BlockSpan(text, r, view);
            bool forward = blockColumns.Cursor >= blockColumns.Anchor;
            for (int line = top; line <= bottom; line++)
            {
                if (BlockPiece(text, line, left, right, view.Indent.TabWidth) is not { } piece)
                {
                    if (line == cursorLine)
                    {
                        primary = shown.Count;
                        shown.Add(Selection.At(text.GetLine(line).End));
                    }
                    continue;
                }
                if (line == cursorLine)
                {
                    primary = shown.Count;
                }
                shown.Add(forward ? new Selection(piece.Start, piece.End) : new Selection(piece.End, piece.Start));
            }
        }
        else
        {
            foreach (Region r in regions)
            {
                if (Mode == VimMode.VisualLine)
                {
                    int start = VimMotions.LineOf(text, r.Low).Start;
                    LineSpan last = VimMotions.LineOf(text, r.High);
                    int end = last.BreakLength > 0 ? last.EndIncludingBreak : last.End;
                    shown.Add(r.Cursor >= r.Anchor ? new Selection(start, end) : new Selection(end, start));
                    continue;
                }
                int after = CharEnd(text, r.High);
                shown.Add(r.Cursor >= r.Anchor ? new Selection(r.Low, after) : new Selection(after, r.Low));
            }
        }
        shownVisual = SelectionSet.Create(shown, Math.Clamp(primary, 0, shown.Count - 1));
        return new VimResult(true, shownVisual);
    }

    /// <summary>Just past a character visual mode takes: the next one, or past the line break for a cursor on its line's end.</summary>
    private static int CharEnd(TextSnapshot text, int offset) => offset >= text.Length ? text.Length : TextNavigation.NextGrapheme(text, offset);

    /// <summary>A block's first and last display column, the last int.MaxValue where it reaches each line's end.</summary>
    private (int Left, int Right) BlockSpan(TextSnapshot text, Region r, IVimView view)
    {
        int tab = view.Indent.TabWidth;
        (int anchor, int cursor, bool toEnd) = blockColumns;
        int anchorEnd = anchor + Math.Max(1, CellsOf(text, r.Anchor, tab)) - 1;
        int cursorEnd = cursor + Math.Max(1, CellsOf(text, VimMotions.OffsetAtColumn(text, text.GetLineFromOffset(r.Cursor), cursor, tab), tab)) - 1;
        return (Math.Min(anchor, cursor), toEnd ? int.MaxValue : Math.Max(anchorEnd, cursorEnd));
    }

    /// <summary>The cells the character at an offset takes, 1 at a line's end.</summary>
    private static int CellsOf(TextSnapshot text, int offset, int tab)
    {
        LineSpan line = VimMotions.LineOf(text, offset);
        return offset >= line.End ? 1 : VimMotions.ColumnOf(text, TextNavigation.NextGrapheme(text, offset), tab) - VimMotions.ColumnOf(text, offset, tab);
    }

    /// <summary>The characters of a line a block's columns take, any cell of theirs inside it; null where the line ends
    /// before the block begins.</summary>
    private static (int Start, int End)? BlockPiece(TextSnapshot text, int lineNumber, int left, int right, int tab)
    {
        LineSpan line = text.GetLine(lineNumber);
        int column = 0;
        int start = -1;
        int end = line.End;
        for (int p = line.Start; p < line.End; p = TextNavigation.NextGrapheme(text, p))
        {
            int next = VimMotions.ColumnOf(text, TextNavigation.NextGrapheme(text, p), tab);
            if (start < 0 && next > left)
            {
                start = p;
            }
            if (column > right)
            {
                end = p;
                break;
            }
            column = next;
        }
        return start < 0 ? null : (start, end);
    }

    private VimResult PressVisual(string key, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        if (shownVisual is null || !shownVisual.SameAs(selections))
        {
            Derive(text, selections);
        }
        if (key == Escape)
        {
            pending.Clear();
            return LeaveVisual(text, view);
        }
        if (!IsVimKey(key))
        {
            pending.Clear();
            return VimResult.Left;
        }
        if (key is "/" or "?" && TakesCommandLine(pending, search: false))
        {
            return OpenCommandLine(key[0], string.Empty);
        }
        if (key == ":")
        {
            // The command line takes the stretch's lines, visual mode ending as Vim's does.
            pending.Clear();
            VimResult left = LeaveVisual(text, view);
            commandLine = (':', new System.Text.StringBuilder("'<,'>"), [], 0);
            return left;
        }
        pending.Add(key);
        if (ReadPrefix(pending, out int count, out char? register, out int at) is not true)
        {
            bool waiting = at < 0;
            if (!waiting)
            {
                pending.Clear();
            }
            return VimResult.Held;
        }
        string first = pending[at];
        int n = Math.Max(count, 1);
        List<string> body = [.. pending.Skip(at)];
        if (register is { } named)
        {
            body.InsertRange(0, ["\"", named.ToString()]);
        }
        Command command = new(body, count, register, null, first, null);
        if (first is "g" or "r" or "i" or "a" or "f" or "F" or "t" or "T" && at + 1 >= pending.Count)
        {
            return VimResult.Held;
        }
        string second = at + 1 < pending.Count ? pending[at + 1] : string.Empty;
        pending.Clear();
        switch (first)
        {
            case "v" or "V" or "<C-v>":
                VimMode to = first switch { "v" => VimMode.Visual, "V" => VimMode.VisualLine, _ => VimMode.VisualBlock };
                return to == Mode ? LeaveVisual(text, view) : SwitchVisual(to, text, view);
            case "o":
                regions = [.. regions.Select(r => new Region(r.Cursor, r.Anchor))];
                blockColumns = (blockColumns.Cursor, blockColumns.Anchor, blockColumns.ToEnd);
                visualAims = [.. regions.Select(_ => -1)];
                return ShowVisual(text, view);
            case "O" when Mode == VimMode.VisualBlock:
                Region corner = regions[0];
                int tab = view.Indent.TabWidth;
                int anchorAt = VimMotions.OffsetAtColumn(text, text.GetLineFromOffset(corner.Anchor), blockColumns.Cursor, tab);
                int cursorAt = VimMotions.OffsetAtColumn(text, text.GetLineFromOffset(corner.Cursor), blockColumns.Anchor, tab);
                regions = [new Region(anchorAt, cursorAt)];
                blockColumns = (blockColumns.Cursor, blockColumns.Anchor, false);
                return ShowVisual(text, view);
            case "O":
                goto case "o";
            case "i" or "a":
                return VisualObject(text, second, first == "i", n, view);
            case "r":
                if ((second == "<CR>" ? "\n" : VimMotions.Typed(second)) is not { } with)
                {
                    return VimResult.Held;
                }
                return VisualChange(command with { Argument = with }, "r", text, view);
            case "g" when second == "v":
                return SwapLastVisual(text, view);
            case "g" when second is "J" or "~" or "u" or "U" or "q" or "w":
                return VisualChange(command with { Name = "g" + second }, "g" + second, text, view);
            case var op when VisualOperators.Contains(op):
                return VisualChange(command, op, text, view);
        }
        (string? motion, string? argument, int used) = Motion([first, .. second.Length > 0 ? [second] : Array.Empty<string>()], 0, out bool _);
        if (motion is null || used < (second.Length > 0 ? 2 : 1))
        {
            return VimResult.Held;
        }
        searchSaid = null;
        return Told(MoveVisual(command with { Name = motion, Argument = argument }, text, view), motion);
    }

    /// <summary>The count and register before a command's keys: true with <paramref name="at"/> its first key, null while
    /// more may come (<paramref name="at"/> -1), false where they make no command.</summary>
    private static bool? ReadPrefix(List<string> keys, out int count, out char? register, out int at)
    {
        count = 0;
        register = null;
        long current = 0;
        long total = 0;
        at = 0;
        while (at < keys.Count)
        {
            string key = keys[at];
            if (key.Length == 1 && char.IsAsciiDigit(key[0]) && (key[0] != '0' || current > 0))
            {
                current = Math.Min(current * 10 + (key[0] - '0'), int.MaxValue);
                at++;
            }
            else if (key == "\"")
            {
                if (at + 1 >= keys.Count)
                {
                    at = -1;
                    return null;
                }
                if (keys[at + 1].Length != 1 || !VimRegisters.Names(keys[at + 1][0]))
                {
                    return false;
                }
                register = keys[at + 1][0];
                total = Multiply(total, current);
                current = 0;
                at += 2;
            }
            else
            {
                break;
            }
        }
        count = (int)Multiply(total, current);
        if (at >= keys.Count)
        {
            at = -1;
            return null;
        }
        return true;
    }

    private VimResult SwitchVisual(VimMode to, TextSnapshot text, IVimView view)
    {
        int tab = view.Indent.TabWidth;
        if (to == VimMode.VisualBlock)
        {
            Region r = regions[Math.Clamp(visualPrimary, 0, regions.Count - 1)];
            regions = [r];
            visualPrimary = 0;
            visualAims = [-1];
            blockColumns = (VimMotions.ColumnOf(text, r.Anchor, tab), VimMotions.ColumnOf(text, r.Cursor, tab), false);
        }
        else if (Mode == VimMode.VisualBlock)
        {
            Region r = regions[0];
            regions = [new Region(r.Anchor, VimMotions.OffsetAtColumn(text, text.GetLineFromOffset(r.Cursor), blockColumns.Cursor, tab))];
        }
        Mode = to;
        return ShowVisual(text, view);
    }

    /// <summary>Escape, or the visual key of the mode shown: back to normal mode with each caret at its cursor.</summary>
    private VimResult LeaveVisual(TextSnapshot text, IVimView view)
    {
        RememberVisual(text, view);
        int tab = view.Indent.TabWidth;
        List<Selection> carets = Mode == VimMode.VisualBlock
            ? [Selection.At(Settled(text, VimMotions.OffsetAtColumn(text, text.GetLineFromOffset(regions[0].Cursor), blockColumns.Cursor, tab), breakEnds))]
            : [.. regions.Select(r => Selection.At(Settled(text, r.Cursor, breakEnds)))];
        Mode = VimMode.Normal;
        shownVisual = null;
        return new VimResult(true, SelectionSet.Create(carets, Math.Clamp(visualPrimary, 0, carets.Count - 1)));
    }

    /// <summary>The marks &lt; and &gt; at the primary stretch's ends, and how it stood, for gv.</summary>
    private void RememberVisual(TextSnapshot text, IVimView view)
    {
        if (regions.Count == 0)
        {
            return;
        }
        Region r = regions[Math.Clamp(visualPrimary, 0, regions.Count - 1)];
        view.SetMark('<', Math.Min(r.Low, text.Length));
        view.SetMark('>', Math.Min(r.High, text.Length));
        lastVisual = (Mode, r.Cursor < r.Anchor, blockColumns);
    }

    /// <summary>gv in normal mode: the last visual stretch again, between the marks &lt; and &gt;.</summary>
    private VimResult Reselect(TextSnapshot text, IVimView view)
    {
        if (lastVisual is not { } last || view.Mark('<', text) is not { } low || view.Mark('>', text) is not { } high)
        {
            return VimResult.Held;
        }
        Mode = last.Mode;
        regions = [last.CursorFirst ? new Region(high, low) : new Region(low, high)];
        visualPrimary = 0;
        visualAims = [-1];
        blockColumns = last.Block;
        return ShowVisual(text, view);
    }

    /// <summary>gv in visual mode: the last visual stretch shown, and this one kept for the next gv.</summary>
    private VimResult SwapLastVisual(TextSnapshot text, IVimView view)
    {
        (VimMode, bool, (int, int, bool))? before = lastVisual;
        int? low = view.Mark('<', text);
        int? high = view.Mark('>', text);
        RememberVisual(text, view);
        if (before is not { } last || low is null || high is null)
        {
            return ShowVisual(text, view);
        }
        Mode = last.Item1;
        regions = [last.Item2 ? new Region(high.Value, low.Value) : new Region(low.Value, high.Value)];
        visualPrimary = 0;
        visualAims = [-1];
        blockColumns = last.Item3;
        return ShowVisual(text, view);
    }

    /// <summary>A motion in visual mode: each cursor moved, its anchor staying; a cursor that cannot move stays.</summary>
    private VimResult MoveVisual(Command command, TextSnapshot text, IVimView view)
    {
        int n = Math.Max(command.Count, 1);
        int tab = view.Indent.TabWidth;
        if (visualAims.Length != regions.Count)
        {
            visualAims = [.. regions.Select(_ => -1)];
        }
        for (int i = 0; i < regions.Count; i++)
        {
            Region r = regions[i];
            int cursor = r.Cursor;
            int? moved;
            int aim = -1;
            bool block = Mode == VimMode.VisualBlock;
            switch (command.Name)
            {
                case "j" or "k":
                    aim = block ? (blockColumns.ToEnd ? int.MaxValue : visualAims[i] >= 0 ? visualAims[i] : blockColumns.Cursor)
                        : visualAims[i] >= 0 ? visualAims[i] : CaretColumn(text, cursor, tab);
                    moved = VimMotions.LinesAway(text, cursor, command.Name == "j" ? n : -n, breakEnds) is { } line
                        ? aim == int.MaxValue ? LineEndOf(text, line) : VimMotions.OffsetAtColumn(text, line, aim, tab)
                        : null;
                    break;
                case "gj" or "gk":
                    int row = view.RowsAway(text, cursor, command.Name == "gj" ? n : -n);
                    moved = row == cursor ? null : row;
                    break;
                case "$":
                    moved = VimMotions.LineEnd(text, cursor, n, breakEnds) is { } end ? LineEndOf(text, text.GetLineFromOffset(end.Offset)) : null;
                    aim = int.MaxValue;
                    break;
                case "l" or " ":
                    moved = VimMotions.Right(text, cursor, n, crossing: command.Name == " ", operating: true, breakEnds)?.Offset;
                    break;
                default:
                    moved = Target(command, text, cursor, operating: false, view)?.Offset;
                    break;
            }
            if (moved is not { } to)
            {
                continue;
            }
            regions[i] = r with { Cursor = to };
            visualAims[i] = aim;
            if (block)
            {
                // Onto a line too short for the column aimed at, the block's cursor stands just past its end.
                blockColumns = command.Name is "j" or "k" or "gj" or "gk"
                    ? blockColumns with
                    {
                        Cursor = blockColumns.ToEnd ? VimMotions.ColumnOf(text, LastCharOf(text, to), tab)
                            : aim >= 0 && aim != int.MaxValue ? Math.Min(aim, VimMotions.ColumnOf(text, VimMotions.LineOf(text, to).End, tab))
                            : VimMotions.ColumnOf(text, to, tab),
                    }
                    : blockColumns with { Cursor = VimMotions.ColumnOf(text, to, tab), ToEnd = command.Name == "$" };
            }
        }
        return ShowVisual(text, view);
    }

    /// <summary>The last character of the line an offset is on, its start where it is empty.</summary>
    private static int LastCharOf(TextSnapshot text, int offset)
    {
        LineSpan line = VimMotions.LineOf(text, offset);
        return line.Length == 0 ? line.Start : VimMotions.StepBack(text, line.End);
    }

    /// <summary>Where a visual cursor stands at a line's end: on its line break, at the start of an empty line.</summary>
    private int LineEndOf(TextSnapshot text, int line) => text.GetLine(Math.Min(line, VimMotions.LastLine(text, breakEnds))).End;

    /// <summary>A text object in visual mode, at each stretch: at a single place the object there, as an operator takes
    /// it; over a longer stretch words and sentences carry the cursor on (back, where it stands before where the stretch
    /// began), paragraphs by runs of lines, and a block, a tag or a quoted string is the one around the stretch's start
    /// (or the first after it), a block or a tag the one around that where the stretch holds more than a character of
    /// it. A paragraph taken over one line makes the mode linewise, words keep a block's, and anything else is charwise.</summary>
    private VimResult VisualObject(TextSnapshot text, string key, bool inner, int n, IVimView view)
    {
        bool block = key is "(" or ")" or "b" or "{" or "}" or "B" or "[" or "]" or "<" or ">" or "t";
        bool quote = key is "\"" or "'" or "`";
        if (key is not ("w" or "W" or "s" or "p") && !block && !quote)
        {
            return VimResult.Held;
        }
        bool any = false;
        bool lines = false;
        // Words, and sentences over a longer stretch, keep a block's mode and a linewise one's for sentences.
        bool keeps = key is "w" or "W" || (key == "s" && regions.All(r => r.Anchor != r.Cursor));
        List<Region> taken = [];
        foreach (Region r in regions)
        {
            Region? got = key switch
            {
                "p" => ParagraphOver(text, r, inner, n, ref lines),
                "w" or "W" => WordsOver(text, r, inner, n, key == "W"),
                "s" => SentencesOver(text, r, inner, n),
                _ => Around(text, r, key, inner, n, block),
            };
            any |= got is not null;
            taken.Add(got ?? r);
        }
        if (!any)
        {
            return VimResult.Held;
        }
        regions = taken;
        visualAims = [.. regions.Select(_ => -1)];
        int tab = view.Indent.TabWidth;
        if (lines)
        {
            Mode = VimMode.VisualLine;
        }
        else if (keeps && Mode == VimMode.VisualBlock)
        {
            blockColumns = (VimMotions.ColumnOf(text, regions[0].Anchor, tab), VimMotions.ColumnOf(text, regions[0].Cursor, tab), false);
        }
        else if (key != "p" && !(keeps && key == "s"))
        {
            Mode = VimMode.Visual;
        }
        else if (Mode == VimMode.VisualBlock)
        {
            blockColumns = blockColumns with { Cursor = VimMotions.ColumnOf(text, regions[0].Cursor, tab), ToEnd = false };
        }
        return ShowVisual(text, view);
    }

    /// <summary>A stretch from the start of a span to its last character (its own start where it is empty).</summary>
    private static Region Spanned(TextSnapshot text, VimSpan span) => new(span.Start, span.End > span.Start ? VimMotions.StepBack(text, span.End) : span.Start);

    /// <summary>ip and ap in visual mode: over several lines the cursor carried on by runs of lines; over one line the
    /// paragraph around it, linewise (<paramref name="lines"/>), the stretch's start kept where it stands on its first
    /// line, and in linewise mode over a line that is a run of lines alone, held already, one run more after it.</summary>
    private Region? ParagraphOver(TextSnapshot text, Region r, bool inner, int n, ref bool lines)
    {
        int cursorLine = text.GetLineFromOffset(r.Cursor);
        if (text.GetLineFromOffset(r.Anchor) != cursorLine)
        {
            int to = VimTextObjects.ParagraphsOn(text, cursorLine, r.Cursor < r.Anchor, n, inner, breakEnds);
            return r with { Cursor = text.GetLineStart(to) };
        }
        if (VimTextObjects.Of(text, r.Cursor, "p", inner, n, breakEnds) is not { } span)
        {
            return null;
        }
        lines = true;
        int first = text.GetLineFromOffset(span.Start);
        int last = text.GetLineFromOffset(span.End);
        if (Mode == VimMode.VisualLine && VimTextObjects.RunOfOneLine(text, cursorLine, breakEnds))
        {
            last = VimTextObjects.ParagraphsOn(text, last, up: false, 1, inner: true, breakEnds);
        }
        return new Region(text.GetLineFromOffset(r.Anchor) == first ? r.Anchor : span.Start, text.GetLineStart(last));
    }

    /// <summary>iw, aw, iW and aW in visual mode: the words at a single place; over a longer stretch its cursor carried
    /// <paramref name="n"/> runs on (iw), or over white and a word (aw), forward or, where the cursor stands before where
    /// the stretch began, back.</summary>
    private Region? WordsOver(TextSnapshot text, Region r, bool inner, int n, bool bigWord)
    {
        if (r.Anchor == r.Cursor)
        {
            // A count of aw the text cuts short takes as many words as there are, found by halving; one of iw fails.
            int at = Settled(text, r.Cursor, breakEnds);
            string key = bigWord ? "W" : "w";
            if (VimTextObjects.Of(text, at, key, inner, n, breakEnds) is { } whole)
            {
                return Spanned(text, whole);
            }
            if (inner)
            {
                return null;
            }
            int low = 0;
            int high = n;
            VimSpan? most = null;
            while (high - low > 1)
            {
                int mid = low + (high - low) / 2;
                if (VimTextObjects.Of(text, at, key, inner, mid, breakEnds) is { } found)
                {
                    (low, most) = (mid, found);
                }
                else
                {
                    high = mid;
                }
            }
            return most is { } span ? Spanned(text, span) : null;
        }
        bool back = r.Cursor < r.Anchor;
        int cursor = r.Cursor;
        for (int i = 0; i < n; i++)
        {
            int? step = back ? WordBehind(text, cursor, inner, bigWord) : WordAhead(text, cursor, inner, bigWord);
            if (step is not { } to || (to == cursor && Mode == VimMode.VisualLine))
            {
                // A count running out stops there, but fails from the first step, and in linewise mode at all.
                if (i == 0 || Mode == VimMode.VisualLine)
                {
                    return null;
                }
                break;
            }
            cursor = to;
        }
        return r with { Cursor = cursor };
    }

    /// <summary>A character's kind for visual words: 0 a blank, 1 punctuation, 2 a word's; a WORD's every non-blank 2.</summary>
    private static int WordKind(TextSnapshot text, int offset, bool bigWord) => text[offset] is ' ' or '\t' ? 0 : VimMotions.Kind(text[offset], bigWord);

    /// <summary>Where the run of one kind holding the offset ends (its last character) or begins, within its line.</summary>
    private static int RunEdge(TextSnapshot text, int offset, bool bigWord, bool end)
    {
        LineSpan line = VimMotions.LineOf(text, offset);
        int kind = WordKind(text, offset, bigWord);
        int p = offset;
        while (end ? p + 1 < line.End && WordKind(text, p + 1, bigWord) == kind : p > line.Start && WordKind(text, p - 1, bigWord) == kind)
        {
            p += end ? 1 : -1;
        }
        return p;
    }

    /// <summary>The first character past blanks, line breaks and empty lines from the offset on; the text's length where
    /// there is none.</summary>
    private static int PastWhite(TextSnapshot text, int offset)
    {
        int p = offset;
        while (p < text.Length && (text[p] is ' ' or '\t' or '\r' or '\n'))
        {
            p++;
        }
        return p;
    }

    /// <summary>The last character before blanks, line breaks and empty lines back from the offset; -1 where there is none.</summary>
    private static int BeforeWhite(TextSnapshot text, int offset)
    {
        int p = offset;
        while (p >= 0 && (text[p] is ' ' or '\t' or '\r' or '\n'))
        {
            p--;
        }
        return p;
    }

    /// <summary>One step of iw or aw carrying a visual cursor forward: the next run's last character (iw), or the white
    /// up to the next word and that word, or a word and the white after it on its line (aw). A line's break is no run:
    /// from a line's last character the step begins on the next line, and onto an empty line it goes on to the start of
    /// the line after, where aw stops if that is empty too.</summary>
    private static int? WordAhead(TextSnapshot text, int cursor, bool inner, bool bigWord)
    {
        LineSpan line = VimMotions.LineOf(text, cursor);
        int from;
        if (line.Length == 0 || cursor >= VimMotions.StepBack(text, line.End))
        {
            if (line.Line + 1 >= text.LineCount)
            {
                // At the text's end aw takes nothing more yet holds, iw fails.
                return inner ? null : cursor;
            }
            LineSpan below = text.GetLine(line.Line + 1);
            if (below.Length == 0)
            {
                if (below.Line + 1 >= text.LineCount)
                {
                    return below.Start;
                }
                LineSpan after = text.GetLine(below.Line + 1);
                if (inner || after.Length == 0)
                {
                    return after.Start;
                }
                int word = PastWhite(text, after.Start);
                return word >= text.Length ? after.Start : RunEdge(text, word, bigWord, end: true);
            }
            from = below.Start;
        }
        else
        {
            from = TextNavigation.NextGrapheme(text, cursor);
        }
        int end = RunEdge(text, from, bigWord, end: true);
        if (inner)
        {
            return end;
        }
        if (WordKind(text, from, bigWord) == 0)
        {
            int word = PastWhite(text, from);
            return word >= text.Length ? null : RunEdge(text, word, bigWord, end: true);
        }
        LineSpan wordLine = VimMotions.LineOf(text, end);
        return end + 1 < wordLine.End && WordKind(text, end + 1, bigWord) == 0 ? RunEdge(text, end + 1, bigWord, end: true) : end;
    }

    /// <summary>One step of iw or aw carrying a visual cursor back: the run before's first character (iw), or the white
    /// back to the word before and that word, or the word before and the white before it on its line, where none is a
    /// word starting its line leaving its first character out, as Vim's does (aw). From a line's first character the
    /// step begins on the line above's last.</summary>
    private static int? WordBehind(TextSnapshot text, int cursor, bool inner, bool bigWord)
    {
        LineSpan line = VimMotions.LineOf(text, cursor);
        int from;
        if (cursor <= line.Start)
        {
            if (line.Line == 0)
            {
                return null;
            }
            LineSpan above = text.GetLine(line.Line - 1);
            if (above.Length == 0)
            {
                if (inner)
                {
                    return above.Start;
                }
                int word0 = BeforeWhite(text, above.Start);
                return word0 < 0 ? null : RunEdge(text, word0, bigWord, end: false);
            }
            from = VimMotions.StepBack(text, above.End);
        }
        else
        {
            from = VimMotions.StepBack(text, cursor);
        }
        int start = RunEdge(text, from, bigWord, end: false);
        if (inner)
        {
            return start;
        }
        if (WordKind(text, from, bigWord) == 0)
        {
            int word = BeforeWhite(text, from);
            return word < 0 ? null : RunEdge(text, word, bigWord, end: false);
        }
        LineSpan wordLine = VimMotions.LineOf(text, start);
        if (start > wordLine.Start && WordKind(text, start - 1, bigWord) == 0)
        {
            return RunEdge(text, start - 1, bigWord, end: false);
        }
        return start == wordLine.Start ? Math.Min(start + 1, cursor) : start;
    }

    /// <summary>is and as in visual mode: the sentence at a single place; over a longer stretch its cursor carried to
    /// the end of the sentences after it, or back to the start of those before it.</summary>
    private Region? SentencesOver(TextSnapshot text, Region r, bool inner, int n)
    {
        if (r.Anchor == r.Cursor)
        {
            return VimTextObjects.Of(text, r.Cursor, "s", inner, n, breakEnds) is { } span ? Spanned(text, OwnLine(text, span)) : null;
        }
        if (r.Cursor < r.Anchor)
        {
            return r.Cursor > 0 && VimTextObjects.Of(text, VimMotions.StepBack(text, r.Cursor), "s", inner, n, breakEnds) is { } before && before.Start < r.Cursor
                ? r with { Cursor = before.Start }
                : null;
        }
        int after = CharEnd(text, r.Cursor);
        return after < text.Length && VimTextObjects.Of(text, after, "s", inner, n, breakEnds) is { } next && OwnLine(text, next).End > after
            ? r with { Cursor = VimMotions.StepBack(text, OwnLine(text, next).End) }
            : null;
    }

    /// <summary>A sentence's span ending at its own line's end where it runs to the next line's first column.</summary>
    private static VimSpan OwnLine(TextSnapshot text, VimSpan span)
    {
        LineSpan last = VimMotions.LineOf(text, span.End);
        return span.End > span.Start && span.End == last.Start && last.Line > 0 ? span with { End = Math.Max(span.Start, text.GetLine(last.Line - 1).End) } : span;
    }

    /// <summary>A block, a tag or a quoted string in visual mode: at a single place the one there; over a longer stretch
    /// the one around its start or the first after it, and for a block or a tag the one around that where the stretch
    /// already holds more than a character of it. An inner block of whole lines runs to its last line's break.</summary>
    private Region? Around(TextSnapshot text, Region r, string key, bool inner, int n, bool block)
    {
        bool single = r.Anchor == r.Cursor;
        int low = r.Low;
        int high = CharEnd(text, r.High);
        if (low >= text.Length || VimTextObjects.Of(text, single ? r.Cursor : low, key, inner, n, breakEnds) is not { } found)
        {
            return null;
        }
        (int start, int end) = Charwise(text, found);
        if (!single && !block)
        {
            return QuotedOn(text, r, key[0], inner);
        }
        if (!single && block && end - start > 1 && start >= low && end <= high)
        {
            if (VimTextObjects.Of(text, low, key, inner, n + 1, breakEnds) is not { } outer)
            {
                return null;
            }
            (start, end) = Charwise(text, outer);
        }
        return new Region(start, end > start ? VimMotions.StepBack(text, end) : start);
    }

    /// <summary>A quoted string over a longer visual stretch whose cursor stands last: the string around or after the
    /// cursor in place of the stretch (in linewise mode up to its end), or, where the stretch holds that already, the
    /// stretch as it is (i) or carried to the next string on the line (a).</summary>
    private Region? QuotedOn(TextSnapshot text, Region r, char quote, bool inner)
    {
        if (r.Cursor < r.Anchor || VimTextObjects.Of(text, r.Cursor, quote.ToString(), inner, 1, breakEnds) is not { } found)
        {
            return null;
        }
        if (found.Start < r.Low || found.End > CharEnd(text, r.High))
        {
            return Mode == VimMode.VisualLine ? r with { Cursor = Spanned(text, found).Cursor } : Spanned(text, found);
        }
        if (inner)
        {
            return r;
        }
        LineSpan line = VimMotions.LineOf(text, r.Cursor);
        return found.End < line.End && VimTextObjects.Of(text, found.End, quote.ToString(), inner, 1, breakEnds) is { } next && next.End > found.End
            ? r with { Cursor = VimMotions.StepBack(text, next.End) }
            : null;
    }

    /// <summary>An object's characters: one of whole lines up to its last line's break.</summary>
    private static (int Start, int End) Charwise(TextSnapshot text, VimSpan span) =>
        span.Linewise ? (span.Start, VimMotions.LineOf(text, span.End).EndIncludingBreak) : (span.Start, span.End);
}
