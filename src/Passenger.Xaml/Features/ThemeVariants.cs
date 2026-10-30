using Passenger.Core;
using Passenger.Text;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>Resources kept per theme variant (a <c>ThemeDictionaries</c>' Dark and Light dictionaries): a key one variant
/// defines and another does not is a warning where it is defined and where it is used, with a fix that adds it to the
/// variant missing it, as a copy of the other's definition to change.</summary>
public static class ThemeVariants
{
    /// <summary>The file's definitions missing from a variant of their group, and its uses of keys missing so anywhere.</summary>
    public static IEnumerable<XamlDiagnostic> Of(XamlFile file)
    {
        XamlProject project = file.Project;
        if (!project.Files.TryGetValue(file.Path, out XamlFileSummary? own))
        {
            yield break;
        }
        foreach (ThemeGroup group in own.ThemeGroups)
        {
            List<ResourceDefinition> inGroup = [.. own.Resources.Where(r => r.Group == group.Range)];
            foreach (ResourceDefinition definition in inGroup)
            {
                List<string> missing = [.. group.Variants.Where(v => !inGroup.Any(r => r.Key == definition.Key && r.Variant == v))];
                if (missing.Count == 0)
                {
                    continue;
                }
                List<string> defined = [.. inGroup.Where(r => r.Key == definition.Key).Select(r => r.Variant).OfType<string>().Distinct()];
                yield return new XamlDiagnostic(definition.KeyRange, XamlSeverity.Warning,
                    $"'{definition.Key}' is defined for {Plural.Listed(defined)} but not for {Plural.Listed(missing)}")
                {
                    Fixes = [.. missing.Select(v => Fix(file.Path, definition.Key, v))],
                };
            }
        }
    }

    /// <summary>A use of a key some variant of its group lacks: a warning naming the variants, with their fixes.</summary>
    public static XamlDiagnostic? Use(XamlFile file, XamlFileSummary own, TextRange range, string key)
    {
        IReadOnlyList<(ThemeGroup Group, string Missing, ResourceDefinition Defined)> missing = file.Project.MissingVariants(own, key);
        if (missing.Count == 0)
        {
            return null;
        }
        return new XamlDiagnostic(range, XamlSeverity.Warning, $"'{key}' is not defined for {Plural.Listed([.. missing.Select(m => m.Missing).Distinct()])}")
        {
            Fixes = [.. missing.Select(m => Fix(m.Defined.Path, key, m.Missing))],
        };
    }

    private static XamlFix Fix(string path, string key, string variant) =>
        new($"Add '{key}' to {variant}", path, text => AddTo(text, key, variant));

    /// <summary>The edit that copies a key's definition from the variant that has it into a variant's dictionary of the
    /// same group, after its last entry and indented as that is; none when the file no longer has them.</summary>
    public static IReadOnlyList<TextEdit> AddTo(string text, string key, string variant)
    {
        XmlTree tree = XmlTree.Parse(text);
        foreach (XmlElement group in tree.Elements.Where(e => e.LocalName.EndsWith(".ThemeDictionaries", StringComparison.Ordinal)))
        {
            XmlElement? target = group.Children.FirstOrDefault(d => KeyOf(d) is { } named && Variant(named) == variant);
            XmlElement? source = group.Children.Where(d => d != target).SelectMany(d => d.Children).FirstOrDefault(e => KeyOf(e) == key);
            if (target is null || source is null || target.IsSelfClosing || !target.IsStartTagClosed || target.EndTagStart is not { } end)
            {
                continue;
            }
            if (target.Children.Any(e => KeyOf(e) == key))
            {
                return [];
            }
            string copy = text[source.Start..source.End];
            string indent = IndentOf(text, source.Start);
            if (target.Children.Count > 0)
            {
                XmlElement last = target.Children[^1];
                return [new TextEdit(last.End, 0, $"\n{IndentOf(text, last.Start)}{copy}")];
            }
            int lineStart = text.LastIndexOf('\n', Math.Max(0, end - 1)) + 1;
            return [new TextEdit(lineStart, 0, $"{indent}{copy}\n")];
        }
        return [];
    }

    private static string? KeyOf(XmlElement element) =>
        element.Attributes.FirstOrDefault(a => a.LocalName == "Key" && a.Prefix.Length > 0)?.Value;

    /// <summary>A variant written as <c>{x:Static ThemeVariant.Dark}</c> is <c>Dark</c>.</summary>
    private static string Variant(string key)
    {
        string trimmed = key.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            int dot = trimmed.LastIndexOf('.');
            return dot < 0 ? trimmed : trimmed[(dot + 1)..^1].Trim();
        }
        return trimmed;
    }

    private static string IndentOf(string text, int offset)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        int at = lineStart;
        while (at < offset && text[at] is ' ' or '\t')
        {
            at++;
        }
        return text[lineStart..at];
    }
}
