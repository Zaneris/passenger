using System.Globalization;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>A size an Avalonia preview draws its root at in place of the root's design size: a phone's or a tablet's screen
/// in device-independent units, upright or turned, as an app of the cross-platform template lays its view out there.</summary>
public sealed record PreviewSize(string Name, double Width, double Height)
{
    /// <summary>What a preview offers beside the design size, in the chooser's order.</summary>
    public static IReadOnlyList<PreviewSize> Offered { get; } =
    [
        new("Phone", 412, 915),
        new("Phone, turned", 915, 412),
        new("Tablet", 800, 1280),
        new("Tablet, turned", 1280, 800),
    ];

    /// <summary>The roots drawn as a sample through their <c>Design.PreviewWith</c>, which takes no size of its own.</summary>
    private static readonly string[] Samples = ["Styles", "Style", "ControlTheme", "ResourceDictionary", "Application"];

    /// <summary>The size as the chooser names it.</summary>
    public string Shown => string.Create(CultureInfo.InvariantCulture, $"{Name} ({Width:0} × {Height:0})");

    /// <summary>Whether a file's root takes a size: a control's, not a sample's or a file with no root.</summary>
    public static bool Fits(string xaml) => XmlTree.Parse(xaml).Root is { } root && !Samples.Contains(root.LocalName, StringComparer.Ordinal);

    /// <summary>The XAML as the previewer is sent it to draw its root at this size: the root's design width and height given
    /// this size's where it sets them (<c>d:DesignWidth</c>, <c>Design.Width</c>), else added after its name, so no line
    /// moves and the previewer's places stay the file's; the text as it is where its root takes no size.</summary>
    public string Sized(string xaml)
    {
        XmlTree tree = XmlTree.Parse(xaml);
        if (tree.Root is not { } root || Samples.Contains(root.LocalName, StringComparer.Ordinal))
        {
            return xaml;
        }
        List<(int Start, int End, string Text)> edits = [];
        bool width = false;
        bool height = false;
        foreach (XmlAttribute attribute in root.Attributes)
        {
            bool designer = attribute.Prefix.Length > 0 && root.Attribute($"xmlns:{attribute.Prefix}")?.Value == XamlDialect.DesignerUri;
            bool? wide = attribute.Name == "Design.Width" || (designer && attribute.LocalName == "DesignWidth") ? true
                : attribute.Name == "Design.Height" || (designer && attribute.LocalName == "DesignHeight") ? false
                : null;
            if (wide is not { } isWidth || !attribute.HasValue || !attribute.IsValueClosed)
            {
                continue;
            }
            edits.Add((attribute.ValueStart, attribute.ValueEnd, Number(isWidth ? Width : Height)));
            width |= isWidth;
            height |= !isWidth;
        }
        string added = (width ? string.Empty : $" Design.Width=\"{Number(Width)}\"") + (height ? string.Empty : $" Design.Height=\"{Number(Height)}\"");
        if (added.Length > 0)
        {
            edits.Add((root.NameEnd, root.NameEnd, added));
        }
        string sized = xaml;
        foreach ((int start, int end, string text) in edits.OrderByDescending(e => e.Start))
        {
            sized = string.Concat(sized.AsSpan(0, start), text, sized.AsSpan(end));
        }
        return sized;
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
