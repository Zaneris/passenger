using Passenger.Text;
using Passenger.Xaml.Features;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Localization;

/// <summary>A XAML file's read of a localized string: an <c>{x:Static p:Resources.Welcome}</c> of a property of a class a .resx
/// generates; where the property's name is written, the key it reads, the class, and how its files are found (the base paths
/// beside its Designer file, else the name its ResourceManager looks it up by under its project's folder).</summary>
public sealed record LocalizedStatic(TextRange Range, string Key, ClrType Type, IReadOnlyList<string> Beside, string Manager, string ProjectFolder, string RootNamespace)
{
    /// <summary>The resource's files, an open one's as the editor has it (<paramref name="open"/>); null where none are found.</summary>
    public ResourceSet? SetOf(Func<string, TextSnapshot?>? open) => ResourceFiles.Find(Beside, ProjectFolder, RootNamespace, Manager, open);
}

/// <summary>Localized strings in XAML, Avalonia's and WPF's usual way: a neutral <c>Resources.resx</c>, its satellites and the class
/// generated over it (ResXFileCodeGenerator's Designer class, Arcade's), read as <c>{x:Static p:Resources.Key}</c>. A class is one a
/// .resx generates when it has a static ResourceManager property of that type; its property reads the key its getter gives
/// ResourceManager.GetString (else its own name). The files are read by <see cref="ResourceFiles"/>.</summary>
public static class XamlLocalization
{
    private const string ResourceManager = "System.Resources.ResourceManager";

    /// <summary>Every <c>{x:Static}</c> of the file that reads a generated resource class's string, nested ones included, and the
    /// <c>&lt;x:Static Member="…"/&gt;</c> element; in the order they are written.</summary>
    public static IReadOnlyList<LocalizedStatic> Reads(XamlFile file)
    {
        List<LocalizedStatic> reads = [];
        Dictionary<ClrType, LocalizedClass?> classes = [];
        foreach (XmlElement element in file.Tree.Elements)
        {
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (!attribute.HasValue || !attribute.RawValue.Contains("Static", StringComparison.Ordinal) && !(element.LocalName == "Static" && attribute.Name == "Member"))
                {
                    continue;
                }
                if (MarkupExtension.Parse(file.Text, attribute.ValueStart, attribute.ValueEnd) is { } extension)
                {
                    Visit(file, element, extension, classes, reads);
                }
                else if (element.LocalName == "Static" && attribute.Name == "Member" && IsLanguage(file, element, element.Prefix)
                    && Read(file, element, attribute.Value, attribute.ValueStart, classes) is { } read)
                {
                    reads.Add(read);
                }
            }
        }
        return reads;
    }

    /// <summary>The read whose property's name an offset is on; null for none.</summary>
    public static LocalizedStatic? At(XamlFile file, int offset) => Reads(file).FirstOrDefault(r => r.Range.Start <= offset && offset <= r.Range.End);

    /// <summary>A warning where a read's key a language of its files lacks (no entry, or an XLIFF unit still new), one a language,
    /// each carrying the gap its fix gives; the files read with an open one's text as the editor has it.</summary>
    public static IReadOnlyList<XamlDiagnostic> Findings(IReadOnlyList<LocalizedStatic> reads, Func<string, TextSnapshot?>? open)
    {
        List<XamlDiagnostic> found = [];
        Dictionary<ClrType, ResourceSet?> sets = [];
        foreach (LocalizedStatic read in reads)
        {
            if (!sets.TryGetValue(read.Type, out ResourceSet? set))
            {
                sets[read.Type] = set = read.SetOf(open);
            }
            foreach (ResourceGap gap in set?.Lacking(read.Key) ?? [])
            {
                found.Add(new XamlDiagnostic(read.Range, XamlSeverity.Warning, gap.Message) { Gap = gap });
            }
        }
        return found;
    }

    /// <summary>The text of the key each property of a class a .resx generates reads: its neutral file's, else a language's;
    /// null for another member or class.</summary>
    internal static Func<ClrMember, string?> TextsOf(XamlFile file, ClrType type)
    {
        if (Class(file, type) is not { } generated || ResourceFiles.Find(generated.Beside, generated.ProjectFolder, generated.RootNamespace, generated.Manager) is not { } set)
        {
            return _ => null;
        }
        return property => generated.Read(property) is { } key
            ? (set.Neutral?.Entry(key) ?? set.Files.Select(f => f.Entry(key)).FirstOrDefault(e => e is { Translated: true }))?.Value
            : null;
    }

    private static void Visit(XamlFile file, XmlElement element, MarkupExtension extension, Dictionary<ClrType, LocalizedClass?> classes, List<LocalizedStatic> reads)
    {
        if (!StackLeft.Enough())
        {
            return;
        }
        if (extension.LocalName == "Static" && IsLanguage(file, element, extension.Prefix)
            && (extension.Argument(null) ?? extension.Argument("Member")) is { Nested: null, Value.Length: > 0 } member
            && Read(file, element, member.Value, member.ValueStart, classes) is { } read)
        {
            reads.Add(read);
        }
        foreach (MarkupArgument argument in extension.Arguments)
        {
            if (argument.Nested is { } nested)
            {
                Visit(file, element, nested, classes, reads);
            }
        }
    }

    private static bool IsLanguage(XamlFile file, XmlElement element, string prefix) =>
        prefix.Length > 0 && file.KindOf(file.UriOf(element, prefix)) == XamlNamespaceKind.Language;

    /// <summary>A member's name (<c>p:Resources.Welcome</c>) written at a place, as a read of a generated class's string.</summary>
    private static LocalizedStatic? Read(XamlFile file, XmlElement element, string value, int start, Dictionary<ClrType, LocalizedClass?> classes)
    {
        int dot = value.LastIndexOf('.');
        if (dot <= 0 || file.ResolveType(element, value[..dot]) is not { } type)
        {
            return null;
        }
        if (!classes.TryGetValue(type, out LocalizedClass? generated))
        {
            classes[type] = generated = Class(file, type);
        }
        string name = value[(dot + 1)..];
        if (generated is null || type.Members.FirstOrDefault(m => m.Kind == MemberKind.Property && m.IsStatic && m.Name == name) is not { } property
            || generated.Read(property) is not { } key)
        {
            return null;
        }
        return new LocalizedStatic(new TextRange(start + dot + 1, name.Length), key, type, generated.Beside, generated.Manager, generated.ProjectFolder, generated.RootNamespace);
    }

    /// <summary>A class a .resx generates (one with a static ResourceManager property of that type), with how its files are
    /// found; null for any other class.</summary>
    private static LocalizedClass? Class(XamlFile file, ClrType type)
    {
        if (type.Members.FirstOrDefault(m => m.Kind == MemberKind.Property && m.IsStatic && m.Name == "ResourceManager"
            && (m.Type.Name == ResourceManager || file.Types.Resolve(m.Type)?.FullName == ResourceManager)) is not { } manager)
        {
            return null;
        }
        // Beside its Designer file, the .resx it was generated from.
        IEnumerable<string> declared = file.Types.PlacesOf(type.FullName).Select(p => p.Path).Prepend(type.Origin.Place?.Path).OfType<string>();
        string[] beside = [.. declared.Where(d => d.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)).Select(d => d[..^".Designer.cs".Length]).Distinct(StringComparer.Ordinal)];
        XamlProject project = file.Project;
        bool own = type.Origin.AssemblyName == project.AssemblyName;
        string folder = own ? project.Folder : project.Referenced.FirstOrDefault(r => r.AssemblyName == type.Origin.AssemblyName).Folder ?? project.Folder;
        string root = own ? project.Inputs.PropertyValue("RootNamespace") ?? project.AssemblyName : type.Origin.AssemblyName;
        return new LocalizedClass(beside, manager.Constant as string ?? type.FullName, folder, root);
    }

    /// <summary>A generated resource class: the base paths beside its Designer file, the name its ResourceManager is made with,
    /// its project's folder and root namespace.</summary>
    private sealed record LocalizedClass(IReadOnlyList<string> Beside, string Manager, string ProjectFolder, string RootNamespace)
    {
        /// <summary>The key a string property reads: the one its getter gives, else its own name; null for another member.</summary>
        public string? Read(ClrMember property) => property.Type.Name == "System.String" ? property.Constant as string ?? property.Name : null;
    }
}
