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
        if (op is "x" or "<Del>")
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
            ? BlockStretches(text, top, bottom, left, right, tab, perLine: basic == "c")
            : RegionStretches(text, taken, lines);
        if (block && !lines && basic == "c" && stretches.Count > 0)
        {
            blockCorner = stretches.Min(t => t.Start);
        }
        if (stretches.Count == 0)
        {
            return new VimResult(true, carets);
        }
        if (op is ">" or "<")
        {
            Remember(acting);
            return block ? ShiftBlock(text, stretches, Math.Max(command.Count, 1), op == ">", view) : Shift(text, carets, stretches, op == ">", view, Math.Max(command.Count, 1));
        }
        if (op == "r")
        {
            Remember(acting);
            return ReplaceIn(text, stretches, carets, command.Argument!, view);
        }
        if (op is "p" or "P")
        {
            return PutOver(acting, op == "P", text, stretches, carets, block, view);
        }
        return Act(acting, basic, stretches, text, carets, view, block && !lines);
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
        int columns = lines == 0 ? r.High - r.Low : VimMotions.ColumnOf(text, r.High, view.Indent.TabWidth);
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
            int column = VimMotions.ColumnOf(text, caret, tab);
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
        return new Region(caret, VimMotions.ColumnOf(text, end.End, tab) <= extent.Columns ? end.End : VimMotions.OffsetAtColumn(text, line, extent.Columns, tab));
    }

    /// <summary>The stretches of character or line regions, and a caret at each one's start (a line region's first line's).</summary>
    private static (List<Stretch>, SelectionSet) RegionStretches(TextSnapshot text, List<Region> taken, bool lines)
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
            stretches.Add(new Stretch(r.Low, CharEnd(text, r.High), false, first, last, r.Low, r.Low));
            carets.Add(Selection.At(r.Low));
        }
        return (stretches, SelectionSet.Create(carets, 0));
    }

    /// <summary>A block's part of each line from <paramref name="top"/> to <paramref name="bottom"/> that reaches it, and
    /// a caret at its top left, or one at each line's part (<paramref name="perLine"/>) for a change typed on every line.</summary>
    private (List<Stretch>, SelectionSet) BlockStretches(TextSnapshot text, int top, int bottom, int left, int right, int tab, bool perLine)
    {
        List<Stretch> stretches = [];
        List<Selection> carets = [];
        for (int line = top; line <= bottom; line++)
        {
            if (BlockPiece(text, line, left, right, tab) is not { } piece)
            {
                continue;
            }
            stretches.Add(new Stretch(piece.Start, piece.End, false, line, line, piece.Start, piece.Start));
            if (perLine || carets.Count == 0)
            {
                carets.Add(Selection.At(piece.Start));
            }
        }
        if (carets.Count == 0)
        {
            carets.Add(Selection.At(VimMotions.Settled(text, text.GetLine(top).End, breakEnds)));
        }
        return (stretches, SelectionSet.Create(carets, 0));
    }

    /// <summary>I and A in visual mode: insert mode before the stretch (a character or line stretch's first line's start)
    /// or after it (a line stretch's last line's end), or before or after a block on each line it crosses, a short
    /// line padded to the block's right edge for A.</summary>
    private VimResult VisualInsert(Command command, bool after, VimMode mode, List<Region> taken, TextSnapshot text, IVimView view, int top, int bottom, int left, int right)
    {
        int tab = view.Indent.TabWidth;
        string how = after ? "A" : "I";
        if (mode != VimMode.VisualBlock)
        {
            SelectionSet at = SelectionSet.Create([.. taken.Select(r => Selection.At(after
                ? Math.Min(CharEnd(text, r.High), VimMotions.LineOf(text, r.High).End)
                : VimMotions.LineOf(text, r.Low).Start))], 0);
            return Insert(text, at, command, how);
        }
        List<TextEdit> pads = [];
        List<int> places = [];
        for (int line = top; line <= bottom; line++)
        {
            LineSpan span = text.GetLine(line);
            int width = VimMotions.ColumnOf(text, span.End, tab);
            if (!after)
            {
                if (width > left)
                {
                    places.Add(BlockPiece(text, line, left, right, tab)!.Value.Start);
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

    /// <summary>A block's &gt; and &lt;: blanks added at its left edge on each line, or taken from there.</summary>
    private VimResult ShiftBlock(TextSnapshot text, List<Stretch> stretches, int times, bool right, IVimView view)
    {
        int width = view.Indent.Size * times;
        List<TextEdit> edits = [];
        foreach (Stretch s in stretches.OrderBy(s => s.Start))
        {
            if (right)
            {
                edits.Add(new TextEdit(s.Start, 0, new string(' ', width)));
                continue;
            }
            int end = s.Start;
            LineSpan line = VimMotions.LineOf(text, s.Start);
            while (end < line.End && end - s.Start < width && text[end] is ' ' or '\t')
            {
                end++;
            }
            if (end > s.Start)
            {
                edits.Add(new TextEdit(s.Start, end - s.Start, string.Empty));
            }
        }
        int first = stretches.Min(s => s.Start);
        SelectionSet caret = SelectionSet.Single(first);
        if (edits.Count == 0)
        {
            return new VimResult(true, caret);
        }
        TextSnapshot after = text.Apply(edits);
        return new VimResult(true, Edits: [new EditPlan(edits, SelectionSet.Single(Settled(after, TextEditing.MapOffset(first, edits, false), breakEnds)), EditKind.Other)]);
    }

    /// <summary>p and P over a visual stretch: the register's text in place of what is shown, which p keeps in the
    /// unnamed register and P doesn't; whole lines put over characters go on lines of their own. From an empty register
    /// the stretch is deleted alone, as a d into the unnamed register (for P into none), which . makes again.</summary>
    private VimResult PutOver(Command command, bool keep, TextSnapshot text, List<Stretch> stretches, SelectionSet carets, bool block, IVimView view)
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
            string piece = LineEndings.In(dealt ? value.Parts[i] : value.Text, eol);
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
                landing = piece.Contains('\n', StringComparison.Ordinal) ? 0 : Math.Max(0, piece.Length - 1);
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

    /// <summary>A block's put: its lines one below another from the caret's column (after the caret's character for p),
    /// each padded to the block's width where text follows, a line too short padded out to the column, lines added past
    /// the text's end.</summary>
    private VimResult PutBlock(VimRegister value, bool before, int count, TextSnapshot text, SelectionSet selections, IVimView view, bool cursorAfter)
    {
        int tab = view.Indent.TabWidth;
        string eol = view.LineEnding;
        int caret = selections.Primary.Caret;
        LineSpan home = VimMotions.LineOf(text, caret);
        int column = VimMotions.ColumnOf(text, caret, tab) + (before || home.Length == 0 ? 0 : CellsOf(text, caret, tab));
        int[] widths = [.. value.Parts.Select(p => VimMotions.ColumnOf(TextSnapshot.From(p), p.Length, tab))];
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
                int lineWidth = VimMotions.ColumnOf(text, span.End, tab);
                if (lineWidth <= column)
                {
                    (at, lead, followed) = (span.End, new string(' ', column - lineWidth), false);
                }
                else
                {
                    at = span.Start;
                    while (at < span.End && VimMotions.ColumnOf(text, at, tab) < column)
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
