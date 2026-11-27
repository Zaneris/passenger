using System.Text.RegularExpressions;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>Changes Markdown emphasis while keeping the selected words and their surrounding whitespace.</summary>
internal static partial class MarkdownFormatting
{
    public static (TextEdit Edit, TextRange Selection)? Toggle(string text, TextRange selected, bool bold)
    {
        TextRange words = TextRange.Trimmed(text, selected);
        string marker = bold ? "**"
            : (words.Start > 0 && char.IsLetterOrDigit(text[words.Start - 1])) || (words.End < text.Length && char.IsLetterOrDigit(text[words.End])) ? "*" : "_";
        if (selected.Length == 0)
        {
            if (HasMark(text, selected.Start - marker.Length, marker + marker))
            {
                return (new TextEdit(selected.Start - marker.Length, 2 * marker.Length, ""), new TextRange(selected.Start - marker.Length, 0));
            }
            return (new TextEdit(selected.Start, 0, marker + marker), new TextRange(selected.Start + marker.Length, 0));
        }

        if (words.Length == 0)
        {
            return null;
        }
        string value = text[words.Start..words.End];
        foreach (string mark in bold ? (string[])["**", "__"] : ["_", "*"])
        {
            if (Around(text, words, mark))
            {
                int at = words.Start - mark.Length;
                return (new TextEdit(at, words.Length + 2 * mark.Length, value), new TextRange(at, words.Length));
            }
            if (value.Length > 2 * mark.Length && HasMark(value, 0, mark) && HasMark(value, value.Length - mark.Length, mark))
            {
                string inner = value[mark.Length..^mark.Length];
                return (new TextEdit(words.Start, words.Length, inner), new TextRange(words.Start, inner.Length));
            }
        }
        if (value.Contains('\r') || value.Contains('\n'))
        {
            string[] parts = LineBreaks().Split(value);
            for (int i = 0; i < parts.Length; i += 2)
            {
                TextRange line = TextRange.Trimmed(parts[i], new TextRange(0, parts[i].Length));
                if (line.Length > 0)
                {
                    parts[i] = parts[i].Insert(line.End, marker).Insert(line.Start, marker);
                }
            }
            string formatted = string.Concat(parts);
            return (new TextEdit(words.Start, words.Length, formatted), new TextRange(words.Start, formatted.Length));
        }
        return (new TextEdit(words.Start, words.Length, marker + value + marker), new TextRange(words.Start + marker.Length, words.Length));
    }

    private static bool Around(string text, TextRange range, string mark) =>
        HasMark(text, range.Start - mark.Length, mark) && HasMark(text, range.End, mark);

    private static bool HasMark(string text, int at, string mark) => at >= 0 && at + mark.Length <= text.Length
        && text.AsSpan(at, mark.Length).SequenceEqual(mark)
        && (at == 0 || text[at - 1] != mark[0])
        && (at + mark.Length == text.Length || text[at + mark.Length] != mark[0]);

    [GeneratedRegex("(\\r\\n|\\r|\\n)")]
    private static partial Regex LineBreaks();
}
