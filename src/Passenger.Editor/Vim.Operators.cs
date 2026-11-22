using System.Text;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>What normal mode's operators and editing commands do to the text at every caret: delete, change, yank, shift
/// and change case over what a motion or object spans; put, replace, join and toggle case.</summary>
public sealed partial class Vim
{
    /// <summary>The motions whose delete or change always goes to register 1, however little it takes: the searches among
    /// them (n and N standing for / and ? too) as well.</summary>
    private static readonly HashSet<string> Jumps = ["%", "(", ")", "{", "}", "`", "n", "N", "*", "#", "g*", "g#"];

    /// <summary>One caret's stretch for an operator: the text from <see cref="Start"/> to <see cref="End"/>, or the lines
    /// <see cref="First"/> to <see cref="Last"/> when <see cref="Lines"/>; <see cref="Reached"/> the line after it an exclusive
    /// motion ended at the start of, -1 for any other.</summary>
    private readonly record struct Stretch(int Start, int End, bool Lines, int First, int Last, int Caret, int Landing = 0, bool Inclusive = false, int Reached = -1);

    private VimResult Operate(Command command, string op, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        List<Stretch> stretches = [];
        List<Selection> ranOut = [];
        foreach (Selection s in selections.Items)
        {
            if (SpanOf(command, op, text, s.Caret, view) is not ({ } span, int landing, bool inclusive))
            {
                ranOut.Add(Selection.At(ranOutAt ?? s.Caret));
                continue;
            }
            Stretch stretch = span.Linewise
                ? Whole(text, text.GetLineFromOffset(span.Start), text.GetLineFromOffset(span.End), s.Caret) with { Landing = landing, Reached = reachedPast ?? -1 }
                : new Stretch(span.Start, span.End, false, text.GetLineFromOffset(span.Start), text.GetLineFromOffset(span.End), s.Caret, landing, inclusive, reachedPast ?? -1);
            if (op is "g~" or "gu" or "gU" && !stretch.Lines && stretch.End == stretch.Start && stretch.Start == text.GetLineStart(stretch.First)
                && text.GetLine(stretch.First).Length > 0)
            {
                // Case over nothing at a line's start takes its first character on the first line, else the whole line.
                LineSpan line = text.GetLine(stretch.First);
                stretch = stretch with { End = stretch.First == 0 ? TextNavigation.NextGrapheme(text, line.Start) : line.End };
            }
            if (op == "d" && !stretch.Lines && stretch.Last > stretch.First && VimMotions.InIndent(text, stretch.Start) && OnlyBlanksFrom(text, stretch.End))
            {
                // A delete over lines from the indent to the line's blanks takes the lines whole.
                stretch = Whole(text, stretch.First, stretch.Last, s.Caret) with { Landing = stretch.Landing };
            }
            stretches.Add(stretch);
        }
        if (stretches.Count == 0)
        {
            // Nothing to act on: a count of words cut short leaves each caret where it ran out, as Vim's does.
            SelectionSet left = SelectionSet.Create(ranOut, selections.PrimaryIndex);
            if (command.Name == "$")
            {
                // A $ cut short aims j and k at the line's end all the same.
                Aimed(left, [.. Enumerable.Repeat(int.MaxValue, left.Count)]);
            }
            return left.SameAs(selections) ? VimResult.Held : new VimResult(true, left);
        }
        return Act(command, op, stretches, text, selections, view);
    }

    /// <summary>An operator done over the stretches it takes at the carets, the text it took kept in the registers; a
    /// block's (<paramref name="block"/>) kept as one, a piece for each of its lines (<paramref name="lines"/>).</summary>
    private VimResult Act(Command command, string op, List<Stretch> stretches, TextSnapshot text, SelectionSet selections, IVimView view, bool block = false,
        (int Top, int Bottom, int Left, int Right)? lines = null)
    {
        stretches.Sort((a, b) => a.Start.CompareTo(b.Start));
        if (op != "y" && view.Refuses is { } why)
        {
            return Refused(why, SelectionSet.Create([.. stretches.Select(t => Selection.At(Settled(text, t.Landing, breakEnds)))], Math.Min(selections.PrimaryIndex, stretches.Count - 1)));
        }
        VimRegister taken = TakenBy(text, stretches, block, lines, view.Indent.TabWidth);
        VimResult result = op switch
        {
            "d" => Delete(text, selections, stretches),
            "c" => Change(command, text, selections, stretches),
            "y" => Yank(text, selections, stretches, view),
            ">" or "<" => Shift(text, selections, stretches, op == ">", view),
            "=" => Reindent(text, selections, stretches),
            "gq" or "gw" => Wrap(op == "gw", text, selections, stretches, view),
            _ => Case(command, op, text, selections, stretches, view),
        };
        // An emptied text gives the registers nothing, as Vim's empty buffer gives them nothing.
        bool emptied = text.Length == 0 || intoEmptied;
        // Nothing taken leaves them, but a change of an empty line's place empties one, and a block's change or lines take pieces.
        bool empty = emptied || !(block && (op == "c" || (lines is { } over && over.Bottom > over.Top)))
            && stretches.All(t => !t.Lines && t.End == t.Start && !(op == "c" && t.Inclusive && VimMotions.LineOf(text, t.Start).Length == 0));
        if (op is "d" or "c" && !empty)
        {
            Registers.Delete(command.Register, taken, Jumps.Contains(command.Name));
            EraseMarks(text, stretches, op == "c", view);
        }
        else if (op == "y")
        {
            // A yank of nothing still yanks, leaving its register empty, as Vim's does.
            Registers.Yank(command.Register, taken);
        }
        if (op is not ("y" or "c"))
        {
            Remember(command);
        }
        return VimRegisters.IsClipboard(command.Register) && op is "d" or "c" or "y" ? result with { Clipboard = taken } : result;
    }

    /// <summary>The marks a mark command sets, which a delete of their line erases.</summary>
    private const string Marked = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>The marks on lines a delete or change takes out whole erased, as Vim erases them: a stretch of lines its
    /// own (a change's but its first, which it keeps), any other the lines between its ends.</summary>
    private static void EraseMarks(TextSnapshot text, List<Stretch> stretches, bool change, IVimView view)
    {
        foreach (char name in Marked)
        {
            if (view.Mark(name, text) is { } at && text.GetLineFromOffset(at) is int line
                && stretches.Any(s => s.Lines ? line >= s.First + (change ? 1 : 0) && line <= s.Last : line > s.First && line < s.Last))
            {
                view.EraseMark(name);
            }
        }
    }

    private static Stretch Whole(TextSnapshot text, int first, int last, int caret)
    {
        LineSpan end = text.GetLine(last);
        if (end.BreakLength > 0)
        {
            return new Stretch(text.GetLineStart(first), end.EndIncludingBreak, true, first, last, caret);
        }
        return first > 0
            ? new Stretch(text.GetLine(first - 1).End, end.End, true, first, last, caret)
            : new Stretch(0, end.End, true, first, last, caret);
    }

    private static Stretch? Mine(List<Stretch> stretches, int caret) => stretches.FindIndex(s => s.Caret == caret) is var i and >= 0 ? stretches[i] : null;

    private static bool OnlyBlanksFrom(TextSnapshot text, int offset)
    {
        LineSpan line = VimMotions.LineOf(text, offset);
        for (int p = offset; p < line.End; p++)
        {
            if (text[p] is not (' ' or '\t'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The stretches with those that overlap made one, in order: by their text for an edit, by their lines for
    /// the pieces a register keeps (<paramref name="pieces"/>).</summary>
    private static List<Stretch> Merged(List<Stretch> sorted, bool pieces = false)
    {
        List<Stretch> merged = [];
        foreach (Stretch s in sorted)
        {
            if (merged.Count > 0 && (pieces && s.Lines && merged[^1].Lines ? s.First <= merged[^1].Last : s.Start < merged[^1].End))
            {
                Stretch last = merged[^1];
                merged[^1] = last with { End = Math.Max(last.End, s.End), Last = Math.Max(last.Last, s.Last) };
                continue;
            }
            merged.Add(s);
        }
        return merged;
    }

    /// <summary>What an operator over the sorted stretches takes, as a register keeps it: a block's (<paramref name="block"/>)
    /// as one as wide as its columns (<paramref name="over"/>), else the stretches' text.</summary>
    private static VimRegister TakenBy(TextSnapshot text, List<Stretch> sorted, bool block, (int Top, int Bottom, int Left, int Right)? over, int tab) =>
        block && over is { } columns && sorted.All(t => t.First == t.Last)
            ? BlockTaken(text, sorted, columns, tab)
            : new([.. Merged(sorted, pieces: true).Select(m => Taken(text, m))], sorted[0].Lines) { Blockwise = block };

    /// <summary>A block's text as a register keeps it: a piece for each of its lines, a line ending where the block
    /// begins giving an empty one and a line short of it as many spaces as the block is wide (one more after $), the
    /// block as wide as its columns or after $ its longest piece.</summary>
    private static VimRegister BlockTaken(TextSnapshot text, List<Stretch> stretches, (int Top, int Bottom, int Left, int Right) block, int tab)
    {
        List<string?> found = [.. Enumerable.Range(block.Top, block.Bottom - block.Top + 1)
            .Select(line => stretches.FindIndex(t => t.First == line) is int at and >= 0 ? Taken(text, stretches[at])
                : TextNavigation.VisualColumn(text, text.GetLine(line).End, tab) >= block.Left ? string.Empty
                : null)];
        int longest = found.Max(p => p is null ? 0 : TextNavigation.VisualColumn(TextSnapshot.From(p), p.Length, tab));
        bool toEnd = block.Right == int.MaxValue;
        int width = toEnd ? Math.Max(1, longest) : block.Right - block.Left + 1;
        return new VimRegister([.. found.Select(p => p ?? new string(' ', toEnd ? longest + 1 : width))], false) { Blockwise = true, Width = width };
    }

    /// <summary>The text a stretch takes, as a register keeps it: line breaks "\n", whole lines each ending with one.</summary>
    private static string Taken(TextSnapshot text, Stretch s)
    {
        if (s.Lines)
        {
            int start = text.GetLineStart(s.First);
            return LineEndings.In(text.GetText(start, text.GetLine(s.Last).End - start), "\n") + "\n";
        }
        return LineEndings.In(text.GetText(s.Start, s.End - s.Start), "\n");
    }

    private VimResult Delete(TextSnapshot text, SelectionSet selections, List<Stretch> stretches)
    {
        List<Stretch> merged = Merged(stretches);
        List<TextEdit> edits = [.. merged.Where(s => s.End > s.Start).Select(s => new TextEdit(s.Start, s.End - s.Start, string.Empty))];
        if (edits.Count == 0)
        {
            // Nothing to delete (an empty block, the file's final line break alone): each caret goes to its stretch's start.
            return new VimResult(true, selections.With(c => Selection.At(Settled(text, Mine(stretches, c.Caret) is { } s ? s.Start : c.Caret, breakEnds))));
        }
        TextSnapshot after = text.Apply(edits);
        SelectionSet carets = selections.With(c =>
        {
            Stretch? mine = Mine(stretches, c.Caret);
            if (mine is not { } s)
            {
                return Selection.At(Settled(after, TextEditing.MapOffset(c.Caret, edits, false), breakEnds));
            }
            int start = TextEditing.MapOffset(s.Start, edits, false);
            if (!s.Lines)
            {
                return Selection.At(Settled(after, start, breakEnds));
            }
            int line = Math.Min(after.GetLineFromOffset(Math.Min(start, after.Length)), VimMotions.LastLine(after, breakEnds));
            return Selection.At(VimMotions.FirstNonBlank(after, line, breakEnds));
        });
        return new VimResult(true, Edits: [new EditPlan(edits, carets, EditKind.Other)]);
    }

    private VimResult Change(Command command, TextSnapshot text, SelectionSet selections, List<Stretch> stretches)
    {
        List<Stretch> merged = Merged(stretches);
        List<TextEdit> edits = [];
        foreach (Stretch s in merged)
        {
            if (s.Lines)
            {
                // Lines changed keep the first one's indent, as Vim's autoindent does.
                LineSpan first = text.GetLine(s.First);
                int start = first.Start;
                string indent = text.GetText(start, TextNavigation.IndentEnd(text, first) - start);
                edits.Add(new TextEdit(start, text.GetLine(s.Last).End - start, indent));
            }
            else if (s.End > s.Start)
            {
                edits.Add(new TextEdit(s.Start, s.End - s.Start, string.Empty));
            }
        }
        SelectionSet carets = selections.With(c =>
        {
            Stretch? mine = Mine(stretches, c.Caret);
            if (mine is not { } s)
            {
                return Selection.At(TextEditing.MapOffset(c.Caret, edits, false));
            }
            if (!s.Lines)
            {
                return Selection.At(TextEditing.MapOffset(s.Start, edits, false));
            }
            int start = text.GetLineStart(s.First);
            return Selection.At(TextEditing.MapOffset(start, edits, false) + (TextNavigation.IndentEnd(text, text.GetLine(s.First)) - start));
        });
        bool lines = merged.Any(s => s.Lines);
        EditPlan plan = new(edits, carets, EditKind.Other);
        return InsertAfter(text, plan, command, lines ? "cc" : "c");
    }

    private VimResult Yank(TextSnapshot text, SelectionSet selections, List<Stretch> stretches, IVimView view)
    {
        SelectionSet carets = selections.With(c =>
        {
            Stretch? mine = Mine(stretches, c.Caret);
            if (mine is not { } s)
            {
                return c;
            }
            return Selection.At(Settled(text, s.Lines ? s.Landing : s.Start, breakEnds));
        });
        return new VimResult(true, carets);
    }

    private VimResult Shift(TextSnapshot text, SelectionSet selections, List<Stretch> stretches, bool right, IVimView view, int times = 1)
    {
        IndentOptions indent = view.Indent;
        SortedSet<int> lines = [];
        foreach (Stretch s in stretches)
        {
            for (int line = s.First; line <= s.Last; line++)
            {
                lines.Add(line);
            }
        }
        List<TextEdit> edits = [];
        foreach (int number in lines)
        {
            LineSpan line = text.GetLine(number);
            if (line.Length == 0)
            {
                continue;
            }
            int end = TextNavigation.IndentEnd(text, line);
            int width = TextNavigation.VisualColumn(text, end, indent.TabWidth);
            int wanted = right ? width + (indent.Size * times) : Math.Max(0, width - (indent.Size * times));
            string now = text.GetText(line.Start, end - line.Start);
            string made = IndentOf(wanted, indent);
            if (made != now)
            {
                edits.Add(new TextEdit(line.Start, end - line.Start, made));
            }
        }
        TextSnapshot after = text.Apply(edits);
        SelectionSet carets = selections.With(c =>
        {
            Stretch? mine = Mine(stretches, c.Caret);
            return mine is { } s ? Selection.At(VimMotions.FirstNonBlank(after, s.First, breakEnds)) : Selection.At(Settled(after, TextEditing.MapOffset(c.Caret, edits, false), breakEnds));
        });
        return edits.Count == 0 ? new VimResult(true, carets) : new VimResult(true, Edits: [new EditPlan(edits, carets, EditKind.Other)]);
    }

    /// <summary>The lines the stretches span, those that meet or touch made one, in order.</summary>
    private static List<(int First, int Last)> LinesOf(List<Stretch> sorted)
    {
        List<(int First, int Last)> lines = [];
        foreach (Stretch s in sorted.OrderBy(s => s.First))
        {
            if (lines.Count > 0 && s.First <= lines[^1].Last + 1)
            {
                lines[^1] = (lines[^1].First, Math.Max(lines[^1].Last, s.Last));
                continue;
            }
            lines.Add((s.First, s.Last));
        }
        return lines;
    }

    /// <summary>=: the lines the stretches span laid out by the host as its Reformat Code lays them out, each caret on its
    /// stretch's first line's first non-blank.</summary>
    private VimResult Reindent(TextSnapshot text, SelectionSet selections, List<Stretch> stretches)
    {
        SelectionSet carets = selections.With(c => Mine(stretches, c.Caret) is { } s ? Selection.At(VimMotions.FirstNonBlank(text, s.First, breakEnds)) : c);
        return new VimResult(true, carets) { Request = new VimRequest(VimRequestKind.Reformat) { Lines = LinesOf(stretches) } };
    }

    /// <summary>gq and gw: the lines the stretches span wrapped at textwidth (else the width the view gives, else Vim's
    /// 79); gq leaves each caret on its last line's first non-blank, gw (<paramref name="stay"/>) where it was in the text.</summary>
    private VimResult Wrap(bool stay, TextSnapshot text, SelectionSet selections, List<Stretch> stretches, IVimView view)
    {
        int width = Options.TextWidth > 0 ? Options.TextWidth : view.LineLength > 0 ? view.LineLength : VimWrapping.DefaultWidth;
        VimWrapping.Wrapped wrapped = VimWrapping.Wrap(text, LinesOf(stretches), width, view.Indent, view.LineEnding, view.LineComment, Options.JoinSpaces);
        IReadOnlyList<TextEdit> edits = wrapped.Edits;
        TextSnapshot after = text.Apply(edits);
        SelectionSet carets = selections.With(c =>
        {
            if (stay || Mine(stretches, c.Caret) is not { } s)
            {
                return Selection.At(Settled(after, wrapped.Map(c.Caret, after), breakEnds));
            }
            // gq leaves the caret on the line its motion reached, the stretch's last where it took that line in.
            int end = TextEditing.MapOffset(s.Reached >= 0 ? text.GetLineStart(s.Reached) : text.GetLine(s.Last).End, edits, true);
            return Selection.At(VimMotions.FirstNonBlank(after, after.GetLineFromOffset(Math.Min(end, after.Length)), breakEnds));
        });
        return edits.Count == 0 ? new VimResult(true, carets) : new VimResult(true, Edits: [new EditPlan(edits, carets, EditKind.Other)]);
    }

    private static string IndentOf(int width, IndentOptions indent) =>
        indent.UseTabs && indent.TabWidth > 0 ? new string('\t', width / indent.TabWidth) + new string(' ', width % indent.TabWidth) : new string(' ', width);

    private VimResult Case(Command command, string op, TextSnapshot text, SelectionSet selections, List<Stretch> stretches, IVimView view)
    {
        List<TextEdit> edits = [];
        foreach (Stretch s in Merged(stretches))
        {
            int start = s.Lines ? text.GetLineStart(s.First) : s.Start;
            int end = s.Lines ? text.GetLine(s.Last).End : s.End;
            string was = text.GetText(start, end - start);
            string made = Cased(was, op);
            if (made != was)
            {
                edits.Add(new TextEdit(start, end - start, made));
            }
        }
        SelectionSet carets = selections.With(c =>
        {
            Stretch? mine = Mine(stretches, c.Caret);
            if (mine is not { } s)
            {
                return c;
            }
            if (!s.Lines)
            {
                return Selection.At(Settled(text, s.Start, breakEnds));
            }
            return Selection.At(Settled(text, s.Landing, breakEnds));
        });
        return edits.Count == 0 ? new VimResult(true, carets) : new VimResult(true, Edits: [new EditPlan(edits, carets, EditKind.Other)]);
    }

    /// <summary>Text with each character's case changed as g~, gu or gU (or ~) change it, its length kept.</summary>
    private static string Cased(string text, string op)
    {
        StringBuilder made = new(text.Length);
        foreach (char c in text)
        {
            made.Append(op switch
            {
                "gu" => char.ToLowerInvariant(c),
                "gU" => char.ToUpperInvariant(c),
                _ => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.IsLower(c) ? char.ToUpperInvariant(c) : c,
            });
        }
        return made.ToString();
    }

    private VimResult Put(Command command, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        Remember(command);
        if (VimRegisters.IsClipboard(command.Register))
        {
            if (replaying)
            {
                return VimResult.Held;
            }
            clipboardPut = (command.Name is "P" or "gP", command.Count, command.Name.StartsWith('g'));
            return new VimResult(true) { ReadsClipboard = true };
        }
        // An empty register puts nothing, and the keys after it go on.
        return Registers.Get(command.Register) is { } value
            ? Put(value, command.Name is "P" or "gP", command.Count, text, selections, view, cursorAfter: command.Name.StartsWith('g'))
            : new VimResult(true);
    }

    /// <summary>p and P: the register's text after (or before) each caret, or its lines below (or above) the caret's line,
    /// <paramref name="count"/> times; each caret its own piece where the register holds one for each. gp and gP
    /// (<paramref name="cursorAfter"/>) leave the caret just after what they put.</summary>
    private VimResult Put(VimRegister value, bool before, int count, TextSnapshot text, SelectionSet selections, IVimView view, bool cursorAfter = false)
    {
        if (value.Blockwise)
        {
            return PutBlock(value, before, count, text, selections, view, cursorAfter);
        }
        string eol = view.LineEnding;
        bool dealt = selections.Count > 1 && value.Parts.Count == selections.Count;
        List<(int At, string Text, int Caret)> puts = [];
        HashSet<int> linesPut = [];
        for (int i = 0; i < selections.Count; i++)
        {
            int caret = selections.Items[i].Caret;
            string piece = LineEndings.In(dealt ? value.Parts[i] : value.Text, eol);
            if (piece.Length == 0)
            {
                continue;
            }
            int times = Math.Clamp(count, 1, Math.Max(1, MostRepeatedText / piece.Length));
            string repeated = times == 1 ? piece : string.Concat(Enumerable.Repeat(piece, times));
            LineSpan line = VimMotions.LineOf(text, caret);
            if (value.Linewise)
            {
                if (!dealt && !linesPut.Add(line.Line))
                {
                    continue;
                }
                if (before)
                {
                    puts.Add((line.Start, repeated, 0));
                }
                else if (line.BreakLength > 0)
                {
                    puts.Add((line.EndIncludingBreak, repeated, 0));
                }
                else
                {
                    puts.Add((line.End, eol + repeated[..^eol.Length], eol.Length));
                }
                continue;
            }
            int at = before || line.Length == 0 ? caret : Math.Min(TextNavigation.NextGrapheme(text, caret), line.End);
            bool oneLine = !repeated.Contains('\n', StringComparison.Ordinal);
            puts.Add((at, repeated, oneLine ? repeated.Length - (repeated.Length > 1 && char.IsLowSurrogate(repeated[^1]) ? 2 : 1) : 0));
        }
        if (puts.Count == 0)
        {
            return VimResult.Held;
        }
        puts.Sort((a, b) => a.At.CompareTo(b.At));
        List<TextEdit> edits = [];
        List<int> landed = [];
        int shift = 0;
        foreach ((int at, string put, int caret) in puts)
        {
            if (edits.Count > 0 && edits[^1].Start == at)
            {
                edits[^1] = edits[^1] with { Text = edits[^1].Text + put };
            }
            else
            {
                edits.Add(new TextEdit(at, 0, put));
            }
            landed.Add(at + shift + (cursorAfter ? put.Length : caret));
            shift += put.Length;
        }
        TextSnapshot after = text.Apply(edits);
        List<Selection> carets = [.. landed.Select(p => Selection.At(!value.Linewise ? Settled(after, p, breakEnds)
            : cursorAfter ? after.GetLineStart(Math.Min(after.GetLineFromOffset(p), VimMotions.LastLine(after, breakEnds)))
            : VimMotions.FirstNonBlank(after, after.GetLineFromOffset(p), breakEnds)))];
        return new VimResult(true, Edits: [new EditPlan(edits, SelectionSet.Create(carets, Math.Min(selections.PrimaryIndex, carets.Count - 1)), EditKind.Other)]);
    }

    /// <summary>r: the <paramref name="n"/> characters from each caret replaced by the one typed (a line break replaces
    /// them all with one, indented as the line); a caret with fewer left on its line changes nothing.</summary>
    private static EditPlan? Replace(TextSnapshot text, SelectionSet selections, int n, string with, IVimView view)
    {
        List<TextEdit> edits = [];
        List<int> landings = [];
        foreach (Selection s in selections.Items)
        {
            LineSpan line = VimMotions.LineOf(text, s.Caret);
            int end = s.Caret;
            int taken = 0;
            while (taken < n && end < line.End)
            {
                end = TextNavigation.NextGrapheme(text, end);
                taken++;
            }
            if (taken < n || (edits.Count > 0 && edits[^1].End > s.Caret))
            {
                continue;
            }
            if (with == "\n")
            {
                // The new line takes its line's indent where text follows the break, as Vim's autoindent does.
                int rest = end;
                while (rest < line.End && text[rest] is ' ' or '\t')
                {
                    rest++;
                }
                string indent = rest < line.End ? text.GetText(line.Start, Math.Min(TextNavigation.IndentEnd(text, line), s.Caret) - line.Start) : string.Empty;
                edits.Add(new TextEdit(s.Caret, rest - s.Caret, view.LineEnding + indent));
                landings.Add(view.LineEnding.Length + Math.Max(0, indent.Length - 1));
                continue;
            }
            edits.Add(new TextEdit(s.Caret, end - s.Caret, string.Concat(Enumerable.Repeat(with, taken))));
            landings.Add((taken - 1) * with.Length);
        }
        if (edits.Count == 0)
        {
            return null;
        }
        int shift = 0;
        List<Selection> carets = [];
        for (int i = 0; i < edits.Count; i++)
        {
            carets.Add(Selection.At(edits[i].Start + shift + landings[i]));
            shift += edits[i].Text.Length - edits[i].Length;
        }
        return new EditPlan(edits, SelectionSet.Create(carets, Math.Min(selections.PrimaryIndex, carets.Count - 1)), EditKind.Other);
    }

    /// <summary>J: each caret's line joined with the <paramref name="n"/> - 1 after it (at least one): the next line's
    /// indent goes and <see cref="JoinSpace"/> comes between; gJ (<paramref name="spaced"/> false) joins them as they are.
    /// The caret stands where the last join met.</summary>
    private EditPlan? Join(TextSnapshot text, SelectionSet selections, int n, bool spaced = true, bool sentenceSpaces = false)
    {
        int lastLine = VimMotions.LastLine(text, breakEnds);
        List<TextEdit> edits = [];
        List<(int LineStart, int Landing)> landings = [];
        int reached = -1;
        Span<char> ends = stackalloc char[2];
        Span<char> starts = stackalloc char[1];
        foreach (Selection s in selections.Items)
        {
            int first = text.GetLineFromOffset(s.Caret);
            if (first >= lastLine || first <= reached)
            {
                continue;
            }
            int last = (int)Math.Min(first + (long)Math.Max(n, 2) - 1, lastLine);
            int joinedLength = text.GetLine(first).Length;
            int joinedFrom = text.GetLine(first).Start;
            int landing = 0;
            for (int line = first; line < last; line++)
            {
                LineSpan here = text.GetLine(line);
                LineSpan next = text.GetLine(line + 1);
                int body = spaced ? TextNavigation.IndentEnd(text, next) : next.Start;
                string space = string.Empty;
                if (spaced)
                {
                    Span<char> end = ends[..Math.Min(2, here.End - joinedFrom)];
                    Span<char> start = starts[..Math.Min(1, next.End - body)];
                    text.CopyTo(here.End - end.Length, end);
                    text.CopyTo(body, start);
                    space = JoinSpace(end, start, joinedLength > 0, sentenceSpaces);
                }
                edits.Add(new TextEdit(here.End, body - here.End, space));
                landing = joinedLength;
                joinedLength += space.Length + (next.End - body);
                joinedFrom = body;
            }
            landings.Add((text.GetLineStart(first), landing));
            reached = last;
        }
        if (edits.Count == 0)
        {
            return null;
        }
        TextSnapshot after = text.Apply(edits);
        List<Selection> carets = [.. landings.Select(at => Selection.At(Settled(after, Math.Min(TextEditing.MapOffset(at.LineStart, edits, false) + at.Landing, after.Length), breakEnds)))];
        return new EditPlan(edits, SelectionSet.Create(carets, Math.Min(selections.PrimaryIndex, carets.Count - 1)), EditKind.Other);
    }

    /// <summary>What J and gq put where a line meets the next one joined to it, as Vim 9.1's do: nothing before an empty
    /// line or a ')', or with nothing joined yet; one space after an empty line; nothing after a blank, but a second space
    /// after a sentence's end and a space with <paramref name="sentenceSpaces"/> (joinspaces); else one space, two after a
    /// sentence's end with it. <paramref name="line"/> ends as the line joined (the first whole, one after from its
    /// indent's end), <paramref name="next"/> begins as the next one from its indent's end.</summary>
    internal static string JoinSpace(ReadOnlySpan<char> line, ReadOnlySpan<char> next, bool joinedAny, bool sentenceSpaces)
    {
        if (next.IsEmpty || !joinedAny || next[0] == ')')
        {
            return string.Empty;
        }
        if (line.IsEmpty)
        {
            return " ";
        }
        if (line[^1] is ' ' or '\t')
        {
            return sentenceSpaces && line[^1] == ' ' && line.Length > 1 && line[^2] is '.' or '!' or '?' ? " " : string.Empty;
        }
        return sentenceSpaces && line[^1] is '.' or '!' or '?' ? "  " : " ";
    }

    /// <summary>~: the case of the <paramref name="n"/> characters from each caret on its line toggled, the caret going past them.</summary>
    private EditPlan? Toggle(TextSnapshot text, SelectionSet selections, int n)
    {
        List<TextEdit> edits = [];
        List<int> ends = [];
        foreach (Selection s in selections.Items)
        {
            LineSpan line = VimMotions.LineOf(text, s.Caret);
            int end = VimMotions.Repeated(n, s.Caret, p => Math.Min(TextNavigation.NextGrapheme(text, p), line.End));
            if (end == s.Caret || (edits.Count > 0 && edits[^1].End > s.Caret))
            {
                continue;
            }
            string was = text.GetText(s.Caret, end - s.Caret);
            edits.Add(new TextEdit(s.Caret, end - s.Caret, Cased(was, "~")));
            ends.Add(end);
        }
        if (edits.Count == 0)
        {
            return null;
        }
        TextSnapshot after = text.Apply(edits);
        List<Selection> carets = [.. ends.Select(e => Selection.At(Settled(after, e, breakEnds)))];
        return new EditPlan(edits, SelectionSet.Create(carets, Math.Min(selections.PrimaryIndex, carets.Count - 1)), EditKind.Other);
    }
}
