using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App.Templates;

/// <summary>The project templates <c>dotnet new</c> has installed for one SDK, with the known packages' kinds it lacks:
/// in families (Console, Unit Test, Web), each Type a template group (xUnit Test Project) with its languages, target
/// frameworks and options; and the solution formats <c>dotnet new sln</c> makes.</summary>
public sealed record TemplateCatalog(string Sdk, IReadOnlyList<TemplateFamily> Families, IReadOnlyList<string> SolutionFormats, string SolutionFormat)
{
    /// <summary>Every template, family by family.</summary>
    public IReadOnlyList<ProjectTemplate> Templates { get; } = [.. Families.SelectMany(f => f.Types)];

    /// <summary>What it offers, as text: two reads of the same templates (the watch's after the dialog's own) are the same.</summary>
    public string Shape { get; } = ShapeOf(Sdk, Families, SolutionFormats, SolutionFormat);

    private static string ShapeOf(string sdk, IReadOnlyList<TemplateFamily> families, IReadOnlyList<string> formats, string format)
    {
        static string Variant(TemplateVariant v) =>
            $"{v.Identity}:{v.Language}:{string.Join(',', v.Frameworks)}:{v.DefaultFramework}:{v.Description}:"
            + string.Join(',', v.Options.Select(o => $"{o.Name}={o.Default}/{o.Label}/{o.Kind}/{o.Choices.Count}/{o.EnabledWhen}"));
        IEnumerable<string> types = families.SelectMany(f => f.Types.Select(t =>
            $"{f.Name}|{t.TypeName}|{t.Name}|{string.Join(',', t.ShortNames)}|{t.IsSolution}|{t.Pack?.Id}|{t.DefaultName}|{string.Join(';', t.Variants.Select(Variant))}"));
        return string.Join('\n', [sdk, string.Join(',', formats), format, .. types]);
    }
}

/// <summary>A template group as <c>dotnet new list</c> shows it, by the name and short name of its newest template, made
/// of a template per language and framework band (<see cref="Variants"/>, newest first).</summary>
public sealed record ProjectTemplate(string Name, IReadOnlyList<string> ShortNames, string Author, IReadOnlyList<string> Classifications,
    string DefaultName, IReadOnlyList<TemplateVariant> Variants)
{
    /// <summary>The name it is created by.</summary>
    public string ShortName => ShortNames[0];

    /// <summary>The family New Project lists it in (<see cref="TemplateFamilies"/>).</summary>
    public string Family { get; init; } = Name;

    /// <summary>Its name in its family's Type chooser: its name without what the family's says ("xUnit" of xUnit Test
    /// Project in Unit Test).</summary>
    public string TypeName { get; init; } = Name;

    /// <summary>Whether it makes a whole solution (its type is "solution"), which only New Solution offers.</summary>
    public bool IsSolution { get; init; }

    /// <summary>The package that brings it when dotnet new has not got it yet; null for an installed template.</summary>
    public TemplatePack? Pack { get; init; }

    /// <summary>Its languages, C# first, as <c>dotnet new list</c> lists them.</summary>
    public IReadOnlyList<string> Languages =>
        [.. Variants.Select(v => v.Language).OfType<string>().Distinct().OrderBy(ProjectTemplates.LanguageOrder).ThenBy(l => l, StringComparer.Ordinal)];

    /// <summary>The frameworks it can target in a language, those of its newest templates first, as <c>dotnet new</c>'s
    /// help lists them.</summary>
    public IReadOnlyList<string> Frameworks(string? language) =>
        [.. Variants.Where(v => v.Language == language).SelectMany(v => v.Frameworks).Distinct()];

    /// <summary>The template <c>dotnet new</c> makes for a language and framework: the newest of the language that targets
    /// it.</summary>
    public TemplateVariant Variant(string? language, string? framework) =>
        Variants.FirstOrDefault(v => v.Language == language && (framework is null || v.Frameworks.Count == 0 || v.Frameworks.Contains(framework)))
        ?? Variants.FirstOrDefault(v => v.Language == language) ?? Variants[0];
}

/// <summary>One template of a group: its language, the frameworks it targets (with the option that sets them) and the
/// options an IDE shows for it.</summary>
public sealed record TemplateVariant(string Identity, string? Language, int Precedence, string Description, string? FrameworkOption,
    IReadOnlyList<string> Frameworks, string? DefaultFramework, IReadOnlyList<TemplateOption> Options)
{
    /// <summary>The option that keeps dotnet new from restoring what it made (skipRestore's), when the template restores.</summary>
    public string? SkipRestoreOption { get; init; }
}

public enum TemplateOptionKind
{
    Bool,
    Choice,
    Text,
    Integer,
    Float,
}

/// <summary>One of a template's options, as an IDE shows it: its label, kind, default and choices, the command line's
/// name for it, whether the checkbox means its opposite (ide.host.json's <c>invertBoolean</c>), and when it is
/// enabled.</summary>
public sealed record TemplateOption(string Name, string Option, string Label, string? Description, TemplateOptionKind Kind, string Default,
    IReadOnlyList<TemplateChoice> Choices, bool Inverted, bool Required, string? EnabledWhen);

public sealed record TemplateChoice(string Value, string Label, string? Description);

/// <summary>Reads the templates from what <c>dotnet new</c> itself keeps: <c>dotnet new list</c> brings its cache of the
/// installed templates up to date, and the cache (templatecache.json, per SDK under DOTNET_CLI_HOME or the home folder)
/// has each template's parameters. The options shown are those a template's ide.host.json makes visible. Read once per
/// SDK, ahead of the dialog and again on a change (ProjectTemplates.Ahead.cs), with the kinds of
/// <see cref="TemplatePacks"/> not installed, in families.</summary>
public static partial class ProjectTemplates
{
    private static readonly ConcurrentDictionary<(string Sdk, string? Hive), Lazy<Task<TemplateCatalog>>> Read = new();

    /// <summary>A member's name read in any letter case, as ide.host.json files write them both ways.</summary>
    private const bool AnyCase = true;

    /// <summary>What the dialog asks for itself (the name, the framework, the language), or a person never sets.</summary>
    private static readonly HashSet<string> NotShown = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "type", "language", "Framework", "TargetFrameworkOverride", "skipRestore", "langVersion", "HostIdentifier",
    };

    private static volatile string? customHive;

    /// <summary>Where dotnet new keeps the IDE's templates when not in its own home (its <c>--debug:custom-hive</c>); null
    /// for dotnet new's own. Changed once the templates are watched, the watch follows it.</summary>
    public static string? Hive
    {
        get => customHive;
        set
        {
            customHive = value;
            if (Volatile.Read(ref watching) == 1)
            {
                WatchPackages();
            }
        }
    }

    /// <summary>The .NET command line's arguments for <c>dotnet new</c> with these, in the IDE's hive.</summary>
    public static IReadOnlyList<string> New(IEnumerable<string> arguments) =>
        ["new", .. arguments, .. Hive is { } hive ? ["--debug:custom-hive", hive] : Array.Empty<string>()];

    /// <summary><c>dotnet new</c> with these arguments in a folder, in the IDE's hive.</summary>
    public static System.Diagnostics.ProcessStartInfo NewCommand(IEnumerable<string> arguments, string folder) =>
        ChildProcesses.DotnetCommand(New(arguments), folder);

    /// <summary><c>dotnet restore</c> of a project a template made, run in a folder: null once restored, else what dotnet
    /// said why not.</summary>
    public static async Task<string?> RestoreAsync(string project, string folder, CancellationToken cancel = default)
    {
        Task<ChildResult> restore = ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["restore", project], folder), limit: TimeSpan.FromMinutes(10), cancel: cancel);
        BackgroundWork.Track(restore);
        ChildResult restored = await restore;
        return restored.Succeeded ? null : restored.Said;
    }

    /// <summary>Lets go of the templates read, once a package is installed, so the next read lists its templates.</summary>
    public static void Forget() => Read.Clear();

    /// <summary>The templates of the SDK a folder builds with (its global.json's), read the first time they are asked for
    /// and kept until what they come from changes; a read that fails is tried again next time. Runs <c>dotnet</c>, so
    /// never on the window's thread.</summary>
    public static async Task<TemplateCatalog> LoadAsync(string folder)
    {
        string sdk = await SdkAsync(folder);
        Remember(folder);
        (string, string?) key = (sdk, Hive);
        Lazy<Task<TemplateCatalog>> once = Read.GetOrAdd(key, _ => new Lazy<Task<TemplateCatalog>>(() => ReadAsync(folder, sdk, key.Item2)));
        try
        {
            TemplateCatalog catalog = await once.Value;
            Newest[key] = catalog;
            Watch();
            return catalog;
        }
        catch
        {
            Read.TryRemove(KeyValuePair.Create(key, once));
            throw;
        }
    }

    /// <summary>The SDK a folder's dotnet runs (<see cref="Dotnet.SdkAsync"/>), kept as the one it said last.</summary>
    private static async Task<string> SdkAsync(string folder)
    {
        string sdk = await Dotnet.SdkAsync(folder);
        Sdks[folder] = sdk;
        lastSdk = sdk;
        return sdk;
    }

    internal static int LanguageOrder(string language) => language switch { "C#" => 0, "F#" => 1, "VB" => 2, _ => 3 };

    /// <summary>Whether an option is enabled by its template's condition ("UseMSTestSdk &amp;&amp; (TestRunner == MSTest)") over
    /// the values the options have now, a word no option is named being itself; one that does not read leaves it enabled.</summary>
    public static bool IsEnabled(string? condition, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }
        List<string> tokens = [.. Regex.Matches(condition, @"""[^""]*""|'[^']*'|==|!=|&&|\|\||[!()]|[^\s!=&|()""']+").Select(m => m.Value)];
        int at = 0;
        try
        {
            bool enabled = Or();
            return at != tokens.Count || enabled;
        }
        catch (FormatException)
        {
            return true;
        }

        bool Or()
        {
            bool value = And();
            while (Next("||"))
            {
                value = And() | value;
            }
            return value;
        }

        bool And()
        {
            bool value = Not();
            while (Next("&&"))
            {
                value = Not() & value;
            }
            return value;
        }

        bool Not()
        {
            if (Next("!"))
            {
                return !Not();
            }
            if (Next("("))
            {
                bool inner = Or();
                return Next(")") ? inner : throw new FormatException();
            }
            string left = Operand();
            if (Next("=="))
            {
                return string.Equals(left, Operand(), StringComparison.OrdinalIgnoreCase);
            }
            if (Next("!="))
            {
                return !string.Equals(left, Operand(), StringComparison.OrdinalIgnoreCase);
            }
            return left.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        string Operand()
        {
            if (at >= tokens.Count || tokens[at] is "==" or "!=" or "&&" or "||" or "!" or "(" or ")")
            {
                throw new FormatException();
            }
            string token = tokens[at++];
            return token[0] is '"' or '\'' ? token[1..^1] : values.TryGetValue(token, out string? value) ? value : token;
        }

        bool Next(string token)
        {
            if (at < tokens.Count && tokens[at] == token)
            {
                at++;
                return true;
            }
            return false;
        }
    }

    /// <summary>dotnet new list, which brings dotnet new's cache up to date, then the cache.</summary>
    private static async Task<TemplateCatalog> ReadAsync(string folder, string sdk, string? custom)
    {
        ChildResult listed = await ChildProcesses.RunAsync(NewCommand(["list", "--type", "project"], folder), limit: TimeSpan.FromMinutes(3));
        if (!listed.Succeeded)
        {
            throw new InvalidOperationException($"dotnet new list failed: {listed.Said}");
        }
        string root = TemplateRoot(custom)
            ?? throw new InvalidOperationException("Neither DOTNET_CLI_HOME nor the home folder is set, so dotnet new keeps no templates to read");
        string cache = CacheFile(root, sdk) ?? throw new InvalidOperationException($"dotnet new keeps no templates at {Path.Combine(root, "dotnetcli", sdk, "templatecache.json")}");
        return await ParseAsync(cache, sdk);
    }

    /// <summary>The cache dotnet new keeps of an SDK's templates, when it keeps one.</summary>
    private static string? CacheFile(string root, string sdk) =>
        new[] { Path.Combine(root, "dotnetcli", sdk, "templatecache.json"), Path.Combine(root, "dotnetcli", "v" + sdk, "templatecache.json") }.FirstOrDefault(File.Exists);

    /// <summary>The folder dotnet new keeps what it installs and caches in: .templateengine in its home, or the custom
    /// hive, which is what it keeps there; null when there is no home.</summary>
    private static string? TemplateRoot(string? custom) =>
        custom ?? (Dotnet.CliHome() is { } home ? Path.Combine(home, ".templateengine") : null);

    /// <summary>The templates in dotnet new's cache, each one's options from its ide.host.json, with the known packages'
    /// kinds it has not got, in families.</summary>
    private static async Task<TemplateCatalog> ParseAsync(string cache, string sdk)
    {
        using JsonDocument document = await ReadSharedAsync(cache);
        List<JsonElement> all = [.. document.RootElement.GetProperty("TemplateInfo").EnumerateArray()];
        using HostFiles ide = new();
        List<ProjectTemplate> templates = [];
        foreach (IGrouping<string, JsonElement> group in all.Where(t => Tag(t, "type") is "project" or "solution" && Usable(t)).GroupBy(t => JsonMembers.Scalar(t, "GroupIdentity", AnyCase) ?? JsonMembers.Scalar(t, "Identity", AnyCase)!))
        {
            List<TemplateVariant> variants = [];
            foreach (JsonElement template in group.OrderByDescending(t => Int(t, "Precedence")).ThenBy(t => LanguageOrder(Tag(t, "language") ?? string.Empty)))
            {
                if (Variant(template, ide) is { } variant)
                {
                    variants.Add(variant);
                }
            }
            if (variants.Count == 0)
            {
                continue;
            }
            JsonElement newest = group.Where(t => variants.Any(v => v.Identity == JsonMembers.Scalar(t, "Identity", AnyCase)))
                .OrderByDescending(t => Int(t, "Precedence")).ThenBy(t => LanguageOrder(Tag(t, "language") ?? string.Empty)).First();
            string name = JsonMembers.Scalar(newest, "Name", AnyCase) ?? JsonMembers.Scalar(newest, "Identity", AnyCase)!;
            templates.Add(new ProjectTemplate(name, [.. JsonMembers.Texts(newest, "ShortNameList", AnyCase)], JsonMembers.Scalar(newest, "Author", AnyCase) ?? string.Empty,
                [.. JsonMembers.Texts(newest, "Classifications", AnyCase)], DefaultName(newest, name), variants) { IsSolution = Tag(newest, "type") == "solution" });
        }
        (IReadOnlyList<string> formats, string format) = SolutionFormats(all);
        return new TemplateCatalog(sdk, TemplateFamilies.Of([.. templates, .. TemplatePacks.Offered(templates)]), formats, format);
    }

    /// <summary>The cache, read as another dotnet new may be writing it: a read that does not parse is tried again.</summary>
    private static async Task<JsonDocument> ReadSharedAsync(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return await JsonDocument.ParseAsync(stream, LenientJson.Document);
            }
            catch (Exception error) when (attempt < 3 && error is IOException or JsonException)
            {
                await Task.Delay(200);
            }
        }
    }

    /// <summary>Whether a template can make a project Passenger builds here: WPF and Windows Forms only on Windows, and a
    /// template that says which systems it runs on only on those.</summary>
    private static bool Usable(JsonElement template)
    {
        string[] classifications = [.. JsonMembers.Texts(template, "Classifications", AnyCase)];
        if (!OperatingSystem.IsWindows() && classifications.Any(c => c is "WPF" or "WinForms"))
        {
            return false;
        }
        foreach (JsonElement constraint in JsonMembers.Items(template, "Constraints"))
        {
            if (JsonMembers.Scalar(constraint, "Type", AnyCase) == "os" && JsonMembers.Scalar(constraint, "Args", AnyCase) is { } args)
            {
                using JsonDocument parsed = JsonDocument.Parse(args.TrimStart().StartsWith('[') || args.TrimStart().StartsWith('"') ? args : $"\"{args}\"", LenientJson.Document);
                string[] systems = parsed.RootElement.ValueKind == JsonValueKind.Array ? [.. parsed.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)] : [parsed.RootElement.GetString() ?? string.Empty];
                if (!systems.Any(s => OperatingSystem.IsOSPlatform(s.Equals("OSX", StringComparison.OrdinalIgnoreCase) ? "macOS" : s)))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>One template as a variant of its group: null when every framework it targets is .NET Framework, or
    /// Windows' alone off Windows, which Passenger does not build.</summary>
    private static TemplateVariant? Variant(JsonElement template, HostFiles ide)
    {
        Dictionary<string, JsonElement> parameters = new(StringComparer.Ordinal);
        foreach (JsonElement parameter in JsonMembers.Items(template, "Parameters", AnyCase))
        {
            if (JsonMembers.Scalar(parameter, "Name", AnyCase) is { } name)
            {
                parameters[name] = parameter;
            }
        }
        Dictionary<string, JsonElement> cli = HostSymbols(template);
        string? frameworkOption = null;
        List<string> frameworks = [];
        string? defaultFramework = null;
        if (parameters.TryGetValue("Framework", out JsonElement framework))
        {
            frameworkOption = LongName(cli, "Framework");
            List<string> offered = [.. Choices(framework).Select(c => c.Value)];
            frameworks = [.. offered.Where(Targetable)];
            if (offered.Count > 0 && frameworks.Count == 0)
            {
                return null;
            }
            defaultFramework = JsonMembers.Scalar(framework, "DefaultValue", AnyCase) is { } given && frameworks.Contains(given) ? given : frameworks.FirstOrDefault();
        }
        List<TemplateOption> options = [];
        if (ide.Read(template) is { } host)
        {
            // An IDE's options are the ones the template's ide.host.json makes visible, in its order.
            foreach (JsonElement symbol in JsonMembers.Items(host, "symbolInfo", AnyCase))
            {
                if (JsonMembers.Scalar(symbol, "id", AnyCase) is { } id && Bool(symbol, "isVisible") && !NotShown.Contains(id) && parameters.TryGetValue(id, out JsonElement parameter)
                    && Option(parameter, cli, symbol, inverted: Bool(symbol, "invertBoolean"), label: JsonMembers.Scalar(JsonMembers.Object(symbol, "name", AnyCase), "text", AnyCase)) is { } option)
                {
                    options.Add(option);
                }
            }
            if (JsonMembers.Scalar(host, "disableHttpsSymbol", AnyCase) is { } https && parameters.TryGetValue(https, out JsonElement noHttps) && options.All(o => o.Name != https)
                && Option(noHttps, cli, null, inverted: true, label: "Configure for HTTPS") is { } configure)
            {
                options.Add(configure);
            }
        }
        else
        {
            foreach ((string name, JsonElement parameter) in parameters)
            {
                bool hidden = cli.TryGetValue(name, out JsonElement symbol) && Bool(symbol, "isHidden");
                if (!NotShown.Contains(name) && !hidden && Option(parameter, cli, null, inverted: false, label: null) is { } option)
                {
                    options.Add(option);
                }
            }
        }
        return new TemplateVariant(JsonMembers.Scalar(template, "Identity", AnyCase)!, Tag(template, "language"), Int(template, "Precedence"), JsonMembers.Scalar(template, "Description", AnyCase) ?? string.Empty,
            frameworkOption, frameworks, defaultFramework, options)
        {
            SkipRestoreOption = parameters.ContainsKey("skipRestore") ? LongName(cli, "skipRestore") : null,
        };
    }

    /// <summary>A framework Passenger builds for here: no .NET Framework (net48), and Windows' own only on Windows.</summary>
    private static bool Targetable(string framework) =>
        !Regex.IsMatch(framework, @"^net\d+$", RegexOptions.IgnoreCase)
        && (OperatingSystem.IsWindows() || !framework.EndsWith("-windows", StringComparison.OrdinalIgnoreCase));

    private static TemplateOption? Option(JsonElement parameter, Dictionary<string, JsonElement> cli, JsonElement? ide, bool inverted, string? label)
    {
        string name = JsonMembers.Scalar(parameter, "Name", AnyCase)!;
        if (JsonMembers.Scalar(parameter, "Type", AnyCase) is { } type && type != "parameter")
        {
            return null;
        }
        JsonElement? precedence = JsonMembers.Object(parameter, "Precedence", AnyCase);
        // Implicit (the name, the type and language) and Disabled are never asked of a person.
        int definition = precedence is { } ? Int(precedence, "PrecedenceDefinition") : 2;
        if (definition is 3 or 5)
        {
            return null;
        }
        TemplateOptionKind kind = (JsonMembers.Scalar(parameter, "DataType", AnyCase) ?? "string").ToLowerInvariant() switch
        {
            "bool" or "boolean" => TemplateOptionKind.Bool,
            "choice" => TemplateOptionKind.Choice,
            "int" or "integer" => TemplateOptionKind.Integer,
            "float" => TemplateOptionKind.Float,
            _ => TemplateOptionKind.Text,
        };
        List<TemplateChoice> choices = [.. Choices(parameter)];
        if (kind == TemplateOptionKind.Choice && choices.Count == 0)
        {
            return null;
        }
        string shown = Plain(label ?? JsonMembers.Scalar(parameter, "DisplayName", AnyCase) ?? JsonMembers.Scalar(parameter, "Description", AnyCase) ?? name);
        string? description = ide is { } symbol ? JsonMembers.Scalar(JsonMembers.Object(symbol, "description", AnyCase), "text", AnyCase) : null;
        string fallback = kind switch { TemplateOptionKind.Bool => "false", TemplateOptionKind.Choice => choices[0].Value, _ => string.Empty };
        return new TemplateOption(name, LongName(cli, name), shown, Plain(description ?? JsonMembers.Scalar(parameter, "Description", AnyCase) ?? string.Empty), kind,
            JsonMembers.Scalar(parameter, "DefaultValue", AnyCase) ?? fallback, choices, inverted && kind == TemplateOptionKind.Bool,
            Bool(precedence, "IsRequired"), JsonMembers.Scalar(precedence, "IsEnabledCondition", AnyCase));
    }

    private static IEnumerable<TemplateChoice> Choices(JsonElement parameter)
    {
        foreach (JsonProperty choice in JsonMembers.Properties(parameter, "Choices"))
        {
            string? display = JsonMembers.Scalar(choice.Value, "DisplayName", AnyCase);
            yield return new TemplateChoice(choice.Name, string.IsNullOrWhiteSpace(display) ? choice.Name : display, JsonMembers.Scalar(choice.Value, "Description", AnyCase));
        }
    }

    /// <summary>A label without its access-key underscores ("Do not use _top-level statements").</summary>
    private static string Plain(string text) => Regex.Replace(text, "_(_?)", "$1");

    /// <summary>The command line's name for an option: its dotnetcli.host.json long name, else its own.</summary>
    private static string LongName(Dictionary<string, JsonElement> cli, string name) =>
        cli.TryGetValue(name, out JsonElement symbol) && JsonMembers.Scalar(symbol, "longName", AnyCase) is { Length: > 0 } longName ? longName : name;

    private static Dictionary<string, JsonElement> HostSymbols(JsonElement template)
    {
        Dictionary<string, JsonElement> symbols = new(StringComparer.Ordinal);
        if (JsonMembers.Scalar(template, "HostData", AnyCase) is { Length: > 0 } data)
        {
            using JsonDocument host = JsonDocument.Parse(data, LenientJson.Document);
            if (JsonMembers.Object(host.RootElement, "symbolInfo", AnyCase) is { } info)
            {
                foreach (JsonProperty symbol in info.EnumerateObject())
                {
                    symbols[symbol.Name] = symbol.Value.Clone();
                }
            }
        }
        return symbols;
    }

    /// <summary>The name a new project of it gets, before a number makes it unique: the template's default name, else its
    /// name without spaces or punctuation.</summary>
    private static string DefaultName(JsonElement template, string name)
    {
        string given = JsonMembers.Scalar(template, "DefaultName", AnyCase) ?? string.Empty;
        given = Regex.Replace(given, @"\d+$", string.Empty);
        return given.Length > 0 ? given : new string([.. name.Where(char.IsLetterOrDigit)]);
    }

    /// <summary>The formats dotnet new sln makes, and the one it makes by default: a .sln alone where it has no choice.</summary>
    private static (IReadOnlyList<string> Formats, string Default) SolutionFormats(List<JsonElement> all)
    {
        JsonElement? solution = all.Where(t => JsonMembers.Texts(t, "ShortNameList", AnyCase).Contains("sln")).OrderByDescending(t => Int(t, "Precedence")).Cast<JsonElement?>().FirstOrDefault();
        JsonElement? format = JsonMembers.Items(solution, "Parameters").Where(p => JsonMembers.Scalar(p, "Name", AnyCase) == "Format").Cast<JsonElement?>().FirstOrDefault();
        if (format is not { } choice)
        {
            return (["sln"], "sln");
        }
        List<string> formats = [.. Choices(choice).Select(c => c.Value)];
        string preferred = JsonMembers.Scalar(choice, "DefaultValue", AnyCase) is { } given && formats.Contains(given) ? given : formats.FirstOrDefault() ?? "sln";
        return (formats.Count == 0 ? ["sln"] : [.. formats.OrderBy(f => f == preferred ? 0 : 1)], preferred);
    }

    private static string? Tag(JsonElement template, string tag) =>
        JsonMembers.Scalar(JsonMembers.Object(template, "TagsCollection"), tag, AnyCase);

    /// <summary>A flag as dotnet new's files write it, <c>true</c> or <c>"true"</c>; false when it is neither.</summary>
    private static bool Bool(JsonElement? element, string name) =>
        JsonMembers.Bool(element, name, AnyCase) ?? (bool.TryParse(JsonMembers.Text(element, name, AnyCase), out bool value) && value);

    /// <summary>A whole number as dotnet new's files write it, a number or its text; 0 when it is neither.</summary>
    private static int Int(JsonElement? element, string name) => int.TryParse(JsonMembers.Scalar(element, name, AnyCase), out int value) ? value : 0;

    /// <summary>The ide.host.json beside each template's template.json, in its package (or folder), each package opened once.</summary>
    private sealed class HostFiles : IDisposable
    {
        private readonly Dictionary<string, ZipArchive?> packages = new(StringComparer.Ordinal);
        private readonly List<JsonDocument> read = [];

        public JsonElement? Read(JsonElement template)
        {
            if (JsonMembers.Scalar(template, "MountPointUri", AnyCase) is not { } mount || JsonMembers.Scalar(template, "ConfigPlace", AnyCase) is not { } place)
            {
                return null;
            }
            if (mount.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(mount, UriKind.Absolute, out Uri? uri))
            {
                mount = uri.LocalPath;
            }
            string entry = $"{place.TrimStart('/')[..(place.TrimStart('/').LastIndexOf('/') + 1)]}ide.host.json";
            try
            {
                string? text = null;
                if (Directory.Exists(mount))
                {
                    string file = Path.Combine(mount, entry);
                    text = File.Exists(file) ? File.ReadAllText(file) : null;
                }
                else if (Package(mount)?.GetEntry(entry) is { } zipped)
                {
                    using StreamReader reader = new(zipped.Open());
                    text = reader.ReadToEnd();
                }
                if (text is null)
                {
                    return null;
                }
                JsonDocument host = JsonDocument.Parse(text, LenientJson.Document);
                read.Add(host);
                return host.RootElement;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private ZipArchive? Package(string path)
        {
            if (!packages.TryGetValue(path, out ZipArchive? archive))
            {
                archive = File.Exists(path) ? ZipFile.OpenRead(path) : null;
                packages[path] = archive;
            }
            return archive;
        }

        public void Dispose()
        {
            foreach (ZipArchive? archive in packages.Values)
            {
                archive?.Dispose();
            }
            foreach (JsonDocument document in read)
            {
                document.Dispose();
            }
        }
    }
}
