using System.Xml;
using System.Xml.Linq;

namespace Passenger.Inspector;

/// <summary>A XAML file's root element as XAML Hot Reload compares two texts of it: its type, its x:Class, what each of its
/// attributes says (an attribute of no namespace sets a property or names an event's handler), the text of each property
/// element it sets on itself (Window.DataContext), and what it writes into the root's collections, against which what the
/// app put there itself is told. Read by XML's own rules: a text that does not read is refused at its line
/// (<see cref="UnreadXaml"/>).</summary>
internal sealed record XamlRoot(string Type, string? Class, IReadOnlyDictionary<string, string> Attributes, IReadOnlyDictionary<string, string> Elements)
{
    /// <summary>XAML's own namespace, x:Class's.</summary>
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>How many items each property element holds, by its property (Window.Styles' by Styles).</summary>
    public IReadOnlyDictionary<string, int> Items { get; private init; } = new Dictionary<string, int>();

    /// <summary>What its Resources element writes.</summary>
    public XamlResources Resources { get; private init; } = XamlResources.Empty;

    /// <summary>The classes its Classes attribute names.</summary>
    public IReadOnlySet<string> Classes => new HashSet<string>(
        Attributes.GetValueOrDefault("Classes")?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [], StringComparer.Ordinal);

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
        // Written as the root's type or a base's (FrameworkElement.Resources), each adds to the same collection.
        Dictionary<string, int> items = new(StringComparer.Ordinal);
        foreach (XElement child in root.Elements().Where(c => c.Name.LocalName.Contains('.', StringComparison.Ordinal)))
        {
            items[child.Name.LocalName[(child.Name.LocalName.LastIndexOf('.') + 1)..]] = child.Elements().Count();
        }
        return new XamlRoot(root.Name.LocalName, (string?)root.Attribute(XName.Get("Class", XamlNamespace)), attributes, elements)
        {
            Items = items,
            Resources = XamlResources.Read(root.Elements().FirstOrDefault(c => c.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal))),
        };
    }

    /// <summary>What of a collection the root holds the app put there itself, in the order held, given how many its text wrote
    /// (null, a text not known: all of them) and where each item came from: those before all the text made, and the rest.
    /// Of items alike the first are the text's, as the app adds to what its text made.</summary>
    public static (List<T> First, List<T> Last) Own<T>(IReadOnlyList<T> held, int? written, Func<T, XamlOrigin> origin)
        where T : class
    {
        HashSet<object> made = new(held.OrderBy(origin).Take(written ?? held.Count), ReferenceEqualityComparer.Instance);
        int first = made.Count == 0 ? 0 : held.Select((item, at) => (item, at)).First(i => made.Contains(i.item)).at;
        return ([.. held.Take(first).Where(i => !made.Contains(i))], [.. held.Skip(first).Where(i => !made.Contains(i))]);
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

/// <summary>Where an object of a root's collection came from, as far as the app's build recorded it, the likeliest to be the
/// text's first.</summary>
internal enum XamlOrigin
{
    /// <summary>The XAML file being applied.</summary>
    ThisFile,

    /// <summary>Another XAML file, as a style or a dictionary a text includes.</summary>
    AnotherFile,

    /// <summary>No XAML the build recorded: the app's own, or any item of a build that records none.</summary>
    Unrecorded,
}

/// <summary>What a root's Resources element writes, a ResourceDictionary inside it read as itself: its entries' keys, a type's
/// (a style's TargetType, a template's DataType) by the type's name, how many dictionaries it merges, and its theme
/// dictionaries' keys. A key given by another markup extension is not known here, so such an entry reads as written by no
/// text.</summary>
internal sealed record XamlResources(IReadOnlySet<string> Keys, IReadOnlySet<string> Types, int Merged, IReadOnlySet<string> Themes)
{
    /// <summary>XAML's own namespace, x:Key's.</summary>
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private const string Dictionary = "ResourceDictionary";

    public static readonly XamlResources Empty = new(new HashSet<string>(), new HashSet<string>(), 0, new HashSet<string>());

    /// <summary>Whether a text writes the entry of this key.</summary>
    public bool Writes(object key) => key switch
    {
        string name => Keys.Contains(name),
        Type type => Types.Contains(type.Name),
        _ => false,
    };

    public static XamlResources Read(XElement? resources)
    {
        if (resources is null)
        {
            return Empty;
        }
        XElement holder = resources.Elements().FirstOrDefault(e => e.Name.LocalName == Dictionary && Key(e) is null) ?? resources;
        HashSet<string> keys = new(StringComparer.Ordinal);
        HashSet<string> types = new(StringComparer.Ordinal);
        HashSet<string> themes = new(StringComparer.Ordinal);
        int merged = 0;
        foreach (XElement entry in holder.Elements())
        {
            if (entry.Name.LocalName == $"{Dictionary}.MergedDictionaries")
            {
                merged += entry.Elements().Count();
            }
            else if (entry.Name.LocalName == $"{Dictionary}.ThemeDictionaries")
            {
                themes.UnionWith(entry.Elements().Select(Key).OfType<string>());
            }
            else if (Key(entry) is not { } key)
            {
                if (((string?)entry.Attribute("TargetType") ?? (string?)entry.Attribute("DataType")) is { } implied && TypeNamed(implied) is { } type)
                {
                    types.Add(type);
                }
            }
            else if (!key.TrimStart().StartsWith('{'))
            {
                keys.Add(key);
            }
            else if (TypeNamed(key) is { } typed)
            {
                types.Add(typed);
            }
        }
        return new XamlResources(keys, types, merged, themes);
    }

    private static string? Key(XElement entry) => (string?)entry.Attribute(XName.Get("Key", XamlNamespace));

    /// <summary>A type's bare name as XAML writes it: plainly, with a prefix, or by x:Type; null for any other extension.</summary>
    private static string? TypeNamed(string written)
    {
        string name = written.Trim();
        if (name.StartsWith('{'))
        {
            string[] words = name.Trim('{', '}').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words is not [_, ..] || !words[0].EndsWith(":Type", StringComparison.Ordinal) || words.Length < 2)
            {
                return null;
            }
            name = words[1];
            name = name[(name.IndexOf('=', StringComparison.Ordinal) + 1)..];
        }
        return name[(name.LastIndexOf(':') + 1)..];
    }
}

/// <summary>A XAML text that does not read as XML, with where its reader stopped.</summary>
internal sealed class UnreadXaml(string message, int? line, int? column) : Exception(message)
{
    public int? Line { get; } = line;

    public int? Column { get; } = column;
}
