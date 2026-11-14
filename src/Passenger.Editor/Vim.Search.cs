using System.Text;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>What only the editor's host can do for Vim mode: save, close or open a file, or step through the IDE's places.</summary>
public enum VimRequestKind
{
    Save,
    SaveAll,
    Close,
    SaveAndClose,
    Open,
    Older,
    Newer,

    /// <summary>= : the lines named laid out as the language's Reformat Code lays them out.</summary>
    Reformat,

    /// <summary>:action: one of the IDE's actions run, by its id or title (<see cref="VimRequest.Path"/>).</summary>
    Action,

    /// <summary>:bufdo: the command line (<see cref="VimRequest.Path"/>) run in each open file in turn.</summary>
    EachFile,

    /// <summary>:windo: the command line run in each editor shown, split by split.</summary>
    EachEditor,

    /// <summary>:argdo: the command line run in each file open, in the order their tabs stand.</summary>
    EachTab,

    /// <summary>:cdo: the command line run at each place the IDE's list of places holds (Vim's quickfix list).</summary>
    EachPlace,

    /// <summary>:cfdo: the command line run in each file of the IDE's list of places, at its first place there.</summary>
    EachPlaceFile,

    /// <summary>A command line of several commands joined by '|', run in turn in the file shown as one change.</summary>
    Commands,
}

/// <summary>A request of the host: what to do, the file to open (or the action to run), and how many steps.</summary>
public sealed record VimRequest(VimRequestKind Kind, string? Path = null, int Count = 1)
{
    /// <summary>The lines = names, first and last (from zero), each stretch apart, in order.</summary>
    public IReadOnlyList<(int First, int Last)> Lines { get; init; } = [];
}

/// <summary>The command line (: / ?) typed in the status bar's place, searching with / ? n N * #, marks set by m and gone
/// to by ' and `, and the jumps that leave a place for '' and the IDE's Back.</summary>
public sealed partial class Vim
{
    /// <summary>The motions that jump: the place they leave is the mark ' and goes behind in the host's places.</summary>
    private static readonly HashSet<string> Jumping = ["G", "gg", "%", "(", ")", "{", "}", "n", "N", "*", "#", "g*", "g#", "H", "M", "L", "'", "`"];

    /// <summary>The command line under way: its prompt (: / ?), what is typed, the keys of the command it ends (a count,
    /// an operator) and the history entry Up and Down show.</summary>
    private (char Prompt, StringBuilder Text, List<string> Keys, int Back)? commandLine;

    private readonly List<string> commandHistory = [];

    private readonly List<string> searchHistory = [];

    /// <summary>A search's matches kept for its text, so n after n searches it once.</summary>
    private (TextSnapshot Text, string Regex, bool MatchCase, IReadOnlyList<TextRange> Matches)? found;

    /// <summary>The command line as the status bar shows it (":s/a/b", "/foo"); null while none is open.</summary>
    public string? CommandLine => commandLine is { } line ? line.Prompt + line.Text.ToString() : null;

    private VimOptions Options => Registers.Options;

    /// <summary>Whether keys typed so far may take a search as their motion (nothing, a count, a register, an operator),
    /// or for : a count alone.</summary>
    private static bool TakesCommandLine(List<string> keys, bool search)
    {
        int i = 0;
        while (i < keys.Count && keys[i].Length == 1 && char.IsAsciiDigit(keys[i][0]))
        {
            i++;
        }
        if (!search)
        {
            return i == keys.Count;
        }
        if (i + 1 < keys.Count && keys[i] == "\"")
        {
            i += 2;
        }
        while (i < keys.Count && keys[i].Length == 1 && char.IsAsciiDigit(keys[i][0]))
        {
            i++;
        }
        if (i < keys.Count && keys[i] is "d" or "c" or "y" or "<" or ">" or "=")
        {
            i++;
        }
        else if (i + 1 < keys.Count && keys[i] == "g" && keys[i + 1] is "~" or "u" or "U" or "q" or "w")
        {
            i += 2;
        }
        while (i < keys.Count && keys[i].Length == 1 && char.IsAsciiDigit(keys[i][0]))
        {
            i++;
        }
        return i == keys.Count;
    }

    /// <summary>Opens the command line, the keys typed before it kept for the command it ends; : after a count names the
    /// lines from the caret's (.,.+2), in visual mode the stretch's ('&lt;,'&gt;).</summary>
    private VimResult OpenCommandLine(char prompt, string start)
    {
        List<string> keys = [.. pending];
        pending.Clear();
        int count = prompt == ':' && keys.Count > 0 ? int.Parse(string.Concat(keys), System.Globalization.CultureInfo.InvariantCulture) : 0;
        string text = count > 1 ? $".,.+{count - 1}" : count == 1 ? "." : start;
        commandLine = (prompt, new StringBuilder(text), prompt == ':' ? [] : keys, 0);
        return VimResult.Held;
    }

    private VimResult PressCommandLine(string key, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        (char prompt, StringBuilder typed, List<string> keys, int back) = commandLine!.Value;
        List<string> history = prompt == ':' ? commandHistory : searchHistory;
        switch (key)
        {
            case Escape:
                commandLine = null;
                return VimResult.Held;
            case "<BS>" when typed.Length == 0:
                commandLine = null;
                return VimResult.Held;
            case "<BS>":
                typed.Length -= typed.Length > 1 && char.IsLowSurrogate(typed[^1]) ? 2 : 1;
                return VimResult.Held;
            case "<C-u>":
                typed.Clear();
                return VimResult.Held;
            case "<C-w>":
                int end = typed.Length;
                while (end > 0 && typed[end - 1] == ' ')
                {
                    end--;
                }
                while (end > 0 && typed[end - 1] != ' ')
                {
                    end--;
                }
                typed.Length = end;
                return VimResult.Held;
            case "<Up>" or "<Down>":
                int at = Math.Clamp(back + (key == "<Up>" ? 1 : -1), 0, history.Count);
                typed.Clear().Append(at == 0 ? string.Empty : history[^at]);
                commandLine = (prompt, typed, keys, at);
                return VimResult.Held;
            case "<CR>":
                commandLine = null;
                string line = typed.ToString();
                if (line.Length > 0 && (history.Count == 0 || history[^1] != line))
                {
                    history.Add(line);
                }
                return prompt == ':' ? Ex(line, text, selections, view) : SearchTyped(prompt, line, keys, text, selections, view);
        }
        if (key.Length == 1 || (key.Length == 2 && char.IsSurrogatePair(key[0], key[1])) || key == "<Tab>")
        {
            typed.Append(key == "<Tab>" ? "\t" : key);
        }
        return VimResult.Held;
    }

    /// <summary>/ or ? ended with Enter: the pattern (the last one where none is typed) and its offset kept as the last
    /// search, then n made the motion of the keys typed before it.</summary>
    private VimResult SearchTyped(char prompt, string typed, List<string> keys, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        int split = Delimiter(typed, 0, prompt);
        string pattern = split < 0 ? typed : typed[..split];
        string offset = split < 0 ? string.Empty : typed[(split + 1)..];
        if (pattern.Length == 0)
        {
            if (Registers.LastSearch is not { } last)
            {
                return Said("E35: No previous regular expression");
            }
            pattern = last.Pattern;
        }
        Registers.LastSearch = new VimSearch(pattern, prompt == '?', offset);
        pending.Clear();
        pending.AddRange(keys);
        return IsVisual(Mode) ? PressVisual("n", text, shownVisual ?? selections, view) : Take("n", text, selections, view);
    }

    /// <summary>Where an unescaped <paramref name="delimiter"/> stands from <paramref name="from"/>, -1 where none does.</summary>
    private static int Delimiter(string text, int from, char delimiter)
    {
        bool inBrackets = false;
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (c == '[')
            {
                inBrackets = text.IndexOf(']', i + 1) > i;
            }
            else if (c == ']')
            {
                inBrackets = false;
            }
            else if (c == delimiter && !inBrackets)
            {
                return i;
            }
        }
        return -1;
    }

    private static VimResult Said(string message, bool error = true) => new(true) { Message = message, MessageIsError = error };

    /// <summary>The query the editor's own find takes for a Vim pattern, case as the options and the pattern say; null
    /// with why where it can't be read.</summary>
    private SearchQuery? QueryOf(string pattern, out string? problem, bool smartCase = true)
    {
        VimPattern read = VimPatterns.Translate(pattern, Options.IgnoreCase, smartCase && Options.SmartCase);
        problem = read.Problem;
        return problem is null ? new SearchQuery(read.Regex, read.MatchCase, WholeWords: false, Regex: true) : null;
    }

    /// <summary>Every match of a query in the text, by the editor's own find, kept while the text and query stand.</summary>
    private IReadOnlyList<TextRange>? Matches(TextSnapshot text, SearchQuery query, out string? problem)
    {
        problem = null;
        if (found is { } kept && ReferenceEquals(kept.Text, text) && kept.Regex == query.Text && kept.MatchCase == query.MatchCase)
        {
            return kept.Matches;
        }
        SearchResult result = TextSearch.FindAll(text, query, TextSearch.AtOnce, int.MaxValue);
        if (result.Problem is { } why)
        {
            problem = why;
            return null;
        }
        found = (text, query.Text, query.MatchCase, result.Matches);
        return result.Matches;
    }

    /// <summary>n and N (and / and ? through them): the <paramref name="n"/>th match from the caret in the last search's
    /// direction (the other for N), wrapping past the text's end where wrapscan allows, its offset applied.</summary>
    private VimTarget? SearchTarget(TextSnapshot text, int caret, int n, bool reverse)
    {
        searchSaid = null;
        if (Registers.LastSearch is not { } last)
        {
            searchSaid = ("E35: No previous regular expression", true);
            return null;
        }
        if (QueryOf(last.Pattern, out string? problem) is not { } query || Matches(text, query, out problem) is not { } matches)
        {
            searchSaid = (problem ?? "The pattern can't be read", true);
            return null;
        }
        if (matches.Count == 0)
        {
            searchSaid = ($"E486: Pattern not found: {last.Pattern}", true);
            return null;
        }
        bool back = last.Back != reverse;
        (char kind, int by) = OffsetOf(last.Offset);
        int at = kind == 'e' ? caret - by : caret;
        int index = -1;
        for (int i = 0; i < Math.Min(n, matches.Count + 1); i++)
        {
            int from = index < 0 ? at : (kind == 'e' ? Math.Max(matches[index].Start, matches[index].End - 1) : matches[index].Start);
            int next = back ? LastBefore(matches, from, kind == 'e') : FirstAfter(matches, from, kind == 'e');
            if (next < 0)
            {
                if (!Options.WrapScan)
                {
                    searchSaid = ($"E{(back ? "384" : "385")}: Search hit {(back ? "TOP" : "BOTTOM")} without match for: {last.Pattern}", true);
                    return null;
                }
                next = back ? matches.Count - 1 : 0;
                searchSaid = (back ? "search hit TOP, continuing at BOTTOM" : "search hit BOTTOM, continuing at TOP", false);
            }
            index = next;
        }
        TextRange match = matches[index];
        return kind switch
        {
            'e' => new VimTarget(Math.Clamp(Math.Max(match.Start, match.End - 1) + by, 0, text.Length), VimMotionKind.Inclusive),
            's' => new VimTarget(Math.Clamp(match.Start + by, 0, text.Length), VimMotionKind.Exclusive),
            'l' => VimMotions.LinesAway(text, match.Start, by, breakEnds) is { } line ? new VimTarget(text.GetLineStart(line), VimMotionKind.Linewise) : new VimTarget(match.Start, VimMotionKind.Linewise),
            _ => new VimTarget(match.Start, VimMotionKind.Exclusive),
        };
    }

    /// <summary>A search's offset: e and s (or b) with what they add, a number of lines (l), or none.</summary>
    private static (char Kind, int By) OffsetOf(string offset)
    {
        if (offset.Length == 0)
        {
            return ('\0', 0);
        }
        char kind = offset[0] is 'e' or 's' or 'b' ? (offset[0] == 'b' ? 's' : offset[0]) : 'l';
        string number = kind == 'l' ? offset : offset[1..];
        int by = number.Length == 0 ? 0 : number is "+" ? 1 : number is "-" ? -1 : int.TryParse(number, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int read) ? read : 0;
        return (kind, by);
    }

    /// <summary>The first match starting after an offset, or with <paramref name="byEnd"/> ending (its last character)
    /// after it; -1 with none. Matches never overlap, so their ends are in order as their starts are.</summary>
    private static int FirstAfter(IReadOnlyList<TextRange> matches, int offset, bool byEnd)
    {
        int i = byEnd ? Sorted.FirstAfter(matches, LastOf, offset) : Sorted.FirstAfter(matches, m => m.Start, offset);
        return i < matches.Count ? i : -1;
    }

    private static int LastBefore(IReadOnlyList<TextRange> matches, int offset, bool byEnd) =>
        (byEnd ? Sorted.FirstAtOrAfter(matches, LastOf, offset) : Sorted.FirstAtOrAfter(matches, m => m.Start, offset)) - 1;

    private static int LastOf(TextRange match) => Math.Max(match.Start, match.End - 1);

    /// <summary>What the last search said (a wrap, a pattern not found), for the status bar.</summary>
    private (string Text, bool Error)? searchSaid;

    /// <summary>* and # (g* and g# without the word's edges): the keyword at or after the caret on its line searched for,
    /// forward or back, as the last search.</summary>
    private VimTarget? WordSearch(TextSnapshot text, int caret, int n, bool back, bool whole)
    {
        LineSpan line = VimMotions.LineOf(text, caret);
        int start = caret;
        while (start < line.End && !TextNavigation.IsWordChar(text[start]))
        {
            start++;
        }
        if (start >= line.End)
        {
            searchSaid = ("E348: No string under cursor", true);
            return null;
        }
        while (start > line.Start && TextNavigation.IsWordChar(text[start - 1]))
        {
            start--;
        }
        int end = start;
        while (end < line.End && TextNavigation.IsWordChar(text[end]))
        {
            end++;
        }
        string word = text.GetText(start, end - start);
        StringBuilder escaped = new();
        foreach (char c in word)
        {
            escaped.Append(c is '\\' or '/' or '.' or '*' or '$' or '^' or '~' or '[' or ']' ? "\\" + c : c.ToString());
        }
        Registers.LastSearch = new VimSearch(whole ? $"\\<{escaped}\\>" : escaped.ToString(), back);
        // * matches case as ignorecase says, never smartcase.
        bool smart = Options.SmartCase;
        Options.SmartCase = false;
        try
        {
            return SearchTarget(text, back ? start : start, n, reverse: false);
        }
        finally
        {
            Options.SmartCase = smart;
        }
    }

    /// <summary>The highlight a search asks the editor to show: its matches by the editor's own find, while hlsearch is on.</summary>
    private SearchQuery? SearchShown() =>
        Options.HighlightSearch && Registers.LastSearch is { } last && QueryOf(last.Pattern, out _) is { } query ? query : null;

    /// <summary>' and ` to a mark: its line's first non-blank, or the very place; '' and `` the place the last jump left.</summary>
    private VimTarget? MarkTarget(TextSnapshot text, string name, bool exact, IVimView view)
    {
        char mark = name is "'" or "`" ? '\'' : name[0];
        if (view.Mark(mark, text) is not { } at)
        {
            searchSaid = ("E20: Mark not set", true);
            return mark == '\'' ? new VimTarget(0, exact ? VimMotionKind.Exclusive : VimMotionKind.Linewise) : null;
        }
        return exact ? new VimTarget(Settled(text, at, breakEnds), VimMotionKind.Exclusive) : new VimTarget(VimMotions.FirstNonBlank(text, text.GetLineFromOffset(at), breakEnds), VimMotionKind.Linewise);
    }

    /// <summary>m: the mark named set at the caret; ' and ` set the place '' goes back to.</summary>
    private VimResult SetMark(Command command, SelectionSet selections, IVimView view)
    {
        char name = command.Argument![0];
        if (!(char.IsAsciiLetter(name) || name is '\'' or '`' or '<' or '>' or '[' or ']'))
        {
            return VimResult.Held;
        }
        view.SetMark(name == '`' ? '\'' : name, selections.Primary.Caret);
        return new VimResult(true, selections);
    }
}
