using System.Text;
using System.Text.RegularExpressions;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>A hover's Markdown as language servers write it, read once for the window's card (its words) and an agent (its
/// text): fenced code apart from the prose between it, and in a line of prose, outside a code span, a backslash before
/// punctuation and a character reference (<see cref="XmlEntities"/>, and <c>&amp;nbsp;</c>) read as the character each
/// stands for, a code span's characters as written, and neither an image nor a data URI's bytes, which are no words.</summary>
internal static partial class HoverMarkdown
{
    /// <summary>The first of the Private Use Area's characters, which no Markdown mark is, that a line's parts are held as
    /// while its marks are read (<see cref="Held"/>).</summary>
    private const char FirstHeld = '';

    private const char LastHeld = '';

    /// <summary>The Markdown's fenced blocks of code and the prose between them, in order, each as its lines, the fences'
    /// own lines left out.</summary>
    public static IEnumerable<(IReadOnlyList<string> Lines, bool Code)> Blocks(string markdown)
    {
        bool code = false;
        List<string> block = [];
        foreach (string line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                yield return (block, code);
                block = [];
                code = !code;
                continue;
            }
            block.Add(line);
        }
        yield return (block, code);
    }

    /// <summary>A line of prose as the card shows it: a link as its words, emphasis and a code span's backticks taken out.</summary>
    public static string Words(string line) => Read(line, words: true);

    /// <summary>A line of prose as an agent reads it: a code span, a link and emphasis as written.</summary>
    public static string Text(string line) => Read(line, words: false);

    /// <summary>Plain text without a data URI's bytes.</summary>
    public static string Dataless(string text) => DataUri().Replace(text, string.Empty);

    private static string Read(string line, bool words)
    {
        line = line.TrimEnd();
        List<string> held = [];
        if (Held(line, words, held) is not { } marked)
        {
            // More parts than the Area has characters: read with Markdown's marks left in.
            return Imageless(Held(line, words, null)!);
        }
        string shown = Imageless(marked);
        if (words)
        {
            shown = MarkdownItalic().Replace(MarkdownLink().Replace(shown, "$1")
                .Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("__", string.Empty, StringComparison.Ordinal), "$1$2");
        }
        return Released(shown, held);
    }

    /// <summary>The line with each code span, escaped character, character reference and character of the Private Use Area
    /// held as one character of that Area (<see cref="FirstHeld"/>), <paramref name="held"/> what each stands for: a code
    /// span as written, or its code alone for <paramref name="words"/>; with no list, each put as it stands for. Null where
    /// the line has more such parts than the Area has characters.</summary>
    private static string? Held(string line, bool words, List<string>? held)
    {
        StringBuilder marked = new(line.Length);
        // The lengths of the runs of backticks found to close no span from where they were looked for on.
        HashSet<int> unclosed = [];
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            string? meant = null;
            int length = 1;
            if (c == '`')
            {
                length = Run(line, i);
                int close = unclosed.Contains(length) ? -1 : Closing(line, i + length, length);
                if (close < 0)
                {
                    unclosed.Add(length);
                }
                else
                {
                    meant = words ? Code(line[(i + length)..close]) : line[i..(close + length)];
                    length = close + length - i;
                }
            }
            else if (c == '\\' && i + 1 == line.Length)
            {
                // Markdown's line break.
                meant = string.Empty;
            }
            else if (c == '\\' && char.IsAscii(line[i + 1]) && (char.IsPunctuation(line[i + 1]) || char.IsSymbol(line[i + 1])))
            {
                meant = line[i + 1].ToString();
                length = 2;
            }
            else if (c == '&' && Reference(line, i, out int written) is { } named)
            {
                meant = named;
                length = written;
            }
            else if (c is >= FirstHeld and <= LastHeld)
            {
                meant = c.ToString();
            }
            if (meant is null)
            {
                marked.Append(line, i, length);
            }
            else if (held is null)
            {
                marked.Append(meant);
            }
            else if (held.Count > LastHeld - FirstHeld)
            {
                return null;
            }
            else
            {
                marked.Append((char)(FirstHeld + held.Count));
                held.Add(meant);
            }
            i += length;
        }
        return marked.ToString();
    }

    /// <summary>The line with what each held character stands for in its place (<see cref="Held"/>).</summary>
    private static string Released(string marked, List<string> held)
    {
        if (held.Count == 0)
        {
            return marked;
        }
        StringBuilder text = new(marked.Length);
        foreach (char c in marked)
        {
            int index = c - FirstHeld;
            if (index >= 0 && index < held.Count)
            {
                text.Append(held[index]);
            }
            else
            {
                text.Append(c);
            }
        }
        return text.ToString();
    }

    /// <summary>How many backticks run from <paramref name="at"/>.</summary>
    private static int Run(string line, int at)
    {
        int end = at;
        while (end < line.Length && line[end] == '`')
        {
            end++;
        }
        return end - at;
    }

    /// <summary>Where a run of exactly <paramref name="ticks"/> backticks from <paramref name="from"/> on begins, which
    /// closes a code span opened by as many; -1 where none does.</summary>
    private static int Closing(string line, int from, int ticks)
    {
        int at = line.IndexOf('`', from);
        while (at >= 0)
        {
            int run = Run(line, at);
            if (run == ticks)
            {
                return at;
            }
            at = line.IndexOf('`', at + run);
        }
        return -1;
    }

    /// <summary>A code span's code: a space taken off each end where both have one and it is not all spaces.</summary>
    private static string Code(string span) =>
        span.Length >= 2 && span[0] == ' ' && span[^1] == ' ' && span.AsSpan().ContainsAnyExcept(' ') ? span[1..^1] : span;

    /// <summary>The characters a character reference at <paramref name="at"/> names, <c>&amp;nbsp;</c> a space.</summary>
    private static string? Reference(string line, int at, out int length)
    {
        const string Nbsp = "&nbsp;";
        if (string.CompareOrdinal(line, at, Nbsp, 0, Nbsp.Length) == 0)
        {
            length = Nbsp.Length;
            return " ";
        }
        return XmlEntities.ReferenceAt(line, at, out length);
    }

    /// <summary>A line without its images and data URIs, and without the space one began it with.</summary>
    private static string Imageless(string line)
    {
        string without = Dataless(MarkdownImage().Replace(line, string.Empty));
        return without.Length != line.Length && line.TrimStart().StartsWith("![", StringComparison.Ordinal) ? without.TrimStart() : without;
    }

    /// <summary>Markdown's _italic_ and *italic* (TypeScript's *@param*), shown as their words.</summary>
    [GeneratedRegex(@"(?<!\w)_([^_\n]+)_(?!\w)|(?<![\w*])\*(\S(?:[^*\n]*\S)?)\*(?![\w*])")]
    private static partial Regex MarkdownItalic();

    /// <summary>A Markdown image and the blanks after it, which words cannot show (the CSS server's Baseline icon).</summary>
    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)[ \t]*")]
    private static partial Regex MarkdownImage();

    /// <summary>A Markdown link, shown as its words (the CSS server's MDN Reference).</summary>
    [GeneratedRegex(@"\[([^\]]+)\]\([^)\s]*\)")]
    private static partial Regex MarkdownLink();

    /// <summary>A data URI's base64 bytes.</summary>
    [GeneratedRegex(@"\bdata:[\w.+/-]*(?:;[\w.+=-]+)*;base64,[A-Za-z0-9+/=]+")]
    private static partial Regex DataUri();
}
