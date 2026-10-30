using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Syntax;

namespace Passenger.App.LiveTree;

/// <summary>A binding written in an .axaml file's attribute: the element it is on (where its name starts, from zero), the class
/// a root element names (its x:Class), the property it sets (its attribute's name), where its markup extension is and the line
/// it ends on, after which its value shows.</summary>
public sealed record XamlBinding(int Line, int Column, string? Class, string Property, TextRange Range, int EndLine);

/// <summary>The bindings an .axaml file writes as markup extensions ({Binding}, {CompiledBinding}, {ReflectionBinding},
/// {TemplateBinding}), read from its text as typed: what a debugged app's values are asked for and shown after.</summary>
public static class XamlBindings
{
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "Binding", "CompiledBinding", "ReflectionBinding", "TemplateBinding" };

    /// <summary>Each binding of the text, in order.</summary>
    public static IReadOnlyList<XamlBinding> In(TextSnapshot text)
    {
        string source = text.ToString();
        XmlTree tree = XmlTree.Parse(source);
        List<XamlBinding> found = [];
        foreach (XmlElement element in tree.Elements)
        {
            // A style's setter binds on each control the style sets, not on an element of the file's own.
            if (element.IsPropertyElement || element.LocalName == "Setter")
            {
                continue;
            }
            string? root = element == tree.Root ? XamlFile.ClassAttribute(tree)?.Value : null;
            int line = text.GetLineFromOffset(element.NameStart);
            int column = element.NameStart - text.GetLineStart(line);
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (!attribute.HasValue || attribute.Name == "xmlns" || attribute.Prefix == "xmlns" || IsLanguageOrDesign(element, attribute)
                    || MarkupExtension.Parse(source, attribute.ValueStart, attribute.ValueEnd) is not { Prefix.Length: 0 } extension
                    || !Kinds.Contains(extension.LocalName))
                {
                    continue;
                }
                TextRange range = TextRange.FromBounds(extension.Start, extension.End);
                found.Add(new XamlBinding(line, column, root, attribute.Name, range, text.GetLineFromOffset(Math.Max(extension.Start, extension.End - 1))));
            }
        }
        return found;
    }

    /// <summary>Whether an attribute is one of XAML's language, the designer's or markup compatibility's, by the namespace
    /// its prefix is declared for, whatever the prefix: none sets a property of the running app.</summary>
    private static bool IsLanguageOrDesign(XmlElement element, XmlAttribute attribute) =>
        attribute.Prefix.Length > 0
        && XamlFile.DeclaredUri(element, attribute.Prefix) is XamlDialect.XamlUri or XamlDialect.DesignerUri or XamlDialect.CompatibilityUri;
}
