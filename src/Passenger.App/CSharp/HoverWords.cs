using Passenger.Lsp;

namespace Passenger.App.CSharp;

/// <summary>A hover card's parts as plain text, as an agent and Read as Text read them: code and words as they are, a
/// language server's made plain (<see cref="Plain"/>) and set apart from the rest by a blank line.</summary>
internal sealed class HoverWords : IHoverCard
{
    private readonly List<(string Text, bool Served)> parts = [];

    public string Text => string.Concat(parts.Select((p, i) => (i == 0 ? string.Empty : p.Served || parts[i - 1].Served ? "\n\n" : "\n") + p.Text));

    public void AddCode(string code, Languages.Language? language = null) => Add(code, false);

    public void AddText(string text) => Add(text, false);

    public void AddHover(Hover hover, bool csharp = false) => Add(Plain(hover), true);

    private void Add(string text, bool served)
    {
        if (text.Trim() is { Length: > 0 } said)
        {
            parts.Add((said, served));
        }
    }

    /// <summary>A hover card's words as plain text, by its kind: plain text as written, Markdown made plain text, its code
    /// fences, backslash escapes, entities and line-break spaces taken out, the code in it kept as written; no image's data
    /// (<see cref="HoverMarkdown.ByKind"/>), a run of blank lines kept as one.</summary>
    public static string Plain(Hover hover)
    {
        System.Text.StringBuilder text = new();
        bool gap = false;
        foreach (string line in HoverMarkdown.ByKind(Shapes.Text(hover.Contents), HoverMarkdown.IsPlain(hover.Contents), words: false).SelectMany(b => b.Lines))
        {
            string plain = line.TrimEnd();
            if (plain.Trim().Length == 0)
            {
                gap = text.Length > 0;
                continue;
            }
            text.Append(gap ? "\n" : string.Empty).Append(plain).Append('\n');
            gap = false;
        }
        return text.ToString().TrimEnd();
    }
}
