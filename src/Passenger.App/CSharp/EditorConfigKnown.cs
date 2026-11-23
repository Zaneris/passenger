using Microsoft.CodeAnalysis;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>A key an .editorconfig's readers take: its name, who reads it, what its value is
/// (<see cref="EditorConfigValueKinds"/>), the values it takes where they are few, its default as written, whether a
/// severity may follow its value, and what it does in a person's words, line by line.</summary>
internal sealed record KnownKey(string Name, string From, string Kind, IReadOnlyList<string> Values, string? Default, bool Severity, IReadOnlyList<string> Said)
{
    /// <summary>Whether a value it is given is one it takes; one whose readers pass over what they do not know (Passenger's
    /// own) takes any.</summary>
    public bool Checked { get; init; } = true;
}

/// <summary>Every key the readers of an .editorconfig in the folder take: editorconfig.org's own (<see cref="EditorConfig.Standard"/>),
/// those Passenger's Settings rows are said over by (<see cref="SettingRow.Over"/>), and, once Roslyn's server answers,
/// its C# options and a severity for each diagnostic the projects' analyzers report, by id or by category. Keys under
/// .NET's prefixes (<c>dotnet_</c>, <c>csharp_</c>) are known whole only from the server's answer, and its analyzers' own
/// families (<c>dotnet_code_quality.</c>, naming rules) are left to them.</summary>
internal sealed class EditorConfigKnown
{
    public const string Diagnostic = "dotnet_diagnostic.";
    public const string AnalyzerDiagnostic = "dotnet_analyzer_diagnostic.";
    public const string Category = "category-";
    public const string SeveritySuffix = ".severity";

    /// <summary>Who reads the server's options.</summary>
    public const string Roslyn = "Roslyn";

    /// <summary>The prefixes of the keys Roslyn's options and its compiler read.</summary>
    private static readonly string[] DotnetPrefixes = ["dotnet_", "csharp_"];

    /// <summary>The families of keys analyzers read under .NET's prefixes, by names of their own choosing.</summary>
    private static readonly string[] OpenFamilies = ["dotnet_naming_rule.", "dotnet_naming_symbols.", "dotnet_naming_style.", "dotnet_code_quality."];

    private readonly Dictionary<string, KnownKey> keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EditorConfigDiagnostic> diagnostics = new(StringComparer.OrdinalIgnoreCase);

    private EditorConfigKnown(EditorConfigKeysFound? server)
    {
        Server = server;
        foreach (EditorConfig.StandardKey standard in EditorConfig.Standard)
        {
            Add(new KnownKey(standard.Name, "EditorConfig", standard.Numbered ? EditorConfigValueKinds.Number : EditorConfigValueKinds.Choice, standard.Values, null, false, [standard.Said]));
        }
        foreach (Setting setting in Settings.Declared.All.Where(s => s.Row?.Over is not null))
        {
            SettingRow row = setting.Row!;
            string said = $"Settings, {row.Page}: {row.Label}" + (row.Note is { } note ? $". {note}" : string.Empty);
            foreach (string key in row.Over!.Keys.Concat(row.Over.Aliases))
            {
                string kind = setting.EditorConfigValues.Count > 0 ? EditorConfigValueKinds.Choice : setting is NumberSetting ? EditorConfigValueKinds.Number : EditorConfigValueKinds.Text;
                Add(new KnownKey(key, "Passenger", kind, setting.EditorConfigValues, null, false, [said]) { Checked = false });
            }
        }
        if (server is null)
        {
            return;
        }
        Severities = server.Severities;
        DiagnosticSeverities = CompilerSeverities(server.Severities);
        foreach (EditorConfigOption option in server.Options)
        {
            List<string> said = [option.Group is { Length: > 0 } group ? $"{option.Feature} › {group}" : option.Feature];
            Add(new KnownKey(option.Name, Roslyn, option.Kind, option.Values, option.Default, option.Severity, said));
        }
        foreach (EditorConfigDiagnostic diagnostic in server.Diagnostics)
        {
            diagnostics.TryAdd(diagnostic.Id, diagnostic);
        }
        Categories = [.. server.Diagnostics.Select(d => d.Category).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>What the folder's readers take without the server.</summary>
    private static readonly Lazy<EditorConfigKnown> Unserved = new(() => new EditorConfigKnown(null));

    /// <summary>The last answer read, which every ask until the server's projects change gives again.</summary>
    private static EditorConfigKnown? last;

    /// <summary>What the folder's readers take, with what Roslyn's server read of the file where it answered.</summary>
    public static EditorConfigKnown For(EditorConfigKeysFound? server)
    {
        if (server is null)
        {
            return Unserved.Value;
        }
        EditorConfigKnown? read = Volatile.Read(ref last);
        if (read?.Server != server)
        {
            read = new EditorConfigKnown(server);
            Volatile.Write(ref last, read);
        }
        return read;
    }

    /// <summary>The server's answer; null where none came, so .NET's keys are not known whole.</summary>
    public EditorConfigKeysFound? Server { get; }

    /// <summary>The severities a code style option's value may end in.</summary>
    public IReadOnlyList<string> Severities { get; } = [];

    /// <summary>The severities the compiler takes for a diagnostic.</summary>
    public IReadOnlyList<string> DiagnosticSeverities { get; } = [];

    /// <summary>The categories the projects' analyzers report diagnostics in.</summary>
    public IReadOnlyList<string> Categories { get; } = [];

    /// <summary>Every key by name, a diagnostic's aside.</summary>
    public IEnumerable<KnownKey> Keys => keys.Values;

    public IReadOnlyCollection<EditorConfigDiagnostic> Diagnostics => diagnostics.Values;

    /// <summary>Whether a key is under .NET's prefixes, which Roslyn's options and its compiler read.</summary>
    public static bool IsDotnet(string key) => DotnetPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>A key known by its name; one of a diagnostic's family is <see cref="DiagnosticOf"/>'s.</summary>
    public KnownKey? KeyOf(string name) => keys.GetValueOrDefault(name);

    /// <summary>The diagnostic a <c>dotnet_diagnostic.ID.severity</c> key names, where the projects' analyzers report it.</summary>
    public EditorConfigDiagnostic? DiagnosticOf(string key) => IdOf(key) is { } id ? diagnostics.GetValueOrDefault(id) : null;

    /// <summary>The id a <c>dotnet_diagnostic.ID.severity</c> key names; null for any other key.</summary>
    public static string? IdOf(string key) =>
        key.StartsWith(Diagnostic, StringComparison.OrdinalIgnoreCase) && key.EndsWith(SeveritySuffix, StringComparison.OrdinalIgnoreCase) && key.Length > Diagnostic.Length + SeveritySuffix.Length
            ? key[Diagnostic.Length..^SeveritySuffix.Length] : null;

    /// <summary>The category a <c>dotnet_analyzer_diagnostic.category-NAME.severity</c> key names, empty for every
    /// category's <c>dotnet_analyzer_diagnostic.severity</c>; null for any other key.</summary>
    public static string? CategoryOf(string key) =>
        key.Equals(AnalyzerDiagnostic + SeveritySuffix[1..], StringComparison.OrdinalIgnoreCase) ? string.Empty
        : key.StartsWith(AnalyzerDiagnostic + Category, StringComparison.OrdinalIgnoreCase) && key.EndsWith(SeveritySuffix, StringComparison.OrdinalIgnoreCase)
            && key.Length > AnalyzerDiagnostic.Length + Category.Length + SeveritySuffix.Length
            ? key[(AnalyzerDiagnostic.Length + Category.Length)..^SeveritySuffix.Length] : null;

    /// <summary>Why a pair an .editorconfig sets is read by nothing, or not as written, and whether its value is what is
    /// wrong: a key under .NET's prefixes no option of the server's is named, a diagnostic or a category no analyzer of the
    /// projects reports, or a value its key does not take; null where it reads, where it is unset, or where only the
    /// server could say.</summary>
    public (string Why, bool OnValue)? Problem(string written, string value, bool preamble)
    {
        string key = written.ToLowerInvariant();
        if (EditorConfig.Word(value) == EditorConfig.Unset)
        {
            return null;
        }
        if (KeyOf(key) is { } known)
        {
            if (known.Name == "root" != preamble)
            {
                return (preamble ? $"Nothing reads {written} before the first section" : "root is read only before the first section", false);
            }
            return ValueProblem(known, value) is { } wrong ? (wrong, true) : null;
        }
        if (Server is null)
        {
            return null;
        }
        string severity = value.Trim();
        string? notSeverity = DiagnosticSeverities.Contains(severity, StringComparer.OrdinalIgnoreCase) ? null : $"{severity} is no severity: {Plural.Either(DiagnosticSeverities)}";
        if (IdOf(written) is { } id)
        {
            return !diagnostics.ContainsKey(id) ? ($"No analyzer {Plural.Either(Server.Projects)} runs reports {id}", false) : notSeverity is null ? null : (notSeverity, true);
        }
        if (CategoryOf(written) is { } category)
        {
            return category.Length > 0 && !Categories.Contains(category, StringComparer.OrdinalIgnoreCase)
                ? ($"No analyzer {Plural.Either(Server.Projects)} runs reports diagnostics in the category {category}", false)
                : notSeverity is null ? null : (notSeverity, true);
        }
        if (!IsDotnet(key) || key.Contains('.', StringComparison.Ordinal) || OpenFamilies.Any(f => key.StartsWith(f, StringComparison.Ordinal)))
        {
            return null;
        }
        return ($"Nothing reads {written}: no option of Roslyn's is named so", false);
    }

    /// <summary>Why a known key does not take a value, as its reader would pass it over.</summary>
    private string? ValueProblem(KnownKey known, string value)
    {
        // Passenger's own readers pass over a value they do not know, and Roslyn's options are checked by their own reading.
        if (!known.Checked || known.From == Roslyn)
        {
            return null;
        }
        string written = value.Trim();
        string severity = string.Empty;
        if (known.Severity && written.IndexOf(':', StringComparison.Ordinal) is int colon and >= 0)
        {
            severity = written[(colon + 1)..].Trim();
            written = written[..colon].Trim();
            if (!Severities.Contains(severity, StringComparer.OrdinalIgnoreCase))
            {
                return $"{severity} is no severity: {Plural.Either(Severities)}";
            }
        }
        bool number = int.TryParse(written, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);
        return known.Kind switch
        {
            EditorConfigValueKinds.Choice when known.Values.Contains(written, StringComparer.OrdinalIgnoreCase) => null,
            EditorConfigValueKinds.Choice => $"{known.Name} takes {Plural.Either(known.Values)}, not {written}",
            EditorConfigValueKinds.Number when number || known.Values.Contains(written, StringComparer.OrdinalIgnoreCase) => null,
            EditorConfigValueKinds.Number => $"{known.Name} takes a number{(known.Values.Count > 0 ? $" or {Plural.Either(known.Values)}" : string.Empty)}, not {written}",
            EditorConfigValueKinds.List when known.Values.Count > 0 && written.Split(',').Select(p => p.Trim()).FirstOrDefault(p => !known.Values.Contains(p, StringComparer.OrdinalIgnoreCase)) is { } wrong =>
                $"{known.Name} takes {Plural.Either(known.Values)}, or several of them parted by commas, not {wrong}",
            _ => null,
        };
    }

    /// <summary>Why the server's own reading of an option refuses a value, in its words: the values it takes.</summary>
    public string RefusedBy(string key, string value) => KeyOf(key) is { Values.Count: > 0 } known
        ? $"Roslyn does not read {value.Trim()} as {key}: it takes {Plural.Either(known.Values)}{(known.Severity ? ", a severity after a colon" : string.Empty)}"
        : $"Roslyn does not read {value.Trim()} as {key}";

    /// <summary>The lines a card says of a key: what it does, the values it takes, its default and who reads it; a
    /// diagnostic's title, category and default severity; null for a key nothing known reads.</summary>
    public IReadOnlyList<string>? Said(string key)
    {
        if (KeyOf(key) is { } known)
        {
            List<string> said = [.. known.Said];
            if (known.Values.Count > 0)
            {
                said.Add(known.Kind == EditorConfigValueKinds.List ? $"Takes {Plural.Either(known.Values)}, or several of them parted by commas"
                    : known.Kind == EditorConfigValueKinds.Number ? $"Takes a number or {Plural.Either(known.Values)}" : $"Takes {Plural.Either(known.Values)}");
            }
            else if (known.Kind == EditorConfigValueKinds.Number)
            {
                said.Add("Takes a number");
            }
            if (known.Severity && Severities.Count > 0)
            {
                said.Add($"A severity may follow its value after a colon: {Plural.Either(Severities)}");
            }
            if (known.Default is { Length: > 0 } fallback)
            {
                said.Add($"Default: {fallback}");
            }
            said.Add($"Read by {known.From}");
            return said;
        }
        if (DiagnosticOf(key) is { } diagnostic)
        {
            return [$"{diagnostic.Id}: {diagnostic.Title}", $"Category {diagnostic.Category}, {diagnostic.Severity} by default{(diagnostic.Enabled ? string.Empty : ", off unless turned on")}",
                $"Takes {Plural.Either(DiagnosticSeverities)}", $"Reported by {diagnostic.From}"];
        }
        if (CategoryOf(key) is { } category && Server is not null)
        {
            int count = diagnostics.Values.Count(d => category.Length == 0 || d.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
            return [category.Length == 0 ? "The severity of every analyzer's diagnostics no key of its own or its category's sets"
                : $"The severity of the {category} category's diagnostics no key of their own sets", $"{Plural.Of(count, "diagnostic")} the projects' analyzers report",
                $"Takes {Plural.Either(DiagnosticSeverities)}"];
        }
        return null;
    }

    private void Add(KnownKey key)
    {
        if (keys.TryGetValue(key.Name, out KnownKey? first))
        {
            keys[key.Name] = first with { Said = [.. first.Said, .. key.Said.Select(s => $"{s} ({key.From})")] };
            return;
        }
        keys[key.Name] = key;
    }

    /// <summary>The words the compiler takes as a diagnostic's severity: the server's and its own default, each as the
    /// compiler reads a <c>dotnet_diagnostic</c> key set to it.</summary>
    private static IReadOnlyList<string> CompilerSeverities(IReadOnlyList<string> severities)
    {
        string file = Path.Combine(Path.GetTempPath(), "passenger-severities", EditorConfig.FileName);
        string source = Path.Combine(Path.GetTempPath(), "passenger-severities", "A.cs");
        return [.. ((string[])["default", .. severities]).Where(word =>
            AnalyzerConfigSet.Create<AnalyzerConfig[]>([AnalyzerConfig.Parse($"[*.cs]\n{Diagnostic}X{SeveritySuffix} = {word}\n", file)]).GetOptionsForSourcePath(source).TreeOptions.ContainsKey("X"))];
    }
}
