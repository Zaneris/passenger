using System.Text;
using Passenger.Text;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>The C# of a XAML page's x:Code blocks, which the page's compiler puts in its class (WPF's markup compiler, MAUI's
/// source generator): each element of XAML's language named Code that the root holds, its text read as XML reads it (a
/// CDATA section's as it stands, the rest decoded, its runs a line apart), with where in the file each character is written.</summary>
public static class XamlCodeBlocks
{
    /// <summary>A block's code, and for each of its characters and its end the offset in the file it is written at.</summary>
    public sealed record Block(string Code, int[] WrittenOf)
    {
        /// <summary>Whether an offset of the file is in the block's code, at its end too.</summary>
        public bool Holds(int offset) => offset >= WrittenOf[0] && offset <= WrittenOf[^1];
    }

    public static IReadOnlyList<Block> In(XamlFile file) =>
        file.Tree.Root is not { } root ? []
        : [.. file.Tree.Elements.Where(e => e.Parent == root && e.LocalName == "Code" && XamlDialect.IsLanguageUri(file.UriOf(e, e.Prefix)) && e.Texts.Count > 0)
            .Select(e => Read(file.Text, e))];

    /// <summary>The block an offset of the file is in; null outside every one.</summary>
    public static Block? At(XamlFile file, int offset) => In(file).FirstOrDefault(b => b.Holds(offset));

    private static Block Read(string text, XmlElement element)
    {
        StringBuilder code = new();
        List<int> written = [];
        foreach (XmlText run in element.Texts)
        {
            if (code.Length > 0)
            {
                code.Append('\n');
                written.Add(run.Start);
            }
            int at = run.Start;
            while (at < run.End)
            {
                int semicolon = text[at] == '&' && !run.IsCdata ? text.IndexOf(';', at, Math.Min(12, run.End - at)) : -1;
                if (semicolon > at)
                {
                    foreach (char decoded in XmlEntities.Decode(text[at..(semicolon + 1)]))
                    {
                        code.Append(decoded);
                        written.Add(at);
                    }
                    at = semicolon + 1;
                    continue;
                }
                code.Append(text[at]);
                written.Add(at);
                at++;
            }
        }
        written.Add(element.Texts[^1].End);
        return new Block(code.ToString(), [.. written]);
    }
}
