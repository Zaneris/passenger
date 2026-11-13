using System.Text;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>What normal mode's operators and editing commands do to the text at every caret: delete, change, yank, shift
/// and change case over what a motion or object spans; put, replace, join and toggle case.</summary>
public sealed partial class Vim
{
    /// <summary>The motions whose delete always goes to register 1, however little it takes.</summary>
    private static readonly HashSet<string> Jumps = ["%", "(", ")", "{", "}"];

    /// <summary>One caret's stretch for an operator: the text from <see cref="Start"/> to <see cref="End"/>, or the lines
    /// <see cref="First"/> to <see cref="Last"/> when <see cref="Lines"/>.</summary>
    private readonly record struct Stretch(int Start, int End, bool Lines, int First, int Last, int Caret, int Landing = 0);

    private VimResult Operate(Command command, string op, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        List<Stretch> stretches = [];
        foreach (Selection s in selections.Items)
        {
            if (SpanOf(command, op, text, s.Caret, view) is not ({ } span, int landing))
            {
                continue;
            }
            Stretch stretch = span.Linewise
                ? Whole(text, text.GetLineFromOffset(span.Start), text.GetLineFromOffset(span.End), s.Caret) with { Landing = landing }
                : new Stretch(span.Start, span.End, false, text.GetLineFromOffset(span.Start), text.GetLineFromOffset(span.End), s.Caret, landing);
            if (op == "d" && !stretch.Lines && stretch.Last > stretch.First && VimMotions.InIndent(text, stretch.Start) && OnlyBlanksFrom(text, stretch.End))
            {
                // A delete over lines from the indent to the line's blanks takes the lines whole.
                stretch = Whole(text, stretch.First, stretch.Last, s.Caret) with { Landing = stretch.Landing };
            }
            stretches.Add(stretch);
        }
        if (stretches.Count == 0)
        {
            return VimResult.Held;
        }
        return Act(command, op, stretches, text, selections, view);
    }

    /// <summary>An operator done over the stretches it takes at the carets, the text it took kept in the registers; a
    /// block's (<paramref name="block"/>) kept as one.</summary>
    private VimResult Act(Command command, string op, List<Stretch> stretches, TextSnapshot text, SelectionSet selections, IVimView view, bool block = false)
    {
        stretches.Sort((a, b) => a.Start.CompareTo(b.Start));
        VimRegister taken = new([.. Merged(stretches, pieces: true).Select(m => Taken(text, m))], stretches[0].Lines) { Blockwise = block };
        VimResult result = op switch
        {
            "d" => Delete(text, selections, stretches),
            "c" => Change(command, text, selections, stretches),
            "y" => Yank(text, selections, stretches, view),
            ">" or "<" => Shift(text, selections, stretches, op == ">", view),
            _ => Case(command, op, text, selections, stretches, view),
        };
        bool empty = stretches.All(t => !t.Lines && t.End == t.Start);
        if (op is "d" or "c" && !empty)
        {
            Registers.Delete(command.Register, taken, Jumps.Contains(command.Name));
        }
        else if (op == "y" && !empty)
        {
            Registers.Yank(command.Register, taken);
        }
        if (op is not ("y" or "c"))
        {
            Remember(command);
        }
        return VimRegisters.IsClipboard(command.Register) && op is "d" or "c" or "y" ? result with { Clipboard = taken } : result;
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

    private static VimResult Delete(TextSnapshot text, SelectionSet selections, List<Stretch> stretches)
    {
        List<Stretch> merged = Merged(stretches);
        List<TextEdit> edits = [.. merged.Where(s => s.End > s.Start).Select(s => new TextEdit(s.Start, s.End - s.Start, string.Empty))];
        if (edits.Count == 0)
        {
            return new VimResult(true);
        }
        TextSnapshot after = text.Apply(edits);
        SelectionSet carets = selections.With(c =>
        {
            Stretch? mine = Mine(stretches, c.Caret);
            if (mine is not { } s)
            {
                return Selection.At(Settled(after, TextEditing.MapOffset(c.Caret, edits, false)));
            }
            int start = TextEditing.MapOffset(s.Start, edits, false);
            if (!s.Lines)
            {
                return Selection.At(Settled(after, start));
            }
            int line = Math.Min(after.GetLineFromOffset(Math.Min(start, after.Length)), VimMotions.LastLine(after));
            return Selection.At(VimMotions.FirstNonBlank(after, line));
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

    private static VimResult Yank(TextSnapshot text, SelectionSet selections, List<Stretch> stretches, IVimView view)
    {
        SelectionSet carets = selections.With(c =>
        {
            Stretch? mine = Mine(stretches, c.Caret);
            if (mine is not { } s)
            {
                return c;
            }
            return Selection.At(Settled(text, s.Lines ? s.Landing : s.Start));
        });
        return new VimResult(true, carets);
    }

    private static VimResult Shift(TextSnapshot text, SelectionSet selections, List<Stretch> stretches, bool right, IVimView view, int times = 1)
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
            int width = VimMotions.ColumnOf(text, end, indent.TabWidth);
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
            return mine is { } s ? Selection.At(VimMotions.FirstNonBlank(after, s.First)) : Selection.At(Settled(after, TextEditing.MapOffset(c.Caret, edits, false)));
        });
        return edits.Count == 0 ? new VimResult(true, carets) : new VimResult(true, Edits: [new EditPlan(edits, carets, EditKind.Other)]);
    }

    private static string IndentOf(int width, IndentOptions indent) =>
        indent.UseTabs && indent.TabWidth > 0 ? new string('\t', width / indent.TabWidth) + new string(' ', width % indent.TabWidth) : new string(' ', width);

    private static VimResult Case(Command command, string op, TextSnapshot text, SelectionSet selections, List<Stretch> stretches, IVimView view)
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
                return Selection.At(Settled(text, s.Start));
            }
            return Selection.At(Settled(text, s.Landing));
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
    private static VimResult Put(VimRegister value, bool before, int count, TextSnapshot text, SelectionSet selections, IVimView view, bool cursorAfter = false)
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
        List<Selection> carets = [.. landed.Select(p => Selection.At(!value.Linewise ? Settled(after, p)
            : cursorAfter ? after.GetLineStart(Math.Min(after.GetLineFromOffset(p), VimMotions.LastLine(after)))
            : VimMotions.FirstNonBlank(after, after.GetLineFromOffset(p))))];
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
    /// indent goes and one space comes between, none after white, before a ')' or with an empty side; gJ
    /// (<paramref name="spaced"/> false) joins them as they are. The caret stands where the last join met.</summary>
    private static EditPlan? Join(TextSnapshot text, SelectionSet selections, int n, bool spaced = true)
    {
        int lastLine = VimMotions.LastLine(text);
        List<TextEdit> edits = [];
        List<(int LineStart, int Landing)> landings = [];
        int reached = -1;
        foreach (Selection s in selections.Items)
        {
            int first = text.GetLineFromOffset(s.Caret);
            if (first >= lastLine || first <= reached)
            {
                continue;
            }
            int last = (int)Math.Min(first + (long)Math.Max(n, 2) - 1, lastLine);
            int joinedLength = text.GetLine(first).Length;
            char end = joinedLength > 0 ? text[text.GetLine(first).End - 1] : '\n';
            int landing = 0;
            for (int line = first; line < last; line++)
            {
                LineSpan here = text.GetLine(line);
                LineSpan next = text.GetLine(line + 1);
                int body = spaced ? TextNavigation.IndentEnd(text, next) : next.Start;
                bool empty = body >= next.End;
                // One space even after a sentence's end, as most editors and code's comments have it.
                string space = !spaced || empty || joinedLength == 0 || end is ' ' or '\t' || text[body] == ')' ? string.Empty : " ";
                edits.Add(new TextEdit(here.End, body - here.End, space));
                landing = joinedLength;
                joinedLength += space.Length + (next.End - body);
                end = empty ? end : text[next.End - 1];
            }
            landings.Add((text.GetLineStart(first), landing));
            reached = last;
        }
        if (edits.Count == 0)
        {
            return null;
        }
        TextSnapshot after = text.Apply(edits);
        List<Selection> carets = [.. landings.Select(at => Selection.At(Settled(after, Math.Min(TextEditing.MapOffset(at.LineStart, edits, false) + at.Landing, after.Length))))];
        return new EditPlan(edits, SelectionSet.Create(carets, Math.Min(selections.PrimaryIndex, carets.Count - 1)), EditKind.Other);
    }

    /// <summary>~: the case of the <paramref name="n"/> characters from each caret on its line toggled, the caret going past them.</summary>
    private static EditPlan? Toggle(TextSnapshot text, SelectionSet selections, int n)
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
        List<Selection> carets = [.. ends.Select(e => Selection.At(Settled(after, e)))];
        return new EditPlan(edits, SelectionSet.Create(carets, Math.Min(selections.PrimaryIndex, carets.Count - 1)), EditKind.Other);
    }
}
