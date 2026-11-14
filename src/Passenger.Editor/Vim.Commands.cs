using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Normal mode's commands: a count and a register, then a motion, an operator with its own count and a motion,
/// a text object or itself doubled, or one of the commands that edit alone.</summary>
public sealed partial class Vim
{
    /// <summary>A command read whole: its keys after the count (for . to make again), the counts multiplied (0 for none
    /// typed), the register named, the operator if any, and the motion, object or command with the key it takes.</summary>
    private sealed record Command(IReadOnlyList<string> Keys, int Count, char? Register, string? Operator, string Name, string? Argument);

    private static readonly HashSet<string> SingleMotions =
        ["h", "l", "j", "k", "0", "^", "$", "|", "w", "W", "b", "B", "e", "E", "G", ";", ",", "%", "{", "}", "(", ")", "H", "M", "L", "<BS>", "<CR>", " ", "+", "-", "_", "n", "N", "*", "#"];

    private static readonly HashSet<string> GMotions = ["g", "e", "E", "j", "k", "_", "*", "#"];

    private static readonly HashSet<string> Simple = ["x", "X", "s", "S", "C", "D", "Y", "J", "~", "i", "a", "I", "A", "o", "O", "u", "p", "P", ".", "v", "V", "<C-v>", "R", "<C-o>", "<C-i>", "<Tab>"];

    /// <summary>The commands that change the text, or begin insert or replace mode, without an operator: refused alone where the
    /// text refuses changes (x and its kind are operators' and refused by the operator).</summary>
    private static readonly HashSet<string> Changing = ["i", "a", "I", "A", "o", "O", "R", "u", "p", "P", "gp", "gP", "r", "J", "gJ", "~"];

    /// <summary>The commands g begins that are neither motions nor operators.</summary>
    private static readonly HashSet<string> GCommands = ["v", "J", "p", "P"];

    /// <summary>The command the keys make, null where they make none; <paramref name="incomplete"/> while more keys may
    /// still make one.</summary>
    private static Command? Parse(List<string> keys, out bool incomplete)
    {
        incomplete = false;
        int i = 0;
        long count = 0;
        long current = 0;
        char? register = null;
        while (i < keys.Count)
        {
            string key = keys[i];
            if (key.Length == 1 && char.IsAsciiDigit(key[0]) && (key[0] != '0' || current > 0))
            {
                current = Math.Min(current * 10 + (key[0] - '0'), int.MaxValue);
                i++;
            }
            else if (key == "\"")
            {
                if (i + 1 >= keys.Count)
                {
                    incomplete = true;
                    return null;
                }
                if (keys[i + 1].Length != 1 || !VimRegisters.Names(keys[i + 1][0]))
                {
                    return null;
                }
                register = keys[i + 1][0];
                count = Multiply(count, current);
                current = 0;
                i += 2;
            }
            else
            {
                break;
            }
        }
        count = Multiply(count, current);
        if (i >= keys.Count)
        {
            incomplete = true;
            return null;
        }
        // What . presses again: the register and the command, never a count, which it may replace.
        List<string> body = register is { } named ? ["\"", named.ToString()] : [];
        string first = keys[i];
        string? op = first is "d" or "c" or "y" or ">" or "<" or "=" ? first : null;
        if (op is null && first == "g")
        {
            if (i + 1 >= keys.Count)
            {
                incomplete = true;
                return null;
            }
            op = keys[i + 1] is "~" or "u" or "U" or "q" or "w" ? "g" + keys[i + 1] : null;
        }
        if (op is null)
        {
            (string? name, string? argument, int used) = Motion(keys, i, out incomplete);
            if (name is null && !incomplete && Simple.Contains(first))
            {
                (name, used) = (first, 1);
            }
            if (name is null && !incomplete && first == "m")
            {
                if (i + 1 >= keys.Count)
                {
                    incomplete = true;
                    return null;
                }
                (name, argument, used) = keys[i + 1].Length == 1 ? (first, keys[i + 1], 2) : (null, null, 0);
            }
            if (name is null && !incomplete && first is "q" or "@")
            {
                if (i + 1 >= keys.Count)
                {
                    incomplete = true;
                    return null;
                }
                string which = keys[i + 1];
                bool fits = which.Length == 1 && (char.IsAsciiLetterOrDigit(which[0]) || which[0] == '"' || (first == "@" && which[0] is '@' or ':'));
                (name, argument, used) = fits ? (first, which, 2) : (null, null, 0);
            }
            if (name is null && !incomplete && first == "g" && i + 1 < keys.Count && GCommands.Contains(keys[i + 1]))
            {
                (name, used) = ("g" + keys[i + 1], 2);
            }
            if (name is null && !incomplete && first == "r")
            {
                if (i + 1 >= keys.Count)
                {
                    incomplete = true;
                    return null;
                }
                (name, argument, used) = (first, keys[i + 1] == "<CR>" ? "\n" : VimMotions.Typed(keys[i + 1]), 2);
                if (argument is null)
                {
                    return null;
                }
            }
            return name is null || i + used != keys.Count ? null : new Command([.. body, .. keys.Skip(i)], (int)count, register, null, name, argument);
        }
        body.AddRange(keys.Skip(i).Take(op.Length));
        i += op.Length;
        long second = 0;
        while (i < keys.Count && keys[i].Length == 1 && char.IsAsciiDigit(keys[i][0]) && (keys[i][0] != '0' || second > 0))
        {
            second = Math.Min(second * 10 + (keys[i][0] - '0'), int.MaxValue);
            i++;
        }
        count = Multiply(count, second);
        if (i >= keys.Count)
        {
            incomplete = true;
            return null;
        }
        body.AddRange(keys.Skip(i));
        string target = keys[i];
        string doubled = op.Length == 1 ? op : op[1..];
        if (target == doubled || (op.Length == 2 && target == "g" && i + 1 < keys.Count && keys[i + 1] == doubled))
        {
            int used = target == "g" ? 2 : 1;
            return i + used == keys.Count ? new Command(body, (int)count, register, op, "line", null) : null;
        }
        if (op.Length == 2 && target == "g" && i + 1 >= keys.Count)
        {
            incomplete = true;
            return null;
        }
        if (target is "i" or "a")
        {
            if (i + 1 >= keys.Count)
            {
                incomplete = true;
                return null;
            }
            return i + 2 == keys.Count ? new Command(body, (int)count, register, op, target, keys[i + 1]) : null;
        }
        (string? motion, string? arg, int took) = Motion(keys, i, out incomplete);
        return motion is null || i + took != keys.Count ? null : new Command(body, (int)count, register, op, motion, arg);
    }

    private static long Multiply(long count, long current) => current == 0 ? count : count == 0 ? current : Math.Min(count * current, int.MaxValue);

    /// <summary>The motion the keys from <paramref name="at"/> make, the key it takes after it and how many keys it used.</summary>
    private static (string? Name, string? Argument, int Used) Motion(List<string> keys, int at, out bool incomplete)
    {
        incomplete = false;
        string key = keys[at];
        if (SingleMotions.Contains(key))
        {
            return (key, null, 1);
        }
        if (key is "g" or "f" or "F" or "t" or "T" or "'" or "`")
        {
            if (at + 1 >= keys.Count)
            {
                incomplete = true;
                return (null, null, 0);
            }
            if (key == "g")
            {
                return GMotions.Contains(keys[at + 1]) ? ("g" + keys[at + 1], null, 2) : (null, null, 0);
            }
            if (key is "'" or "`")
            {
                return keys[at + 1].Length == 1 ? (key, keys[at + 1], 2) : (null, null, 0);
            }
            return VimMotions.Typed(keys[at + 1]) is { } typed ? (key, typed, 2) : (null, null, 0);
        }
        return (null, null, 0);
    }

    private VimResult Run(Command command, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        int n = Math.Max(command.Count, 1);
        if (command is not { Operator: null, Name: "j" or "k" or "$" })
        {
            // Any other command sets the column j and k aim for afresh, even where the caret stays.
            wantedFor = null;
        }
        changeExtent = null;
        if (command.Operator is { } op)
        {
            return Operate(command, op, text, selections, view);
        }
        if (view.Refuses is { } why && (Changing.Contains(command.Name) || command.Name == "." && lastChange is not null))
        {
            return Refused(why);
        }
        switch (command.Name)
        {
            case "v":
                return EnterVisual(VimMode.Visual, command.Count, text, selections, view);
            case "V":
                return EnterVisual(VimMode.VisualLine, command.Count, text, selections, view);
            case "<C-v>":
                return EnterVisual(VimMode.VisualBlock, command.Count, text, selections, view);
            case "gv":
                return Reselect(text, view);
            case "gJ":
                return Changed(command, Join(text, selections, n, spaced: false));
            case "R":
                return EnterReplace(command, selections);
            case "@" when command.Argument == ":":
                return Registers.LastCommand is { } again ? Ex(again, text, selections, view) : Said("E30: No previous command line");
            case "q" or "@":
                return Macro(command);
            case "m":
                return SetMark(command, selections, view);
            case "<C-o>":
                return Asked(VimRequestKind.Older) with { Request = new VimRequest(VimRequestKind.Older, Count: n) };
            case "<C-i>" or "<Tab>":
                return Asked(VimRequestKind.Newer) with { Request = new VimRequest(VimRequestKind.Newer, Count: n) };
            case "j" or "k" or "gj" or "gk" when command.Operator is null:
                return Vertically(text, selections, command.Name is "j" or "gj" ? n : -n, command.Name.StartsWith('g'), view);
            case "x":
                return Operate(command with { Name = "l" }, "d", text, selections, view);
            case "X":
                return Operate(command with { Name = "h" }, "d", text, selections, view);
            case "D":
                return Operate(command with { Name = "$" }, "d", text, selections, view);
            case "C":
                return Operate(command with { Name = "$" }, "c", text, selections, view);
            case "s":
                return Operate(command with { Name = "l" }, "c", text, selections, view);
            case "S":
                return Operate(command with { Name = "line" }, "c", text, selections, view);
            case "Y":
                return Operate(command with { Name = "line" }, "y", text, selections, view);
            case "i":
                return Insert(text, selections, command, "i");
            case "a":
                return Insert(text, selections.With(s => Selection.At(Math.Min(TextNavigation.NextGrapheme(text, s.Caret), VimMotions.LineOf(text, s.Caret).End))), command, "a");
            case "I":
                return Insert(text, selections.With(s => Selection.At(TextNavigation.IndentEnd(text, VimMotions.LineOf(text, s.Caret)))), command, "I");
            case "A":
                return Insert(text, selections.With(s => Selection.At(VimMotions.LineOf(text, s.Caret).End)), command, "A");
            case "o" or "O":
                return InsertAfter(text, TextEditing.StartNewLine(text, selections, view.LineEnding, view.Indent, above: command.Name == "O", view.Markup), command, command.Name);
            case "u":
                return new VimResult(true, Action: EditorActions.Undo, Repeat: Math.Min(n, MostRepeated));
            case "p" or "P" or "gp" or "gP":
                return Put(command, text, selections, view);
            case ".":
                return Again(command.Count, text, selections, view);
            case "r":
                return Changed(command, Replace(text, selections, n, command.Argument!, view));
            case "J":
                return Changed(command, Join(text, selections, n, spaced: true, Options.JoinSpaces));
            case "~":
                return Changed(command, Toggle(text, selections, n));
        }
        return MoveTo(command, text, selections, view);
    }

    /// <summary>A change made, kept for . to make again; nothing changed where there is no plan.</summary>
    private VimResult Changed(Command command, EditPlan? plan)
    {
        if (plan is null)
        {
            return VimResult.Held;
        }
        Remember(command);
        return new VimResult(true, Edits: [plan]);
    }

    private VimResult MoveTo(Command command, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        bool any = false;
        SelectionSet moved = selections.With(s =>
        {
            if (Target(command, text, s.Caret, operating: false, view) is not { } target)
            {
                return s;
            }
            any = true;
            return Selection.At(Settled(text, target.Offset));
        });
        if (!any)
        {
            return VimResult.Held;
        }
        VimResult result = command.Name == "$" ? Aimed(moved, [.. Enumerable.Repeat(int.MaxValue, moved.Count)]) : new VimResult(true, moved);
        int from = selections.Primary.Caret;
        if (Jumping.Contains(command.Name) && moved.Primary.Caret != from)
        {
            view.SetMark('\'', from);
            result = result with { JumpedFrom = from };
        }
        return result;
    }

    /// <summary>Where a motion takes a caret, and how an operator takes the text to there; null where it cannot move.</summary>
    private VimTarget? Target(Command command, TextSnapshot text, int caret, bool operating, IVimView view)
    {
        int count = command.Count;
        int n = Math.Max(count, 1);
        string? argument = command.Argument;
        switch (command.Name)
        {
            case "h":
                // Under an operator, h at the line's start and l at its end take nothing rather than fail.
                return VimMotions.Left(text, caret, n, crossing: false) ?? (operating ? new VimTarget(caret, VimMotionKind.Exclusive) : null);
            case "<BS>":
                return VimMotions.Left(text, caret, n, crossing: true);
            case "l":
                return VimMotions.Right(text, caret, n, crossing: false, operating) ?? (operating ? new VimTarget(caret, VimMotionKind.Exclusive) : null);
            case " ":
                return VimMotions.Right(text, caret, n, crossing: true, operating);
            case "0":
                return new VimTarget(VimMotions.LineOf(text, caret).Start, VimMotionKind.Exclusive);
            case "^":
                return new VimTarget(Settled(text, TextNavigation.IndentEnd(text, VimMotions.LineOf(text, caret))), VimMotionKind.Exclusive);
            case "$":
                return VimMotions.LineEnd(text, caret, n);
            case "|":
                return new VimTarget(VimMotions.OffsetAtColumn(text, text.GetLineFromOffset(caret), n - 1, view.Indent.TabWidth), VimMotionKind.Exclusive);
            case "w" or "W":
                return VimMotions.WordStart(text, caret, n, command.Name == "W", operating);
            case "b" or "B":
                return VimMotions.WordBack(text, caret, n, command.Name == "B");
            case "e" or "E":
                return VimMotions.WordEnd(text, caret, n, command.Name == "E");
            case "ge" or "gE":
                return VimMotions.WordEndBack(text, caret, n, command.Name == "gE");
            case "gg":
                return Line(text, count == 0 ? 0 : Math.Min(count - 1, VimMotions.LastLine(text)));
            case "G":
                return Line(text, count == 0 ? VimMotions.LastLine(text) : Math.Min(count - 1, VimMotions.LastLine(text)));
            case "j" or "k" or "+" or "-" or "<CR>" or "_":
                long lines = command.Name switch { "j" or "+" or "<CR>" => n, "_" => n - 1, _ => -n };
                if (VimMotions.LinesAway(text, caret, lines) is not { } line)
                {
                    return null;
                }
                // j and k keep the caret's column, the others go to the line's first non-blank.
                return command.Name is "j" or "k"
                    ? new VimTarget(VimMotions.OffsetAtColumn(text, line, CaretColumn(text, caret, view.Indent.TabWidth), view.Indent.TabWidth), VimMotionKind.Linewise)
                    : Line(text, line);
            case "gj" or "gk":
                int row = view.RowsAway(text, caret, command.Name == "gj" ? n : -n);
                return row == caret ? null : new VimTarget(row, VimMotionKind.Exclusive);
            case "g_":
                if (n > 1 && text.GetLineFromOffset(caret) >= VimMotions.LastLine(text))
                {
                    return null;
                }
                int last = (int)Math.Min(text.GetLineFromOffset(caret) + (long)n - 1, VimMotions.LastLine(text));
                LineSpan span = text.GetLine(last);
                int end = span.End;
                while (end > span.Start && text[end - 1] is ' ' or '\t')
                {
                    end--;
                }
                return new VimTarget(Settled(text, Math.Max(span.Start, VimMotions.StepBack(text, end))), VimMotionKind.Inclusive);
            case "f" or "F" or "t" or "T":
                lastFind = (argument!, command.Name is "F" or "T", command.Name is "t" or "T");
                return VimMotions.Find(text, caret, n, argument!, command.Name is "F" or "T", command.Name is "t" or "T", again: false);
            case ";" or ",":
                return lastFind is { } find ? VimMotions.Find(text, caret, n, find.Target, find.Back != (command.Name == ","), find.Till, again: true) : null;
            case "%":
                return VimMotions.MatchPair(text, caret, count);
            case "{" or "}":
                return VimMotions.Paragraph(text, caret, n, command.Name == "}");
            case "(" or ")":
                return VimMotions.Sentence(text, caret, n, command.Name == ")");
            case "n" or "N":
                return SearchTarget(text, caret, n, reverse: command.Name == "N");
            case "*" or "#" or "g*" or "g#":
                return WordSearch(text, caret, n, back: command.Name.EndsWith('#'), whole: !command.Name.StartsWith('g'));
            case "'" or "`":
                return MarkTarget(text, argument!, exact: command.Name == "`", view);
            case "H" or "M" or "L":
                VimScreenLine at = command.Name switch { "H" => VimScreenLine.Top, "L" => VimScreenLine.Bottom, _ => VimScreenLine.Middle };
                return Line(text, Math.Clamp(view.LineShown(text, at, n), 0, VimMotions.LastLine(text)));
        }
        return null;
    }

    private static VimTarget Line(TextSnapshot text, int line) => new(VimMotions.FirstNonBlank(text, line), VimMotionKind.Linewise);

    private VimResult Vertically(TextSnapshot text, SelectionSet selections, int lines, bool rows, IVimView view)
    {
        int tab = view.Indent.TabWidth;
        if (rows)
        {
            SelectionSet stepped = selections.With(s => Selection.At(Settled(text, view.RowsAway(text, s.Caret, lines))));
            return stepped.SameAs(selections) ? VimResult.Held : new VimResult(true, stepped);
        }
        int[] columns = wantedFor is { } known && known.SameAs(selections) ? wanted : [.. selections.Items.Select(s => CaretColumn(text, s.Caret, tab))];
        List<Selection> moved = [];
        for (int i = 0; i < selections.Count; i++)
        {
            int caret = selections.Items[i].Caret;
            moved.Add(VimMotions.LinesAway(text, caret, lines) is { } line
                ? Selection.At(columns[i] == int.MaxValue ? Settled(text, text.GetLine(line).End) : VimMotions.OffsetAtColumn(text, line, columns[i], tab))
                : selections.Items[i]);
        }
        SelectionSet result = SelectionSet.Create(moved, selections.PrimaryIndex);
        return result.SameAs(selections) ? VimResult.Held : Aimed(result, columns);
    }

    /// <summary>The display column a normal-mode caret stands at: a tab's last cell, as its block is drawn over the tab.</summary>
    private static int CaretColumn(TextSnapshot text, int caret, int tabWidth)
    {
        int column = VimMotions.ColumnOf(text, caret, tabWidth);
        return caret < text.Length && text[caret] == '\t' ? VimMotions.ColumnOf(text, caret + 1, tabWidth) - 1 : column;
    }

    private VimResult Aimed(SelectionSet moved, int[] columns)
    {
        bool kept = moved.Count == columns.Length;
        wantedFor = kept ? moved : null;
        wanted = kept ? columns : [];
        return new VimResult(true, moved);
    }

    /// <summary>The text an operator takes at one caret, from a motion, a text object or the operator doubled, and where a
    /// yank or a change of case over whole lines leaves the caret; null where the motion fails or there is no such object.</summary>
    private (VimSpan Span, int Landing)? SpanOf(Command command, string op, TextSnapshot text, int caret, IVimView view)
    {
        int n = Math.Max(command.Count, 1);
        if (command.Name == "line")
        {
            int first = text.GetLineFromOffset(caret);
            if (n > 1 && first >= VimMotions.LastLine(text))
            {
                // Lines asked for below the last fail, as j would there.
                return null;
            }
            int last = (int)Math.Min(first + (long)n - 1, VimMotions.LastLine(text));
            // A yank leaves the caret; a change of case over one line goes to its first non-blank, as Vim's do.
            int landing = n == 1 && op != "y" ? Math.Min(caret, VimMotions.FirstNonBlank(text, first)) : caret;
            return (new VimSpan(text.GetLineStart(first), text.GetLineStart(last), true), landing);
        }
        if (command.Name is "i" or "a")
        {
            return VimTextObjects.Of(text, caret, command.Argument!, command.Name == "i", n) is { } found ? (found, found.Start) : null;
        }
        if (op == "c" && command.Name is "w" or "W" && caret < text.Length && !char.IsWhiteSpace(text[caret]))
        {
            // cw on a word changes to its end, not the white after it.
            bool big = command.Name == "W";
            int end = caret;
            for (int i = 0; i < n; i++)
            {
                bool atEnd = end + 1 >= text.Length || VimMotions.Kind(text[end + 1], big) != VimMotions.Kind(text[end], big);
                if (i > 0 || !atEnd)
                {
                    end = VimMotions.WordEnd(text, end, 1, big)?.Offset ?? end;
                }
            }
            return (new VimSpan(caret, TextNavigation.NextGrapheme(text, end), false), caret);
        }
        if (Target(command, text, caret, operating: true, view) is not { } target)
        {
            return null;
        }
        if (target.Kind == VimMotionKind.Linewise)
        {
            int from = Math.Min(text.GetLineFromOffset(caret), text.GetLineFromOffset(target.Offset));
            int to = Math.Max(text.GetLineFromOffset(caret), text.GetLineFromOffset(target.Offset));
            return (new VimSpan(text.GetLineStart(from), text.GetLineStart(to), true), Math.Min(caret, target.Offset));
        }
        int start = Math.Min(caret, target.Offset);
        int stop = Math.Max(caret, target.Offset);
        if (target.Kind == VimMotionKind.Inclusive && stop < VimMotions.LineOf(text, stop).End)
        {
            stop = TextNavigation.NextGrapheme(text, stop);
        }
        int startLine = text.GetLineFromOffset(start);
        int stopLine = text.GetLineFromOffset(stop);
        if (target.Kind == VimMotionKind.Exclusive && stopLine > startLine && stop == text.GetLineStart(stopLine))
        {
            // An exclusive motion ending at a line's start ends at the line before; from the indent, it takes whole lines.
            if (VimMotions.InIndent(text, start))
            {
                return (new VimSpan(text.GetLineStart(startLine), text.GetLineStart(stopLine - 1), true), start);
            }
            stop = text.GetLine(stopLine - 1).End;
        }
        return (new VimSpan(start, stop, false), start);
    }
}
