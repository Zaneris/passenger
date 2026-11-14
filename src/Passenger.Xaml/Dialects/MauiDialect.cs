using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Dialects;

/// <summary>.NET MAUI's XAML: its properties are the bindable properties its types register (a static
/// <c>BindableProperty</c> field <c>XxxProperty</c>, read as its CLR wrapper's type) as well as plain CLR properties;
/// attached properties are a static <c>SetXxx</c> with its field; content is the property <c>[ContentProperty]</c> names;
/// markup extensions implement <c>IMarkupExtension</c>; styles are keyed by <c>TargetType</c>. A file may declare no
/// namespace at all: its default one is MAUI's global namespace, which the compiler maps to MAUI's own and to whatever
/// the project's assembly maps to it, and <c>x:</c> is XAML 2009's. Bindings compile where an <c>x:DataType</c> is in
/// scope.</summary>
public sealed class MauiDialect : XamlDialect
{
    public static readonly MauiDialect Instance = new();

    private MauiDialect()
    {
    }

    public const string Uri = "http://schemas.microsoft.com/dotnet/2021/maui";

    /// <summary>The namespace a file's elements are in when it declares none, which aggregates others.</summary>
    public const string GlobalUri = "http://schemas.microsoft.com/dotnet/maui/global";

    /// <summary>MAUI's designer namespace, read at design time alone.</summary>
    public const string DesignUri = "http://schemas.microsoft.com/dotnet/2021/maui/design";

    private const string BindableProperty = "Microsoft.Maui.Controls.BindableProperty";

    private const string BindableObject = "Microsoft.Maui.Controls.BindableObject";

    public override string Name => "MAUI";

    public override string PresentationUri => Uri;

    public override IReadOnlyList<string> Extensions { get; } = [".xaml"];

    public override string LanguageUri => Xaml2009Uri;

    public override bool CompilesBindings => true;

    public override bool CompilesBindingsByDataType => true;

    public override bool RefusesUnknownPaths => false;

    public override bool SettersNameRegisteredProperties => true;

    public override bool ResourcesFromApplication => true;

    public override bool MakesClasses => true;

    /// <summary>The prefixes the compiler declares for every file, a file's own declarations above them.</summary>
    private static readonly Dictionary<string, string> Implicit = new(StringComparer.Ordinal)
    {
        [string.Empty] = GlobalUri,
        ["x"] = Xaml2009Uri,
        ["d"] = DesignUri,
        ["maui"] = Uri,
        ["global"] = GlobalUri,
    };

    /// <summary>The URI the compiler declares a prefix for in every file, read from MAUI's own table alone; null for one
    /// it does not.</summary>
    public static string? ImplicitlyDeclared(string prefix) => Implicit.GetValueOrDefault(prefix);

    /// <summary>The implicit prefixes, else the one a referenced assembly suggests for its namespace (<c>XmlnsPrefix</c>),
    /// which the compiler declares too.</summary>
    public override string? ImplicitUri(TypeSystem types, string prefix)
    {
        if (Implicit.TryGetValue(prefix, out string? uri))
        {
            return uri;
        }
        foreach ((string suggested, string named) in types.XmlnsPrefixes)
        {
            if (named == prefix)
            {
                return suggested;
            }
        }
        return null;
    }

    public override IReadOnlyList<(string Prefix, string Uri)> ImplicitPrefixes { get; } = [(string.Empty, GlobalUri), ("x", Xaml2009Uri)];

    /// <summary>The global namespace's mapping to MAUI's own, which MAUI's source generator writes into every project.</summary>
    public override IReadOnlyList<XmlnsDefinition> ImpliedDefinitions(string assemblyName) => [new XmlnsDefinition(GlobalUri, Uri, assemblyName)];

    /// <summary>The WinUI XAML of the Windows head, which MAUI's build takes out of its own.</summary>
    public override bool LeavesOut(string relative) => relative.StartsWith("Platforms/Windows/", StringComparison.OrdinalIgnoreCase);

    /// <summary>An include's file, or a style sheet's; an image or a font is named by its file's name alone, which the
    /// build gives it from wherever the project keeps it (<c>MauiImage</c>).</summary>
    public override bool NamesFile(XmlAttribute attribute) => attribute.Owner.LocalName is "ResourceDictionary" or "StyleSheet";

    /// <summary>The target's own property, else an attached one it or a base declares (<c>NavigationPage.IconColor</c> on
    /// a navigation page's style), as MAUI finds a setter's property by its field on the target.</summary>
    public override XamlProperty? SetterProperty(TypeSystem types, ClrType target, string name) =>
        Property(types, target, name) ?? types.SelfAndBases(target).Select(t => Attached(types, t, name)).FirstOrDefault(p => p is not null);

    /// <summary>A value in braces that starts with no markup extension, as MAUI's source generator reads one: <c>{= …}</c>
    /// always, else any whose first name is no extension's (a prefixed one is, but for an attached property's
    /// <c>target.(Type.Property)</c>), a binding's path, a member of the page, a static one, an interpolation, a lambda
    /// or an operator's operands. The generator judges it; the live check leaves it be.</summary>
    public override bool ReadsExpressions => true;

    /// <summary>A <c>clr-namespace</c>'s <c>targetPlatform=</c>, as MAUI's XAML reads it: one of its parts, split at ';'.</summary>
    public override string? TargetPlatformOf(string uri) =>
        uri.StartsWith("clr-namespace:", StringComparison.Ordinal) || uri.StartsWith("using:", StringComparison.Ordinal)
            ? uri.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("targetPlatform=", StringComparison.Ordinal))?["targetPlatform=".Length..]
            : null;

    /// <summary>The platforms MAUI names (its <c>DevicePlatform</c>'s) by the platform part of a target framework.</summary>
    private static readonly Dictionary<string, string[]> Platforms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["android"] = ["Android"],
        ["ios"] = ["iOS"],
        ["maccatalyst"] = ["MacCatalyst"],
        ["windows"] = ["WinUI", "Windows"],
        ["tizen"] = ["Tizen"],
        ["macos"] = ["macOS"],
        ["tvos"] = ["tvOS"],
    };

    /// <summary>Whether a target framework's platform is the one named; a framework of no platform (<c>net11.0</c>) builds
    /// for none of them.</summary>
    public override bool BuildsFor(string? targetFramework, string platform)
    {
        int dash = targetFramework?.IndexOf('-') ?? -1;
        if (targetFramework is null || dash < 0)
        {
            return false;
        }
        string part = new([.. targetFramework[(dash + 1)..].TakeWhile(char.IsLetter)]);
        return Platforms.TryGetValue(part, out string[]? names) && names.Contains(platform, StringComparer.Ordinal);
    }

    public override bool IsExpression(string value, Func<string, bool> extension)
    {
        string trimmed = value.Trim();
        if (trimmed.Length < 3 || trimmed[0] != '{' || trimmed[^1] != '}' || trimmed.StartsWith("{}", StringComparison.Ordinal))
        {
            return false;
        }
        if (trimmed[1] == '=')
        {
            return true;
        }
        int start = 1;
        while (start < trimmed.Length && char.IsWhiteSpace(trimmed[start]))
        {
            start++;
        }
        int end = start;
        while (end < trimmed.Length && (char.IsLetterOrDigit(trimmed[end]) || trimmed[end] is ':' or '_'))
        {
            end++;
        }
        if (end == start)
        {
            return true;
        }
        string name = trimmed[start..end];
        if (name.Contains(':', StringComparison.Ordinal))
        {
            return end < trimmed.Length && trimmed[end] == '.';
        }
        return !extension(name);
    }

    public override XamlProperty? ContentProperty(TypeSystem types, ClrType type)
    {
        foreach (ClrType at in types.SelfAndBases(type))
        {
            if (at.Attribute("ContentProperty")?.StringArgument(0) is { Length: > 0 } name)
            {
                return Property(types, type, name);
            }
        }
        return null;
    }

    public override bool IsMarkupExtension(TypeSystem types, ClrType type) =>
        type.Kind == ClrTypeKind.Class && !type.IsAbstract && !type.IsStatic && types.IsA(type, "Microsoft.Maui.Controls.Xaml.IMarkupExtension");

    /// <summary>As the base says, but no attribute, exception, event's arguments or type converter, which no XAML makes;
    /// and a platform's own view (<see cref="IsPlatformView"/>).</summary>
    public override bool CanCreate(TypeSystem types, ClrType type) =>
        (base.CanCreate(types, type) && !type.Name.EndsWith("Attribute", StringComparison.Ordinal) && !type.Name.EndsWith("Exception", StringComparison.Ordinal)
            && !type.Name.EndsWith("EventArgs", StringComparison.Ordinal) && !types.IsA(type, "System.ComponentModel.TypeConverter")) || IsPlatformView(types, type);

    /// <summary>A platform's view XAML can make: an Android view with a constructor taking only its context, or a UIKit view
    /// with one taking nothing.</summary>
    private static bool IsPlatformView(TypeSystem types, ClrType type) =>
        type is { Kind: ClrTypeKind.Class, IsAbstract: false, Arity: 0, IsNested: false }
        && ((types.IsA(type, "Android.Views.View") && type.Members.Any(m => m.Kind == MemberKind.Constructor && m.IsPublic && m.Parameters is [{ Name: "Android.Content.Context" }]))
            || (types.IsA(type, "UIKit.UIView") && type.HasDefaultConstructor));

    protected override IReadOnlyList<XamlProperty> ReadProperties(TypeSystem types, ClrType type)
    {
        Dictionary<string, XamlProperty> found = new(StringComparer.Ordinal);
        foreach (ClrType at in types.SelfAndBases(type))
        {
            foreach (ClrMember member in at.Members)
            {
                if (member.Kind == MemberKind.Field && member.IsStatic && member.Name.Length > "Property".Length
                    && member.Name.EndsWith("Property", StringComparison.Ordinal) && IsBindableProperty(types, member.Type))
                {
                    string name = member.Name[..^"Property".Length];
                    if (found.TryGetValue(name, out XamlProperty? wrapper))
                    {
                        if (wrapper.Field is null && wrapper.Kind == XamlPropertyKind.Clr)
                        {
                            found[name] = wrapper with { Kind = XamlPropertyKind.Bindable, Field = member };
                        }
                        continue;
                    }
                    // A field whose owner has SetXxx or GetXxx and no wrapper is an attached property's, which AttachedOf gives.
                    if (!at.Members.Any(m => m.Kind == MemberKind.Property && !m.IsStatic && m.Name == name)
                        && at.Members.Any(m => m.Kind == MemberKind.Method && m.IsStatic && (m.Name == "Set" + name || m.Name == "Get" + name)))
                    {
                        continue;
                    }
                    found[name] = new XamlProperty(name, at, TypeRef.Named("System.Object"), XamlPropertyKind.Bindable, Field: member);
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
                                IsReadOnly = !member.CanWrite && !collection, IsHidden = Hidden(member),
                            };
                        }
                        continue;
                    }
                    // A read-only property a constructor takes by its name ([Parameter("TargetType")]) is set as the object is made.
                    bool made = !member.CanWrite && TakenByConstructor(at, member.Name);
                    found[member.Name] = member.CanWrite || collection || made
                        ? new XamlProperty(member.Name, at, member.Type, XamlPropertyKind.Clr, ClrProperty: member, IsCollection: collection && !member.CanWrite && !made)
                        {
                            IsHidden = Hidden(member),
                        }
                        : new XamlProperty(member.Name, at, member.Type, XamlPropertyKind.Clr, ClrProperty: member) { IsReadOnly = true };
                }
            }
        }
        // A plain read-only CLR property is no property XAML names; a bindable one's (IsFocused) is, in a trigger.
        return [.. found.Values.Where(p => !(p.Kind == XamlPropertyKind.Clr && p.IsReadOnly)).OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    protected override IReadOnlyList<XamlProperty> ReadAttached(TypeSystem types, ClrType owner)
    {
        Dictionary<string, XamlProperty> found = new(StringComparer.Ordinal);
        foreach (ClrMember method in owner.Members)
        {
            if (method.Kind != MemberKind.Method || !method.IsStatic || method.Name.Length <= 3 || !method.Name.StartsWith("Set", StringComparison.Ordinal)
                || method.Parameters.Count != 2)
            {
                continue;
            }
            string name = method.Name[3..];
            ClrMember? field = FieldOf(types, owner, name);
            if (field is null && !TakesElement(types, method.Parameters[0]))
            {
                continue;
            }
            found.TryAdd(name, new XamlProperty(name, owner, method.Parameters[1], XamlPropertyKind.Attached, Field: field, Setter: method));
        }
        return [.. found.Values.OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    /// <summary>Whether a public constructor of a type takes a parameter of a property's name, as MAUI's XAML sets one.</summary>
    private static bool TakenByConstructor(ClrType type, string property) =>
        type.Members.Any(m => m.Kind == MemberKind.Constructor && m.IsPublic && m.ParameterNames.Any(p => string.Equals(p, property, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Whether a member is marked <c>[EditorBrowsable(EditorBrowsableState.Never)]</c>.</summary>
    private static bool Hidden(ClrMember member) =>
        member.Attributes.FirstOrDefault(a => a.ShortName == "EditorBrowsable") is { Arguments: [1] };

    private static ClrMember? FieldOf(TypeSystem types, ClrType owner, string name) =>
        owner.Members.FirstOrDefault(m => m.Kind == MemberKind.Field && m.IsStatic && m.Name == name + "Property" && IsBindableProperty(types, m.Type));

    private static bool TakesElement(TypeSystem types, TypeRef parameter) =>
        types.Resolve(parameter) is { } type && (types.IsA(type, BindableObject) || type.Kind == ClrTypeKind.Interface);

    /// <summary>Whether a field's type is <c>BindableProperty</c>, as metadata names it or as source writes it.</summary>
    private static bool IsBindableProperty(TypeSystem types, TypeRef field)
    {
        if (field.Scope is null)
        {
            return field.Name == BindableProperty;
        }
        return field.Name is "BindableProperty" or BindableProperty && (types.Resolve(field)?.FullName ?? BindableProperty) == BindableProperty;
    }
}
