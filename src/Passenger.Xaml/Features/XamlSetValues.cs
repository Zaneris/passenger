using Passenger.Xaml.Dialects;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>What a value an element's XAML gives a property is, as the file alone says it.</summary>
public enum XamlValueKind
{
    /// <summary>A value written as it is.</summary>
    Local,

    /// <summary>A binding's ({Binding}, {CompiledBinding}, {ReflectionBinding}, {TemplateBinding}), which the running app reads.</summary>
    Binding,

    /// <summary>A resource's ({StaticResource}, {DynamicResource}).</summary>
    Resource,

    /// <summary>Another markup extension's ({x:Static}, {OnPlatform}).</summary>
    Extension,

    /// <summary>A property element's content (<c>&lt;Window.Styles&gt;</c>).</summary>
    Element,
}

/// <summary>A property an element's XAML sets: its name as written (an attached one as Owner.Name), its value as written (a
/// property element's by what it holds), what kind of value it is, and a binding's path or a resource's key.</summary>
public sealed record XamlSetValue(string Property, string Value, XamlValueKind Kind, string? Source);

/// <summary>What a XAML element sets itself, read from the text alone, in the order written: each attribute that sets a property
/// (XAML's language, the designer's and markup compatibility's left out) and each property element. A value a style, theme
/// or template gives, an inherited one and a default are the running app's to say.</summary>
public static class XamlSetValues
{
    private static readonly HashSet<string> Bindings = new(StringComparer.Ordinal) { "Binding", "CompiledBinding", "ReflectionBinding", "TemplateBinding" };

    private static readonly HashSet<string> Resources = new(StringComparer.Ordinal) { "StaticResource", "DynamicResource" };

    public static IReadOnlyList<XamlSetValue> Of(XmlTree tree, XmlElement element)
    {
        List<XamlSetValue> set = [];
        foreach (XmlAttribute attribute in element.Attributes)
        {
            if (!attribute.HasValue || attribute.Name == "xmlns" || attribute.Prefix == "xmlns"
                || (attribute.Prefix.Length > 0 && XamlFile.DeclaredUri(element, attribute.Prefix) is XamlDialect.XamlUri or XamlDialect.DesignerUri or XamlDialect.CompatibilityUri))
            {
                continue;
            }
            MarkupExtension? extension = MarkupExtension.Parse(tree.Text, attribute.ValueStart, attribute.ValueEnd);
            (XamlValueKind kind, string? source) = extension switch
            {
                null => (XamlValueKind.Local, null),
                { Prefix.Length: 0 } when Bindings.Contains(extension.LocalName) => (XamlValueKind.Binding, (extension.Argument("Path") ?? extension.Argument(null))?.Value),
                { Prefix.Length: 0 } when Resources.Contains(extension.LocalName) => (XamlValueKind.Resource, (extension.Argument("ResourceKey") ?? extension.Argument(null))?.Value),
                _ => (XamlValueKind.Extension, extension.Name),
            };
            set.Add(new XamlSetValue(attribute.Name, attribute.Value, kind, source));
        }
        string owner = element.LocalName + ".";
        foreach (XmlElement held in element.Children.Where(c => c.IsPropertyElement))
        {
            string property = held.LocalName.StartsWith(owner, StringComparison.Ordinal) ? held.LocalName[owner.Length..] : held.LocalName;
            string value = held.Children switch
            {
                [] => "(empty)",
                [XmlElement only] => $"<{only.Name}>",
                [XmlElement first, ..] => $"<{first.Name}> and {held.Children.Count - 1} more",
            };
            set.Add(new XamlSetValue(property, value, XamlValueKind.Element, null));
        }
        return set;
    }
}
