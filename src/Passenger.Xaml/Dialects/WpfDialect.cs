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

    /// <summary>Its pack URI, the assembly escaped as the path is.</summary>
    public override string UriOf(string assembly, string path) => $"pack://application:,,,/{System.Uri.EscapeDataString(assembly)};component/{path}";

    public override IReadOnlyList<string> AssetItems { get; } = ["Resource", "Content"];

    public override IReadOnlyList<string> DesignerExtensions { get; } = ["DesignInstance", "DesignData"];

    public override bool MakesClasses => true;

    public override bool SettersNameRegisteredProperties => true;

    public override bool ResourcesFromApplication => true;

    public override bool IsMarkupExtension(TypeSystem types, ClrType type) =>
        type.Kind == ClrTypeKind.Class && !type.IsAbstract && !type.IsStatic && types.IsA(type, "System.Windows.Markup.MarkupExtension");

    public override bool CanCreate(TypeSystem types, ClrType type) => base.CanCreate(types, type) && !NoXamlMakes(type);

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

    protected override IReadOnlyList<XamlProperty> ReadProperties(TypeSystem types, ClrType type) =>
        ReadRegistered(types, type, XamlPropertyKind.Dependency, field => IsRegistration(types, field, DependencyProperty));

    protected override IReadOnlyList<XamlProperty> ReadAttached(TypeSystem types, ClrType owner) =>
        ReadAttachedPairs(owner, field => IsRegistration(types, field, DependencyProperty), parameter => TakesElement(types, parameter), gettersAlone: true);

    private static bool TakesElement(TypeSystem types, TypeRef parameter) =>
        parameter.Name is "System.Object" || (types.Resolve(parameter) is { } type && types.IsA(type, DependencyObject));
}
