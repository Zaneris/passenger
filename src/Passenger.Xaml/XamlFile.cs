using System.Collections.Concurrent;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml;

/// <summary>What an xmlns URI is to XAML.</summary>
public enum XamlNamespaceKind
{
    /// <summary>The framework's own (Avalonia's <c>https://github.com/avaloniaui</c>), or another its assemblies map.</summary>
    Mapped,

    /// <summary>XAML's language (<c>x:</c>).</summary>
    Language,

    /// <summary>The designer's or markup compatibility's, ignored at run time (<c>d:</c>, <c>mc:</c>).</summary>
    Ignorable,

    /// <summary>A CLR namespace named in the URI (<c>using:App.ViewModels</c>, <c>clr-namespace:App;assembly=App</c>).</summary>
    Clr,

    /// <summary>A URI nothing maps.</summary>
    Unknown,
}

/// <summary>What an attribute is to XAML.</summary>
public enum XamlAttributeKind
{
    /// <summary>An xmlns declaration.</summary>
    Namespace,

    /// <summary>One of XAML's own (<c>x:Name</c>, <c>x:Key</c>, <c>x:Class</c>, <c>x:DataType</c>).</summary>
    Directive,

    /// <summary>A property of the element's type.</summary>
    Property,

    /// <summary>An attached property of another type (<c>Grid.Row</c>).</summary>
    Attached,

    /// <summary>An event, its value a handler's name.</summary>
    Event,

    /// <summary>In an ignorable namespace (<c>d:DesignWidth</c>, <c>mc:Ignorable</c>).</summary>
    Ignorable,

    /// <summary>A style class an Avalonia element has while its value is true (<c>Classes.selected="{Binding}"</c>).</summary>
    StyleClass,

    /// <summary>Nothing the element's type or its namespaces have.</summary>
    Unknown,
}

/// <summary>What Avalonia's compiler takes the data context a compiled binding starts from to be.</summary>
public enum CompiledContext
{
    /// <summary>Nothing gives it a type: the compiler refuses a binding from it (AVLN2100; AVLN2101 for a DataContext's).</summary>
    None,

    /// <summary>Something gives it a type, which may be one Passenger cannot tell.</summary>
    Typed,

    /// <summary>One the compiler has no type for, a reflection binding's or a data template's whose items say none: it
    /// refuses any step of a path from it (AVLN2000).</summary>
    Untyped,
}

/// <summary>What an attribute means: its kind, the property or event it sets, and for an attached property its owner.</summary>
public sealed record XamlAttributeMeaning(XamlAttributeKind Kind, XamlProperty? Property = null, ClrMember? Event = null, ClrType? Owner = null);

/// <summary>A XAML file's markup read against its project: which xmlns each prefix means where, which type each element
/// makes, what each attribute sets, the data type bindings resolve against and a style's target type. One per parse,
/// immutable but for answers it keeps as they are first asked, so any thread may ask it.</summary>
public sealed class XamlFile
{
    private readonly ConcurrentDictionary<XmlElement, ClrType?> types = new();

    public XamlFile(string path, XmlTree tree, XamlProject project)
    {
        Path = path;
        Tree = tree;
        Project = project;
    }

    public string Path { get; }

    public XmlTree Tree { get; }

    public XamlProject Project { get; }

    /// <summary>The theme variant (Dark, Light) whose resources are read where a theme has several.</summary>
    public string? Variant { get; init; }

    public TypeSystem Types => Project.Types;

    public XamlDialect Dialect => Project.Dialect;

    public string Text => Tree.Text;

    /// <summary>The type the root names in <c>x:Class</c>, its code-behind's.</summary>
    public string? ClassName => Tree.Root?.Attribute("x:Class")?.Value;

    public ClrType? ClassType => ClassName is { } name ? Types.Find(name) : null;

    /// <summary>The URI a prefix means at an element (the empty prefix, the default namespace).</summary>
    public string? UriOf(XmlElement? scope, string prefix)
    {
        string name = prefix.Length == 0 ? "xmlns" : "xmlns:" + prefix;
        for (XmlElement? at = scope; at is not null; at = at.Parent)
        {
            if (at.Attribute(name) is { } declared)
            {
                return declared.Value;
            }
        }
        return null;
    }

    /// <summary>Every prefix in scope at an element, the nearest declaration of each, the default one's as empty.</summary>
    public IReadOnlyList<(string Prefix, string Uri)> PrefixesAt(XmlElement? scope)
    {
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        for (XmlElement? at = scope; at is not null; at = at.Parent)
        {
            foreach (XmlAttribute attribute in at.Attributes)
            {
                if (attribute.Name == "xmlns")
                {
                    found.TryAdd(string.Empty, attribute.Value);
                }
                else if (attribute.Prefix == "xmlns")
                {
                    found.TryAdd(attribute.LocalName, attribute.Value);
                }
            }
        }
        return [.. found.Select(f => (f.Key, f.Value))];
    }

    /// <summary>The prefix a URI is declared with at an element; null when it is not declared there.</summary>
    public string? PrefixOf(XmlElement? scope, string uri) =>
        PrefixesAt(scope).Where(p => p.Uri == uri).Select(p => p.Prefix).Cast<string?>().FirstOrDefault();

    public XamlNamespaceKind KindOf(string? uri)
    {
        if (uri is null)
        {
            return XamlNamespaceKind.Unknown;
        }
        if (uri == XamlDialect.XamlUri)
        {
            return XamlNamespaceKind.Language;
        }
        if (uri is XamlDialect.DesignerUri or XamlDialect.CompatibilityUri)
        {
            return XamlNamespaceKind.Ignorable;
        }
        if (uri.StartsWith("using:", StringComparison.Ordinal) || uri.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            return XamlNamespaceKind.Clr;
        }
        return uri == Dialect.PresentationUri || Types.XmlnsDefinitions.Any(d => d.Uri == uri) ? XamlNamespaceKind.Mapped : XamlNamespaceKind.Unknown;
    }

    /// <summary>The CLR namespaces a URI maps to.</summary>
    public IReadOnlyList<string> ClrNamespacesOf(string? uri) => Types.ClrNamespacesOf(uri);

    /// <summary>The type a qualified name (<c>prefix:Name</c>) names at an element; for a markup extension, its
    /// <c>…Extension</c> type is tried first, and for an element after its own name (<c>&lt;StaticResource&gt;</c>), as
    /// XAML does.</summary>
    public ClrType? ResolveType(XmlElement? scope, string qualified, bool extension = false)
    {
        int colon = qualified.IndexOf(':');
        string prefix = colon < 0 ? string.Empty : qualified[..colon];
        string name = colon < 0 ? qualified : qualified[(colon + 1)..];
        if (name.Length == 0)
        {
            return null;
        }
        string? uri = UriOf(scope, prefix);
        if (KindOf(uri) == XamlNamespaceKind.Language)
        {
            return LanguageType(name);
        }
        IReadOnlyList<string> namespaces = ClrNamespacesOf(uri);
        foreach (string candidate in extension ? [name + "Extension", name] : new[] { name, name + "Extension" })
        {
            foreach (string clr in namespaces)
            {
                if (Types.Find(clr.Length > 0 ? $"{clr}.{candidate}" : candidate) is { } found)
                {
                    return found;
                }
            }
        }
        return null;
    }

    /// <summary>The CLR types XAML's language names (<c>x:String</c>, <c>x:Double</c>).</summary>
    public ClrType? LanguageType(string name) => LanguageTypes.TryGetValue(name, out string? full) ? Types.Find(full) : null;

    public static readonly IReadOnlyDictionary<string, string> LanguageTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["String"] = "System.String",
        ["Double"] = "System.Double",
        ["Single"] = "System.Single",
        ["Int32"] = "System.Int32",
        ["Int64"] = "System.Int64",
        ["Int16"] = "System.Int16",
        ["Byte"] = "System.Byte",
        ["Boolean"] = "System.Boolean",
        ["Char"] = "System.Char",
        ["Decimal"] = "System.Decimal",
        ["Object"] = "System.Object",
        ["TimeSpan"] = "System.TimeSpan",
        ["Uri"] = "System.Uri",
    };

    /// <summary>The type an object element makes; null for a property element, an x: directive's element, or a name
    /// nothing resolves. A <c>&lt;Binding&gt;</c> makes what the compiler makes of it: a compiled binding where bindings
    /// compile, a reflection binding where they do not.</summary>
    public ClrType? TypeOf(XmlElement element) =>
        element.IsPropertyElement ? null : types.GetOrAdd(element, e => (IsBindingElement(e) ? ResolveType(e, BindingMade(e), extension: true) : null) ?? ResolveType(e, e.Name));

    private string BindingMade(XmlElement binding) =>
        (binding.Prefix.Length > 0 ? binding.Prefix + ":" : string.Empty) + (CompilesBindingsAt(binding) ? "CompiledBinding" : "ReflectionBinding");

    /// <summary>A property element's owner and the property it sets (<c>&lt;Grid.RowDefinitions&gt;</c>).</summary>
    public (ClrType? Owner, XamlProperty? Property) PropertyElement(XmlElement element)
    {
        if (!element.IsPropertyElement)
        {
            return (null, null);
        }
        string local = element.LocalName;
        int dot = local.LastIndexOf('.');
        string ownerName = (element.Prefix.Length > 0 ? element.Prefix + ":" : string.Empty) + local[..dot];
        ClrType? owner = ResolveType(element, ownerName);
        if (owner is null)
        {
            return (null, null);
        }
        string property = local[(dot + 1)..];
        // The parent's own property, as written through a base type (<Panel.Children> in a StackPanel), else an attached one.
        XamlProperty? found = Dialect.Property(Types, ObjectOf(element) ?? owner, property) ?? Dialect.Property(Types, owner, property)
            ?? Dialect.Attached(Types, owner, property);
        return (owner, found);
    }

    /// <summary>The object an element's attributes and content belong to: its own type, or for a property element the
    /// object element it is in.</summary>
    public ClrType? ObjectOf(XmlElement element)
    {
        if (!element.IsPropertyElement)
        {
            return TypeOf(element);
        }
        return element.Parent is { } parent ? ObjectOf(parent) : null;
    }

    /// <summary>What an attribute sets on its element.</summary>
    public XamlAttributeMeaning MeaningOf(XmlAttribute attribute)
    {
        if (attribute.Name == "xmlns" || attribute.Prefix == "xmlns")
        {
            return new XamlAttributeMeaning(XamlAttributeKind.Namespace);
        }
        XmlElement element = attribute.Owner;
        if (attribute.Prefix.Length > 0)
        {
            string? uri = UriOf(element, attribute.Prefix);
            switch (KindOf(uri))
            {
                case XamlNamespaceKind.Language:
                    return new XamlAttributeMeaning(XamlAttributeKind.Directive);
                case XamlNamespaceKind.Ignorable:
                    return new XamlAttributeMeaning(XamlAttributeKind.Ignorable);
            }
            if (IsIgnored(element, attribute.Prefix))
            {
                return new XamlAttributeMeaning(XamlAttributeKind.Ignorable);
            }
        }
        string local = attribute.LocalName;
        int dot = local.LastIndexOf('.');
        if (dot > 0)
        {
            string ownerName = (attribute.Prefix.Length > 0 ? attribute.Prefix + ":" : string.Empty) + local[..dot];
            string name = local[(dot + 1)..];
            ClrType? owner = ResolveType(element, ownerName);
            if (owner is null)
            {
                return new XamlAttributeMeaning(XamlAttributeKind.Unknown);
            }
            if (owner.FullName == "Avalonia.Controls.Classes")
            {
                return new XamlAttributeMeaning(XamlAttributeKind.StyleClass, Owner: owner);
            }
            if (Dialect.Attached(Types, owner, name) is { } attachedProperty)
            {
                return new XamlAttributeMeaning(XamlAttributeKind.Attached, attachedProperty, Owner: owner);
            }
            // Owner.Property written for the element's own property (Button.Background on a Button).
            if (ObjectOf(element) is { } self && Types.IsA(self, owner.FullName) && Dialect.Property(Types, owner, name) is { } own)
            {
                return new XamlAttributeMeaning(XamlAttributeKind.Property, own, Owner: owner);
            }
            if (Dialect.EventsOf(Types, owner).FirstOrDefault(e => e.Name == name) is { } ownerEvent)
            {
                return new XamlAttributeMeaning(XamlAttributeKind.Event, Event: ownerEvent, Owner: owner);
            }
            return new XamlAttributeMeaning(XamlAttributeKind.Unknown, Owner: owner);
        }
        if (ObjectOf(element) is not { } type)
        {
            return new XamlAttributeMeaning(XamlAttributeKind.Unknown);
        }
        if (Dialect.Property(Types, type, local) is { } property)
        {
            return new XamlAttributeMeaning(XamlAttributeKind.Property, property);
        }
        if (Dialect.EventsOf(Types, type).FirstOrDefault(e => e.Name == local) is { } @event)
        {
            return new XamlAttributeMeaning(XamlAttributeKind.Event, Event: @event);
        }
        return new XamlAttributeMeaning(XamlAttributeKind.Unknown);
    }

    /// <summary>Whether a prefix is one <c>mc:Ignorable</c> names where the element is.</summary>
    public bool IsIgnored(XmlElement element, string prefix)
    {
        for (XmlElement? at = element; at is not null; at = at.Parent)
        {
            foreach (XmlAttribute attribute in at.Attributes)
            {
                if (attribute.LocalName == "Ignorable" && UriOf(at, attribute.Prefix) == XamlDialect.CompatibilityUri
                    && attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(prefix, StringComparer.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>The type a binding at an element resolves its path against: the nearest <c>x:DataType</c> above it (a data
    /// template's <c>DataType</c> too), or what a compiled <c>DataContext</c> binding gives, or where bindings compile the
    /// items a data template presents. In a dialect that compiles no binding (WPF's), unless <paramref name="hints"/> says
    /// otherwise, also <c>d:DataContext</c>'s <c>d:DesignInstance</c>, a <c>DataContext</c> property element's object, a
    /// <c>DataContext</c> bound to a path of the one above, and a template's items, from its control's <c>ItemsSource</c>.</summary>
    public ClrType? DataTypeAt(XmlElement element, bool inherit = true, bool stopAtTemplates = false, bool? hints = null)
    {
        bool reading = hints ?? !Dialect.CompilesBindings;
        if (reading && IsColumn(element))
        {
            return element.Ancestors().FirstOrDefault(a => a.Attribute("ItemsSource") is not null) is { } items ? ItemsOf(items, stopAtTemplates) : null;
        }
        for (XmlElement? at = element; at is not null; at = inherit ? at.Parent : null)
        {
            // A template's own bindings (a HierarchicalDataTemplate's ItemsSource) are its items' too, where hints are read.
            bool template = (at != element || reading) && !at.IsPropertyElement && at.LocalName.EndsWith("Template", StringComparison.Ordinal)
                && at.Attribute("DataType") is null && Directive(at, "DataType") is null;
            if (template && reading && ItemTypeOf(at, stopAtTemplates) is { } item)
            {
                return item;
            }
            // A data template's bindings, its own included, start from the items it presents, where the compiler infers their type.
            if (!reading && Dialect.CompilesBindings && DataTypeNamed(at) is null && IsDataTemplate(at))
            {
                if (TemplateItems(at, stopAtTemplates) is { Inferred: true } presented)
                {
                    return presented.Type;
                }
                if (stopAtTemplates)
                {
                    return null;
                }
            }
            // A template with no data type of its own is bound to what it is applied to, which no x:DataType above it says.
            if (stopAtTemplates && template)
            {
                return null;
            }
            // Nor is what a style or a resource is applied to.
            if (stopAtTemplates && reading && at != element && (at.IsPropertyElement ? at.LocalName.EndsWith(".Resources", StringComparison.Ordinal) : at.LocalName == "Style"))
            {
                return null;
            }
            if (DataTypeNamed(at) is { } named)
            {
                return TypeFromValue(at, named.Value);
            }
            if (reading && !at.IsPropertyElement && DataContextOf(at, stopAtTemplates) is { Says: true } context)
            {
                return context.Type;
            }
            if (Dialect.CompilesBindings && !at.IsPropertyElement && CompiledDataContextOf(at, stopAtTemplates) is { Context: not CompiledContext.None } compiled)
            {
                return compiled.Type;
            }
        }
        return null;
    }

    /// <summary>What names an element's data type: its <c>x:DataType</c>, or a data template's <c>DataType</c>.</summary>
    private XmlAttribute? DataTypeNamed(XmlElement element) =>
        element.Attributes.FirstOrDefault(a => a.LocalName == "DataType" && a.Prefix.Length > 0 && KindOf(UriOf(element, a.Prefix)) == XamlNamespaceKind.Language)
        ?? (!element.IsPropertyElement && element.LocalName is "DataTemplate" or "TreeDataTemplate" or "HierarchicalDataTemplate" ? element.Attribute("DataType") : null);

    /// <summary>The type a binding in an attribute resolves against: its element's, but for the element's own
    /// <c>DataContext</c>, which binds against the one above, and for a property that takes its type from the items its
    /// control presents (<c>[InheritDataTypeFromItems]</c>, a <c>DisplayMemberBinding</c>), theirs where the compiler infers it.</summary>
    public ClrType? DataTypeOf(XmlAttribute attribute, bool stopAtTemplates = false) =>
        Dialect.CompilesBindings && attribute.Name != "DataContext" && MeaningOf(attribute).Property is { } property && PresentedItems(attribute.Owner, property, stopAtTemplates) is { Inferred: true } items
            ? items.Type
            : BoundAgainst(attribute.Owner, attribute.Name == "DataContext", stopAtTemplates);

    /// <summary>The type a binding in an attribute starts its path from: the data type it names (see
    /// <see cref="DataTypeNamedBy(XmlElement, MarkupExtension, bool)"/>), else its attribute's.</summary>
    public ClrType? DataTypeOf(XmlAttribute attribute, MarkupExtension binding, bool stopAtTemplates = false) =>
        DataTypeNamedBy(attribute.Owner, binding, attribute.Name == "DataContext") ?? DataTypeOf(attribute, stopAtTemplates);

    /// <summary>The data type a compiled binding names for its path to start from (<c>DataType=vm:Person</c>, over any
    /// source it names), as the compiler reads it: not an element's own DataContext's (<paramref name="dataContext"/>),
    /// which starts above whatever it names, nor one whose path starts at an element or a relative source. Null where it
    /// names none, or bindings do not compile.</summary>
    public ClrType? DataTypeNamedBy(XmlElement element, MarkupExtension binding, bool dataContext)
    {
        if (!Dialect.CompilesBindings || dataContext || !(binding.LocalName == "CompiledBinding" || (binding.LocalName == "Binding" && CompilesBindingsAt(element)))
            || binding.Argument("DataType") is not { } named || binding.Arguments.Any(a => a.Key == "ElementName" || (a.Key == "RelativeSource" && !IsDataContextSource(a.Nested))))
        {
            return null;
        }
        return named.Nested is { } nested
            ? nested.LocalName == "Type" && nested.Argument(null) is { Nested: null } type ? ResolveType(element, type.Value.Trim()) : null
            : TypeFromValue(element, named.Value);
    }

    /// <summary>The data type a <c>&lt;Binding&gt;</c> element names for its path to start from, as
    /// <see cref="DataTypeNamedBy(XmlElement, MarkupExtension, bool)"/> reads a binding's.</summary>
    public ClrType? DataTypeNamedBy(XmlElement binding, bool dataContext) =>
        dataContext || !CompilesBindingsAt(binding) || binding.Attribute("DataType") is not { } named
        || binding.Attributes.Any(a => a.Name == "ElementName" || (a.Name == "RelativeSource" && !IsDataContextSource(MarkupExtension.Parse(Text, a.ValueStart, a.ValueEnd))))
        || binding.Children.Any(c => c.IsPropertyElement && c.LocalName[(c.LocalName.LastIndexOf('.') + 1)..] is "ElementName" or "RelativeSource")
            ? null
            : TypeFromValue(binding, named.Value);

    /// <summary>The type a binding element (<c>&lt;Binding Path="Name"/&gt;</c>) resolves against: that of the element
    /// whose property it sets, or of a multi-binding's, but for an element's own <c>DataContext</c>, which binds against
    /// the one above.</summary>
    public ClrType? DataTypeOf(XmlElement binding, bool stopAtTemplates = false) =>
        binding.Parent is { IsPropertyElement: true } holder && holder.Parent is { } owner
            ? BoundAgainst(owner, holder.LocalName.EndsWith(".DataContext", StringComparison.Ordinal), stopAtTemplates)
            : DataTypeAt(binding, stopAtTemplates: stopAtTemplates);

    private ClrType? BoundAgainst(XmlElement owner, bool dataContext, bool stopAtTemplates) => dataContext
        ? owner.Parent is { } parent ? DataTypeAt(parent, stopAtTemplates: stopAtTemplates) : null
        : DataTypeAt(owner, stopAtTemplates: stopAtTemplates);

    /// <summary>What the compiler takes the data context a compiled binding of an element's property starts from to be,
    /// and the element that settles it (null for none): the items the property presents where it takes its type from them
    /// and the compiler infers it, else the nearest <c>x:DataType</c>, data template or <c>DataContext</c> a markup
    /// extension sets, an element's own <c>DataContext</c> binding (<paramref name="dataContext"/>) looking above it. An
    /// element nothing resolves gives a type, unknown.</summary>
    public (CompiledContext Context, XmlElement? At) CompiledContextOf(XmlElement element, XamlProperty? property, bool dataContext)
    {
        if (!dataContext && property is not null && PresentedItems(element, property, stopAtTemplates: true) is { Inferred: true })
        {
            return (CompiledContext.Typed, element);
        }
        for (XmlElement? at = dataContext ? element.Parent : element; at is not null; at = at.Parent)
        {
            if (at.IsPropertyElement)
            {
                continue;
            }
            if (DataTypeNamed(at) is not null || TypeOf(at) is null)
            {
                return (CompiledContext.Typed, at);
            }
            if (IsDataTemplate(at))
            {
                return (TemplateItems(at, stopAtTemplates: true).Inferred ? CompiledContext.Typed : CompiledContext.Untyped, at);
            }
            if (CompiledDataContextOf(at, stopAtTemplates: true).Context is not CompiledContext.None and var set)
            {
                return (set, at);
            }
        }
        return (CompiledContext.None, null);
    }

    private bool IsDataTemplate(XmlElement element) =>
        !element.IsPropertyElement && TypeOf(element) is { } type && Types.IsA(type, "Avalonia.Controls.Templates.IDataTemplate");

    /// <summary>The items a data template presents, where the property it is the value of takes its type from them: see
    /// <see cref="PresentedItems"/>.</summary>
    private (bool Inferred, ClrType? Type) TemplateItems(XmlElement template, bool stopAtTemplates) =>
        template.Parent is { IsPropertyElement: true } holder && holder.Parent is { } owner && PropertyElement(holder).Property is { } property
            ? PresentedItems(owner, property, stopAtTemplates)
            : (false, null);

    /// <summary>Whether the compiler infers the type of the items an element's <c>[InheritDataTypeFromItems]</c> property
    /// presents, and that type where it is known: from a compiled binding with a data context to start from, its path's
    /// element type; from an object, its element type; not from a binding that does not compile, a dynamic or static
    /// resource, nor where the items are not given. What else gives them (text, <c>x:Static</c>, another extension) is
    /// taken as inferred, its type unknown.</summary>
    private (bool Inferred, ClrType? Type) PresentedItems(XmlElement element, XamlProperty property, bool stopAtTemplates)
    {
        if (property.ClrProperty?.Attributes.FirstOrDefault(a => a.ShortName == "InheritDataTypeFromItems") is not { } inherit
            || inherit.StringArgument(0) is not { Length: > 0 } itemsProperty)
        {
            return (false, null);
        }
        string? ancestor = inherit.Named.FirstOrDefault(n => n.Key == "AncestorType").Value as string;
        XmlElement? items = element;
        while (ancestor is not null && items is not null && !(TypeOf(items) is { } type && Types.IsA(type, ancestor)))
        {
            items = items.Parent;
        }
        if (items is null)
        {
            return (false, null);
        }
        if (items.Attribute(itemsProperty) is { } given)
        {
            if (MarkupExtension.Parse(Text, given.ValueStart, given.ValueEnd) is not { Name.Length: > 0 } extension
                || (extension.Prefix.Length > 0 && KindOf(UriOf(items, extension.Prefix)) == XamlNamespaceKind.Language))
            {
                return (true, null);
            }
            if (extension.LocalName == "CompiledBinding" || (extension.LocalName == "Binding" && CompilesBindingsAt(items)))
            {
                if (CompiledContextOf(items, property: null, dataContext: false).Context == CompiledContext.None)
                {
                    return (false, null);
                }
                MarkupArgument? path = extension.Argument(null) ?? extension.Argument("Path");
                return (true, NamesSource(extension) || path is not { Nested: null, Value.Length: > 0 } || DataTypeAt(items, stopAtTemplates: stopAtTemplates) is not { } context
                    || PathType(context, path.Value.Trim()) is not { } collection ? null : ItemTypeOf(collection));
            }
            return ResolveType(items, extension.Name, extension: true) is { } made && (IsBinding(made) || made.FullName == "Avalonia.Markup.Xaml.MarkupExtensions.StaticResourceExtension")
                ? (false, null)
                : (true, null);
        }
        if (items.Children.FirstOrDefault(c => c.IsPropertyElement && c.LocalName.EndsWith("." + itemsProperty, StringComparison.Ordinal)) is not { } holder)
        {
            return (false, null);
        }
        return (true, holder.Children.FirstOrDefault(c => !c.IsPropertyElement) is { } presented && !IsBindingElement(presented) && TypeOf(presented) is { } presentedType
            ? ItemTypeOf(TypeRef.Named(presentedType.FullName))
            : null);
    }

    /// <summary>The type of the items a collection holds, where one type argument says it (a dictionary's items are pairs).</summary>
    private ClrType? ItemTypeOf(TypeRef collection) => collection.Arguments.Count > 1 ? null : ElementTypeOf(collection);

    /// <summary>Whether a type is one of the framework's bindings, whose result the compiler takes no type from.</summary>
    private bool IsBinding(ClrType type) => Types.IsA(type, "Avalonia.Data.BindingBase") || Types.IsA(type, "Avalonia.Data.IBinding");

    /// <summary>What an element's own <c>DataContext</c> says its data context is where bindings compile, as the compiler
    /// infers it: a compiled binding's path's type from the type above, whatever data type or source it names (unknown where
    /// it starts at an element or a relative source, or is not followed); another binding's (a reflection binding's, a
    /// dynamic resource's) no type; another markup extension's that extension's own type. Text, an object, XAML's own
    /// extensions and a binding element but <c>Binding</c> say nothing, leaving it to the type above.</summary>
    private (CompiledContext Context, ClrType? Type) CompiledDataContextOf(XmlElement element, bool stopAtTemplates)
    {
        if (element.Attribute("DataContext") is { } set)
        {
            if (MarkupExtension.Parse(Text, set.ValueStart, set.ValueEnd) is not { Name.Length: > 0 } extension
                || (extension.Prefix.Length > 0 && KindOf(UriOf(element, extension.Prefix)) == XamlNamespaceKind.Language))
            {
                return (CompiledContext.None, null);
            }
            if (extension.LocalName == "CompiledBinding" || (extension.LocalName == "Binding" && CompilesBindingsAt(element)))
            {
                MarkupArgument? path = extension.Argument(null) ?? extension.Argument("Path");
                return (CompiledContext.Typed, NamesSource(extension, dataContext: true) || path is { Nested: not null } ? null : PathTypeAbove(element, path?.Value, stopAtTemplates));
            }
            return ResolveType(element, extension.Name, extension: true) is not { } made ? (CompiledContext.Typed, null)
                : IsBinding(made) ? (CompiledContext.Untyped, null)
                : (CompiledContext.Typed, made);
        }
        XmlElement? holder = element.Children.FirstOrDefault(c => c.IsPropertyElement && c.LocalName.EndsWith(".DataContext", StringComparison.Ordinal));
        if (holder?.Children.FirstOrDefault(c => !c.IsPropertyElement) is not { } held || !IsBindingElement(held))
        {
            return (CompiledContext.None, null);
        }
        return CompilesBindingsAt(held)
            ? (CompiledContext.Typed, NamesSource(held, dataContext: true) ? null : PathTypeAbove(element, held.Attribute("Path")?.Value, stopAtTemplates))
            : (CompiledContext.Untyped, null);
    }

    /// <summary>The type a path has, read from the type above an element; the type itself for no path or ".".</summary>
    private ClrType? PathTypeAbove(XmlElement element, string? path, bool stopAtTemplates)
    {
        if (element.Parent is not { } parent || DataTypeAt(parent, stopAtTemplates: stopAtTemplates) is not { } above)
        {
            return null;
        }
        string trimmed = path?.Trim() ?? string.Empty;
        return trimmed is "" or "." ? above : Types.Resolve(PathType(above, trimmed));
    }

    /// <summary>Whether a <c>{Binding}</c> or <c>&lt;Binding&gt;</c> at an element compiles: the project's default
    /// (Avalonia's own, on since 11), unless the nearest <c>x:CompileBindings</c> says otherwise.</summary>
    public bool CompilesBindingsAt(XmlElement element)
    {
        for (XmlElement? at = element; at is not null; at = at.Parent)
        {
            if (Directive(at, "CompileBindings") is { } set)
            {
                return !string.Equals(set.Value.Trim(), "False", StringComparison.OrdinalIgnoreCase);
            }
        }
        return Project.CompilesBindings;
    }

    /// <summary>Whether an element is the framework's <c>&lt;Binding&gt;</c>, which the compiler reads as <c>{Binding}</c>
    /// is read where bindings compile.</summary>
    public bool IsBindingElement(XmlElement element) =>
        Dialect.CompilesBindings && !element.IsPropertyElement && element.LocalName == "Binding" && UriOf(element, element.Prefix) == Dialect.PresentationUri;

    /// <summary>Whether a binding names where its path starts other than its data context: an element, a source, a data
    /// type, or a relative source but the data context's own (<c>{RelativeSource DataContext}</c>). An element's own
    /// <c>DataContext</c> binding (<paramref name="dataContext"/>) starts at the data context above whatever source or data
    /// type it names, as the compiler reads it.</summary>
    public static bool NamesSource(MarkupExtension binding, bool dataContext = false) =>
        binding.Arguments.Any(a => (a.Key is "Source" or "DataType" && !dataContext) || a.Key == "ElementName"
            || (a.Key == "RelativeSource" && !IsDataContextSource(a.Nested)));

    /// <summary>Whether a binding element names where its path starts other than its data context, as
    /// <see cref="NamesSource(MarkupExtension, bool)"/> reads its attributes; any such property given as an element does.</summary>
    public bool NamesSource(XmlElement binding, bool dataContext = false) =>
        binding.Attributes.Any(a => (a.Name is "Source" or "DataType" && !dataContext) || a.Name == "ElementName"
            || (a.Name == "RelativeSource" && !IsDataContextSource(MarkupExtension.Parse(Text, a.ValueStart, a.ValueEnd))))
        || binding.Children.Any(c => c.IsPropertyElement && c.LocalName[(c.LocalName.LastIndexOf('.') + 1)..] is var name
            && ((name is "Source" or "DataType" && !dataContext) || name is "ElementName" or "RelativeSource"));

    /// <summary>Whether a relative source is the data context's own: <c>{RelativeSource DataContext}</c>, or its
    /// <c>Mode=DataContext</c>.</summary>
    private static bool IsDataContextSource(MarkupExtension? relative) =>
        relative is { LocalName: "RelativeSource" } && (relative.Argument("Mode") ?? relative.Argument(null))?.Value.Trim() == "DataContext";

    /// <summary>What an element says its data context is: whether it sets one, and its type when that is known.</summary>
    private (bool Says, ClrType? Type) DataContextOf(XmlElement element, bool stopAtTemplates)
    {
        foreach (XmlAttribute attribute in element.Attributes)
        {
            if (attribute.LocalName == "DataContext" && attribute.Prefix.Length > 0 && UriOf(element, attribute.Prefix) == XamlDialect.DesignerUri)
            {
                if (MarkupExtension.Parse(Text, attribute.ValueStart, attribute.ValueEnd) is { LocalName: "DesignInstance" } design
                    && (design.Argument("Type") ?? design.Argument(null)) is { } named)
                {
                    string type = named.Nested is { LocalName: "Type" } nested ? nested.Argument(null)?.Value ?? string.Empty : named.Value;
                    return (true, ResolveType(element, type.Trim()));
                }
                return (true, null);
            }
        }
        foreach (XmlElement child in element.Children)
        {
            if (child.IsPropertyElement && child.LocalName.EndsWith(".DataContext", StringComparison.Ordinal))
            {
                return (true, child.Children.FirstOrDefault(c => !c.IsPropertyElement) is { } made ? TypeOf(made) : null);
            }
        }
        if (element.Attribute("DataContext") is not { } set)
        {
            return (false, null);
        }
        if (MarkupExtension.Parse(Text, set.ValueStart, set.ValueEnd) is { LocalName: "Binding" or "CompiledBinding" } binding
            && !binding.Arguments.Any(a => a.Key is "Source" or "ElementName" or "RelativeSource")
            && element.Parent is { } parent && DataTypeAt(parent, stopAtTemplates: stopAtTemplates) is { } outer)
        {
            string path = (binding.Argument(null) ?? binding.Argument("Path"))?.Value.Trim() ?? ".";
            return (true, path is "" or "." ? outer : Types.Resolve(PathType(outer, path)));
        }
        return (true, null);
    }

    /// <summary>The type of the items a template inside an items control's <c>ItemTemplate</c> is applied to: the element
    /// type of the collection its <c>ItemsSource</c> binds.</summary>
    private ClrType? ItemTypeOf(XmlElement template, bool stopAtTemplates)
    {
        if (template.Parent is not { IsPropertyElement: true } holder)
        {
            return null;
        }
        // A list's ItemTemplate, or a grid column's cell templates (WPF's DataGridTemplateColumn), whose list is above.
        XmlElement? items = holder.LocalName.EndsWith(".ItemTemplate", StringComparison.Ordinal) ? holder.Parent
            : holder.LocalName.EndsWith(".CellTemplate", StringComparison.Ordinal) || holder.LocalName.EndsWith(".CellEditingTemplate", StringComparison.Ordinal)
                ? holder.Ancestors().FirstOrDefault(a => a.Attribute("ItemsSource") is not null)
                : null;
        return items is null ? null : ItemsOf(items, stopAtTemplates);
    }

    /// <summary>The type of an items control's items: the element type of the collection its <c>ItemsSource</c> binds.</summary>
    private ClrType? ItemsOf(XmlElement items, bool stopAtTemplates)
    {
        if (items.Attribute("ItemsSource") is not { } source
            || MarkupExtension.Parse(Text, source.ValueStart, source.ValueEnd) is not { LocalName: "Binding" or "CompiledBinding" } binding
            || binding.Arguments.Any(a => a.Key is "Source" or "ElementName" or "RelativeSource")
            || (binding.Argument(null) ?? binding.Argument("Path")) is not { Nested: null, Value.Length: > 0 } path
            || DataTypeAt(items, stopAtTemplates: stopAtTemplates) is not { } context
            || PathType(context, path.Value.Trim()) is not { } collection)
        {
            return null;
        }
        return ElementTypeOf(collection);
    }

    /// <summary>Whether an element is a column of WPF's grids (DataGrid's, a ListView's GridView's), whose bindings are
    /// applied to each item, outside the visual tree.</summary>
    private bool IsColumn(XmlElement element) =>
        !element.IsPropertyElement && TypeOf(element) is { } type
        && (Types.IsA(type, "System.Windows.Controls.DataGridColumn") || Types.IsA(type, "System.Windows.Controls.GridViewColumn"));

    /// <summary>The type a binding path's last step has, each step a property of the one before; null where a step is not
    /// one (an indexer, an attached property, a current item).</summary>
    public TypeRef? PathType(ClrType start, string path)
    {
        ClrType current = start;
        TypeRef? last = null;
        foreach (string step in path.Split('.'))
        {
            string name = step.Trim();
            if (last is not null)
            {
                if (Types.Resolve(last) is not { } next)
                {
                    return null;
                }
                current = next;
            }
            if (name.Length == 0 || name.IndexOfAny(['[', '(', '/', '!', '#', '$', '^']) >= 0
                || Types.MembersOf(current).FirstOrDefault(m => m.Kind == MemberKind.Property && !m.IsStatic && m.Name == name) is not { } property)
            {
                return null;
            }
            last = property.Type;
        }
        return last;
    }

    /// <summary>What a collection's type holds: an array's element, a generic collection's last argument
    /// (<c>ObservableCollection&lt;Person&gt;</c>), or what the collection type's own bases and interfaces hold.</summary>
    public ClrType? ElementTypeOf(TypeRef collection)
    {
        if (collection.IsArray)
        {
            return Types.Resolve(collection with { IsArray = false });
        }
        if (collection.Arguments.Count > 0)
        {
            return Types.Resolve(collection.Arguments[^1]);
        }
        if (Types.Resolve(collection) is not { } resolved)
        {
            return null;
        }
        foreach (ClrType at in Types.SelfAndBases(resolved).Take(8))
        {
            foreach (TypeRef named in at.Base is { } baseType ? [baseType, .. at.Interfaces] : at.Interfaces)
            {
                if (named.Arguments.Count == 1 && (named.Name.EndsWith("IEnumerable`1", StringComparison.Ordinal) || named.Name.EndsWith("Collection`1", StringComparison.Ordinal)
                    || named.Name.EndsWith("List`1", StringComparison.Ordinal)))
                {
                    return Types.Resolve(named.Arguments[0]);
                }
            }
        }
        return null;
    }

    /// <summary>A type named in a value (<c>vm:MainViewModel</c>, <c>{x:Type vm:MainViewModel}</c>).</summary>
    public ClrType? TypeFromValue(XmlElement scope, string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith('{') && MarkupExtension.Parse(trimmed, 0, trimmed.Length) is { } extension && extension.LocalName == "Type"
            && extension.Argument(null) is { } argument)
        {
            trimmed = argument.Value;
        }
        return ResolveType(scope, trimmed);
    }

    /// <summary>The type a style's or control theme's setters set properties of: a <c>ControlTheme</c>'s
    /// <c>TargetType</c>, or the type a <c>Style</c>'s selector ends at, the nearest such element at or above
    /// <paramref name="element"/>; a nested style's <c>^</c> reaches its parent's.</summary>
    public ClrType? StyleTargetAt(XmlElement element)
    {
        for (XmlElement? at = element; at is not null; at = at.Parent)
        {
            if (at.IsPropertyElement)
            {
                continue;
            }
            switch (at.LocalName)
            {
                case "ControlTheme" or "Style" or "ControlTemplate" when at.Attribute("TargetType") is { } target:
                    return TypeFromValue(at, target.Value);
                case "Style" when at.Attribute("Selector") is { } selector:
                    if (Selectors.TargetOf(this, at, selector.Value) is { } selected)
                    {
                        return selected;
                    }
                    break;
            }
        }
        return null;
    }

    /// <summary>The type a setter's or trigger's property belongs to: the element its <c>TargetName</c> or <c>SourceName</c>
    /// names in its template (WPF's), else its style's or template's target.</summary>
    public ClrType? SetterTargetAt(XmlElement setter)
    {
        if ((setter.Attribute("TargetName") ?? setter.Attribute("SourceName")) is { Value.Length: > 0 } named)
        {
            return ElementNamed(setter, named.Value) is { } element ? ObjectOf(element) : null;
        }
        return StyleTargetAt(setter);
    }

    /// <summary>The element a name names from somewhere: in the nearest template around it (a template is a namescope of
    /// its own), else in the file.</summary>
    public XmlElement? ElementNamed(XmlElement from, string name)
    {
        XmlElement? scope = from.Ancestors().FirstOrDefault(a => !a.IsPropertyElement && a.LocalName.EndsWith("Template", StringComparison.Ordinal));
        IEnumerable<XmlElement> candidates = scope is not null ? scope.DescendantsAndSelf() : Tree.Elements;
        return candidates.FirstOrDefault(e => NameAttribute(e) is { } attribute && attribute.Value == name);
    }

    /// <summary>The elements named with <c>x:Name</c> or <c>Name</c>, with their names, in document order.</summary>
    public IReadOnlyList<(string Name, XmlAttribute Attribute)> Names
    {
        get
        {
            List<(string, XmlAttribute)> names = [];
            foreach (XmlElement element in Tree.Elements)
            {
                if (NameAttribute(element) is { } attribute && attribute.Value.Length > 0)
                {
                    names.Add((attribute.Value, attribute));
                }
            }
            return names;
        }
    }

    /// <summary>The attribute that names an element (<c>x:Name</c>, else <c>Name</c>).</summary>
    public XmlAttribute? NameAttribute(XmlElement element) =>
        element.Attributes.FirstOrDefault(a => a.LocalName == "Name" && a.Prefix.Length > 0 && KindOf(UriOf(element, a.Prefix)) == XamlNamespaceKind.Language)
        ?? (element.IsPropertyElement ? null : element.Attribute("Name"));

    /// <summary>The directive attribute of XAML's language with a name on an element (<c>x:Key</c>).</summary>
    public XmlAttribute? Directive(XmlElement element, string name) =>
        element.Attributes.FirstOrDefault(a => a.LocalName == name && a.Prefix.Length > 0 && KindOf(UriOf(element, a.Prefix)) == XamlNamespaceKind.Language);
}
