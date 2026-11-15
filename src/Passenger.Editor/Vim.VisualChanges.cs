using Passenger.Text;

namespace Passenger.Editor;

/// <summary>What visual mode's operators do to the stretches shown: delete, change, yank, shift, change case, replace,
/// join, put over them and insert before or after them, each at every stretch, a block line by line.</summary>
public sealed partial class Vim
{
    /// <summary>A change the last visual stretch makes for . to make again (<see cref="ChangeMade"/>): kept with what it took.</summary>
    private void Remember(Command command)
    {
        if (!replaying)
        {
            lastChange = new ChangeMade(command.Keys, command.Count, string.Empty, changeExtent);
        }
        changeExtent = null;
    }

    private VimResult VisualChange(Command command, string op, TextSnapshot text, IVimView view)
    {
        VimMode mode = Mode;
        bool block = mode == VimMode.VisualBlock;
        changeExtent = Extent(text, view);
        RememberVisual(text, view);
        int tab = view.Indent.TabWidth;
        (int left, int right) = block ? BlockSpan(text, regions[0], view) : (0, 0);
        int top = text.GetLineFromOffset(regions[0].Low);
        int bottom = text.GetLineFromOffset(regions[0].High);
        List<Region> taken = regions;
        Mode = VimMode.Normal;
        shownVisual = null;
        if (op is "x" or VimKeys.Delete)
        {
            op = "d";
        }
        else if (op == "s")
        {
            op = "c";
        }
        bool lines = mode == VimMode.VisualLine || op is "S" or "R" || (op is "X" or "Y" or "D" or "C" && !block);
        if (block && op is "D" or "C")
        {
            // A block's D and C take each line from the block's left to its end.
            right = int.MaxValue;
        }
        string basic = op switch { "X" or "D" => "d", "C" or "S" or "R" => "c", "Y" => "y", "~" => "g~", "u" => "gu", "U" => "gU", _ => op };
        Command acting = command with { Name = "visual" };
        if (basic != "y" && view.Refuses is { } why)
        {
            // Visual mode ends as it would with the change made, the carets at the stretches' starts.
            return Refused(why, block ? SelectionSet.Single(VimMotions.OffsetAtColumn(text, top, left, tab)) : RegionStretches(text, taken, lines).Item2);
        }
        if (block)
        {
            keptBlockLeft = left;
        }
        // A tab the block's edge cuts in two (r: any tab it takes) is first made the spaces it shows.
        EditPlan? split = block && !lines && (op is "I" or "A" or "r" || basic is "d" or "c" or "y")
            ? SplitTabs(text, top, bottom, left, right, tab, cutsLeft: op != "A", cutsRight: op != "I", inside: op == "r")
            : null;
        return split is null ? Acted(text) : AfterSplit(split, Acted(text.Apply(split.Edits)), text, keepsText: basic == "y" && op is not ("I" or "A" or "r"), tab);

        VimResult Acted(TextSnapshot text)
        {
            if (op is "I" or "A")
            {
                return VisualInsert(acting, op == "A", mode, taken, text, view, top, bottom, left, right);
            }
            if (op is "J" or "gJ")
            {
                Remember(acting);
                int n = Math.Max(2, taken.Max(r => text.GetLineFromOffset(r.High)) - taken.Min(r => text.GetLineFromOffset(r.Low)) + 1);
                SelectionSet starts = SelectionSet.Create([.. taken.Select(r => Selection.At(r.Low))], 0);
                if (Join(text, starts, block ? Math.Max(2, bottom - top + 1) : n, spaced: op == "J", Options.JoinSpaces) is { } joined)
                {
                    return new VimResult(true, Edits: [joined]);
                }
                // Nothing to join below the last line: visual mode ends all the same, the caret at the stretch's start.
                return new VimResult(true, block ? SelectionSet.Single(Settled(text, VimMotions.OffsetAtColumn(text, top, left, tab), breakEnds))
                    : mode == VimMode.VisualLine ? starts.With(c => Selection.At(VimMotions.LineOf(text, c.Caret).Start))
                    : starts.With(c => Selection.At(Settled(text, c.Caret, breakEnds))));
            }
            (List<Stretch> stretches, SelectionSet carets) = block && !lines
                ? BlockStretches(text, top, bottom, left, right, tab, perLine: basic == "c", lineEnds: basic == "c" || op is ">" or "p" or "P", emptyToo: op != ">")
                : RegionStretches(text, taken, lines);
            if (lines && !block && basic is "y" or "g~" or "gu" or "gU")
            {
                // Over lines whose cursor stands above where the stretch began, a yank or a change of case leaves the caret there.
                for (int i = 0; i < stretches.Count; i++)
                {
                    if (text.GetLineFromOffset(taken[i].Cursor) < text.GetLineFromOffset(taken[i].Anchor))
                    {
                        stretches[i] = stretches[i] with { Landing = taken[i].Cursor };
                    }
                }
            }
            blockChange = block && !lines && basic == "c" && stretches.Count > 0;
            if (stretches.Count == 0)
            {
                return new VimResult(true, carets);
            }
            if (op is ">" or "<")
            {
                Remember(acting);
                return block ? ShiftBlock(text, stretches, Math.Max(command.Count, 1), op == ">", view, top, left) : Shift(text, carets, stretches, op == ">", view, Math.Max(command.Count, 1));
            }
            if (op == "r")
            {
                Remember(acting);
                return ReplaceIn(text, stretches, carets, command.Argument!, view);
            }
            if (op is "p" or "P")
            {
                return PutOver(acting, op == "P", text, stretches, carets, block, view, top, bottom);
            }
            return Act(acting, basic, stretches, text, carets, view, block && !lines);
        }
    }

    /// <summary>The tabs of a block's lines that its left or right edge cuts in two (with <paramref name="inside"/>, every
    /// tab it takes) made as many spaces as the cells they show; null where there are none.</summary>
    private static EditPlan? SplitTabs(TextSnapshot text, int top, int bottom, int left, int right, int tab, bool cutsLeft, bool cutsRight, bool inside)
    {
        List<TextEdit> edits = [];
        for (int line = top; line <= bottom; line++)
        {
            if (BlockPiece(text, line, left, right, tab) is not { } piece)
            {
                continue;
            }
            for (int p = piece.Start; p < piece.End; p = TextNavigation.NextGrapheme(text, p))
            {
                if (text[p] != '\t')
                {
                    continue;
                }
                int from = TextNavigation.VisualColumn(text, p, tab);
                int to = TextNavigation.VisualColumn(text, p + 1, tab);
                if (inside || (cutsLeft && from < left) || (cutsRight && right != int.MaxValue && to > right + 1))
                {
                    edits.Add(new TextEdit(p, 1, new string(' ', to - from)));
                }
            }
        }
        return edits.Count == 0 ? null : new EditPlan(edits, SelectionSet.Single(edits[0].Start), EditKind.Other);
    }

    /// <summary>A block's change made after its tabs were split: the split as its first step, or for a yank, which leaves
    /// the text as it was, its carets carried back to the same columns of the text unsplit.</summary>
    private static VimResult AfterSplit(EditPlan split, VimResult result, TextSnapshot text, bool keepsText, int tab)
    {
        if (keepsText)
        {
            TextSnapshot spaced = text.Apply(split.Edits);
            return result.Selections is not { } carets ? result : result with
            {
                Selections = carets.With(c => Selection.At(VimMotions.OffsetAtColumn(text, spaced.GetLineFromOffset(c.Caret), TextNavigation.VisualColumn(spaced, c.Caret, tab), tab))),
            };
        }
        return result.Edits is { } edits ? result with { Edits = [split, .. edits] }
            : result with { Edits = [split with { Selections = result.Selections ?? split.Selections }], Selections = null };
    }

    /// <summary>How much the primary stretch takes, for . to take again.</summary>
    private VisualExtent Extent(TextSnapshot text, IVimView view)
    {
        Region r = regions[Math.Clamp(visualPrimary, 0, regions.Count - 1)];
        int lines = text.GetLineFromOffset(r.High) - text.GetLineFromOffset(r.Low);
        if (Mode == VimMode.VisualBlock)
        {
            (int left, int right) = BlockSpan(text, r, view);
            return new VisualExtent(Mode, lines, right == int.MaxValue ? 0 : right - left, blockColumns.ToEnd);
        }
        int columns = lines == 0 ? r.High - r.Low : TextNavigation.VisualColumn(text, r.High, view.Indent.TabWidth);
        return new VisualExtent(Mode, lines, columns, false);
    }

    /// <summary>A stretch as much as <paramref name="extent"/> from a caret, for . after a visual change; a block's
    /// columns set from it.</summary>
    private Region FromExtent(TextSnapshot text, int caret, VisualExtent extent, IVimView view)
    {
        int tab = view.Indent.TabWidth;
        int line = (int)Math.Min(text.GetLineFromOffset(caret) + (long)extent.Lines, VimMotions.LastLine(text, breakEnds));
        if (extent.Mode == VimMode.VisualBlock)
        {
            int column = TextNavigation.VisualColumn(text, caret, tab);
            blockColumns = (column, column + extent.Columns, extent.ToEnd);
            return new Region(caret, VimMotions.OffsetAtColumn(text, line, column + extent.Columns, tab));
        }
        if (extent.Mode == VimMode.VisualLine)
        {
            return new Region(caret, text.GetLineStart(line));
        }
        LineSpan end = text.GetLine(line);
        if (extent.Lines == 0)
        {
            return new Region(caret, Math.Min(caret + extent.Columns, end.End));
        }
        // Past the last line's end, the stretch takes its line break.
        return new Region(caret, TextNavigation.VisualColumn(text, end.End, tab) <= extent.Columns ? end.End : VimMotions.OffsetAtColumn(text, line, extent.Columns, tab));
    }

    /// <summary>The stretches of character or line regions, and a caret at each one's start (a line region's first line's).
    /// A character stretch ending on the last line's end leaves the file's final line break, which joins no line.</summary>
    private (List<Stretch>, SelectionSet) RegionStretches(TextSnapshot text, List<Region> taken, bool lines)
    {
        List<Stretch> stretches = [];
        List<Selection> carets = [];
        foreach (Region r in taken)
        {
            int first = text.GetLineFromOffset(r.Low);
            int last = text.GetLineFromOffset(r.High);
            if (lines)
            {
                int start = text.GetLineStart(first);
                stretches.Add(Whole(text, first, last, start) with { Landing = start });
                carets.Add(Selection.At(start));
                continue;
            }
            int end = Math.Min(CharEnd(text, r.High), text.GetLine(VimMotions.LastLine(text, breakEnds)).End);
            stretches.Add(new Stretch(r.Low, Math.Max(r.Low, end), false, first, last, r.Low, r.Low));
            carets.Add(Selection.At(r.Low));
        }
        return (stretches, SelectionSet.Create(carets, 0));
    }

    /// <summary>A block's part of each line from <paramref name="top"/> to <paramref name="bottom"/> that reaches it (with
    /// <paramref name="lineEnds"/>, an empty part at the end of a line ending where it begins, an empty line only where
    /// <paramref name="emptyToo"/>), and a caret at its top left, or one at each line's part (<paramref name="perLine"/>)
    /// for a change typed on every line.</summary>
    private (List<Stretch>, SelectionSet) BlockStretches(TextSnapshot text, int top, int bottom, int left, int right, int tab, bool perLine,
        bool lineEnds = false, bool emptyToo = false)
    {
        List<Stretch> stretches = [];
        List<Selection> carets = [];
        for (int line = top; line <= bottom; line++)
        {
            if (BlockPiece(text, line, left, right, tab) is not { } piece)
            {
                LineSpan span = text.GetLine(line);
                if (lineEnds && (emptyToo || span.Length > 0) && TextNavigation.VisualColumn(text, span.End, tab) == left)
                {
                    // A line ending just where the block begins takes what a change types or a shift adds there, at its end.
                    stretches.Add(new Stretch(span.End, span.End, false, line, line, span.End, span.End));
                    if (perLine)
                    {
                        carets.Add(Selection.At(span.End));
                    }
                }
                continue;
            }
            stretches.Add(new Stretch(piece.Start, piece.End, false, line, line, piece.Start, piece.Start));
            if (perLine || carets.Count == 0)
            {
                // The caret goes to the block's top left, at the end of a top line too short to reach it.
                carets.Add(Selection.At(perLine || line == top ? piece.Start : VimMotions.OffsetAtColumn(text, top, left, tab)));
            }
        }
        if (carets.Count == 0)
        {
            carets.Add(Selection.At(VimMotions.Settled(text, text.GetLine(top).End, breakEnds)));
        }
        return (stretches, SelectionSet.Create(carets, 0));
    }

    /// <summary>I and A in visual mode, before or after a block on each line it crosses (a short line padded to the
    /// block's right edge for A), or over characters or lines once, as Vim's do: with the cursor on a line above where
    /// the stretch began, I at the cursor and A at that line's start; otherwise I at the first line's start and A after
    /// the cursor's character, or on it after $, at a single place in the first column, or where the column it aims at
    /// is the one the last block began at, or past it from a line too short to reach that.</summary>
    private VimResult VisualInsert(Command command, bool after, VimMode mode, List<Region> taken, TextSnapshot text, IVimView view, int top, int bottom, int left, int right)
    {
        int tab = view.Indent.TabWidth;
        string how = after ? "A" : "I";
        if (mode != VimMode.VisualBlock)
        {
            List<Selection> inserts = [];
            for (int i = 0; i < taken.Count; i++)
            {
                Region r = taken[i];
                bool above = text.GetLineFromOffset(r.Cursor) < text.GetLineFromOffset(r.Anchor);
                LineSpan anchorLine = VimMotions.LineOf(text, r.Anchor);
                // A cursor j or k left past a short line's end stands on its last character here.
                int aimed = i < visualAims.Length ? visualAims[i] : -1;
                int cursor = aimed >= 0 && aimed != int.MaxValue ? Settled(text, r.Cursor, breakEnds) : r.Cursor;
                int column = CaretColumn(text, cursor, tab);
                int aim = aimed >= 0 ? aimed : column;
                bool onIt = r.Anchor == r.Cursor ? column == 0 : aim == keptBlockLeft || (aim > keptBlockLeft && column < keptBlockLeft);
                int place = !after ? (above ? r.Cursor : anchorLine.Start)
                    : above ? (keptBlockLeft == 0 || anchorLine.Length == 0 ? anchorLine.Start : TextNavigation.NextGrapheme(text, anchorLine.Start))
                    : aim == int.MaxValue ? LastCharOf(text, r.Cursor)
                    : onIt ? cursor
                    : Math.Min(CharEnd(text, r.Cursor), VimMotions.LineOf(text, r.Cursor).End);
                inserts.Add(Selection.At(place));
            }
            return Insert(text, SelectionSet.Create(inserts, 0), command, how);
        }
        List<TextEdit> pads = [];
        List<int> places = [];
        for (int line = top; line <= bottom; line++)
        {
            LineSpan span = text.GetLine(line);
            int width = TextNavigation.VisualColumn(text, span.End, tab);
            if (!after)
            {
                // A line ending just where the block begins takes it at its end; one ending before is passed over.
                if (width >= left)
                {
                    places.Add(width == left ? span.End : BlockPiece(text, line, left, right, tab)!.Value.Start);
                }
                continue;
            }
            if (right == int.MaxValue)
            {
                places.Add(span.End);
            }
            else if (width > right + 1)
            {
                places.Add(BlockPiece(text, line, left, right, tab)!.Value.End);
            }
            else
            {
                // A short line is padded with spaces to just past the block.
                int pad = right + 1 - width;
                pads.Add(new TextEdit(span.End, 0, new string(' ', pad)));
                places.Add(span.End);
            }
        }
        if (places.Count == 0)
        {
            Mode = VimMode.Normal;
            return VimResult.Held;
        }
        blockCorner = BlockPiece(text, top, left, right, tab)?.Start ?? text.GetLine(top).End;
        if (pads.Count == 0)
        {
            return Insert(text, SelectionSet.Create([.. places.Select(Selection.At)], 0), command, how);
        }
        List<Selection> carets = [];
        foreach (int place in places)
        {
            carets.Add(Selection.At(TextEditing.MapOffset(place, pads, true)));
        }
        return InsertAfter(text, new EditPlan(pads, SelectionSet.Create(carets, 0), EditKind.Other), command, how);
    }

    /// <summary>r in visual mode: every character the stretches take, line breaks aside, made the one typed.</summary>
    private VimResult ReplaceIn(TextSnapshot text, List<Stretch> stretches, SelectionSet carets, string with, IVimView view)
    {
        List<TextEdit> edits = [];
        foreach (Stretch s in Merged([.. stretches.OrderBy(s => s.Start)]))
        {
            for (int line = text.GetLineFromOffset(s.Start); line <= text.GetLineFromOffset(Math.Max(s.Start, s.End - 1)); line++)
            {
                LineSpan span = text.GetLine(line);
                int from = Math.Max(span.Start, s.Start);
                int to = Math.Min(span.End, s.End);
                if (to <= from)
                {
                    continue;
                }
                int count = 0;
                for (int p = from; p < to; p = TextNavigation.NextGrapheme(text, p))
                {
                    count++;
                }
                edits.Add(new TextEdit(from, to - from, with == "\n" ? view.LineEnding : string.Concat(Enumerable.Repeat(with, count))));
            }
        }
        if (edits.Count == 0)
        {
            return new VimResult(true, carets);
        }
        TextSnapshot after = text.Apply(edits);
        SelectionSet landed = carets.With(c => Selection.At(Settled(after, TextEditing.MapOffset(c.Caret, edits, false), breakEnds)));
        return new VimResult(true, Edits: [new EditPlan(edits, landed, EditKind.Other)]);
    }

    /// <summary>A block's &gt; and &lt; by columns. &gt; lays the blanks around its left edge out again (in spaces, or tabs
    /// where the file indents with them) as wide again as the shift; &lt; brings the first character after the blanks from
    /// the edge on back by the shift, no further than the edge, keeping the blanks that still fit and spacing the rest.</summary>
    private VimResult ShiftBlock(TextSnapshot text, List<Stretch> stretches, int times, bool right, IVimView view, int top, int left)
    {
        int width = view.Indent.Size * times;
        int tab = view.Indent.TabWidth;
        List<TextEdit> edits = [];
        foreach (Stretch s in stretches.OrderBy(s => s.Start))
        {
            LineSpan line = VimMotions.LineOf(text, s.Start);
            int from = s.Start;
            int to = s.Start;
            while (to < line.End && text[to] is ' ' or '\t')
            {
                to++;
            }
            if (right)
            {
                while (from > line.Start && text[from - 1] is ' ' or '\t')
                {
                    from--;
                }
                int start = TextNavigation.VisualColumn(text, from, tab);
                edits.Add(new TextEdit(from, to - from, Blanks(start, TextNavigation.VisualColumn(text, to, tab) - start + width, view.Indent)));
                continue;
            }
            if (to == from)
            {
                continue;
            }
            int first = TextNavigation.VisualColumn(text, to, tab);
            int target = first - Math.Min(width, first - left);
            int kept = from;
            while (kept < to && TextNavigation.VisualColumn(text, kept + 1, tab) <= target)
            {
                kept++;
            }
            int reached = TextNavigation.VisualColumn(text, kept, tab);
            if (kept < to)
            {
                edits.Add(new TextEdit(kept, to - kept, new string(' ', Math.Max(0, target - reached))));
            }
        }
        // The caret goes to the block's top left, on the blanks > added at the end of a top line reaching just that far.
        if (edits.Count == 0)
        {
            return new VimResult(true, SelectionSet.Single(Settled(text, VimMotions.OffsetAtColumn(text, top, left, tab), breakEnds)));
        }
        TextSnapshot after = text.Apply(edits);
        return new VimResult(true, Edits: [new EditPlan(edits, SelectionSet.Single(Settled(after, VimMotions.OffsetAtColumn(after, top, left, tab), breakEnds)), EditKind.Other)]);
    }

    /// <summary>Blanks from a display column as wide as <paramref name="columns"/>: tabs to each stop they reach and spaces
    /// after where the file indents with tabs, else spaces alone.</summary>
    private static string Blanks(int start, int columns, IndentOptions indent)
    {
        if (!indent.UseTabs || indent.TabWidth <= 0)
        {
            return new string(' ', columns);
        }
        System.Text.StringBuilder blanks = new();
        int at = start;
        int end = start + columns;
        while ((at / indent.TabWidth + 1) * indent.TabWidth <= end)
        {
            blanks.Append('\t');
            at = (at / indent.TabWidth + 1) * indent.TabWidth;
        }
        return blanks.Append(' ', end - at).ToString();
    }

    /// <summary>p and P over a visual stretch: the register's text in place of what is shown, which p keeps in the
    /// unnamed register and P doesn't; whole lines put over characters go on lines of their own. From an empty register
    /// the stretch is deleted alone, as a d into the unnamed register (for P into none), which . makes again.</summary>
    private VimResult PutOver(Command command, bool keep, TextSnapshot text, List<Stretch> stretches, SelectionSet carets, bool block, IVimView view, int top, int bottom)
    {
        if (VimRegisters.IsClipboard(command.Register))
        {
            Remember(command);
            return new VimResult(true, carets);
        }
        if (Registers.Get(command.Register) is not { } value)
        {
            char? into = keep ? '_' : null;
            return Act(command with { Keys = into is { } none ? ["\"", none.ToString(), "d"] : ["d"], Register = into }, "d", stretches, text, carets, view, block);
        }
        Remember(command);
        stretches.Sort((a, b) => a.Start.CompareTo(b.Start));
        VimRegister replaced = new([.. Merged(stretches, pieces: true).Select(m => Taken(text, m))], stretches[0].Lines) { Blockwise = block };
        string eol = view.LineEnding;
        int times = Math.Clamp(command.Count, 1, Math.Max(1, MostRepeatedText / Math.Max(1, value.Text.Length)));
        if (block && value.Linewise)
        {
            // P over a block whose top line's part ran to that line's end, past text it leaves, puts the lines below that line.
            Stretch first = stretches[0];
            bool belowTop = keep && first.First == top && first.End > first.Start && first.End == text.GetLine(top).End && first.Start > text.GetLine(top).Start;
            return PutLinesBy(value, keep, keep && !belowTop, text, stretches, top, belowTop ? top : bottom, eol, replaced, times);
        }
        if (block && value.Blockwise)
        {
            // A block put over a block: the block deleted, the register's put in its place from its top left.
            List<TextEdit> deletes = [.. stretches.Where(t => t.End > t.Start).Select(t => new TextEdit(t.Start, t.End - t.Start, string.Empty))];
            int corner = stretches[0].Start;
            TextSnapshot cleared = text.Apply(deletes);
            VimResult put = PutBlock(value, before: true, times, cleared, SelectionSet.Single(TextEditing.MapOffset(corner, deletes, false)), view, cursorAfter: false);
            if (!keep)
            {
                Registers.Delete(null, replaced, always: false);
            }
            return new VimResult(true, Edits: [new EditPlan(deletes, SelectionSet.Single(corner), EditKind.Other), .. put.Edits ?? []]);
        }
        if (block && !value.Text.Contains('\n', StringComparison.Ordinal))
        {
            // Text of one line put over a block takes each line's part of it, the caret on its last character on the first.
            string each = string.Concat(Enumerable.Repeat(value.Text, times));
            List<TextEdit> pieces = [.. stretches.Select(t => new TextEdit(t.Start, t.End - t.Start, each))];
            TextSnapshot filled = text.Apply(pieces);
            int lastOfFirst = stretches[0].Start + Math.Max(0, each.Length - 1);
            if (!keep)
            {
                Registers.Delete(null, replaced, always: false);
            }
            return new VimResult(true, Edits: [new EditPlan(pieces, SelectionSet.Single(Settled(filled, lastOfFirst, breakEnds)), EditKind.Other)]);
        }
        List<TextEdit> edits = [];
        List<int> landed = [];
        bool dealt = stretches.Count > 1 && value.Parts.Count == stretches.Count && !block;
        int shift = 0;
        for (int i = 0; i < stretches.Count; i++)
        {
            Stretch s = stretches[i];
            if (block && i > 0)
            {
                edits.Add(new TextEdit(s.Start, s.End - s.Start, string.Empty));
                shift -= s.End - s.Start;
                continue;
            }
            string one = LineEndings.In(dealt ? value.Parts[i] : value.Text, eol);
            // A count puts the text that many times over, lines one under another over lines.
            string piece = s.Lines && !value.Linewise ? string.Join(eol, Enumerable.Repeat(one, times)) : string.Concat(Enumerable.Repeat(one, times));
            string body = value.Linewise && piece.EndsWith(eol, StringComparison.Ordinal) ? piece[..^eol.Length] : piece;
            string put;
            int landing;
            if (s.Lines)
            {
                // Over lines, the register's text takes their place as lines of its own.
                bool breakBefore = s.Start < text.GetLineStart(s.First);
                bool breakAfter = !breakBefore && s.End > text.GetLine(s.Last).End;
                put = breakBefore ? eol + body : breakAfter ? body + eol : body;
                landing = breakBefore ? eol.Length : 0;
            }
            else if (value.Linewise)
            {
                // Lines put over characters go on lines of their own between what stays.
                put = eol + piece;
                landing = eol.Length;
            }
            else
            {
                put = piece;
                landing = value.Blockwise || piece.Contains('\n', StringComparison.Ordinal) ? 0 : Math.Max(0, piece.Length - 1);
            }
            edits.Add(new TextEdit(s.Start, s.End - s.Start, put));
            landed.Add(s.Start + shift + landing);
            shift += put.Length - (s.End - s.Start);
        }
        TextSnapshot after = text.Apply(edits);
        bool lines = value.Linewise || stretches[0].Lines;
        List<Selection> putCarets = [.. landed.Select(p => Selection.At(!lines ? Settled(after, p, breakEnds)
            : value.Linewise ? VimMotions.FirstNonBlank(after, after.GetLineFromOffset(Math.Min(p, after.Length)), breakEnds) : after.GetLineStart(after.GetLineFromOffset(Math.Min(p, after.Length)))))];
        if (!keep)
        {
            Registers.Delete(null, replaced, always: false);
        }
        return new VimResult(true, Edits: [new EditPlan(edits, SelectionSet.Create(putCarets, 0), EditKind.Other)]);
    }

    /// <summary>Whole lines put over a block: the block deleted, the lines put above its top line (<paramref name="above"/>)
    /// or below <paramref name="bottom"/>, <paramref name="times"/> over, the caret on the first one's first non-blank;
    /// the register kept where <paramref name="keep"/>.</summary>
    private VimResult PutLinesBy(VimRegister value, bool keep, bool above, TextSnapshot text, List<Stretch> stretches, int top, int bottom, string eol, VimRegister replaced, int times)
    {
        string lines = string.Concat(Enumerable.Repeat(LineEndings.In(value.Text, eol), times));
        LineSpan end = text.GetLine(bottom);
        (int at, string put) = above ? (text.GetLineStart(top), lines)
            : end.BreakLength > 0 ? (end.EndIncludingBreak, lines) : (end.End, eol + lines[..^eol.Length]);
        List<TextEdit> edits = [.. stretches.Where(t => t.End > t.Start).Select(t => new TextEdit(t.Start, t.End - t.Start, string.Empty))];
        int merged = edits.FindIndex(e => e.Start == at);
        if (merged >= 0)
        {
            edits[merged] = edits[merged] with { Text = put };
        }
        else
        {
            edits.Add(new TextEdit(at, 0, put));
            edits.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
        TextSnapshot after = text.Apply(edits);
        int first = after.GetLineFromOffset(Math.Min(TextEditing.MapOffset(at, edits, false) + (above || end.BreakLength > 0 ? 0 : eol.Length), after.Length));
        if (!keep)
        {
            Registers.Delete(null, replaced, always: false);
        }
        return new VimResult(true, Edits: [new EditPlan(edits, SelectionSet.Single(VimMotions.FirstNonBlank(after, first, breakEnds)), EditKind.Other)]);
    }

    /// <summary>A block's put: its lines one below another from the caret's column (after the caret's character for p),
    /// each padded to the block's width where text follows, a line too short padded out to the column, lines added past
    /// the text's end.</summary>
    private VimResult PutBlock(VimRegister value, bool before, int count, TextSnapshot text, SelectionSet selections, IVimView view, bool cursorAfter)
    {
        int tab = view.Indent.TabWidth;
        string eol = view.LineEnding;
        int caret = selections.Primary.Caret;
        LineSpan home = VimMotions.LineOf(text, caret);
        int column = TextNavigation.VisualColumn(text, caret, tab) + (before || home.Length == 0 ? 0 : CellsOf(text, caret, tab));
        int[] widths = [.. value.Parts.Select(p => TextNavigation.VisualColumn(TextSnapshot.From(p), p.Length, tab))];
        int width = widths.Max();
        int times = Math.Clamp(count, 1, Math.Max(1, MostRepeatedText / Math.Max(1, width * value.Parts.Count)));
        List<TextEdit> edits = [];
        int last = VimMotions.LastLine(text, breakEnds);
        int firstAt = 0;
        int lastAt = 0;
        int shift = 0;
        string added = string.Empty;
        for (int i = 0; i < value.Parts.Count; i++)
        {
            string padded = value.Parts[i] + new string(' ', width - widths[i]);
            int line = home.Line + i;
            int at;
            string lead;
            bool followed;
            if (line > last)
            {
                at = text.Length;
                lead = (text.Length > 0 && text.GetLine(text.LineCount - 1).Length == 0 && text.LineCount - 1 > last && added.Length == 0 ? string.Empty : eol) + new string(' ', column);
                followed = false;
            }
            else
            {
                LineSpan span = text.GetLine(line);
                int lineWidth = TextNavigation.VisualColumn(text, span.End, tab);
                if (lineWidth <= column)
                {
                    (at, lead, followed) = (span.End, new string(' ', column - lineWidth), false);
                }
                else
                {
                    at = span.Start;
                    while (at < span.End && TextNavigation.VisualColumn(text, at, tab) < column)
                    {
                        at = TextNavigation.NextGrapheme(text, at);
                    }
                    (lead, followed) = (string.Empty, true);
                }
            }
            string put = lead + string.Concat(Enumerable.Repeat(padded, times - 1)) + (followed ? padded : value.Parts[i]);
            if (line > last)
            {
                added += put;
                continue;
            }
            edits.Add(new TextEdit(at, 0, put));
            if (i == 0)
            {
                firstAt = at + shift + lead.Length;
            }
            lastAt = at + shift + lead.Length;
            shift += put.Length;
        }
        if (added.Length > 0)
        {
            edits.Add(new TextEdit(text.Length, 0, added));
            if (edits.Count == 1)
            {
                firstAt = text.Length + added.IndexOf(value.Parts[0], StringComparison.Ordinal);
            }
            lastAt = text.Length + shift + added.Length - value.Parts[^1].Length;
        }
        TextSnapshot after = text.Apply(edits);
        int landing = cursorAfter ? Settled(after, Math.Min(lastAt + (width * times), VimMotions.LineOf(after, lastAt).End), breakEnds) : Settled(after, firstAt, breakEnds);
        return new VimResult(true, Edits: [new EditPlan(edits, SelectionSet.Single(landing), EditKind.Other)]);
    }
}
