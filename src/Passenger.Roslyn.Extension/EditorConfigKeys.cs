using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Diagnostics.Analyzers.NamingStyles;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Options;
using Passenger.Core;

namespace Passenger.Roslyn.Extension;

/// <summary>What the server reads of an .editorconfig for C#: the options its own features name for .editorconfig (the
/// server's <see cref="EditorConfigOptionsEnumerator"/>), each with what its serializer writes of its values and default;
/// and every diagnostic the solution's and the projects' analyzers report that a severity can be set for, from the
/// projects that read the file (its analyzer config documents), else every project.</summary>
public sealed class EditorConfigKeys : IExtensionWorkspaceMessageHandler<EditorConfigKeysAsked, EditorConfigKeysFound>
{
    /// <summary>The diagnostics each analyzer reference reports, read once: its analyzers stay loaded while it is referenced.</summary>
    private static readonly ConditionalWeakTable<AnalyzerReference, IReadOnlyList<EditorConfigDiagnostic>> Reported = new();

    /// <summary>The options, the same for every solution of the server's.</summary>
    private static readonly ConcurrentDictionary<string, IReadOnlyList<EditorConfigOption>> Options = new(StringComparer.Ordinal);

    /// <summary>Each option's definition by its key, which reads its values.</summary>
    internal static readonly ConcurrentDictionary<string, OptionDefinition> Definitions = new(StringComparer.Ordinal);

    public Task<EditorConfigKeysFound> ExecuteAsync(EditorConfigKeysAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        Solution solution = context.Solution;
        List<Project> projects = [.. solution.Projects.Where(p => p.Language == LanguageNames.CSharp)];
        List<Project> reading = [.. projects.Where(p => p.AnalyzerConfigDocuments.Any(d => d.FilePath is { } file && PathNames.Same(file, message.Path)))];
        if (reading.Count == 0)
        {
            reading = projects;
        }
        IReadOnlyList<EditorConfigOption> options = Options.GetOrAdd(LanguageNames.CSharp, _ => OptionsOf(solution));
        Dictionary<string, EditorConfigDiagnostic> diagnostics = new(StringComparer.OrdinalIgnoreCase);
        foreach (AnalyzerReference reference in solution.AnalyzerReferences.Concat(reading.SelectMany(p => p.AnalyzerReferences)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (EditorConfigDiagnostic diagnostic in ReportedBy(reference))
            {
                diagnostics.TryAdd(diagnostic.Id, diagnostic);
            }
        }
        return Task.FromResult(new EditorConfigKeysFound(options, [.. diagnostics.Values.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase)], Severities.Value,
            [.. reading.Select(p => NameOf(p)).Distinct(StringComparer.Ordinal)]));
    }

    /// <summary>The diagnostics a reference's C# analyzers report whose severity can be set, each id once, read once.</summary>
    internal static IReadOnlyList<EditorConfigDiagnostic> ReportedBy(AnalyzerReference reference) => Reported.GetValue(reference, DiagnosticsOf);

    /// <summary>A project's name without the framework the server adds to each of a multi-targeted project's.</summary>
    internal static string NameOf(Project project) =>
        project.Name.EndsWith(')') && project.Name.LastIndexOf(" (", StringComparison.Ordinal) is int framework and > 0 ? project.Name[..framework] : project.Name;

    /// <summary>The severities a code style option's value may end in, as the server's own words for them.</summary>
    private static readonly Lazy<IReadOnlyList<string>> Severities = new(() =>
        [.. typeof(IOption2).Assembly.GetType("Microsoft.CodeAnalysis.EditorConfigSeverityStrings")?.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!) ?? []]);

    /// <summary>Every C# option the server's features read from .editorconfig, by the feature they stand in.</summary>
    private static IReadOnlyList<EditorConfigOption> OptionsOf(Solution solution)
    {
        if (solution.Services.ExportProvider.GetExports<EditorConfigOptionsEnumerator>().FirstOrDefault()?.Value is not { } enumerator)
        {
            return [];
        }
        List<EditorConfigOption> found = [];
        HashSet<string> named = new(StringComparer.Ordinal);
        foreach ((string feature, ImmutableArray<IOption2> options) in enumerator.GetOptions(LanguageNames.CSharp))
        {
            foreach (IOption2 option in options)
            {
                OptionDefinition definition = option.Definition;
                if (!definition.IsEditorConfigOption || definition.Type == typeof(NamingStylePreferences) || !named.Add(definition.ConfigName))
                {
                    continue;
                }
                Definitions[definition.ConfigName] = definition;
                (string kind, IReadOnlyList<string> values, bool severity) = Shape(definition);
                string? fallback = Written(definition, definition.DefaultValue);
                if (definition.DefaultValue is ICodeStyleOption2 style && fallback is not null && !fallback.Contains(':', StringComparison.Ordinal))
                {
                    fallback += ":" + ReportDiagnosticExtensions.ToEditorConfigString(style.Notification.Severity);
                }
                found.Add(new EditorConfigOption(definition.ConfigName, feature, GroupOf(definition.Group), kind, values, fallback, severity));
            }
        }
        return found;
    }

    /// <summary>A group's words, its parents' first.</summary>
    private static string GroupOf(OptionGroup group)
    {
        List<string> words = [];
        for (OptionGroup? at = group; at is not null; at = at.Parent)
        {
            if (at.Description is { Length: > 0 } said)
            {
                words.Insert(0, said);
            }
        }
        return string.Join(" › ", words);
    }

    /// <summary>What an option's value is and the values it takes, each as its serializer writes it; a code style option's
    /// with the severity that may follow it left off.</summary>
    private static (string Kind, IReadOnlyList<string> Values, bool Severity) Shape(OptionDefinition definition)
    {
        Type type = definition.Type;
        if (definition.DefaultValue is ICodeStyleOption2 style)
        {
            Type inner = type.GetGenericArguments()[0];
            IEnumerable<object> each = inner == typeof(bool) ? [true, false] : inner.IsEnum ? Enum.GetValues(inner).Cast<object>() : [];
            List<string> values = [.. each.Select(v => Written(definition, style.WithValue(v))).OfType<string>().Select(Bare).Distinct(StringComparer.Ordinal)];
            return (values.Count > 0 ? EditorConfigValueKinds.Choice : EditorConfigValueKinds.Text, values, true);
        }
        Type bare = Nullable.GetUnderlyingType(type) ?? type;
        if (bare == typeof(bool))
        {
            return (EditorConfigValueKinds.Choice, ["true", "false"], false);
        }
        if (bare == typeof(int) || bare == typeof(long))
        {
            return (EditorConfigValueKinds.Number, [], false);
        }
        if (bare.IsEnum)
        {
            List<string> values = [.. Enum.GetValues(bare).Cast<object>().Select(v => Written(definition, v)).OfType<string>().Distinct(StringComparer.Ordinal)];
            bool flags = bare.IsDefined(typeof(FlagsAttribute), inherit: false);
            return (flags ? EditorConfigValueKinds.List : EditorConfigValueKinds.Choice, values, false);
        }
        return (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>) ? EditorConfigValueKinds.List : EditorConfigValueKinds.Text, [], false);

        static string Bare(string written) => written.Split(':')[0].Trim();
    }

    /// <summary>A value as the option's serializer writes it; null for one it does not write.</summary>
    private static string? Written(OptionDefinition definition, object? value)
    {
        try
        {
            return definition.Serializer.Serialize(value);
        }
        catch (Exception refused) when (refused is InvalidOperationException or InvalidCastException or ArgumentException or NullReferenceException)
        {
            return null;
        }
    }

    /// <summary>The diagnostics a reference's C# analyzers report whose severity an .editorconfig can set, each id once.</summary>
    private static IReadOnlyList<EditorConfigDiagnostic> DiagnosticsOf(AnalyzerReference reference)
    {
        List<EditorConfigDiagnostic> found = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        string from = reference.Display ?? Path.GetFileNameWithoutExtension(reference.FullPath) ?? "an analyzer";
        ImmutableArray<DiagnosticAnalyzer> analyzers;
        try
        {
            analyzers = reference.GetAnalyzers(LanguageNames.CSharp);
        }
        catch (Exception unread) when (unread is IOException or BadImageFormatException or TypeLoadException or ReflectionTypeLoadException)
        {
            return [];
        }
        foreach (DiagnosticAnalyzer analyzer in analyzers)
        {
            ImmutableArray<DiagnosticDescriptor> descriptors;
            try
            {
                descriptors = analyzer.SupportedDiagnostics;
            }
            catch (Exception failed) when (failed is not OperationCanceledException)
            {
                // An analyzer whose list throws reports nothing the server runs either.
                continue;
            }
            foreach (DiagnosticDescriptor descriptor in descriptors)
            {
                if (descriptor.CustomTags.Contains(WellKnownDiagnosticTags.NotConfigurable) || !ids.Add(descriptor.Id))
                {
                    continue;
                }
                found.Add(new EditorConfigDiagnostic(descriptor.Id, descriptor.Title.ToString(System.Globalization.CultureInfo.InvariantCulture), descriptor.Category,
                    SeverityOf(descriptor.DefaultSeverity), descriptor.IsEnabledByDefault, from, descriptor.HelpLinkUri is { Length: > 0 } help ? help : null));
            }
        }
        return found;
    }

    /// <summary>A diagnostic's default severity in the words an .editorconfig gives it.</summary>
    private static string SeverityOf(DiagnosticSeverity severity) => ReportDiagnosticExtensions.ToEditorConfigString(DiagnosticSeverityExtensions.ToReportDiagnostic(severity));
}

/// <summary>Which values an .editorconfig gives the server's C# options their own reading refuses (each option's
/// serializer, as the server reads the file), so the option keeps its default; a key the server has no option of is passed
/// over.</summary>
public sealed class EditorConfigValues : IExtensionWorkspaceMessageHandler<EditorConfigValuesAsked, EditorConfigValuesRefused>
{
    public Task<EditorConfigValuesRefused> ExecuteAsync(EditorConfigValuesAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        List<int> refused = [];
        for (int i = 0; i < message.Pairs.Count; i++)
        {
            if (EditorConfigKeys.Definitions.TryGetValue(message.Pairs[i].Key, out OptionDefinition? definition) && !definition.Serializer.TryParse(message.Pairs[i].Value.Trim(), out _))
            {
                refused.Add(i);
            }
        }
        return Task.FromResult(new EditorConfigValuesRefused(refused));
    }
}
