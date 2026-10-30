using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.Markup;
using Passenger.Languages.TextMate;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.Languages;

/// <summary>What answers for a language's files beyond the editor: the C# language server (C# and Razor), Passenger's
/// XAML, an MSBuild evaluation, one of the web languages' servers, or nothing.</summary>
public enum LanguageService
{
    None,
    CSharp,
    Xaml,
    MSBuild,
    TypeScript,
    Html,
    Css,
    Json,
    Dockerfile,
    Yaml,
}

/// <summary>A file name a language claims, matched without regard to case: a name, or one <c>*</c> standing for any run
/// (<c>*.tsx</c>, <c>Dockerfile.*</c>, <c>tsconfig*.json</c>), after the folders the file must be in where it names
/// them (<c>.vscode/*.json</c>), with the language id a server is told for it where that is not the language's own
/// (<c>javascriptreact</c>).</summary>
public sealed record FilePattern(string Glob, string? Id = null)
{
    private readonly int slash = Glob.LastIndexOf('/');
    private readonly int star = Glob.IndexOf('*', Glob.LastIndexOf('/') + 1);

    public static implicit operator FilePattern(string glob) => new(glob);

    public bool Matches(ReadOnlySpan<char> path)
    {
        ReadOnlySpan<char> name = Path.GetFileName(path);
        ReadOnlySpan<char> glob = Glob.AsSpan(slash + 1);
        bool named = star < 0
            ? name.Equals(glob, StringComparison.OrdinalIgnoreCase)
            : name.Length >= glob.Length - 1 && name.StartsWith(Glob.AsSpan(slash + 1, star - slash - 1), StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(Glob.AsSpan(star + 1), StringComparison.OrdinalIgnoreCase);
        return named && (slash < 0 || In(Path.GetDirectoryName(path), Glob.AsSpan(0, slash)));
    }

    /// <summary>Whether a folder's path ends in the folders named, the innermost last.</summary>
    private static bool In(ReadOnlySpan<char> folder, ReadOnlySpan<char> folders)
    {
        while (folders.Length > 0)
        {
            int last = folders.LastIndexOf('/');
            if (!Path.GetFileName(folder).Equals(folders[(last + 1)..], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            folders = last < 0 ? [] : folders[..last];
            folder = Path.GetDirectoryName(folder);
        }
        return true;
    }
}

/// <summary>Every language a file can be in, and what each gives its files before any language server answers: its id
/// (VS Code's, which a server is told), the name the IDE says, the file names it claims, its comments, its default
/// indent, the grammar that colours it, how its folds are found and what serves it. A file is in the first language,
/// as they are declared here, whose pattern its name matches, so names (Dockerfile.dev, .env.local) come before
/// extensions; one matching none is plain text. A language is compared as itself, never by its name. A new TextMate
/// language is one entry here, its grammar under <c>TextMate/Grammars</c> and its notice.</summary>
public sealed class Language
{
    /// <summary>Every language in the order declared, each adding itself as it is made, so this comes before them.</summary>
    private static readonly List<Language> All = [];

    /// <summary>Scopes the grammars here embed for a language under another name than its own grammar's, each read by the
    /// grammar its files are coloured by: JSON with comments and JSON Lines by JSON's (which reads comments), and HTML
    /// inside another document (Markdown's) by HTML's.</summary>
    private static readonly (string Scope, string Carried)[] EmbeddedAs =
        [("source.json.comments", "source.json"), ("source.json.lines", "source.json"), ("text.html.derivative", "text.html.basic")];

    /// <summary>Each grammar's file by the scope it names, every language's, for the grammars that embed others, and by
    /// the scopes of <see cref="EmbeddedAs"/>.</summary>
    private static readonly Lazy<Dictionary<string, string>> GrammarFiles = new(() =>
    {
        Dictionary<string, string> files = All.Select(l => l.Grammar).OfType<GrammarFile>()
            .DistinctBy(g => g.Scope).ToDictionary(g => g.Scope, g => g.File, StringComparer.Ordinal);
        foreach ((string scope, string carried) in EmbeddedAs)
        {
            files[scope] = files[carried];
        }
        return files;
    });

    private readonly IReadOnlyList<FilePattern> patterns;

    private Language([IdentifierOf(typeof(LanguageIds))] string id, string name, IReadOnlyList<FilePattern> patterns, string? line = null,
        (string Start, string End)? block = null, int indent = 4, GrammarFile? grammar = null, Func<TextDocument, IFoldFinder>? folds = null,
        LanguageService service = LanguageService.None, MarkupKind markup = MarkupKind.None, bool reindentsPaste = true, bool parsed = false)
    {
        Id = id;
        Name = name;
        this.patterns = patterns;
        LineComment = line;
        BlockComment = block;
        Indent = new IndentOptions(indent);
        Grammar = grammar;
        Folds = folds;
        Service = service;
        Markup = markup;
        ReindentsPaste = reindentsPaste;
        Parsed = parsed;
        All.Add(this);
    }

    /// <summary>What a file matching no language is, claiming none itself.</summary>
    public static readonly Language PlainText = new("plaintext", "Text", [], reindentsPaste: false);

    /// <summary>What an image's or a font's preview is, as the status bar and the tools name it: no text, so no comments.</summary>
    public static readonly Language Image = new("image", "Image", []);
    public static readonly Language Font = new("font", "Font", []);

    /// <summary>Ignore files, a Dockerfile's own (<c>Dockerfile.dockerignore</c>) among them, so before Dockerfile.</summary>
    public static readonly Language Ignore = new("ignore", "Ignore", ["*.dockerignore", "*.gitignore", ".*ignore"], line: "#");

    public static readonly Language Dockerfile = new("dockerfile", "Dockerfile",
        ["Dockerfile", "Dockerfile.*", "Containerfile", "Containerfile.*", "*.dockerfile"], line: "#",
        grammar: new("source.dockerfile", "docker.tmLanguage.json"), service: LanguageService.Dockerfile);

    public static readonly Language Dotenv = new("dotenv", "Dotenv", [".env", ".env.*", "*.env"], line: "#");

    /// <summary>An .editorconfig, coloured as VS Code colours it, by the INI grammar.</summary>
    public static readonly Language EditorConfig = new("properties", "EditorConfig", [".editorconfig"], line: "#",
        grammar: new("source.ini", "ini.tmLanguage.json"));

    /// <summary>C#, coloured and folded by its own parse; its grammar is only what Razor and Markdown's fences embed.</summary>
    public static readonly Language CSharp = new(LanguageIds.CSharp, "C#", ["*.cs", "*.csx"], line: "//", block: ("/*", "*/"),
        grammar: new("source.cs", "csharp.tmLanguage.json"), service: LanguageService.CSharp, parsed: true);

    public static readonly Language Razor = new(LanguageIds.Razor, "Razor", ["*" + RazorFiles.Component, "*" + RazorFiles.Page], block: ("@*", "*@"),
        grammar: new("text.aspnetcorerazor", "aspnetcorerazor.tmLanguage.json", Tags.Html), service: LanguageService.CSharp);

    /// <summary>XAML, MSBuild's files and the rest of XML are one markup to the editor, each named and answered as its own.</summary>
    public static readonly Language Xaml = XmlKind("xaml", "XAML", ["*.axaml", "*.paml", "*.xaml"], LanguageService.Xaml);

    public static readonly Language MSBuild = XmlKind("msbuild", "MSBuild", ["*.csproj", "*.fsproj", "*.vbproj", "*.proj", "*.props", "*.targets"], LanguageService.MSBuild);

    public static readonly Language Xml = XmlKind("xml", "XML",
        ["*.xml", "*.slnx", "*.resx", "*.config", "*.nuspec", "*.manifest", "*.runsettings", "*.xsd", "*.xslt", "*.xsl", "*.svg", "*.plist", "*.storyboard"],
        LanguageService.None);

    /// <summary>JSON, told to its server as JSON with comments where the file's reader takes them: the files VS Code reads
    /// so (its json, configuration-editing and typescript-basics extensions' jsonc names, and every JSON file under
    /// .vscode), and .NET's (ASP.NET Core's settings and launch profiles, the SDK's global.json, a solution filter).</summary>
    public static readonly Language Json = new("json", "JSON",
        [.. ((string[])["*.jsonc", "*.eslintrc", "*.eslintrc.json", "*.jsfmtrc", "*.jshintrc", "*.swcrc", "*.hintrc", "*.babelrc", "*.toolset.jsonc",
            "babel.config.json", "bun.lock", ".babelrc.json", ".ember-cli", "typedoc.json", ".github/hooks/*.json",
            "*.code-workspace", "*language-configuration.json", "*icon-theme.json", "*color-theme.json", "settings.json", "launch.json", "tasks.json",
            "mcp.json", "keybindings.json", "extensions.json", "argv.json", "profiles.json", "devcontainer.json", ".devcontainer.json", ".vscode/*.json",
            "tsconfig*.json", "jsconfig*.json", "appsettings*.json", "launchSettings.json", "global.json", "*.slnf"]).Select(glob => new FilePattern(glob, "jsonc")),
            "*.json"],
        line: "//", block: ("/*", "*/"), indent: 2, grammar: new("source.json", "JSON.tmLanguage.json"), folds: FoldScan.Json, service: LanguageService.Json);

    public static readonly Language Yaml = new("yaml", "YAML", ["*.yml", "*.yaml"], line: "#", indent: 2,
        grammar: new("source.yaml", "yaml.tmLanguage.json"), service: LanguageService.Yaml);

    public static readonly Language Markdown = new("markdown", "Markdown", ["*.md", "*.markdown"], block: ("<!--", "-->"), indent: 2,
        grammar: new("text.html.markdown", "markdown.tmLanguage.json"), reindentsPaste: false);

    public static readonly Language Html = new("html", "HTML", ["*.html", "*.htm"], block: ("<!--", "-->"),
        grammar: new("text.html.basic", "html.tmLanguage.json", Tags.Html), service: LanguageService.Html, markup: MarkupKind.Html);

    public static readonly Language Css = new("css", "CSS", ["*.css"], block: ("/*", "*/"),
        grammar: new("source.css", "css.tmLanguage.json"), service: LanguageService.Css);

    /// <summary>SCSS; its grammar includes SassDoc's (its <c>///</c> comments' tags), which is not carried, so those stay comments.</summary>
    public static readonly Language Scss = new("scss", "SCSS", ["*.scss"], line: "//", block: ("/*", "*/"),
        grammar: new("source.css.scss", "scss.tmLanguage.json"), service: LanguageService.Css);

    public static readonly Language Less = new("less", "Less", ["*.less"], line: "//", block: ("/*", "*/"),
        grammar: new("source.css.less", "less.tmLanguage.json"), service: LanguageService.Css);

    public static readonly Language JavaScript = new("javascript", "JavaScript", ["*.js", "*.mjs", "*.cjs", new("*.jsx", "javascriptreact")],
        line: "//", block: ("/*", "*/"), grammar: new("source.js", "JavaScript.tmLanguage.json"), service: LanguageService.TypeScript);

    public static readonly Language TypeScript = new("typescript", "TypeScript", ["*.ts", "*.mts", "*.cts"],
        line: "//", block: ("/*", "*/"), grammar: new("source.ts", "TypeScript.tmLanguage.json"), service: LanguageService.TypeScript);

    /// <summary>TypeScript with JSX (a .tsx file), coloured by a grammar that reads its elements as markup, and named
    /// TypeScript.</summary>
    public static readonly Language TypeScriptReact = new("typescriptreact", "TypeScript", ["*.tsx"],
        line: "//", block: ("/*", "*/"), grammar: new("source.tsx", "TypeScriptReact.tmLanguage.json"), service: LanguageService.TypeScript);

    public static readonly Language Shell = new("shellscript", "Shell", ["*.sh", "*.bash", "*.zsh"], line: "#",
        grammar: new("source.shell", "shell-unix-bash.tmLanguage.json"));

    /// <summary>HTTP requests, as the Endpoints window reads them, coloured by REST Client's grammar, which colours a JSON or
    /// XML body and a curl line by the grammars here (a GraphQL body's not being carried, it stays plain).</summary>
    public static readonly Language Http = new("http", "HTTP", ["*.http", "*.rest"], line: "#",
        grammar: new("source.http", "http.tmLanguage.json"));

    /// <summary>Languages with no grammar here, commented as C's are or as scripts are.</summary>
    public static readonly Language Code = new("code", "Code", ["*.rs", "*.go", "*.java", "*.kt", "*.swift", "*.c", "*.h", "*.cpp"],
        line: "//", block: ("/*", "*/"));

    public static readonly Language Script = new("script", "Script", ["*.py", "*.toml", "*.ps1", "*.rb", "justfile"], line: "#");

    /// <summary>VS Code's id for the language, which a language server is told unless a file's pattern names another.</summary>
    [IdentifierOf(typeof(LanguageIds))]
    public string Id { get; }

    /// <summary>The language as the status bar and the tools name it.</summary>
    public string Name { get; }

    public string? LineComment { get; }

    /// <summary>The block comment's two ends, or null for a language with none; a language with no line comment of its own
    /// comments a line with it.</summary>
    public (string Start, string End)? BlockComment { get; }

    /// <summary>How a file of the language indents when neither an .editorconfig nor its own lines say.</summary>
    public IndentOptions Indent { get; }

    /// <summary>What answers for its files beyond the editor.</summary>
    public LanguageService Service { get; }

    /// <summary>Which markup the language is, whose tags indentation follows: XML's (XAML, MSBuild) or HTML's.</summary>
    public MarkupKind Markup { get; }

    /// <summary>Whether a pasted block takes the file's indentation: not where indentation is the text's own (Markdown's
    /// code blocks, plain text).</summary>
    public bool ReindentsPaste { get; }

    internal GrammarFile? Grammar { get; }

    /// <summary>What scans a file of the language for its folds, where its colours' parse does not find them.</summary>
    internal Func<TextDocument, IFoldFinder>? Folds { get; }

    /// <summary>Whether the language is coloured and folded by its own parse in-process (C#'s) rather than by its grammar.</summary>
    internal bool Parsed { get; }

    /// <summary>The language a file is in, by its name (and its folders, where a pattern names them).</summary>
    public static Language ForPath(string path)
    {
        foreach (Language language in All)
        {
            if (language.PatternOf(path) is not null)
            {
                return language;
            }
        }
        return PlainText;
    }

    /// <summary>The language id a server is told a file of this language is in: its pattern's, else the language's own.</summary>
    public string IdOf(string path) => PatternOf(path)?.Id ?? Id;

    /// <summary>The indentation a file's own lines show, read as tags where the language is markup.</summary>
    public IndentOptions? Detect(ReadOnlySpan<char> text) => Indentation.Detect(text, Indent.TabWidth, markup: Markup != MarkupKind.None);

    public override string ToString() => Name;

    /// <summary>The file a grammar a language's grammar embeds is carried in, by its scope; null for one not carried.</summary>
    internal static string? GrammarFileOf(string scope) => GrammarFiles.Value.GetValueOrDefault(scope);

    private static Language XmlKind(string id, string name, IReadOnlyList<FilePattern> patterns, LanguageService service) =>
        new(id, name, patterns, block: ("<!--", "-->"), indent: 2, grammar: new("text.xml", "xml.tmLanguage.json", Tags.Xml), folds: FoldScan.Xml,
            service: service, markup: MarkupKind.Xml);

    private FilePattern? PatternOf(string path)
    {
        foreach (FilePattern pattern in patterns)
        {
            if (pattern.Matches(path))
            {
                return pattern;
            }
        }
        return null;
    }
}
