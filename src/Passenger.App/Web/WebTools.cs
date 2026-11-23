using System.Text.Json;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.App.Web;

/// <summary>One of the folder's own linters and formatters for the web languages (<see cref="FolderTools"/>): ESLint, through
/// the language server Passenger carries, which loads each file's own eslint; or Oxlint, Oxfmt or Biome, each through the
/// language server its package installed in the folder has (its bin's <see cref="Arguments"/>), run wherever the folder's
/// configuration names it (<see cref="NamedFor"/>). What it gives: problems as typed (<see cref="Lints"/>), its fixes, and
/// formatting (<see cref="Formats"/>).</summary>
public sealed class WebTool
{
    private WebTool(string name, string? package, string? command, IReadOnlyList<string> arguments, IReadOnlyList<string> configurations, bool lints, bool formats, string? fixAll)
    {
        Name = name;
        Package = package;
        Command = command;
        Arguments = arguments;
        Configurations = configurations;
        Lints = lints;
        Formats = formats;
        FixAll = fixAll;
    }

    /// <summary>ESLint, as Settings configure it (<see cref="FolderTools"/>'s own part for it).</summary>
    public static readonly WebTool Eslint = new("ESLint", null, null, [], [], lints: true, formats: false, "source.fixAll.eslint");

    /// <summary>Oxc's linter: <c>oxlint --lsp</c>, its configuration a <c>.oxlintrc.json</c> or <c>oxlint.config.ts</c>.</summary>
    public static readonly WebTool Oxlint = new("Oxlint", "oxlint", "oxlint", ["--lsp"],
        [".oxlintrc.json", ".oxlintrc.jsonc", "oxlint.config.ts", "oxlint.config.mts"], lints: true, formats: false, "source.fixAll.oxc");

    /// <summary>Oxc's formatter: <c>oxfmt --lsp</c>, its configuration a <c>.oxfmtrc.json</c> or <c>oxfmt.config.ts</c>.</summary>
    public static readonly WebTool Oxfmt = new("Oxfmt", "oxfmt", "oxfmt", ["--lsp"],
        [".oxfmtrc.json", ".oxfmtrc.jsonc", "oxfmt.config.ts", "oxfmt.config.mts"], lints: false, formats: true, null);

    /// <summary>Biome, linter and formatter: <c>biome lsp-proxy</c>, which serves through the Biome daemon of its version it
    /// starts, or joins; its configuration a <c>biome.json</c>.</summary>
    public static readonly WebTool Biome = new("Biome", "@biomejs/biome", "biome", ["lsp-proxy"],
        ["biome.json", "biome.jsonc", ".biome.json", ".biome.jsonc"], lints: true, formats: true, "source.fixAll.biome");

    /// <summary>Every tool, in the order a file's problems and fixes are listed.</summary>
    public static readonly IReadOnlyList<WebTool> All = [Eslint, Oxlint, Biome, Oxfmt];

    /// <summary>The tools a folder's own package runs, beside ESLint.</summary>
    public static readonly IReadOnlyList<WebTool> Installed = [Oxlint, Biome, Oxfmt];

    /// <summary>Its name as the status bar, its marks and its fixes say it.</summary>
    public string Name { get; }

    /// <summary>The npm package that runs it, null for ESLint, whose server is Passenger's.</summary>
    public string? Package { get; }

    /// <summary>Its package's command (a <c>bin</c> of its package.json) that serves the language server.</summary>
    public string? Command { get; }

    /// <summary>What its command is given to serve over standard input and output.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>The names of the files that configure it, as its server watches them.</summary>
    public IReadOnlyList<string> Configurations { get; }

    /// <summary>Whether it finds problems, shown with the file's others, and fixes them.</summary>
    public bool Lints { get; }

    /// <summary>Whether it lays a file out, for Reformat Code and Reformat on save.</summary>
    public bool Formats { get; }

    /// <summary>The kind of its code action that fixes all it can in a file, null for none.</summary>
    public string? FixAll { get; }

    /// <summary>Whether a file of this name configures it (its own files, or a package.json, which may list it).</summary>
    public bool Configures(string name) => name == "package.json" || Configurations.Contains(name);

    /// <summary>The nearest of its configuration files from the file's folder up, else the nearest package.json there that
    /// lists its package, in the person's folders (<see cref="FoldersUp"/>); null where the folder names it nowhere.</summary>
    public string? NamedFor(string file)
    {
        if (Package is not { } package)
        {
            return null;
        }
        foreach (string folder in Path.GetDirectoryName(file) is { } start ? FoldersUp.From(start) : [])
        {
            foreach (string name in Configurations)
            {
                if (FoldersUp.Found(folder, name) is { } configuration && File.Exists(configuration))
                {
                    return configuration;
                }
            }
            if (FoldersUp.Found(folder, "package.json") is { } manifest && File.Exists(manifest) && ProjectPackages.Names(manifest, package))
            {
                return manifest;
            }
        }
        return null;
    }

    /// <summary>Its package installed for a file the folder names it for, in a trusted folder (whose configuration is code):
    /// the package's folder, null where it does not serve the file.</summary>
    public string? PackageFor(string file) =>
        Package is { } package && WorkspaceTrust.Trusts(file) && NamedFor(file) is not null ? ProjectPackages.Installed(file, package) : null;

    /// <summary>The package.json that lists its package for a file it is not installed for: what <c>npm install</c> there
    /// would put in place. Null in a folder not trusted, or where it is installed or listed nowhere.</summary>
    public string? Missing(string file) =>
        Package is { } package && WorkspaceTrust.Trusts(file) ? ProjectPackages.Missing(file, package) : null;

    /// <summary>The script its package's command runs (its package.json's <c>bin</c>), in its folder or a Plug'n'Play
    /// project's archive; null where the package names none there.</summary>
    public string? ScriptOf(string folder)
    {
        try
        {
            if (YarnPnp.ReadFile(Path.Combine(folder, "package.json")) is not { } text)
            {
                return null;
            }
            using JsonDocument read = JsonDocument.Parse(text, LenientJson.Document);
            string? bin = JsonMembers.Value(read.RootElement, "bin") is { ValueKind: JsonValueKind.String } only ? only.GetString()
                : JsonMembers.Text(JsonMembers.Object(read.RootElement, "bin"), Command!);
            return bin is { Length: > 0 } && Path.GetFullPath(bin, folder) is { } script && PathNames.IsUnder(script, folder) && YarnPnp.Exists(script) ? script : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The folder a package's server works in and is told is its workspace: the project the package is installed
    /// for, where its configuration lies: the folder holding the node_modules it is in, or a Plug'n'Play project's, whose
    /// .pnp.cjs places a package in an archive.</summary>
    public static string ProjectOf(string package)
    {
        for (string? at = Path.GetDirectoryName(package); at is not null; at = Path.GetDirectoryName(at))
        {
            if (Path.GetFileName(at) == "node_modules" && Path.GetDirectoryName(at) is { } project && Directory.Exists(project))
            {
                return project;
            }
        }
        return YarnPnp.ManifestFor(package) is { } manifest ? Path.GetDirectoryName(manifest)! : Path.GetDirectoryName(package) ?? package;
    }

    /// <summary>One of its fixes, named as the editor lists it ("Oxlint: Disable prefer-const for this line"), its rule
    /// where the action names one, from the file's share of it.</summary>
    internal LintFix Named(CodeAction action, ToolDocument from)
    {
        if (this != Eslint)
        {
            return new LintFix($"{Name}: {action.Title}", action, null, from);
        }
        string? rule = action.Command?.Arguments is [JsonElement first, ..] ? JsonMembers.Text(first, "ruleId") : null;
        string title = action.Command?.Command switch
        {
            "eslint.applySingleFix" when rule is not null => $"ESLint: Fix '{rule}'",
            "eslint.applyAllFixes" => "ESLint: Fix current file",
            "eslint.applyDisableLine" when rule is not null => $"ESLint: Suppress '{rule}' for current line",
            "eslint.applyDisableFile" when rule is not null => $"ESLint: Suppress '{rule}' for current file",
            "eslint.openRuleDoc" when rule is not null => $"ESLint: Show documentation for '{rule}'",
            // Its rule is in its title alone ("Fix all prefer-const problems").
            "eslint.applySameFixes" when action.Title.StartsWith("Fix all ", StringComparison.Ordinal) && action.Title.EndsWith(" problems", StringComparison.Ordinal)
                => $"ESLint: Fix all '{action.Title["Fix all ".Length..^" problems".Length]}' problems",
            _ => $"ESLint: {action.Title}",
        };
        return new LintFix(title, action, rule, from);
    }

    /// <summary>Where a fix is listed among the tool's: ESLint's each rule's fix, its suggestions, the file's, then the
    /// suppressions and the rule's page; another's as its server lists them, its preferred first.</summary>
    internal int Rank(LintFix fix) => this != Eslint ? (fix.Action.IsPreferred == true ? 0 : 1) : fix.Action.Command?.Command switch
    {
        "eslint.applySingleFix" => 0,
        "eslint.applySuggestion" => 1,
        "eslint.applyAllFixes" => 2,
        "eslint.applySameFixes" => 3,
        "eslint.applyDisableLine" => 4,
        "eslint.applyDisableFile" => 5,
        _ => 6,
    };

    public override string ToString() => Name;
}

/// <summary>One of a linter's fixes, named as the editor lists it ("ESLint: Fix 'prefer-const'", "Biome: Apply unsafe fix
/// for noDebugger"), with the server's code action that makes it and the file's share of the tool it came from.</summary>
public sealed record LintFix(string Title, CodeAction Action, string? Rule, ToolDocument From)
{
    /// <summary>A rule's page to open rather than an edit to make.</summary>
    public bool OpensDocumentation => Action.Command?.Command == "eslint.openRuleDoc";
}
