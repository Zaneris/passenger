using System.Collections.Concurrent;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Dialects;

/// <summary>How a property is declared: a plain CLR property, or its framework's own kind (Avalonia's styled and direct
/// properties, WPF's dependency properties, MAUI's bindable ones), or an attached property set on other elements.</summary>
public enum XamlPropertyKind
{
    Clr,
    Styled,
    Direct,
    Attached,
    Dependency,
    Bindable,
}

/// <summary>A property an element can set, as an attribute or a property element: its name, the type that declares it,
/// the type its value is read as, and what declares it in code (its framework field, its CLR property, an attached
/// property's setter), for a description and go to declaration.</summary>
public sealed record XamlProperty(string Name, ClrType Owner, TypeRef ValueType, XamlPropertyKind Kind, ClrMember? Field = null, ClrMember? ClrProperty = null,
    ClrMember? Setter = null, bool IsCollection = false)
{
    public bool IsAttached => Kind == XamlPropertyKind.Attached;

    /// <summary>A property XAML reads but cannot set (WPF's <c>IsMouseOver</c>), which only a trigger names.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>A property its framework keeps out of sight (<c>[EditorBrowsable(Never)]</c>), which XAML may set but
    /// completion does not offer.</summary>
    public bool IsHidden { get; init; }

    /// <summary>What go to declaration lands on: the CLR property, else the framework's field, else the setter.</summary>
    public ClrMember? Declaration => ClrProperty ?? Field ?? Setter;
}

/// <summary>A XAML dialect: what Avalonia's, WPF's and MAUI's XAML share is the language (<c>x:</c>, xmlns, markup extensions,
/// property elements) the core reads; how each declares properties, content, events, templates and styles, and which
/// namespaces are its own, is the dialect's. Answers are kept per type, since a file asks the same types over and over.</summary>
public abstract class XamlDialect
{
    /// <summary>XAML's own namespace, the <c>x:</c> prefix's.</summary>
    public const string XamlUri = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>XAML 2009's own namespace, the <c>x:</c> prefix's in MAUI's XAML, read as <see cref="XamlUri"/> is.</summary>
    public const string Xaml2009Uri = "http://schemas.microsoft.com/winfx/2009/xaml";

    /// <summary>Whether a URI is XAML's own language namespace, of either year.</summary>
    public static bool IsLanguageUri(string? uri) => uri is XamlUri or Xaml2009Uri;

    /// <summary>The designer's namespace (<c>d:</c>), ignorable at run time.</summary>
    public const string DesignerUri = "http://schemas.microsoft.com/expression/blend/2008";

    /// <summary>Markup compatibility's namespace (<c>mc:</c>, its <c>Ignorable</c>).</summary>
    public const string CompatibilityUri = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>Each type system's answers, gone with it once a newer one replaces it.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TypeSystem, Answers> kept = [];

    private sealed class Answers
    {
        public ConcurrentDictionary<ClrType, IReadOnlyList<XamlProperty>> Properties { get; } = new();

        public ConcurrentDictionary<ClrType, IReadOnlyList<XamlProperty>> Attached { get; } = new();
    }

    public abstract string Name { get; }

    /// <summary>The namespace a file's elements are in by default.</summary>
    public abstract string PresentationUri { get; }

    /// <summary>The file extensions its XAML is written in.</summary>
    public abstract IReadOnlyList<string> Extensions { get; }

    /// <summary>Whether a XAML file of the project, by its path from the project's folder with forward slashes, is another
    /// framework's that its build leaves out (a MAUI app's WinUI <c>Platforms/Windows/App.xaml</c>).</summary>
    public virtual bool LeavesOut(string relative) => false;

    /// <summary>The URI its build names a XAML file of an assembly by, given the file's path in the assembly as a URI writes
    /// it (<see cref="XamlProject.PathIn"/>); null in a dialect whose build names none.</summary>
    public virtual string? UriOf(string assembly, string path) => null;

    /// <summary>The language namespace a declaration of <c>x:</c> that Passenger writes names.</summary>
    public virtual string LanguageUri => XamlUri;

    /// <summary>The namespace a prefix means where no element declares it (MAUI's implicit <c>x:</c> and default
    /// namespace); null for none, as in a dialect that declares every prefix it uses.</summary>
    public virtual string? ImplicitUri(TypeSystem types, string prefix) => null;

    /// <summary>The prefixes a file has in scope though none is declared, which completion offers.</summary>
    public virtual IReadOnlyList<(string Prefix, string Uri)> ImplicitPrefixes => [];

    /// <summary>The xmlns definitions its compiler adds to the project's own assembly, beyond those of its code.</summary>
    public virtual IReadOnlyList<XmlnsDefinition> ImpliedDefinitions(string assemblyName) => [];

    /// <summary>The xmlns definitions its compiler reads off its framework's types though no attribute declares them (WinUI's
    /// presentation namespace), given every type the project references.</summary>
    public virtual IReadOnlyList<XmlnsDefinition> FrameworkDefinitions(IEnumerable<ClrType> types) => [];

    /// <summary>The files of its framework whose resource keys an app reaches though none of its own files merges them in
    /// (WinUI's <c>generic.xaml</c>, read through <see cref="ThemeFileKeys"/>), found from the assemblies the project
    /// compiles against.</summary>
    public virtual IEnumerable<string> ThemeFiles(IReadOnlyList<string> references) => [];

    /// <summary>The directives of XAML's namespace its compiler takes beyond those every dialect has, each with what it is
    /// for (WinUI's <c>x:Load</c>, <c>x:Phase</c>).</summary>
    public virtual IReadOnlyList<(string Name, string About)> OwnDirectives => [];

    /// <summary>Whether a <c>&lt;Binding&gt;</c> element compiles as <c>{Binding}</c> does (Avalonia's).</summary>
    public virtual bool CompilesBindingElements => false;

    /// <summary>Whether a <c>{Binding}</c> compiles only where an <c>x:DataType</c> is in scope (MAUI's), rather than as
    /// the project's default and <c>x:CompileBindings</c> say.</summary>
    public virtual bool CompilesBindingsByDataType => false;

    /// <summary>Whether a compiled binding's path its data type lacks fails the build, rather than falling back to a
    /// binding read as the app runs (MAUI's source generator's, which warns).</summary>
    public virtual bool RefusesUnknownPaths => true;

    /// <summary>Whether a setter or trigger names its framework's registered properties alone (WPF's, MAUI's).</summary>
    public virtual bool SettersNameRegisteredProperties => false;

    /// <summary>Whether a library's XAML takes the resources its keys name from the application that uses it, so a key
    /// nothing in it defines is no mistake there.</summary>
    public virtual bool ResourcesFromApplication => false;

    /// <summary>Whether a value in braces is a C# expression its compiler reads (MAUI's from .NET 11: <c>{Name}</c>,
    /// <c>{$'{First} {Last}'}</c>, <c>{(s, e) => Count++}</c>) rather than a markup extension; <paramref name="extension"/>
    /// says whether a name is one of the file's markup extensions.</summary>
    public virtual bool IsExpression(string value, Func<string, bool> extension) => false;

    /// <summary>The platform a namespace is for, as a <c>clr-namespace</c> names it (<c>targetPlatform=Android</c>); null for
    /// one of every platform, or in a dialect that has none.</summary>
    public virtual string? TargetPlatformOf(string uri) => null;

    /// <summary>Whether a project built for a target framework (<c>net11.0-android</c>, null for none known) builds for a
    /// platform its namespaces name.</summary>
    public virtual bool BuildsFor(string? targetFramework, string platform) => true;

    /// <summary>Whether its compiler reads C# expressions in braces at all (<see cref="IsExpression"/>).</summary>
    public virtual bool ReadsExpressions => false;

    /// <summary>Whether an attribute's value names a file by its path in the project, which is checked to be there (an
    /// include's <c>Source</c>, an image's).</summary>
    public virtual bool NamesFile(XmlAttribute attribute) => true;

    /// <summary>Whether a static resource may name a key of its framework's theme (WinUI's <c>BodyTextBlockStyle</c>), as
    /// a dynamic one may in every dialect.</summary>
    public virtual bool StaticThemeKeys => false;

    /// <summary>The property every element's resources are set through (<c>Resources</c>).</summary>
    public virtual string ResourcesProperty => "Resources";

    /// <summary>The markup extensions its compiler knows by name though no type is written for them, beyond x:'s.</summary>
    public virtual IReadOnlyList<string> IntrinsicExtensions => [];

    /// <summary>The markup extensions of XAML's own namespace it takes (<c>x:Static</c>, <c>x:Type</c>).</summary>
    public virtual IReadOnlyList<string> LanguageExtensions { get; } = ["Static", "Type", "Null", "Array", "Reference"];

    /// <summary>Whether its compiler compiles bindings against <c>x:DataType</c> (Avalonia's), which <c>x:DataType</c> and
    /// <c>x:CompileBindings</c> are for.</summary>
    public virtual bool CompilesBindings => false;

    /// <summary>Whether its compiler makes the class an <c>x:Class</c> names when no C# declares it (WPF's MarkupCompile);
    /// Avalonia's refuses one it cannot find (AVLN2000).</summary>
    public virtual bool MakesClasses => false;

    /// <summary>The markup extensions of the designer's namespace its designer reads (WPF's <c>d:DesignInstance</c>).</summary>
    public virtual IReadOnlyList<string> DesignerExtensions => [];

    /// <summary>The project file's item types that embed an asset a XAML value can name (Avalonia's
    /// <c>AvaloniaResource</c>, WPF's <c>Resource</c>).</summary>
    public virtual IReadOnlyList<string> AssetItems => [];

    /// <summary>The xmlns a CLR namespace is written as, of an assembly not the project's own when one is named.</summary>
    public virtual string ClrNamespaceUri(string clrNamespace, string? assembly) =>
        assembly is null ? $"clr-namespace:{clrNamespace}" : $"clr-namespace:{clrNamespace};assembly={assembly}";

    /// <summary>The properties a type's elements can set (not attached ones), its bases' included, by name.</summary>
    public IReadOnlyList<XamlProperty> PropertiesOf(TypeSystem types, ClrType type) =>
        kept.GetValue(types, _ => new Answers()).Properties.GetOrAdd(type, t => ReadProperties(types, t));

    /// <summary>The attached properties a type declares for other elements (<c>Grid.Row</c>).</summary>
    public IReadOnlyList<XamlProperty> AttachedOf(TypeSystem types, ClrType owner) =>
        kept.GetValue(types, _ => new Answers()).Attached.GetOrAdd(owner, t => ReadAttached(types, t));

    public XamlProperty? Property(TypeSystem types, ClrType type, string name) =>
        PropertiesOf(types, type).FirstOrDefault(p => p.Name == name);

    /// <summary>The property a setter of a style of a type names without an owner: the type's own.</summary>
    public virtual XamlProperty? SetterProperty(TypeSystem types, ClrType target, string name) => Property(types, target, name);

    /// <summary>An attached property by its owner, as the dialect's compiler looks one up.</summary>
    public virtual XamlProperty? Attached(TypeSystem types, ClrType owner, string name) =>
        AttachedOf(types, owner).FirstOrDefault(p => p.Name == name);

    /// <summary>The events a type's elements can name a handler for.</summary>
    public virtual IEnumerable<ClrMember> EventsOf(TypeSystem types, ClrType type) =>
        types.MembersOf(type).Where(m => m.Kind == MemberKind.Event && !m.IsStatic && m.IsPublic);

    /// <summary>The property an element's content sets, if its type has one: the one its type's or a base's
    /// <c>[ContentProperty]</c> names.</summary>
    public virtual XamlProperty? ContentProperty(TypeSystem types, ClrType type)
    {
        foreach (ClrType at in types.SelfAndBases(type))
        {
            // WinUI's names it by its Name, WPF's and MAUI's first argument.
            if (at.Attribute("ContentProperty") is { } content && (content.StringArgument(0) ?? content.Named.FirstOrDefault(n => n.Key == "Name").Value as string) is { Length: > 0 } name)
            {
                return Property(types, type, name);
            }
        }
        return null;
    }

    /// <summary>Whether XAML can make an element of a type.</summary>
    public virtual bool CanCreate(TypeSystem types, ClrType type) =>
        type.Arity == 0 && type.Kind is ClrTypeKind.Class or ClrTypeKind.Struct && type.HasDefaultConstructor && !type.IsNested;

    /// <summary>An attribute, an exception or an event's arguments, by its name, which no XAML makes though it could.</summary>
    protected static bool NoXamlMakes(ClrType type) =>
        type.Name.EndsWith("Attribute", StringComparison.Ordinal) || type.Name.EndsWith("Exception", StringComparison.Ordinal)
        || type.Name.EndsWith("EventArgs", StringComparison.Ordinal);

    /// <summary>Whether a type is a markup extension, written in braces.</summary>
    public abstract bool IsMarkupExtension(TypeSystem types, ClrType type);

    /// <summary>The type a value of the given type names, when that is a framework's wrapper (a property's
    /// <c>StyledProperty&lt;T&gt;</c> holds a <c>T</c>).</summary>
    public virtual TypeRef ValueTypeOf(TypeRef type) => type;

    protected abstract IReadOnlyList<XamlProperty> ReadProperties(TypeSystem types, ClrType type);

    protected abstract IReadOnlyList<XamlProperty> ReadAttached(TypeSystem types, ClrType owner);

    /// <summary>The suffix of the static field a framework registers a property by (<c>WidthProperty</c>).</summary>
    private const string RegisteredSuffix = "Property";

    /// <summary>The name a static field registers a property by (<c>WidthProperty</c>'s Width), or where
    /// <paramref name="properties"/> a static property (WinUI's projection gives each as one); null for any other member.</summary>
    protected static string? RegisteredName(ClrMember member, bool properties = false) =>
        (member.Kind == MemberKind.Field || (properties && member.Kind == MemberKind.Property)) && member.IsStatic && member.Name.Length > RegisteredSuffix.Length
        && member.Name.EndsWith(RegisteredSuffix, StringComparison.Ordinal)
            ? member.Name[..^RegisteredSuffix.Length]
            : null;

    /// <summary>Whether a field's type is a framework's registration type (<paramref name="fullName"/>), as metadata names it
    /// or as source writes it, by its short name resolved.</summary>
    protected static bool IsRegistration(TypeSystem types, TypeRef field, string fullName)
    {
        if (field.Scope is null)
        {
            return field.Name == fullName;
        }
        return (field.Name == fullName || field.Name == fullName[(fullName.LastIndexOf('.') + 1)..]) && (types.Resolve(field)?.FullName ?? fullName) == fullName;
    }

    /// <summary>The properties a type's elements set where its framework registers them as WPF's and MAUI's do: a static field
    /// <c>XxxProperty</c> its <paramref name="registers"/> takes (or a static property, where <paramref name="properties"/>),
    /// of <paramref name="kind"/>, read as its CLR wrapper's type where it has one, and the plain CLR properties XAML can
    /// set, its bases' included.</summary>
    protected IReadOnlyList<XamlProperty> ReadRegistered(TypeSystem types, ClrType type, XamlPropertyKind kind, Func<TypeRef, bool> registers, bool properties = false)
    {
        Dictionary<string, XamlProperty> found = new(StringComparer.Ordinal);
        foreach (ClrType at in types.SelfAndBases(type))
        {
            foreach (ClrMember member in at.Members)
            {
                if (RegisteredName(member, properties) is { } name && registers(member.Type))
                {
                    if (found.TryGetValue(name, out XamlProperty? wrapper))
                    {
                        if (wrapper.Field is null && wrapper.Kind == XamlPropertyKind.Clr)
                        {
                            found[name] = wrapper with { Kind = kind, Field = member };
                        }
                        continue;
                    }
                    // A field whose owner has SetXxx or GetXxx and no wrapper is an attached property's, which AttachedOf gives.
                    if (!at.Members.Any(m => m.Kind == MemberKind.Property && !m.IsStatic && m.Name == name)
                        && at.Members.Any(m => m.Kind == MemberKind.Method && m.IsStatic && (m.Name == "Set" + name || m.Name == "Get" + name)))
                    {
                        continue;
                    }
                    found[name] = new XamlProperty(name, at, TypeRef.Named("System.Object"), kind, Field: member);
                }
                else if (member.Kind == MemberKind.Property && !member.IsStatic && member.IsPublic)
                {
                    bool collection = IsCollection(types, member.Type);
                    if (found.TryGetValue(member.Name, out XamlProperty? registered))
                    {
                        if (registered.ClrProperty is null)
                        {
                            found[member.Name] = registered with
                            {
                                ClrProperty = member, ValueType = member.Type, IsCollection = !member.CanWrite && collection,
                                IsReadOnly = !member.CanWrite && !collection, IsHidden = Hides(member),
                            };
                        }
                        continue;
                    }
                    bool made = !member.CanWrite && SetAsMade(at, member);
                    // A read-only wrapper is kept until its registered property's field shows it is one (IsMouseOver).
                    found[member.Name] = member.CanWrite || collection || made
                        ? new XamlProperty(member.Name, at, member.Type, XamlPropertyKind.Clr, ClrProperty: member, IsCollection: collection && !member.CanWrite && !made)
                        {
                            IsHidden = Hides(member),
                        }
                        : new XamlProperty(member.Name, at, member.Type, XamlPropertyKind.Clr, ClrProperty: member) { IsReadOnly = true };
                }
            }
        }
        // A plain read-only CLR property is no property XAML names at all; a registered property's is, in a trigger.
        return [.. found.Values.Where(p => !(p.Kind == XamlPropertyKind.Clr && p.IsReadOnly)).OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    /// <summary>Whether its framework keeps a CLR property out of completion's sight (<see cref="XamlProperty.IsHidden"/>).</summary>
    protected virtual bool Hides(ClrMember property) => false;

    /// <summary>Whether a read-only CLR property of a type is set as its object is made, so XAML sets it as any other.</summary>
    protected virtual bool SetAsMade(ClrType type, ClrMember property) => false;

    /// <summary>The attached properties a type declares as WPF's and MAUI's do: a static <c>SetXxx(element, value)</c>, with
    /// its registered field (or static property, where <paramref name="properties"/>) where <paramref name="registers"/>
    /// takes one, else only where it takes an element first (<paramref name="takesElement"/>); where
    /// <paramref name="gettersAlone"/>, also a <c>GetXxx(element)</c> with its field and no setter, read-only, which a trigger
    /// names (<c>Validation.HasError</c>).</summary>
    protected static IReadOnlyList<XamlProperty> ReadAttachedPairs(ClrType owner, Func<TypeRef, bool> registers, Func<TypeRef, bool> takesElement, bool gettersAlone,
        bool properties = false)
    {
        ClrMember? FieldOf(string name) =>
            owner.Members.FirstOrDefault(m => RegisteredName(m, properties) == name && registers(m.Type));
        Dictionary<string, XamlProperty> found = new(StringComparer.Ordinal);
        foreach (ClrMember method in owner.Members)
        {
            if (method.Kind != MemberKind.Method || !method.IsStatic || method.Name.Length <= 3 || !method.Name.StartsWith("Set", StringComparison.Ordinal)
                || method.Parameters.Count != 2)
            {
                continue;
            }
            string name = method.Name[3..];
            ClrMember? field = FieldOf(name);
            // A static setter that is no attached property's (Application.SetCookie(Uri, string)) takes no element first.
            if (field is null && !takesElement(method.Parameters[0]))
            {
                continue;
            }
            found.TryAdd(name, new XamlProperty(name, owner, method.Parameters[1], XamlPropertyKind.Attached, Field: field, Setter: method));
        }
        foreach (ClrMember method in owner.Members)
        {
            if (gettersAlone && method.Kind == MemberKind.Method && method.IsStatic && method.Name.Length > 3 && method.Name.StartsWith("Get", StringComparison.Ordinal)
                && method.Parameters.Count == 1 && !found.ContainsKey(method.Name[3..]) && FieldOf(method.Name[3..]) is { } field)
            {
                string name = method.Name[3..];
                found[name] = new XamlProperty(name, owner, method.Type, XamlPropertyKind.Attached, Field: field, Setter: method) { IsReadOnly = true };
            }
        }
        return [.. found.Values.OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    /// <summary>Whether a type is a collection XAML adds its children to, so a read-only property of it is still set as a
    /// property element.</summary>
    public static bool IsCollection(TypeSystem types, TypeRef type)
    {
        if (type.IsArray)
        {
            return true;
        }
        string name = type.Name;
        if (name is "System.Collections.IList" or "System.Collections.IDictionary" or "System.Collections.Generic.IList`1" or "System.Collections.Generic.ICollection`1"
            or "System.Collections.Generic.IDictionary`2")
        {
            return true;
        }
        if (types.Resolve(type) is not { } resolved || resolved.Kind is ClrTypeKind.Enum or ClrTypeKind.Struct || resolved.FullName == "System.String")
        {
            return false;
        }
        return types.IsA(resolved, "System.Collections.IList") || types.IsA(resolved, "System.Collections.Generic.ICollection`1")
            || types.IsA(resolved, "System.Collections.IDictionary") || types.IsA(resolved, "System.Collections.Generic.IDictionary`2");
    }
}
