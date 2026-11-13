using System.Text;
using System.Text.RegularExpressions;

namespace Passenger.Editor;

/// <summary>A Vim pattern read as .NET's regex: its text, whether it matches case, or why it can't be read.</summary>
public readonly record struct VimPattern(string Regex, bool MatchCase, string? Problem = null);

/// <summary>Vim's regular expressions as .NET's: magic by default, \v \m \M \V switching how much is special, \c and \C
/// the case, \&lt; \&gt; word edges, \{n,m} counts (\{-} the fewest), \zs and \ze the match's ends, \%( groups, the
/// classes \s \d \w \a \l \u \x \h and their opposites, \_x taking line breaks too, and [:alpha:]-style classes in
/// brackets. What has no counterpart (\&amp;, \@ lookarounds, \%V and the like) is said rather than guessed; ~, the last
/// replacement, is read as itself. Substitutions' replacements read &amp;, \0 to \9, \r and \n (both a line break, where
/// Vim's \n writes a NUL), \t, \u \l \U \L \E and ~.</summary>
public static class VimPatterns
{
    private enum Magic
    {
        VeryNo,
        No,
        Magic,
        Very,
    }

    /// <summary>The regex a Vim pattern makes, its case from \c or \C, else <paramref name="ignoreCase"/> unless
    /// <paramref name="smartCase"/> finds a capital in it.</summary>
    public static VimPattern Translate(string pattern, bool ignoreCase, bool smartCase)
    {
        StringBuilder made = new(pattern.Length + 8);
        Magic magic = Magic.Magic;
        bool? caseFlag = null;
        bool capital = false;
        int depth = 0;
        int zs = -1;
        int ze = -1;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            bool escaped = c == '\\' && i + 1 < pattern.Length;
            char k = escaped ? pattern[++i] : c;
            if (!escaped && char.IsUpper(c))
            {
                capital = true;
            }
            // Whether the character is special: by itself in the magic levels that make it so, else with a backslash.
            bool special = escaped != SpecialAlone(k, magic);
            if (escaped && k is 'c' or 'C')
            {
                caseFlag = k == 'C';
                continue;
            }
            if (escaped && k is 'v' or 'm' or 'M' or 'V')
            {
                magic = k switch { 'v' => Magic.Very, 'm' => Magic.Magic, 'M' => Magic.No, _ => Magic.VeryNo };
                continue;
            }
            if (escaped && k == 'z' && i + 1 < pattern.Length && pattern[i + 1] is 's' or 'e')
            {
                if (depth > 0)
                {
                    return Problem(@"\zs and \ze only at the pattern's top level");
                }
                if (pattern[++i] == 's')
                {
                    zs = made.Length;
                }
                else
                {
                    ze = made.Length;
                }
                continue;
            }
            if (escaped && k == '_' && i + 1 < pattern.Length)
            {
                char of = pattern[++i];
                made.Append(of switch
                {
                    '.' => @"[\s\S]",
                    '^' => "^",
                    '$' => "$",
                    _ when Class(of) is { } inClass => $"(?:{inClass}|\\n)",
                    _ => Regex.Escape(of.ToString()),
                });
                continue;
            }
            if (escaped && k is 'n' or 't' or 'e' or 'r' or 's' or 'S' or 'd' or 'D' or 'w' or 'W' or 'a' or 'A' or 'l' or 'L' or 'u' or 'U' or 'x' or 'X' or 'o' or 'O' or 'h' or 'H' or 'k' or 'K' or 'i' or 'I')
            {
                made.Append(k switch { 'n' => @"\n", 't' => @"\t", 'e' => @"\x1b", 'r' => @"\r", _ => Class(k) });
                continue;
            }
            if (escaped && char.IsAsciiDigit(k) && k != '0')
            {
                made.Append('\\').Append(k);
                continue;
            }
            if (!special)
            {
                made.Append(k == '\n' ? @"\n" : Regex.Escape(k.ToString()));
                continue;
            }
            switch (k)
            {
                case '%' when i + 1 < pattern.Length && pattern[i + 1] == '(':
                    i++;
                    depth++;
                    made.Append("(?:");
                    break;
                case '%':
                    if (Percent(pattern, ref i) is not { } read)
                    {
                        return Problem("Vim's % atoms other than %( %^ %$ %d %x %u have no counterpart here");
                    }
                    made.Append(read);
                    break;
                case '(':
                    depth++;
                    made.Append('(');
                    break;
                case ')':
                    depth--;
                    made.Append(')');
                    break;
                case '|' or '+' or '*' or '.':
                    made.Append(k);
                    break;
                case '?' or '=':
                    made.Append('?');
                    break;
                case '<':
                    made.Append(@"\b(?=\w)");
                    break;
                case '>':
                    made.Append(@"\b(?<=\w)");
                    break;
                case '^':
                    // An anchor where a branch begins, else the character itself.
                    made.Append(AtBranchStart(pattern, i, escaped, magic) ? "^" : @"\^");
                    break;
                case '$':
                    made.Append(AtBranchEnd(pattern, i, magic) ? "$" : @"\$");
                    break;
                case '{':
                    int close = pattern.IndexOf('}', i);
                    if (close < 0)
                    {
                        return Problem("a \\{ count never closed");
                    }
                    string inner = pattern[(i + 1)..close].TrimEnd('\\');
                    i = close;
                    bool fewest = inner.StartsWith('-');
                    inner = fewest ? inner[1..] : inner;
                    string count = inner.Length == 0 ? "*" : inner.StartsWith(',') ? "{0" + inner + "}" : "{" + inner + "}";
                    made.Append(count).Append(fewest ? "?" : string.Empty);
                    break;
                case '[':
                    if (Bracket(pattern, ref i) is not { } bracket)
                    {
                        made.Append(@"\[");
                        break;
                    }
                    made.Append(bracket);
                    break;
                case '~':
                    made.Append('~');
                    break;
                case '&':
                    return Problem(@"Vim's \& (both branches) has no counterpart here");
                case '@':
                    return Problem(@"Vim's \@ lookarounds have no counterpart here; \zs and \ze often do");
                default:
                    made.Append(Regex.Escape(k.ToString()));
                    break;
            }
        }
        string regex = made.ToString();
        if (ze >= 0)
        {
            regex = regex[..ze] + "(?=" + regex[ze..] + ")";
        }
        if (zs >= 0)
        {
            regex = "(?<=" + regex[..zs] + ")" + regex[zs..];
        }
        bool matchCase = caseFlag ?? (!ignoreCase || (smartCase && capital));
        try
        {
            _ = new Regex(regex, RegexOptions.CultureInvariant | RegexOptions.Multiline);
        }
        catch (ArgumentException error)
        {
            return Problem(error.Message);
        }
        return new VimPattern(regex, matchCase);
    }

    private static VimPattern Problem(string why) => new(string.Empty, true, why);

    /// <summary>Whether a character is special without a backslash at a level of magic.</summary>
    private static bool SpecialAlone(char c, Magic magic) => magic switch
    {
        Magic.Very => c is '(' or ')' or '|' or '+' or '?' or '=' or '{' or '@' or '%' or '<' or '>' or '.' or '*' or '[' or '~' or '^' or '$' or '&',
        Magic.Magic => c is '.' or '*' or '[' or '~' or '^' or '$',
        Magic.No => c is '^' or '$',
        _ => false,
    };

    private static bool AtBranchStart(string pattern, int at, bool escaped, Magic magic)
    {
        int before = at - (escaped ? 2 : 1);
        if (before < 0)
        {
            return true;
        }
        string open = magic == Magic.Very ? "(" : @"\(";
        string or = magic == Magic.Very ? "|" : @"\|";
        string prefix = pattern[..(before + 1)];
        return prefix.EndsWith(open, StringComparison.Ordinal) || prefix.EndsWith(or, StringComparison.Ordinal) || prefix.EndsWith(@"\%(", StringComparison.Ordinal)
            || prefix.EndsWith(@"\v", StringComparison.Ordinal) || prefix.EndsWith(@"\m", StringComparison.Ordinal) || prefix.EndsWith(@"\c", StringComparison.Ordinal) || prefix.EndsWith(@"\C", StringComparison.Ordinal);
    }

    private static bool AtBranchEnd(string pattern, int at, Magic magic)
    {
        string rest = pattern[(at + 1)..];
        return rest.Length == 0 || rest.StartsWith(magic == Magic.Very ? ")" : @"\)", StringComparison.Ordinal) || rest.StartsWith(magic == Magic.Very ? "|" : @"\|", StringComparison.Ordinal)
            || rest is @"\c" or @"\C";
    }

    /// <summary>The class a letter after a backslash names, null for none.</summary>
    private static string? Class(char c) => c switch
    {
        's' => @"[ \t]",
        'S' => @"[^ \t\n]",
        'd' => "[0-9]",
        'D' => @"[^0-9\n]",
        'w' => "[0-9A-Za-z_]",
        'W' => @"[^0-9A-Za-z_\n]",
        'a' => "[A-Za-z]",
        'A' => @"[^A-Za-z\n]",
        'l' => "[a-z]",
        'L' => @"[^a-z\n]",
        'u' => "[A-Z]",
        'U' => @"[^A-Z\n]",
        'x' => "[0-9A-Fa-f]",
        'X' => @"[^0-9A-Fa-f\n]",
        'o' => "[0-7]",
        'O' => @"[^0-7\n]",
        'h' => "[A-Za-z_]",
        'H' => @"[^A-Za-z_\n]",
        'k' or 'i' => @"\w",
        'K' or 'I' => "[A-Za-z_]",
        _ => null,
    };

    /// <summary>%^ %$ and a character by number (%d123 %x2a %u20ac), read from just past the backslash; null for others.</summary>
    private static string? Percent(string pattern, ref int at)
    {
        if (at + 1 >= pattern.Length)
        {
            return null;
        }
        char kind = pattern[at + 1];
        if (kind is '^' or '$')
        {
            at++;
            return kind == '^' ? @"\A" : @"\z";
        }
        int radix = kind switch { 'd' => 10, 'x' or 'u' or 'U' => 16, 'o' => 8, _ => 0 };
        if (radix == 0)
        {
            return null;
        }
        int end = at + 2;
        while (end < pattern.Length && Uri.IsHexDigit(pattern[end]) && (radix == 16 || char.IsAsciiDigit(pattern[end])))
        {
            end++;
        }
        if (end == at + 2)
        {
            return null;
        }
        int value = Convert.ToInt32(pattern[(at + 2)..end], radix);
        at = end - 1;
        return value is >= 0 and <= 0x10FFFF ? Regex.Escape(char.ConvertFromUtf32(value)) : null;
    }

    /// <summary>A bracketed class from its [ to its ], its [:name:] classes spelled out; null where it never closes,
    /// leaving the [ a character.</summary>
    private static string? Bracket(string pattern, ref int at)
    {
        StringBuilder made = new("[");
        int i = at + 1;
        if (i < pattern.Length && pattern[i] == '^')
        {
            made.Append('^');
            i++;
        }
        if (i < pattern.Length && pattern[i] == ']')
        {
            made.Append(@"\]");
            i++;
        }
        for (; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == ']')
            {
                at = i;
                return made.Append(']').ToString();
            }
            if (c == '[' && i + 1 < pattern.Length && pattern[i + 1] == ':' && pattern.IndexOf(":]", i + 2, StringComparison.Ordinal) is var end and > 0)
            {
                string name = pattern[(i + 2)..end];
                string? named = name switch
                {
                    "alpha" => "A-Za-z",
                    "digit" => "0-9",
                    "alnum" => "0-9A-Za-z",
                    "lower" => "a-z",
                    "upper" => "A-Z",
                    "space" => @"\s",
                    "blank" => @" \t",
                    "xdigit" => "0-9A-Fa-f",
                    "punct" => @"!-/:-@\[-`{-~",
                    "cntrl" => @"\x00-\x1f\x7f",
                    "print" => @"\x20-\x7e",
                    "graph" => @"\x21-\x7e",
                    _ => null,
                };
                if (named is null)
                {
                    return null;
                }
                made.Append(named);
                i = end + 1;
                continue;
            }
            if (c == '\\' && i + 1 < pattern.Length)
            {
                char next = pattern[++i];
                made.Append(next switch { 'e' => @"\x1b", 't' => @"\t", 'r' => @"\r", 'n' => @"\n", 'b' => @"\x08", '\\' => @"\\", ']' => @"\]", '^' => @"\^", '-' => @"\-", _ => @"\\" + Regex.Escape(next.ToString()) });
                continue;
            }
            made.Append(c is '[' ? @"\[" : c.ToString());
        }
        return null;
    }

    /// <summary>A substitution's replacement for one match: &amp; and \0 the match, \1 to \9 its groups, ~ the last
    /// replacement, \r and \n a line break, \t a tab, \u \l the next character's case, \U \L the rest's until \E or \e.</summary>
    public static string Replace(string replacement, Match match, string? previous, string lineBreak)
    {
        StringBuilder made = new();
        char? once = null;
        char? held = null;
        void Put(string text)
        {
            foreach (char c in text)
            {
                char cased = held switch { 'U' => char.ToUpperInvariant(c), 'L' => char.ToLowerInvariant(c), _ => c };
                if (once is { } next)
                {
                    cased = next == 'u' ? char.ToUpperInvariant(cased) : char.ToLowerInvariant(cased);
                    once = null;
                }
                made.Append(cased);
            }
        }
        for (int i = 0; i < replacement.Length; i++)
        {
            char c = replacement[i];
            if (c == '&')
            {
                Put(match.Value);
                continue;
            }
            if (c == '~')
            {
                Put(previous ?? string.Empty);
                continue;
            }
            if (c != '\\' || i + 1 >= replacement.Length)
            {
                Put(c.ToString());
                continue;
            }
            char k = replacement[++i];
            switch (k)
            {
                case >= '0' and <= '9':
                    Put(match.Groups[k - '0'].Value);
                    break;
                case 'r' or 'n':
                    made.Append(lineBreak);
                    break;
                case 't':
                    Put("\t");
                    break;
                case 'u' or 'l':
                    once = k;
                    break;
                case 'U' or 'L':
                    held = k;
                    break;
                case 'E' or 'e':
                    held = null;
                    break;
                default:
                    Put(k.ToString());
                    break;
            }
        }
        return made.ToString();
    }
}
