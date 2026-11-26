using Passenger.Editor;
using Passenger.Roslyn.Extension;

namespace Passenger.Languages.CSharp;

/// <summary>Code a C# server's answer gives in parts (<see cref="CodeParts"/>): its text and colours, each part's token
/// type the editor's kind as a file's are (<see cref="SemanticKinds"/>), so a card's code is coloured as the
/// same code in the editor is.</summary>
public static class ServerCode
{
    /// <summary>The parts' text, and a span for each part of a type the editor colours; blank lines before and after the
    /// code left out.</summary>
    public static (string Text, SyntaxSpan[] Spans) Of(IReadOnlyList<CodePart> parts, Dictionary<int, string>? declarations = null)
    {
        string whole = string.Concat(parts.Select(p => p.Text));
        int start = whole.Length - whole.TrimStart('\n', '\r').Length;
        int end = whole.TrimEnd('\n', '\r', ' ').Length;
        List<SyntaxSpan> spans = [];
        int at = 0;
        foreach (CodePart part in parts)
        {
            int from = Math.Max(at, start);
            int to = Math.Min(at + part.Text.Length, end);
            if (to > from)
            {
                TokenKind? kind = part.Type is { } type ? SemanticKinds.Of(type) : null;
                if (declarations is not null && part.Declaration is { } key)
                {
                    declarations.Add(from - start, key);
                    kind ??= TokenKind.Text;
                }
                if (kind is { } coloured)
                {
                    spans.Add(new SyntaxSpan(from - start, to - from, coloured));
                }
            }
            at += part.Text.Length;
        }
        return (end > start ? whole[start..end] : string.Empty, [.. spans]);
    }

    /// <summary>Whether two texts of code are the same but for the spaces ending their lines and the blank lines around them,
    /// as Markdown's line breaks write a code block's lines.</summary>
    public static bool SameCode(string one, string other) => Lines(one).SequenceEqual(Lines(other), StringComparer.Ordinal);

    private static IEnumerable<string> Lines(string code) =>
        code.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n', ' ').Split('\n').Select(l => l.TrimEnd(' '));
}
