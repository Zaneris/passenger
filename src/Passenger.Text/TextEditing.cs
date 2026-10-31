using Passenger.Core;

namespace Passenger.Text;

/// <summary>An edit worked out but not yet applied: the edits (in the snapshot it was planned on),
/// where every caret ends up after them, and the kind of edit for undo.</summary>
public sealed record EditPlan(IReadOnlyList<TextEdit> Edits, SelectionSet Selections, EditKind Kind)
{
    /// <summary>Text the edits take out and put back elsewhere as it was (the line a moved line steps over), for what keeps
    /// offsets in it (a fold) to go with it rather than collapse where it was taken out.</summary>
    public IReadOnlyList<TextMove> Moved { get; init; } = [];
}

/// <summary>Text moved whole: <paramref name="Length"/> characters at <paramref name="From"/> in the text before stand at
/// <paramref name="To"/> in the text after.</summary>
public readonly record struct TextMove(int From, int Length, int To);

/// <summary>Every editing command, as a function from text and carets to an <see cref="EditPlan"/>.
/// Each acts on every caret at once; a command with nothing to do returns null.</summary>
public static class TextEditing
{
    private const string Openers = "([{";
    private const string Closers = ")]}";

    public static EditPlan Insert(TextSnapshot text, SelectionSet selections, string typed, EditKind kind = EditKind.Typing)
    {
        List<Replacement> replacements = [];
        for (int i = 0; i < selections.Count; i++)
        {
            Selection s = selections.Items[i];
            replacements.Add(new Replacement(s.Start, s.End, typed, typed.Length, typed.Length, i));
        }
        return Build(replacements, selections, kind);
    }

    /// <summary>Typing with the editor's help: a bracket or quote typed over a selection wraps it, an
    /// opener before a space gets its closer, and a closer typed in front of the same closer steps
    /// over it. Anything else is <see cref="Insert"/>.</summary>
    public static EditPlan Type(TextSnapshot text, SelectionSet selections, string typed)
    {
        if (typed.Length != 1)
        {
            return Insert(text, selections, typed);
        }
        char c = typed[0];
        int opener = Openers.IndexOf(c);
        bool quote = c is '"' or '\'';
        List<Replacement> replacements = [];
        for (int i = 0; i < selections.Count; i++)
        {
            Selection s = selections.Items[i];
            char next = s.End < text.Length ? text[s.End] : '\n';
            char previous = s.Start > 0 ? text[s.Start - 1] : '\n';
            if (!s.IsEmpty && (opener >= 0 || quote))
            {
                char close = quote ? c : Closers[opener];
                string inner = text.GetText(s.Start, s.Length);
                replacements.Add(new Replacement(s.Start, s.End, c + inner + close, 1, 1 + inner.Length, i));
            }
            else if (s.IsEmpty && (Closers.Contains(c) || quote) && next == c)
            {
                replacements.Add(new Replacement(s.Start, s.Start + 1, typed, 1, 1, i));
            }
            else if (s.IsEmpty && opener >= 0 && (char.IsWhiteSpace(next) || Closers.Contains(next) || next is ';' or ','))
            {
                replacements.Add(new Replacement(s.Start, s.End, c.ToString() + Closers[opener], 1, 1, i));
            }
            else if (s.IsEmpty && quote && !TextNavigation.IsWordChar(previous) && !TextNavigation.IsWordChar(next) && previous != c)
            {
                replacements.Add(new Replacement(s.Start, s.End, new string(c, 2), 1, 1, i));
            }
            else
            {
                replacements.Add(new Replacement(s.Start, s.End, typed, 1, 1, i));
            }
        }
        return Build(replacements, selections, EditKind.Typing);
    }

    public static EditPlan? Backspace(TextSnapshot text, SelectionSet selections, IndentOptions indent)
    {
        List<Replacement> replacements = [];
        for (int i = 0; i < selections.Count; i++)
        {
            Selection s = selections.Items[i];
            if (!s.IsEmpty)
            {
                replacements.Add(Removal(s.Start, s.End, i));
                continue;
            }
            int at = s.Caret;
            if (at == 0)
            {
                continue;
            }
            LineSpan line = text.GetLine(text.GetLineFromOffset(at));
            if (at == line.Start)
            {
                replacements.Add(Removal(text.GetLine(line.Line - 1).End, at, i));
                continue;
            }
            char before = text[at - 1];
            char after = at < text.Length ? text[at] : '\0';
            int opener = Openers.IndexOf(before);
            if ((opener >= 0 && after == Closers[opener]) || (before is '"' or '\'' && after == before))
            {
                replacements.Add(Removal(at - 1, at + 1, i));
                continue;
            }
            if (!indent.UseTabs && before == ' ' && SpacesStart(text, line.Start, at) == line.Start)
            {
                int stop = (at - line.Start - 1) / indent.Size * indent.Size;
                replacements.Add(Removal(line.Start + stop, at, i));
                continue;
            }
            replacements.Add(Removal(TextNavigation.PreviousGrapheme(text, at), at, i));
        }
        return replacements.Count == 0 ? null : Build(Union(replacements), selections, EditKind.DeleteBackward);
    }

    /// <summary>Where the spaces just before an offset of a line begin, read back a block at a time on the stack.</summary>
    private static int SpacesStart(TextSnapshot text, int lineStart, int at)
    {
        Span<char> block = stackalloc char[64];
        while (at > lineStart)
        {
            int from = Math.Max(lineStart, at - block.Length);
            Span<char> read = block[..(at - from)];
            text.CopyTo(from, read);
            int last = read.LastIndexOfAnyExcept(' ');
            if (last >= 0)
            {
                return from + last + 1;
            }
            at = from;
        }
        return at;
    }

    public static EditPlan? Delete(TextSnapshot text, SelectionSet selections)
    {
        List<Replacement> replacements = [];
        for (int i = 0; i < selections.Count; i++)
        {
            Selection s = selections.Items[i];
            if (!s.IsEmpty)
            {
                replacements.Add(Removal(s.Start, s.End, i));
            }
            else if (s.Caret < text.Length)
            {
                replacements.Add(Removal(s.Caret, TextNavigation.NextGrapheme(text, s.Caret), i));
            }
        }
        return replacements.Count == 0 ? null : Build(Union(replacements), selections, EditKind.DeleteForward);
    }

    public static EditPlan? DeleteWord(TextSnapshot text, SelectionSet selections, bool backward, WordStop stop)
    {
        List<Replacement> replacements = [];
        for (int i = 0; i < selections.Count; i++)
        {
            Selection s = selections.Items[i];
            if (!s.IsEmpty)
            {
                replacements.Add(Removal(s.Start, s.End, i));
                continue;
            }
            int to = backward ? TextNavigation.PreviousWord(text, s.Caret) : TextNavigation.NextWord(text, s.Caret, stop);
            if (to != s.Caret)
            {
                replacements.Add(Removal(Math.Min(to, s.Caret), Math.Max(to, s.Caret), i));
            }
        }
        EditKind kind = backward ? EditKind.DeleteBackward : EditKind.DeleteForward;
        return replacements.Count == 0 ? null : Build(Union(replacements), selections, kind);
    }

    /// <summary>Enter: a line break carrying the line's indentation, one level more after an opening
    /// bracket, and the closer pushed to its own line when the caret sat between a pair. Spaces right
    /// after the caret go, up to the next caret, so the new line starts at its indentation; from inside the
    /// indentation, that is the whole line's, which the text moved down keeps. In <paramref name="markup"/> the tags
    /// decide instead: a level in after a start tag, its end tag pushed to a line of its own at the element's.</summary>
    public static EditPlan NewLine(TextSnapshot text, SelectionSet selections, string eol, IndentOptions indent, MarkupKind markup = MarkupKind.None)
    {
        List<Replacement> replacements = [];
        for (int i = 0; i < selections.Count; i++)
        {
            Selection s = selections.Items[i];
            LineSpan line = text.GetLine(text.GetLineFromOffset(s.Start));
            string lineIndent = text.GetText(line.Start, TextNavigation.IndentEnd(text, line) - line.Start);

            LineSpan endLine = text.GetLine(text.GetLineFromOffset(s.End));
            // A later caret in these spaces breaks the line itself, so this one's spaces, and any pair, stop at it.
            int limit = i + 1 < selections.Count ? Math.Min(endLine.End, selections.Items[i + 1].Start) : endLine.End;
            int end = s.End;
            while (end < limit && text[end] is ' ' or '\t')
            {
                end++;
            }
            int before = TextNavigation.WhitespaceBefore(text, line, s.Start);
            char next = end < limit ? text[end] : '\0';
            int opener = before > line.Start ? Openers.IndexOf(text[before - 1]) : -1;
            string extra = opener >= 0 ? indent.Unit : string.Empty;

            if (markup != MarkupKind.None && MarkupIndent.ForNewLine(text, s.Start, end, limit, markup, indent) is { } tags)
            {
                string inserted = eol + tags.Indent + (tags.EndTagIndent is { } endTag ? eol + endTag : string.Empty);
                int caret = eol.Length + tags.Indent.Length;
                replacements.Add(new Replacement(s.Start, end, inserted, caret, caret, i));
            }
            else if (opener >= 0 && next == Closers[opener])
            {
                string inserted = eol + lineIndent + extra + eol + lineIndent;
                int caret = eol.Length + lineIndent.Length + extra.Length;
                replacements.Add(new Replacement(s.Start, end, inserted, caret, caret, i));
            }
            else
            {
                string inserted = eol + lineIndent + extra;
                replacements.Add(new Replacement(s.Start, end, inserted, inserted.Length, inserted.Length, i));
            }
        }
        return Build(replacements, selections, EditKind.Other);
    }

    /// <summary>Shift+Enter (<paramref name="above"/> false) or Ctrl+Enter: a new indented line below
    /// or above the caret's line, wherever on it the caret is; below one in <paramref name="markup"/>, indented as Enter
    /// at the line's end indents it.</summary>
    public static EditPlan StartNewLine(TextSnapshot text, SelectionSet selections, string eol, IndentOptions indent, bool above, MarkupKind markup = MarkupKind.None)
    {
        List<Replacement> replacements = [];
        HashSet<int> lines = [];
        for (int i = 0; i < selections.Count; i++)
        {
            LineSpan line = text.GetLine(text.GetLineFromOffset(selections.Items[i].Caret));
            if (!lines.Add(line.Line))
            {
                continue;
            }
            string lineIndent = text.GetText(line.Start, TextNavigation.IndentEnd(text, line) - line.Start);
            if (above)
            {
                string inserted = lineIndent + eol;
                replacements.Add(new Replacement(line.Start, line.Start, inserted, lineIndent.Length, lineIndent.Length, i));
            }
            else
            {
                int trimmed = TextNavigation.WhitespaceBefore(text, line, line.End);
                bool opens = trimmed > line.Start && Openers.Contains(text[trimmed - 1]);
                string inserted = markup != MarkupKind.None && MarkupIndent.ForNewLine(text, line.End, line.End, line.End, markup, indent) is { } tags
                    ? eol + tags.Indent
                    : eol + lineIndent + (opens ? indent.Unit : string.Empty);
                replacements.Add(new Replacement(line.End, line.End, inserted, inserted.Length, inserted.Length, i));
            }
        }
        return Build(replacements, selections, EditKind.Other);
    }

    /// <summary>Tab: a selection over several lines indents those lines; otherwise each caret's
    /// selection is replaced by indentation up to the next stop.</summary>
    public static EditPlan Indent(TextSnapshot text, SelectionSet selections, IndentOptions indent)
    {
        bool lines = selections.Items.Any(s => text.GetLineFromOffset(s.Start) != text.GetLineFromOffset(s.End));
        if (!lines)
        {
            List<Replacement> replacements = [];
            for (int i = 0; i < selections.Count; i++)
            {
                Selection s = selections.Items[i];
                LineSpan line = text.GetLine(text.GetLineFromOffset(s.Start));
                string content = text.GetText(line.Start, line.Length);
                int visual = TextNavigation.VisualColumn(content, s.Start - line.Start, indent.TabWidth);
                string inserted = indent.UseTabs ? "\t" : new string(' ', indent.Size - visual % indent.Size);
                replacements.Add(new Replacement(s.Start, s.End, inserted, inserted.Length, inserted.Length, i));
            }
            return Build(replacements, selections, EditKind.Typing);
        }
        List<TextEdit> edits = [];
        foreach (int lineNumber in TouchedLines(text, selections))
        {
            LineSpan line = text.GetLine(lineNumber);
            if (line.Length > 0)
            {
                edits.Add(new TextEdit(line.Start, 0, indent.Unit));
            }
        }
        return Mapped(edits, selections, EditKind.Other);
    }

    /// <summary>Shift+Tab: every touched line loses a tab or up to one level of spaces.</summary>
    public static EditPlan? Outdent(TextSnapshot text, SelectionSet selections, IndentOptions indent)
    {
        List<TextEdit> edits = [];
        foreach (int lineNumber in TouchedLines(text, selections))
        {
            LineSpan line = text.GetLine(lineNumber);
            if (line.Length == 0)
            {
                continue;
            }
            if (text[line.Start] == '\t')
            {
                edits.Add(new TextEdit(line.Start, 1, string.Empty));
                continue;
            }
            int spaces = 0;
            while (spaces < indent.Size && spaces < line.Length && text[line.Start + spaces] == ' ')
            {
                spaces++;
            }
            if (spaces > 0)
            {
                edits.Add(new TextEdit(line.Start, spaces, string.Empty));
            }
        }
        return edits.Count == 0 ? null : Mapped(edits, selections, EditKind.Other);
    }

    /// <summary>Duplicates each caret's line below it, the caret following onto the copy; a
    /// selection is duplicated after itself instead, and the copy selected. A line takes the lines
    /// <paramref name="folded"/> hides behind it along, as every line command does.</summary>
    public static EditPlan Duplicate(TextSnapshot text, SelectionSet selections, string eol, IReadOnlyList<(int First, int Last)>? folded = null)
    {
        List<Replacement> replacements = [];
        HashSet<int> lines = [];
        for (int i = 0; i < selections.Count; i++)
        {
            Selection s = selections.Items[i];
            if (!s.IsEmpty)
            {
                string selected = text.GetText(s.Start, s.Length);
                bool forward = s.Caret >= s.Anchor;
                replacements.Add(new Replacement(s.End, s.End, selected, forward ? 0 : selected.Length, forward ? selected.Length : 0, i));
                continue;
            }
            (int first, int last) = ShownLine(text.GetLineFromOffset(s.Caret), folded);
            if (!lines.Add(first))
            {
                continue;
            }
            LineSpan top = text.GetLine(first);
            LineSpan bottom = text.GetLine(last);
            string copy = text.GetText(top.Start, bottom.End - top.Start);
            int column = s.Caret - top.Start;
            // After the line's break, so a fold ending with the line stays out of the copy; the last line has none.
            replacements.Add(bottom.BreakLength > 0
                ? new Replacement(bottom.EndIncludingBreak, bottom.EndIncludingBreak, copy + eol, column, column, i)
                : new Replacement(bottom.End, bottom.End, eol + copy, eol.Length + column, eol.Length + column, i));
        }
        return Build(replacements, selections, EditKind.Other);
    }

    /// <summary>Deletes every line a caret or selection touches, with the lines <paramref name="folded"/> hides behind
    /// them; carets stay at their column on the line that moves up into the gap.</summary>
    public static EditPlan DeleteLines(TextSnapshot text, SelectionSet selections, IReadOnlyList<(int First, int Last)>? folded = null)
    {
        List<(int First, int Last)> blocks = Blocks(text, selections, folded);
        List<TextEdit> edits = blocks.Select(b => LineBlockRemoval(text, b.First, b.Last)).ToList();
        TextSnapshot after = text.Apply(edits);
        return new EditPlan(edits, selections.With(s =>
        {
            TextPosition position = text.GetPosition(s.Caret);
            int removedAbove = blocks.Where(b => b.Last < position.Line).Sum(b => b.Last - b.First + 1);
            int line = position.Line;
            foreach ((int first, int last) in blocks)
            {
                if (first <= position.Line && position.Line <= last)
                {
                    line = first;
                }
            }
            line = Math.Min(line - removedAbove, after.LineCount - 1);
            return Selection.At(after.GetOffset(new TextPosition(line, position.Column)));
        }), EditKind.Other);
    }

    /// <summary>Moves every touched line up or down past its neighbour, carets and selections travelling with them; a line
    /// takes the lines <paramref name="folded"/> hides behind it along, and a neighbour shown with folded lines is stepped
    /// over whole. The lines stay and their neighbour goes round them, so what is in them (a fold, a breakpoint) goes with
    /// them. Nothing moves if any block is already at the edge.</summary>
    public static EditPlan? MoveLines(TextSnapshot text, SelectionSet selections, string eol, bool up, IReadOnlyList<(int First, int Last)>? folded = null)
    {
        List<(int First, int Last)> blocks = Blocks(text, selections, folded);
        if (up ? blocks[0].First == 0 : blocks[^1].Last == text.LineCount - 1)
        {
            return null;
        }
        List<TextEdit> edits = [];
        List<TextMove> moved = [];
        int[] deltas = new int[blocks.Count];
        int shift = 0;
        void Add(TextEdit edit)
        {
            edits.Add(edit);
            shift += edit.Text.Length - edit.Length;
        }
        for (int b = 0; b < blocks.Count; b++)
        {
            (int first, int last) = blocks[b];
            LineSpan top = text.GetLine(first);
            LineSpan bottom = text.GetLine(last);
            (int nearFirst, int nearLast) = ShownLine(up ? first - 1 : last + 1, folded);
            LineSpan nearTop = text.GetLine(nearFirst);
            LineSpan nearBottom = text.GetLine(nearLast);
            string neighbour = text.GetText(nearTop.Start, nearBottom.End - nearTop.Start);
            deltas[b] = (up ? -1 : 1) * (nearLast - nearFirst + 1);
            if (up)
            {
                Add(new TextEdit(nearTop.Start, top.Start - nearTop.Start, string.Empty));
                // After the block's break, so a fold ending with the block stays out of the neighbour; the last line has none.
                bool broken = bottom.BreakLength > 0;
                int at = broken ? bottom.EndIncludingBreak : bottom.End;
                moved.Add(new TextMove(nearTop.Start, neighbour.Length, at + shift + (broken ? 0 : eol.Length)));
                Add(new TextEdit(at, 0, broken ? neighbour + eol : eol + neighbour));
            }
            else
            {
                moved.Add(new TextMove(nearTop.Start, neighbour.Length, top.Start + shift));
                Add(new TextEdit(top.Start, 0, neighbour + eol));
                int from = nearBottom.BreakLength > 0 ? bottom.EndIncludingBreak : bottom.End;
                Add(new TextEdit(from, (nearBottom.BreakLength > 0 ? nearBottom.EndIncludingBreak : nearBottom.End) - from, string.Empty));
            }
        }
        TextSnapshot after = text.Apply(edits);
        return new EditPlan(edits, selections.With(s =>
        {
            int line = CoveredLines(text, s).First;
            int delta = deltas[blocks.FindIndex(b => b.First <= line && line <= b.Last)];
            TextPosition anchor = text.GetPosition(s.Anchor);
            TextPosition caret = text.GetPosition(s.Caret);
            return new Selection(
                after.GetOffset(anchor with { Line = anchor.Line + delta }),
                after.GetOffset(caret with { Line = caret.Line + delta }),
                s.PreferredX);
        }), EditKind.Other) { Moved = moved };
    }

    /// <summary>Comments every touched line with <paramref name="prefix"/> at their shallowest
    /// indentation, or uncomments them if every non-blank one is already commented. With a
    /// <paramref name="suffix"/> (a block comment's end, for a language with no line comment, as
    /// <c>&lt;!--</c> and <c>--&gt;</c> in XML) each line is wrapped in the two. Given
    /// <paramref name="add"/>, only ever comments (true) or only ever uncomments (false).</summary>
    public static EditPlan? ToggleLineComment(TextSnapshot text, SelectionSet selections, string prefix, string? suffix = null, bool? add = null)
    {
        List<(LineSpan Line, string Content, int Indent)> filled = [];
        foreach (int lineNumber in TouchedLines(text, selections))
        {
            LineSpan line = text.GetLine(lineNumber);
            string content = text.GetText(line.Start, line.Length);
            if (content.AsSpan().Trim().Length > 0)
            {
                filled.Add((line, content, TextNavigation.IndentLength(content)));
            }
        }
        if (filled.Count == 0)
        {
            return null;
        }
        bool commented = filled.All(x => x.Content.AsSpan(x.Indent).StartsWith(prefix, StringComparison.Ordinal)
            && (suffix is null || x.Content.AsSpan(x.Indent + prefix.Length).TrimEnd().EndsWith(suffix, StringComparison.Ordinal)));
        if (add == commented)
        {
            return null;
        }
        List<TextEdit> edits = [];
        if (commented)
        {
            foreach ((LineSpan line, string content, int lineIndent) in filled)
            {
                int length = prefix.Length;
                if (lineIndent + length < content.Length && content[lineIndent + length] == ' ')
                {
                    length++;
                }
                edits.Add(new TextEdit(line.Start + lineIndent, length, string.Empty));
                if (suffix is not null)
                {
                    int end = content.AsSpan().TrimEnd().Length - suffix.Length;
                    int from = end > lineIndent + length && content[end - 1] == ' ' ? end - 1 : end;
                    edits.Add(new TextEdit(line.Start + from, end + suffix.Length - from, string.Empty));
                }
            }
        }
        else
        {
            int column = filled.Min(x => x.Indent);
            foreach ((LineSpan line, string content, _) in filled)
            {
                edits.Add(new TextEdit(line.Start + column, 0, suffix is null ? prefix + " " : prefix));
                if (suffix is not null)
                {
                    edits.Add(new TextEdit(line.Start + content.AsSpan().TrimEnd().Length, 0, suffix));
                }
            }
        }
        return Mapped(edits, selections, EditKind.Other);
    }

    /// <summary>Comment with Block Comment: each selection wrapped in <paramref name="start"/> and
    /// <paramref name="end"/>, or, where it is a block comment already or lies in one, that comment's ends taken off; a
    /// caret with nothing selected takes off the comment it is in, or elsewhere gets an empty one with the caret inside.</summary>
    public static EditPlan? ToggleBlockComment(TextSnapshot text, SelectionSet selections, string start, string end)
    {
        string all = text.ToString();
        List<TextEdit> edits = [];
        HashSet<int> emptied = [];
        HashSet<int> uncommented = [];
        foreach (Selection s in selections.Items)
        {
            TextEdit[] own;
            TextRange? comment = Enclosing(all, s, start, end);
            if (comment is { } enclosing)
            {
                if (uncommented.Contains(enclosing.Start))
                {
                    continue;
                }
                int opened = enclosing.Start + start.Length;
                int closed = enclosing.End - end.Length;
                int afterStart = opened < closed && all[opened] == ' ' ? 1 : 0;
                int beforeEnd = closed - 1 > opened && all[closed - 1] == ' ' ? 1 : 0;
                own = [new TextEdit(enclosing.Start, start.Length + afterStart, string.Empty), new TextEdit(closed - beforeEnd, end.Length + beforeEnd, string.Empty)];
            }
            else if (s.IsEmpty)
            {
                if (emptied.Contains(s.Caret))
                {
                    continue;
                }
                own = [new TextEdit(s.Caret, 0, start + end)];
            }
            else
            {
                own = [new TextEdit(s.Start, 0, start), new TextEdit(s.End, 0, end)];
            }
            // Carets in a comment holding another start ("/*.cs") find one end: edits meeting another's go whole.
            if (own.Any(e => edits.Any(made => Meet(made, e))))
            {
                continue;
            }
            edits.AddRange(own);
            if (comment is { } taken)
            {
                uncommented.Add(taken.Start);
            }
            else if (s.IsEmpty)
            {
                emptied.Add(s.Caret);
            }
        }
        if (edits.Count == 0)
        {
            return null;
        }
        List<TextEdit> sorted = [.. edits.OrderBy(e => e.Start)];
        // A caret given an empty comment stands inside it; every other is carried as a whole-line edit carries it.
        return new EditPlan(sorted, selections.With(s => s.IsEmpty && emptied.Contains(s.Caret)
            ? Selection.At(Map(sorted, s.Caret, true) - end.Length)
            : Carried(sorted, s)), EditKind.Other);
    }

    /// <summary>Whether an edit overlaps one made before it, as <see cref="TextDocument.Edit"/> refuses them once sorted
    /// (the one made first goes first of two at one place).</summary>
    private static bool Meet(TextEdit made, TextEdit edit) => made.Start <= edit.Start ? edit.Start < made.End : made.Start < edit.End;

    /// <summary>The block comment a selection is, or lies in: the nearest start at or before it with no end between, and
    /// the first end at or after it.</summary>
    private static TextRange? Enclosing(string text, Selection s, string start, string end)
    {
        if (text.Length == 0)
        {
            return null;
        }
        ReadOnlySpan<char> selected = text.AsSpan(s.Start, s.Length);
        int from = s.Start + (selected.Length - selected.TrimStart().Length);
        int to = Math.Max(from, s.End - (selected.Length - selected.TrimEnd().Length));
        int opened = text.LastIndexOf(start, Math.Min(text.Length - 1, from + start.Length - 1), StringComparison.Ordinal);
        if (opened < 0)
        {
            return null;
        }
        int ended = text.IndexOf(end, opened + start.Length, StringComparison.Ordinal);
        if (ended < 0 || ended + end.Length <= from)
        {
            return null;
        }
        int closed = text.IndexOf(end, Math.Max(opened + start.Length, to - end.Length), StringComparison.Ordinal);
        return closed < 0 || closed + end.Length < to ? null : TextRange.FromBounds(opened, closed + end.Length);
    }

    /// <summary>What Copy puts on the clipboard: the selections' text, or with nothing selected the
    /// whole lines under the carets, each with the lines <paramref name="folded"/> hides behind it
    /// (marked, so Paste puts them back as lines).</summary>
    public static (string Text, bool WholeLines) Copy(TextSnapshot text, SelectionSet selections, string eol, IReadOnlyList<(int First, int Last)>? folded = null)
    {
        if (selections.Items.All(s => s.IsEmpty))
        {
            IEnumerable<string> lines = selections.Items
                .Select(s => ShownLine(text.GetLineFromOffset(s.Caret), folded))
                .Distinct()
                .Select(l => text.GetText(text.GetLineStart(l.First), text.GetLine(l.Last).End - text.GetLineStart(l.First)) + eol);
            return (string.Concat(lines), true);
        }
        return (string.Join(eol, selections.Items.Where(s => !s.IsEmpty).Select(s => text.GetText(s.Start, s.Length))), false);
    }

    /// <summary>Cut: the selections' text, or with nothing selected the carets' whole lines, as <see cref="Copy"/> takes them.</summary>
    public static EditPlan Cut(TextSnapshot text, SelectionSet selections, IReadOnlyList<(int First, int Last)>? folded = null)
    {
        if (selections.Items.All(s => s.IsEmpty))
        {
            return DeleteLines(text, selections, folded);
        }
        List<Replacement> replacements = [];
        for (int i = 0; i < selections.Count; i++)
        {
            Selection s = selections.Items[i];
            if (!s.IsEmpty)
            {
                replacements.Add(Removal(s.Start, s.End, i));
            }
        }
        return Build(replacements, selections, EditKind.Other);
    }

    /// <summary>Paste: whole lines from a line copy go in above each caret's line; text with one line
    /// per caret is dealt out one line each; anything else goes in at every caret. Given the document's
    /// <paramref name="indent"/>, a block's lines are indented with it, a level of the block's own for each of its.</summary>
    public static EditPlan Paste(TextSnapshot text, SelectionSet selections, string clipboard, bool wholeLines, string eol, IndentOptions? indent = null)
    {
        // A lone '\r' (classic Mac text) breaks a line on the clipboard, though in the editor's text only '\n' does.
        string normalized = LineEndings.In(clipboard.Contains('\r', StringComparison.Ordinal) ? clipboard.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : clipboard, eol);
        string[] parts = normalized.Split(eol);
        if (selections.HasMultiple && parts.Length == selections.Count && !(wholeLines && selections.Items.All(s => s.IsEmpty)))
        {
            List<Replacement> dealt = [];
            for (int i = 0; i < selections.Count; i++)
            {
                Selection s = selections.Items[i];
                dealt.Add(new Replacement(s.Start, s.End, parts[i], parts[i].Length, parts[i].Length, i));
            }
            return Build(dealt, selections, EditKind.Other);
        }
        if (indent is { } to)
        {
            // The first line's indentation is the block's own only where it starts a line's.
            bool firstLine = wholeLines || selections.Items.All(s => TextNavigation.IndentEnd(text, text.GetLine(text.GetLineFromOffset(s.Start))) >= s.Start);
            normalized = Indentation.Reindent(normalized, Indentation.Of(normalized, to, firstLine), to, firstLine);
        }
        if (wholeLines && selections.Items.All(s => s.IsEmpty))
        {
            List<TextEdit> edits = selections.Items
                .Select(s => text.GetLine(text.GetLineFromOffset(s.Caret)).Start)
                .Distinct()
                .Select(start => new TextEdit(start, 0, normalized))
                .ToList();
            return Mapped(edits, selections, EditKind.Other);
        }
        return Insert(text, selections, normalized, EditKind.Other);
    }

    /// <summary>What a save does to the text first, as .editorconfig asks: the spaces and tabs ending each line go, but
    /// on a line with a caret (so a space just typed stays); the text ends with a line break; every break is
    /// <paramref name="convertTo"/>. Null when there is nothing to do.</summary>
    public static EditPlan? Saving(string text, SelectionSet selections, bool trimTrailing, bool finalNewline, string? convertTo, string eol)
    {
        List<TextEdit> edits = [];
        int[] carets = [.. selections.Items.Select(s => s.Caret).Order()];
        int next = 0;
        int start = 0;
        while (trimTrailing || convertTo is not null)
        {
            int feed = text.IndexOf('\n', start);
            int end = feed < 0 ? text.Length : feed > start && text[feed - 1] == '\r' ? feed - 1 : feed;
            while (next < carets.Length && carets[next] < start)
            {
                next++;
            }
            int trimmed = end;
            while (trimTrailing && trimmed > start && text[trimmed - 1] is ' ' or '\t')
            {
                trimmed--;
            }
            if (trimmed < end && !(next < carets.Length && carets[next] <= end))
            {
                edits.Add(new TextEdit(trimmed, end - trimmed, string.Empty));
            }
            if (feed < 0)
            {
                break;
            }
            if (convertTo is not null && (end < feed ? "\r\n" : "\n") != convertTo)
            {
                edits.Add(end < feed ? new TextEdit(end, 1, string.Empty) : new TextEdit(feed, 0, "\r"));
            }
            start = feed + 1;
        }
        if (finalNewline && text.Length > 0 && text[^1] != '\n')
        {
            edits.Add(new TextEdit(text.Length, 0, convertTo ?? eol));
        }
        return edits.Count == 0 ? null : new EditPlan(edits, MapThrough(selections, edits), EditKind.Other);
    }

    /// <summary>Join Lines: a caret joins its line with the next, a selection the lines it covers.
    /// Each run of joined lines is worked out once: its blank lines go, and every other line meets the
    /// text before it with one space. A caret lands where the next line's text begins on the joined
    /// line (its end if none has text); a selection keeps covering what it did.</summary>
    public static EditPlan? JoinLines(TextSnapshot text, SelectionSet selections)
    {
        SortedSet<int> joins = [];
        foreach (Selection s in selections.Items)
        {
            (int first, int last) = CoveredLines(text, s);
            // A caret joins its line with the next; a selection within one line joins nothing.
            last = s.IsEmpty ? first + 1 : last;
            for (int line = first; line < last && line < text.LineCount - 1; line++)
            {
                joins.Add(line);
            }
        }
        if (joins.Count == 0)
        {
            return null;
        }
        List<TextEdit> edits = [];
        Dictionary<int, int> landings = [];
        foreach ((int first, int last) in Runs(joins))
        {
            JoinRun(text, first, last + 1, edits, landings);
        }
        return new EditPlan(edits, selections.With(s =>
        {
            if (!s.IsEmpty)
            {
                return new Selection(Map(edits, s.Start, false), Map(edits, s.End, true));
            }
            int line = text.GetLineFromOffset(s.Caret);
            return Selection.At(Map(edits, landings.TryGetValue(line, out int landing) ? landing : s.Caret, true));
        }), EditKind.Other);
    }

    public static EditPlan? ToggleCase(TextSnapshot text, SelectionSet selections)
    {
        List<Selection> selected = selections.Items.Where(s => !s.IsEmpty).ToList();
        if (selected.Count == 0)
        {
            return null;
        }
        bool anyLower = selected.Any(s => text.GetText(s.Start, s.Length).Any(char.IsLower));
        List<TextEdit> edits = selected
            .Select(s =>
            {
                string part = text.GetText(s.Start, s.Length);
                return new TextEdit(s.Start, s.Length, anyLower ? part.ToUpperInvariant() : part.ToLowerInvariant());
            })
            .ToList();
        return new EditPlan(edits, selections, EditKind.Other);
    }

    /// <summary>Ctrl+K on macOS: removes from each caret to the end of its line, or the line break if
    /// the caret is already there. Also returns the removed text, for the clipboard.</summary>
    public static (EditPlan? Plan, string Removed) CutToLineEnd(TextSnapshot text, SelectionSet selections)
    {
        List<Replacement> replacements = [];
        List<string> removed = [];
        for (int i = 0; i < selections.Count; i++)
        {
            Selection s = selections.Items[i];
            LineSpan line = text.GetLine(text.GetLineFromOffset(s.Caret));
            int end = s.Caret < line.End ? line.End : line.EndIncludingBreak;
            if (end > s.Caret)
            {
                replacements.Add(Removal(s.Caret, end, i));
                removed.Add(text.GetText(s.Caret, end - s.Caret));
            }
        }
        if (replacements.Count == 0)
        {
            return (null, string.Empty);
        }
        return (Build(Union(replacements), selections, EditKind.Other), string.Concat(removed));
    }

    /// <summary>A range carried through edits (sorted, in the old text's coordinates): its ends move
    /// with the text around them, so a marked word stays marked as the line it is on is edited.</summary>
    public static TextRange MapRange(TextRange range, IReadOnlyList<TextEdit> edits)
    {
        int start = Map(edits, range.Start, false);
        int end = Math.Max(start, Map(edits, range.End, range.Length > 0));
        return TextRange.FromBounds(start, end);
    }

    /// <summary>An offset carried through edits (sorted, in the old text's coordinates); text inserted
    /// exactly at it goes before it when <paramref name="afterInsertions"/>, after it otherwise.</summary>
    public static int MapOffset(int offset, IReadOnlyList<TextEdit> edits, bool afterInsertions) => Map(edits, offset, afterInsertions);

    /// <summary>A line (from zero) carried through edits from one text to the other, as a breakpoint is: with its first
    /// character, which text inserted before it or at its very start pushes along, so a line broken at its start goes
    /// down with its code; a line removed lands where the removal did.</summary>
    public static int MapLine(TextSnapshot before, IReadOnlyList<TextEdit> edits, TextSnapshot after, int line)
    {
        int start = before.GetLineStart(Math.Min(line, before.LineCount - 1));
        return after.GetLineFromOffset(Math.Min(Map(edits, start, afterInsertions: true), after.Length));
    }

    /// <summary>Carries carets through edits someone else made (sorted, non-overlapping, in the old
    /// text's coordinates): a caret inside a replaced range lands at its start.</summary>
    public static SelectionSet MapThrough(SelectionSet selections, IReadOnlyList<TextEdit> edits) =>
        selections.With(s => new Selection(Map(edits, s.Anchor, false), Map(edits, s.Caret, false), s.PreferredX));

    /// <summary>The lines each selection covers; a selection ending at a line's very start does not
    /// count that line.</summary>
    public static SortedSet<int> TouchedLines(TextSnapshot text, SelectionSet selections)
    {
        SortedSet<int> lines = [];
        foreach (Selection s in selections.Items)
        {
            (int first, int last) = CoveredLines(text, s);
            for (int line = first; line <= last; line++)
            {
                lines.Add(line);
            }
        }
        return lines;
    }

    /// <summary>The first and last line a selection covers; a selection ending at a line's very start does
    /// not count that line.</summary>
    private static (int First, int Last) CoveredLines(TextSnapshot text, Selection s)
    {
        int first = text.GetLineFromOffset(s.Start);
        int last = text.GetLineFromOffset(s.End);
        return last > first && s.End == text.GetLineStart(last) ? (first, last - 1) : (first, last);
    }

    /// <summary>The touched lines as runs of consecutive lines, each widened to whole lines as shown: with the lines
    /// <paramref name="folded"/> hides behind its first and last.</summary>
    private static List<(int First, int Last)> Blocks(TextSnapshot text, SelectionSet selections, IReadOnlyList<(int First, int Last)>? folded = null)
    {
        List<(int First, int Last)> runs = Runs(TouchedLines(text, selections));
        if (folded is null || folded.Count == 0)
        {
            return runs;
        }
        List<(int First, int Last)> shown = [];
        foreach ((int first, int last) in runs)
        {
            (int from, int to) = (ShownLine(first, folded).First, ShownLine(last, folded).Last);
            if (shown.Count > 0 && from <= shown[^1].Last + 1)
            {
                shown[^1] = (shown[^1].First, Math.Max(shown[^1].Last, to));
            }
            else
            {
                shown.Add((from, to));
            }
        }
        return shown;
    }

    /// <summary>A line as it shows: from the line a fold hides it behind (or itself) through the lines folded behind that.
    /// <paramref name="folded"/> is the hidden lines' ranges in order, never touching, each shown on the line before it.</summary>
    private static (int First, int Last) ShownLine(int line, IReadOnlyList<(int First, int Last)>? folded)
    {
        if (folded is null)
        {
            return (line, line);
        }
        // The last range starting at most a line below: the one this line shows, or hides in, if any.
        int low = Sorted.FirstAfter(folded, static f => f.First, line + 1);
        return low > 0 && line <= folded[low - 1].Last ? (Math.Min(line, folded[low - 1].First - 1), folded[low - 1].Last) : (line, line);
    }

    /// <summary>Sorted line numbers as runs of consecutive lines.</summary>
    private static List<(int First, int Last)> Runs(IEnumerable<int> lines)
    {
        List<(int First, int Last)> runs = [];
        foreach (int line in lines)
        {
            if (runs.Count > 0 && runs[^1].Last == line - 1)
            {
                runs[^1] = (runs[^1].First, line);
            }
            else
            {
                runs.Add((line, line));
            }
        }
        return runs;
    }

    /// <summary>Lines <paramref name="first"/> to <paramref name="last"/> made one in edits that never overlap: blank
    /// lines go whole, the first line with text keeps its indentation and the last its trailing spaces. Each line but
    /// the last gets its landing: where the text after it begins, or the run's end if none has text.</summary>
    private static void JoinRun(TextSnapshot text, int first, int last, List<TextEdit> edits, Dictionary<int, int> landings)
    {
        int[] textStarts = new int[last - first + 1];
        int textEnd = -1;
        for (int line = first; line <= last; line++)
        {
            LineSpan span = text.GetLine(line);
            string content = text.GetText(span.Start, span.Length);
            int indent = TextNavigation.IndentLength(content);
            bool blank = indent == content.Length;
            textStarts[line - first] = blank ? -1 : span.Start + indent;
            if (blank)
            {
                continue;
            }
            if (textEnd >= 0)
            {
                edits.Add(new TextEdit(textEnd, span.Start + indent - textEnd, " "));
            }
            else if (line > first)
            {
                int start = text.GetLine(first).Start;
                edits.Add(new TextEdit(start, span.Start - start, string.Empty));
            }
            textEnd = span.Start + content.TrimEnd(' ', '\t').Length;
        }
        int end = text.GetLine(last).End;
        if (textStarts[^1] < 0)
        {
            int from = textEnd >= 0 ? textEnd : text.GetLine(first).Start;
            edits.Add(new TextEdit(from, end - from, string.Empty));
        }
        int following = end;
        for (int line = last; line > first; line--)
        {
            following = textStarts[line - first] >= 0 ? textStarts[line - first] : following;
            landings[line - 1] = following;
        }
    }

    /// <summary>Removes whole lines with their breaks; the last line has none, so it takes the break
    /// before it instead.</summary>
    private static TextEdit LineBlockRemoval(TextSnapshot text, int first, int last)
    {
        LineSpan top = text.GetLine(first);
        LineSpan bottom = text.GetLine(last);
        if (bottom.BreakLength == 0 && first > 0)
        {
            int start = text.GetLine(first - 1).End;
            return new TextEdit(start, bottom.End - start, string.Empty);
        }
        return new TextEdit(top.Start, bottom.EndIncludingBreak - top.Start, string.Empty);
    }

    /// <summary>One caret's replacement: where its anchor and caret land inside the new text, and
    /// which caret (its index in the set) it belongs to.</summary>
    private readonly record struct Replacement(int Start, int End, string Text, int AnchorInText, int CaretInText, int Owner);

    private static Replacement Removal(int start, int end, int owner) => new(start, end, string.Empty, 0, 0, owner);

    /// <summary>Merges overlapping removals (two carets whose deletions meet) into one; the caret
    /// that loses its own lands in the same place and merges with the other.</summary>
    private static List<Replacement> Union(List<Replacement> replacements)
    {
        List<Replacement> merged = [];
        foreach (Replacement r in replacements.OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && r.Start < merged[^1].End)
            {
                Replacement last = merged[^1];
                merged[^1] = last with { End = Math.Max(last.End, r.End), Text = last.Text + r.Text };
            }
            else
            {
                merged.Add(r);
            }
        }
        return merged;
    }

    /// <summary>Applies the replacements, each caret landing where its own replacement says; a caret
    /// with none of its own is carried through the edits unchanged.</summary>
    private static EditPlan Build(List<Replacement> replacements, SelectionSet selections, EditKind kind)
    {
        List<Replacement> sorted = replacements.OrderBy(r => r.Start).ToList();
        List<TextEdit> edits = sorted.Select(r => new TextEdit(r.Start, r.End - r.Start, r.Text)).ToList();
        Selection[] placed = new Selection[selections.Count];
        bool[] done = new bool[selections.Count];
        int shift = 0;
        foreach (Replacement r in sorted)
        {
            int start = r.Start + shift;
            placed[r.Owner] = new Selection(start + r.AnchorInText, start + r.CaretInText);
            done[r.Owner] = true;
            shift += r.Text.Length - (r.End - r.Start);
        }
        for (int i = 0; i < placed.Length; i++)
        {
            if (!done[i])
            {
                Selection s = selections.Items[i];
                placed[i] = new Selection(Map(edits, s.Anchor, false), Map(edits, s.Caret, false), s.PreferredX);
            }
        }
        return new EditPlan(edits, SelectionSet.Create(placed, selections.PrimaryIndex), kind);
    }

    /// <summary>Edits that are not one per caret (whole-line work): carets and selections are carried
    /// through them. A caret moves past text inserted at it; a selection's start stays before such
    /// text and its end moves past, so a selection grows to cover what was added at its edges.</summary>
    private static EditPlan Mapped(List<TextEdit> edits, SelectionSet selections, EditKind kind)
    {
        List<TextEdit> sorted = edits.OrderBy(e => e.Start).ToList();
        return new EditPlan(sorted, selections.With(s => Carried(sorted, s)), kind);
    }

    /// <summary>A caret or selection carried through whole-line edits, as <see cref="Mapped"/> carries it.</summary>
    private static Selection Carried(List<TextEdit> sorted, Selection s)
    {
        if (s.IsEmpty)
        {
            return Selection.At(Map(sorted, s.Caret, true));
        }
        int start = Map(sorted, s.Start, false);
        int end = Map(sorted, s.End, true);
        return s.Caret >= s.Anchor ? new Selection(start, end) : new Selection(end, start);
    }

    /// <summary>An offset in the text after <paramref name="edits"/> (sorted, in the old text's
    /// coordinates). Inside a replaced range it lands at the range's start, or its end if
    /// <paramref name="afterInsertions"/>; at an insertion point it stays before the inserted text
    /// unless <paramref name="afterInsertions"/>.</summary>
    private static int Map(IReadOnlyList<TextEdit> edits, int offset, bool afterInsertions)
    {
        if (edits is SortedEdits sorted)
        {
            return sorted.Map(offset, afterInsertions);
        }
        int shift = 0;
        // By index: an enumerator of the list would be made for every offset carried, every fold and marker every key.
        for (int i = 0; i < edits.Count; i++)
        {
            TextEdit e = edits[i];
            bool before = e.End < offset || (e.End == offset && (e.Length > 0 || afterInsertions));
            if (before)
            {
                shift += e.Text.Length - e.Length;
                continue;
            }
            if (e.Start < offset && offset < e.End)
            {
                return e.Start + shift + (afterInsertions ? e.Text.Length : 0);
            }
            break;
        }
        return offset + shift;
    }
}
