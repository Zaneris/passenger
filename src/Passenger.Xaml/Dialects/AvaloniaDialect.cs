using Passenger.Xaml.Metadata;

namespace Passenger.Xaml.Dialects;

/// <summary>Avalonia's XAML: its properties are the static <c>StyledProperty</c>, <c>DirectProperty</c> and
/// <c>AttachedProperty</c> fields its types register (each with a CLR wrapper but the attached ones), as well as plain CLR
/// properties; content is the property marked <c>[Content]</c>; markup extensions are its <c>…Extension</c> types and the
/// bindings its compiler knows; styles select by Avalonia's selectors, themes by <c>ControlTheme</c>.</summary>
public sealed class AvaloniaDialect : XamlDialect
{
    public static readonly AvaloniaDialect Instance = new();

    private AvaloniaDialect()
    {
    }

    public const string Uri = "https://github.com/avaloniaui";

    public override string Name => "Avalonia";

    public override string PresentationUri => Uri;

    public override IReadOnlyList<string> Extensions { get; } = [".axaml", ".paml", ".xaml"];

    public override IReadOnlyList<string> LanguageExtensions { get; } = ["Static", "Type", "Null", "True", "False"];

    public override bool CompilesBindings => true;

    public override IReadOnlyList<string> AssetItems { get; } = ["AvaloniaResource"];

    /// <summary>Avalonia's own form, <c>using:</c>, whatever assembly holds the namespace.</summary>
    public override string ClrNamespaceUri(string clrNamespace, string? assembly) => $"using:{clrNamespace}";

    /// <summary>The bindings Avalonia's compiler takes in braces, whose types are no <c>…Extension</c>.</summary>
    private static readonly HashSet<string> Bindings = new(StringComparer.Ordinal)
    {
        "Avalonia.Data.Binding", "Avalonia.Data.TemplateBinding", "Avalonia.Data.MultiBinding",
    };

    public override XamlProperty? ContentProperty(TypeSystem types, ClrType type)
    {
        foreach (ClrType at in types.SelfAndBases(type))
        {
            foreach (ClrMember member in at.Members)
            {
                if (member.Kind == MemberKind.Property && member.Has("Content"))
                {
                    return Property(types, type, member.Name);
                }
            }
        }
        return null;
    }

    public override bool IsMarkupExtension(TypeSystem types, ClrType type) =>
        Bindings.Contains(type.FullName)
        || (type.Name.EndsWith("Extension", StringComparison.Ordinal) && type.Name.Length > "Extension".Length && type.Kind == ClrTypeKind.Class && !type.IsAbstract && !type.IsStatic);

    public override bool CanCreate(TypeSystem types, ClrType type)
    {
        if (type.Arity != 0 || type.IsNested || type.Kind is not (ClrTypeKind.Class or ClrTypeKind.Struct) || type.IsAbstract || type.IsStatic)
        {
            return false;
        }
        if (type.HasDefaultConstructor)
        {
            return !type.Name.EndsWith("Attribute", StringComparison.Ordinal) && !type.Name.EndsWith("Exception", StringComparison.Ordinal)
                && !type.Name.EndsWith("EventArgs", StringComparison.Ordinal);
        }
        // What Avalonia's compiler builds from the file's own place (ResourceInclude, StyleInclude), given a service provider or base URI.
        return type.Members.Any(m => m.Kind == MemberKind.Constructor && m.Parameters.Count == 1
            && m.Parameters[0].Name is "System.IServiceProvider" or "System.Uri");
    }

    public override TypeRef ValueTypeOf(TypeRef type) => Describe(type) is { } described ? described.Value : type;

    protected override IReadOnlyList<XamlProperty> ReadProperties(TypeSystem types, ClrType type)
    {
        Dictionary<string, XamlProperty> found = new(StringComparer.Ordinal);
        foreach (ClrType at in types.SelfAndBases(type))
        {
            foreach (ClrMember member in at.Members)
            {
                if (member.Kind == MemberKind.Field && member.IsStatic && member.Name.Length > "Property".Length
                    && member.Name.EndsWith("Property", StringComparison.Ordinal) && Describe(member.Type, types) is { Kind: not XamlPropertyKind.Attached } described)
                {
                    string name = member.Name[..^"Property".Length];
                    if (found.TryGetValue(name, out XamlProperty? wrapper))
                    {
                        if (wrapper.Field is null && wrapper.Kind == XamlPropertyKind.Clr)
                        {
                            found[name] = wrapper with { Kind = described.Kind, Field = member, ValueType = described.Value };
                        }
                        continue;
                    }
                    found[name] = new XamlProperty(name, at, described.Value, described.Kind, Field: member);
                }
                else if (member.Kind == MemberKind.Property && !member.IsStatic && member.IsPublic)
                {
                    if (found.TryGetValue(member.Name, out XamlProperty? registered))
                    {
                        if (registered.ClrProperty is null)
                        {
                            found[member.Name] = registered with { ClrProperty = member, IsCollection = !member.CanWrite && IsCollection(types, member.Type) };
                        }
                        continue;
                    }
                    bool collection = IsCollection(types, member.Type);
                    if (member.CanWrite || collection)
                    {
                        found[member.Name] = new XamlProperty(member.Name, at, member.Type, XamlPropertyKind.Clr, ClrProperty: member, IsCollection: collection && !member.CanWrite);
                    }
                }
            }
        }
        // The compiler reads a compiled binding's ElementName and RelativeSource as a reflection binding's, as where its path starts.
        if (type.FullName == "Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindingExtension" && types.Find("Avalonia.Data.Binding") is { } binding)
        {
            foreach (XamlProperty borrowed in PropertiesOf(types, binding).Where(p => p.Name is "ElementName" or "RelativeSource"))
            {
                found.TryAdd(borrowed.Name, borrowed);
            }
        }
        // A direct property whose wrapper cannot be set is read-only to XAML.
        return [.. found.Values.Where(p => !(p.Kind == XamlPropertyKind.Direct && p.ClrProperty is { CanWrite: false } && !p.IsCollection))
            .OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    protected override IReadOnlyList<XamlProperty> ReadAttached(TypeSystem types, ClrType owner)
    {
        List<XamlProperty> found = [];
        foreach (ClrMember member in owner.Members)
        {
            if (member.Kind == MemberKind.Field && member.IsStatic && member.Name.Length > "Property".Length
                && member.Name.EndsWith("Property", StringComparison.Ordinal) && Describe(member.Type, types) is { Kind: XamlPropertyKind.Attached } described)
            {
                string name = member.Name[..^"Property".Length];
                ClrMember? setter = owner.Members.FirstOrDefault(m => m.Kind == MemberKind.Method && m.IsStatic && m.Name == "Set" + name && m.Parameters.Count == 2);
                found.Add(new XamlProperty(name, owner, described.Value, XamlPropertyKind.Attached, Field: member, Setter: setter));
            }
        }
        return [.. found.OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    /// <summary>What an Avalonia property field's type says: its kind and the type of its value.</summary>
    private static (XamlPropertyKind Kind, TypeRef Value)? Describe(TypeRef field, TypeSystem? types = null)
    {
        string name = field.Scope is not null && types?.Resolve(field) is { } resolved ? resolved.FullName : field.Name;
        int dot = name.LastIndexOf('.');
        string bare = dot < 0 ? name : name[(dot + 1)..];
        return bare switch
        {
            "StyledProperty`1" when field.Arguments.Count == 1 => (XamlPropertyKind.Styled, field.Arguments[0]),
            "AvaloniaProperty`1" when field.Arguments.Count == 1 => (XamlPropertyKind.Styled, field.Arguments[0]),
            "DirectProperty`2" when field.Arguments.Count == 2 => (XamlPropertyKind.Direct, field.Arguments[1]),
            "AttachedProperty`1" when field.Arguments.Count == 1 => (XamlPropertyKind.Attached, field.Arguments[0]),
            _ => null,
        };
    }
}
