using System.Text;
using System.Text.RegularExpressions;
using Passenger.Text;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>What a fix or refactoring does to one file: its edits, made from the file's text as it is when it is applied
/// (none when that text is no longer what it was made for), or the text of a file it makes.</summary>
public sealed record XamlFileChange(string Path, Func<string, IReadOnlyList<TextEdit>>? Edits, string? Made = null);

/// <summary>Where a resource or a style can go: a file and, as a list says it, the element it goes in
/// ("MainWindow.axaml &lt;Window.Resources&gt;").</summary>
public sealed record ResourcePlace(string Path, string Title);

/// <summary>A value Extract Resource can take: the property it sets, how its resource is written, the key suggested, and
/// where the same value is written for the same type in the file, this one first.</summary>
public sealed record ExtractableValue(string Property, string Value, ResourceShape Shape, string Suggested, IReadOnlyList<(int Start, int End)> Uses)
{
    /// <summary>Its uses but the one at the caret.</summary>
    public int Others => Uses.Count - 1;
}

/// <summary>How a resource of a value is written: its element's namespace, type and the prefix suggested for it when the
/// file has none, and whether the value is its Color attribute (a solid brush) rather than its content.</summary>
public sealed record ResourceShape(string Uri, string TypeName, string SuggestedPrefix, bool ColorAttribute, char Quote);

/// <summary>An element Extract Style can take: its name as written, the properties it sets that a style can set (a binding
/// left out at first), and the name suggested.</summary>
public sealed record StyleSource(string ElementName, int ElementStart, IReadOnlyList<StyleProperty> Properties, string Suggested, bool ByClass);

/// <summary>One of an element's attributes as Extract Style offers it: its name, its value with its quotes, whether it is
/// a binding, and where and how it is written whole.</summary>
public sealed record StyleProperty(string Name, string Written, bool IsBinding, int Start, int End, string Original);

/// <summary>XAML's refactorings: Extract Resource (a value to a keyed resource in this file, the application's or a
/// resource dictionary it reaches, its uses in the file replaced by <c>{StaticResource}</c>), Extract Style (an element's
/// properties to a style: WPF's keyed <c>Style</c> its element names, Avalonia's <c>Style</c> by a class its element
/// takes), and Surround With's elements. Each describes what is at the caret and makes its changes as
/// <see cref="XamlFileChange"/>s, each file's edits made from its text when applied.</summary>
public static partial class XamlRefactorings
{
    private static readonly HashSet<string> BrushTypes = new(StringComparer.Ordinal)
    {
        "Avalonia.Media.IBrush", "Avalonia.Media.Brush", "Avalonia.Media.ISolidColorBrush", "Avalonia.Media.SolidColorBrush",
        "System.Windows.Media.Brush", "System.Windows.Media.SolidColorBrush",
    };

    /// <summary>The value of the attribute at an offset, when Extract Resource can take it; else why not.</summary>
    public static (ExtractableValue? Value, string? Why) ValueAt(XamlFile file, int offset)
    {
        XmlPlace place = file.Tree.PlaceAt(offset);
        if (place is not { Kind: XmlPlaceKind.AttributeValue or XmlPlaceKind.AttributeName, Attribute: { } attribute })
        {
            return (null, "Put the caret on a property's value to extract it to a resource");
        }
        if (!attribute.HasValue || !attribute.IsValueClosed || attribute.Value.Trim().Length == 0)
        {
            return (null, $"'{attribute.Name}' has no value to extract");
        }
        if (attribute.Value.TrimStart().StartsWith('{'))
        {
            return (null, "A markup extension is not a value to extract to a resource");
        }
        if (PropertyOf(file, attribute) is not { } property)
        {
            return (null, $"'{attribute.Name}' sets no property whose value a resource can hold");
        }
        ClrType? type = file.Types.Resolve(file.Dialect.ValueTypeOf(property.ValueType));
        if (Shape(file, attribute, type) is not { } shape)
        {
            return (null, $"A {type?.Name ?? property.ValueType.Display} value cannot be written as a resource here");
        }
        List<(int, int)> uses = [(attribute.ValueStart, attribute.ValueEnd)];
        foreach (XmlAttribute other in file.Tree.Elements.SelectMany(e => e.Attributes))
        {
            if (other != attribute && other.HasValue && other.IsValueClosed && other.RawValue == attribute.RawValue && PropertyOf(file, other) is { } same
                && file.Types.Resolve(file.Dialect.ValueTypeOf(same.ValueType))?.FullName == type?.FullName)
            {
                uses.Add((other.ValueStart, other.ValueEnd));
            }
        }
        string suggested = Unique(file, shape.ColorAttribute ? (KnownColours.IsName(attribute.Value) ? Pascal(attribute.Value.Trim()) : property.Name) + "Brush"
            : (ObjectName(attribute.Owner) is { Length: > 0 } owner ? owner : string.Empty) + property.Name);
        return (new ExtractableValue(property.Name, attribute.RawValue, shape, suggested, uses), null);
    }

    /// <summary>Why a key will not do for a new resource reached from the file: empty, not one XAML can write, or the key of
    /// a resource the file reaches already; null when it will.</summary>
    public static string? KeyProblem(XamlFile file, string key)
    {
        if (key.Trim().Length == 0)
        {
            return "Resource name is empty";
        }
        if (key.IndexOfAny(['"', '\'', '<', '>', '&', '{', '}']) >= 0 || key != key.Trim())
        {
            return $"'{key}' is not a valid resource name";
        }
        return Reached(file).Any(r => r.Key == key) ? "Resource with the same name already exists" : null;
    }

    /// <summary>Where a new resource (or, for Avalonia's styles, a style) of the file can go: its own resources (or
    /// styles), the application's, and the resource dictionaries (or style files) the file reaches.</summary>
    public static IReadOnlyList<ResourcePlace> Places(XamlFile file, bool styles, Func<string, string?> textOf)
    {
        List<ResourcePlace> places = [];
        AddPlace(file.Path, file.Tree);
        XamlProject project = file.Project;
        XamlFileSummary own = project.Files.TryGetValue(file.Path, out XamlFileSummary? summary) ? summary : XamlFileSummary.Of(file.Path, file.Tree, project.ResolveSource);
        foreach (XamlFileSummary scope in project.ResourceScopes(own))
        {
            if (scope.Path != file.Path && textOf(scope.Path) is { } text)
            {
                XmlTree tree = XmlTree.Parse(text);
                if (scope.IsApplication || tree.Root?.LocalName == (styles ? "Styles" : "ResourceDictionary"))
                {
                    AddPlace(scope.Path, tree);
                }
            }
        }
        return places;

        void AddPlace(string path, XmlTree tree)
        {
            if (tree.Root is { } root && places.All(p => p.Path != path))
            {
                places.Add(new ResourcePlace(path, $"{System.IO.Path.GetFileName(path)} <{ContainerTitle(root, styles)}>"));
            }
        }
    }

    /// <summary>Extract Resource: the resource made where chosen, keyed, and the value it holds replaced by
    /// <c>{StaticResource key}</c> where it is at the caret and, with <paramref name="everywhere"/>, wherever else the
    /// file writes it for the same type (not inside the resources it goes in).</summary>
    public static IReadOnlyList<XamlFileChange> ExtractResource(XamlFile file, ExtractableValue value, string key, ResourcePlace place, bool everywhere)
    {
        IReadOnlyList<(int Start, int End)> uses = everywhere ? value.Uses : [value.Uses[0]];
        string reference = $"{{StaticResource {key}}}";
        Func<string, IReadOnlyList<TextEdit>> entry = text => Insert(text, Container.Resources, tree =>
        {
            List<TextEdit> declared = [];
            string x = Prefix(text, tree, XamlDialect.XamlUri, "x", declared);
            string type = Qualified(Prefix(text, tree, value.Shape.Uri, value.Shape.SuggestedPrefix, declared), value.Shape.TypeName);
            char quote = value.Shape.Quote;
            string made = value.Shape.ColorAttribute
                ? $"<{type} {x}:Key=\"{key}\" Color={quote}{value.Value}{quote} />"
                : $"<{type} {x}:Key=\"{key}\">{value.Value}</{type}>";
            return (made, declared);
        });
        IReadOnlyList<TextEdit> Replaced(string text, (int Start, int End)? inside)
        {
            List<TextEdit> edits = [];
            foreach ((int start, int end) in uses)
            {
                if (end > text.Length || text[start..end] != value.Value)
                {
                    return [];
                }
                if (inside is not { } container || start < container.Start || start >= container.End)
                {
                    edits.Add(new TextEdit(start, end - start, reference));
                }
            }
            return edits;
        }
        if (place.Path == file.Path)
        {
            return [new XamlFileChange(file.Path, text =>
            {
                IReadOnlyList<TextEdit> replaced = Replaced(text, ContainerRange(text, Container.Resources));
                return replaced.Count == 0 ? [] : [.. entry(text), .. replaced];
            })];
        }
        return [new XamlFileChange(file.Path, text => Replaced(text, null)), new XamlFileChange(place.Path, entry)];
    }

    /// <summary>The element at an offset and the properties it sets that a style can set, when Extract Style can take it;
    /// else why not.</summary>
    public static (StyleSource? Source, string? Why) StyleAt(XamlFile file, int offset)
    {
        XmlElement? element = file.Tree.ElementAt(offset);
        while (element is { IsPropertyElement: true })
        {
            element = element.Parent;
        }
        if (element is null || element.Parent is null)
        {
            return (null, "Put the caret on an element to extract its properties to a style");
        }
        if (file.TypeOf(element) is null)
        {
            return (null, $"Cannot resolve symbol '{element.Name}'");
        }
        List<StyleProperty> properties = [];
        foreach (XmlAttribute attribute in element.Attributes)
        {
            XamlAttributeMeaning meaning = file.MeaningOf(attribute);
            if (meaning.Kind is not (XamlAttributeKind.Property or XamlAttributeKind.Attached) || meaning.Property is not { IsReadOnly: false }
                || attribute.Name is "Name" or "Style" or "Classes" or "Theme" || !attribute.HasValue || !attribute.IsValueClosed)
            {
                continue;
            }
            bool binding = MarkupExtension.Parse(file.Text, attribute.ValueStart, attribute.ValueEnd) is { LocalName: "Binding" or "CompiledBinding" or "TemplateBinding" or "Bind" or "ReflectionBinding" };
            properties.Add(new StyleProperty(attribute.Name, file.Text[(attribute.ValueStart - 1)..attribute.End], binding, attribute.NameStart, attribute.End,
                file.Text[attribute.NameStart..attribute.End]));
        }
        if (properties.Count == 0)
        {
            return (null, $"'{element.Name}' sets no property a style can set");
        }
        bool byClass = file.Dialect is AvaloniaDialect;
        string suggested = byClass ? Camel(element.LocalName) + "Style" : element.LocalName + "Style";
        return (new StyleSource(element.Name, element.Start, properties, byClass ? suggested : Unique(file, suggested), byClass), null);
    }

    /// <summary>Why a style's name will not do: for Avalonia a class a selector can name, for WPF a key the file does not
    /// reach already; null when it will.</summary>
    public static string? StyleNameProblem(XamlFile file, StyleSource source, string name)
    {
        if (name.Trim().Length == 0)
        {
            return "Style name is empty";
        }
        return source.ByClass ? (ClassName().IsMatch(name) ? null : $"'{name}' is not a class a selector can name") : KeyProblem(file, name);
    }

    /// <summary>Extract Style: the chosen properties' setters in a new style where chosen, taken off the element, which
    /// names the style: WPF's keyed <c>Style</c> of its type through <c>Style="{StaticResource name}"</c> (based on the
    /// style it named before), or Avalonia's <c>Style</c> selecting its type with the class the element takes.</summary>
    public static IReadOnlyList<XamlFileChange> ExtractStyle(XamlFile file, StyleSource source, string name, IReadOnlyList<string> chosen, ResourcePlace place)
    {
        List<StyleProperty> moved = [.. source.Properties.Where(p => chosen.Contains(p.Name))];
        string? basedOn = null;
        Func<string, IReadOnlyList<TextEdit>> element = text =>
        {
            XmlTree tree = XmlTree.Parse(text);
            if (tree.Elements.FirstOrDefault(e => e.Start == source.ElementStart && e.Name == source.ElementName) is not { } found
                || moved.Any(p => p.End > text.Length || text[p.Start..p.End] != p.Original))
            {
                return [];
            }
            return [Restyled(text, found, moved, source.ByClass, name, out basedOn)];
        };
        Func<string, IReadOnlyList<TextEdit>> style = text => Insert(text, source.ByClass ? Container.Styles : Container.Resources, tree =>
        {
            List<TextEdit> declared = [];
            XmlElement? at = file.Tree.Elements.FirstOrDefault(e => e.Start == source.ElementStart);
            string target = source.ElementName;
            int colon = target.IndexOf(':');
            if (colon > 0 && place.Path != file.Path && file.UriOf(at, target[..colon]) is { } uri)
            {
                target = Qualified(Prefix(text, tree, uri, target[..colon], declared), target[(colon + 1)..]);
            }
            string x = source.ByClass ? string.Empty : Prefix(text, tree, XamlDialect.XamlUri, "x", declared);
            string opening = source.ByClass
                ? $"<Style Selector=\"{target.Replace(':', '|')}.{name}\">"
                : $"<Style {x}:Key=\"{name}\" TargetType=\"{target}\"{(basedOn is { } based ? $" BasedOn=\"{based}\"" : string.Empty)}>";
            StringBuilder made = new(opening);
            bool elsewhere = place.Path != file.Path;
            foreach (StyleProperty property in moved)
            {
                string named = elsewhere ? Requalified(property.Name, false) : property.Name;
                string value = elsewhere ? Requalified(property.Written, true) : property.Written;
                made.Append('\n').Append(Unit).Append($"<Setter Property=\"{named}\" Value={value} />");
            }
            made.Append("\n</Style>");
            return (made.ToString(), declared);

            // A name's or a value's prefixes as the receiving file declares their namespaces, declared there where it lacks them.
            string Requalified(string written, bool quoted)
            {
                string inner = quoted && written.Length >= 2 ? written[1..^1] : written;
                bool extension = inner.TrimStart().StartsWith('{') && !inner.TrimStart().StartsWith("{}", StringComparison.Ordinal);
                if (!extension && !QualifiedName().IsMatch(inner))
                {
                    return written;
                }
                return PrefixUse().Replace(written, use => use.Groups["prefix"].Value is var prefix && prefix != "xml" && file.UriOf(at, prefix) is { } uri
                    ? Qualified(Prefix(text, tree, uri, prefix, declared), string.Empty)
                    : use.Value);
            }
        });
        if (place.Path == file.Path)
        {
            return [new XamlFileChange(file.Path, text =>
            {
                IReadOnlyList<TextEdit> restyled = element(text);
                return restyled.Count == 0 ? [] : [.. restyled, .. style(text)];
            })];
        }
        // The element's edit first: it learns the style it was based on, which the new style takes.
        return [new XamlFileChange(file.Path, element), new XamlFileChange(place.Path, style)];
    }

    /// <summary>The element's start tag without the properties moved, naming the style: WPF's <c>Style</c> (the one it
    /// named before becoming the new one's <c>BasedOn</c>), Avalonia's class added to its <c>Classes</c>.</summary>
    private static TextEdit Restyled(string text, XmlElement element, IReadOnlyList<StyleProperty> moved, bool byClass, string name, out string? basedOn)
    {
        basedOn = null;
        IReadOnlyList<XmlAttribute> all = element.Attributes;
        XmlAttribute? classes = byClass ? element.Attribute("Classes") : null;
        XmlAttribute? styled = byClass ? null : element.Attribute("Style");
        if (styled is not null)
        {
            basedOn = styled.RawValue;
        }
        StringBuilder written = new();
        bool first = true;
        string? lastGap = null;
        for (int i = 0; i < all.Count; i++)
        {
            XmlAttribute attribute = all[i];
            if (moved.Any(p => p.Start == attribute.NameStart) || attribute == styled)
            {
                continue;
            }
            int gapStart = i > 0 ? all[i - 1].End : element.NameEnd;
            // The first attribute kept takes the first attribute's place, on the tag's line when that one was.
            string gap = first ? text[element.NameEnd..all[0].NameStart] : text[gapStart..attribute.NameStart];
            first = false;
            lastGap = gap;
            written.Append(gap);
            if (attribute == classes)
            {
                string value = attribute.RawValue.TrimEnd();
                written.Append(text[attribute.NameStart..attribute.ValueStart]).Append(value.Length > 0 ? value + " " + name : name).Append(text[attribute.ValueEnd..attribute.End]);
            }
            else
            {
                written.Append(text[attribute.NameStart..attribute.End]);
            }
        }
        if (classes is null)
        {
            string gap = lastGap is { } before && before.Contains('\n', StringComparison.Ordinal) ? before : " ";
            written.Append(gap).Append(byClass ? $"Classes=\"{name}\"" : $"Style=\"{{StaticResource {name}}}\"");
        }
        int end = all.Count > 0 ? all[^1].End : element.NameEnd;
        return new TextEdit(element.NameEnd, end - element.NameEnd, written.ToString());
    }

    /// <summary>Where Surround With wraps in a XAML file: the elements the selection takes in, whole, side by side under one
    /// parent (the element at the caret with nothing chosen), and how many; null where there are none (text, a comment,
    /// the root itself or a property element).</summary>
    public static (TextRange Range, int Count)? SurroundTarget(XmlTree tree, TextRange selection)
    {
        IReadOnlyList<XmlElement> level = tree.Roots;
        XmlElement? parent = null;
        int start = selection.Start;
        int end = Math.Max(selection.End, selection.Start);
        if (selection.Length == 0)
        {
            XmlElement? at = tree.ElementAt(start);
            while (at is { IsPropertyElement: true })
            {
                at = at.Parent;
            }
            return at is { Parent: not null } ? (TextRange.FromBounds(at.Start, at.End), 1) : null;
        }
        while (true)
        {
            XmlElement? holding = level.FirstOrDefault(e => e.StartTagEnd <= start && e.EndTagStart is { } close && end <= close);
            if (holding is null)
            {
                break;
            }
            parent = holding;
            level = holding.Children;
        }
        List<XmlElement> chosen = [.. level.Where(e => e.End > start && e.Start < end)];
        if (parent is null || chosen.Count == 0 || chosen.Any(e => e.IsPropertyElement))
        {
            return null;
        }
        return (TextRange.FromBounds(chosen[0].Start, chosen[^1].End), chosen.Count);
    }

    private const string Unit = "    ";

    private enum Container
    {
        Resources,
        Styles,
    }

    /// <summary>What a list calls the place a file's resources (or styles) go.</summary>
    private static string ContainerTitle(XmlElement root, bool styles) =>
        styles ? (root.LocalName == "Styles" ? "Styles" : $"{root.LocalName}.Styles")
        : root.LocalName == "ResourceDictionary" ? "ResourceDictionary" : $"{root.LocalName}.Resources";

    /// <summary>The edits that put an entry last in a file's resources (or styles), indented as the entries there are, its
    /// property element made first as its root's first child when there is none, with the namespaces the entry needs
    /// declared on the root.</summary>
    private static IReadOnlyList<TextEdit> Insert(string text, Container kind, Func<XmlTree, (string Entry, List<TextEdit> Declared)> make)
    {
        XmlTree tree = XmlTree.Parse(text);
        if (tree.Root is not { IsStartTagClosed: true } root)
        {
            return [];
        }
        (string entry, List<TextEdit> declared) = make(tree);
        string eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string unit = (Indentation.Detect(text, 4, markup: true) ?? new IndentOptions()).Unit;
        List<TextEdit> edits = [.. declared];
        if (ContainerOf(root, kind) is not { } container)
        {
            if (root.IsSelfClosing)
            {
                return [];
            }
            string property = $"{root.Name}.{(kind == Container.Styles ? "Styles" : "Resources")}";
            string inner = root.Children.Count > 0 ? LineIndent(text, root.Children[0].Start) : LineIndent(text, root.Start) + unit;
            edits.Add(new TextEdit(root.StartTagEnd, 0, $"{eol}{inner}<{property}>{eol}{inner}{unit}{Indented(entry, inner + unit, eol)}{eol}{inner}</{property}>"));
            return edits;
        }
        string at = LineIndent(text, container.Start);
        if (container.IsSelfClosing)
        {
            int last = container.Attributes.Count > 0 ? container.Attributes[^1].End : container.NameEnd;
            edits.Add(new TextEdit(last, container.End - last, $">{eol}{at}{unit}{Indented(entry, at + unit, eol)}{eol}{at}</{container.Name}>"));
        }
        else if (container.Children.Count > 0)
        {
            XmlElement after = container.Children[^1];
            string lined = LineIndent(text, after.Start);
            edits.Add(new TextEdit(after.End, 0, $"{eol}{lined}{Indented(entry, lined, eol)}"));
        }
        else if (container.EndTagStart is { } close)
        {
            edits.Add(new TextEdit(container.StartTagEnd, close - container.StartTagEnd, $"{eol}{at}{unit}{Indented(entry, at + unit, eol)}{eol}{at}"));
        }
        return edits;
    }

    /// <summary>The edits that put a written resource last in a file's resources, with namespaces already declared for it.</summary>
    internal static IReadOnlyList<TextEdit> AddResource(string text, string entry, List<TextEdit> declared) =>
        Insert(text, Container.Resources, _ => (entry, declared));

    /// <summary>The element a file's resources (or styles) are entries of: a dictionary's root, the root's resources
    /// property element or the dictionary it holds, the root's styles or a styles file's root; null when it has none.</summary>
    private static XmlElement? ContainerOf(XmlElement root, Container kind)
    {
        string own = kind == Container.Styles ? "Styles" : "ResourceDictionary";
        if (root.LocalName == own)
        {
            return root;
        }
        string property = $"{root.LocalName}.{(kind == Container.Styles ? "Styles" : "Resources")}";
        if (root.Children.FirstOrDefault(c => c.IsPropertyElement && c.LocalName == property) is not { } found)
        {
            return null;
        }
        return kind == Container.Resources && found.Children.FirstOrDefault(c => !c.IsPropertyElement && c.LocalName == "ResourceDictionary") is { } dictionary
            ? dictionary
            : found;
    }

    /// <summary>Where a file's resources (or styles) are written now, null when it has none.</summary>
    private static (int Start, int End)? ContainerRange(string text, Container kind) =>
        XmlTree.Parse(text).Root is { } root && ContainerOf(root, kind) is { } container ? (container.Start, container.End) : null;

    /// <summary>The prefix a namespace has on a file's root (empty for its default namespace); one declared there, as
    /// <paramref name="wanted"/> or a numbered one free, with its edit added to <paramref name="declared"/>, when it has none.</summary>
    internal static string Prefix(string text, XmlTree tree, string uri, string wanted, List<TextEdit> declared)
    {
        if (tree.Root is not { } root)
        {
            return wanted;
        }
        foreach (XmlAttribute attribute in root.Attributes)
        {
            if (attribute.Name == "xmlns" && attribute.Value == uri)
            {
                return string.Empty;
            }
            if (attribute.Prefix == "xmlns" && attribute.Value == uri)
            {
                return attribute.LocalName;
            }
        }
        if (declared.FirstOrDefault(d => d.Text.Contains($"=\"{uri}\"", StringComparison.Ordinal)) is { Text: { } made })
        {
            return Regex.Match(made, @"xmlns:(?<p>[\w.-]+)=").Groups["p"].Value;
        }
        string prefix = wanted;
        for (int n = 2; root.Attributes.Any(a => a.Name == "xmlns:" + prefix) || declared.Any(d => d.Text.Contains($"xmlns:{prefix}=", StringComparison.Ordinal)); n++)
        {
            prefix = wanted + n;
        }
        XmlAttribute? after = root.Attributes.LastOrDefault(a => a.Name == "xmlns" || a.Prefix == "xmlns") ?? root.Attributes.LastOrDefault();
        int at = after?.End ?? root.NameEnd;
        declared.Add(new TextEdit(at, 0, $"{AttributeGap(text, root, after)}xmlns:{prefix}=\"{uri}\""));
        return prefix;
    }

    /// <summary>What goes before an attribute written after <paramref name="after"/> (after the element's name when null):
    /// a line of its own indented as that one, where that one starts its own line; else a space.</summary>
    internal static string AttributeGap(string text, XmlElement element, XmlAttribute? after)
    {
        if (after is null)
        {
            return " ";
        }
        int index = element.Attributes.ToList().IndexOf(after);
        int before = index > 0 ? element.Attributes[index - 1].End : element.NameEnd;
        return text.AsSpan(before, after.NameStart - before).Contains('\n')
            ? (text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n") + LineIndent(text, after.NameStart)
            : " ";
    }

    private static string Qualified(string prefix, string name) => prefix.Length > 0 ? $"{prefix}:{name}" : name;

    /// <summary>The whitespace a line starts with, up to the offset (the indentation of what starts there).</summary>
    internal static string LineIndent(string text, int offset)
    {
        int start = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        if (offset == 0)
        {
            start = 0;
        }
        int at = start;
        while (at < offset && text[at] is ' ' or '\t')
        {
            at++;
        }
        return text[start..at];
    }

    /// <summary>A block's lines after its first indented by <paramref name="indent"/>, its breaks the file's.</summary>
    internal static string Indented(string block, string indent, string eol) =>
        block.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", eol + indent, StringComparison.Ordinal);

    /// <summary>The property an attribute's value is read as: a setter's <c>Value</c> its <c>Property</c>'s, else the
    /// property or attached property it sets.</summary>
    private static XamlProperty? PropertyOf(XamlFile file, XmlAttribute attribute)
    {
        if (attribute.Name == "Value" && XamlCompletion.IsSetterOrTrigger(attribute.Owner))
        {
            return XamlCompletion.SetterProperty(file, attribute.Owner);
        }
        XamlAttributeMeaning meaning = file.MeaningOf(attribute);
        return meaning.Kind is XamlAttributeKind.Property or XamlAttributeKind.Attached && meaning.Property is { IsReadOnly: false } property ? property : null;
    }

    /// <summary>How a value of a type is written as a resource: a solid brush of a colour, one of XAML's own types (x:'s
    /// in Avalonia, System's through <c>sys:</c> in WPF, whose MarkupCompile reads no XAML 2009), or the type's element
    /// with the value as its content; null for a type it cannot be.</summary>
    private static ResourceShape? Shape(XamlFile file, XmlAttribute attribute, ClrType? type)
    {
        char quote = attribute.Quote == '\'' ? '\'' : '"';
        if (type is null)
        {
            return null;
        }
        if (BrushTypes.Contains(type.FullName) && KnownColours.Parse(attribute.Value) is not null)
        {
            return new ResourceShape(file.Dialect.PresentationUri, "SolidColorBrush", string.Empty, true, quote);
        }
        string full = type.FullName == "System.Object" ? "System.String" : type.FullName;
        if (XamlFile.LanguageTypes.FirstOrDefault(t => t.Value == full) is { Key: { } language })
        {
            return file.Dialect is AvaloniaDialect
                ? new ResourceShape(XamlDialect.XamlUri, language, "x", false, quote)
                : new ResourceShape("clr-namespace:System;assembly=mscorlib", language, "sys", false, quote);
        }
        if (type.Kind is ClrTypeKind.Interface || type.IsAbstract || type.Arity > 0 || type.IsNested)
        {
            return null;
        }
        if (file.Types.NamespacesOf(file.Dialect.PresentationUri).Contains(type.Namespace, StringComparer.Ordinal))
        {
            return new ResourceShape(file.Dialect.PresentationUri, type.Name, string.Empty, false, quote);
        }
        string suggested = type.IsFromSource ? "local" : type.Namespace.Split('.')[^1].ToLowerInvariant();
        return new ResourceShape(file.Dialect.ClrNamespaceUri(type.Namespace, type.IsFromSource ? null : type.Origin.AssemblyName), type.Name, suggested, false, quote);
    }

    /// <summary>Every resource the file reaches, as definitions.</summary>
    private static IReadOnlyList<ResourceDefinition> Reached(XamlFile file)
    {
        XamlProject project = file.Project;
        XamlFileSummary own = project.Files.TryGetValue(file.Path, out XamlFileSummary? summary) ? summary : XamlFileSummary.Of(file.Path, file.Tree, project.ResolveSource);
        return [.. project.ResourceScopes(own).SelectMany(s => s.Resources)];
    }

    /// <summary>A key no resource the file reaches has: the name, else with a number after it.</summary>
    private static string Unique(XamlFile file, string name)
    {
        IReadOnlyList<ResourceDefinition> reached = Reached(file);
        string key = name;
        for (int n = 2; reached.Any(r => r.Key == key); n++)
        {
            key = name + n;
        }
        return key;
    }

    /// <summary>The object element an attribute sets a property of: its own, or for a setter's value its style's target.</summary>
    private static string ObjectName(XmlElement element) => XamlCompletion.IsSetterOrTrigger(element) ? string.Empty : element.LocalName;

    private static string Pascal(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    private static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    [GeneratedRegex(@"^[A-Za-z_][\w-]*$")]
    private static partial Regex ClassName();

    /// <summary>A prefix where XAML reads one: a name's start before a colon, a name after it.</summary>
    [GeneratedRegex(@"(?<![\w.:-])(?<prefix>[A-Za-z_][\w.-]*):(?=[A-Za-z_])")]
    private static partial Regex PrefixUse();

    /// <summary>A value that is one qualified name (<c>local:Sizes.Wide</c>).</summary>
    [GeneratedRegex(@"^\s*[A-Za-z_][\w.-]*:[A-Za-z_][\w.]*\s*$")]
    private static partial Regex QualifiedName();
}
