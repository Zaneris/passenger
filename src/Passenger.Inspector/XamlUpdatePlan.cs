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
            XElement built = XElement.Parse(original, LoadOptions.SetLineInfo);
            XElement before = XElement.Parse(source.Before, LoadOptions.SetLineInfo);
            XElement after = XElement.Parse(source.After, LoadOptions.SetLineInfo);
            List<XElement> anchors = [.. built.DescendantsAndSelf()];
            List<XElement> previous = [.. before.DescendantsAndSelf()];
            List<XElement> current = [.. after.DescendantsAndSelf()];
            if (anchors.Count != previous.Count || previous.Count != current.Count)
            {
                return Refused(source, after, "Adding or removing XAML elements needs a restart", true);
            }
            List<XamlPropertyUpdate> changes = [];
            for (int i = 0; i < current.Count; i++)
            {
                XElement old = previous[i];
                XElement next = current[i];
                if (old.Name != next.Name || anchors[i].Name != old.Name || Depth(old) != Depth(next))
                {
                    return Refused(source, next, "Changing XAML structure needs a restart", true);
                }
                if (old.Attributes().Any(a => next.Attribute(a.Name) is null))
                {
                    return Refused(source, next, "Removing XAML attributes needs a restart to restore their original value sources", true);
                }
                foreach (XElement property in next.Elements().Where(e => e.Name.LocalName.Contains('.') && e.Name.Namespace == next.Name.Namespace))
                {
                    string name = property.Name.LocalName;
                    int dot = name.IndexOf('.');
                    if (dot <= 0 || dot != name.LastIndexOf('.') || dot == name.Length - 1)
                    {
                        continue;
                    }
                    XAttribute? duplicate = next.Attribute(name) ?? (name[..dot] == next.Name.LocalName ? next.Attribute(name[(dot + 1)..]) : null);
                    if (duplicate is not null)
                    {
                        return Refused(source, duplicate, "The same XAML property cannot be specified by both an attribute and a property element", false);
                    }
                }
                if (old.Name.LocalName.Contains('.') && old.Value != next.Value
                    || !old.Nodes().OfType<XText>().Select(t => t.Value.Trim()).Where(t => t.Length > 0)
                    .SequenceEqual(next.Nodes().OfType<XText>().Select(t => t.Value.Trim()).Where(t => t.Length > 0)))
                {
                    if (LiteralProperty(source, anchors[i], old, next, built, changes) is { } refused)
                    {
                        return refused;
                    }
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
    private static XamlUpdatePlan? LiteralProperty(XamlSourceUpdate source, XElement anchor, XElement before, XElement after,
        XElement built, List<XamlPropertyUpdate> changes)
    {
        string name = after.Name.LocalName;
        int dot = name.IndexOf('.');
        if (dot <= 0 || dot != name.LastIndexOf('.') || dot == name.Length - 1 || before.Parent is not { } parent
            || parent.Name.Namespace != after.Name.Namespace || anchor.Parent is not { } owner
            || before.Ancestors().Any(e => e.Name.LocalName.Contains('.') || e.Name.LocalName.EndsWith("Template", StringComparison.Ordinal)
                || e.Name.LocalName is "Style" or "Setter" or "ResourceDictionary"))
        {
            return Refused(source, after, "Changing XAML text content or nested property objects needs a restart; a simple literal property element can be updated", true);
        }
        foreach (XElement value in new[] { before, after })
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
        IXmlLineInfo at = owner;
        IXmlLineInfo edited = after;
        string? rootClass = built.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value;
        changes.Add(new XamlPropertyUpdate(at.LineNumber, at.LinePosition, rootClass, ReferenceEquals(owner, built), parent.Name.LocalName,
            property, after.Value, edited.LineNumber, edited.LinePosition, false));
        return null;
    }

    private static bool Markup(string value) => value.StartsWith('{') && !value.StartsWith("{}", StringComparison.Ordinal);

    private static int Depth(XElement element) => element.Ancestors().Count();

    private static XamlUpdatePlan Refused(XamlSourceUpdate source, XObject at, string message, bool restart)
    {
        IXmlLineInfo line = at;
        return new XamlUpdatePlan([], [new XamlUpdateProblem(source.File, line.LineNumber, line.LinePosition, message, restart)]);
    }
}
