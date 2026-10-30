using Passenger.Host;
using Passenger.Text;
using Passenger.Xaml.Syntax;
using Passenger.Core;

namespace Passenger.App.MSBuild;

public enum MSBuildItemKind
{
    Element,
    Property,
    Item,
    Metadata,
    Task,
    Attribute,
    Value,
    PropertyReference,
    ItemReference,
    Target,
    File,
    Folder,
    Package,
    Version,
    Sdk,
    ClosingTag,
}

/// <summary>A package id offered, with the version a reference to it takes when it goes in (the feed's latest).</summary>
public sealed record PackageChoice(string Id, string Version);

/// <summary>One thing completion offers in an MSBuild file: what the list shows, what goes in (a snippet's <c>$0</c> is
/// where the caret stays), whether completion opens again after it (a property's values once its tags are in), and for a
/// package id the version its reference takes.</summary>
public sealed record MSBuildItem(string Label, MSBuildItemKind Kind, string Insert, string? Detail = null, string? Documentation = null,
    bool IsSnippet = false, bool Reopen = false)
{
    public string? SortText { get; init; }

    public PackageChoice? Package { get; init; }
}

/// <summary>What a list still needs from the folder's NuGet feeds: the ids that match what is typed (no <see cref="Id"/>),
/// or one package's versions; and the element it is for.</summary>
public sealed record PackageAsk(string? Id, string Typed, XmlElement Element);

/// <summary>A completion list: its items, where the text they replace starts (it ends at the caret), the characters beyond a
/// name's that narrow it rather than close it (a package id's dots, a path's), what it asks of the feeds, and whether
/// typing on asks again (a feed's search).</summary>
public sealed record MSBuildCompletionList(int Start, IReadOnlyList<MSBuildItem> Items, string WordCharacters = "", PackageAsk? Packages = null, bool Incomplete = false);

/// <summary>What completion reads: the file's path and markup as typed, MSBuild's schemas, the project's last evaluation
/// (names and values only: it may be a pause behind the text) and the SDKs installed.</summary>
public sealed record MSBuildContext(string Path, XmlTree Tree, MSBuildSchema Schema, MSBuildInsight? Insight, IReadOnlyList<string> Sdks);

/// <summary>Completion in an MSBuild file, from its text as typed (Passenger.Xaml's tolerant XML tree) with MSBuild's schemas
/// and the project's evaluation: after <c>&lt;</c> the elements its parent takes (structural ones, properties, item types,
/// metadata, tasks); in a start tag its attributes; a value by what it holds (a property's allowed values, target
/// frameworks, SDKs, paths from the disk, target names, package ids and versions from the feeds); and <c>$(</c>,
/// <c>@(</c> and <c>%(</c> the names the evaluation knows. An end tag's name after <c>&lt;/</c>.</summary>
public static class MSBuildCompletion
{
    /// <summary>The item metadata every item has, which MSBuild gives itself.</summary>
    public static readonly string[] WellKnownMetadata =
    [
        "FullPath", "RootDir", "Filename", "Extension", "RelativeDir", "Directory", "RecursiveDir", "Identity", "ModifiedTime", "CreatedTime",
        "AccessedTime", "DefiningProjectFullPath", "DefiningProjectDirectory", "DefiningProjectName", "DefiningProjectExtension",
    ];

    /// <summary>The targets every SDK-style project has, offered before the evaluation is in.</summary>
    private static readonly string[] CommonTargets = ["Build", "Rebuild", "Clean", "Restore", "Publish", "Pack", "Test", "BeforeBuild", "AfterBuild", "CoreCompile"];

    /// <summary>SDKs that come from NuGet rather than the .NET install, which a project may name all the same.</summary>
    private static readonly string[] PackageSdks = ["MSTest.Sdk", "Aspire.AppHost.Sdk", "Microsoft.Build.NoTargets", "Microsoft.Build.Traversal", "Microsoft.Build.Sql"];

    /// <summary>The properties a project sets most, which the list offers first.</summary>
    private static readonly string[] CommonProperties =
    [
        "TargetFramework", "TargetFrameworks", "OutputType", "Nullable", "ImplicitUsings", "LangVersion", "RootNamespace", "AssemblyName",
        "Version", "IsPackable", "GenerateDocumentationFile", "TreatWarningsAsErrors", "NoWarn", "RuntimeIdentifier", "SelfContained",
        "PublishAot", "InvariantGlobalization", "UserSecretsId", "ManagePackageVersionsCentrally", "AllowUnsafeBlocks", "IsTestProject",
    ];

    /// <summary>The item types a project holds most, offered first.</summary>
    private static readonly string[] CommonItems =
    [
        "PackageReference", "ProjectReference", "Compile", "None", "Content", "EmbeddedResource", "Reference", "FrameworkReference", "Using",
        "InternalsVisibleTo", "Folder", "PackageVersion", "AdditionalFiles",
    ];

    /// <summary>An item element's own attributes, which are no metadata.</summary>
    private static readonly HashSet<string> ItemAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Include", "Exclude", "Remove", "Update", "Condition", "Label", "KeepMetadata", "RemoveMetadata", "KeepDuplicates", "MatchOnMetadata", "MatchOnMetadataOptions",
    };

    public static MSBuildCompletionList? At(MSBuildContext context, int offset)
    {
        string text = context.Tree.Text;
        if (offset < 0 || offset > text.Length || context.Tree.InComment(offset))
        {
            return null;
        }
        if (Closing(context, offset) is { } closing)
        {
            return closing;
        }
        XmlPlace place = context.Tree.PlaceAt(offset);
        return place.Kind switch
        {
            XmlPlaceKind.StartTagName when place.Element is { } element => ElementNames(context, element, offset),
            XmlPlaceKind.InStartTag when place.Element is { } element && offset > element.NameEnd => Attributes(context, element, null, offset),
            XmlPlaceKind.AttributeName when place.Element is { } element => Attributes(context, element, place.Attribute, offset),
            XmlPlaceKind.AttributeValue when place.Attribute is { } attribute => AttributeValue(context, attribute, offset),
            XmlPlaceKind.Content when place.Element is { } element => ContentValue(context, element, offset),
            _ => null,
        };
    }

    /// <summary>After <c>&lt;/</c>, the name of the element it closes.</summary>
    private static MSBuildCompletionList? Closing(MSBuildContext context, int offset)
    {
        string text = context.Tree.Text;
        int at = offset;
        while (at > 0 && MarkupNames.IsNameChar(text[at - 1]))
        {
            at--;
        }
        if (at < 2 || text[at - 1] != '/' || text[at - 2] != '<')
        {
            return null;
        }
        XmlElement? open = context.Tree.ElementAt(at - 2);
        while (open is not null && open.EndTagStart is { } end && end != at - 2)
        {
            open = open.Parent;
        }
        return open is null ? null : new MSBuildCompletionList(at, [new MSBuildItem(open.Name, MSBuildItemKind.ClosingTag, open.Name + ">")]);
    }

    private static MSBuildCompletionList ElementNames(MSBuildContext context, XmlElement element, int offset)
    {
        bool fresh = !element.IsStartTagClosed && element.Attributes.Count == 0;
        List<MSBuildItem> items = [];
        MSBuildSchema schema = context.Schema;
        if (element.Parent is not { } parent)
        {
            items.Add(Structural("Project", schema.Project.Documentation, fresh));
            return new MSBuildCompletionList(element.NameStart, items);
        }
        switch (MSBuildSyntax.RoleOf(parent))
        {
            case MSBuildRole.Project or MSBuildRole.Structure:
                SchemaElement? shape = schema.Structure.GetValueOrDefault(parent.LocalName);
                if (shape is null)
                {
                    break;
                }
                items.AddRange(shape.Children.Select(c => Structural(c.Name, c.Documentation, fresh)));
                if (shape.TakesProperties)
                {
                    items.AddRange(PropertyNames(context, fresh));
                }
                if (shape.TakesItems)
                {
                    items.AddRange(ItemNames(context, fresh, definition: parent.LocalName == "ItemDefinitionGroup"));
                }
                if (shape.TakesTasks)
                {
                    items.AddRange(schema.Tasks.Values.Select(t => new MSBuildItem(t.Name, MSBuildItemKind.Task, fresh ? t.Name + " $0/>" : t.Name, null, t.Documentation, fresh, fresh)));
                }
                break;
            case MSBuildRole.Item:
                items.AddRange(Metadata(context, parent.LocalName).Select(m => new MSBuildItem(m.Name, MSBuildItemKind.Metadata,
                    fresh ? $"{m.Name}>$0</{m.Name}>" : m.Name, null, m.Documentation, fresh, fresh && m.Values.Count > 0)));
                break;
            case MSBuildRole.Task:
                items.Add(Structural("Output", null, fresh));
                break;
        }
        return new MSBuildCompletionList(element.NameStart, [.. items.DistinctBy(i => i.Label, StringComparer.OrdinalIgnoreCase)], ".");
    }

    /// <summary>A structural element, which goes in with what it always has: a Target's name, an Import's project.</summary>
    private static MSBuildItem Structural(string name, string? documentation, bool fresh)
    {
        (string insert, bool reopen) = name switch
        {
            "Target" => ("Target Name=\"$0\"></Target>", false),
            "Import" => ("Import Project=\"$0\" />", true),
            "When" => ("When Condition=\"$0\"></When>", false),
            "Sdk" => ("Sdk Name=\"$0\" />", true),
            "UsingTask" => ("UsingTask TaskName=\"$0\" />", false),
            "OnError" => ("OnError ExecuteTargets=\"$0\" />", true),
            "Output" => ("Output TaskParameter=\"$0\" />", false),
            _ => ($"{name}>$0</{name}>", false),
        };
        return new MSBuildItem(name, MSBuildItemKind.Element, fresh ? insert : name, null, documentation, fresh, fresh && reopen);
    }

    /// <summary>The properties a PropertyGroup may set: the schemas' documented ones and those the evaluation knows the
    /// project's files set, each with its value now.</summary>
    private static IEnumerable<MSBuildItem> PropertyNames(MSBuildContext context, bool fresh)
    {
        MSBuildInsight? insight = context.Insight;
        foreach (SchemaElement property in context.Schema.Properties.Values)
        {
            yield return Property(property.Name, property.Documentation, insight?.Property(property.Name)?.Value);
        }
        foreach (InsightProperty set in insight?.Properties.Where(p => p.Origin == PropertyOrigin.Set && !p.Name.StartsWith('_')
            && !context.Schema.Properties.ContainsKey(p.Name)) ?? [])
        {
            yield return Property(set.Name, null, set.Value);
        }

        MSBuildItem Property(string name, string? documentation, string? value) =>
            new(name, MSBuildItemKind.Property, fresh ? $"{name}>$0</{name}>" : name, Shorten(value), documentation, fresh,
                fresh && PropertyValues(context, name).Count > 0)
            {
                SortText = Ranked(CommonProperties, name, documentation is null ? 2 : 1),
            };
    }

    /// <summary>The item types an ItemGroup may hold: the schemas' and those the evaluation has; an ItemDefinitionGroup's
    /// take no Include, only the metadata they give every item of their type.</summary>
    private static IEnumerable<MSBuildItem> ItemNames(MSBuildContext context, bool fresh, bool definition)
    {
        IEnumerable<(string Name, string? Documentation)> known = context.Schema.Items.Values.Select(i => (i.Name, i.Documentation))
            .Concat(context.Insight?.ItemTypes.Where(t => !t.Name.StartsWith('_')).Select(t => (t.Name, (string?)null)) ?? []);
        foreach ((string name, string? documentation) in known.DistinctBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
        {
            int count = context.Insight?.ItemType(name)?.Count ?? 0;
            string insert = !fresh ? name : definition ? $"{name}>$0</{name}>" : $"{name} Include=\"$0\" />";
            yield return new MSBuildItem(name, MSBuildItemKind.Item, insert, count > 0 ? Plural.Of(count, "item") : null,
                documentation, fresh, fresh && !definition)
            {
                SortText = Ranked(CommonItems, name, documentation is null ? 2 : 1),
            };
        }
    }

    /// <summary>An item type's metadata: those the schemas give it (as elements or attributes), and those its items carry.</summary>
    private static IEnumerable<(string Name, string? Documentation, IReadOnlyList<string> Values)> Metadata(MSBuildContext context, string type)
    {
        List<(string, string?, IReadOnlyList<string>)> found = [];
        if (context.Schema.Items.GetValueOrDefault(type) is { } item)
        {
            found.AddRange(item.Children.Select(c => (c.Name, c.Documentation, c.Values)));
            found.AddRange(item.Attributes.Where(a => !ItemAttributes.Contains(a.Name)).Select(a => (a.Name, a.Documentation, a.Values)));
        }
        found.AddRange(context.Insight?.ItemType(type)?.Metadata.Select(m => (m, (string?)null, (IReadOnlyList<string>)[])) ?? []);
        // A package's assets, which the schemas leave as text, take NuGet's names.
        return found.DistinctBy(f => f.Item1, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.Item3.Count == 0 && f.Item1 is "IncludeAssets" or "ExcludeAssets" or "PrivateAssets" ? (f.Item1, f.Item2, Assets) : f);
    }

    private static readonly IReadOnlyList<string> Assets = ["all", "none", "compile", "runtime", "build", "buildtransitive", "contentfiles", "native", "analyzers"];

    private static MSBuildCompletionList Attributes(MSBuildContext context, XmlElement element, XmlAttribute? typing, int offset)
    {
        int start = typing?.NameStart ?? offset;
        bool valued = typing is { HasValue: true };
        HashSet<string> present = new(element.Attributes.Where(a => a != typing).Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        List<(string Name, string? Documentation, bool Reopen)> offered = [];
        MSBuildSchema schema = context.Schema;
        switch (MSBuildSyntax.RoleOf(element))
        {
            case MSBuildRole.Project or MSBuildRole.Structure:
                if (schema.Structure.GetValueOrDefault(element.LocalName) is { } shape)
                {
                    offered.AddRange(shape.Attributes.Select(a => (a.Name, a.Documentation, a.Values.Count > 0 || Opens(element.LocalName, a.Name))));
                }
                break;
            case MSBuildRole.Item:
                SchemaElement? item = schema.Items.GetValueOrDefault(element.LocalName);
                IEnumerable<SchemaAttribute> own = item?.Attributes ?? schema.Items["Compile"].Attributes.Where(a => ItemAttributes.Contains(a.Name));
                offered.AddRange(own.Select(a => (a.Name, a.Documentation, a.Values.Count > 0 || a.Name is "Include" or "Update" or "Version" or "VersionOverride")));
                offered.AddRange(Metadata(context, element.LocalName).Select(m => (m.Name, m.Documentation, m.Values.Count > 0)));
                break;
            case MSBuildRole.Task:
                if (schema.Tasks.GetValueOrDefault(element.LocalName) is { } task)
                {
                    offered.AddRange(task.Attributes.Select(a => (a.Name, a.Documentation, a.Values.Count > 0)));
                }
                break;
            case MSBuildRole.Property or MSBuildRole.Metadata:
                offered.Add(("Condition", "Optional expression evaluated to determine whether the property should be evaluated", false));
                offered.Add(("Label", "Optional expression. Used to identify or order system and user elements", false));
                break;
        }
        List<MSBuildItem> items = [.. offered.Where(o => !present.Contains(o.Name)).DistinctBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Select(o => new MSBuildItem(o.Name, MSBuildItemKind.Attribute, valued ? o.Name : $"{o.Name}=\"$0\"", null, o.Documentation, !valued, !valued && o.Reopen))];
        return new MSBuildCompletionList(start, items);
    }

    /// <summary>Structural attributes whose values completion fills: an SDK's name, an import's path, targets.</summary>
    private static bool Opens(string element, string attribute) => (element, attribute) switch
    {
        (_, "Sdk") or ("Sdk", "Name") or ("Import", "Project") => true,
        ("Target", "DependsOnTargets" or "BeforeTargets" or "AfterTargets") or ("Project", "DefaultTargets" or "InitialTargets") or ("OnError", "ExecuteTargets") => true,
        _ => false,
    };

    private static MSBuildCompletionList? AttributeValue(MSBuildContext context, XmlAttribute attribute, int offset)
    {
        XmlElement element = attribute.Owner;
        string text = context.Tree.Text;
        if (References(context, attribute.ValueStart, offset) is { } references)
        {
            return references;
        }
        string typed = text[attribute.ValueStart..offset];
        MSBuildRole role = MSBuildSyntax.RoleOf(element);
        string name = attribute.Name;
        if (name == "Condition")
        {
            return null;
        }
        if ((name == "Sdk" && element.LocalName is "Project" or "Import") || (element.LocalName == "Sdk" && name == "Name"))
        {
            return new MSBuildCompletionList(attribute.ValueStart,
                [.. context.Sdks.Concat(PackageSdks).Distinct(StringComparer.OrdinalIgnoreCase).Select(s => new MSBuildItem(s, MSBuildItemKind.Sdk, s))], ".");
        }
        if (element.LocalName == "Import" && name == "Project" && role == MSBuildRole.Structure)
        {
            return Paths(context, attribute.ValueStart, offset, IsImportable);
        }
        if (role == MSBuildRole.Item && MSBuildSyntax.PackageItems.Contains(element.LocalName))
        {
            if (name is "Include" or "Update")
            {
                return new MSBuildCompletionList(attribute.ValueStart, [], ".-_", new PackageAsk(null, typed, element), Incomplete: true);
            }
            if (name is "Version" or "VersionOverride" && PackageOf(element, text) is { } id)
            {
                return new MSBuildCompletionList(attribute.ValueStart, [], ".-+", new PackageAsk(id, typed, element));
            }
        }
        if (role == MSBuildRole.Item && name is "Include" or "Exclude" or "Remove" or "Update")
        {
            return Paths(context, attribute.ValueStart, offset, element.LocalName == "ProjectReference" ? MSBuildFiles.IsProject : _ => true);
        }
        if (MSBuildSyntax.NamesTargets(element, attribute) && name != "Name")
        {
            (_, TextRange entry) = MSBuildSyntax.EntryAt(text, attribute.ValueStart, attribute.ValueEnd, offset);
            IEnumerable<string> targets = context.Insight?.Targets.Select(t => t.Name).Where(t => !t.StartsWith('_')) ?? CommonTargets;
            return new MSBuildCompletionList(entry.Start, [.. targets.Order(StringComparer.OrdinalIgnoreCase).Select(t => new MSBuildItem(t, MSBuildItemKind.Target, t))]);
        }
        IReadOnlyList<string> values = role switch
        {
            MSBuildRole.Item => Metadata(context, element.LocalName).FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Values ?? [],
            MSBuildRole.Task => context.Schema.Tasks.GetValueOrDefault(element.LocalName)?.Attribute(name)?.Values ?? [],
            _ => context.Schema.Structure.GetValueOrDefault(element.LocalName)?.Attribute(name)?.Values ?? [],
        };
        return values.Count == 0 ? null : new MSBuildCompletionList(attribute.ValueStart, [.. values.Select(v => new MSBuildItem(v, MSBuildItemKind.Value, v))]);
    }

    /// <summary>A property's value or a metadata element's, as its text is typed.</summary>
    private static MSBuildCompletionList? ContentValue(MSBuildContext context, XmlElement element, int offset)
    {
        string text = context.Tree.Text;
        TextRange content = element.Content;
        if (content.Length == 0 && !element.IsStartTagClosed || offset < content.Start || element.Children.Count > 0)
        {
            return null;
        }
        if (References(context, content.Start, offset) is { } references)
        {
            return references;
        }
        int start = content.Start;
        while (start < offset && char.IsWhiteSpace(text[start]))
        {
            start++;
        }
        switch (MSBuildSyntax.RoleOf(element))
        {
            case MSBuildRole.Property:
                IReadOnlyList<(string Value, string? Detail)> values = PropertyValues(context, element.LocalName);
                if (values.Count == 0)
                {
                    return null;
                }
                if (element.LocalName.EndsWith('s') && element.LocalName is "TargetFrameworks" or "RuntimeIdentifiers")
                {
                    start = MSBuildSyntax.EntryAt(text, start, Math.Max(offset, content.End), offset).Range.Start;
                }
                return new MSBuildCompletionList(start, [.. values.Select((v, i) => new MSBuildItem(v.Value, MSBuildItemKind.Value, v.Value, v.Detail) { SortText = $"{i:D4}" })], ".-");
            case MSBuildRole.Metadata when element.Parent is { } item:
                if (MSBuildSyntax.PackageItems.Contains(item.LocalName) && element.LocalName is "Version" or "VersionOverride" && PackageOf(item, text) is { } id)
                {
                    return new MSBuildCompletionList(start, [], ".-+", new PackageAsk(id, text[start..offset], item));
                }
                IReadOnlyList<string> allowed = Metadata(context, item.LocalName).FirstOrDefault(m => m.Name.Equals(element.LocalName, StringComparison.OrdinalIgnoreCase)).Values ?? [];
                return allowed.Count == 0 ? null : new MSBuildCompletionList(start, [.. allowed.Select(v => new MSBuildItem(v, MSBuildItemKind.Value, v))], "-;");
        }
        return null;
    }

    /// <summary>The package an item element names, as written.</summary>
    private static string? PackageOf(XmlElement element, string text) =>
        (MSBuildSyntax.ValueOf(element, "Include", text) ?? MSBuildSyntax.ValueOf(element, "Update", text)) is { Length: > 0 } id && !MSBuildSyntax.IsExpression(id) ? id.Trim() : null;

    /// <summary>The values a property takes: the schemas' (an enumeration, or true and false), else the SDK's own
    /// (target frameworks the evaluation's SDK supports, output types, language versions).</summary>
    public static IReadOnlyList<(string Value, string? Detail)> PropertyValues(MSBuildContext context, string name)
    {
        if (context.Schema.Properties.GetValueOrDefault(name) is { Values.Count: > 0 } property)
        {
            return [.. property.Values.Select(v => (v, (string?)null))];
        }
        return name switch
        {
            "TargetFramework" or "TargetFrameworks" => context.Insight?.Raw.Frameworks is { Count: > 0 } known
                ? [.. known.Select(f => (f.Alias, (string?)f.Name))]
                : [("net10.0", ".NET 10.0"), ("net9.0", ".NET 9.0"), ("net8.0", ".NET 8.0"), ("netstandard2.1", ".NET Standard 2.1"), ("netstandard2.0", ".NET Standard 2.0"), ("net48", ".NET Framework 4.8")],
            "OutputType" => [("Exe", "Console application"), ("Library", "Class library"), ("WinExe", "Windows application"), ("Module", "Module")],
            "LangVersion" => [.. new[] { "latest", "latestMajor", "preview", "default", "14.0", "13.0", "12.0", "11.0", "10.0", "9.0", "8.0", "7.3" }.Select(v => (v, (string?)null))],
            "Configuration" => [("Debug", null), ("Release", null)],
            "Platform" or "PlatformTarget" => [("AnyCPU", null), ("x64", null), ("x86", null), ("ARM64", null)],
            "RuntimeIdentifier" or "RuntimeIdentifiers" => [.. new[] { "win-x64", "win-x86", "win-arm64", "osx-arm64", "osx-x64", "linux-x64", "linux-arm64", "linux-musl-x64" }
                .Select(v => (v, (string?)null))],
            _ => [],
        };
    }

    /// <summary>Inside <c>$(</c>, <c>@(</c> or <c>%(</c> before the caret: the names the evaluation knows, the closing
    /// bracket put in with the name unless it is there.</summary>
    private static MSBuildCompletionList? References(MSBuildContext context, int valueStart, int offset)
    {
        string text = context.Tree.Text;
        int at = offset;
        while (at > valueStart && (MSBuildSyntax.IsNameChar(text[at - 1]) || text[at - 1] == '.'))
        {
            at--;
        }
        int dot = text.IndexOf('.', at, offset - at);
        if (at - valueStart < 2 || text[at - 1] != '(' || text[at - 2] is not ('$' or '@' or '%') || (dot >= 0 && text[at - 2] != '%'))
        {
            return null;
        }
        char sigil = text[at - 2];
        string close = offset < text.Length && (text[offset] == ')' || MSBuildSyntax.IsNameChar(text[offset])) ? string.Empty : ")";
        MSBuildInsight? insight = context.Insight;
        List<MSBuildItem> items = [];
        switch (sigil)
        {
            case '$':
                IEnumerable<(string Name, string? Value, int Rank)> known = (insight?.Properties.Select(p => (p.Name, (string?)p.Value, Rank(p))) ?? [])
                    .Concat(context.Schema.Properties.Keys.Select(n => (n, (string?)null, 3)));
                items.AddRange(known.DistinctBy(k => k.Name, StringComparer.OrdinalIgnoreCase).Select(k =>
                    new MSBuildItem(k.Name, MSBuildItemKind.PropertyReference, k.Name + close, Shorten(k.Value), context.Schema.Properties.GetValueOrDefault(k.Name)?.Documentation)
                    {
                        SortText = $"{k.Rank}{k.Name}",
                    }));
                break;
            case '@':
                IEnumerable<string> types = (insight?.ItemTypes.Select(t => t.Name) ?? []).Concat(context.Schema.Items.Keys);
                items.AddRange(types.Distinct(StringComparer.OrdinalIgnoreCase).Select(t => new MSBuildItem(t, MSBuildItemKind.ItemReference, t + close,
                    insight?.ItemType(t) is { } known ? Plural.Of(known.Count, "item") : null) { SortText = (t.StartsWith('_') ? "1" : "0") + t }));
                break;
            case '%':
                if (dot >= 0)
                {
                    string type = text[at..dot];
                    items.AddRange(WellKnownMetadata.Concat(Metadata(context, type).Select(m => m.Name)).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(m => new MSBuildItem(m, MSBuildItemKind.Metadata, m + close)));
                    return new MSBuildCompletionList(dot + 1, items);
                }
                items.AddRange(WellKnownMetadata.Select(m => new MSBuildItem(m, MSBuildItemKind.Metadata, m + close)));
                break;
        }
        return new MSBuildCompletionList(at, items);

        static int Rank(InsightProperty property) => property.Origin switch
        {
            PropertyOrigin.Set => property.Name.StartsWith('_') ? 2 : 0,
            PropertyOrigin.Global or PropertyOrigin.Reserved => 1,
            _ => 4,
        };
    }

    /// <summary>The folders and files under the folder a path typed so far names, relative to the MSBuild file's own: one
    /// segment at a time, a folder going in with its separator and the list opening again on what is in it.</summary>
    private static MSBuildCompletionList? Paths(MSBuildContext context, int valueStart, int offset, Func<string, bool> wanted)
    {
        string text = context.Tree.Text;
        int entry = valueStart;
        for (int at = offset - 1; at >= valueStart; at--)
        {
            if (text[at] == ';')
            {
                entry = at + 1;
                break;
            }
        }
        string typed = text[entry..offset];
        if (MSBuildSyntax.IsExpression(typed) || typed.IndexOfAny(['*', '?']) >= 0)
        {
            return null;
        }
        int slash = typed.LastIndexOfAny(['/', '\\']);
        string folderPart = slash < 0 ? string.Empty : typed[..(slash + 1)];
        char separator = typed.Contains('\\') || text[valueStart..Math.Max(valueStart, offset)].Contains('\\') ? '\\' : '/';
        string folder;
        try
        {
            folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(context.Path)!, folderPart.Replace('\\', '/')));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        List<MSBuildItem> items = [new MSBuildItem("..", MSBuildItemKind.Folder, ".." + separator, null, null, false, true) { SortText = "3" }];
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(folder))
            {
                string name = Path.GetFileName(sub);
                if (Stamps.Counts(sub, folder: true))
                {
                    items.Add(new MSBuildItem(name, MSBuildItemKind.Folder, name + separator, null, null, false, true) { SortText = "1" + name });
                }
            }
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(file);
                if (Stamps.Counts(file, folder: false) && wanted(file) && !Path.GetFullPath(file).Equals(Path.GetFullPath(context.Path), StringComparison.Ordinal))
                {
                    items.Add(new MSBuildItem(name, MSBuildItemKind.File, name) { SortText = "2" + name });
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A folder not there (yet) has nothing to offer but the way up.
        }
        return new MSBuildCompletionList(entry + slash + 1, items, ".-_ ");
    }

    private static bool IsImportable(string path) => Path.GetExtension(path).ToLowerInvariant() is ".props" or ".targets" or ".proj" or ".csproj";

    /// <summary>A name's place in the list: the common ones first, in their own order, then the rest by rank and name.</summary>
    private static string Ranked(string[] common, string name, int rank) =>
        Array.IndexOf(common, name) is var at and >= 0 ? $"0{at:D3}" : $"{rank}{name}";

    private static string? Shorten(string? value) =>
        value is null || value.Trim().Length == 0 ? null : MSBuildNavigation.Shown(value) is var flat && flat.Length > 48 ? flat[..47] + "…" : flat;
}
