using Passenger.Xaml.Dialects;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>A .NET MAUI file's XAML as its previewer is sent it: MAUI's runtime loader reads only the namespaces a file
/// writes, where MAUI's compiler declares its implicit ones (the default, <c>x:</c>, <c>d:</c>) for a file whose root
/// declares no default namespace, so those are written into the root's start tag after its last attribute, for each prefix
/// the root leaves undeclared. No line moves and nothing before the tag's end does, so the previewer's places stay the file's
/// but for what follows the root's start tag on its last line.</summary>
public static class MauiPreviewText
{
    /// <summary>The prefixes declared for the loader where the root declares no default namespace, in the order written.</summary>
    private static readonly (string Prefix, string Uri)[] Implied =
    [
        (string.Empty, MauiDialect.GlobalUri),
        ("x", XamlDialect.Xaml2009Uri),
        ("d", MauiDialect.DesignUri),
    ];

    /// <summary>The XAML with MAUI's implicit namespaces declared on its root; as it is where the root declares a default
    /// namespace, or where there is no root or its start tag is unclosed (the loader says what is wrong with it).</summary>
    public static string Declared(string xaml)
    {
        if (XmlTree.Parse(xaml).Root is not { IsStartTagClosed: true } root || root.Attribute("xmlns") is not null)
        {
            return xaml;
        }
        string added = string.Concat(Implied.Where(p => p.Prefix.Length == 0 || root.Attribute("xmlns:" + p.Prefix) is null)
            .Select(p => p.Prefix.Length == 0 ? $" xmlns=\"{p.Uri}\"" : $" xmlns:{p.Prefix}=\"{p.Uri}\""));
        int at = root.StartTagEnd - (root.IsSelfClosing ? 2 : 1);
        while (at > root.NameEnd && char.IsWhiteSpace(xaml[at - 1]))
        {
            at--;
        }
        return string.Concat(xaml.AsSpan(0, at), added, xaml.AsSpan(at));
    }

    /// <summary>App.xaml's text with each merged dictionary it names by a Source of the project's own written inline as
    /// that file's text stands where <paramref name="open"/> gives it (an open file, edited or not): the loader takes a
    /// Source from the build's compiled dictionary, so an edit not yet built would not be drawn. The dictionary's own
    /// namespaces go with its contents; lines move, which nothing places by in App.xaml's answer.</summary>
    /// <param name="open">A Source as written to its file's text where it is open, else null.</param>
    public static string WithOpenDictionaries(string xaml, Func<string, string?> open)
    {
        List<(int Start, int End, string Text)> inlined = [];
        foreach (XmlElement merged in XmlTree.Parse(xaml).Elements)
        {
            if (merged.LocalName != "ResourceDictionary" || !merged.IsSelfClosing || merged.Attribute("Source") is not { IsValueClosed: true } source
                || source.Value.Contains(";assembly=", StringComparison.Ordinal) || open(source.Value) is not { } text
                || XmlTree.Parse(text).Root is not { LocalName: "ResourceDictionary", IsStartTagClosed: true, EndTagStart: { } contentEnd } root)
            {
                continue;
            }
            IEnumerable<string> attributes = merged.Attributes.Where(a => a != source).Select(a => xaml[a.NameStart..a.End])
                .Concat(root.Attributes.Where(a => a.Name == "xmlns" || a.Prefix == "xmlns").Select(a => text[a.NameStart..a.End]));
            string tag = string.Join(' ', [merged.Name, .. attributes]);
            inlined.Add((merged.Start, merged.End, $"<{tag}>{text[root.StartTagEnd..contentEnd]}</{merged.Name}>"));
        }
        string written = xaml;
        foreach ((int start, int end, string replacement) in inlined.OrderByDescending(i => i.Start))
        {
            written = string.Concat(written.AsSpan(0, start), replacement, written.AsSpan(end));
        }
        return written;
    }
}
