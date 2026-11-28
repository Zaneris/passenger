using Passenger.Xaml.Metadata;

namespace Passenger.Xaml.Dialects;

/// <summary>WinUI's XAML (the Windows App SDK's): its properties are the dependency properties its types register (a
/// static <c>XxxProperty</c> of type <c>DependencyProperty</c>, a property in its C#/WinRT projection and a field in an
/// app's own code), read as its CLR wrapper's type, and plain CLR properties; attached properties are a static
/// <c>SetXxx</c> with its registration; content is the property <c>[ContentProperty(Name = …)]</c> names; markup
/// extensions derive from <c>MarkupExtension</c>, beside those its compiler knows by name (<c>{ThemeResource}</c>,
/// <c>{x:Bind}</c>). Its presentation namespace, which no attribute maps, holds every <c>Microsoft.UI.Xaml</c> namespace
/// of the projection, and its types come from the packages the project restores.</summary>
public sealed class WinUIDialect : XamlDialect
{
    public static readonly WinUIDialect Instance = new();

    private WinUIDialect()
    {
    }

    /// <summary>The namespace its presentation namespace holds, with those below it.</summary>
    public const string XamlNamespace = "Microsoft.UI.Xaml";

    private const string DependencyProperty = "Microsoft.UI.Xaml.DependencyProperty";

    private const string DependencyObject = "Microsoft.UI.Xaml.DependencyObject";

    public override string Name => "WinUI";

    public override string PresentationUri => WpfDialect.Uri;

    public override IReadOnlyList<string> Extensions { get; } = [".xaml"];

    /// <summary>Its <c>ms-appx:///</c> URI, a file of the app's package by its path there.</summary>
    public override string UriOf(string assembly, string path) => $"{AppScheme}/{path}";

    /// <summary>The scheme a file of the app's own package is named by (<c>ms-appx:///Assets/Logo.png</c>).</summary>
    public const string AppScheme = "ms-appx://";

    public override IReadOnlyList<string> AssetItems { get; } = ["Content"];

    public override bool MakesClasses => true;

    public override bool SettersNameRegisteredProperties => true;

    public override bool ResourcesFromApplication => true;

    public override bool StaticThemeKeys => true;

    public override string ClrNamespaceUri(string clrNamespace, string? assembly) => $"using:{clrNamespace}";

    /// <summary>The markup extensions its compiler takes in its presentation namespace though no type of that name derives
    /// from <c>MarkupExtension</c>.</summary>
    public override IReadOnlyList<string> IntrinsicExtensions { get; } = ["Binding", "StaticResource", "ThemeResource", "TemplateBinding", "RelativeSource", "CustomResource"];

    /// <summary><c>{x:Bind}</c>, compiled against the page or a data template's <c>x:DataType</c>, and <c>{x:Null}</c>.</summary>
    public override IReadOnlyList<string> LanguageExtensions { get; } = [Bind, "Null"];

    /// <summary>The name of <c>{x:Bind}</c>.</summary>
    public const string Bind = "Bind";

    /// <summary>The arguments <c>{x:Bind}</c> takes by name.</summary>
    public static readonly IReadOnlyList<string> BindKeys =
        ["Path", "Mode", "Converter", "ConverterParameter", "ConverterLanguage", "FallbackValue", "TargetNullValue", "BindBack", "UpdateSourceTrigger"];

    public override IReadOnlyList<(string Name, string About)> OwnDirectives { get; } =
    [
        ("DataType", "The type x:Bind resolves against in a data template"),
        ("Load", "Whether the element is made as the page loads, or once x:Bind or FindName asks for it"),
        ("Phase", "The pass a list's item draws the element in"),
        ("DefaultBindMode", "The mode x:Bind takes below the element"),
        ("DeferLoadStrategy", "Lazy: the element is made only once FindName asks for it"),
    ];

    /// <summary>The presentation namespace's CLR namespaces: <c>Microsoft.UI.Xaml</c> and every one below it the types hold,
    /// as its compiler maps them.</summary>
    public override IReadOnlyList<XmlnsDefinition> FrameworkDefinitions(IEnumerable<ClrType> types) =>
    [
        .. types.Select(t => (t.Namespace, t.Origin.AssemblyName))
            .Where(n => n.Namespace == XamlNamespace || n.Namespace.StartsWith(XamlNamespace + ".", StringComparison.Ordinal))
            .Distinct()
            .Select(n => new XmlnsDefinition(WpfDialect.Uri, n.Namespace, n.AssemblyName)),
    ];

    /// <summary>The <c>generic.xaml</c> its package keeps beside its projection (<c>Microsoft.WinUI/Themes/</c>), whose
    /// keys <c>XamlControlsResources</c> and the framework give every app.</summary>
    public override IEnumerable<string> ThemeFiles(IReadOnlyList<string> references) =>
        references.Where(r => Path.GetFileName(r).Equals(Projection, StringComparison.OrdinalIgnoreCase))
            .Select(r => Path.Combine(Path.GetDirectoryName(r)!, Path.GetFileNameWithoutExtension(r), "Themes", "generic.xaml")).Where(File.Exists);

    /// <summary>WinUI's projection, the assembly its types are read from.</summary>
    public const string Projection = "Microsoft.WinUI.dll";

    public override bool IsMarkupExtension(TypeSystem types, ClrType type) =>
        type.Kind == ClrTypeKind.Class && !type.IsAbstract && !type.IsStatic && types.IsA(type, "Microsoft.UI.Xaml.Markup.MarkupExtension");

    public override bool CanCreate(TypeSystem types, ClrType type) => base.CanCreate(types, type) && !NoXamlMakes(type);

    protected override IReadOnlyList<XamlProperty> ReadProperties(TypeSystem types, ClrType type) =>
        ReadRegistered(types, type, XamlPropertyKind.Dependency, field => IsRegistration(types, field, DependencyProperty), properties: true);

    protected override IReadOnlyList<XamlProperty> ReadAttached(TypeSystem types, ClrType owner) =>
        ReadAttachedPairs(owner, field => IsRegistration(types, field, DependencyProperty), parameter => TakesElement(types, parameter), gettersAlone: false, properties: true);

    private static bool TakesElement(TypeSystem types, TypeRef parameter) =>
        parameter.Name is "System.Object" || (types.Resolve(parameter) is { } type && types.IsA(type, DependencyObject));
}
