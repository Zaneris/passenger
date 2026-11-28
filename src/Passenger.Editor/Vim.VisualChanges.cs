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

    /// <summary>A visual operator, one that writes into an emptied text writing on the one empty line it holds.</summary>
    private VimResult VisualChange(Command command, string op, TextSnapshot text, IVimView view) =>
        text.Length == 0 && breakEnds && view.Refuses is null && op is "p" or "P" or "c" or "s" or "C" or "S" or "R" or "I" or "A"
            ? OnOneEmptyLine((lined, _) => VisualChangeOn(command, op, lined, view), text, view)
            : VisualChangeOn(command, op, text, view);

    private VimResult VisualChangeOn(Command command, string op, TextSnapshot text, IVimView view)
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
        // Any operator keeps the columns its stretch spans for the next A to place itself by (keptBlockLeft), as . makes a block's again.
        (int Left, int Right) kept = (keptBlockLeft, keptBlockRight);
        bool toEnd = visualAims.Length > 0 && visualAims[Math.Clamp(visualPrimary, 0, visualAims.Length - 1)] == int.MaxValue;
        Region stretch = regions[0];
        bool overLines = text.GetLineFromOffset(stretch.Low) != text.GetLineFromOffset(stretch.High);
        bool inserts = op is "I" or "A";
        // A block reaching each line's end over lines too short to reach keeps no width.
        bool reachesNothing = right == int.MaxValue && Enumerable.Range(top, bottom - top + 1).All(l => TextNavigation.VisualColumn(text, text.GetLine(l).End, tab) <= left);
        (keptBlockLeft, keptBlockRight) = block && !lines ? (left, reachesNothing ? left : right)
            : toEnd || replaying ? (keptBlockLeft, keptBlockRight)
            : inserts && !lines ? (overLines ? keptBlockLeft : 0, stretch.Cursor < stretch.Anchor && overLines ? 0 : CaretColumn(text, stretch.Cursor, tab))
            : lines || overLines
                ? (overLines ? keptBlockLeft : 0,
                    stretch.Cursor >= stretch.Anchor || !lines ? CaretColumn(text, stretch.High, tab) : !overLines ? CaretColumn(text, stretch.Cursor, tab) : 0)
            : (TextNavigation.VisualColumn(text, stretch.Low, tab), CaretColumn(text, stretch.High, tab));
        // A tab or other character of several columns the block's edge cuts in two (r: any it takes) is first made the spaces it shows.
        EditPlan? split = block && !lines && (op is "I" or "A" or "r" or "p" or "P" or "<" || basic is "d" or "c" or "y")
            ? SplitCells(text, top, bottom, left, right, tab, cutsLeft: op != "A", cutsRight: op is not ("I" or "<"), inside: op == "r", tabsLeftAlone: op == "I")
            : null;
        if (split is not null && op == "r" && BlockPiece(text, top, left, right, tab) is { } cutTop && TextNavigation.VisualColumn(text, cutTop.Start, tab) < left)
        {
            // r leaves the cursor where the character its edge cut in two began, as Vim's does.
            VimResult replaced = AfterSplit(split, Acted(text.Apply(split.Edits)), text, keepsText: false, tab);
            int at = TextEditing.MapOffset(cutTop.Start, split.Edits, afterInsertions: false);
            return replaced.Edits is [.., var lastPlan] ? replaced with { Edits = [.. replaced.Edits.Take(replaced.Edits.Count - 1), lastPlan with { Selections = SelectionSet.Single(at) }] } : replaced;
        }
        return split is null ? Acted(text) : AfterSplit(split, Acted(text.Apply(split.Edits)), text, keepsText: basic == "y" && op is not ("I" or "A" or "r"), tab);

        VimResult Acted(TextSnapshot text)
        {
            if (op is "I" or "A")
            {
                return VisualInsert(acting, op == "A", mode, taken, text, view, top, bottom, left, right, kept);
            }
            if (op is "J" or "gJ")
            {
                Remember(acting);
                int n = Math.Max(2, taken.Max(r => text.GetLineFromOffset(r.High)) - taken.Min(r => text.GetLineFromOffset(r.Low)) + 1);
                SelectionSet starts = SelectionSet.Create([.. taken.Select(r => Selection.At(r.Low))], 0);
                if (Join(text, starts, block ? Math.Max(2, bottom - top + 1) : n, view, spaced: op == "J", Options.JoinSpaces) is { } joined)
                {
                    int startColumn = TextNavigation.VisualColumn(text, stretch.Anchor, tab);
                    if (block && Math.Max(bottom, top + 1) == VimMotions.LastLine(text, breakEnds) && text.GetLineFromOffset(stretch.Cursor) == top
                        && joined.Selections.Primary.Caret == stretch.Cursor && startColumn < TextNavigation.VisualColumn(text, stretch.Cursor, tab)
                        && VimMotions.OffsetAtColumn(text, top, startColumn, tab) is var onTop && TextNavigation.VisualColumn(text, onTop, tab) == startColumn
                        && (onTop >= text.Length || text[onTop] != '\t'))
                    {
                        // Joining the last line, a caret left where the block's cursor stood, right of where it began, aims j, k and yy there.
                        (wantedFor, wanted) = (joined.Selections, [startColumn]);
                    }
                    else if (block && toEnd && bottom == top + 1 && bottom == VimMotions.LastLine(text, breakEnds) && text.GetLineFromOffset(stretch.Anchor) == bottom
                        && TextNavigation.IndentEnd(text, text.GetLine(bottom)) < text.GetLine(bottom).End
                        && TextNavigation.VisualColumn(text, stretch.Anchor, tab) is var anchor && anchor < CaretColumn(text.Apply(joined.Edits), joined.Selections.Primary.Caret, tab)
                        && VimMotions.OffsetAtColumn(text, top, anchor, tab) is var above && TextNavigation.VisualColumn(text, above, tab) == anchor
                        && (above >= text.Length || text[above] != '\t'))
                    {
                        // Begun in the last line's text, a block to the lines' ends aims j and k at its start's column, where a tab above holds none of it.
                        (wantedFor, wanted) = (joined.Selections, [anchor]);
                    }
                    return new VimResult(true, Edits: [joined]);
                }
                // Nothing to join: visual mode ends all the same, the caret at the stretch's start, or where . found it.
                if (replaying)
                {
                    // Made again by characters over lines the text no longer holds, the caret goes where the stretch was cut back to.
                    return new VimResult(true, SelectionSet.Create([.. taken.Select(r => Selection.At(Settled(text, mode == VimMode.Visual && r.Cursor < r.Anchor
                        ? r.Cursor : r.Anchor, breakEnds)))], 0));
                }
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
                if (basic is not ("y" or "c"))
                {
                    // Taking nothing, the change is still the one . makes again.
                    Remember(acting);
                }
                // A block with nothing in its columns still yanks and changes, and over more than one line deletes, a piece for each line.
                if (block && basic == "y")
                {
                    Registers.Yank(command.Register, BlockTaken(text, stretches, (top, bottom, left, right), tab));
                }
                else if (block && text.Length > 0 && (basic == "c" || (bottom > top && basic == "d")))
                {
                    Registers.Delete(command.Register, BlockTaken(text, stretches, (top, bottom, left, right), tab), always: false);
                }
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
                return PutOver(acting, op == "P", text, stretches, carets, block, view, (top, bottom, left, right), block ? text.GetLineFromOffset(taken[0].Cursor) : bottom);
            }
            return Act(acting, basic, stretches, text, carets, view, block && !lines, (top, bottom, left, right));
        }
    }

    /// <summary>The tabs and other characters of several columns (wide ones, control characters' marks) of a block's
    /// lines that its left or right edge cuts in two (with <paramref name="inside"/>, every such it takes; with <paramref
    /// name="tabsLeftAlone"/>, only tabs at the left) made as many spaces as the cells they show; null where there are none.</summary>
    private static EditPlan? SplitCells(TextSnapshot text, int top, int bottom, int left, int right, int tab, bool cutsLeft, bool cutsRight, bool inside,
        bool tabsLeftAlone = false)
    {
        List<TextEdit> edits = [];
        for (int line = top; line <= bottom; line++)
        {
            if (BlockPiece(text, line, left, right, tab) is not { } piece)
            {
                continue;
            }
            for (int p = piece.Start, next, from = TextNavigation.VisualColumn(text, p, tab), to; p < piece.End; p = next, from = to)
            {
                next = TextNavigation.NextGrapheme(text, p);
                to = TextNavigation.ColumnAfter(text, p, next, from, tab);
                if ((text[p] == '\t' || to - from > 1)
                    && (inside || (cutsLeft && from < left && (text[p] == '\t' || !tabsLeftAlone)) || (cutsRight && right != int.MaxValue && to > right + 1)))
                {
                    edits.Add(new TextEdit(p, next - p, new string(' ', to - from)));
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
        int tab = view.Indent.TabWidth;
        // In a line, the cells from the first character's first to the last's last; over lines the last's last, a tab's first.
        int columns = lines == 0 ? TextNavigation.VisualColumn(text, r.High, tab) + CellsOf(text, r.High, tab) - 1 - TextNavigation.VisualColumn(text, r.Low, tab)
            : TextNavigation.VisualColumn(text, r.High, tab) + (r.High < text.Length && text[r.High] == '\t' ? 0 : CellsOf(text, r.High, tab) - 1);
        // A stretch $ took to its lines' ends takes . to them too.
        bool toEnd = Mode == VimMode.Visual && visualAims.Length == regions.Count && visualAims[Math.Clamp(visualPrimary, 0, regions.Count - 1)] == int.MaxValue;
        return new VisualExtent(Mode, lines, columns, toEnd);
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
            blockColumns = (column, column + extent.Columns, extent.ToEnd || againToEnd);
            blockAgain = (column, column + extent.Columns);
            return new Region(caret, VimMotions.OffsetAtColumn(text, line, column + extent.Columns, tab));
        }
        if (extent.Mode == VimMode.VisualLine)
        {
            return new Region(caret, text.GetLineStart(line));
        }
        LineSpan end = text.GetLine(line);
        if (extent.Lines == 0 && !extent.ToEnd)
        {
            // As many cells on from the caret's column (a tab's last), to the character holding the last of them.
            int last = TextNavigation.VisualColumn(text, caret, tab) + (caret < end.End && text[caret] == '\t' ? CellsOf(text, caret, tab) - 1 : 0) + extent.Columns;
            return new Region(caret, TextNavigation.VisualColumn(text, end.End, tab) <= last ? end.End : Math.Max(caret, VimMotions.OffsetAtColumn(text, line, last, tab)));
        }
        if (extent.Lines == 0 && CaretColumn(text, caret, tab) >= 2)
        {
            // A line's stretch $ took to its end is taken again from past the line's second column as its start to the caret.
            return new Region(end.Start, caret);
        }
        // Past the last line's end, the stretch takes its line break.
        return new Region(caret, extent.ToEnd || TextNavigation.VisualColumn(text, end.End, tab) <= extent.Columns ? end.End : VimMotions.OffsetAtColumn(text, line, extent.Columns, tab));
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
    /// the cursor's character, or on it after $, in the first column of the stretch's first line, or on a later line
    /// reaching the column the last block began at where the column it aims at is that one, or past it from short of it.</summary>
    private VimResult VisualInsert(Command command, bool after, VimMode mode, List<Region> taken, TextSnapshot text, IVimView view, int top, int bottom, int left, int right,
        (int Left, int Right) kept)
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
                LineSpan cursorLine = VimMotions.LineOf(text, cursor);
                // On one line the cursor's own column counts, past a one-character line's end too, a character of several columns its last.
                bool onIt = cursorLine.Line == anchorLine.Line ? CaretColumn(text, r.Cursor, tab) == 0
                        && (r.Cursor >= cursorLine.End || TextNavigation.ColumnAfter(text, r.Cursor, TextNavigation.NextGrapheme(text, r.Cursor), 0, tab) == 1)
                    : kept.Left <= TextNavigation.VisualColumn(text, cursorLine.End, tab) && (aim == kept.Left || (aim > kept.Left && column < kept.Left));
                // Back over lines A passes the first character where the kept columns start past it, or after $ differ.
                bool afterFirst = above && (aim == int.MaxValue ? kept.Left != kept.Right : kept.Left != 0) && anchorLine.Length > 0;
                int place = !after ? (above ? r.Cursor : anchorLine.Start)
                    : above ? (afterFirst ? TextNavigation.NextGrapheme(text, anchorLine.Start) : anchorLine.Start)
                    : aim == int.MaxValue ? (kept.Left == kept.Right ? Settled(text, r.Cursor, breakEnds)
                        : Math.Min(CharEnd(text, Settled(text, r.Cursor, breakEnds)), VimMotions.LineOf(text, r.Cursor).End))
                    : onIt ? Settled(text, cursor, breakEnds)
                    : Math.Min(CharEnd(text, r.Cursor), VimMotions.LineOf(text, r.Cursor).End);
                inserts.Add(Selection.At(place));
                if (i == Math.Clamp(visualPrimary, 0, taken.Count - 1) && changeExtent is { } extent)
                {
                    changeExtent = extent with
                    {
                        Again = !after ? InsertAgain.AtCursor
                            : aim == int.MaxValue ? InsertAgain.OnLastBelow
                            : InsertAgain.AtColumnBelow,
                    };
                }
            }
            return Insert(text, SelectionSet.Create(inserts, 0), command, how);
        }
        List<TextEdit> pads = [];
        List<int> places = [];
        List<int> padded = [];
        List<int> cutPads = [];
        blockCutRest = null;
        for (int line = top; line <= bottom; line++)
        {
            LineSpan span = text.GetLine(line);
            int width = TextNavigation.VisualColumn(text, span.End, tab);
            if (!after)
            {
                // A line ending just where the block begins takes it at its end; one ending before is passed over.
                if (width >= left)
                {
                    int place = width == left ? span.End : BlockPiece(text, line, left, right, tab)!.Value.Start;
                    int cutPad = place < span.End && TextNavigation.VisualColumn(text, place, tab) is var cut && cut < left ? left - cut : 0;
                    if (cutPad > 0 && line == top)
                    {
                        // Where it cuts one on its first line, the text goes before that character there, the lines below taking what is typed past the columns cut.
                        blockCutRest = PlacesBelowCut(text, top + 1, bottom, left, right, tab) is { } below ? (cutPad, below) : null;
                        (places, padded, cutPads) = ([place], [0], [0]);
                        break;
                    }
                    if (cutPad > 0)
                    {
                        // A character of several columns the block's edge cuts stays whole after the text, spaces before it to the edge.
                        pads.Add(new TextEdit(place, 0, new string(' ', cutPad)));
                    }
                    places.Add(place);
                    padded.Add(0);
                    cutPads.Add(cutPad);
                }
                continue;
            }
            if (right == int.MaxValue)
            {
                places.Add(span.End);
                padded.Add(0);
            }
            else if (width > right + 1)
            {
                places.Add(BlockPiece(text, line, left, right, tab)!.Value.End);
                padded.Add(0);
            }
            else
            {
                // A short line is padded with spaces to just past the block.
                int pad = right + 1 - width;
                pads.Add(new TextEdit(span.End, 0, new string(' ', pad)));
                places.Add(span.End);
                padded.Add(pad);
            }
        }
        if (places.Count == 0)
        {
            Mode = VimMode.Normal;
            return VimResult.Held;
        }
        blockCorner = BlockPiece(text, top, left, right, tab)?.Start ?? text.GetLine(top).End;
        blockPads = [.. padded];
        blockCutPads = cutPads.Any(pad => pad > 0) ? [.. cutPads] : null;
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

    /// <summary>. after I or A over characters or lines: what they typed typed again at each caret, as <see cref="InsertAgain"/> says.</summary>
    private VimResult InsertedAgain(TextSnapshot text, SelectionSet carets, VisualExtent extent, Command command)
    {
        SelectionSet places = carets.With(c =>
        {
            if (extent.Again == InsertAgain.AtCursor)
            {
                return Selection.At(c.Caret);
            }
            int line = (int)Math.Min(text.GetLineFromOffset(c.Caret) + (long)extent.Lines, VimMotions.LastLine(text, breakEnds));
            int last = LastCharOf(text, text.GetLineStart(line));
            // The column is the caret's place in its line, counted in characters, not cells; after $ the line's last.
            int at = extent.Again == InsertAgain.OnLastBelow ? last : Math.Min(text.GetLineStart(line) + (c.Caret - VimMotions.LineOf(text, c.Caret).Start), last);
            // A goes past that character where the last visual change kept two columns, on it where it kept one.
            return Selection.At(keptBlockLeft != keptBlockRight ? Math.Min(CharEnd(text, at), text.GetLine(line).End) : at);
        });
        return Insert(text, places, command, extent.Again == InsertAgain.AtCursor ? "I" : "A");
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

    /// <summary>Where a block's I goes in on each line from <paramref name="first"/> to <paramref name="last"/>, as the line
    /// and the offset in it, passing over lines that end before the block; null where the block's edge cuts a character there.</summary>
    private static (int Line, int Offset)[]? PlacesBelowCut(TextSnapshot text, int first, int last, int left, int right, int tab)
    {
        List<(int Line, int Offset)> places = [];
        for (int line = first; line <= last; line++)
        {
            LineSpan span = text.GetLine(line);
            int width = TextNavigation.VisualColumn(text, span.End, tab);
            if (width < left)
            {
                continue;
            }
            int place = width == left ? span.End : BlockPiece(text, line, left, right, tab)!.Value.Start;
            if (place < span.End && TextNavigation.VisualColumn(text, place, tab) < left)
            {
                return null;
            }
            places.Add((line, place - span.Start));
        }
        return [.. places];
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
    /// unnamed register unless it held nothing (v$ on an empty last line) and P doesn't; whole lines put over characters
    /// go on lines of their own, over a block below the line its cursor stands on (<paramref name="below"/>). From an
    /// empty register the stretch is deleted alone, as a d into the unnamed register (for P into none); . after either
    /// deletes as much again so, putting nothing.</summary>
    private VimResult PutOver(Command command, bool keep, TextSnapshot text, List<Stretch> stretches, SelectionSet carets, bool block, IVimView view, (int Top, int Bottom, int Left, int Right) over, int below)
    {
        if (VimRegisters.IsClipboard(command.Register))
        {
            Remember(command);
            return new VimResult(true, carets);
        }
        // The black hole register puts as empty text, a line of nothing over lines.
        if ((command.Register == '_' ? new VimRegister([string.Empty], false) : Registers.Get(command.Register)) is not { } value)
        {
            char? into = keep ? '_' : null;
            return Act(command with { Keys = into is { } none ? ["\"", none.ToString(), "d"] : ["d"], Register = into }, "d", stretches, text, carets, view, block, over);
        }
        Remember(command with { Keys = keep ? ["\"", "_", "d"] : ["d"], Count = 0 });
        stretches.Sort((a, b) => a.Start.CompareTo(b.Start));
        VimRegister replaced = TakenBy(text, stretches, block, over, view.Indent.TabWidth);
        string eol = view.LineEnding;
        int times = Math.Clamp(command.Count, 1, Math.Max(1, MostRepeatedText / Math.Max(1, value.Text.Length)));
        if (block && intoEmptied && value.Linewise)
        {
            // Over an emptied text a block's P puts lines in its line's place, its p below it less a last empty one.
            List<string> putLines = [.. LineEndings.In(value.Text, eol)[..^eol.Length].Split(eol)];
            if (keep)
            {
                string all = string.Join(eol, putLines);
                return new VimResult(true, Edits: [new EditPlan([new TextEdit(0, 0, all)], SelectionSet.Single(TextNavigation.IndentEnd(TextSnapshot.From(all), TextSnapshot.From(all).GetLine(0))), EditKind.Other)]);
            }
            if (putLines[^1].Length == 0)
            {
                putLines.RemoveAt(putLines.Count - 1);
            }
            if (putLines.Count == 0)
            {
                // Putting nothing, the text is still no longer emptied but its one empty line.
                return new VimResult(true, Edits: [new EditPlan([], SelectionSet.Single(0), EditKind.Other)]);
            }
            string under = string.Join(eol, putLines);
            return new VimResult(true, Edits: [new EditPlan([new TextEdit(0, 0, eol + under)],
                SelectionSet.Single(eol.Length + TextNavigation.IndentEnd(TextSnapshot.From(under), TextSnapshot.From(under).GetLine(0))), EditKind.Other)]);
        }
        if (block && value.Linewise)
        {
            // P over a block whose top line's part ran to that line's end, past text it leaves, puts the lines below that line.
            Stretch first = stretches[0];
            bool belowTop = keep && first.First == over.Top && first.End > first.Start && first.End == text.GetLine(over.Top).End && first.Start > text.GetLine(over.Top).Start;
            return PutLinesBy(value, keep, keep && !belowTop, text, stretches, over.Top, belowTop ? over.Top : below, eol, replaced, times);
        }
        if (value.Blockwise && (block || stretches.TrueForAll(s => !s.Lines)))
        {
            // A block put over a block or characters: what is shown deleted, the register's put in its place from its top left.
            List<TextEdit> deletes = [.. stretches.Where(t => t.End > t.Start).Select(t => new TextEdit(t.Start, t.End - t.Start, string.Empty))];
            int corner = stretches[0].Start;
            TextSnapshot cleared = text.Apply(deletes);
            VimResult put = PutBlock(value, before: true, times, cleared, SelectionSet.Single(TextEditing.MapOffset(corner, deletes, false)), view, cursorAfter: false);
            if (!keep && replaced.Text.Length > 0 && !intoEmptied)
            {
                Registers.Delete(null, replaced, always: false);
            }
            // Over an emptied text's line, taking nothing, it leaves the text emptied unless it writes there.
            IReadOnlyList<EditPlan> deleted = deletes.Count == 0 && intoEmptied ? [] : [new EditPlan(deletes, SelectionSet.Single(corner), EditKind.Other)];
            return new VimResult(true, Edits: [.. deleted, .. put.Edits ?? []]);
        }
        if (block && !value.Text.Contains('\n', StringComparison.Ordinal))
        {
            // Text of one line put over a block takes each line's part of it, the caret on its last character on the first.
            string each = string.Concat(Enumerable.Repeat(value.Text, times));
            List<TextEdit> pieces = [.. stretches.Select(t => new TextEdit(t.Start, t.End - t.Start, each))];
            TextSnapshot filled = text.Apply(pieces);
            int lastOfFirst = stretches[0].Start + Math.Max(0, each.Length - 1);
            if (!keep && replaced.Text.Length > 0 && !intoEmptied)
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
                // Lines put over characters go on lines of their own between what stays, below an emptied text's one line.
                put = eol + (intoEmptied && piece.EndsWith(eol, StringComparison.Ordinal) ? piece[..^eol.Length] : piece);
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
            : VimMotions.FirstNonBlank(after, after.GetLineFromOffset(Math.Min(p, after.Length)), breakEnds)))];
        if (!keep && replaced.Text.Length > 0 && !intoEmptied)
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
        if (!keep && replaced.Text.Length > 0 && !intoEmptied)
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
        int width = value.Width ?? widths.Max();
        int times = Math.Clamp(count, 1, Math.Max(1, MostRepeatedText / Math.Max(1, width * value.Parts.Count)));
        List<TextEdit> edits = [];
        int last = VimMotions.LastLine(text, breakEnds);
        int firstAt = 0;
        int lastAt = 0;
        int shift = 0;
        string added = string.Empty;
        int addedLines = 0;
        bool endsBroken = text.Length > 0 && text.GetLine(text.LineCount - 1).Length == 0 && text.LineCount - 1 > last;
        for (int i = 0; i < value.Parts.Count; i++)
        {
            string padded = value.Parts[i] + new string(' ', Math.Max(0, width - widths[i]));
            int line = home.Line + i;
            int at;
            string lead;
            bool followed;
            if (line > last)
            {
                at = text.Length;
                lead = (endsBroken && addedLines == 0 ? string.Empty : eol) + new string(' ', column);
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
                    int cutAt = at;
                    while (at < span.End && TextNavigation.VisualColumn(text, at, tab) < column)
                    {
                        cutAt = at;
                        at = TextNavigation.NextGrapheme(text, at);
                    }
                    (lead, followed) = (string.Empty, true);
                    if (at <= span.End && TextNavigation.VisualColumn(text, at, tab) > column && text[cutAt] != '\t')
                    {
                        // A character of several columns the column cuts goes after the block whole, spaces before it to the column.
                        (at, lead) = (cutAt, new string(' ', column - TextNavigation.VisualColumn(text, cutAt, tab)));
                    }
                }
            }
            string put = lead + string.Concat(Enumerable.Repeat(padded, times - 1)) + (followed ? padded : value.Parts[i]);
            if (line > last)
            {
                added += put;
                addedLines++;
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
        if (addedLines > 0)
        {
            // Lines added past a file that kept its final break keep it after them.
            edits.Add(new TextEdit(text.Length, 0, endsBroken ? added + eol : added));
            if (edits.Count == 1)
            {
                firstAt = text.Length + added.IndexOf(value.Parts[0], StringComparison.Ordinal);
            }
            lastAt = text.Length + shift + added.Length - value.Parts[^1].Length;
        }
        TextSnapshot after = text.Apply(edits);
        // gp and gP land past the last line's piece as put, its characters with their padding, whatever columns they show.
        int putLength = value.Parts[^1].Length + Math.Max(0, width - widths[^1]);
        int landing = cursorAfter ? Settled(after, Math.Min(lastAt + (putLength * times), VimMotions.LineOf(after, lastAt).End), breakEnds) : Settled(after, firstAt, breakEnds);
        return new VimResult(true, Edits: [new EditPlan(edits, SelectionSet.Single(landing), EditKind.Other)]);
    }
}
