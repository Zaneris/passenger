using System.Text;
using Passenger.Host;
using Passenger.Text;
using Passenger.Xaml.Syntax;

namespace Passenger.App.MSBuild;

public enum MSBuildSymbolKind
{
    Property,
    Item,
    Metadata,
    Target,
    Task,
    Element,
    Attribute,
    Import,
    Sdk,
    File,
}

/// <summary>What a name at an offset of an MSBuild file is: a property (set or referenced), an item type, a piece of
/// metadata, a target, a task, a structural element or attribute, an import, an SDK or a file an item names; with the
/// element it is on, where it is written and, for an attribute or metadata, the element or item type it belongs to.</summary>
public sealed record MSBuildSymbol(MSBuildSymbolKind Kind, string Name, TextRange Range, XmlElement? Element, string? Owner = null);

/// <summary>A place Go to Declaration offers: what it is, and where.</summary>
public sealed record MSBuildTarget(string Title, MSBuildPlace Place);

/// <summary>Hover and Go to Declaration in an MSBuild file: the name at an offset read from the text as typed, described
/// from the project's evaluation (a property's value and where it was last set, an item element's items, a target's
/// definitions, an import's file) and MSBuild's schemas' documentation, and the places it leads to, the SDK's own files
/// among them.</summary>
public static class MSBuildNavigation
{
    public static MSBuildSymbol? At(XmlTree tree, int offset)
    {
        string text = tree.Text;
        if (offset < 0 || offset > text.Length || tree.InComment(offset))
        {
            return null;
        }
        XmlPlace place = tree.PlaceAt(offset);
        switch (place.Kind)
        {
            case XmlPlaceKind.StartTagName or XmlPlaceKind.EndTagName when place.Element is { } element:
                TextRange name = place.Kind == XmlPlaceKind.StartTagName ? element.NameRange : element.EndTagNameRange ?? element.NameRange;
                return MSBuildSyntax.RoleOf(element) switch
                {
                    MSBuildRole.Property => new MSBuildSymbol(MSBuildSymbolKind.Property, element.LocalName, name, element),
                    MSBuildRole.Item => new MSBuildSymbol(MSBuildSymbolKind.Item, element.LocalName, name, element),
                    MSBuildRole.Metadata => new MSBuildSymbol(MSBuildSymbolKind.Metadata, element.LocalName, name, element, element.Parent!.LocalName),
                    MSBuildRole.Task => new MSBuildSymbol(MSBuildSymbolKind.Task, element.LocalName, name, element),
                    MSBuildRole.Project or MSBuildRole.Structure => new MSBuildSymbol(MSBuildSymbolKind.Element, element.LocalName, name, element),
                    _ => null,
                };
            case XmlPlaceKind.AttributeName when place.Element is { } element && place.Attribute is { } attribute:
                return MSBuildSyntax.RoleOf(element) == MSBuildRole.Item && !(attribute.Name is "Include" or "Exclude" or "Remove" or "Update" or "Condition" or "Label")
                    ? new MSBuildSymbol(MSBuildSymbolKind.Metadata, attribute.Name, attribute.NameRange, element, element.LocalName)
                    : new MSBuildSymbol(MSBuildSymbolKind.Attribute, attribute.Name, attribute.NameRange, element, element.LocalName);
            case XmlPlaceKind.AttributeValue when place.Element is { } element && place.Attribute is { } attribute:
                return InValue(text, attribute.ValueStart, attribute.ValueEnd, offset, element) ?? AttributeValue(text, element, attribute, offset);
            case XmlPlaceKind.Content when place.Element is { } element && element.Children.Count == 0:
                TextRange content = element.Content;
                return InValue(text, content.Start, content.End, offset, element)
                    ?? (MSBuildSyntax.RoleOf(element) == MSBuildRole.Property && content.Length > 0
                        ? new MSBuildSymbol(MSBuildSymbolKind.Property, element.LocalName, element.NameRange, element)
                        : null);
        }
        return null;
    }

    /// <summary>A reference in a value: <c>$(Name)</c>, <c>@(Type)</c>, <c>%(Name)</c>.</summary>
    private static MSBuildSymbol? InValue(string text, int start, int end, int offset, XmlElement element) =>
        MSBuildSyntax.ReferenceAt(text, start, end, offset) is not { } reference || reference.Name.Length == 0 ? null : reference.Sigil switch
        {
            '$' => new MSBuildSymbol(MSBuildSymbolKind.Property, reference.Name, reference.NameRange, null),
            '@' => new MSBuildSymbol(MSBuildSymbolKind.Item, reference.Name, reference.NameRange, null),
            _ => new MSBuildSymbol(MSBuildSymbolKind.Metadata, reference.Name, reference.NameRange, null, reference.ItemType),
        };

    /// <summary>What an attribute's value names: a target of a list, an import's file, an SDK, a referenced file.</summary>
    private static MSBuildSymbol? AttributeValue(string text, XmlElement element, XmlAttribute attribute, int offset)
    {
        if (MSBuildSyntax.NamesTargets(element, attribute))
        {
            (string name, TextRange range) = MSBuildSyntax.EntryAt(text, attribute.ValueStart, attribute.ValueEnd, offset);
            return name.Length == 0 || MSBuildSyntax.IsExpression(name) ? null : new MSBuildSymbol(MSBuildSymbolKind.Target, name, range, element);
        }
        if (attribute.Name == "Sdk" || (element.LocalName == "Sdk" && attribute.Name == "Name"))
        {
            return new MSBuildSymbol(MSBuildSymbolKind.Sdk, attribute.Value.Trim(), attribute.ValueRange, element);
        }
        if (element.LocalName == "Import" && attribute.Name == "Project")
        {
            return new MSBuildSymbol(MSBuildSymbolKind.Import, attribute.Value.Trim(), attribute.ValueRange, element);
        }
        if (MSBuildSyntax.RoleOf(element) == MSBuildRole.Item && attribute.Name is "Include" or "Update")
        {
            if (MSBuildSyntax.PackageItems.Contains(element.LocalName))
            {
                return new MSBuildSymbol(MSBuildSymbolKind.Item, element.LocalName, attribute.ValueRange, element);
            }
            (string entry, TextRange range) = MSBuildSyntax.EntryAt(text, attribute.ValueStart, attribute.ValueEnd, offset);
            return entry.Length == 0 || MSBuildSyntax.IsExpression(entry) || entry.IndexOfAny(['*', '?']) >= 0 ? null
                : new MSBuildSymbol(MSBuildSymbolKind.File, Core.ItemPaths.Unescaped(entry), range, element);
        }
        return null;
    }

    /// <summary>The 1-based line and column of an offset, as MSBuild counts its places: an XML reader's lines
    /// (<see cref="Lsp.ServerLines.OfXml"/>).</summary>
    public static (int Line, int Column) PlaceOf(string text, int offset)
    {
        Lsp.LspPosition position = Lsp.ServerLines.OfXml(TextSnapshot.From(text)).PositionOf(Math.Min(offset, text.Length));
        return (position.Line + 1, position.Character + 1);
    }

    /// <summary>The hover card's words: what the name is and, from the evaluation, its value now and where that was set.</summary>
    public static string? Describe(MSBuildSymbol symbol, MSBuildContext context, VulnerabilityLookup? vulnerabilities = null)
    {
        MSBuildInsight? insight = context.Insight;
        StringBuilder card = new();
        switch (symbol.Kind)
        {
            case MSBuildSymbolKind.Property:
                InsightProperty? property = insight?.Property(symbol.Name);
                card.AppendLine(property is null ? symbol.Name : $"{property.Name} = {Shown(property.Value)}");
                card.AppendLine(property is null ? (insight is { Evaluated: true } ? "MSBuild property, not set in this project" : "MSBuild property")
                    : $"MSBuild property, {Origin(property, insight!)}");
                AppendDocumentation(card, context.Schema.Properties.GetValueOrDefault(symbol.Name)?.Documentation);
                break;
            case MSBuildSymbolKind.Item:
                DescribeItem(card, symbol, context, vulnerabilities);
                break;
            case MSBuildSymbolKind.Metadata:
                card.AppendLine(symbol.Owner is { } owner ? $"%({owner}.{symbol.Name})" : $"%({symbol.Name})");
                card.AppendLine("MSBuild item metadata");
                AppendDocumentation(card, symbol.Owner is { } type ? Documentation(context, type, symbol.Name) : null);
                break;
            case MSBuildSymbolKind.Target:
                card.AppendLine($"Target {symbol.Name}");
                if (insight?.Target(symbol.Name) is { } target && target.Defined.Count > 0)
                {
                    card.AppendLine($"MSBuild target, defined in {Where(insight.Place(target.Defined[0]))}"
                        + (target.Defined.Count > 1 ? $" (and {target.Defined.Count - 1} other place{(target.Defined.Count == 2 ? "" : "s")})" : string.Empty));
                }
                else
                {
                    card.AppendLine(insight is { Evaluated: true } ? "MSBuild target, defined nowhere this project imports" : "MSBuild target");
                }
                break;
            case MSBuildSymbolKind.Task:
                card.AppendLine($"Task {symbol.Name}");
                card.AppendLine("MSBuild task");
                AppendDocumentation(card, context.Schema.Tasks.GetValueOrDefault(symbol.Name)?.Documentation);
                break;
            case MSBuildSymbolKind.Element:
                card.AppendLine(symbol.Name);
                AppendDocumentation(card, context.Schema.Structure.GetValueOrDefault(symbol.Name)?.Documentation);
                if (symbol.Name == "Import" && symbol.Element is { } import && Imported(import, context) is { Count: > 0 } files)
                {
                    card.AppendLine($"Imports {string.Join(", ", files)}");
                }
                break;
            case MSBuildSymbolKind.Attribute:
                card.AppendLine($"{symbol.Owner}.{symbol.Name}");
                AppendDocumentation(card, AttributeDocumentation(context, symbol));
                break;
            case MSBuildSymbolKind.Import:
                card.AppendLine($"Import {symbol.Name}");
                card.AppendLine(symbol.Element is { } element && Imported(element, context) is { Count: > 0 } imported
                    ? $"Imports {string.Join(", ", imported)}"
                    : insight is { Evaluated: true } ? "Imports nothing: the file is not there, or the condition is false" : "MSBuild import");
                break;
            case MSBuildSymbolKind.Sdk:
                card.AppendLine($"Sdk {symbol.Name}");
                card.AppendLine(insight?.SdkImports(symbol.Name) is { Count: > 0 } sdk ? $"MSBuild SDK, from {Path.GetDirectoryName(Path.GetDirectoryName(sdk[0]))}" : "MSBuild SDK");
                break;
            case MSBuildSymbolKind.File:
                string full = Full(context.Path, symbol.Name);
                card.AppendLine(symbol.Name);
                card.AppendLine(File.Exists(full) ? full : $"{full} is not there");
                break;
        }
        string described = card.ToString().TrimEnd();
        return described.Length == 0 ? null : described;
    }

    /// <summary>An item element's or item type's card: its items as evaluated, the first's metadata, a package's version
    /// (its central one under central package management) and a known vulnerability of it.</summary>
    private static void DescribeItem(StringBuilder card, MSBuildSymbol symbol, MSBuildContext context, VulnerabilityLookup? vulnerabilities)
    {
        MSBuildInsight? insight = context.Insight;
        string type = symbol.Name;
        InsightItem? made = symbol.Element is { } element && insight is not null
            ? insight.ItemsAt(context.Path, PlaceOf(context.Tree.Text, element.Start).Line, PlaceOf(context.Tree.Text, element.Start).Column)
            : null;
        if (made is not null)
        {
            card.AppendLine($"{type} {string.Join(";", made.Includes.Take(3))}{(made.Count > 3 ? $" (and {made.Count - 3} more)" : string.Empty)}");
            foreach ((string name, string value) in made.Metadata.Take(8))
            {
                card.AppendLine($"{name} = {Shown(value)}");
            }
        }
        else if (symbol.Element is { } written && MSBuildSyntax.ValueOf(written, "Include", context.Tree.Text) is { Length: > 0 } include)
        {
            card.AppendLine($"{type} {include}");
        }
        else
        {
            card.AppendLine($"@({type})");
        }
        if (MSBuildSyntax.PackageItems.Contains(type) && symbol.Element is { } package && insight is not null
            && MSBuildSyntax.ValueOf(package, "Include", context.Tree.Text) is { Length: > 0 } id)
        {
            string? version = made?.Metadata.GetValueOrDefault("VersionOverride") is { Length: > 0 } overridden ? overridden : made?.Metadata.GetValueOrDefault("Version");
            if (string.IsNullOrEmpty(version) && type == "PackageReference" && insight.CentralVersions().TryGetValue(id, out (string Version, MSBuildPlace Place) central))
            {
                card.AppendLine($"Version = {central.Version}, central, set in {Where(central.Place)}");
                version = central.Version;
            }
            if (version is { Length: > 0 } && vulnerabilities?.Invoke(id, version) is { } advisory)
            {
                card.AppendLine(advisory);
            }
        }
        InsightItemType? known = insight?.ItemType(type);
        card.AppendLine(known is null ? "MSBuild item" : $"MSBuild item, {Core.Plural.Of(known.Count, $"{type} item")} in the project");
        AppendDocumentation(card, context.Schema.Items.GetValueOrDefault(type)?.Documentation);
    }

    /// <summary>The places Go to Declaration offers for a name, the one it would go to first; none where it leads nowhere.</summary>
    public static IReadOnlyList<MSBuildTarget> Targets(MSBuildSymbol symbol, MSBuildContext context)
    {
        MSBuildInsight? insight = context.Insight;
        if (insight is null)
        {
            return symbol.Kind == MSBuildSymbolKind.File && File.Exists(Full(context.Path, symbol.Name))
                ? [new MSBuildTarget(symbol.Name, new MSBuildPlace(Full(context.Path, symbol.Name), 1, 1))]
                : [];
        }
        switch (symbol.Kind)
        {
            case MSBuildSymbolKind.Property:
                if (insight.Property(symbol.Name) is not { } property)
                {
                    return [];
                }
                (int line, int column) = symbol.Element is { } own ? PlaceOf(context.Tree.Text, own.Start) : (0, 0);
                return [.. property.Set.Select(insight.Place)
                    .Where(p => !(p.Line == line && p.Column == column && Core.PathNames.Same(p.Path, context.Path)))
                    .Select(p => new MSBuildTarget($"{property.Name} in {Where(p)}", p))];
            case MSBuildSymbolKind.Target:
                return insight.Target(symbol.Name) is { } target
                    ? [.. target.Defined.Select(insight.Place).Select(p => new MSBuildTarget($"{symbol.Name} in {Where(p)}", p))]
                    : [];
            case MSBuildSymbolKind.Item when symbol.Element is null:
                return insight.ItemType(symbol.Name) is { } type
                    ? [.. type.Defined.Select(insight.Place).Select(p => new MSBuildTarget($"{symbol.Name} in {Where(p)}", p))]
                    : [];
            case MSBuildSymbolKind.Import:
            case MSBuildSymbolKind.Element when symbol.Name == "Import":
                return symbol.Element is { } import
                    ? [.. Imported(import, context).Select(f => new MSBuildTarget(Path.GetFileName(f), new MSBuildPlace(f, 1, 1)))]
                    : [];
            case MSBuildSymbolKind.Sdk:
                return [.. insight.SdkImports(symbol.Name).Select(f => new MSBuildTarget($"{Path.GetFileName(f)} of {symbol.Name}", new MSBuildPlace(f, 1, 1)))];
            case MSBuildSymbolKind.File:
                string full = Full(context.Path, symbol.Name);
                return File.Exists(full) ? [new MSBuildTarget(symbol.Name, new MSBuildPlace(full, 1, 1))] : [];
        }
        return [];
    }

    /// <summary>The files an Import element brought in, as the evaluation has them.</summary>
    private static IReadOnlyList<string> Imported(XmlElement import, MSBuildContext context)
    {
        (int line, int column) = PlaceOf(context.Tree.Text, import.Start);
        return context.Insight?.ImportsAt(context.Path, line, column) ?? [];
    }

    private static string Full(string from, string relative)
    {
        try
        {
            return Core.PathNames.Canonical(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(from)!, relative.Replace('\\', '/'))));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return relative;
        }
    }

    /// <summary>Where a property's value came from: the file and line that last set it, the environment, the command line.</summary>
    private static string Origin(InsightProperty property, MSBuildInsight insight) => property.Origin switch
    {
        PropertyOrigin.Global => "a global property",
        PropertyOrigin.Environment => "from an environment variable",
        PropertyOrigin.Reserved => "reserved, set by MSBuild",
        _ when property.Set.Count > 0 => $"set in {Where(insight.Place(property.Set[0]))}"
            + (property.Set.Count > 1 ? $" (over {property.Set.Count - 1} earlier definition{(property.Set.Count == 2 ? "" : "s")})" : string.Empty),
        _ => "set by MSBuild",
    };

    /// <summary>A place as a person reads it: the file's name and its line.</summary>
    public static string Where(MSBuildPlace place) => place.Line > 0 ? $"{Path.GetFileName(place.Path)}, line {place.Line}" : Path.GetFileName(place.Path);

    /// <summary>A value on one line, its runs of white space (a list written a line an entry) as one space, and not too long.</summary>
    public static string Shown(string value)
    {
        string flat = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length == 0 ? "(empty)" : flat.Length > 200 ? flat[..199] + "…" : flat;
    }

    private static void AppendDocumentation(StringBuilder card, string? documentation)
    {
        if (documentation is { Length: > 0 })
        {
            card.AppendLine(documentation);
        }
    }

    private static string? Documentation(MSBuildContext context, string type, string metadata) =>
        context.Schema.Items.GetValueOrDefault(type) is { } item
            ? item.Child(metadata)?.Documentation ?? item.Attribute(metadata)?.Documentation
            : null;

    private static string? AttributeDocumentation(MSBuildContext context, MSBuildSymbol symbol)
    {
        if (symbol.Element is not { } element)
        {
            return null;
        }
        return MSBuildSyntax.RoleOf(element) switch
        {
            MSBuildRole.Item => context.Schema.Items.GetValueOrDefault(element.LocalName)?.Attribute(symbol.Name)?.Documentation
                ?? context.Schema.Items["Compile"].Attribute(symbol.Name)?.Documentation,
            MSBuildRole.Task => context.Schema.Tasks.GetValueOrDefault(element.LocalName)?.Attribute(symbol.Name)?.Documentation,
            _ => context.Schema.Structure.GetValueOrDefault(element.LocalName)?.Attribute(symbol.Name)?.Documentation,
        };
    }
}

/// <summary>The words of a package version's known vulnerability, as NuGet's audit says them; null for none.</summary>
public delegate string? VulnerabilityLookup(string id, string version);
