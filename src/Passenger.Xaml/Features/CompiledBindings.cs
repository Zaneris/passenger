using Passenger.Text;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>A compiled binding with no type to start its path from, which Avalonia's compiler refuses (AVLN2100; AVLN2101
/// for a DataContext's): its path starts at the data context, and nothing above it gives one a type, neither an
/// <c>x:DataType</c>, a data template, a <c>DataContext</c> set by a markup extension, nor the items an items control is
/// given, where its property takes its type from them. Its fixes give the file's root an <c>x:DataType</c> of one of the
/// project's view models, or make it a <c>ReflectionBinding</c>, resolved as the app runs.</summary>
public static class CompiledBindings
{
    public const string NoDataType = "No x:DataType gives this compiled binding the type it binds to";

    private const string DataTemplate = "Avalonia.Controls.Templates.IDataTemplate";

    /// <summary>The most view models offered as fixes of one binding.</summary>
    private const int MostOffered = 10;

    /// <summary>Whether a path (null when the binding names none) starts at the data context: none, ".", or a property or
    /// indexer after any '!'; not an element's name (#), a relative source ($), or what parentheses start (an attached
    /// property, a cast).</summary>
    public static bool StartsAtDataContext(string? path)
    {
        string text = path?.Trim() ?? string.Empty;
        if (text.Length == 0 || text == ".")
        {
            return true;
        }
        text = text.TrimStart('!').TrimStart();
        return text.Length > 0 && (char.IsLetter(text[0]) || text[0] is '_' or '[');
    }

    /// <summary>Whether something gives a type to the data context a binding in an attribute starts from; a binding
    /// nested in another extension's argument (<paramref name="nested"/>) takes none from its property's items.</summary>
    public static bool HasDataType(XamlFile file, XmlAttribute attribute, bool nested)
    {
        XmlElement element = attribute.Owner;
        if (!nested && file.MeaningOf(attribute).Property?.ClrProperty?.Attributes.FirstOrDefault(a => a.ShortName == "InheritDataTypeFromItems") is { } inherit
            && ItemsGiven(file, element, inherit))
        {
            return true;
        }
        // An element's own DataContext binds against the data context above it.
        for (XmlElement? at = attribute.Name == "DataContext" ? element.Parent : element; at is not null; at = at.Parent)
        {
            if (at.IsPropertyElement)
            {
                continue;
            }
            if (file.Directive(at, "DataType") is not null || file.TypeOf(at) is not { } type || file.Types.IsA(type, DataTemplate))
            {
                return true;
            }
            if (at.Attribute("DataContext") is { } context && MarkupExtension.Parse(file.Text, context.ValueStart, context.ValueEnd) is { Name.Length: > 0 } set
                && !IsLanguage(file, at, set))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether the items an <c>[InheritDataTypeFromItems]</c> property takes its type from are given otherwise
    /// than by a markup extension of the presentation's (a binding, a resource), which says no element type: as an object,
    /// as text, or by one of XAML's own (<c>x:Static</c>).</summary>
    private static bool ItemsGiven(XamlFile file, XmlElement element, ClrAttribute inherit)
    {
        if (inherit.StringArgument(0) is not { Length: > 0 } property)
        {
            return false;
        }
        string? ancestor = inherit.Named.FirstOrDefault(n => n.Key == "AncestorType").Value as string;
        XmlElement? items = element;
        while (ancestor is not null && items is not null && !(file.TypeOf(items) is { } type && file.Types.IsA(type, ancestor)))
        {
            items = items.Parent;
        }
        if (items is null)
        {
            return false;
        }
        if (items.Attribute(property) is { } given)
        {
            return MarkupExtension.Parse(file.Text, given.ValueStart, given.ValueEnd) is not { Name.Length: > 0 } extension || IsLanguage(file, items, extension);
        }
        return items.Children.Any(c => c.IsPropertyElement && c.LocalName.EndsWith("." + property, StringComparison.Ordinal));
    }

    private static bool IsLanguage(XamlFile file, XmlElement element, MarkupExtension extension) =>
        extension.Prefix.Length > 0 && file.KindOf(file.UriOf(element, extension.Prefix)) == XamlNamespaceKind.Language;

    /// <summary>A binding's fixes: <c>x:DataType</c> on the file's root (but for the root's own DataContext, which binds
    /// against what is above the file) of each view model of the project's that has the path's first step, every one where
    /// none has it; then <c>ReflectionBinding</c> in its place.</summary>
    public static IReadOnlyList<XamlFix> Fixes(XamlFile file, XmlAttribute attribute, MarkupExtension binding, string? path)
    {
        List<XamlFix> fixes = [];
        if (file.Tree.Root is { } root && !(attribute.Owner == root && attribute.Name == "DataContext"))
        {
            List<ClrType> models = [.. ViewModels(file)];
            string? step = FirstStep(path);
            List<ClrType> having = step is null ? [] : [.. models.Where(t => file.Types.MembersOf(t).Any(m => m.Kind is MemberKind.Property or MemberKind.Method && !m.IsStatic && m.Name == step))];
            List<ClrType> offered = [.. (having.Count > 0 ? having : models).Take(MostOffered)];
            foreach (ClrType type in offered)
            {
                string named = offered.Count(t => t.Name == type.Name) > 1 ? type.FullName : type.Name;
                fixes.Add(new XamlFix($"Add x:DataType '{named}' to {root.LocalName}", file.Path, text => AddDataType(text, file.Dialect, type))
                {
                    Detail = type.Namespace,
                });
            }
        }
        int name = binding.NameStart + (binding.Prefix.Length > 0 ? binding.Prefix.Length + 1 : 0);
        string local = binding.LocalName;
        fixes.Add(new XamlFix("Use ReflectionBinding", file.Path, text => name + local.Length <= text.Length && text.AsSpan(name, local.Length).SequenceEqual(local)
            ? [new TextEdit(name, local.Length, "ReflectionBinding")]
            : [])
        {
            Detail = "Bound as the app runs",
        });
        return fixes;
    }

    /// <summary>The project's view models: its classes named for one, in a namespace, by name.</summary>
    private static IEnumerable<ClrType> ViewModels(XamlFile file) =>
        file.Types.All.Where(t => t.IsFromSource && t.Name.EndsWith("ViewModel", StringComparison.Ordinal) && t.Kind == ClrTypeKind.Class && !t.IsNested && t.Arity == 0
            && t.Namespace.Length > 0 && !t.IsStatic).OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Namespace, StringComparer.Ordinal);

    /// <summary>The property a path's first step names; null for none, or an indexer.</summary>
    private static string? FirstStep(string? path)
    {
        string text = (path ?? string.Empty).Trim().TrimStart('!').TrimStart();
        int end = text.IndexOfAny(['.', '[', '^', ' ']);
        string step = end < 0 ? text : text[..end];
        return step.Length > 0 && (char.IsLetter(step[0]) || step[0] == '_') ? step : null;
    }

    /// <summary>The edits that give a file's root an <c>x:DataType</c> of a type, after its <c>x:Class</c> or else its last
    /// attribute, declaring the type's namespace (and XAML's) where the root lacks them; none where it has one.</summary>
    private static IReadOnlyList<TextEdit> AddDataType(string text, XamlDialect dialect, ClrType type)
    {
        XmlTree tree = XmlTree.Parse(text);
        if (tree.Root is not { } root)
        {
            return [];
        }
        List<TextEdit> declared = [];
        string x = XamlRefactorings.Prefix(text, tree, XamlDialect.XamlUri, "x", declared);
        if (root.Attributes.Any(a => a.Prefix == x && a.LocalName == "DataType"))
        {
            return [];
        }
        string wanted = type.Namespace.EndsWith("ViewModels", StringComparison.Ordinal) ? "vm" : "local";
        string prefix = XamlRefactorings.Prefix(text, tree, dialect.ClrNamespaceUri(type.Namespace, null), wanted, declared);
        XmlAttribute? after = root.Attributes.FirstOrDefault(a => a.Prefix == x && a.LocalName == "Class") ?? root.Attributes.LastOrDefault();
        int at = after?.End ?? root.NameEnd;
        string added = $"{XamlRefactorings.AttributeGap(text, root, after)}{x}:DataType=\"{(prefix.Length > 0 ? prefix + ":" : string.Empty)}{type.Name}\"";
        // A declaration made at the same place goes first, as one edit, so the two land in that order.
        int same = declared.FindIndex(d => d.Start == at && d.Length == 0);
        if (same >= 0)
        {
            declared[same] = declared[same] with { Text = declared[same].Text + added };
            return declared;
        }
        return [.. declared, new TextEdit(at, 0, added)];
    }
}
