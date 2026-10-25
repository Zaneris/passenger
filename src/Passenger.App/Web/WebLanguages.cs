using System.Diagnostics;
using Passenger.App.Settings;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Languages;
using Passenger.Lsp;

namespace Passenger.App.Web;

/// <summary>The language servers of a web project's files.</summary>
public enum WebServerKind
{
    /// <summary>typescript-language-server over TypeScript's own tsserver: .ts, .tsx, .js, .jsx, .mjs, .cjs.</summary>
    TypeScript,

    /// <summary>VS Code's HTML server, which answers a page's &lt;style&gt; and &lt;script&gt; too, and Razor's HTML.</summary>
    Html,

    /// <summary>VS Code's CSS server: CSS, SCSS and Less.</summary>
    Css,

    /// <summary>VS Code's JSON server, with SchemaStore's schemas for the well-known files (<see cref="JsonSchemas"/>).</summary>
    Json,

    /// <summary>dockerfile-language-server-nodejs: Dockerfiles and Containerfiles, by their names (<see cref="Language.Dockerfile"/>).</summary>
    Dockerfile,

    /// <summary>Red Hat's yaml-language-server: every YAML file, a compose file checked and completed by the Compose
    /// specification's schema (<see cref="JsonSchemas.ComposeUri"/>).</summary>
    Yaml,
}

/// <summary>JavaScript, TypeScript, HTML, CSS, JSON, Dockerfiles and YAML for the open folder: each kind's language
/// server (the npm packages carried under <c>web/</c>, run on the machine's Node.js through ChildProcesses) started as
/// the first file that needs it opens, every open file of its languages kept in step with it (a
/// <see cref="WebDocument"/> each), and ended with the folder. Without a Node.js to run them (none found, or one too
/// old), each file says why in its banner and the status bar, keeping its colours. A server that ends or stops
/// answering is started again with its files, a few times at most. The project's own <c>node_modules/typescript</c> is
/// the TypeScript server's when it has one. A UI-thread object: what a server says from its threads crosses through
/// <see cref="UiThread"/>.</summary>
public sealed class WebLanguages : IAsyncDisposable
{
    private readonly string folder;
    private readonly Func<AppSettings> settings;
    private readonly Dictionary<WebServerKind, NodeServer> servers = [];
    private readonly Dictionary<OpenDocument, WebDocument> documents = [];

    /// <summary>The folders npm install runs in now, whose files' banners say so.</summary>
    private readonly HashSet<string> installing = new(PathNames.Comparer);
    private readonly CancellationTokenSource ending = new();
    private string? configured;
    private Task<NodeRuntime>? finding;

    /// <summary>The ESLint settings the ESLint server runs by, which a change of them starts it again for.</summary>
    private (ToolConfiguration, string?, string) linting;

    /// <summary>Whether the Dockerfile server asks Docker Hub for an image's tags, which a change starts it again for.</summary>
    private bool asksDockerHub;
    private bool disposed;

    /// <summary>A folder's servers, run on the Node.js Settings names (null for the PATH's), with ESLint and Prettier as
    /// <paramref name="settings"/> configure them; remote JSON schemas are downloaded, and Docker Hub asked for image
    /// tags, while Settings allows it. The container <paramref name="engine"/> (the person's when none is given) checks
    /// the Dockerfiles and compose files.</summary>
    public WebLanguages(string folder, Func<AppSettings> settings, Func<Containers.ContainerEngine>? engine = null)
    {
        this.folder = folder;
        this.settings = settings;
        AppSettings now = settings();
        configured = now.NodeInterpreter;
        linting = Linting(now);
        asksDockerHub = now.DockerHubTags;
        Schemas = new JsonSchemas(() => settings().DownloadJsonSchemas);
        Eslint = new EslintService(folder, settings, FindNodeAsync);
        Prettier = new PrettierService(folder, settings, FindNodeAsync);
        Checks = new Containers.EngineChecks(engine ?? (() => Containers.ContainerEngine.Shared), now.BuildKitChecks);
        Eslint.Message += (_, message) => Message?.Invoke(this, message);
    }

    /// <summary>A server's state changed (starting, ready, stopped, no Node.js); raised on the UI thread.</summary>
    public event EventHandler? StateChanged;

    /// <summary>A server ended or stopped answering, with what was done about it, for the status bar.</summary>
    public event EventHandler<string>? Stopped;

    /// <summary>Something else for the status bar: ESLint's word on a file, npm install's end. Raised on the UI thread.</summary>
    public event EventHandler<string>? Message;

    public JsonSchemas Schemas { get; }

    /// <summary>Where the Dockerfile server asks for image tags in Docker Hub's place: a stand-in; null for the place
    /// <c>PASSENGER_DOCKER_HUB</c> names, else Docker Hub's own (<see cref="DockerHubTags"/>).</summary>
    public Uri? DockerHub { get; init; }

    /// <summary>ESLint, for the files it checks.</summary>
    public EslintService Eslint { get; }

    /// <summary>Prettier, for the files it formats.</summary>
    public PrettierService Prettier { get; }

    /// <summary>The container engine's checks of the Dockerfiles and compose files, beside their servers' own.</summary>
    public Containers.EngineChecks Checks { get; }

    /// <summary>The folder the servers work on.</summary>
    public string Folder => folder;

    /// <summary>Where the servers keep their logs, beside the IDE's own.</summary>
    public static string LogFolder => Path.Combine(IdeErrors.LogFolder, "web");

    /// <summary>The servers' packages, carried beside the app.</summary>
    public static string Modules => Path.Combine(Carried, "node_modules");

    /// <summary>Where the servers, their schemas and Passenger's scripts for them are carried: beside the app, or in a
    /// macOS bundle its Resources, since codesign takes a folder under Contents/MacOS named with a dot (a package's
    /// .github, yaml's dist/schema/yaml-1.1) for a nested bundle and refuses to sign the app.</summary>
    public static string Carried { get; } = CarriedFolder();

    private static string CarriedFolder()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "web");
        string resources = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "web"));
        return !Directory.Exists(beside) && Directory.Exists(resources) ? resources : beside;
    }

    /// <summary>The Node.js found for the servers, once one has been looked for.</summary>
    public NodeRuntime? Node => finding is { IsCompletedSuccessfully: true } found ? found.Result : null;

    /// <summary>The server and language a file is answered in, by its language; null for a file none of them reads.</summary>
    public static (WebServerKind Kind, string LanguageId)? LanguageOf(string path)
    {
        Language language = Language.ForPath(path);
        WebServerKind? kind = language.Service switch
        {
            LanguageService.TypeScript => WebServerKind.TypeScript,
            LanguageService.Html => WebServerKind.Html,
            LanguageService.Css => WebServerKind.Css,
            LanguageService.Json => WebServerKind.Json,
            LanguageService.Dockerfile => WebServerKind.Dockerfile,
            LanguageService.Yaml => WebServerKind.Yaml,
            _ => null,
        };
        return kind is { } server ? (server, language.IdOf(path)) : null;
    }

    /// <summary>A server's name as the status bar and banner say it.</summary>
    public static string Name(WebServerKind kind) => kind switch
    {
        WebServerKind.TypeScript => "TypeScript",
        WebServerKind.Html => "HTML",
        WebServerKind.Css => "CSS",
        WebServerKind.Dockerfile => "Dockerfile",
        WebServerKind.Yaml => "YAML",
        _ => "JSON",
    };

    /// <summary>Gives an open file its server's features, now or once the server has started (starting it if it has not).</summary>
    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        if (disposed || documents.ContainsKey(document) || LanguageOf(document.Path) is not { } language)
        {
            return;
        }
        WebDocument features = new(document, language.Kind, language.LanguageId);
        documents[document] = features;
        NodeServer server = ServerFor(language.Kind);
        if (server.Session is { } running)
        {
            features.Connect(running);
        }
        else if (server.Problem is not null)
        {
            // A server that cannot start holds none of its files' checks, this one's no more than those before it.
            features.Unreached();
        }
        else if (server.Starting is null)
        {
            Start(server);
        }
        // Else the start under way connects it with the rest of its kind.
        Eslint.Attach(document, features);
        Checks.Attach(document, features);
        Announce(document);
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.Remove(document, out WebDocument? features))
        {
            Eslint.Detach(document);
            Checks.Detach(document);
            features.Dispose();
            document.Notice = null;
            document.NoticeAction = null;
        }
    }

    /// <summary>The Node.js the servers run on, looked for once. Asked from any thread.</summary>
    private Task<NodeRuntime> FindNodeAsync() => UiThread.InvokeAsync(NodeAsync);

    /// <summary><see cref="FindNodeAsync"/> on the UI thread.</summary>
    private Task<NodeRuntime> NodeAsync()
    {
        string? asked = configured;
        return finding ??= BackgroundWork.Run(() => NodeRuntime.FindAsync(asked));
    }

    private static (ToolConfiguration, string?, string) Linting(AppSettings settings) => (settings.Eslint, settings.EslintPackage, settings.EslintFiles);

    /// <summary>What a file's banner says: why its language server cannot run (no Node.js); else that a package its
    /// package.json lists for ESLint or Prettier is not installed, offering to install it; else nothing.</summary>
    private void Announce(OpenDocument document)
    {
        if (!documents.TryGetValue(document, out WebDocument? features))
        {
            return;
        }
        if (servers.GetValueOrDefault(features.Kind) is { Problem: { } problem })
        {
            document.Notice = Notice(Language.ForPath(document.Path), problem);
            document.NoticeAction = null;
            return;
        }
        List<string> tools = [];
        string? manifest = null;
        if (Eslint.Missing(document.Path) is { } linted)
        {
            tools.Add("ESLint");
            manifest = linted;
        }
        if (Prettier.Missing(document.Path) is { } formatted)
        {
            tools.Add("Prettier");
            manifest ??= formatted;
        }
        if (manifest is null)
        {
            document.Notice = null;
            document.NoticeAction = null;
            return;
        }
        string where = Path.GetDirectoryName(manifest)!;
        if (installing.Contains(where))
        {
            document.Notice = $"Running npm install in {Shown(where)}…";
            document.NoticeAction = null;
            return;
        }
        string packages = string.Join(" and ", tools.Select(t => t.ToLowerInvariant()));
        document.Notice = tools.Count == 1
            ? $"{tools[0]} is not installed: {Shown(manifest)} lists {packages}, which is not in node_modules."
            : $"{string.Join(" and ", tools)} are not installed: {Shown(manifest)} lists {packages}, which are not in node_modules.";
        document.NoticeAction = new NoticeAction("Run 'npm install'", () => IdeErrors.Observe(InstallAsync(where)));
    }

    private void AnnounceAll()
    {
        foreach (OpenDocument document in documents.Keys.ToList())
        {
            Announce(document);
        }
    }

    /// <summary>A path as the banner names it: from the folder open when it is inside it.</summary>
    private string Shown(string path) => PathNames.Shown(path, folder);

    /// <summary>The packages looked for again, a package.json or its lock changed (npm install run in the terminal):
    /// ESLint checks the files it covers now, and each banner says what is still missing.</summary>
    public void Recheck()
    {
        UiThread.Check();
        if (disposed)
        {
            return;
        }
        foreach ((OpenDocument document, WebDocument features) in documents.ToList())
        {
            Eslint.Attach(document, features);
        }
        AnnounceAll();
    }

    /// <summary>Files changed on disk: a package.json or lock file looks for the packages again, and ESLint hears of its
    /// configuration's changes.</summary>
    public void FilesChanged(IReadOnlyCollection<string> paths)
    {
        UiThread.Check();
        if (paths.Any(p => Path.GetFileName(p) is "package.json" or "package-lock.json" or "npm-shrinkwrap.json" or "yarn.lock" or "pnpm-lock.yaml"))
        {
            Recheck();
        }
        Eslint.FilesChanged(paths);
        Checks.FilesChanged(paths);
    }

    /// <summary>Settings changed: ESLint configured otherwise starts again on the files it covers then, and every banner
    /// says what is missing by the new settings.</summary>
    public void UseTools()
    {
        UiThread.Check();
        if (disposed)
        {
            return;
        }
        (ToolConfiguration, string?, string) now = Linting(settings());
        if (now != linting)
        {
            linting = now;
            Eslint.Reset();
        }
        Recheck();
    }

    /// <summary>"Run 'npm install'": npm install in the folder of the package.json that lists what is missing, on the
    /// Node.js the servers run on, through ChildProcesses; the banners say so meanwhile, and once it is done ESLint
    /// checks the files it covers and the banners go.</summary>
    public async Task InstallAsync(string where)
    {
        UiThread.Check();
        if (disposed || !installing.Add(where))
        {
            return;
        }
        AnnounceAll();
        Message?.Invoke(this, $"Running npm install in {Shown(where)}…");
        try
        {
            NodeRuntime node = await FindNodeAsync();
            if (node.Problem is { } none)
            {
                Message?.Invoke(this, $"npm install needs Node.js: {none}");
                return;
            }
            if (Npm.Cli(node.Path!) is not { } cli)
            {
                Message?.Invoke(this, $"npm install cannot run: there is no npm beside {node.Path} or on the PATH");
                return;
            }
            ChildResult result = await BackgroundWork.Run(() => ChildProcesses.RunAsync(Npm.Install(node.Path!, cli, where), cancel: ending.Token), ending.Token);
            if (result.Succeeded)
            {
                Message?.Invoke(this, $"npm install finished in {Shown(where)}");
            }
            else
            {
                IdeErrors.Write($"npm install in {where} failed with exit code {result.ExitCode}:\n{result.Error}{result.Output}");
                string said = (result.Error + "\n" + result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? "the IDE's log has what it said";
                Message?.Invoke(this, $"npm install failed in {Shown(where)} (exit code {result.ExitCode}): {said}");
            }
        }
        catch (OperationCanceledException)
        {
            // The folder was closed.
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Message?.Invoke(this, $"npm install could not run in {Shown(where)}: {error.Message}");
        }
        finally
        {
            installing.Remove(where);
            Recheck();
        }
    }

    public WebDocument? For(OpenDocument document) => documents.GetValueOrDefault(document);

    /// <summary>What the status bar says of a file's server while it cannot answer (starting, no Node.js); null once it can.</summary>
    public string? StateOf(OpenDocument document) =>
        LanguageOf(document.Path) is { } language && servers.GetValueOrDefault(language.Kind) is { Session: null } server ? server.State : null;

    /// <summary>What the status bar says of a file's running server worth saying: for a script, the TypeScript it runs,
    /// the project's own or, marked so, the one Passenger carries; null otherwise.</summary>
    public string? DetailOf(OpenDocument document) =>
        LanguageOf(document.Path) is { Kind: WebServerKind.TypeScript } && servers.GetValueOrDefault(WebServerKind.TypeScript)?.Session?.Server.TypeScript is { } typescript
            ? $"TypeScript {typescript.Version}{(typescript.Source == "bundled" ? " (Passenger's)" : string.Empty)}"
            : null;

    /// <summary>Whether a file's server cannot start (no Node.js, one too old, a server missing), so none will answer for it.</summary>
    public bool CannotStart(OpenDocument document) =>
        LanguageOf(document.Path) is { } language && servers.GetValueOrDefault(language.Kind) is { Problem: not null };

    /// <summary>What the status bar says when a command (<paramref name="what"/>) asks a file's server before it can answer.</summary>
    public string Unready(string what, OpenDocument document) =>
        LanguageOf(document.Path) is { } language
            ? $"{what} needs the {Name(language.Kind)} language server: {servers.GetValueOrDefault(language.Kind) switch { { Problem: { } problem } => problem, { State.Length: > 0 } server => $"it is {server.State}", _ => "it has not started" }}"
            : $"{what} needs a language server, and none reads {document.Name}";

    /// <summary>The HTML server, started if it has not been (Razor's HTML asks it), once it can answer; null without a
    /// Node.js to run it, or once it has stopped. Called on any thread.</summary>
    public Task<LanguageServer?> HtmlAsync(CancellationToken cancel) => UiThread.InvokeAsync(async () =>
    {
        if (disposed)
        {
            return null;
        }
        NodeServer server = ServerFor(WebServerKind.Html);
        if (server.Session is null && server.Problem is null && server.Starting is null)
        {
            Start(server);
        }
        LanguageSession? started = server.Starting is { } starting ? await starting : server.Session;
        return started?.Server is { HasExited: false } running ? running : null;
    });

    /// <summary>Runs the servers on another Node.js (Settings changed it, null for the PATH's): those running end, and
    /// start again on it with their files, as does any that had none to run on.</summary>
    public void UseNode(string? node)
    {
        UiThread.Check();
        if (disposed || node == configured)
        {
            return;
        }
        configured = node;
        finding = null;
        Eslint.Reset();
        IdeErrors.Observe(Prettier.ResetAsync());
        foreach ((WebServerKind kind, NodeServer server) in servers.ToList())
        {
            Replace(kind, server);
        }
    }

    /// <summary>Settings' Complete image tags from Docker Hub changed: the Dockerfile server, running or starting, starts
    /// again with its files, told whether to ask Docker Hub, which it learns only as it starts.</summary>
    public void UseDockerHub(bool asks)
    {
        UiThread.Check();
        if (disposed || asks == asksDockerHub)
        {
            return;
        }
        asksDockerHub = asks;
        if (servers.TryGetValue(WebServerKind.Dockerfile, out NodeServer? server) && server.Problem is null)
        {
            Replace(WebServerKind.Dockerfile, server);
        }
    }

    /// <summary>A kind's server, made as a file first needs it, counting its restarts in <paramref name="restarts"/>
    /// (those of the servers before it in its place).</summary>
    private NodeServer ServerFor(WebServerKind kind, ServerRestarts? restarts = null)
    {
        if (servers.TryGetValue(kind, out NodeServer? server))
        {
            return server;
        }
        NodeServer made = null!;
        made = new NodeServer(Name(kind), folder, NodeAsync, restarts ?? new ServerRestarts())
        {
            Package = Path.Combine(Modules, Package(kind), "package.json"),
            Missing = () => Missing(kind),
            Command = node => Starting(kind, node),
            Configure = language => Configure(kind, language),
            Listen = started => started.Server.TypeScriptVersion += (_, _) => UiThread.Post(() => StateChanged?.Invoke(this, EventArgs.Empty)),
            Started = started => Started(kind, started),
            Unavailable = () => Unavailable(kind),
            Refresh = () =>
            {
                foreach (WebDocument features in documents.Values.Where(d => d.Kind == kind))
                {
                    features.Refresh();
                }
            },
            Lost = what => Lost(kind, made, what),
        };
        servers[kind] = made;
        return made;
    }

    private void Start(NodeServer server)
    {
        server.Start();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A kind's server started: its files connected to it, their banners said again.</summary>
    private void Started(WebServerKind kind, LanguageSession started)
    {
        foreach (WebDocument features in documents.Values.Where(d => d.Kind == kind))
        {
            features.Connect(started);
        }
        foreach (OpenDocument open in documents.Keys.Where(d => documents[d].Kind == kind).ToList())
        {
            Announce(open);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Why a server cannot start that this build is to blame for: its package, or the Dockerfile server's preload,
    /// is not beside the app.</summary>
    private static string? Missing(WebServerKind kind) =>
        !File.Exists(Script(kind)) ? $"its language server is missing from the app's folder ({Script(kind)})"
        : kind == WebServerKind.Dockerfile && !File.Exists(DockerHubTags.Preload) ? $"Passenger's part of its language server is missing from the app's folder ({DockerHubTags.Preload})"
        : null;

    /// <summary>A server that cannot run: its files keep their colours and say why in their banner, the status bar in a
    /// few words.</summary>
    private void Unavailable(WebServerKind kind)
    {
        foreach (OpenDocument open in documents.Where(d => d.Value.Kind == kind).Select(d => d.Key).ToList())
        {
            documents[open].Unreached();
            Announce(open);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The banner over a file whose server cannot run, in the file's own language.</summary>
    private static string Notice(Language language, string problem) =>
        $"{(language == Language.Dockerfile ? "A Dockerfile" : language.Name)} has its colours only: {problem}.";

    /// <summary>The npm package a server is, whose package.json says which Node.js it needs.</summary>
    private static string Package(WebServerKind kind) => kind switch
    {
        WebServerKind.TypeScript => "typescript-language-server",
        WebServerKind.Dockerfile => "dockerfile-language-server-nodejs",
        WebServerKind.Yaml => "yaml-language-server",
        _ => "vscode-langservers-extracted",
    };

    /// <summary>The script of a server's package that runs it.</summary>
    private static string Script(WebServerKind kind) => kind switch
    {
        WebServerKind.TypeScript => Path.Combine(Modules, "typescript-language-server", "lib", "cli.mjs"),
        WebServerKind.Html => Path.Combine(Modules, "vscode-langservers-extracted", "lib", "html-language-server", "node", "htmlServerMain.js"),
        WebServerKind.Css => Path.Combine(Modules, "vscode-langservers-extracted", "lib", "css-language-server", "node", "cssServerMain.js"),
        WebServerKind.Dockerfile => Path.Combine(Modules, "dockerfile-language-server-nodejs", "lib", "server.js"),
        // Its module rather than its bin script, which has no extension for Node.js to read as a module by.
        WebServerKind.Yaml => Path.Combine(Modules, "yaml-language-server", "out", "src", "server.js"),
        _ => Path.Combine(Modules, "vscode-langservers-extracted", "lib", "json-language-server", "node", "jsonServerMain.js"),
    };

    /// <summary>How a server starts: its script on Node.js in the folder, with the options its package documents. The
    /// TypeScript server never installs type packages from npm (automatic type acquisition) behind the person's back, nor
    /// the Dockerfile server asks Docker Hub for image tags unless Settings allows it (its preload, <see cref="DockerHubTags"/>).</summary>
    private ServerStart Starting(WebServerKind kind, string node)
    {
        ProcessStartInfo process = ChildProcesses.Command(node, kind == WebServerKind.Dockerfile
            ? ["--require", DockerHubTags.Preload, Script(kind), "--stdio"]
            : [Script(kind), "--stdio"], folder);
        if (kind == WebServerKind.Dockerfile && DockerHubTags.Told(asksDockerHub, DockerHub) is { } told)
        {
            process.Environment[DockerHubTags.Variable] = told;
        }
        object? options = kind switch
        {
            WebServerKind.TypeScript => new
            {
                hostInfo = "Passenger",
                disableAutomaticTypingAcquisition = true,
                tsserver = new { logVerbosity = "off" },
                preferences = new { includeCompletionsForModuleExports = true, includeCompletionsForImportStatements = true },
            },
            WebServerKind.Html => new { provideFormatter = true, embeddedLanguages = new { css = true, javascript = true } },
            WebServerKind.Css => new { provideFormatter = true },
            // Both read their settings from workspace/configuration alone.
            WebServerKind.Dockerfile or WebServerKind.Yaml => null,
            _ => new { provideFormatter = true, handledSchemaProtocols = new[] { "file" } },
        };
        return new ServerStart(process, folder, LogFolder, ClientCapabilities.Web, options)
        {
            Configuration = section => Section(kind, section),
            Content = kind is WebServerKind.Json or WebServerKind.Yaml ? Schemas.ContentAsync : null,
            // How each server's pushes follow a change, since none names the version a push answers.
            Pushes = kind switch
            {
                WebServerKind.Dockerfile => new PushPace(EachText: true, TimeSpan.Zero),
                WebServerKind.Yaml => new PushPace(EachText: false, TimeSpan.FromMilliseconds(200)),
                WebServerKind.TypeScript => new PushPace(EachText: false, TimeSpan.FromMilliseconds(200), QuietWhenClean: true),
                _ => PushPace.Latest,
            },
        };
    }

    /// <summary>What a server's <c>workspace/configuration</c> is answered: the CSS server's validation on for each of its
    /// languages, the HTML server's for its styles and scripts, the YAML server's settings (<see cref="YamlSettings"/>),
    /// the rest left to its defaults (the Dockerfile server's checks and layout among them).</summary>
    private static object? Section(WebServerKind kind, string? section) => (kind, section) switch
    {
        (WebServerKind.Css, "css" or "scss" or "less") => new { validate = true },
        (WebServerKind.Html, "html") => new { validate = new { scripts = true, styles = true }, format = new { enable = true } },
        (WebServerKind.Html, "css") => new { validate = true },
        (WebServerKind.Yaml, "yaml") => YamlSettings,
        _ => null,
    };

    /// <summary>The YAML server's settings: checks, completion, hover and formatting on; the Compose specification's schema,
    /// carried and read from disk, for compose files; Compose's merge tags known; and nothing fetched by the server itself,
    /// neither SchemaStore's catalogue nor Kubernetes' schemas (a schema a file names over HTTP is asked of
    /// <see cref="JsonSchemas"/>, as the JSON server's are, once <c>yaml/registerContentRequest</c> has told it to).</summary>
    private static readonly object YamlSettings = new
    {
        validate = true,
        completion = true,
        hover = true,
        format = new { enable = true },
        schemaStore = new { enable = false },
        kubernetesCRDStore = new { enable = false },
        customTags = new[] { "!reset scalar", "!reset sequence", "!reset mapping", "!override sequence", "!override mapping" },
        schemas = new Dictionary<string, string[]> { [JsonSchemas.ComposeUri] = JsonSchemas.ComposeFiles },
    };

    /// <summary>The settings a server reads from <c>workspace/didChangeConfiguration</c> rather than asks for, sent once it
    /// has started: the JSON server's validation and formatting, and which schema each well-known file takes; and the YAML
    /// server's word that Passenger answers for schemas over HTTP.</summary>
    private static async Task Configure(WebServerKind kind, LanguageServer server)
    {
        switch (kind)
        {
            case WebServerKind.Json:
                await server.NotifyAsync(LspMethods.DidChangeConfiguration, new
                {
                    settings = new { json = new { validate = new { enable = true }, format = new { enable = true }, schemas = Array.Empty<object>() }, http = new { } },
                });
                // Its one parameter, the list, as the protocol's positional parameters.
                await server.NotifyAsync("json/schemaAssociations", new[] { JsonSchemas.Notification });
                break;
            case WebServerKind.Html:
                await server.NotifyAsync(LspMethods.DidChangeConfiguration, new
                {
                    settings = new { html = new { format = new { enable = true }, validate = new { scripts = true, styles = true } }, css = new { validate = true } },
                });
                break;
            case WebServerKind.Css:
                await server.NotifyAsync(LspMethods.DidChangeConfiguration, new
                {
                    settings = new { css = new { validate = true }, scss = new { validate = true }, less = new { validate = true } },
                });
                break;
            case WebServerKind.Yaml:
                // Has the server ask Passenger for a schema named over HTTP rather than download it.
                await server.NotifyAsync("yaml/registerContentRequest", new { });
                break;
            case WebServerKind.Dockerfile:
                await server.NotifyAsync(LspMethods.DidChangeConfiguration, new { settings = new { } });
                break;
            default:
                await server.NotifyAsync(LspMethods.DidChangeConfiguration, new { settings = new { } });
                break;
        }
    }

    /// <summary>A server gone: started again with its files, unless servers of its kind have gone too often to be worth it.</summary>
    private void Lost(WebServerKind kind, NodeServer server, string what)
    {
        bool again = server.Restarts.Again();
        Replace(kind, server, stopped: again ? null : $"its language server stopped {ServerRestarts.Often}");
        Stopped?.Invoke(this, ServerRestarts.Said(what, again));
    }

    /// <summary>Ends a server (or its start) and lets its files go, then gives them a new one: started, or left stopped for
    /// the reason given, which their banners say.</summary>
    private void Replace(WebServerKind kind, NodeServer server, string? stopped = null)
    {
        IdeErrors.Observe(server.End());
        List<OpenDocument> open = [.. documents.Where(d => d.Value.Kind == kind).Select(d => d.Key)];
        foreach (OpenDocument document in open)
        {
            Detach(document);
        }
        servers.Remove(kind);
        NodeServer again = ServerFor(kind, server.Restarts);
        if (stopped is not null)
        {
            again.Stop(stopped);
        }
        foreach (OpenDocument document in open)
        {
            Attach(document);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lets the files go and ends every server, on a worker, so the process can wait for them once the window's
    /// loop has stopped.</summary>
    public ValueTask DisposeAsync()
    {
        UiThread.Check();
        disposed = true;
        ending.Cancel();
        Task linter = Eslint.DisposeAsync().AsTask();
        Task formatter = Prettier.DisposeAsync().AsTask();
        Checks.Dispose();
        foreach (WebDocument features in documents.Values)
        {
            features.Dispose();
        }
        foreach (OpenDocument document in documents.Keys)
        {
            document.Notice = null;
            document.NoticeAction = null;
        }
        documents.Clear();
        List<Task> closing = [linter, formatter, .. servers.Values.Select(s => s.End())];
        servers.Clear();
        // Done at once with nothing running, so the folder opened next opens as the call that left this one returns.
        return closing.All(t => t.IsCompleted) ? ValueTask.CompletedTask : new ValueTask(Task.WhenAll(closing));
    }
}
