using Passenger.Text;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>A compiled binding with no type to start its path from, which Avalonia's compiler refuses: in an attribute or
/// as a <c>&lt;Binding&gt;</c> element, its path starts at the data context, and nothing above it gives that a type
/// (AVLN2100; AVLN2101 for a DataContext's), or what does is one the compiler has no type for, a reflection binding or a
/// data template whose items say none (AVLN2000). Its fixes give an <c>x:DataType</c> of one of the project's view models
/// to the element where the type is missing, the file's root where none is, or make it a <c>ReflectionBinding</c>,
/// resolved as the app runs. A <c>&lt;CompiledBinding&gt;</c> element's path given as text is refused too (AVLN3000).</summary>
public static class CompiledBindings
{
    public const string NoDataType = "No x:DataType gives this compiled binding the type it binds to";

    private const string CompiledBindingExtension = "Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindingExtension";

    /// <summary>The most view models offered as fixes of one binding.</summary>
    private const int MostOffered = 10;

    /// <summary>Whether a path (null when the binding names none) starts at the data context: none, ".", or a property or
    /// indexer after any '!', or one cast (<c>((vm:Shop)Item).Name</c>); not an element's name (#), a relative source ($),
    /// or what other parentheses start (an attached property, the data context cast).</summary>
    public static bool StartsAtDataContext(string? path)
    {
        string text = path?.Trim() ?? string.Empty;
        if (text.Length == 0 || text == ".")
        {
            return true;
        }
        text = Uncast(text.TrimStart('!').TrimStart());
        return text.Length > 0 && (char.IsLetter(text[0]) || text[0] is '_' or '[');
    }

    /// <summary>A path whose first step is cast (<c>((vm:Shop)Item).Name</c>) from that step on (<c>Item).Name</c>); any
    /// other as it is.</summary>
    private static string Uncast(string path)
    {
        int close = path.StartsWith("((", StringComparison.Ordinal) ? path.IndexOf(')') : -1;
        return close > 0 ? path[(close + 1)..] : path;
    }

    /// <summary>Whether the compiler refuses a binding's path (null when it names none) for want of a type to start it
    /// from: any path from the data context where nothing gives that a type, and one with a step from it where the data
    /// context is one the compiler has no type for (not <c>{Binding}</c>, ".", nor the data context cast).</summary>
    public static bool Refused(CompiledContext context, string? path) => context switch
    {
        CompiledContext.None => StartsAtDataContext(path),
        CompiledContext.Untyped => StartsAtDataContext(path) && path?.Trim() is { Length: > 0 } and not ".",
        _ => false,
    };

    /// <summary>What the compiler takes the data context a binding in an attribute starts from to be, and the element that
    /// settles it (see <see cref="XamlFile.CompiledContextOf"/>); a binding nested in another extension's argument
    /// (<paramref name="nested"/>) takes none from its property's items.</summary>
    public static (CompiledContext Context, XmlElement? At) ContextOf(XamlFile file, XmlAttribute attribute, bool nested) =>
        file.CompiledContextOf(attribute.Owner, nested ? null : file.MeaningOf(attribute).Property, attribute.Name == "DataContext");

    /// <summary>What the compiler takes the data context a <c>&lt;Binding&gt;</c> element starts from to be: as the value
    /// of the property element it is in, else as one of a multi-binding's, which takes none from items.</summary>
    public static (CompiledContext Context, XmlElement? At) ContextOf(XamlFile file, XmlElement binding)
    {
        if (binding.Parent is { IsPropertyElement: true } holder && holder.Parent is { } owner)
        {
            return file.CompiledContextOf(owner, file.PropertyElement(holder).Property, IsDataContextOf(holder));
        }
        return binding.Parent is { } parent ? file.CompiledContextOf(parent, property: null, dataContext: false) : (CompiledContext.Typed, null);
    }

    /// <summary>Whether a property element sets its element's own <c>DataContext</c>.</summary>
    public static bool IsDataContextOf(XmlElement holder) => holder.IsPropertyElement && holder.LocalName.EndsWith(".DataContext", StringComparison.Ordinal);

    /// <summary>A binding's fixes: <c>x:DataType</c> of each view model of the project's that has the path's first step
    /// (where none has it, each other class that has it, else every view model) on the element that settles its data
    /// context (<paramref name="at"/>), else the file's root but for the root's own DataContext, which binds against what
    /// is above the file; then <c>ReflectionBinding</c> in its place.</summary>
    public static IReadOnlyList<XamlFix> Fixes(XamlFile file, XmlAttribute attribute, MarkupExtension binding, string? path, XmlElement? at) =>
        Fixes(file, at ?? (attribute.Owner == file.Tree.Root && attribute.Name == "DataContext" ? null : file.Tree.Root), path,
            [(binding.NameStart + (binding.Prefix.Length > 0 ? binding.Prefix.Length + 1 : 0), binding.LocalName)], "ReflectionBinding", "Bound as the app runs");

    /// <summary>A <c>&lt;Binding&gt;</c> element's fixes, as <see cref="Fixes(XamlFile, XmlAttribute, MarkupExtension, string?, XmlElement?)"/>
    /// gives a binding's; <c>ReflectionBinding</c> names its tags and those of its property elements
    /// (<c>&lt;Binding.Path&gt;</c>).</summary>
    public static IReadOnlyList<XamlFix> Fixes(XamlFile file, XmlElement binding, string? path, XmlElement? at)
    {
        bool rootsOwn = binding.Parent is { IsPropertyElement: true } holder && holder.Parent == file.Tree.Root && IsDataContextOf(holder);
        return Fixes(file, at ?? (rootsOwn ? null : file.Tree.Root), path, TagNames(binding), "ReflectionBinding", "Bound as the app runs");
    }

    /// <summary>Where an element's name stands in its tags and those of its property elements (<c>&lt;Binding.Path&gt;</c>),
    /// in order.</summary>
    private static IReadOnlyList<(int At, string Local)> TagNames(XmlElement element)
    {
        List<(int At, string Local)> names = [];
        foreach (XmlElement tag in element.Children.Where(c => c.IsPropertyElement && c.LocalName.StartsWith(element.LocalName + ".", StringComparison.Ordinal)).Prepend(element))
        {
            int prefix = tag.Prefix.Length > 0 ? tag.Prefix.Length + 1 : 0;
            names.Add((tag.NameStart + prefix, element.LocalName));
            if (tag.EndTagNameStart is { } end)
            {
                names.Add((end + prefix, element.LocalName));
            }
        }
        return [.. names.OrderBy(n => n.At)];
    }

    /// <summary>The fixes of a binding whose name stands at each of <paramref name="names"/>' places: <c>x:DataType</c> on
    /// <paramref name="target"/> (none where null), then the binding renamed <paramref name="renamed"/>.</summary>
    private static IReadOnlyList<XamlFix> Fixes(XamlFile file, XmlElement? target, string? path, IReadOnlyList<(int At, string Local)> names, string renamed, string detail)
    {
        List<XamlFix> fixes = [];
        if (target is not null)
        {
            List<ClrType> models = [.. ViewModels(file)];
            string? step = FirstStep(path);
            bool Has(ClrType t) => file.Types.MembersOf(t).Any(m => m.Kind is MemberKind.Property or MemberKind.Method && !m.IsStatic && m.Name == step);
            List<ClrType> having = step is null ? [] : [.. models.Where(Has)];
            if (having.Count == 0 && step is not null)
            {
                having = [.. Models(file).Where(Has)];
            }
            List<ClrType> offered = [.. (having.Count > 0 ? having : models).Take(MostOffered)];
            int place = target.NameStart;
            foreach (ClrType type in offered)
            {
                string named = offered.Count(t => t.Name == type.Name) > 1 ? type.FullName : type.Name;
                fixes.Add(new XamlFix($"Add x:DataType '{named}' to {target.LocalName}", file.Path, text => AddDataType(text, file.Dialect, type, place))
                {
                    Detail = type.Namespace,
                });
            }
        }
        fixes.Add(Rename(file, names, renamed, detail));
        return fixes;
    }

    /// <summary>A fix naming each of <paramref name="names"/> <paramref name="renamed"/>, none where the text there has
    /// changed since.</summary>
    private static XamlFix Rename(XamlFile file, IReadOnlyList<(int At, string Local)> names, string renamed, string detail) =>
        new($"Use {renamed}", file.Path, text => names.All(n => n.At + n.Local.Length <= text.Length && text.AsSpan(n.At, n.Local.Length).SequenceEqual(n.Local))
            ? [.. names.Select(n => new TextEdit(n.At, n.Local.Length, renamed))]
            : [])
        {
            Detail = detail,
        };

    /// <summary>A <c>&lt;CompiledBinding&gt;</c> element's path given as text, which the compiler refuses (AVLN3000): only a
    /// compiled binding extension reads a path from text, as <c>{CompiledBinding}</c> and <c>&lt;Binding&gt;</c> are where
    /// bindings compile, which its fix makes of it there. Null for any other property or element.</summary>
    public static XamlDiagnostic? PathAsText(XamlFile file, XmlElement element, XamlProperty property, TextRange range)
    {
        if (file.Types.Resolve(file.Dialect.ValueTypeOf(property.ValueType)) is not { FullName: "Avalonia.Data.CompiledBindingPath" }
            || file.ObjectOf(element) is not { } made || file.Types.IsA(made, CompiledBindingExtension))
        {
            return null;
        }
        XmlElement binding = element.IsPropertyElement ? element.Parent! : element;
        return new XamlDiagnostic(range, XamlSeverity.Error, $"A {made.Name} element takes no {property.Name} as text")
        {
            Fixes = binding.LocalName == "CompiledBinding" && file.CompilesBindingsAt(binding) ? [Rename(file, TagNames(binding), "Binding", "Compiled as {CompiledBinding} is")] : [],
        };
    }

    /// <summary>The project's view models: its classes named for one, in a namespace, by name.</summary>
    private static IEnumerable<ClrType> ViewModels(XamlFile file) =>
        Models(file).Where(t => t.Name.EndsWith("ViewModel", StringComparison.Ordinal));

    /// <summary>The project's classes a view can bind to: in a namespace, not generic, nested, static or an Avalonia
    /// object (a view, a control), by name.</summary>
    private static IEnumerable<ClrType> Models(XamlFile file) =>
        file.Types.All.Where(t => t.IsFromSource && t.Kind == ClrTypeKind.Class && !t.IsNested && t.Arity == 0 && t.Namespace.Length > 0 && !t.IsStatic
            && !file.Types.IsA(t, "Avalonia.AvaloniaObject")).OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Namespace, StringComparer.Ordinal);

    /// <summary>The property a path's first step names, cast or not; null for none, or an indexer.</summary>
    private static string? FirstStep(string? path)
    {
        string text = Uncast((path ?? string.Empty).Trim().TrimStart('!').TrimStart());
        int end = text.IndexOfAny(['.', '[', '^', ' ', ')']);
        string step = end < 0 ? text : text[..end];
        return step.Length > 0 && (char.IsLetter(step[0]) || step[0] == '_') ? step : null;
    }

    /// <summary>The edits that give the element whose name starts at <paramref name="place"/> an <c>x:DataType</c> of a
    /// type, after the root's <c>x:Class</c> or else its last attribute, declaring the type's namespace (and XAML's) on the
    /// root where it lacks them; none where it has one, or no element starts there any more.</summary>
    private static IReadOnlyList<TextEdit> AddDataType(string text, XamlDialect dialect, ClrType type, int place)
    {
        XmlTree tree = XmlTree.Parse(text);
        if (tree.Elements.FirstOrDefault(e => e.NameStart == place && !e.IsPropertyElement) is not { } target)
        {
            return [];
        }
        List<TextEdit> declared = [];
        string x = XamlRefactorings.Prefix(text, tree, XamlDialect.XamlUri, "x", declared);
        if (target.Attributes.Any(a => a.Prefix == x && a.LocalName == "DataType"))
        {
            return [];
        }
        string wanted = type.Namespace.EndsWith("ViewModels", StringComparison.Ordinal) ? "vm" : "local";
        string prefix = XamlRefactorings.Prefix(text, tree, dialect.ClrNamespaceUri(type.Namespace, null), wanted, declared);
        XmlAttribute? after = target.Attributes.FirstOrDefault(a => a.Prefix == x && a.LocalName == "Class") ?? target.Attributes.LastOrDefault();
        int at = after?.End ?? target.NameEnd;
        string added = $"{XamlRefactorings.AttributeGap(text, target, after)}{x}:DataType=\"{(prefix.Length > 0 ? prefix + ":" : string.Empty)}{type.Name}\"";
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
