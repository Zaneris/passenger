using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Ex commands, typed on the command line: a range of lines (%, ., $, a number, a mark, /pattern/, each with + and
/// - after it, two joined by , or ;), then :{n}, :s, :g and :v, :d, :y, :j, :&gt; and :&lt;, :normal, :noh, :set, and
/// :w, :q, :wq, :x, :e and :action, which the host does as its Save, Close Tab, open and the action named; a command Vim
/// mode doesn't do is said.</summary>
public sealed partial class Vim
{
    private VimResult Ex(string line, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        if (line.Trim().Length > 0)
        {
            Registers.LastCommand = line;
        }
        int i = 0;
        while (i < line.Length && line[i] is ' ' or ':')
        {
            i++;
        }
        int caretLine = text.GetLineFromOffset(selections.Primary.Caret);
        if (!ReadRange(line, ref i, text, caretLine, view, out (int First, int Last)? range, out string? problem))
        {
            return Said(problem!);
        }
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }
        int nameStart = i;
        if (i < line.Length && line[i] is '&' or '~' or '<' or '>' or '!' or '=')
        {
            i++;
            while (i < line.Length && line[i] == line[nameStart] && line[i] is '&' or '<' or '>')
            {
                i++;
            }
        }
        else
        {
            while (i < line.Length && char.IsAsciiLetter(line[i]))
            {
                i++;
            }
        }
        string name = line[nameStart..i];
        // A name ending in s, g or v takes its pattern straight after it (:s/a/b, :g/x/d): one letter, its own command.
        if (name.Length > 1 && name[0] is 's' or 'g' or 'v' && name is not ("se" or "set" or "setl" or "setlocal" or "setg" or "setglobal" or "substitute" or "global" or "vglobal" or "sil" or "silent"))
        {
            i = nameStart + 1;
            name = name[..1];
        }
        bool bang = i < line.Length && line[i] == '!' && name is not ("" or "!");
        if (bang)
        {
            i++;
        }
        string args = line[i..];
        int last = VimMotions.LastLine(text);
        (int first, int end) = range ?? (caretLine, caretLine);
        (first, end) = (Math.Clamp(Math.Min(first, end), 0, last), Math.Clamp(Math.Max(first, end), 0, last));
        string trimmed = args.Trim();
        switch (name)
        {
            case "":
                if (range is null)
                {
                    return VimResult.Held;
                }
                return Jumped(text, selections, view, VimMotions.FirstNonBlank(text, range.Value.Last < 0 ? 0 : Math.Min(range.Value.Last, last)));
            case "w" or "write":
                return trimmed.Length > 0 ? Said($"Vim mode saves the file shown: :w {trimmed} is not done") : Asked(VimRequestKind.Save);
            case "wa" or "wall":
                return Asked(VimRequestKind.SaveAll);
            case "q" or "quit" or "clo" or "close":
                return Asked(VimRequestKind.Close);
            case "wq" or "x" or "xi" or "xit" or "exi" or "exit":
                return Asked(VimRequestKind.SaveAndClose);
            case "e" or "edit":
                return trimmed.Length > 0 ? Asked(VimRequestKind.Open, trimmed) : Said(":e opens a file named after it; :e alone is not done");
            case "s" or "substitute" or "&" or "&&" or "~":
                return Substitute(name, args, first, end, range is null, text, selections, view, null);
            case "g" or "global" or "v" or "vglobal":
                return Global(name is "v" or "vglobal" || bang, args, range is null ? (0, last) : (first, end), text, selections, view);
            case "noh" or "nohlsearch":
                return new VimResult(true) { Highlight = new SearchQuery(string.Empty) };
            case "se" or "set" or "setl" or "setlocal" or "setg" or "setglobal":
                return Set(trimmed);
            case "d" or "de" or "del" or "delete" or "y" or "ya" or "yank":
                return LinesCommand(name[0] == 'd' ? "d" : "y", trimmed, first, end, selections.Primary.Caret, text, view);
            case "j" or "join":
                return Join(text, SelectionSet.Single(text.GetLineStart(first)), Math.Max(2, end - first + 1), spaced: !bang, Options.JoinSpaces) is { } joined
                    ? new VimResult(true, Edits: [OnFirstNonBlank(text, joined, first)])
                    : VimResult.Held;
            case ">" or ">>" or ">>>" or "<" or "<<" or "<<<":
                Stretch shifted = Whole(text, first, end, text.GetLineStart(first));
                VimResult moved = Shift(text, SelectionSet.Single(text.GetLineStart(first)), [shifted], name[0] == '>', view, name.Length);
                return moved.Edit is { } shift ? new VimResult(true, Edits: [OnFirstNonBlank(text, shift, end)]) : moved;
            case "norm" or "norma" or "normal":
                return Normally(range is null ? null : (first, end), args.TrimStart(), text, selections);
            case "action":
                return trimmed.Length > 0 ? Asked(VimRequestKind.Action, trimmed) : Said(":action runs one of the IDE's actions, named after it by its id or title");
        }
        return Said($"Vim mode does not do :{line[nameStart..].Trim()}");
    }

    private static VimResult Asked(VimRequestKind kind, string? path = null) => new(true) { Request = new VimRequest(kind, path) };

    /// <summary>A plan whose caret lands on the first non-blank of a line it leaves where it was, as a line command's does.</summary>
    private static EditPlan OnFirstNonBlank(TextSnapshot text, EditPlan plan, int line)
    {
        TextSnapshot after = text.Apply(plan.Edits);
        return plan with { Selections = SelectionSet.Single(VimMotions.FirstNonBlank(after, Math.Min(line, VimMotions.LastLine(after)))) };
    }

    /// <summary>A jump to an offset: the place left kept as the mark ' and told to the host for its places.</summary>
    private static VimResult Jumped(TextSnapshot text, SelectionSet selections, IVimView view, int to)
    {
        int from = selections.Primary.Caret;
        view.SetMark('\'', from);
        return new VimResult(true, SelectionSet.Single(Settled(text, to))) { JumpedFrom = from };
    }

    /// <summary>The lines a command names, from zero; none where it names none; false with why where an address fails.</summary>
    private bool ReadRange(string line, ref int i, TextSnapshot text, int caretLine, IVimView view, out (int First, int Last)? range, out string? problem)
    {
        range = null;
        problem = null;
        if (i < line.Length && line[i] == '%')
        {
            i++;
            range = (0, VimMotions.LastLine(text));
            return true;
        }
        int current = caretLine;
        if (!Address(line, ref i, text, current, view, out int? first, out problem))
        {
            return false;
        }
        if (i < line.Length && line[i] is ',' or ';')
        {
            if (line[i] == ';' && first is { } here)
            {
                current = here;
            }
            i++;
            if (!Address(line, ref i, text, current, view, out int? second, out problem))
            {
                return false;
            }
            range = (first ?? caretLine, second ?? current);
            return true;
        }
        range = first is { } only ? (only, only) : null;
        return true;
    }

    /// <summary>One address, with its + and - after it: . $ a number, a mark ('x) or a pattern's next (/x/) or last
    /// (?x?) line; null where none stands.</summary>
    private bool Address(string line, ref int i, TextSnapshot text, int current, IVimView view, out int? address, out string? problem)
    {
        address = null;
        problem = null;
        if (i >= line.Length)
        {
            return true;
        }
        char c = line[i];
        if (c == '.')
        {
            address = current;
            i++;
        }
        else if (c == '$')
        {
            address = VimMotions.LastLine(text);
            i++;
        }
        else if (char.IsAsciiDigit(c))
        {
            int start = i;
            while (i < line.Length && char.IsAsciiDigit(line[i]))
            {
                i++;
            }
            address = (int)Math.Min(long.Parse(line[start..i], CultureInfo.InvariantCulture), int.MaxValue) - 1;
        }
        else if (c == '\'' && i + 1 < line.Length)
        {
            char mark = line[i + 1];
            i += 2;
            if (view.Mark(mark == '`' ? '\'' : mark, text) is not { } at)
            {
                problem = "E20: Mark not set";
                return false;
            }
            address = text.GetLineFromOffset(at);
        }
        else if (c is '/' or '?')
        {
            int end = Delimiter(line, i + 1, c);
            string pattern = end < 0 ? line[(i + 1)..] : line[(i + 1)..end];
            i = end < 0 ? line.Length : end + 1;
            if (pattern.Length == 0 && Registers.LastSearch is { } last)
            {
                pattern = last.Pattern;
            }
            if (QueryOf(pattern, out problem) is not { } query || Matches(text, query, out problem) is not { } matches)
            {
                return false;
            }
            int from = c == '/' ? text.GetLine(current).EndIncludingBreak : text.GetLineStart(current);
            int index = c == '/' ? FirstAfter(matches, from - 1, false) : LastBefore(matches, from, false);
            if (index < 0 && matches.Count > 0 && Options.WrapScan)
            {
                index = c == '/' ? 0 : matches.Count - 1;
            }
            if (index < 0)
            {
                problem = $"E486: Pattern not found: {pattern}";
                return false;
            }
            address = text.GetLineFromOffset(matches[index].Start);
        }
        while (i < line.Length && line[i] is '+' or '-')
        {
            int sign = line[i] == '+' ? 1 : -1;
            i++;
            int start = i;
            while (i < line.Length && char.IsAsciiDigit(line[i]))
            {
                i++;
            }
            int by = i > start ? int.Parse(line[start..i], CultureInfo.InvariantCulture) : 1;
            address = (address ?? current) + (sign * by);
        }
        if (address is < 0 && c != '0')
        {
            problem = "E16: Invalid range";
            return false;
        }
        return true;
    }

    /// <summary>:s: each match of the pattern (the last search's where none is given) on the lines named replaced, one
    /// step of undo, flags g (every match; gdefault turns it round), i and I (case), n (count only), e (no error) and &amp;
    /// (the last flags); :&amp;, :&amp;&amp; and :~ do the last again. Only <paramref name="only"/>'s lines where given (:g).</summary>
    private VimResult Substitute(string name, string args, int first, int end, bool noRange, TextSnapshot text, SelectionSet selections, IVimView view, HashSet<int>? only)
    {
        string pattern;
        string replacement;
        string flags;
        string rest;
        bool again = name is "&" or "&&" or "~" || args.Length == 0 || char.IsAsciiLetterOrDigit(args[0]) || args[0] is ' ' or '&' or '"' or '|';
        if (again)
        {
            if (Registers.LastSubstitute is not { } last)
            {
                return Said("E35: No previous regular expression");
            }
            pattern = name == "~" && Registers.LastSearch is { } search ? search.Pattern : last.Pattern;
            replacement = last.Replacement;
            flags = name == "&&" ? last.Flags : string.Empty;
            rest = args.TrimStart();
            if (rest.StartsWith('&'))
            {
                flags = last.Flags;
                rest = rest[1..];
            }
        }
        else
        {
            char delimiter = args[0];
            int split = Delimiter(args, 1, delimiter);
            pattern = split < 0 ? args[1..] : args[1..split];
            int closing = split < 0 ? -1 : Delimiter(args, split + 1, delimiter);
            replacement = split < 0 ? string.Empty : closing < 0 ? args[(split + 1)..] : args[(split + 1)..closing];
            rest = closing < 0 ? string.Empty : args[(closing + 1)..];
            flags = string.Empty;
            if (rest.StartsWith('&'))
            {
                flags = Registers.LastSubstitute?.Flags ?? string.Empty;
                rest = rest[1..];
            }
            if (pattern.Length == 0)
            {
                if (Registers.LastSearch is not { } search)
                {
                    return Said("E35: No previous regular expression");
                }
                pattern = search.Pattern;
            }
            if (replacement == "~")
            {
                replacement = Registers.LastSubstitute?.Replacement ?? string.Empty;
            }
        }
        int read = 0;
        while (read < rest.Length && rest[read] is 'g' or 'c' or 'e' or 'i' or 'I' or 'n' or 'p' or '#' or 'l' or 'r')
        {
            read++;
        }
        flags += rest[..read];
        string countText = rest[read..].Trim();
        if (countText.Length > 0)
        {
            if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 1)
            {
                return Said($"E488: Trailing characters: {countText}");
            }
            first = noRange ? first : end;
            end = Math.Min(first + count - 1, VimMotions.LastLine(text));
        }
        if (flags.Contains('c', StringComparison.Ordinal))
        {
            return Said(":s's c flag (asking at each match) is not done; Find and Replace asks match by match");
        }
        string? previous = Registers.LastSubstitute?.Replacement;
        Registers.LastSubstitute = (pattern, replacement, flags.Replace("n", string.Empty, StringComparison.Ordinal));
        Registers.LastSearch = new VimSearch(pattern, false);
        bool every = flags.Count(f => f == 'g') % 2 == 1 != Options.GlobalDefault;
        bool ignore = flags.LastIndexOfAny(['i', 'I']) is var at and >= 0 ? flags[at] == 'i' : Options.IgnoreCase;
        VimPattern translated = VimPatterns.Translate(pattern, ignore, flags.Contains('I', StringComparison.Ordinal) ? false : Options.SmartCase && !flags.Contains('i', StringComparison.Ordinal));
        if (translated.Problem is { } problem)
        {
            return Said(problem);
        }
        Regex regex = new(translated.Regex, RegexOptions.CultureInvariant | RegexOptions.Multiline | (translated.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TextSearch.AtOnce);
        (string lines, int[] starts) = LinesFrom(text, first);
        List<TextEdit> edits = [];
        int matched = 0;
        HashSet<int> changedLines = [];
        int lastLine = -1;
        int reached = 0;
        try
        {
            for (int line = first; line <= end; line++)
            {
                if (only is not null && !only.Contains(line))
                {
                    continue;
                }
                int lineStart = starts[line - first];
                int lineEnd = line - first + 1 < starts.Length ? starts[line - first + 1] - 1 : lines.Length;
                int from = Math.Max(lineStart, reached);
                while (from <= lineEnd)
                {
                    Match match = regex.Match(lines, from);
                    if (!match.Success || match.Index > lineEnd)
                    {
                        break;
                    }
                    matched++;
                    changedLines.Add(line);
                    lastLine = line;
                    int start = OffsetIn(text, first, starts, match.Index);
                    int stop = OffsetIn(text, first, starts, match.Index + match.Length);
                    edits.Add(new TextEdit(start, stop - start, VimPatterns.Replace(replacement, match, previous, view.LineEnding)));
                    reached = match.Index + match.Length;
                    from = match.Length == 0 ? reached + 1 : reached;
                    if (!every)
                    {
                        break;
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return Said(SearchResult.TookTooLong);
        }
        int caretLine = text.GetLineFromOffset(selections.Primary.Caret);
        if (matched == 0)
        {
            return flags.Contains('e', StringComparison.Ordinal) || only is not null ? new VimResult(true) : Said($"E486: Pattern not found: {pattern}");
        }
        if (flags.Contains('n', StringComparison.Ordinal))
        {
            return new VimResult(true, SelectionSet.Single(VimMotions.FirstNonBlank(text, caretLine))) { Message = $"{Plural(matched, "match", "matches")} on {Plural(changedLines.Count, "line", "lines")}", Highlight = SearchShown() };
        }
        TextSnapshot after = text.Apply(edits);
        int landing = TextEditing.MapOffset(text.GetLineStart(lastLine), edits, false);
        int landedLine = after.GetLineFromOffset(Math.Min(landing, after.Length));
        // Lines a replacement split put the caret on the last of them, as Vim's does.
        landedLine = Math.Min(landedLine + edits.Where(e => text.GetLineFromOffset(e.Start) == lastLine).Sum(e => e.Text.Count(c => c == '\n')), VimMotions.LastLine(after));
        EditPlan plan = new(edits, SelectionSet.Single(VimMotions.FirstNonBlank(after, landedLine)), EditKind.Other);
        string? said = changedLines.Count > 2 ? $"{Plural(matched, "substitution", "substitutions")} on {Plural(changedLines.Count, "line", "lines")}" : null;
        return new VimResult(true, Edits: [plan]) { Message = said, Highlight = SearchShown() };
    }

    private static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    /// <summary>The text from a line to the end with every break "\n", and where each line starts in it.</summary>
    private static (string Text, int[] Starts) LinesFrom(TextSnapshot text, int first)
    {
        int last = text.LineCount - 1;
        StringBuilder made = new();
        int[] starts = new int[last - first + 1];
        for (int line = first; line <= last; line++)
        {
            starts[line - first] = made.Length;
            LineSpan span = text.GetLine(line);
            made.Append(text.GetText(span.Start, span.Length));
            if (span.BreakLength > 0)
            {
                made.Append('\n');
            }
        }
        return (made.ToString(), starts);
    }

    /// <summary>An offset of <see cref="LinesFrom"/>'s text as one of the text's own.</summary>
    private static int OffsetIn(TextSnapshot text, int first, int[] starts, int offset)
    {
        int index = Math.Max(0, Passenger.Core.Sorted.FirstAfter(starts.AsSpan(), offset) - 1);
        LineSpan line = text.GetLine(first + index);
        int column = offset - starts[index];
        return column <= line.Length ? line.Start + column : line.EndIncludingBreak;
    }

    /// <summary>:g and :v: the command after the pattern (d, s, normal, or p by default) on each line that matches (or
    /// doesn't), as one step of undo.</summary>
    private VimResult Global(bool not, string args, (int First, int Last) range, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        if (args.Length == 0)
        {
            return Said("E35: No previous regular expression");
        }
        char delimiter = args[0];
        int split = Delimiter(args, 1, delimiter);
        string pattern = split < 0 ? args[1..] : args[1..split];
        string command = split < 0 ? string.Empty : args[(split + 1)..].Trim();
        if (pattern.Length == 0 && Registers.LastSearch is { } search)
        {
            pattern = search.Pattern;
        }
        Registers.LastSearch = new VimSearch(pattern, false);
        if (QueryOf(pattern, out string? problem) is not { } query || Matches(text, query, out problem) is not { } matches)
        {
            return Said(problem ?? "The pattern can't be read");
        }
        HashSet<int> hit = [.. matches.Select(m => text.GetLineFromOffset(m.Start))];
        List<int> lines = [.. Enumerable.Range(range.First, range.Last - range.First + 1).Where(l => hit.Contains(l) != not)];
        if (lines.Count == 0)
        {
            return Said(not ? "E538: Pattern found in every line" : $"E486: Pattern not found: {pattern}");
        }
        int nameEnd = 0;
        while (nameEnd < command.Length && char.IsAsciiLetter(command[nameEnd]))
        {
            nameEnd++;
        }
        string name = command[..nameEnd];
        switch (name)
        {
            case "" or "p" or "print":
                return new VimResult(true) { Highlight = SearchShown() };
            case "d" or "de" or "del" or "delete":
                // Lines next to each other go as one, so the last line's going takes the break before it.
                List<Stretch> taken = [];
                for (int at = 0; at < lines.Count;)
                {
                    int run = at;
                    while (run + 1 < lines.Count && lines[run + 1] == lines[run] + 1)
                    {
                        run++;
                    }
                    taken.Add(Whole(text, lines[at], lines[run], text.GetLineStart(lines[at])));
                    at = run + 1;
                }
                ChangeMade? keep = lastChange;
                VimResult deleted = Act(new Command([], 0, null, "d", "line", null), "d", taken, text, SelectionSet.Single(text.GetLineStart(lines[^1])), view);
                lastChange = keep;
                // The caret goes to the line after the last one deleted, as Vim's does.
                return deleted.Edit is { } plan
                    ? new VimResult(true, Edits: [OnFirstNonBlank(text, plan, lines[^1] - (lines.Count - 1))]) { Highlight = SearchShown() }
                    : deleted;
            case "s" or "substitute":
                return Substitute("s", command[nameEnd..], range.First, range.Last, false, text, selections, view, [.. lines]);
            case "norm" or "norma" or "normal":
                return Normally(null, command[nameEnd..].TrimStart(), text, selections, lines);
        }
        return Said($"Vim mode's :g does d, s and normal; :g/.../{command} is not done");
    }

    /// <summary>:d and :y over lines, into the register named after them; :y leaves the caret where it stands.</summary>
    private VimResult LinesCommand(string op, string args, int first, int end, int caret, TextSnapshot text, IVimView view)
    {
        char? register = args.Length > 0 && VimRegisters.Names(args[0]) && !char.IsAsciiDigit(args[0]) ? args[0] : null;
        ChangeMade? keep = lastChange;
        int at = op == "y" ? caret : text.GetLineStart(first);
        VimResult result = Act(new Command([], 0, register, op, "line", null), op, [Whole(text, first, end, at) with { Landing = at }], text, SelectionSet.Single(at), view);
        lastChange = keep;
        return result;
    }

    /// <summary>:normal: its keys pressed on the caret's line, or at the start of each line named, each line found by
    /// its count from the text's end (which the runs above it leave as it was), each run ended as Escape ends it.</summary>
    private VimResult Normally((int First, int Last)? range, string keys, TextSnapshot text, SelectionSet selections, IReadOnlyList<int>? lines = null)
    {
        if (keys.Length == 0)
        {
            return VimResult.Held;
        }
        List<string> pressed = KeysOf(keys);
        IReadOnlyList<int> each = lines ?? (range is { } r ? [.. Enumerable.Range(r.First, r.Last - r.First + 1)] : []);
        if (each.Count == 0)
        {
            return new VimResult(true) { Replay = [.. pressed, Escape] };
        }
        List<string> replay = [];
        int last = VimMotions.LastLine(text);
        foreach (int line in each)
        {
            replay.Add(GoKey + (last - line).ToString(CultureInfo.InvariantCulture));
            replay.AddRange(pressed);
            replay.Add(Escape);
        }
        return new VimResult(true, selections) { Replay = replay };
    }

    /// <summary>A key only a replay presses: the caret to a line's start, the number after it counting lines up from the
    /// text's last, no jump.</summary>
    private const string GoKey = "<Go>";

    /// <summary>:set: each option named turned on (name), off (noname), round (invname, name!) or said (name?); with none,
    /// those that differ from their defaults said.</summary>
    private VimResult Set(string args)
    {
        if (args.Length == 0 || args == "all")
        {
            List<string> differ = [];
            VimOptions defaults = new();
            foreach ((string name, Func<VimOptions, bool> get, Action<VimOptions, bool> _) in OptionTable)
            {
                if (get(Options) != get(defaults))
                {
                    differ.Add(get(Options) ? name : "no" + name);
                }
            }
            if (Options.TextWidth != defaults.TextWidth)
            {
                differ.Add($"textwidth={Options.TextWidth}");
            }
            return Said(differ.Count == 0 ? "Vim mode's options are as they begin" : string.Join("  ", differ), error: false);
        }
        List<string> answers = [];
        SearchQuery? highlight = null;
        foreach (string item in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string word = item.TrimEnd('?', '!');
            bool ask = item.EndsWith('?');
            bool flip = item.EndsWith('!') || word.StartsWith("inv", StringComparison.Ordinal);
            bool off = !flip && word.StartsWith("no", StringComparison.Ordinal) && Option(word[2..]) is not null;
            string name = off ? word[2..] : word.StartsWith("inv", StringComparison.Ordinal) ? word[3..] : word;
            if (name.Contains('=', StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal))
            {
                name = name[..name.IndexOfAny(['=', ':'])];
            }
            if (name is "tw" or "textwidth")
            {
                int equals = item.IndexOfAny(['=', ':']);
                if (equals < 0 || ask)
                {
                    answers.Add($"textwidth={Options.TextWidth}");
                    continue;
                }
                if (!int.TryParse(item.AsSpan(equals + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int width))
                {
                    return Said($"E521: Number required after =: {item}");
                }
                Options.TextWidth = width;
                continue;
            }
            if (Option(name) is not { } option)
            {
                return Said(name is "sw" or "shiftwidth" or "ts" or "tabstop" or "et" or "expandtab" or "sts" or "softtabstop" or "ai" or "autoindent"
                    ? $"{name} is the file's indent, which Settings › Editor and an .editorconfig set"
                    : name is "nu" or "number" or "rnu" or "relativenumber" or "wrap" or "list" or "cursorline"
                        ? $"{name} is one of Passenger's settings (Settings › Editor)"
                        : $"E518: Unknown option: {name}");
            }
            bool now = option.Get(Options);
            if (ask)
            {
                answers.Add(now ? option.Name : "no" + option.Name);
                continue;
            }
            bool value = flip ? !now : !off;
            option.Set(Options, value);
            if (option.Name == "hlsearch")
            {
                highlight = value ? SearchShown() ?? new SearchQuery(string.Empty) : new SearchQuery(string.Empty);
            }
        }
        return new VimResult(true) { Message = answers.Count > 0 ? string.Join("  ", answers) : null, Highlight = highlight };
    }

    private static readonly (string Name, Func<VimOptions, bool> Get, Action<VimOptions, bool> Set)[] OptionTable =
    [
        ("ignorecase", o => o.IgnoreCase, (o, v) => o.IgnoreCase = v),
        ("smartcase", o => o.SmartCase, (o, v) => o.SmartCase = v),
        ("hlsearch", o => o.HighlightSearch, (o, v) => o.HighlightSearch = v),
        ("wrapscan", o => o.WrapScan, (o, v) => o.WrapScan = v),
        ("joinspaces", o => o.JoinSpaces, (o, v) => o.JoinSpaces = v),
        ("gdefault", o => o.GlobalDefault, (o, v) => o.GlobalDefault = v),
    ];

    private static (string Name, Func<VimOptions, bool> Get, Action<VimOptions, bool> Set)? Option(string name)
    {
        string full = name switch { "ic" => "ignorecase", "scs" => "smartcase", "hls" => "hlsearch", "ws" => "wrapscan", "js" => "joinspaces", "gd" => "gdefault", _ => name };
        foreach ((string Name, Func<VimOptions, bool> Get, Action<VimOptions, bool> Set) option in OptionTable)
        {
            if (option.Name == full)
            {
                return option;
            }
        }
        return null;
    }
}
