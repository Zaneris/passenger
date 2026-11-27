using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Visual mode, by characters, lines or a block: each caret a stretch from where it began to its cursor, shown as
/// the editor's own selections (a block as one per line), moved by normal mode's motions and acted on by its operators.</summary>
public sealed partial class Vim
{
    /// <summary>A visual stretch: where it began and where its cursor stands, both on a character, the cursor on its
    /// line's end after $, an l past the last character, or j or k onto a line too short for the column aimed for.</summary>
    private readonly record struct Region(int Anchor, int Cursor)
    {
        public int Low => Math.Min(Anchor, Cursor);

        public int High => Math.Max(Anchor, Cursor);
    }

    /// <summary>How much a visual change took, for . to take as much again from the caret: the lines below its first,
    /// and the characters it took on one line or the column it ended at on its last; a block's width in columns; and
    /// for I or A over characters or lines, where . types again (<see cref="InsertAgain"/>).</summary>
    private readonly record struct VisualExtent(VimMode Mode, int Lines, int Columns, bool ToEnd, InsertAgain Again = InsertAgain.None);

    /// <summary>Where . types again what I or A over characters or lines typed: at the caret, as I did; else as many
    /// lines below as the stretch had, at the caret's place in its line or, as A after $ did (its cursor above where the
    /// stretch began too), that line's last character, past it where the columns the last visual change kept
    /// differ.</summary>
    private enum InsertAgain
    {
        None,
        AtCursor,
        AtColumnBelow,
        OnLastBelow,
    }

    /// <summary>A change for . to make again: its keys after the count, the count, what its insert typed, and for a change
    /// made in visual mode how much it took; <see cref="Breaks"/> the places in what it typed of each line break Enter made,
    /// which . makes as Enter does, the indent Enter wrote after it left out of the text; <see cref="JoinedNothing"/> set
    /// for a J that had nothing below to join.</summary>
    private sealed record ChangeMade(IReadOnlyList<string> Keys, int Count, string Inserted, VisualExtent? Visual = null)
    {
        public IReadOnlyList<int> Breaks { get; init; } = [];

        public bool JoinedNothing { get; init; }
    }

    private static readonly HashSet<string> VisualOperators = ["d", "x", VimKeys.Delete, "X", "D", "c", "s", "C", "S", "R", "y", "Y", ">", "<", "=", "~", "u", "U", "J", "p", "P", "I", "A"];

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

    /// <summary>The columns . takes a block's change again over: as many as it took, whatever characters they cut.</summary>
    private (int Left, int Right)? blockAgain;

    /// <summary>The last visual stretch, for gv: its mode, whether its cursor stood first, and a block's columns; its
    /// ends are the marks &lt; and &gt;.</summary>
    private (VimMode Mode, bool CursorFirst, (int Anchor, int Cursor, bool ToEnd) Block)? lastVisual;

    /// <summary>Where a block's insert session goes back to as it ends, its one cursor at the block's top left; null for
    /// any other session.</summary>
    private int? blockCorner;

    /// <summary>Whether the insert session under way is a block's change, whose one cursor goes back to where it typed on
    /// the block's first line.</summary>
    private bool blockChange;

    /// <summary>The spaces a block's A padded each line it inserts on with, top to bottom, for the session to remember.</summary>
    private int[]? blockPads;

    /// <summary>A block's insert session as it began: the text then, each caret's line, top to bottom, and the spaces
    /// padding it, for what it types with a line break to go on the first line alone.</summary>
    private (TextSnapshot Text, int[] Lines, int[] Pads)? blockStart;

    /// <summary>The visual extent of the change under way, kept with it for . to take again.</summary>
    private VisualExtent? changeExtent;

    /// <summary>The display columns the last stretch a visual operator took began and ended at, which A over characters or
    /// lines and . after it place themselves by: a block's edges; after $ those before kept; over lines the left one kept,
    /// to the first column where I, A or lines went back up, else the cursor's for I or A by characters, else the end's;
    /// on one line from the first column to the cursor's for I or A by characters and lines taken back, the end's by
    /// lines; else from where its first character begins to where its last ends.</summary>
    private int keptBlockLeft;

    private int keptBlockRight;

    /// <summary>Set where words in linewise mode ran out at the text's last character, which keeps the mode linewise.</summary>
    private bool wordsKeptLines;

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

    /// <summary>Visual mode begun at each caret (a block's at the primary one), j and k aiming for the columns normal
    /// mode's <paramref name="aims"/> for them, a block after $ reaching each line's end.</summary>
    private VimResult EnterVisual(VimMode mode, int count, TextSnapshot text, SelectionSet carets, IVimView view, int[]? aims = null)
    {
        Mode = mode;
        IReadOnlyList<Selection> from = mode == VimMode.VisualBlock ? [carets.Primary] : carets.Items;
        regions = [.. from.Select(s => new Region(s.Caret, s.Caret))];
        visualPrimary = mode == VimMode.VisualBlock ? 0 : carets.PrimaryIndex;
        visualAims = aims is null || aims.Length != carets.Count ? [.. regions.Select(_ => -1)]
            : mode == VimMode.VisualBlock ? [aims[carets.PrimaryIndex]]
            : [.. aims];
        int tab = view.Indent.TabWidth;
        if (mode == VimMode.VisualBlock)
        {
            int column = TextNavigation.VisualColumn(text, regions[0].Cursor, tab);
            blockColumns = (column, column, visualAims[0] == int.MaxValue);
            // j and k aim for the column normal mode's cursor stood in, a tab's last, until the block's cursor moves along its line.
            visualAims[0] = visualAims[0] >= 0 ? visualAims[0] : CaretColumn(text, regions[0].Cursor, tab);
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
                        : VimMotions.LinesAway(text, cursor, count - 1, breakEnds) is { } line ? VimMotions.OffsetAtColumn(text, line, TextNavigation.VisualColumn(text, cursor, tab), tab) : cursor,
                };
            }
            if (mode == VimMode.VisualBlock)
            {
                blockColumns = blockColumns with { Cursor = TextNavigation.VisualColumn(text, regions[0].Cursor, tab) };
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

    /// <summary>The character before a place: the line before's last before a line's first, or that line where empty.</summary>
    private static int CharacterBefore(TextSnapshot text, int place)
    {
        LineSpan line = VimMotions.LineOf(text, place);
        return place > line.Start ? VimMotions.StepBack(text, place)
            : text.GetLine(line.Line - 1) is { Length: > 0 } prior ? VimMotions.StepBack(text, prior.End) : text.GetLineStart(line.Line - 1);
    }

    /// <summary>Just past a character visual mode takes: the next one, or past the line break for a cursor on its line's end.</summary>
    private static int CharEnd(TextSnapshot text, int offset) => offset >= text.Length ? text.Length : TextNavigation.NextGrapheme(text, offset);

    /// <summary>A block's first and last display column, the last int.MaxValue where it reaches each line's end.</summary>
    private (int Left, int Right) BlockSpan(TextSnapshot text, Region r, IVimView view)
    {
        int tab = view.Indent.TabWidth;
        (int anchor, int cursor, bool toEnd) = blockColumns;
        if (blockAgain is { } again)
        {
            return (again.Left, toEnd ? int.MaxValue : again.Right);
        }
        int anchorEnd = anchor + Math.Max(1, CellsOf(text, r.Anchor, tab)) - 1;
        int cursorEnd = cursor + Math.Max(1, CellsOf(text, VimMotions.OffsetAtColumn(text, text.GetLineFromOffset(r.Cursor), cursor, tab), tab)) - 1;
        return (Math.Min(anchor, cursor), toEnd ? int.MaxValue : Math.Max(anchorEnd, cursorEnd));
    }

    /// <summary>The cells the character at an offset takes, 1 at a line's end.</summary>
    private static int CellsOf(TextSnapshot text, int offset, int tab)
    {
        LineSpan line = VimMotions.LineOf(text, offset);
        return offset >= line.End ? 1 : TextNavigation.VisualColumn(text, TextNavigation.NextGrapheme(text, offset), tab) - TextNavigation.VisualColumn(text, offset, tab);
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
            int next = TextNavigation.VisualColumn(text, TextNavigation.NextGrapheme(text, p), tab);
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

    private VimResult PressVisual([IdentifierOf(typeof(VimKeys))] string key, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        if (shownVisual is null || !shownVisual.SameAs(selections))
        {
            Derive(text, selections);
        }
        if (key == VimKeys.Escape)
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
            // The command line takes the stretch's lines, visual mode ending at its start (whole lines' first column) as Vim's does.
            pending.Clear();
            (bool lines, bool block) = (Mode == VimMode.VisualLine, Mode == VimMode.VisualBlock);
            List<int> starts = [.. regions.Select(r => lines ? VimMotions.LineOf(text, r.Low).Start : r.Low)];
            VimResult left = LeaveVisual(text, view);
            if (!block && starts.Count == left.Selections?.Count)
            {
                left = left with { Selections = SelectionSet.Create([.. starts.Select(at => Selection.At(Settled(text, at, breakEnds)))], left.Selections.PrimaryIndex) };
            }
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
            case "v" or "V" or VimKeys.CtrlV:
                VimMode to = first switch { "v" => VimMode.Visual, "V" => VimMode.VisualLine, _ => VimMode.VisualBlock };
                return to == Mode ? LeaveVisual(text, view) : SwitchVisual(to, text, view);
            case "o":
                // A block's corner $ took past its line's end stays there, the block no longer reaching each line's end.
                regions = [.. regions.Select(r => new Region(r.Cursor, r.Anchor))];
                blockColumns = (blockColumns.Cursor, blockColumns.Anchor, false);
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
                if (VimKeys.Typed(second, enterBreaks: true) is not { } with)
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
            blockColumns = (TextNavigation.VisualColumn(text, r.Anchor, tab), TextNavigation.VisualColumn(text, r.Cursor, tab), false);
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
        view.SetMark('<', Math.Min(r.Low, text.Length), text);
        view.SetMark('>', Math.Min(r.High, text.Length), text);
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
                        ? aim == int.MaxValue || TextNavigation.VisualColumn(text, text.GetLine(line).End, tab) <= aim ? LineEndOf(text, line)
                        : VimMotions.OffsetAtColumn(text, line, aim, tab)
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
                case "|":
                    // A column past the line's end stands at its end, after its last character; j and k aim at it all the same.
                    aim = n - 1;
                    int columnLine = text.GetLineFromOffset(cursor);
                    moved = TextNavigation.VisualColumn(text, text.GetLine(columnLine).End, tab) <= aim ? LineEndOf(text, columnLine)
                        : VimMotions.OffsetAtColumn(text, columnLine, aim, tab);
                    break;
                case "e" or "E":
                    moved = WordEndOrTextEnd(text, cursor, n, command.Name == "E");
                    break;
                case ")":
                    moved = SentenceOrTextEnd(text, cursor, n);
                    break;
                case "%" when command.Count == 0 && VimMotions.LineOf(text, cursor) is { Length: > 0 } onLine && cursor == onLine.End:
                    // From a line's end, past its last character, % goes from that character.
                    moved = Target(command, text, VimMotions.StepBack(text, cursor), operating: false, view)?.Offset;
                    break;
                default:
                    moved = Target(command, text, cursor, operating: false, view)?.Offset;
                    break;
            }
            if (moved is not { } reached)
            {
                // A $ cut short still aims at the line's end; a word (w in an emptied text), sentence, paragraph or search the cursor.
                bool resets = command.Name is "b" or "B" or "e" or "E" or "ge" or "gE" or "(" or ")" or "{" or "}" or "n" or "N"
                    || (command.Name is "w" or "W" && text.Length == 0);
                visualAims[i] = resets ? -1 : block ? visualAims[i] : command.Name == "$" ? int.MaxValue : visualAims[i];
                blockColumns = !block ? blockColumns
                    : command.Name == "$" ? blockColumns with { ToEnd = true }
                    : resets ? blockColumns with { ToEnd = false, Cursor = TextNavigation.VisualColumn(text, cursor, tab) }
                    : blockColumns;
                continue;
            }
            // Past the last line a visual cursor stands at its end, after its last character.
            int to = Math.Min(reached, LineEndOf(text, VimMotions.LastLine(text, breakEnds)));
            regions[i] = r with { Cursor = to };
            visualAims[i] = aim;
            if (block)
            {
                // The block's cursor stands past a line too short for its aim (or after $), on a tab at its first column.
                int width = TextNavigation.VisualColumn(text, VimMotions.LineOf(text, to).End, tab);
                blockColumns = command.Name is "j" or "k" or "gj" or "gk"
                    ? blockColumns with
                    {
                        Cursor = blockColumns.ToEnd ? width
                            : aim >= 0 && aim != int.MaxValue && aim >= width ? width
                            : TextNavigation.VisualColumn(text, to, tab),
                    }
                    : blockColumns with { Cursor = TextNavigation.VisualColumn(text, to, tab), ToEnd = command.Name == "$" };
            }
        }
        return ShowVisual(text, view);
    }

    /// <summary>e and E in visual mode: the end of the <paramref name="n"/>th word on, or with no word left the last
    /// line's end, past its last character; null where the cursor stands there already.</summary>
    private int? WordEndOrTextEnd(TextSnapshot text, int cursor, int n, bool bigWord)
    {
        int end = text.GetLine(VimMotions.LastLine(text, breakEnds)).End;
        int at = cursor;
        for (int i = 0; i < n && at < end; i++)
        {
            int p = TextNavigation.NextGrapheme(text, at);
            while (p < end && VimMotions.KindAt(text, p, bigWord) == 0)
            {
                p++;
            }
            if (p >= end)
            {
                return end;
            }
            int kind = VimMotions.KindAt(text, p, bigWord);
            while (p + 1 < end && VimMotions.KindAt(text, p + 1, bigWord) == kind)
            {
                p++;
            }
            at = VimMotions.StepBack(text, p + 1);
        }
        return at == cursor ? null : at;
    }

    /// <summary>) in visual mode: from a line's end, past its last character, first past the empty lines right after it
    /// and on to the text after the blanks and line breaks, an empty line stopping it; past the last sentence the last
    /// line's end; null where it cannot move.</summary>
    private int? SentenceOrTextEnd(TextSnapshot text, int cursor, int n)
    {
        int end = LineEndOf(text, VimMotions.LastLine(text, breakEnds));
        int at = cursor;
        if (VimMotions.LineOf(text, at) is { Length: > 0 } line && at == line.End)
        {
            at = Math.Min(end, at + line.BreakLength);
            while (at < end && VimMotions.LineOf(text, at) is { Length: 0 } empty)
            {
                at = empty.End + empty.BreakLength;
            }
            while (at < end && char.IsWhiteSpace(text[at]) && VimMotions.LineOf(text, at).Length > 0)
            {
                at++;
            }
            if (--n == 0)
            {
                return at == cursor ? null : at;
            }
        }
        return VimMotions.Sentence(text, at, n, forward: true, breakEnds) is { } target
            ? target.Kind == VimMotionKind.Inclusive ? end : target.Offset
            : null;
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
    /// it. A paragraph over one line makes the mode linewise, words keep a block's, sentences as <see cref="SentenceAt"/> says.</summary>
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
        // Words keep a block's mode; sentences a block's and a linewise one, but where one found afresh becomes the stretch.
        bool keeps = key is "w" or "W";
        bool sentencesKeep = key == "s";
        List<Region> taken = [];
        foreach (Region r in regions)
        {
            Region? got = key switch
            {
                "p" => ParagraphOver(text, r, inner, n, ref lines),
                "w" or "W" => WordsOver(text, r, inner, n, key == "W"),
                "s" => SentencesOver(text, r, inner, n, ref sentencesKeep),
                _ => Around(text, r, key, inner, n, block),
            };
            any |= got is not null;
            taken.Add(got ?? r);
        }
        // Found or not, the column the cursor aims at is its own again, and a block's is the cursor's.
        visualAims = [.. regions.Select(_ => -1)];
        blockColumns = Mode == VimMode.VisualBlock ? blockColumns with { ToEnd = false, Cursor = TextNavigation.VisualColumn(text, regions[0].Cursor, view.Indent.TabWidth) } : blockColumns;
        if (!any)
        {
            return VimResult.Held;
        }
        regions = taken;
        int tab = view.Indent.TabWidth;
        if (lines || wordsKeptLines)
        {
            Mode = VimMode.VisualLine;
            wordsKeptLines = false;
        }
        else if ((keeps || sentencesKeep) && Mode == VimMode.VisualBlock)
        {
            blockColumns = (TextNavigation.VisualColumn(text, regions[0].Anchor, tab), TextNavigation.VisualColumn(text, regions[0].Cursor, tab), false);
        }
        else if (key != "p" && !sentencesKeep)
        {
            Mode = VimMode.Visual;
        }
        else if (Mode == VimMode.VisualBlock)
        {
            blockColumns = blockColumns with { Cursor = TextNavigation.VisualColumn(text, regions[0].Cursor, tab), ToEnd = false };
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
    /// on (<see cref="VimTextObjects.WordsCarried"/>), back where it stands before where the stretch began.</summary>
    private Region? WordsOver(TextSnapshot text, Region r, bool inner, int n, bool bigWord)
    {
        if (r.Anchor == r.Cursor)
        {
            int at = Settled(text, r.Cursor, breakEnds);
            if (inner && VimTextObjects.EmptyLastLineReach(text, at, breakEnds) is { } above)
            {
                // On an empty last line the words go back: the line above's last place, then a word further back for each more.
                if (n == 1)
                {
                    return new Region(at, above);
                }
                (int back, _, bool walked, bool bumpedBack) = VimTextObjects.WordsCarried(text, above, back: true, n - 1, inner, bigWord, breakEnds, lines: false);
                wordsKeptLines = KeepsLines(back, walked, bumpedBack);
                return new Region(at, back);
            }
            if (VimTextObjects.WordsShown(text, at, n, inner, bigWord, breakEnds) is not { } shown)
            {
                return null;
            }
            // A count the text cuts short takes as many as there are, keeping a linewise mode.
            wordsKeptLines = Mode == VimMode.VisualLine && shown.CutShort;
            return new Region(shown.Start, shown.End);
        }
        (int to, bool moved, bool whole, bool bumped) = VimTextObjects.WordsCarried(text, r.Cursor, r.Cursor < r.Anchor, n, inner, bigWord, breakEnds,
            lines: Mode == VimMode.VisualLine);
        int end = LineEndOf(text, VimMotions.LastLine(text, breakEnds));
        if (Mode == VimMode.VisualLine && !whole && r.Cursor >= r.Anchor && to < end && Settled(text, end, breakEnds) == to)
        {
            // In linewise mode words running out at the text's last character take the cursor past it, the mode kept.
            wordsKeptLines = true;
            return r with { Cursor = end };
        }
        // A count running out stops there, but fails from the first step.
        wordsKeptLines = moved && KeepsLines(to, whole, bumped);
        return moved ? r with { Cursor = to } : null;
    }

    /// <summary>Whether words carried in linewise mode keep it: where the count ran out, or the last step back began on
    /// the text's very start.</summary>
    private bool KeepsLines(int to, bool whole, bool bumped) => Mode == VimMode.VisualLine && (!whole || (to == 0 && bumped));

    /// <summary>is and as in visual mode: at a single place as <see cref="SentenceAt"/> takes them, <paramref name="keeps"/>
    /// cleared where a sentence found afresh becomes the stretch, which makes it charwise; over a longer stretch as <see
    /// cref="SentencesOn"/> carries its cursor.</summary>
    private Region? SentencesOver(TextSnapshot text, Region r, bool inner, int n, ref bool keeps)
    {
        if (r.Anchor == r.Cursor)
        {
            Region? at = SentenceAt(text, r, inner, n, out bool more);
            keeps &= more || at is null || at == r;
            return at;
        }
        return SentencesOn(text, r, inner, n);
    }

    /// <summary>is and as over a stretch of more than a place: its cursor carried back where it stands before where the
    /// stretch began (<see cref="SentencesBack"/>), else to the end of the sentences after it (onto an empty line a
    /// sentence follows, short of the text's last), or as <see cref="Ended"/> says where none is left.</summary>
    private Region? SentencesOn(TextSnapshot text, Region r, bool inner, int n)
    {
        if (r.Cursor < r.Anchor)
        {
            LineSpan on = VimMotions.LineOf(text, r.Cursor);
            if (on.Line > 0 && VimMotions.Blank(text, on.Line - 1) && TextNavigation.IndentEnd(text, on) is var indent
                && r.Cursor < indent && indent < on.End)
            {
                // Below an empty line, a cursor in the blanks opening its line stays in it (is after them, then at its start), a count going on back.
                int steps = inner ? 2 : 1;
                int to = n < steps ? indent : n == steps ? on.Start : SentencesBack(text, on.Start, inner, n - steps, begun: true);
                return to != r.Cursor ? r with { Cursor = to } : null;
            }
            int lastLine = VimMotions.LastLine(text, breakEnds);
            int run = on.Line + 1;
            while (run < lastLine && VimMotions.Blank(text, run) && VimMotions.Blank(text, run + 1))
            {
                run++;
            }
            if (on.Length > 0 && r.Cursor == on.End && on.Line < lastLine && VimMotions.Blank(text, on.Line + 1) && run == lastLine && r.Anchor > on.End
                && r.Anchor < text.GetLineStart(run))
            {
                // Past the end of a line above empty lines ending the text, a stretch begun on one before their last has the white after it taken: steps go back from where that white begins.
                int taken = SentencesBack(text, WhiteEnding(text, on.Line), inner, n);
                return taken != r.Cursor ? r with { Cursor = taken } : null;
            }
            if (on.Length > 0 && r.Cursor == on.End && on.Line < lastLine && VimMotions.Blank(text, on.Line + 1) && (run < lastLine || text.GetLineStart(run) != r.Anchor)
                && (inner || run > on.Line + 1 || text[on.End - 1] is not (' ' or '\t')))
            {
                // Past the end of a line before empty lines, the first step goes on to the last of them (as after blanks, of two or more), but to the text's last where the stretch begins on it, a count going on back.
                int to = n == 1 ? text.GetLineStart(run) : SentencesBack(text, text.GetLineStart(run), inner, n - 1, reached: true, begun: true);
                return to != r.Cursor ? r with { Cursor = to } : null;
            }
            else if (on.Length > 0 && r.Cursor == on.End && TextNavigation.IndentEnd(text, on) == on.End && on.Line > 0
                && text.GetLine(on.Line - 1).Length > 0)
            {
                // Past the end of a line of blanks alone below text, is takes the white they end, then the sentence before; as that sentence.
                int white = WhiteEnding(text, on.Line);
                int sentence = white > 0 ? SentenceUnit(text, VimMotions.StepBack(text, white)).Start : white;
                int steps = inner ? 2 : 1;
                int to = n < steps ? white : n == steps ? sentence : SentencesBack(text, sentence, inner, n - steps, begun: true);
                if (!inner && on.Line < lastLine && VimMotions.Blank(text, on.Line + 1))
                {
                    // Before an empty line, as takes that white first.
                    to = n == 1 ? white : SentencesBack(text, white, inner, n - 1, begun: true);
                }
                return to != r.Cursor ? r with { Cursor = to } : null;
            }
            else if (inner && n == 1 && on.Length > 0 && r.Cursor == on.End && TextNavigation.IndentEnd(text, on) == on.End)
            {
                // Past the end of a line of blanks alone, is takes the line's blanks.
                return r with { Cursor = on.Start };
            }
            if (!inner && on.Length > 0 && r.Cursor == on.End && on.Line + 2 <= lastLine && VimMotions.Blank(text, on.Line + 1) && text.GetLine(on.Line + 2).Length > 0
                && r.Anchor == text.GetLineStart(on.Line + 1) && WhiteEnding(text, on.Line) is var blanksAt && blanksAt > on.Start && blanksAt < on.End)
            {
                // Past the end of a line ending in blanks, above the empty line the stretch began on, as takes those blanks first, then a sentence with the white before it each step.
                int to = n == 1 ? blanksAt : SentencesBack(text, blanksAt, inner, n - 1);
                return to != r.Cursor ? r with { Cursor = to } : null;
            }
            if (!inner && n > 1 && on.Length > 0 && r.Cursor == on.End && on.Line < lastLine && !VimMotions.Blank(text, on.Line + 1)
                && WhiteEnding(text, on.Line) is var tail && tail > on.Start && tail < on.End
                && SentencesBack(text, tail, inner: true, 2 * n - 1) is var sentencesFrom && sentencesFrom != r.Cursor
                && Enumerable.Range(VimMotions.LineOf(text, sentencesFrom).Line, on.Line - VimMotions.LineOf(text, sentencesFrom).Line + 1).All(line => !VimMotions.Blank(text, line)))
            {
                // Past the end of a line ending in blanks after its text, above text, a count of as takes sentences with the white after each, to the last one's start, within its paragraph.
                return r with { Cursor = sentencesFrom };
            }
            if ((inner || n == 1) && on.Length > 0 && r.Cursor == on.End && WhiteEnding(text, on.Line) is var ending && ending > on.Start && ending < on.End)
            {
                // Past the end of a line ending in blanks after its text, is takes those blanks first, then goes on back; as the sentence they end.
                int to = !inner ? SentenceUnit(text, VimMotions.StepBack(text, ending)).Start
                    : n == 1 ? ending : SentencesBack(text, ending, inner, n - 1, passesAtEmpty: true);
                return to != r.Cursor ? r with { Cursor = to } : null;
            }
            int back = SentencesBack(text, r.Cursor, inner, n);
            return back != r.Cursor ? r with { Cursor = back } : null;
        }
        if (inner && VimMotions.LineOf(text, r.Cursor) is { Length: 0 } empty && empty.EndIncludingBreak < text.Length
            && text[empty.EndIncludingBreak] is ' ' or '\t')
        {
            // From an empty line, the blanks opening the next are a white of their own.
            Region white = r with { Cursor = LastOpeningBlank(text, empty.EndIncludingBreak) };
            return n > 1 ? SentencesOn(text, white, inner, n - 1) ?? white : white;
        }
        if (!inner && VimMotions.LineOf(text, r.Cursor) is { Length: 0 } emptyBefore && emptyBefore.EndIncludingBreak < text.Length
            && text.GetLine(emptyBefore.Line + 1) is var indented && TextNavigation.IndentEnd(text, indented) is var opened && opened > indented.Start && opened < indented.End
            && VimTextObjects.Of(text, indented.Start, "s", inner: true, 2 * n, breakEnds) is { } leading && LastWrittenBefore(text, leading.End) is { } unitsEnd && unitsEnd > r.Cursor
            && !Enumerable.Range(indented.Line + 1, VimMotions.LastLine(text, breakEnds) - indented.Line)
                .Any(line => text.GetLineStart(line) <= leading.End && VimMotions.Blank(text, line)))
        {
            // From an empty line before a line opening with blanks, as takes each sentence with the white before it, ending at its last character, within its paragraph.
            return r with { Cursor = unitsEnd };
        }
        if (!inner && n == 2 && VimMotions.LineOf(text, r.Cursor) is { Length: > 0 } beyond && r.Cursor == beyond.End
            && beyond.EndIncludingBreak < text.Length && text[beyond.EndIncludingBreak] is ' ' or '\t'
            && LastOpeningBlank(text, beyond.EndIncludingBreak) + 1 is var opens && opens < text.Length && text[opens] is not ('\n' or '\r')
            && VimTextObjects.Of(text, opens, "s", inner: true, 1, breakEnds) is { } closing
            && VimMotions.LineOf(text, VimMotions.StepBack(text, OwnLine(text, closing).End)).Line is var closes
            && OwnLine(text, closing).End >= text.GetLine(closes).End && closes < VimMotions.LastLine(text, breakEnds) && VimMotions.Blank(text, closes + 1))
        {
            // Past its line's end, 2as takes the sentence the next line's blanks open and the empty lines after it to the last one's start.
            int runLast = closes + 1;
            while (runLast < VimMotions.LastLine(text, breakEnds) && VimMotions.Blank(text, runLast + 1))
            {
                runLast++;
            }
            if (runLast < VimMotions.LastLine(text, breakEnds))
            {
                return r with { Cursor = text.GetLineStart(runLast) };
            }
        }
        if (!inner && n > 1 && VimMotions.LineOf(text, r.Cursor) is { Length: > 0 } passed && r.Cursor == passed.End
            && passed.EndIncludingBreak < text.Length && text[passed.EndIncludingBreak] is ' ' or '\t'
            && LastOpeningBlank(text, passed.EndIncludingBreak) is var opening && opening + 1 < text.Length && text[opening + 1] is not ('\n' or '\r')
            && VimTextObjects.Of(text, opening + 1, "s", inner: true, 2 * n - 1, breakEnds) is { } counted && counted.End > opening + 1)
        {
            // Past its line's end before blanks opening the next, as counts sentences as is takes them, the white between in.
            return r with { Cursor = VimMotions.StepBack(text, OwnLine(text, counted).End) };
        }
        if ((inner || n == 1) && VimMotions.LineOf(text, r.Cursor) is { Length: > 0 } past && r.Cursor == past.End
            && past.EndIncludingBreak < text.Length && text[past.EndIncludingBreak] is ' ' or '\t')
        {
            // Past its line's last character the cursor is in the white the next line's blanks end: is takes it, as the sentence too.
            int blank = LastOpeningBlank(text, past.EndIncludingBreak);
            Region white = r with { Cursor = blank };
            if (inner)
            {
                return n > 1 ? SentencesOn(text, white, inner, n - 1) ?? white : white;
            }
            int last = VimMotions.LastLine(text, breakEnds);
            int run = text.GetLineFromOffset(blank);
            while (run < last && VimMotions.Blank(text, run + 1))
            {
                run++;
            }
            run = run == last ? run - 1 : run;
            if (run > text.GetLineFromOffset(blank) && blank + 1 == VimMotions.LineOf(text, blank).End)
            {
                // Empty lines after a white of blanks alone are taken in place of a sentence, short of the text's last line.
                return r with { Cursor = text.GetLineStart(run) };
            }
            return VimTextObjects.Of(text, blank + 1, "s", inner: true, 1, breakEnds) is { } sentence && sentence.End > blank + 1 && text[blank + 1] is not ('\n' or '\r')
                ? r with { Cursor = VimMotions.StepBack(text, OwnLine(text, sentence).End) } : Ended(text, r, inner, n);
        }
        if (inner && WhiteEndingText(text) is (var lastSentence, _) && r.Cursor >= lastSentence)
        {
            // From the last sentence's end or the white ending the text no sentence is ahead.
            return Ended(text, r, inner, n);
        }
        int after = CharEnd(text, r.Cursor);
        LineSpan line = VimMotions.LineOf(text, after);
        if (after == line.End && line.Length > 0 && line.BreakLength > 0 && line.EndIncludingBreak < text.Length)
        {
            // A sentence ending its line holds its line break: the next one is taken from the next line.
            after = line.EndIncludingBreak;
        }
        // The text's last line, empty, holds no sentence ahead.
        if (after >= text.Length || (!inner && LastWrittenBefore(text, text.Length) is var written && (written is null || written < after))
            || (inner && n > 1 && VimMotions.LineOf(text, after) is { Length: 0 } endLine && endLine.Line == VimMotions.LastLine(text, breakEnds))
            || VimTextObjects.Of(text, after, "s", inner, n, breakEnds) is not { } next)
        {
            return Ended(text, r, inner, n);
        }
        if (next.EndKept && next.End > after)
        {
            // Counted past its paragraph, as takes the empty lines to the last one's start, where the cursor stands.
            return r with { Cursor = next.End };
        }
        VimSpan own = OwnLine(text, next);
        if (!inner && text[after] is ' ' or '\t' && VimTextObjects.Of(text, after, "s", inner: true, 2 * n, breakEnds) is { } units
            && OwnLine(text, units).End is var whole && whole > own.End && LastWordAfterBlanks(text, own.End, whole))
        {
            // Over white to a sentence closing its text or paragraph unmarked, as takes it whole, a last word after a blank too.
            own = own with { End = whole };
        }
        if (own.End == own.Start && VimMotions.NextSentenceStart(text, own.Start) < text.Length)
        {
            if (!inner && EmptyBelowBlanks(text, after, VimMotions.LastLine(text, breakEnds)) is { } lastEmptyStart)
            {
                // From blanks ending a line, and lines of blanks alone, before empty lines, as takes them all to the last empty line's start.
                return r with { Cursor = lastEmptyStart };
            }
            return own.Start > r.Cursor ? r with { Cursor = own.Start } : null;
        }
        if (next.Linewise && !inner && n == 1 && own.End > after && VimMotions.LineOf(text, own.End) is { Length: 0 } lastEmpty
            && lastEmpty.Line < VimMotions.LastLine(text, breakEnds) && text.GetLine(lastEmpty.Line + 1).Length > 0)
        {
            // Lines taken whole, from blanks alone to the last of the empty lines after them, end on that last one.
            return r with { Cursor = lastEmpty.Start };
        }
        if (!inner && n > 1 && CutShortBeforeBlanks(text, after, n, own.End) is { } left
            && SentencesOn(text, r with { Cursor = VimMotions.StepBack(text, own.End) }, inner, left) is { } further)
        {
            // A count cut short at a sentence ending its line before lines of blanks alone and empty lines goes on from that sentence's end.
            return further;
        }
        // Of the empty lines ending the text, the last is none to go to.
        return own.End > after ? r with { Cursor = VimMotions.StepBack(text, own.End) } : Ended(text, r, inner, n);
    }

    /// <summary>Whether the text from <paramref name="from"/> to <paramref name="to"/> is white, blanks and line breaks
    /// but no empty line, then one character.</summary>
    private static bool LastWordAfterBlanks(TextSnapshot text, int from, int to)
    {
        int last = VimMotions.StepBack(text, to);
        if (last <= from || text[last] is ' ' or '\t' or '\n' or '\r')
        {
            return false;
        }
        for (int at = from; at < last; at++)
        {
            if (text[at] is not (' ' or '\t' or '\n' or '\r') || (at > from && VimMotions.LineOf(text, at).Length == 0))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The last of the blanks opening a line, on through lines of blanks alone into the next line's.</summary>
    private static int LastOpeningBlank(TextSnapshot text, int start)
    {
        LineSpan line = VimMotions.LineOf(text, start);
        while (TextNavigation.IndentEnd(text, line) == line.End && line.EndIncludingBreak < text.Length
            && text[line.EndIncludingBreak] is ' ' or '\t')
        {
            line = VimMotions.LineOf(text, line.EndIncludingBreak);
        }
        return TextNavigation.IndentEnd(text, line) - 1;
    }

    /// <summary>A stretch with nothing after its cursor to take: from a line of text above the empty lines ending the
    /// text, or from the last of them, the cursor goes to the one before the last, where it stays; in the white ending the
    /// text (<see cref="WhiteEndingText"/>) is goes to its last blank and the last sentence's end by turns, a step each; else to
    /// the last character that is no blank, at or before it for is (an even count to the blanks after it), the text's for
    /// as, which leaves one at or past the last line of text's end there.</summary>
    private Region? Ended(TextSnapshot text, Region r, bool inner, int n)
    {
        int last = VimMotions.LastLine(text, breakEnds);
        LineSpan on = VimMotions.LineOf(text, r.Cursor);
        int lastHeld = last;
        while (lastHeld > 0 && VimMotions.Blank(text, lastHeld))
        {
            lastHeld--;
        }
        if (inner && n % 2 == 0 && text.GetLine(lastHeld) is { Length: > 0 } blanks && TextNavigation.IndentEnd(text, blanks) == blanks.End
            && ((on.Line == blanks.Line && r.Cursor == blanks.End) || (lastHeld >= last - 1
                && ((on.Line == last && on.Line == blanks.Line + 1) || (on.Line == blanks.Line && r.Cursor >= VimMotions.StepBack(text, blanks.End))))))
        {
            // Past the last line of blanks alone, or, with one empty line after it at most, on its last blank or that empty line, an even count stops on that blank.
            int stays = VimMotions.StepBack(text, blanks.End);
            return stays != r.Cursor ? r with { Cursor = stays } : null;
        }
        if (last > 1 && VimMotions.Blank(text, last) && VimMotions.Blank(text, last - 1) && text.GetLineStart(last - 1) is var before
            && (r.Cursor == before || on.Line == last || (on.Length > 0 && (r.Cursor < on.End || (!inner && n > 1)))))
        {
            return r.Cursor == before ? null : r with { Cursor = before };
        }
        while (last > 0 && VimMotions.Blank(text, last))
        {
            last--;
        }
        (int Sentence, int Trailing)? turns = WhiteEndingText(text);
        if (turns is not { } shaped || r.Cursor < shaped.Sentence)
        {
            turns = LastWrittenBefore(text, text.Length) is { } written && VimMotions.LineOf(text, written) is var closing && r.Cursor >= written
                && r.Cursor <= closing.End && LastBlankAfter(text, written, closing.End) is { } blank ? (written, blank) : null;
        }
        if (inner && turns is (var sentence, var trailing))
        {
            // In the white ending the text, or on its line after its last sentence, is takes that white and the sentence by turns, a step at a time.
            int at = r.Cursor;
            for (int step = 0; step < n; step++)
            {
                at = at < trailing ? trailing : sentence;
            }
            return at != r.Cursor ? r with { Cursor = at } : null;
        }
        int end = Settled(text, text.GetLine(last).End, breakEnds);
        int to = !inner && r.Cursor >= end ? end : LastWrittenBefore(text, inner ? Math.Min(CharEnd(text, r.Cursor), text.Length) : text.Length) ?? 0;
        if (inner && n % 2 == 0 && CharEnd(text, to) is var white && white < r.Cursor && text[white] is ' ' or '\t'
            && (on.Length == 0 || VimMotions.LineOf(text, white).Line == on.Line))
        {
            // An even count stops in the white the sentence ends with, but from a line of blanks alone below it, as is takes sentence and white by turns.
            to = white;
        }
        return to != r.Cursor ? r with { Cursor = to } : null;
    }

    /// <summary>Where is or as carries a cursor before where its stretch began: back a sentence or a white at a time, from
    /// an empty line before the last of its run to that line first; as takes a white with the sentence before it, a
    /// sentence with the white before where the cursor stood in or after it or on an empty line a step after the walk's
    /// first reached (<paramref name="reached"/> for the first, <paramref name="begun"/> where the walk stepped before);
    /// after is steps from the empty line it began on to a sentence, its next passes the white before it.</summary>
    private int SentencesBack(TextSnapshot text, int at, bool inner, int n, bool reached = false, bool begun = false, bool passesAtEmpty = false)
    {
        int left = n;
        // A walk reaching an empty line on its way passes no white after it.
        bool fromStart = VimMotions.LineOf(text, at).Length == 0;
        if (VimMotions.LineOf(text, at).Length == 0 && VimTextObjects.Of(text, at, "s", inner: true, 1, breakEnds) is { } run && run.Start > at
            && VimMotions.NextSentenceStart(text, run.Start) < text.Length)
        {
            // An empty line before the last of its run, a sentence following, steps on to that last line first.
            at = run.Start;
            left--;
            begun = true;
        }
        bool passWhite = false;
        bool landedAlone = false;
        // An empty line in a run that ends the text passes no white: is takes the white before the sentence it steps to.
        int runLast = VimMotions.LineOf(text, at).Line;
        while (fromStart && runLast < VimMotions.LastLine(text, breakEnds) && text.GetLine(runLast + 1).Length == 0)
        {
            runLast++;
        }
        bool endsText = fromStart && runLast == VimMotions.LastLine(text, breakEnds);
        // A walk begun inside a sentence or the white after one's mark, short of its line's end.
        bool begunInside = SentenceUnit(text, at) is var begunIn && (begunIn.Start < at || begunIn.White) && at < VimMotions.LineOf(text, at).End;
        // A line break alone between two sentences is no white: each is a step of its own.
        for (; left > 0 && at > 0; left--)
        {
            bool fromEmpty = VimMotions.LineOf(text, at).Length == 0;
            (int Start, bool White) here = SentenceUnit(text, at);
            bool inside = here.Start < at;
            (int start, bool white) = inside ? here : SentenceUnit(text, VimMotions.StepBack(text, at));
            // After a step to an empty line alone, is takes the blanks ending the line above it with it.
            if ((passWhite || (passesAtEmpty && fromEmpty) || (inner && landedAlone)) && white && start > 0)
            {
                (start, white) = SentenceUnit(text, VimMotions.StepBack(text, start));
            }
            passWhite = inner && fromEmpty && !white && fromStart && !endsText;
            fromStart = false;
            reached = fromEmpty && (reached || left < (begun ? n : n - 1));
            at = start;
            landedAlone = LoneEmpty(text, at);
            if (!inner && (white || inside || here.White || reached) && at > 0 && SentenceUnit(text, VimMotions.StepBack(text, at)) is var before
                && before.White != white)
            {
                at = before.Start;
            }
            else if (!inner && landedAlone && begunInside && text.GetLine(text.GetLineFromOffset(at) - 1) is var above && WhiteEnding(text, above.Line) is var blanks
                && blanks > above.Start && blanks < above.End)
            {
                // A walk of as begun inside a sentence, on an empty line alone, takes the blanks ending the line above it too.
                at = blanks;
            }
        }
        return at;
    }

    /// <summary>Whether an offset is the start of an empty line between two lines of text, the one below opening with a
    /// character that is no blank, end mark or closer.</summary>
    private bool LoneEmpty(TextSnapshot text, int at) => VimMotions.LineOf(text, at) is { Length: 0, Line: > 0 } empty && empty.Start == at
        && empty.Line < VimMotions.LastLine(text, breakEnds) && text.GetLine(empty.Line - 1).Length > 0 && text.GetLine(empty.Line + 1) is { Length: > 0 } below
        && text[below.Start] is not (' ' or '\t' or '.' or '!' or '?' or ')' or ']' or '"' or '\'');

    /// <summary>The last character of the last line before a line's run of empty lines that holds any.</summary>
    private static int LastCharacterBefore(TextSnapshot text, int line)
    {
        while (line > 0 && text.GetLine(line - 1).Length == 0)
        {
            line--;
        }
        return VimMotions.StepBack(text, text.GetLine(line - 1).End);
    }

    /// <summary>Where the white ending a line begins: its blanks, and for a line of blanks alone those ending the line
    /// above too; the line's end where it ends in none.</summary>
    private static int WhiteEnding(TextSnapshot text, int line)
    {
        LineSpan span = text.GetLine(line);
        int at = span.End;
        while (at > span.Start && text[at - 1] is ' ' or '\t')
        {
            at--;
        }
        return at == span.Start && span.Length > 0 && line > 0 && text.GetLine(line - 1) is { Length: > 0 } above
            && WhiteEnding(text, line - 1) is var before && before < above.End ? before : at;
    }

    /// <summary>Where the white after a line of text begins, its blanks ending that line or the lines of blanks alone
    /// below it, where an offset stands in it; null elsewhere.</summary>
    private static int? BlanksEnding(TextSnapshot text, int offset)
    {
        LineSpan line = VimMotions.LineOf(text, offset);
        int white = WhiteEnding(text, line.Line);
        LineSpan begins = VimMotions.LineOf(text, white);
        bool afterText = TextNavigation.IndentEnd(text, begins) < white
            || (white == begins.Start && begins.Line > 0 && text.GetLine(begins.Line - 1).Length > 0);
        return white < line.End && offset >= white && afterText ? white : null;
    }

    /// <summary>The first line below an offset's that is no line of blanks alone.</summary>
    private static int BelowBlanks(TextSnapshot text, int offset, int lastLine)
    {
        int line = text.GetLineFromOffset(offset) + 1;
        while (line <= lastLine && text.GetLine(line) is { Length: > 0 } below && TextNavigation.IndentEnd(text, below) == below.End)
        {
            line++;
        }
        return line;
    }

    /// <summary>How much of a count of as from <paramref name="from"/> is left where the sentences it takes, ending at
    /// <paramref name="end"/>, stop short at a sentence ending its line before lines of blanks alone and then an empty
    /// line; null where the count is used up or other lines follow.</summary>
    private int? CutShortBeforeBlanks(TextSnapshot text, int from, int n, int end)
    {
        LineSpan line = VimMotions.LineOf(text, end);
        int lastLine = VimMotions.LastLine(text, breakEnds);
        if (end <= from || line.Length == 0 || end != line.End || line.Line >= lastLine
            || text.GetLine(line.Line + 1) is not { Length: > 0 } blanks || TextNavigation.IndentEnd(text, blanks) != blanks.End || BelowBlanks(text, line.EndIncludingBreak, lastLine) is var below && (below > lastLine || text.GetLine(below).Length > 0))
        {
            return null;
        }
        int taken = 1;
        while (taken < n && VimTextObjects.Of(text, from, "s", inner: false, taken, breakEnds) is { } fewer && OwnLine(text, fewer).End < end)
        {
            taken++;
        }
        return taken < n ? n - taken : null;
    }

    /// <summary>From blanks that end their line, the start of the last of the empty lines after it and the lines of blanks
    /// alone that follow it, where a line that is not empty comes after them; null otherwise.</summary>
    private static int? EmptyBelowBlanks(TextSnapshot text, int offset, int lastLine)
    {
        LineSpan line = VimMotions.LineOf(text, offset);
        if (offset >= line.End || !VimMotions.BlanksTo(text, offset, line.End))
        {
            return null;
        }
        int empty = BelowBlanks(text, offset, lastLine);
        if (empty > lastLine || text.GetLine(empty).Length > 0)
        {
            return null;
        }
        while (empty < lastLine && text.GetLine(empty + 1).Length == 0)
        {
            empty++;
        }
        return empty < lastLine ? text.GetLineStart(empty) : null;
    }

    /// <summary>Whether an offset is a blank of the white after a mark ending a sentence on its line.</summary>
    private static bool AfterMark(TextSnapshot text, int offset)
    {
        if (offset >= text.Length || text[offset] is not (' ' or '\t'))
        {
            return false;
        }
        int start = VimMotions.LineOf(text, offset).Start;
        int white = offset;
        while (white > start && text[white - 1] is ' ' or '\t')
        {
            white--;
        }
        int mark = white - 1;
        while (mark >= start && text[mark] is ')' or ']' or '"' or '\'')
        {
            mark--;
        }
        return mark >= start && text[mark] is '.' or '!' or '?' && VimMotions.EndsSentence(text, mark, out int after) && after == white;
    }

    /// <summary>The last of the blanks after a character up to a line's end, null where none follows it.</summary>
    private static int? LastBlankAfter(TextSnapshot text, int character, int lineEnd)
    {
        int at = CharEnd(text, character);
        while (at < lineEnd && text[at] is ' ' or '\t')
        {
            at++;
        }
        return at > CharEnd(text, character) ? at - 1 : null;
    }

    /// <summary>The text's last character that is no blank and the last blank of the white after it, where that white
    /// is blanks ending its line and lines of blanks alone, one empty line after them at most; null otherwise.</summary>
    private (int Sentence, int Trailing)? WhiteEndingText(TextSnapshot text)
    {
        int last = VimMotions.LastLine(text, breakEnds);
        if (LastWrittenBefore(text, text.Length) is not { } sentence)
        {
            return null;
        }
        int closing = VimMotions.LineOf(text, sentence).Line;
        int held = last > closing && VimMotions.Blank(text, last) ? last - 1 : last;
        if (Enumerable.Range(closing + 1, held - closing).Any(line => VimMotions.Blank(text, line)))
        {
            return null;
        }
        int? trailing = held == closing ? LastBlankAfter(text, sentence, text.GetLine(closing).End) : VimMotions.StepBack(text, text.GetLine(held).End);
        return trailing is { } blank ? (sentence, blank) : null;
    }

    /// <summary>The last character that is no blank or line break before an offset, null where there is none.</summary>
    private static int? LastWrittenBefore(TextSnapshot text, int offset)
    {
        int at = offset;
        while (at > 0 && text[at - 1] is ' ' or '\t' or '\n' or '\r')
        {
            at--;
        }
        return at > 0 ? VimMotions.StepBack(text, at) : null;
    }

    /// <summary>is and as from a single place: the sentence there, the white after one no bigger than the character
    /// already shown, and from an empty line or the white after a line of text what Vim's take there. <paramref
    /// name="more"/> says the place grew into its stretch, which keeps a block's or a linewise mode, where a sentence
    /// found afresh makes the stretch charwise.</summary>
    private Region? SentenceAt(TextSnapshot text, Region r, bool inner, int n, out bool more)
    {
        more = false;
        if (VimTextObjects.Of(text, r.Cursor, "s", inner, n, breakEnds) is not { } span)
        {
            return null;
        }
        int lastLine = VimMotions.LastLine(text, breakEnds);
        if (VimMotions.LineOf(text, r.Cursor) is { Length: 0 } inRun && inRun.Line < lastLine && text.GetLine(inRun.Line + 1).Length == 0)
        {
            int runLast = inRun.Line + 1;
            while (runLast < lastLine && text.GetLine(runLast + 1).Length == 0)
            {
                runLast++;
            }
            bool next = runLast == inRun.Line + 1;
            if (runLast == lastLine && Enumerable.Range(0, inRun.Line).All(line => VimMotions.Blank(text, line)))
            {
                // In a text of empty lines alone, the lines from the first to the one before the last, whatever the count.
                return new Region(0, text.GetLineStart(lastLine - 1));
            }
            bool unindented = runLast < lastLine && text[text.GetLineStart(runLast + 1)] is not (' ' or '\t');
            if (runLast < lastLine && !next && (n == 1 ? inner || unindented : n == 2 && inner && unindented)
                && LastWrittenBefore(text, r.Cursor) is { } before)
            {
                // From an empty line two or more above its run's last, is goes back to the last written character before the run, 2is and as (before text unindented) to its last character.
                more = true;
                return new Region(r.Cursor, n == 1 && inner ? before : LastCharacterBefore(text, inRun.Line));
            }
            if (n == 1 && runLast < lastLine && (inner || (next && text[text.GetLineStart(runLast + 1)] is not (' ' or '\t'))))
            {
                // From empty lines in a run the text goes on after, is takes them to the run's last, as from the one before it.
                more = true;
                return new Region(r.Cursor, text.GetLineStart(runLast));
            }
        }
        if (VimMotions.LineOf(text, r.Cursor) is { Length: 0 } onEmpty && onEmpty.Line == lastLine && onEmpty.Line > 0
            && text.GetLine(onEmpty.Line - 1) is var above)
        {
            int blanks = WhiteEnding(text, above.Line);
            // From the last empty line back: as to before its sentence, is to a lone blank before it (more blanks alone).
            if (!inner && span.End > span.Start)
            {
                return new Region(r.Cursor, span.Start > 0 ? CharacterBefore(text, span.Start) : 0);
            }
            if (inner && n > 1 && blanks > 0 && blanks < above.End)
            {
                // A count: even, the white's first blank and the character before; odd, a white of blanks whole, a lone blank's on back.
                more = n % 2 == 1 && above.End - blanks == 1;
                return n % 2 == 0 ? new Region(blanks, CharacterBefore(text, blanks)) : more ? new Region(r.Cursor, SentencesBack(text, blanks, inner, n - 1))
                    : new Region(blanks, above.End - 1);
            }
            if (inner && blanks < above.End)
            {
                more = above.End - blanks == 1;
                return more ? new Region(r.Cursor, blanks) : new Region(blanks, above.End - 1);
            }
        }
        if (!inner && n == 1 && BlanksEnding(text, r.Cursor) is { } ending && BelowBlanks(text, r.Cursor, lastLine) is var first && first <= lastLine
            && VimMotions.Blank(text, first))
        {
            // From the white after a line of text, as takes the empty lines below it the text goes on after, one alone before a blank.
            int to = first;
            while (to < lastLine && VimMotions.Blank(text, to + 1))
            {
                to++;
            }
            to = to == lastLine ? to - 1 : to;
            if (to > first || (to == first && text[text.GetLineStart(first + 1)] is ' ' or '\t'))
            {
                return new Region(ending, text.GetLineStart(to));
            }
        }
        if (!inner && n == 1 && VimMotions.LineOf(text, r.Cursor) is { Length: 0 } empty && empty.Line < lastLine
            && text.GetLine(empty.Line + 1) is { Length: > 0 } below && text[below.Start] is not (' ' or '\t'))
        {
            // From an empty line before a sentence, as takes the blanks ending the line above with it, else it and the sentence.
            if (empty.Line > 0 && WhiteEnding(text, empty.Line - 1) is var blanks && blanks < text.GetLine(empty.Line - 1).End)
            {
                return new Region(blanks, r.Cursor);
            }
            more = true;
            return VimTextObjects.Of(text, below.Start, "s", inner: true, 1, breakEnds) is { } sentence ? new Region(r.Cursor, Spanned(text, OwnLine(text, sentence)).Cursor) : null;
        }
        if (span.End == span.Start)
        {
            // Nothing found: from the place before it to it.
            return new Region(span.Start, span.Start > 0 ? CharacterBefore(text, span.Start) : span.Start);
        }
        Region found = Spanned(text, OwnLine(text, span));
        if (!inner && n >= 2 && AfterMark(text, r.Cursor) && found.Anchor <= r.Cursor && found.Cursor > r.Cursor
            && VimMotions.LineOf(text, found.Cursor) is var closing && closing.Line < lastLine
            && (closing.Length == 0 || (CharEnd(text, found.Cursor) == closing.End && VimMotions.Blank(text, closing.Line + 1)))
            && span.EndKept && (closing.Length == 0 ? closing.Line : closing.Line + 1) is var runFirst)
        {
            // From the white after a sentence's mark, as counted past its paragraph goes on to the last of the empty lines after it, short of the text's last.
            int to = runFirst;
            while (to < lastLine && VimMotions.Blank(text, to + 1))
            {
                to++;
            }
            to = to == lastLine ? to - 1 : to;
            return to >= runFirst ? found with { Cursor = text.GetLineStart(to) } : found;
        }
        if (!inner && n >= 2 && VimMotions.LineOf(text, r.Cursor) is { Length: 0, Line: > 0 } emptyAt && found.Anchor == r.Cursor && found.Cursor > r.Cursor
            && text[found.Cursor] is not (' ' or '\t') && text.GetLine(emptyAt.Line - 1) is var lineAbove
            && WhiteEnding(text, lineAbove.Line) is var trailing && trailing > lineAbove.Start && trailing < lineAbove.End)
        {
            // From an empty line, as counted on to a sentence with no white after it takes the blanks ending the line above, as the white before it.
            return found with { Anchor = trailing };
        }
        if (!inner && n == 1 && found.Anchor == r.Cursor && found.Cursor == r.Cursor && VimMotions.LineOf(text, r.Cursor) is var alone
            && alone.Start == r.Cursor && CharEnd(text, r.Cursor) == alone.End && alone.Line < lastLine && VimMotions.Blank(text, alone.Line + 1))
        {
            // A sentence that is its line's one character takes the empty lines below it, short of the text's last.
            int to = alone.Line + 1;
            while (to < lastLine && VimMotions.Blank(text, to + 1))
            {
                to++;
            }
            to = to == lastLine ? to - 1 : to;
            more = true;
            return to > alone.Line ? new Region(r.Cursor, text.GetLineStart(to)) : found;
        }
        // A sentence no bigger than the character already shown takes the white after it too.
        if (found.Anchor == r.Cursor && found.Cursor == r.Cursor && r.Cursor < text.Length && text[r.Cursor] is not (' ' or '\t')
            && VimTextObjects.Of(text, r.Cursor, "s", inner, n + 1, breakEnds) is { } further)
        {
            more = true;
            return Spanned(text, OwnLine(text, further));
        }
        if (found.Anchor == found.Cursor && found.Cursor != r.Cursor)
        {
            // A sentence of one character elsewhere is no stretch: the place's reaches it and goes on the count's steps, back or over blanks.
            more = true;
            Region reached = new(r.Cursor, found.Cursor);
            int next = CharEnd(text, found.Cursor);
            next = VimMotions.LineOf(text, next) is var ends && next == ends.End ? ends.EndIncludingBreak : next;
            bool on = found.Cursor < r.Cursor || (next < text.Length && text[next] is ' ' or '\t');
            return on ? SentencesOn(text, reached, inner, n) ?? reached : reached;
        }
        // From an empty line, is keeps a block's or a linewise mode over the blanks it takes after it.
        more = inner && n == 1 && found.Anchor == r.Cursor && VimMotions.LineOf(text, r.Cursor).Length == 0;
        return found;
    }

    /// <summary>Where the sentence or the white between two that an offset is in begins, as is takes it there, and
    /// whether it is the white (an empty line being a sentence, but one before the last of its run).</summary>
    private (int Start, bool White) SentenceUnit(TextSnapshot text, int offset)
    {
        if (VimTextObjects.Of(text, offset, "s", inner: true, 1, breakEnds) is not { } unit)
        {
            return (offset, false);
        }
        if (unit.Start > offset && text.GetLineFromOffset(unit.Start) == text.GetLineFromOffset(offset))
        {
            // The blanks opening a line after an empty one are the white before its sentence.
            return (text.GetLineStart(text.GetLineFromOffset(offset)), true);
        }
        if (unit.Start > offset)
        {
            // An empty line before the last of its run goes with the sentence before the run, the blanks ending its line too.
            int line = text.GetLineFromOffset(offset);
            while (line > 0 && VimMotions.Blank(text, line - 1))
            {
                line--;
            }
            if (line == 0)
            {
                return (0, false);
            }
            (int Start, bool White) before = SentenceUnit(text, VimMotions.StepBack(text, text.GetLineStart(line)));
            return before.White && before.Start > 0 ? SentenceUnit(text, VimMotions.StepBack(text, before.Start)) : before;
        }
        bool white = VimMotions.LineOf(text, unit.Start).Length > 0;
        for (int p = unit.Start; p < unit.End && white; p++)
        {
            white = char.IsWhiteSpace(text[p]);
        }
        return (unit.Start, white);
    }

    /// <summary>A sentence's span ending at its own line's end where it runs to the next line's first column, but for an
    /// empty line, which it holds whole; whole lines to their last line's end.</summary>
    private static VimSpan OwnLine(TextSnapshot text, VimSpan span)
    {
        if (span.Linewise)
        {
            return new VimSpan(text.GetLineStart(text.GetLineFromOffset(span.Start)), text.GetLine(text.GetLineFromOffset(span.End)).End, false);
        }
        LineSpan last = VimMotions.LineOf(text, span.End);
        return span.End > span.Start && span.End == last.Start && last.Line > 0 && text.GetLine(last.Line - 1).Length > 0
            ? span with { End = Math.Max(span.Start, text.GetLine(last.Line - 1).End) } : span;
    }

    /// <summary>A block, a tag or a quoted string in visual mode: at a single place the one there; over a longer stretch
    /// a bracketed block as <see cref="VimTextObjects.BlockOver"/> finds it, and a tag or a string the one around its
    /// start or the first after it, a tag the one around that where the stretch already holds more than a character of
    /// it. An inner block of whole lines runs to its last line's break. With nothing inside, an inner block fails, an
    /// inner string takes its quotes, and an inner tag block takes its tags from its end tag's first character.</summary>
    private Region? Around(TextSnapshot text, Region r, string key, bool inner, int n, bool block)
    {
        bool single = r.Anchor == r.Cursor;
        int low = r.Low;
        int high = CharEnd(text, r.High);
        bool brackets = block && key != "t";
        if (low >= text.Length
            || (!single && brackets ? VimTextObjects.BlockOver(text, low, high, key, inner, n) : VimTextObjects.Of(text, single ? r.Cursor : low, key, inner, n, breakEnds))
                is not { } found)
        {
            return null;
        }
        (int start, int end) = Charwise(text, found);
        if (inner && end == start)
        {
            if (brackets && single && BreakAndIndent(text, start, key) is { } between)
            {
                // The break to the indent's first column, but from the bracket or that column one level out, past the cursor.
                return r.Cursor != between.Open && r.Cursor != between.Indent ? new Region(between.Indent, between.Break)
                    : Around(text, r, key, inner, n + 1, block) is { } outer && outer.Low != r.Cursor ? outer : null;
            }
            if (brackets)
            {
                return null;
            }
            if (single && !block)
            {
                return new Region(start, start - 1);
            }
            if (single && r.Cursor == start && VimTextObjects.Of(text, r.Cursor, key, inner: false, n, breakEnds) is { } tags)
            {
                (start, end) = Charwise(text, tags);
            }
        }
        if (!single && !block)
        {
            return QuotedOn(text, r, key[0], inner);
        }
        if (!single && key == "t" && end - start > 1 && start >= low && end <= high)
        {
            if (VimTextObjects.Of(text, low, key, inner, n + 1, breakEnds) is not { } outer)
            {
                return null;
            }
            (start, end) = Charwise(text, outer);
        }
        // An inner block whose closing bracket stands first on its line runs to the line before's break.
        bool toBreak = brackets && inner && !found.Linewise && end < text.Length && end == VimMotions.LineOf(text, end).End;
        return new Region(start, toBreak ? end : end > start ? VimMotions.StepBack(text, end) : start);
    }

    /// <summary>Where an inner block found empty at <paramref name="at"/> holds only a line break and an indent: its opening
    /// bracket ending the line before, that line's break and the indent's first place; null otherwise.</summary>
    private static (int Open, int Break, int Indent)? BreakAndIndent(TextSnapshot text, int at, string key)
    {
        (char open, char close) = key switch { "(" or ")" or "b" => ('(', ')'), "{" or "}" or "B" => ('{', '}'), "[" or "]" => ('[', ']'), _ => ('<', '>') };
        LineSpan line = VimMotions.LineOf(text, at);
        int first = TextNavigation.IndentEnd(text, line);
        return at == line.Start && line.Line > 0 && first > line.Start && first < line.End && text[first] == close
            && text.GetLine(line.Line - 1) is { Length: > 0 } before && text[before.End - 1] == open
            ? (before.End - 1, before.End, line.Start) : null;
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
