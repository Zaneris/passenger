using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.Text;

/// <summary>A glob over a path from a folder ("/src/A.cs"), as an .editorconfig section's reads it (<see cref="EditorConfig"/>),
/// compiled once by its text: <c>*</c> any characters but '/', <c>**</c> any at all ("/**/" none too), <c>?</c> one but '/',
/// <c>[name]</c> one of those (<c>[!name]</c> none), <c>{s1,s2}</c> any of those globs, <c>{num1..num2}</c> a whole number
/// between, a backslash taking the next character as it is, and a bracket or brace with no end itself. Matched by a walk of
/// its own over the path's characters, every place the glob may stand at once, which keeps nothing and makes nothing; a glob
/// of number ranges, or of more choices or places than the walk holds, by a regular expression on the linear engine.
/// Thread-safe.</summary>
public sealed class PathGlob
{
    /// <summary>How deep a glob's braces may nest, which bounds how deep its translation goes.</summary>
    private const int DeepestBraces = 64;

    /// <summary>The most choices the walk takes, its braces' choices multiplied out.</summary>
    private const int MostChoices = 64;

    /// <summary>The most parts a choice the walk takes may have: its places, and the end, are the bits of one number.</summary>
    private const int MostParts = 63;

    private static readonly ConcurrentDictionary<(string Pattern, bool Lowered), PathGlob> Compiled = new();

    /// <summary>The choices the walk matches, or null where the regular expression does.</summary>
    private readonly Choice[]? choices;
    private readonly Regex? regex;
    private readonly IReadOnlyList<(long Min, long Max)> ranges = [];
    private readonly bool lowered;

    private PathGlob(string pattern, bool lowered)
    {
        this.lowered = lowered;
        string read = lowered ? pattern.ToLowerInvariant() : pattern;
        if (Walked(read, 0) is { } walked)
        {
            choices = [.. walked.Select(parts => new Choice([.. parts]))];
            return;
        }
        Translation translation = new();
        string expression = Translate(read, translation, 0);
        if (translation.Refused is { } why)
        {
            Refused = why;
            return;
        }
        try
        {
            // The repository's text: on the linear engine, so no glob holds a match (and the window) past a moment.
            regex = new Regex(@"\A" + expression + @"\z", RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking);
            ranges = translation.Ranges;
        }
        catch (RegexParseException failed)
        {
            Refused = $"it does not read as a glob ({failed.Error})";
        }
    }

    /// <summary>The glob of a pattern, whose path's first '/' is written ("/**/*.cs"), compiled once; <paramref name="lowered"/>,
    /// the pattern and each path matched in lower case, so it matches a path in any letter case.</summary>
    public static PathGlob Of(string pattern, bool lowered = false) => Compiled.GetOrAdd((pattern, lowered), key => new PathGlob(key.Pattern, key.Lowered));

    /// <summary>Why the glob matches no path, or null where it may match one.</summary>
    public string? Refused { get; }

    /// <summary>Whether the glob matches a path, which begins with '/' (on Windows a backslash parts names as a slash does);
    /// <paramref name="rooted"/> takes a path that does not as though it did.</summary>
    public bool Matches(ReadOnlySpan<char> path, bool rooted = false)
    {
        if (Refused is not null)
        {
            return false;
        }
        if (choices is null)
        {
            return MatchesByRegex(path, rooted);
        }
        bool slash = rooted && (path.IsEmpty || !IsSeparator(path[0]));
        foreach (Choice choice in choices)
        {
            if (choice.Matches(path, slash, lowered))
            {
                return true;
            }
        }
        return false;
    }

    private bool MatchesByRegex(ReadOnlySpan<char> path, bool rooted)
    {
        string written = (rooted && (path.IsEmpty || !IsSeparator(path[0])) ? "/" : string.Empty) + path.ToString();
        if (OperatingSystem.IsWindows())
        {
            written = written.Replace('\\', '/');
        }
        if (lowered)
        {
            written = written.ToLowerInvariant();
        }
        if (regex!.Match(written) is not { Success: true } match)
        {
            return false;
        }
        for (int i = 0; i < ranges.Count; i++)
        {
            Group number = match.Groups["n" + i.ToString(CultureInfo.InvariantCulture)];
            if (!number.Success)
            {
                continue;
            }
            string digits = number.Value.TrimStart('+', '-');
            // "{1..10}" matches "07" no more than a shell's brace expansion would make it.
            if ((digits.Length > 1 && digits[0] == '0') || !long.TryParse(number.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value)
                || value < ranges[i].Min || value > ranges[i].Max)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSeparator(char c) => c == '/' || c == '\\' && OperatingSystem.IsWindows();

    /// <summary>A path's character as the glob reads it: a Windows backslash as the slash it is there, in lower case where
    /// <paramref name="lowered"/>.</summary>
    private static char Read(char c, bool lowered)
    {
        char named = c == '\\' && OperatingSystem.IsWindows() ? '/' : c;
        return lowered ? char.ToLowerInvariant(named) : named;
    }

    private enum Kind : byte
    {
        Literal,
        One,
        Set,
        Star,
        Any,
    }

    /// <summary>A part of a choice: a character as itself, one but '/', one of a set's, any run but '/', any run at all.</summary>
    private readonly record struct Part(Kind Kind, char Character = '\0', (char Low, char High)[]? Ranges = null, bool Negated = false)
    {
        public bool Takes(char c) => Kind switch
        {
            Kind.Literal => c == Character,
            Kind.One => c != '/',
            Kind.Set => c != '/' && InSet(c) != Negated,
            _ => false,
        };

        private bool InSet(char c)
        {
            foreach ((char low, char high) in Ranges!)
            {
                if (c >= low && c <= high)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>One of a glob's choices, braces multiplied out: its parts, walked with every place it may stand at once as
    /// the bits of one number (the bit past the last part the match); the characters it must end with, checked first.</summary>
    private sealed class Choice
    {
        private readonly Part[] parts;
        private readonly ulong runs;
        private readonly string ending;

        public Choice(Part[] parts)
        {
            this.parts = parts;
            for (int i = 0; i < parts.Length; i++)
            {
                runs |= parts[i].Kind is Kind.Star or Kind.Any ? 1UL << i : 0;
            }
            int last = parts.Length;
            while (last > 0 && parts[last - 1].Kind == Kind.Literal)
            {
                last--;
            }
            ending = string.Concat(parts[last..].Select(p => p.Character));
        }

        public bool Matches(ReadOnlySpan<char> path, bool slash, bool lowered)
        {
            if (path.Length + (slash ? 1 : 0) < ending.Length)
            {
                return false;
            }
            for (int i = 1; i <= ending.Length && i <= path.Length; i++)
            {
                if (Read(path[^i], lowered) != ending[^i])
                {
                    return false;
                }
            }
            ulong at = Closed(1UL);
            if (slash && (at = Step(at, '/')) == 0)
            {
                return false;
            }
            foreach (char c in path)
            {
                if ((at = Step(at, Read(c, lowered))) == 0)
                {
                    return false;
                }
            }
            return (at >> parts.Length & 1) != 0;
        }

        /// <summary>The places a character takes the walk to from those it stands at.</summary>
        private ulong Step(ulong at, char c)
        {
            ulong next = 0;
            for (ulong left = at; left != 0; left &= left - 1)
            {
                int i = BitOperations.TrailingZeroCount(left);
                if (i == parts.Length)
                {
                    continue;
                }
                Part part = parts[i];
                if (part.Kind == Kind.Any || part.Kind == Kind.Star && c != '/')
                {
                    next |= 1UL << i;
                }
                else if (part.Takes(c))
                {
                    next |= 1UL << (i + 1);
                }
            }
            return Closed(next);
        }

        /// <summary>The places, with those past each run they stand at, which may be empty.</summary>
        private ulong Closed(ulong at)
        {
            for (ulong passing = at & runs; passing != 0;)
            {
                ulong past = passing << 1;
                passing = past & ~at & runs;
                at |= past;
            }
            return at;
        }
    }

    /// <summary>A glob's choices as the walk takes them, each a list of parts, translated as <see cref="Translate"/> reads
    /// it; null for one only the regular expression takes: a number range, a bracket that refuses it, braces nested past
    /// <see cref="DeepestBraces"/>, or more choices or parts than the walk holds.</summary>
    private static List<List<Part>>? Walked(string pattern, int depth)
    {
        if (depth > DeepestBraces)
        {
            return null;
        }
        List<List<Part>> choices = [[]];
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            List<List<Part>>? then;
            switch (c)
            {
                case '\\' when i + 1 < pattern.Length:
                    then = [[new Part(Kind.Literal, pattern[++i])]];
                    break;
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    then = [[new Part(Kind.Any)]];
                    i++;
                    break;
                case '*':
                    then = [[new Part(Kind.Star)]];
                    break;
                case '?':
                    then = [[new Part(Kind.One)]];
                    break;
                case '/' when pattern.AsSpan(i + 1).StartsWith("**/", StringComparison.Ordinal):
                    then = [[new Part(Kind.Literal, '/')], [new Part(Kind.Literal, '/'), new Part(Kind.Any), new Part(Kind.Literal, '/')]];
                    i += 3;
                    break;
                case '[' when ClassEnd(pattern, i) is int close and > 0:
                    if (Set(pattern[(i + 1)..close]) is not { } set)
                    {
                        return null;
                    }
                    then = [[set]];
                    i = close;
                    break;
                case '{' when BraceEnd(pattern, i) is int close and > 0:
                    string inner = pattern[(i + 1)..close];
                    if (IsRange(inner))
                    {
                        return null;
                    }
                    List<string> written = Choices(inner);
                    if (written.Count < 2)
                    {
                        // One choice is no choice: the braces are themselves.
                        if (Walked(inner, depth + 1) is not { } within)
                        {
                            return null;
                        }
                        then = [.. within.Select(parts => Joined([new Part(Kind.Literal, '{'), .. parts], [new Part(Kind.Literal, '}')]))];
                    }
                    else
                    {
                        then = [];
                        foreach (string choice in written)
                        {
                            if (Walked(choice, depth + 1) is not { } within)
                            {
                                return null;
                            }
                            then.AddRange(within);
                        }
                    }
                    i = close;
                    break;
                default:
                    then = [[new Part(Kind.Literal, c)]];
                    break;
            }
            if ((long)choices.Count * then.Count > MostChoices)
            {
                return null;
            }
            choices = [.. choices.SelectMany(parts => then.Select(more => Joined(parts, more)))];
            if (choices.Any(parts => parts.Count > MostParts))
            {
                return null;
            }
        }
        return choices;
    }

    /// <summary>One choice's parts, then another's.</summary>
    private static List<Part> Joined(List<Part> first, List<Part> then)
    {
        List<Part> joined = new(first.Count + then.Count);
        joined.AddRange(first);
        joined.AddRange(then);
        return joined;
    }

    /// <summary>Whether a brace's text is a number range, which the regular expression matches.</summary>
    private static bool IsRange(string inner) => Regex.IsMatch(inner, @"^([+-]?\d+)\.\.([+-]?\d+)$", RegexOptions.CultureInvariant);

    /// <summary>A bracket's characters as a part of one character but '/': each as itself (a backslash taking the next), and
    /// <c>a-z</c> those from one to the other; null for a range that runs backwards, which refuses the glob.</summary>
    private static Part? Set(string inside)
    {
        bool negated = inside.Length > 0 && inside[0] is '!' or '^';
        List<(char Low, char High)> ranges = [];
        for (int i = negated ? 1 : 0; i < inside.Length;)
        {
            char low = Literal(inside, ref i);
            char high = low;
            if (i + 1 < inside.Length && inside[i] == '-')
            {
                i++;
                high = Literal(inside, ref i);
            }
            if (high < low)
            {
                return null;
            }
            ranges.Add((low, high));
        }
        return new Part(Kind.Set, Ranges: [.. ranges], Negated: negated);
    }

    /// <summary>What a glob's translation has found so far: its numbers' ranges, and the first reason it cannot be used.</summary>
    private sealed class Translation
    {
        public List<(long Min, long Max)> Ranges { get; } = [];

        public string? Refused { get; set; }
    }

    /// <summary>A glob as a regular expression, each part as the class comment says, a number range checked once matched.</summary>
    private static string Translate(string pattern, Translation translation, int depth)
    {
        if (depth > DeepestBraces)
        {
            translation.Refused ??= $"its braces nest more than {Numbers.Said(DeepestBraces)} deep";
            return string.Empty;
        }
        StringBuilder regex = new();
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            switch (c)
            {
                case '\\' when i + 1 < pattern.Length:
                    regex.Append(Regex.Escape(pattern[++i].ToString()));
                    break;
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    regex.Append(".*");
                    i++;
                    break;
                case '*':
                    regex.Append("[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                case '/' when pattern.AsSpan(i + 1).StartsWith("**/", StringComparison.Ordinal):
                    regex.Append("(?:/|/.*/)");
                    i += 3;
                    break;
                case '[' when ClassEnd(pattern, i) is int close and > 0:
                    regex.Append(CharacterClass(pattern[(i + 1)..close], translation));
                    i = close;
                    break;
                case '{' when BraceEnd(pattern, i) is int close and > 0:
                    string inner = pattern[(i + 1)..close];
                    Match range = Regex.Match(inner, @"^([+-]?\d+)\.\.([+-]?\d+)$", RegexOptions.CultureInvariant);
                    List<string> choices = Choices(inner);
                    if (range.Success && long.TryParse(range.Groups[1].Value, CultureInfo.InvariantCulture, out long first)
                        && long.TryParse(range.Groups[2].Value, CultureInfo.InvariantCulture, out long last))
                    {
                        regex.Append(CultureInfo.InvariantCulture, $"(?<n{translation.Ranges.Count}>[+-]?[0-9]+)");
                        translation.Ranges.Add((Math.Min(first, last), Math.Max(first, last)));
                    }
                    else if (choices.Count < 2)
                    {
                        // One choice is no choice: the braces are themselves.
                        regex.Append(@"\{").Append(Translate(inner, translation, depth + 1)).Append(@"\}");
                    }
                    else
                    {
                        regex.Append("(?:").AppendJoin('|', choices.Select(choice => Translate(choice, translation, depth + 1))).Append(')');
                    }
                    i = close;
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return regex.ToString();
    }

    /// <summary>Where the ']' closing a '[' at <paramref name="open"/> is, or -1 when none does before a '/'.</summary>
    private static int ClassEnd(string pattern, int open)
    {
        for (int i = open + 1; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    i++;
                    break;
                case '/':
                    return -1;
                case ']' when i > open + 1:
                    return i;
            }
        }
        return -1;
    }

    /// <summary>A bracket's characters as a class of the regular expression, which never matches '/': each character as
    /// itself (a backslash taking the next), and <c>a-z</c> those from one to the other, which may not run backwards.</summary>
    private static string CharacterClass(string inside, Translation translation)
    {
        bool negated = inside.Length > 0 && inside[0] is '!' or '^';
        StringBuilder regex = new(negated ? "[^/" : "[");
        for (int i = negated ? 1 : 0; i < inside.Length;)
        {
            char low = Literal(inside, ref i);
            char high = low;
            if (i + 1 < inside.Length && inside[i] == '-')
            {
                i++;
                high = Literal(inside, ref i);
            }
            if (high < low)
            {
                translation.Refused ??= $"its range {low}-{high} is in reverse order";
            }
            regex.Append(CultureInfo.InvariantCulture, $@"\u{(int)low:X4}");
            if (high != low)
            {
                regex.Append(CultureInfo.InvariantCulture, $@"-\u{(int)high:X4}");
            }
        }
        return regex.Append(negated ? "]" : "-[/]]").ToString();
    }

    /// <summary>The character at <paramref name="i"/> of a bracket, or the one a backslash there takes as itself, moving past it.</summary>
    private static char Literal(string inside, ref int i)
    {
        if (inside[i] == '\\' && i + 1 < inside.Length)
        {
            i++;
        }
        return inside[i++];
    }

    /// <summary>Where the '}' closing a '{' at <paramref name="open"/> is, braces inside counted, or -1 when none does.</summary>
    private static int BraceEnd(string pattern, int open)
    {
        int depth = 0;
        for (int i = open; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    i++;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return i;
            }
        }
        return -1;
    }

    /// <summary>A brace's choices: its text split at the commas not inside another brace nor escaped.</summary>
    private static List<string> Choices(string inner)
    {
        List<string> choices = [];
        int depth = 0;
        int start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            switch (inner[i])
            {
                case '\\':
                    i++;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    choices.Add(inner[start..i]);
                    start = i + 1;
                    break;
            }
        }
        choices.Add(inner[start..]);
        return choices;
    }
}
