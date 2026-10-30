using Passenger.Text;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Projects;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml;

/// <summary>A resource a XAML file defines with <c>x:Key</c>: its key and where it is written, what element makes it,
/// the theme variant it is for (the <c>x:Key</c> of the theme dictionary it is in: Dark, Light) with the dictionaries it
/// is grouped with, and its colour when it is a colour or a solid brush.</summary>
public sealed record ResourceDefinition(string Key, string Path, TextRange KeyRange, string ElementName, TextRange ElementRange, string? Variant,
    TextRange? Group, string? Colour)
{
    /// <summary>Where its colour is written (its Color attribute's value, a Color element's text).</summary>
    public TextRange? ColourRange { get; init; }

    /// <summary>Where the definition's own element starts, its key range's line found from it.</summary>
    public int Start => ElementRange.Start;
}

/// <summary>What the project needs to know of one of its XAML files without reading it against types: its
/// <c>x:Class</c>, its resources, the files it merges in, its named elements, the style classes it uses, and whether it
/// is the application's. Made from each parse, of a file open or on disk.</summary>
public sealed record XamlFileSummary(string Path, string? Class, IReadOnlyList<ResourceDefinition> Resources, IReadOnlyList<string> Includes,
    IReadOnlyList<(string Name, TextRange Range, string Element)> Names, IReadOnlyList<string> StyleClasses, bool IsApplication,
    IReadOnlyList<ThemeGroup> ThemeGroups)
{
    /// <summary>Whether it merges in a dictionary of an assembly outside the solution (a library's theme), whose keys are
    /// not known here.</summary>
    public bool ForeignIncludes { get; init; }

    /// <summary>The root element's namespace URI and name: the type an <c>x:Class</c> no C# declares derives from.</summary>
    public (string Uri, string Name)? Root { get; init; }

    /// <summary>Where the <c>x:Class</c> value is written, where a class no C# declares is declared.</summary>
    public SourcePlace? ClassPlace { get; init; }

    /// <summary>What the summary says, as one string, to tell whether a new one says anything new.</summary>
    public string Signature => signature ??= string.Join('|', [Class ?? string.Empty, Root?.ToString() ?? string.Empty, IsApplication.ToString(), ForeignIncludes.ToString(),
        .. Resources.Select(r => $"{r.Key}:{r.ElementName}:{r.Variant}:{r.Colour}:{r.KeyRange.Start}:{r.ElementRange.End}"),
        .. Includes, .. Names.Select(n => $"{n.Name}:{n.Element}"), .. StyleClasses, .. ThemeGroups.Select(g => string.Join(',', g.Variants))]);

    private string? signature;

    /// <summary>Summarises a parse; <paramref name="resolve"/> turns an include's <c>Source</c> into its file.</summary>
    public static XamlFileSummary Of(string path, XmlTree tree, Func<string, string, string?> resolve)
    {
        List<ResourceDefinition> resources = [];
        List<string> includes = [];
        List<(string, TextRange, string)> names = [];
        HashSet<string> classes = new(StringComparer.Ordinal);
        List<ThemeGroup> groups = [];
        bool foreign = false;
        Dictionary<XmlElement, string> languagePrefixes = [];
        foreach (XmlElement element in tree.Elements)
        {
            string? x = LanguagePrefix(element, languagePrefixes);
            bool variantDictionary = element.Parent?.LocalName.EndsWith(".ThemeDictionaries", StringComparison.Ordinal) == true;
            if (x is not null && !variantDictionary && element.Attribute($"{x}:Key") is { } key && key.Value.Length > 0)
            {
                (string? variant, TextRange? group) = VariantOf(element, x);
                resources.Add(new ResourceDefinition(key.Value, path, key.ValueRange, element.LocalName, TextRange.FromBounds(element.Start, element.End),
                    variant, group, ColourOf(element, tree.Text)) { ColourRange = ColourRangeOf(element, tree.Text) });
            }
            if (element.LocalName.EndsWith(".ThemeDictionaries", StringComparison.Ordinal))
            {
                List<string> variants = [.. element.Children.Select(c => x is null ? null : c.Attribute($"{x}:Key")?.Value).OfType<string>()];
                groups.Add(new ThemeGroup(TextRange.FromBounds(element.Start, element.End), variants));
            }
            // Avalonia's includes, and WPF's merged dictionary by its Source.
            if (element.LocalName is "ResourceInclude" or "MergeResourceInclude" or "StyleInclude" or "ResourceDictionary" && element.Attribute("Source") is { } source
                && source.Value.Length > 0 && !source.Value.TrimStart().StartsWith('{'))
            {
                if (resolve(source.Value, path) is { } included)
                {
                    includes.Add(included);
                }
                else
                {
                    foreign = true;
                }
            }
            XmlAttribute? named = (x is not null ? element.Attribute($"{x}:Name") : null) ?? (element.IsPropertyElement ? null : element.Attribute("Name"));
            if (named is { } name && name.Value.Length > 0)
            {
                names.Add((name.Value, name.ValueRange, element.LocalName));
            }
            if (element.Attribute("Classes") is { } used)
            {
                classes.UnionWith(used.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(c => !c.StartsWith(':')));
            }
            if (element.LocalName == "Style" && element.Attribute("Selector") is { } selector)
            {
                classes.UnionWith(Selectors.Tokenize(selector.Value).Where(t => t.Kind == SelectorPart.Class && t.Text.Length > 0).Select(t => t.Text));
            }
        }
        XmlElement? root = tree.Root;
        string? rootPrefix = root is null ? null : LanguagePrefix(root, languagePrefixes);
        XmlAttribute? classAttribute = root is not null && rootPrefix is not null ? root.Attribute($"{rootPrefix}:Class") : null;
        string? rootUri = root?.Attribute(root.Prefix.Length == 0 ? "xmlns" : "xmlns:" + root.Prefix)?.Value;
        return new XamlFileSummary(path, classAttribute?.Value, resources, includes, names, [.. classes], root?.LocalName == "Application", groups)
        {
            ForeignIncludes = foreign,
            Root = root is not null && rootUri is not null ? (rootUri, root.LocalName) : null,
            ClassPlace = classAttribute is null ? null : PlaceOf(path, tree.Text, classAttribute.ValueStart),
        };
    }

    /// <summary>An offset's line and column from zero, the lines counted as Roslyn counts them (<see cref="SourcePlace"/>).</summary>
    private static SourcePlace PlaceOf(string path, string text, int offset)
    {
        (int line, int column) = RoslynLines.PlaceOf(text, offset);
        return new SourcePlace(path, line, column);
    }

    /// <summary>The prefix XAML's language namespace has at an element, found once per element.</summary>
    private static string? LanguagePrefix(XmlElement element, Dictionary<XmlElement, string> known)
    {
        for (XmlElement? at = element; at is not null; at = at.Parent)
        {
            if (known.TryGetValue(at, out string? found))
            {
                return found;
            }
            foreach (XmlAttribute attribute in at.Attributes)
            {
                if (attribute.Prefix == "xmlns" && attribute.Value == XamlDialect.XamlUri)
                {
                    known[element] = attribute.LocalName;
                    return attribute.LocalName;
                }
            }
        }
        return null;
    }

    /// <summary>The theme variant a keyed element is for: the key of the dictionary it is in under a
    /// <c>ThemeDictionaries</c> property element, and that group's place.</summary>
    private static (string? Variant, TextRange? Group) VariantOf(XmlElement element, string x)
    {
        for (XmlElement? at = element.Parent; at is not null; at = at.Parent)
        {
            if (at.Parent is { } holder && holder.LocalName.EndsWith(".ThemeDictionaries", StringComparison.Ordinal) && at.Attribute($"{x}:Key") is { } variant)
            {
                return (StripStatic(variant.Value), TextRange.FromBounds(holder.Start, holder.End));
            }
        }
        return (null, null);
    }

    /// <summary>A variant written as <c>{x:Static ThemeVariant.Dark}</c> is <c>Dark</c>.</summary>
    private static string StripStatic(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            int dot = trimmed.LastIndexOf('.');
            return dot < 0 ? trimmed : trimmed[(dot + 1)..^1].Trim();
        }
        return trimmed;
    }

    /// <summary>The colour a colour or solid brush resource is written as, if it is a literal.</summary>
    private static string? ColourOf(XmlElement element, string text) => element.LocalName switch
    {
        "SolidColorBrush" => element.Attribute("Color")?.Value ?? TextOf(element, text),
        "Color" => TextOf(element, text),
        _ => null,
    };

    private static TextRange? ColourRangeOf(XmlElement element, string text)
    {
        if (element.LocalName == "SolidColorBrush" && element.Attribute("Color") is { } attribute)
        {
            return attribute.ValueRange;
        }
        if (element.LocalName is "SolidColorBrush" or "Color" && element.Texts.Count == 1 && element.Children.Count == 0)
        {
            TextRange range = element.Texts[0].Range;
            int start = range.Start;
            int end = range.End;
            while (start < end && char.IsWhiteSpace(text[start]))
            {
                start++;
            }
            while (end > start && char.IsWhiteSpace(text[end - 1]))
            {
                end--;
            }
            return TextRange.FromBounds(start, end);
        }
        return null;
    }

    private static string? TextOf(XmlElement element, string text) =>
        element.Texts.Count == 1 && element.Children.Count == 0 ? text[element.Texts[0].Start..element.Texts[0].End].Trim() : null;
}

/// <summary>A <c>ThemeDictionaries</c> property element: where it stands and the variants its dictionaries are keyed by.</summary>
public sealed record ThemeGroup(TextRange Range, IReadOnlyList<string> Variants);

/// <summary>A project's XAML as a whole: its types, its dialect, every XAML file's summary, the assets it embeds
/// (<c>AvaloniaResource</c>), and the resource keys its themes compile in. Immutable; a change makes a new one.</summary>
public sealed class XamlProject
{
    public XamlProject(ProjectInputs inputs, TypeSystem types, XamlDialect dialect, IReadOnlyDictionary<string, XamlFileSummary> files,
        IReadOnlyList<string> assets, IReadOnlySet<string> themeKeys, IReadOnlyList<(string AssemblyName, string Folder)> referenced)
    {
        Inputs = inputs;
        Types = types;
        Dialect = dialect;
        Files = files;
        Assets = assets;
        ThemeKeys = themeKeys;
        Referenced = referenced;
    }

    public ProjectInputs Inputs { get; }

    public TypeSystem Types { get; }

    public XamlDialect Dialect { get; }

    /// <summary>Every XAML file of the project, by its full path.</summary>
    public IReadOnlyDictionary<string, XamlFileSummary> Files { get; }

    /// <summary>The project's embedded assets, as paths from its folder with forward slashes.</summary>
    public IReadOnlyList<string> Assets { get; }

    /// <summary>The keys the themes it references define in their compiled XAML (Fluent's brushes and colours): known to
    /// exist though no file here defines them.</summary>
    public IReadOnlySet<string> ThemeKeys { get; }

    /// <summary>The projects it references, by assembly name and folder, whose files <c>avares://</c> reaches too.</summary>
    public IReadOnlyList<(string AssemblyName, string Folder)> Referenced { get; }

    public string AssemblyName => Inputs.AssemblyName;

    public string Folder => Inputs.Folder;

    public XamlFileSummary? Application => Files.Values.FirstOrDefault(f => f.IsApplication);

    /// <summary>Whether a <c>{Binding}</c> compiles by default: the project's <c>AvaloniaUseCompiledBindingsByDefault</c>,
    /// else Avalonia's own default, on from Avalonia 12; never in a dialect that compiles no binding (WPF's).</summary>
    public bool CompilesBindings => compiles ??= Dialect.CompilesBindings && (Inputs.PropertyValue("AvaloniaUseCompiledBindingsByDefault") is { } set
        ? string.Equals(set, "true", StringComparison.OrdinalIgnoreCase)
        : Inputs.PackageVersions.TryGetValue("Avalonia", out string? version) && int.TryParse(version.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture, out int major) && major >= 12);

    private bool? compiles;

    /// <summary>A project with the file of <paramref name="summary"/> summarised afresh (an open file's text as it stands).</summary>
    public XamlProject With(XamlFileSummary summary)
    {
        Dictionary<string, XamlFileSummary> files = new(Files, StringComparer.Ordinal) { [summary.Path] = summary };
        return new XamlProject(Inputs, Types, Dialect, files, Assets, ThemeKeys, Referenced);
    }

    /// <summary>The file an include's or asset's <c>Source</c> names from a file: <c>avares://Assembly/path</c> of this
    /// project or one it references, WPF's <c>pack://application:,,,/</c> and <c>/Assembly;component/path</c> likewise, a
    /// path from the project's folder (<c>/Theme/Tokens.axaml</c>), or one from the file's own folder. Null for one in no
    /// project here, or an assembly's own resource.</summary>
    public string? ResolveSource(string source, string fromFile)
    {
        string value = source.Trim();
        const string Pack = "pack://application:,,,";
        if (value.StartsWith(Pack, StringComparison.OrdinalIgnoreCase))
        {
            value = "/" + value[Pack.Length..].TrimStart('/');
        }
        int component = value.StartsWith('/') ? value.IndexOf(";component/", StringComparison.OrdinalIgnoreCase) : -1;
        if (component > 0)
        {
            // /Assembly[;Version][;Key];component/path
            string named = value[1..component].Split(';')[0];
            string inside = Uri.UnescapeDataString(value[(component + ";component/".Length)..]);
            string? folder = string.Equals(named, AssemblyName, StringComparison.OrdinalIgnoreCase) ? Folder
                : Referenced.FirstOrDefault(r => string.Equals(r.AssemblyName, named, StringComparison.OrdinalIgnoreCase)).Folder;
            return folder is null ? null : Path.GetFullPath(Path.Combine(folder, inside));
        }
        if (value.StartsWith("avares://", StringComparison.Ordinal))
        {
            string rest = value["avares://".Length..];
            int slash = rest.IndexOf('/');
            if (slash < 0)
            {
                return null;
            }
            string assembly = rest[..slash];
            string relative = Uri.UnescapeDataString(rest[(slash + 1)..]);
            if (assembly == AssemblyName)
            {
                return Path.GetFullPath(Path.Combine(Folder, relative));
            }
            foreach ((string name, string folder) in Referenced)
            {
                if (name == assembly)
                {
                    return Path.GetFullPath(Path.Combine(folder, relative));
                }
            }
            return null;
        }
        if (value.Contains("://", StringComparison.Ordinal) || value.Length == 0)
        {
            return null;
        }
        return value.StartsWith('/')
            ? Path.GetFullPath(Path.Combine(Folder, value.TrimStart('/')))
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fromFile)!, value));
    }

    /// <summary>The summaries a file's resources come from: itself, what it merges in (and they in turn), and the
    /// application's with what it merges in, nearest first, each once.</summary>
    public IReadOnlyList<XamlFileSummary> ResourceScopes(XamlFileSummary file)
    {
        List<XamlFileSummary> scopes = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        Walk(file);
        if (Application is { } application)
        {
            Walk(application);
        }
        return scopes;

        void Walk(XamlFileSummary summary)
        {
            if (!seen.Add(summary.Path))
            {
                return;
            }
            scopes.Add(summary);
            foreach (string included in summary.Includes)
            {
                if (Files.TryGetValue(included, out XamlFileSummary? next))
                {
                    Walk(next);
                }
            }
        }
    }

    /// <summary>Every definition of a key a file can reach, nearest first.</summary>
    public IReadOnlyList<ResourceDefinition> Definitions(XamlFileSummary file, string key) =>
        [.. ResourceScopes(file).SelectMany(s => s.Resources.Where(r => r.Key == key))];

    /// <summary>The variants a key is missing from in a theme dictionary group that defines it in another: Light, for a
    /// key only Dark defines.</summary>
    public IReadOnlyList<(ThemeGroup Group, string Missing, ResourceDefinition Defined)> MissingVariants(XamlFileSummary file, string key)
    {
        List<(ThemeGroup, string, ResourceDefinition)> missing = [];
        foreach (XamlFileSummary scope in ResourceScopes(file))
        {
            foreach (ThemeGroup group in scope.ThemeGroups)
            {
                List<ResourceDefinition> inGroup = [.. scope.Resources.Where(r => r.Key == key && r.Group == group.Range)];
                if (inGroup.Count == 0)
                {
                    continue;
                }
                foreach (string variant in group.Variants)
                {
                    if (!inGroup.Any(r => r.Variant == variant))
                    {
                        missing.Add((group, variant, inGroup[0]));
                    }
                }
            }
        }
        return missing;
    }
}
