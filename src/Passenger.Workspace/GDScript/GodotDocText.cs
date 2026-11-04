using System.Text;

namespace Passenger.Workspace.GDScript;

/// <summary>A part of a description as a card shows it: words, or a block of code.</summary>
public readonly record struct GodotDocPart(bool Code, string Text);

/// <summary>Godot's documentation text as a person reads it: the BBCode its class reference and a script's <c>##</c>
/// comments are written in (<c>[param node]</c>, <c>[method _ready]</c>, <c>[code]null[/code]</c>, <c>[Node]</c>,
/// <c>[codeblocks][gdscript]…</c>) made words and code blocks, a reference by its name, a method's with its parentheses,
/// GDScript's code alone where a block gives C#'s too; and a script's doc comments above a declaration. Pure.</summary>
public static partial class GodotDocText
{
    /// <summary>A line of annotations alone (<c>@export_range(0, 10)</c>, <c>@rpc</c>), which may stand between a doc comment and its declaration.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(@\w+(\([^)]*\))?\s*)+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex AnnotationsAlone();

    /// <summary>The tags that only style the words between them, left out.</summary>
    private static readonly HashSet<string> Styling = new(StringComparer.Ordinal)
    {
        "b", "i", "u", "s", "code", "kbd", "center", "url", "color", "font", "font_size", "/b", "/i", "/u", "/s", "/code", "/kbd", "/center", "/url",
        "/color", "/font", "/font_size", "/gdscript", "/csharp",
    };

    /// <summary>The references whose name follows their word (<c>[member speed]</c>).</summary>
    private static readonly HashSet<string> References = new(StringComparer.Ordinal)
    {
        "method", "member", "signal", "constant", "enum", "annotation", "theme_item", "param", "operator", "constructor",
    };

    /// <summary>A description's words and code blocks in order, at most <paramref name="paragraphs"/> paragraphs of words.</summary>
    public static IReadOnlyList<GodotDocPart> Parts(string bbcode, int paragraphs = int.MaxValue)
    {
        List<GodotDocPart> parts = [];
        StringBuilder words = new();
        int said = 0;
        void Flush()
        {
            foreach (string paragraph in words.ToString().Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                if (said++ < paragraphs)
                {
                    parts.Add(new GodotDocPart(false, paragraph));
                }
            }
            words.Clear();
        }
        int at = 0;
        while (at < bbcode.Length && said < paragraphs)
        {
            char c = bbcode[at];
            int close = c == '[' ? bbcode.IndexOf(']', at) : -1;
            if (close < 0)
            {
                words.Append(c);
                at++;
                continue;
            }
            string tag = bbcode[(at + 1)..close];
            string word = tag.Split(' ', '=')[0];
            at = close + 1;
            if (word == "codeblock")
            {
                int end = bbcode.IndexOf("[/codeblock]", at, StringComparison.Ordinal);
                Flush();
                parts.Add(new GodotDocPart(true, bbcode[at..(end < 0 ? bbcode.Length : end)].Trim('\n')));
                at = end < 0 ? bbcode.Length : end + "[/codeblock]".Length;
            }
            else if (word == "codeblocks")
            {
                int end = bbcode.IndexOf("[/codeblocks]", at, StringComparison.Ordinal);
                string blocks = bbcode[at..(end < 0 ? bbcode.Length : end)];
                int gdscript = blocks.IndexOf("[gdscript]", StringComparison.Ordinal);
                int gdscriptEnd = gdscript < 0 ? -1 : blocks.IndexOf("[/gdscript]", gdscript, StringComparison.Ordinal);
                Flush();
                if (gdscriptEnd > gdscript)
                {
                    parts.Add(new GodotDocPart(true, blocks[(gdscript + "[gdscript]".Length)..gdscriptEnd].Trim('\n')));
                }
                at = end < 0 ? bbcode.Length : end + "[/codeblocks]".Length;
            }
            else if (word == "img")
            {
                int end = bbcode.IndexOf("[/img]", at, StringComparison.Ordinal);
                at = end < 0 ? bbcode.Length : end + "[/img]".Length;
            }
            else if (word == "br")
            {
                words.Append('\n');
            }
            else if (word == "lb")
            {
                words.Append('[');
            }
            else if (word == "rb")
            {
                words.Append(']');
            }
            else if (Styling.Contains(word))
            {
                continue;
            }
            else if (References.Contains(word) && tag.Length > word.Length + 1)
            {
                string named = tag[(word.Length + 1)..].Trim();
                words.Append(word is "method" or "constructor" ? named + "()" : named);
            }
            else if (tag.Length > 0 && (char.IsUpper(tag[0]) || tag[0] == '@') && tag.All(t => char.IsLetterOrDigit(t) || t is '_' or '@' or '.'))
            {
                words.Append(tag);
            }
            else
            {
                words.Append('[').Append(tag).Append(']');
            }
        }
        Flush();
        return parts;
    }

    /// <summary>A description as words alone, its code blocks among them, a paragraph a line.</summary>
    public static string Plain(string bbcode) => string.Join('\n', Parts(bbcode).Select(p => p.Text));

    /// <summary>The doc comment of a declaration of a script's text, by an offset on its line: the <c>##</c> lines just above
    /// it (its annotations' lines passed over) and a <c>##</c> after it on its line, without their marks and tags
    /// (<c>@tutorial</c>); null for none.</summary>
    public static string? CommentAbove(string text, int declared)
    {
        int lineStart = declared > 0 ? text.LastIndexOf('\n', Math.Min(declared, text.Length) - 1) + 1 : 0;
        int lineEnd = text.IndexOf('\n', lineStart) is var end and >= 0 ? end : text.Length;
        List<string> lines = [];
        for (int above = lineStart; above > 0;)
        {
            int previousEnd = above - 1;
            int previousStart = previousEnd > 0 ? text.LastIndexOf('\n', previousEnd - 1) + 1 : 0;
            string line = text[previousStart..previousEnd].Trim();
            above = previousStart;
            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                lines.Add(line);
            }
            else if (!(lines.Count == 0 && AnnotationsAlone().IsMatch(line)))
            {
                break;
            }
        }
        lines.Reverse();
        int inline = text.IndexOf("##", lineStart, lineEnd - lineStart, StringComparison.Ordinal);
        if (inline > lineStart && !text.AsSpan(lineStart, inline - lineStart).Trim().IsEmpty && text.LastIndexOf('#', inline - 1, inline - lineStart) < 0)
        {
            lines.Add(text[inline..lineEnd].Trim());
        }
        string said = string.Join('\n', lines.Select(l => l[2..].StartsWith(' ') ? l[3..] : l[2..]).Where(l => !l.TrimStart().StartsWith("@tutorial", StringComparison.Ordinal)));
        return said.Trim().Length == 0 ? null : said.Trim();
    }

    /// <summary>A script's own doc comment: the first block of <c>##</c> lines before its first member; null for none.</summary>
    public static string? ScriptComment(GDScriptTree tree)
    {
        string text = tree.Text;
        int before = tree.Members.Count > 0 ? tree.Members[0].Start : text.Length;
        List<string> lines = [];
        foreach (string raw in text[..Math.Min(before, text.Length)].Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                lines.Add(line[2..].StartsWith(' ') ? line[3..] : line[2..]);
            }
            else if (lines.Count > 0 && line.Length > 0 && !line.StartsWith('#'))
            {
                break;
            }
        }
        string said = string.Join('\n', lines.Where(l => !l.TrimStart().StartsWith("@tutorial", StringComparison.Ordinal))).Trim();
        return said.Length == 0 ? null : said;
    }
}
