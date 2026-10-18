using System.Xml;
using System.Xml.Linq;

namespace Passenger.Inspector;

/// <summary>A source edit expressed against the original build's element positions, which the live controls keep after
/// later edits move their source lines. Shape and markup changes are refused before any property is changed.</summary>
internal sealed record XamlPropertyUpdate(int Line, int Column, string? Class, bool Root, string Type, string Property, string Value, int EditedLine, int EditedColumn, bool Added);

internal sealed record XamlUpdatePlan(IReadOnlyList<XamlPropertyUpdate> Properties, IReadOnlyList<XamlUpdateProblem> Problems)
{
    public static XamlUpdatePlan Create(XamlSourceUpdate source, string original)
    {
        try
        {
            XElement built = XElement.Parse(original, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
            XElement before = XElement.Parse(source.Before, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
            XElement after = XElement.Parse(source.After, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
            // Property leaves describe values, not runtime objects. Inserting one must not shift the compiled source
            // anchor of any object after it, either in this update or in a later update against the accepted text.
            List<XElement> anchors = [.. built.DescendantsAndSelf().Where(e => !PropertyLeaf(e))];
            List<XElement> previous = [.. before.DescendantsAndSelf().Where(e => !PropertyLeaf(e))];
            List<XElement> current = [.. after.DescendantsAndSelf().Where(e => !PropertyLeaf(e))];
            if (anchors.Count != previous.Count || previous.Count != current.Count)
            {
                return Refused(source, after, "Adding or removing XAML elements needs a restart", true);
            }
            List<XamlPropertyUpdate> changes = [];
            for (int i = 0; i < current.Count; i++)
            {
                XElement old = previous[i];
                XElement next = current[i];
                if (old.Name != next.Name || anchors[i].Name != old.Name || Depth(old) != Depth(next) || Depth(anchors[i]) != Depth(old))
                {
                    return Refused(source, next, "Changing XAML structure needs a restart", true);
                }
                if (old.Attributes().Any(a => next.Attribute(a.Name) is null))
                {
                    return Refused(source, next, "Removing XAML attributes needs a restart to restore their original value sources", true);
                }
                List<XElement> properties = [.. next.Elements().Where(PropertyLeaf)];
                if (properties.GroupBy(p => p.Name).Any(g => g.Count() > 1))
                {
                    return Refused(source, next, "The same XAML property element cannot be specified more than once", false);
                }
                List<XElement> oldProperties = [.. old.Elements().Where(PropertyLeaf)];
                HashSet<XName> oldNames = [.. oldProperties.Select(p => p.Name)];
                if (oldProperties.Any(p => !properties.Any(n => n.Name == p.Name)))
                {
                    return Refused(source, next, properties.Count >= oldProperties.Count
                        ? "Changing XAML property-element structure needs a restart"
                        : "Removing XAML property elements needs a restart to restore their original value sources", true);
                }
                if (!old.Elements().Select(e => e.Name).SequenceEqual(next.Elements().Where(e => !PropertyLeaf(e) || oldNames.Contains(e.Name)).Select(e => e.Name)))
                {
                    return Refused(source, next, "Changing XAML structure needs a restart", true);
                }
                foreach (XElement property in properties)
                {
                    string name = property.Name.LocalName;
                    int dot = name.IndexOf('.');
                    if (dot <= 0 || dot != name.LastIndexOf('.') || dot == name.Length - 1)
                    {
                        return Refused(source, property, "Changing XAML property-element structure needs a restart", true);
                    }
                    XAttribute? duplicate = next.Attribute(name) ?? (name[..dot] == next.Name.LocalName ? next.Attribute(name[(dot + 1)..]) : null);
                    if (duplicate is not null)
                    {
                        return Refused(source, duplicate, "The same XAML property cannot be specified by both an attribute and a property element", false);
                    }
                    XElement? was = oldProperties.FirstOrDefault(p => p.Name == property.Name);
                    if (XNode.DeepEquals(was, property))
                    {
                        continue;
                    }
                    if (was is not null && !was.Attributes().Select(a => (a.Name, a.Value)).SequenceEqual(property.Attributes().Select(a => (a.Name, a.Value))))
                    {
                        return Refused(source, property, "Changing XAML property-element directives needs a restart", true);
                    }
                    if (was is null && (old.Elements().Any(e => !PropertyLeaf(e)) || old.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))))
                    {
                        return Refused(source, property, "Adding a property element to an object with implicit text or child content needs a restart", true);
                    }
                    if (LiteralProperty(source, anchors[i], was, property, built, changes) is { } refused)
                    {
                        return refused;
                    }
                }
                if (old.Name.LocalName.Contains('.') && old.Value != next.Value
                    || !Content(old).SequenceEqual(Content(next)))
                {
                    return Refused(source, next, "Changing XAML text content or nested property objects needs a restart", true);
                }
                foreach (XAttribute attribute in next.Attributes())
                {
                    XAttribute? was = old.Attribute(attribute.Name);
                    if (was?.Value == attribute.Value)
                    {
                        continue;
                    }
                    if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace != XNamespace.None
                        || attribute.Name.LocalName is "Name" or "Uid" || was is null && attribute.Name.LocalName == "DataContext"
                        || old.AncestorsAndSelf().Any(e => e.Name.LocalName.Contains('.') || e.Name.LocalName.EndsWith("Template", StringComparison.Ordinal)
                            || e.Name.LocalName is "Style" or "Setter" or "ResourceDictionary"))
                    {
                        return Refused(source, attribute, "Changing XAML directives, resources, styles or templates needs a restart", true);
                    }
                    if (was is not null && Markup(was.Value) || Markup(attribute.Value))
                    {
                        return Refused(source, attribute, "Changing a XAML binding or markup extension needs a restart", true);
                    }
                    IXmlLineInfo anchor = anchors[i];
                    IXmlLineInfo edited = attribute;
                    string? rootClass = built.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value;
                    changes.Add(new XamlPropertyUpdate(anchor.LineNumber, anchor.LinePosition, rootClass, i == 0, next.Name.LocalName,
                        attribute.Name.LocalName, attribute.Value.StartsWith("{}", StringComparison.Ordinal) ? attribute.Value[2..] : attribute.Value,
                        edited.LineNumber, edited.LinePosition, anchors[i].Attribute(attribute.Name) is null));
                }
            }
            return new XamlUpdatePlan(changes, []);
        }
        catch (XmlException error)
        {
            return new XamlUpdatePlan([], [new XamlUpdateProblem(source.File, error.LineNumber, error.LinePosition, error.Message, false)]);
        }
    }

    /// <summary>A property's literal body belongs to its parent object's compiled source position. No property object is
    /// created, no content collection is replaced, and all other properties keep their value sources.</summary>
    private static XamlUpdatePlan? LiteralProperty(XamlSourceUpdate source, XElement owner, XElement? before, XElement after,
        XElement built, List<XamlPropertyUpdate> changes)
    {
        string name = after.Name.LocalName;
        int dot = name.IndexOf('.');
        if (dot <= 0 || dot != name.LastIndexOf('.') || dot == name.Length - 1 || after.Parent is not { } parent
            || parent.Name.Namespace != after.Name.Namespace
            || after.Ancestors().Any(e => e.Name.LocalName.Contains('.') || e.Name.LocalName.EndsWith("Template", StringComparison.Ordinal)
                || e.Name.LocalName is "Style" or "Setter" or "ResourceDictionary"))
        {
            return Refused(source, after, "Changing XAML text content or nested property objects needs a restart; a simple literal property element can be updated", true);
        }
        foreach (XElement value in before is null ? new[] { after } : new[] { before, after })
        {
            if (value.HasElements || value.Attributes().Any(a => !a.IsNamespaceDeclaration)
                || value.Nodes().Count() > 1 || value.Nodes().Any(n => n is not XText || n is XCData)
                || value.Value != value.Value.Trim() || value.Value.IndexOfAny(['\r', '\n']) >= 0)
            {
                return Refused(source, after, "Only single-line literal property-element values without nested objects or whitespace directives can be updated; restart to load this edit", true);
            }
            if (value.Value.StartsWith('{'))
            {
                return Refused(source, after, "Changing a property-element binding, markup extension or markup escape needs a restart", true);
            }
        }
        string property = name[..dot] == parent.Name.LocalName ? name[(dot + 1)..] : name;
        bool added = owner.Element(after.Name) is null;
        string unqualified = property[(property.LastIndexOf('.') + 1)..];
        if (unqualified is "Name" or "Uid" || added && unqualified == "DataContext")
        {
            return Refused(source, after, "Changing XAML names or adding a data context needs a restart to preserve namescopes and bindings", true);
        }
        IXmlLineInfo at = owner;
        IXmlLineInfo edited = after;
        string? rootClass = built.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value;
        changes.Add(new XamlPropertyUpdate(at.LineNumber, at.LinePosition, rootClass, ReferenceEquals(owner, built), parent.Name.LocalName,
            property, after.Value, edited.LineNumber, edited.LinePosition, added));
        return null;
    }

    private static bool Markup(string value) => value.StartsWith('{') && !value.StartsWith("{}", StringComparison.Ordinal);

    private static IEnumerable<string> Content(XElement element)
    {
        bool preserve = element.AncestorsAndSelf().Select(e => e.Attribute(XNamespace.Xml + "space")).FirstOrDefault(a => a is not null)?.Value == "preserve";
        return element.Nodes().OfType<XText>().Select(t => t.Value).Where(text => preserve || !string.IsNullOrWhiteSpace(text));
    }

    private static bool PropertyLeaf(XElement element) => element.Name.LocalName.Contains('.') && !element.HasElements;

    private static int Depth(XElement element) => element.Ancestors().Count();

    private static XamlUpdatePlan Refused(XamlSourceUpdate source, XObject at, string message, bool restart)
    {
        IXmlLineInfo line = at;
        return new XamlUpdatePlan([], [new XamlUpdateProblem(source.File, line.LineNumber, line.LinePosition, message, restart)]);
    }
}
