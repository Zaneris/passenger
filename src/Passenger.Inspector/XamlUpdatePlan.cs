using System.Xml;
using System.Xml.Linq;

namespace Passenger.Inspector;

/// <summary>A source edit expressed against the original build's element positions, which the live controls keep after
/// later edits move their source lines. Shape and markup changes are refused before any property is changed.</summary>
internal sealed record XamlPropertyUpdate(int Line, int Column, string? Class, bool Root, string Type, string Property, string Value, int EditedLine, int EditedColumn);

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
                if (old.Name != next.Name || anchors[i].Name != old.Name || Depth(old) != Depth(next)
                    || !old.Attributes().Select(a => a.Name).ToHashSet().SetEquals(next.Attributes().Select(a => a.Name)))
                {
                    return Refused(source, next, "Changing XAML structure or adding/removing attributes needs a restart", true);
                }
                if (!old.Nodes().OfType<XText>().Select(t => t.Value.Trim()).Where(t => t.Length > 0)
                    .SequenceEqual(next.Nodes().OfType<XText>().Select(t => t.Value.Trim()).Where(t => t.Length > 0)))
                {
                    return Refused(source, next, "Changing XAML text content needs a restart; a literal property attribute can be updated", true);
                }
                foreach (XAttribute attribute in next.Attributes())
                {
                    XAttribute was = old.Attribute(attribute.Name)!;
                    if (was.Value == attribute.Value)
                    {
                        continue;
                    }
                    if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace != XNamespace.None
                        || old.AncestorsAndSelf().Any(e => e.Name.LocalName.Contains('.') || e.Name.LocalName.EndsWith("Template", StringComparison.Ordinal)
                            || e.Name.LocalName is "Style" or "Setter" or "ResourceDictionary"))
                    {
                        return Refused(source, attribute, "Changing XAML directives, resources, styles or templates needs a restart", true);
                    }
                    if (Markup(was.Value) || Markup(attribute.Value))
                    {
                        return Refused(source, attribute, "Changing a XAML binding or markup extension needs a restart", true);
                    }
                    IXmlLineInfo anchor = anchors[i];
                    IXmlLineInfo edited = attribute;
                    string? rootClass = built.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value;
                    changes.Add(new XamlPropertyUpdate(anchor.LineNumber, anchor.LinePosition, rootClass, i == 0, next.Name.LocalName,
                        attribute.Name.LocalName, attribute.Value.StartsWith("{}", StringComparison.Ordinal) ? attribute.Value[2..] : attribute.Value,
                        edited.LineNumber, edited.LinePosition));
                }
            }
            return new XamlUpdatePlan(changes, []);
        }
        catch (XmlException error)
        {
            return new XamlUpdatePlan([], [new XamlUpdateProblem(source.File, error.LineNumber, error.LinePosition, error.Message, false)]);
        }
    }

    private static bool Markup(string value) => value.StartsWith('{') && !value.StartsWith("{}", StringComparison.Ordinal);

    private static int Depth(XElement element) => element.Ancestors().Count();

    private static XamlUpdatePlan Refused(XamlSourceUpdate source, XObject at, string message, bool restart)
    {
        IXmlLineInfo line = at;
        return new XamlUpdatePlan([], [new XamlUpdateProblem(source.File, line.LineNumber, line.LinePosition, message, restart)]);
    }
}
