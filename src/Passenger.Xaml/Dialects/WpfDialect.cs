using Passenger.Xaml.Metadata;

namespace Passenger.Xaml.Dialects;

/// <summary>WPF's XAML: its properties are the dependency properties its types register (a static <c>DependencyProperty</c>
/// field <c>XxxProperty</c>, read as its CLR wrapper's type) as well as plain CLR properties; attached properties are the
/// static <c>SetXxx</c> and <c>GetXxx</c> pairs of their owner; content is the property <c>[ContentProperty]</c> names;
/// markup extensions derive from <c>MarkupExtension</c>; attached events are a static <c>XxxEvent</c> field with its
/// <c>AddXxxHandler</c>; styles are keyed by <c>TargetType</c>, with triggers. Its types come from the WindowsDesktop
/// targeting pack's reference assemblies.</summary>
public sealed class WpfDialect : XamlDialect
{
    public static readonly WpfDialect Instance = new();

    private WpfDialect()
    {
    }

    public const string Uri = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private const string DependencyProperty = "System.Windows.DependencyProperty";

    private const string DependencyObject = "System.Windows.DependencyObject";

    public override string Name => "WPF";

    public override string PresentationUri => Uri;

    public override IReadOnlyList<string> Extensions { get; } = [".xaml"];

    public override IReadOnlyList<string> AssetItems { get; } = ["Resource", "Content"];

    public override IReadOnlyList<string> DesignerExtensions { get; } = ["DesignInstance", "DesignData"];

    public override bool MakesClasses => true;

    public override bool SettersNameRegisteredProperties => true;

    public override bool ResourcesFromApplication => true;

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
        type.Kind == ClrTypeKind.Class && !type.IsAbstract && !type.IsStatic && types.IsA(type, "System.Windows.Markup.MarkupExtension");

    public override bool CanCreate(TypeSystem types, ClrType type) =>
        base.CanCreate(types, type) && !type.Name.EndsWith("Attribute", StringComparison.Ordinal) && !type.Name.EndsWith("Exception", StringComparison.Ordinal)
            && !type.Name.EndsWith("EventArgs", StringComparison.Ordinal);

    /// <summary>A type's own attached property, else one a base declares: WPF's XAML finds an owner's inherited
    /// <c>SetXxx</c> (<c>VirtualizingStackPanel.IsVirtualizing</c>).</summary>
    public override XamlProperty? Attached(TypeSystem types, ClrType owner, string name)
    {
        foreach (ClrType at in types.SelfAndBases(owner))
        {
            if (AttachedOf(types, at).FirstOrDefault(p => p.Name == name) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>Its CLR events, and the attached events a type declares as a static <c>XxxEvent</c> field with an
    /// <c>AddXxxHandler</c> method and no CLR event (<c>Mouse.MouseDown</c>, <c>Validation.Error</c>).</summary>
    public override IEnumerable<ClrMember> EventsOf(TypeSystem types, ClrType type)
    {
        List<ClrMember> events = [.. base.EventsOf(types, type)];
        HashSet<string> named = new(events.Select(e => e.Name), StringComparer.Ordinal);
        foreach (ClrMember member in type.Members)
        {
            if (member.Kind != MemberKind.Field || !member.IsStatic || member.Name.Length <= "Event".Length || !member.Name.EndsWith("Event", StringComparison.Ordinal))
            {
                continue;
            }
            string name = member.Name[..^"Event".Length];
            if (named.Contains(name))
            {
                continue;
            }
            ClrMember? adder = type.Members.FirstOrDefault(m => m.Kind == MemberKind.Method && m.IsStatic && m.Name == $"Add{name}Handler" && m.Parameters.Count == 2);
            if (adder is not null)
            {
                named.Add(name);
                events.Add(new ClrMember(type, name, MemberKind.Event, adder.Parameters[1], true, true, true, place: member.Place));
            }
        }
        return events;
    }

    protected override IReadOnlyList<XamlProperty> ReadProperties(TypeSystem types, ClrType type)
    {
        Dictionary<string, XamlProperty> found = new(StringComparer.Ordinal);
        foreach (ClrType at in types.SelfAndBases(type))
        {
            foreach (ClrMember member in at.Members)
            {
                if (member.Kind == MemberKind.Field && member.IsStatic && member.Name.Length > "Property".Length
                    && member.Name.EndsWith("Property", StringComparison.Ordinal) && IsDependencyProperty(types, member.Type))
                {
                    string name = member.Name[..^"Property".Length];
                    if (found.TryGetValue(name, out XamlProperty? wrapper))
                    {
                        if (wrapper.Field is null && wrapper.Kind == XamlPropertyKind.Clr)
                        {
                            found[name] = wrapper with { Kind = XamlPropertyKind.Dependency, Field = member };
                        }
                        continue;
                    }
                    // A field whose owner has SetXxx or GetXxx and no wrapper is an attached property's, which AttachedOf gives.
                    if (!at.Members.Any(m => m.Kind == MemberKind.Property && !m.IsStatic && m.Name == name)
                        && at.Members.Any(m => m.Kind == MemberKind.Method && m.IsStatic && (m.Name == "Set" + name || m.Name == "Get" + name)))
                    {
                        continue;
                    }
                    found[name] = new XamlProperty(name, at, TypeRef.Named("System.Object"), XamlPropertyKind.Dependency, Field: member);
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
                                IsReadOnly = !member.CanWrite && !collection,
                            };
                        }
                        continue;
                    }
                    if (member.CanWrite || collection)
                    {
                        found[member.Name] = new XamlProperty(member.Name, at, member.Type, XamlPropertyKind.Clr, ClrProperty: member, IsCollection: collection && !member.CanWrite);
                    }
                    else
                    {
                        // A read-only wrapper is kept until its dependency property's field shows it is one (IsMouseOver).
                        found[member.Name] = new XamlProperty(member.Name, at, member.Type, XamlPropertyKind.Clr, ClrProperty: member) { IsReadOnly = true };
                    }
                }
            }
        }
        // A plain read-only CLR property is no property XAML names at all; a dependency property's is, in a trigger.
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
            // A static setter that is no attached property's (Application.SetCookie(Uri, string)) takes no element first.
            if (field is null && !TakesElement(types, method.Parameters[0]))
            {
                continue;
            }
            found.TryAdd(name, new XamlProperty(name, owner, method.Parameters[1], XamlPropertyKind.Attached, Field: field, Setter: method));
        }
        foreach (ClrMember method in owner.Members)
        {
            // A getter alone, with its property's field, is a read-only attached property a trigger names (Validation.HasError).
            if (method.Kind == MemberKind.Method && method.IsStatic && method.Name.Length > 3 && method.Name.StartsWith("Get", StringComparison.Ordinal)
                && method.Parameters.Count == 1 && !found.ContainsKey(method.Name[3..]) && FieldOf(types, owner, method.Name[3..]) is { } field)
            {
                string name = method.Name[3..];
                found[name] = new XamlProperty(name, owner, method.Type, XamlPropertyKind.Attached, Field: field, Setter: method) { IsReadOnly = true };
            }
        }
        return [.. found.Values.OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    private static ClrMember? FieldOf(TypeSystem types, ClrType owner, string name) =>
        owner.Members.FirstOrDefault(m => m.Kind == MemberKind.Field && m.IsStatic && m.Name == name + "Property" && IsDependencyProperty(types, m.Type));

    private static bool TakesElement(TypeSystem types, TypeRef parameter) =>
        parameter.Name is "System.Object" || (types.Resolve(parameter) is { } type && types.IsA(type, DependencyObject));

    /// <summary>Whether a field's type is <c>DependencyProperty</c>, as metadata names it or as source writes it.</summary>
    private static bool IsDependencyProperty(TypeSystem types, TypeRef field)
    {
        if (field.Scope is null)
        {
            return field.Name == DependencyProperty;
        }
        return field.Name is "DependencyProperty" or DependencyProperty && (types.Resolve(field)?.FullName ?? DependencyProperty) == DependencyProperty;
    }
}
