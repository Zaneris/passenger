using System.Xml;
using System.Xml.Linq;

namespace Passenger.Inspector;

/// <summary>A XAML file's root element as XAML Hot Reload compares two texts of it: its type, its x:Class, what each of its
/// attributes says (an attribute of no namespace sets a property or names an event's handler) and the text of each property
/// element it sets on itself (Window.DataContext). Read by XML's own rules: a text that does not read is refused at its
/// line (<see cref="UnreadXaml"/>).</summary>
internal sealed record XamlRoot(string Type, string? Class, IReadOnlyDictionary<string, string> Attributes, IReadOnlyDictionary<string, string> Elements)
{
    /// <summary>XAML's own namespace, x:Class's.</summary>
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static XamlRoot Read(string text)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.SetLineInfo);
        }
        catch (XmlException error)
        {
            throw new UnreadXaml(error.Message, error.LineNumber, error.LinePosition);
        }
        XElement root = document.Root ?? throw new UnreadXaml("The file has no root element", null, null);
        Dictionary<string, string> attributes = new(StringComparer.Ordinal);
        foreach (XAttribute attribute in root.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None))
        {
            attributes[attribute.Name.LocalName] = attribute.Value;
        }
        Dictionary<string, string> elements = new(StringComparer.Ordinal);
        string own = root.Name.LocalName + ".";
        foreach (XElement child in root.Elements().Where(c => c.Name.LocalName.StartsWith(own, StringComparison.Ordinal)))
        {
            elements[child.Name.LocalName[own.Length..]] = child.ToString(SaveOptions.DisableFormatting);
        }
        return new XamlRoot(root.Name.LocalName, (string?)root.Attribute(XName.Get("Class", XamlNamespace)), attributes, elements);
    }

    /// <summary>The root's properties and events this text sets that <paramref name="now"/> no longer does.</summary>
    public IEnumerable<string> Dropped(XamlRoot now) =>
        Attributes.Keys.Where(a => !now.Attributes.ContainsKey(a) && !now.Elements.ContainsKey(a))
            .Concat(Elements.Keys.Where(e => !now.Elements.ContainsKey(e) && !now.Attributes.ContainsKey(e)));

    /// <summary>Whether <paramref name="now"/> sets a property as this text does, to a value of its own (not a markup
    /// extension, which may read what changed): what the app holds in it then stands.</summary>
    public bool Keeps(XamlRoot now, string property) =>
        (Attributes.TryGetValue(property, out string? was) && now.Attributes.TryGetValue(property, out string? value) && was == value
            && !value.TrimStart().StartsWith('{'))
        || (Elements.TryGetValue(property, out string? before) && now.Elements.TryGetValue(property, out string? after) && before == after);

    /// <summary>The attribute that starts at a line and column (from one), for what the XAML compiler said there.</summary>
    public static (string Name, string Value)? AttributeAt(string text, int line, int column)
    {
        try
        {
            foreach (XAttribute attribute in XDocument.Parse(text, LoadOptions.SetLineInfo).Descendants().SelectMany(e => e.Attributes()))
            {
                if (attribute is IXmlLineInfo at && at.LineNumber == line && at.LinePosition == column)
                {
                    return (attribute.Name.LocalName, attribute.Value);
                }
            }
        }
        catch (XmlException)
        {
            // Read once already: a text that does not read has no attribute to name.
        }
        return null;
    }
}

/// <summary>A XAML text that does not read as XML, with where its reader stopped.</summary>
internal sealed class UnreadXaml(string message, int? line, int? column) : Exception(message)
{
    public int? Line { get; } = line;

    public int? Column { get; } = column;
}
