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

    /// <summary>Where . types again what I or A over characters or lines typed: at the caret, as I did (and A over a
    /// cursor above where the stretch began, unless its last block began past the first column, or on a single place in
    /// the first column), else as many lines below as the stretch had, at the caret's place in its line or, as A after $
    /// did, that line's last character, past it where the columns the last visual change kept widen.</summary>
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
    /// lines places itself by: a block's edges; after $ those before kept; none by lines, or by characters for I or A,
    /// taken back over lines; else for I or A by characters, and lines taken back on one line, from the first column to
    /// the cursor's; else over lines or by lines from the first column (for I or A, or a block's S or R, over lines the
    /// left one kept) to the end's; else its corners'.</summary>
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
        else if (keeps && Mode == VimMode.VisualBlock)
        {
            blockColumns = (TextNavigation.VisualColumn(text, regions[0].Anchor, tab), TextNavigation.VisualColumn(text, regions[0].Cursor, tab), false);
        }
        else if (key != "p" && !(keeps && key == "s"))
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

    /// <summary>is and as in visual mode: at a single place the sentence there, one no bigger with the white after it,
    /// none from the place before to there; over a longer stretch its cursor carried to the end of the sentences after it
    /// (onto an empty line a sentence follows, short of the text's last), or back a sentence or white at a time from the
    /// one before where the cursor begins its own, from an empty line before the last of its run to that line first; as
    /// takes a white with the sentence before it, a sentence with the white before where the cursor stood in or after it.</summary>
    private Region? SentencesOver(TextSnapshot text, Region r, bool inner, int n)
    {
        if (r.Anchor == r.Cursor)
        {
            if (VimTextObjects.Of(text, r.Cursor, "s", inner, n, breakEnds) is not { } span)
            {
                return null;
            }
            int lastLine = VimMotions.LastLine(text, breakEnds);
            if (inner && n == 1 && VimMotions.LineOf(text, r.Cursor) is { Length: 0 } inRun && inRun.Line < lastLine && text.GetLine(inRun.Line + 1).Length == 0)
            {
                // From empty lines in a run the text goes on after, is takes them to the run's last.
                int runLast = inRun.Line + 1;
                while (runLast < lastLine && text.GetLine(runLast + 1).Length == 0)
                {
                    runLast++;
                }
                if (runLast < lastLine)
                {
                    return new Region(r.Cursor, text.GetLineStart(runLast));
                }
            }
            if (VimMotions.LineOf(text, r.Cursor) is { Length: 0 } onEmpty && onEmpty.Line == lastLine && onEmpty.Line > 0
                && text.GetLine(onEmpty.Line - 1) is var above)
            {
                int blanks = above.End;
                while (blanks > above.Start && text[blanks - 1] is ' ' or '\t')
                {
                    blanks--;
                }
                // From the last empty line: as from before its sentence to it, is the blanks before it (one taking it too).
                if (!inner && span.End > span.Start)
                {
                    return new Region(span.Start > 0 ? CharacterBefore(text, span.Start) : 0, r.Cursor);
                }
                if (inner && blanks < above.End)
                {
                    return new Region(blanks, above.End - blanks == 1 ? r.Cursor : above.End - 1);
                }
            }
            if (span.End == span.Start)
            {
                // Nothing found: from the place before it to it.
                return new Region(span.Start, span.Start > 0 ? CharacterBefore(text, span.Start) : span.Start);
            }
            Region found = Spanned(text, OwnLine(text, span));
            // A sentence no bigger than the character already shown takes the white after it too.
            return found.Anchor == r.Cursor && found.Cursor == r.Cursor && r.Cursor < text.Length && text[r.Cursor] is not (' ' or '\t')
                && VimTextObjects.Of(text, r.Cursor, "s", inner, n + 1, breakEnds) is { } more
                ? Spanned(text, OwnLine(text, more)) : found;
        }
        if (r.Cursor < r.Anchor)
        {
            int at = r.Cursor;
            int left = n;
            if (VimTextObjects.Of(text, at, "s", inner: true, 1, breakEnds) is { } run && run.Start > at && VimMotions.NextSentenceStart(text, run.Start) < text.Length)
            {
                // An empty line before the last of its run, a sentence following, steps on to that last line first.
                at = run.Start;
                left--;
            }
            // A line break alone between two sentences is no white: each is a step of its own.
            for (; left > 0 && at > 0; left--)
            {
                (int Start, bool White) here = SentenceUnit(text, at);
                bool inside = here.Start < at;
                (int start, bool white) = inside ? here : SentenceUnit(text, VimMotions.StepBack(text, at));
                at = start;
                if (!inner && (white || inside || here.White) && at > 0 && SentenceUnit(text, VimMotions.StepBack(text, at)) is var before && before.White != white)
                {
                    at = before.Start;
                }
            }
            return at != r.Cursor ? r with { Cursor = at } : null;
        }
        int after = CharEnd(text, r.Cursor);
        LineSpan line = VimMotions.LineOf(text, after);
        if (after == line.End && line.Length > 0 && line.BreakLength > 0 && line.EndIncludingBreak < text.Length)
        {
            // A sentence ending its line holds its line break: the next one is taken from the next line.
            after = line.EndIncludingBreak;
        }
        if (after >= text.Length || VimTextObjects.Of(text, after, "s", inner, n, breakEnds) is not { } next)
        {
            return null;
        }
        VimSpan own = OwnLine(text, next);
        if (own.End == own.Start && VimMotions.NextSentenceStart(text, own.Start) < text.Length)
        {
            return own.Start > r.Cursor ? r with { Cursor = own.Start } : null;
        }
        // Of the empty lines ending the text, the last is none to go to.
        return own.End > after ? r with { Cursor = VimMotions.StepBack(text, own.End) } : null;
    }

    /// <summary>Where the sentence or the white between two that an offset is in begins, as is takes it there, and
    /// whether it is the white (an empty line being a sentence, but one before the last of its run).</summary>
    private (int Start, bool White) SentenceUnit(TextSnapshot text, int offset)
    {
        if (VimTextObjects.Of(text, offset, "s", inner: true, 1, breakEnds) is not { } unit)
        {
            return (offset, false);
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

    /// <summary>A sentence's span ending at its own line's end where it runs to the next line's first column; whole lines
    /// to their last line's end.</summary>
    private static VimSpan OwnLine(TextSnapshot text, VimSpan span)
    {
        if (span.Linewise)
        {
            return new VimSpan(text.GetLineStart(text.GetLineFromOffset(span.Start)), text.GetLine(text.GetLineFromOffset(span.End)).End, false);
        }
        LineSpan last = VimMotions.LineOf(text, span.End);
        return span.End > span.Start && span.End == last.Start && last.Line > 0 ? span with { End = Math.Max(span.Start, text.GetLine(last.Line - 1).End) } : span;
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
