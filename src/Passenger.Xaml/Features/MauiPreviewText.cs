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
}
