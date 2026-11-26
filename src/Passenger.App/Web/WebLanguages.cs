using System.Diagnostics;
using Passenger.App.Settings;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Languages;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.App.Web;

/// <summary>The web, GDScript and Protobuf language servers.</summary>
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

    /// <summary>Godot's own GDScript server, in its editor, reached over TCP (<see cref="Godot.GodotScripts"/>).</summary>
    GDScript,

    /// <summary>Buf's language server for Protocol Buffers schemas.</summary>
    Protobuf,
}

/// <summary>A web language server lost, as the status bar says it, with what starts it again when it was left stopped for
/// ending too often; null when it was started again at once.</summary>
public sealed record WebServerLost(string Said, Action? StartAgain);

/// <summary>JavaScript, TypeScript, HTML, CSS, JSON, Dockerfiles, YAML, GDScript and Protobuf for the open folder: each kind's
/// language server (the npm packages carried under <c>web/</c>, run through ChildProcesses on the runtime its lockfile
/// says, Node.js, Bun or Deno, or the one Settings chooses; GDScript's in Godot's editor, one for each Godot project a
/// script is open in, the person's joined, a headless one started only when asked, <see cref="AskGodot"/>, never in a
/// folder not trusted, and Buf's on the CLI from the PATH in trusted folders) started as the first file that needs it opens, every open file of its languages kept in step with
/// it (a <see cref="WebDocument"/> each), and ended with the folder. Without a runtime to run them, each file says why in
/// its banner and the status bar, keeping its colours. A server that ends or stops answering is started again with its
/// files, a few times at most, then left stopped until a person starts it again. The project's own TypeScript serves
/// where the folder is trusted (<see cref="WorkspaceTrust"/>), a Yarn Plug'n'Play project's with its .pnp.cjs
/// (<see cref="YarnPnp"/>), else the carried one. A UI-thread object: a server's words cross through <see cref="UiThread"/>.</summary>
public sealed class WebLanguages : IAsyncDisposable
{
    private readonly string folder;
    private readonly Func<AppSettings> settings;
    private readonly Dictionary<ServerPlace, NodeServer> servers = [];
    private readonly Dictionary<OpenDocument, WebDocument> documents = [];

    /// <summary>What a server answers for: every file of its kind in the folder, but GDScript's, which lives in Godot's editor on
    /// one project and so answers for that project's scripts alone (<see cref="Project"/>, null for a script in none).</summary>
    private readonly record struct ServerPlace(WebServerKind Kind, string? Project)
    {
        public bool Equals(ServerPlace other) => Kind == other.Kind && PathNames.Comparer.Equals(Project, other.Project);

        public override int GetHashCode() => HashCode.Combine(Kind, Project is null ? 0 : PathNames.Comparer.GetHashCode(Project));
    }

    /// <summary>The folders a package manager's install runs in now, whose files' banners say so.</summary>
    private readonly HashSet<string> installing = new(PathNames.Comparer);
    private readonly CancellationTokenSource ending = new();
    private string? configured;
    private RuntimeChoice runtime;
    private Task<NodeRuntime>? finding;

    /// <summary>The ESLint settings the ESLint server runs by, which a change of them starts it again for.</summary>
    private (ToolConfiguration, string?, string) linting;

    /// <summary>Whether the Dockerfile server asks Docker Hub for an image's tags, which a change starts it again for.</summary>
    private bool asksDockerHub;
    private bool disposed;

    /// <summary>Whether the folder is trusted as the servers know it, which its trust given or taken back later starts TypeScript's again for.</summary>
    private bool trusted;

    /// <summary>Whether TypeScript's server last started on the project's own TypeScript (<see cref="OwnTsserver"/>).</summary>
    private bool ownTypeScript;

    /// <summary>The variable that shortens how long GDScript's server waits, no GDScript open, before it is let go.</summary>
    [IdentifierHome]
    public const string GDScriptIdleVariable = "PASSENGER_GDSCRIPT_IDLE_MS";

    /// <summary>How long GDScript's server, a headless Godot editor of about 800 MB, is kept once no GDScript is open: the
    /// next asking starts it again in a few seconds, Passenger's own reading of the scripts answering meanwhile.</summary>
    private static TimeSpan GDScriptIdle => EnvironmentValues.Milliseconds(GDScriptIdleVariable, TimeSpan.FromMinutes(3));

    /// <summary>The waits for GDScript's servers to be let go, each project's while one is under way.</summary>
    private readonly Dictionary<ServerPlace, CancellationTokenSource> gdscriptIdle = [];

    /// <summary>The next server's configuring held by a test (<see cref="HoldNextConfigure"/>): what it waits for, and its
    /// process's id told once it waits.</summary>
    private (Task Until, TaskCompletionSource<int> Holding)? configureHeld;

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
        runtime = now.WebRuntime;
        linting = Linting(now);
        asksDockerHub = now.DockerHubTags;
        trusted = WorkspaceTrust.Trusts(folder);
        Schemas = new JsonSchemas(() => settings().DownloadJsonSchemas);
        Tools = new FolderTools(folder, settings, FindNodeAsync);
        Prettier = new PrettierService(folder, settings, FindNodeAsync);
        Checks = new Containers.EngineChecks(engine ?? (() => Containers.ContainerEngine.Shared), now.BuildKitChecks);
        Tools.Message += (_, message) => Message?.Invoke(this, message);
        Tools.Stopped += (_, lost) => Stopped?.Invoke(this, lost);
    }

    /// <summary>A server's state changed (starting, ready, stopped, no Node.js); raised on the UI thread.</summary>
    public event EventHandler? StateChanged;

    /// <summary>A server (ESLint's too) ended or stopped answering, with what was done about it, for the status bar; raised
    /// on the UI thread.</summary>
    public event EventHandler<WebServerLost>? Stopped;

    /// <summary>Something else for the status bar: ESLint's word on a file, an install's end. Raised on the UI thread.</summary>
    public event EventHandler<string>? Message;

    public JsonSchemas Schemas { get; }

    /// <summary>Where the Dockerfile server asks for image tags in Docker Hub's place: a stand-in; null for the place
    /// <c>PASSENGER_DOCKER_HUB</c> names, else Docker Hub's own (<see cref="DockerHubTags"/>).</summary>
    public Uri? DockerHub { get; init; }

    /// <summary>The stylesheets the folder's pages link, whose classes an HTML file's are checked against.</summary>
    public PageStylesheets? Stylesheets { get; init; }

    /// <summary>The variables a Godot editor started for GDScript runs with beyond the IDE's own: a test's user folders.</summary>
    public IReadOnlyDictionary<string, string> GodotEnvironment { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether opening a GDScript may start a headless Godot editor to serve it, as a test of Godot's own server
    /// asks; else one starts only once something Passenger's own reading has no answer for asks (<see cref="AskGodot"/>),
    /// the person's own editor joined meanwhile where one is open on the project.</summary>
    public bool GodotHeadlessAtOpen { get; init; }

    /// <summary>The GDScript servers asked for since they were last let go, whose starts may be headless editors.</summary>
    private readonly HashSet<ServerPlace> godotAsked = [];

    /// <summary>The folder's own linters and formatters (ESLint, Oxlint, Oxfmt, Biome), for the files each serves.</summary>
    public FolderTools Tools { get; }

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

    /// <summary>The carried TypeScript's server, which a folder not trusted is served by.</summary>
    public static string CarriedTsserver => Path.Combine(Modules, "typescript", "lib", "tsserver.js");

    /// <summary>The folders under a folder a project's own TypeScript is looked for in, in the order its server looks.</summary>
    private static readonly string[] TypeScriptFolders =
    [
        Path.Combine("node_modules", "typescript", "lib"),
        Path.Combine(".vscode", "pnpify", "typescript", "lib"),
        Path.Combine(".yarn", "sdks", "typescript", "lib"),
        Path.Combine(".pnpm", "sdks", "typescript", "lib"),
    ];

    /// <summary>The project's own tsserver.js, looked for from the folder up as TypeScript's server would, but in the person's
    /// folders alone (<see cref="FoldersUp"/>), and named to it so it looks no further: the nearest folder's TypeScript, where
    /// it has a tsserver.js and says its version, else the typescript package a Yarn Plug'n'Play project places for the
    /// folder (in its archive, which the server reads through its .pnp.cjs); null where there is none, which the carried one
    /// serves.</summary>
    internal static string? OwnTsserver(string folder)
    {
        foreach (string at in FoldersUp.From(folder))
        {
            if (TypeScriptFolders.FirstOrDefault(f => Directory.Exists(Path.Join(at, f))) is { } lib)
            {
                return FoldersUp.Found(at, Path.Combine(lib, "tsserver.js")) is { } server && File.Exists(server)
                    && FoldersUp.Found(at, Path.Combine(Path.GetDirectoryName(lib)!, "package.json")) is { } package
                    && ProjectPackages.Version(Path.GetDirectoryName(package)!) is not null
                    ? server
                    : null;
            }
        }
        return YarnPnp.Resolve(Path.Combine(folder, NodeProjects.ManifestName), "typescript") is { } placed
            && ProjectPackages.Version(placed) is not null
            && YarnPnp.Exists(Path.Combine(placed, "lib", "tsserver.js"))
            ? Path.Combine(placed, "lib", "tsserver.js")
            : null;
    }

    /// <summary>Where the servers, their schemas and Passenger's scripts for them are carried.</summary>
    public static string Carried { get; } = CarriedFiles.Folder("web");

    /// <summary>The runtime found for the servers, once one has been looked for.</summary>
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
            LanguageService.GDScript => WebServerKind.GDScript,
            LanguageService.Protobuf => WebServerKind.Protobuf,
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
        WebServerKind.GDScript => "GDScript",
        WebServerKind.Protobuf => "Buf",
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
        ServerPlace place = new(language.Kind, language.Kind == WebServerKind.GDScript ? Godot.GodotScripts.ProjectOf(document.Path) : null);
        WebDocument features = new(document, language.Kind, language.LanguageId)
        {
            Stylesheets = Stylesheets, GodotProject = place.Project, AskServer = language.Kind == WebServerKind.GDScript ? () => AskGodot(document) : null,
        };
        documents[document] = features;
        if (gdscriptIdle.Remove(place, out CancellationTokenSource? waiting))
        {
            waiting.Cancel();
        }
        NodeServer server = ServerFor(place);
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
        Tools.Attach(document, features);
        Checks.Attach(document, features);
        Announce(document);
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.Remove(document, out WebDocument? features))
        {
            Tools.Detach(document);
            Checks.Detach(document);
            features.Dispose();
            document.Notice = null;
            document.NoticeAction = null;
            if (features.Kind == WebServerKind.GDScript && !Of(PlaceOf(features)).Any())
            {
                LetGoGDScriptLater(PlaceOf(features));
            }
        }
    }

    /// <summary>What answers for a file open: its server's place.</summary>
    private static ServerPlace PlaceOf(WebDocument features) => new(features.Kind, features.GodotProject);

    /// <summary>What a server answers for of the files open.</summary>
    private IEnumerable<WebDocument> Of(ServerPlace place) => documents.Values.Where(d => PlaceOf(d) == place);

    /// <summary>The files open a server answers for, listed as they are now.</summary>
    private List<OpenDocument> Open(ServerPlace place) => [.. documents.Where(d => PlaceOf(d.Value) == place).Select(d => d.Key)];

    /// <summary>A file's server, once it has one.</summary>
    private NodeServer? ServerOf(OpenDocument document) =>
        documents.TryGetValue(document, out WebDocument? features) ? servers.GetValueOrDefault(PlaceOf(features)) : null;

    /// <summary>A project's GDScript server let go once <see cref="GDScriptIdle"/> passes with no script of it open, its next
    /// file starting it again; a server of the person's own Godot editor is only let go of, never ended.</summary>
    private void LetGoGDScriptLater(ServerPlace place)
    {
        CancellationTokenSource waiting = new();
        if (gdscriptIdle.Remove(place, out CancellationTokenSource? earlier))
        {
            earlier.Cancel();
        }
        gdscriptIdle[place] = waiting;
        // Unheld: no screenshot or settling test waits on a server kept for a script yet to come.
        IdeErrors.Observe(BackgroundWork.Unheld(async () =>
        {
            try
            {
                await Task.Delay(GDScriptIdle, waiting.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            UiThread.Post(() =>
            {
                if (disposed || gdscriptIdle.GetValueOrDefault(place) != waiting || Of(place).Any() || !servers.Remove(place, out NodeServer? server))
                {
                    return;
                }
                gdscriptIdle.Remove(place);
                IdeErrors.Observe(server.End());
                godotAsked.Remove(place);
                IdeErrors.Write($"GDScript's language server let go for {place.Project ?? folder}: no GDScript of it open for {Durations.Said(GDScriptIdle)}");
                StateChanged?.Invoke(this, EventArgs.Empty);
            });
        }));
    }

    /// <summary>The runtime the servers run on, looked for once. Asked from any thread.</summary>
    private Task<NodeRuntime> FindNodeAsync() => UiThread.InvokeAsync(NodeAsync);

    /// <summary><see cref="FindNodeAsync"/> on the UI thread.</summary>
    private Task<NodeRuntime> NodeAsync()
    {
        (string? asked, RuntimeChoice choice) = (configured, runtime);
        return finding ??= BackgroundWork.Run(() => FindRuntimeAsync(folder, choice, asked));
    }

    /// <summary>The runtime a folder's web servers run on: the one Settings chooses, else the one its lockfile says, with
    /// the Node.js Settings names (<paramref name="node"/>, null for the PATH's). Reads files and asks the runtime its
    /// version: a worker's job.</summary>
    public static Task<NodeRuntime> FindRuntimeAsync(string folder, RuntimeChoice choice, string? node)
    {
        if (choice != RuntimeChoice.FromLockfile)
        {
            return NodeRuntime.FindAsync(KindOf(choice), node, null);
        }
        (ScriptRuntime kind, string source) = NodeProjects.RuntimeOf(folder);
        return NodeRuntime.FindAsync(kind, node, kind == ScriptRuntime.Node ? null : source);
    }

    private static ScriptRuntime KindOf(RuntimeChoice choice) => choice switch
    {
        RuntimeChoice.Bun => ScriptRuntime.Bun,
        RuntimeChoice.Deno => ScriptRuntime.Deno,
        _ => ScriptRuntime.Node,
    };

    private static (ToolConfiguration, string?, string) Linting(AppSettings settings) => (settings.Eslint, settings.EslintPackage, settings.EslintFiles);

    /// <summary>What a file's banner says: why its language server cannot run (no Node.js), offering to start one left
    /// stopped again; else that a package its package.json lists for ESLint, Prettier, Oxlint, Oxfmt or Biome is not
    /// installed, offering to install it (those the nearest such package.json lists); else nothing.</summary>
    private void Announce(OpenDocument document)
    {
        if (!documents.TryGetValue(document, out WebDocument? features))
        {
            return;
        }
        if (ServerOf(document) is { Problem: { } problem } server)
        {
            ServerPlace place = PlaceOf(features);
            document.Notice = Notice(Language.ForPath(document.Path), problem);
            document.NoticeAction = server.LeftStopped ? new NoticeAction("Start it again", () => StartAgain(place)) : null;
            return;
        }
        List<string> tools = [];
        List<string> packages = [];
        string? manifest = null;
        void Lacks(string tool, string package, string? listing)
        {
            if (listing is not null && (manifest is null || PathNames.Same(listing, manifest)))
            {
                tools.Add(tool);
                packages.Add(package);
                manifest = listing;
            }
        }
        Lacks("ESLint", "eslint", Tools.EslintMissing(document.Path));
        Lacks("Prettier", "prettier", Prettier.Missing(document.Path));
        foreach (WebTool tool in WebTool.Installed)
        {
            Lacks(tool.Name, tool.Package!, tool.Missing(document.Path));
        }
        if (manifest is null)
        {
            document.Notice = null;
            document.NoticeAction = null;
            return;
        }
        string where = Path.GetDirectoryName(manifest)!;
        string install = $"{NodeProjects.ProgramOf(ManagerOf(where))} install";
        if (installing.Contains(where))
        {
            document.Notice = $"Running {install} in {Shown(where)}…";
            document.NoticeAction = null;
            return;
        }
        string holder = YarnPnp.ManifestFor(where) is null ? "node_modules" : YarnPnp.ManifestName;
        document.Notice = tools.Count == 1
            ? $"{tools[0]} is not installed: {Shown(manifest)} lists {packages[0]}, which is not in {holder}."
            : $"{Plural.Listed(tools)} are not installed: {Shown(manifest)} lists {Plural.Listed(packages)}, which are not in {holder}.";
        document.NoticeAction = new NoticeAction($"Run '{install}'", () => IdeErrors.Observe(InstallAsync(where)));
    }

    /// <summary>The package manager that installs a package's folder, as its lockfile or packageManager field says.</summary>
    private NodeManager ManagerOf(string where) => PackageIn(where)?.Manager ?? NodeManager.Npm;

    private NodePackage? PackageIn(string where) =>
        NodeProjects.Read(folder, [PathNames.Canonical(Path.Combine(where, NodeProjects.ManifestName))]).FirstOrDefault();

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
            Tools.Attach(document, features);
        }
        AnnounceAll();
    }

    /// <summary>Files changed on disk: a package.json, lock file, Yarn's .pnp.cjs or a tool's configuration (a biome.json
    /// come or gone) looks for the packages and the tools again, and, under the runtime the lockfile says, for the runtime;
    /// and each tool hears of its configuration's changes.</summary>
    public void FilesChanged(IReadOnlyCollection<string> paths)
    {
        UiThread.Check();
        if (paths.Any(p => NodeProjects.Concerns(p) || Path.GetFileName(p) == YarnPnp.ManifestName || WebTool.Installed.Any(t => t.Configures(Path.GetFileName(p)))))
        {
            Recheck();
            IdeErrors.Observe(FollowLockfileAsync());
        }
        Tools.FilesChanged(paths);
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
            Tools.Reset(WebTool.Eslint);
        }
        Recheck();
    }

    /// <summary>"Run 'npm install'": the install of the package manager of the folder of the package.json that lists what
    /// is missing (<see cref="ScriptRuns.Install"/>: npm, pnpm, Yarn or Bun, as its lockfile says), through ChildProcesses;
    /// the banners say so meanwhile, and once it is done ESLint checks the files it covers and the banners go.</summary>
    public async Task InstallAsync(string where)
    {
        UiThread.Check();
        if (disposed || !installing.Add(where))
        {
            return;
        }
        AnnounceAll();
        string install = $"{NodeProjects.ProgramOf(ManagerOf(where))} install";
        Message?.Invoke(this, $"Running {install} in {Shown(where)}…");
        try
        {
            string? asked = configured;
            (NodePackage? package, NodeRuntime node) = await BackgroundWork.Run(async () => (PackageIn(where), await NodeRuntime.FindAsync(asked)));
            if (package is null)
            {
                Message?.Invoke(this, $"{install} cannot run: {Shown(Path.Combine(where, NodeProjects.ManifestName))} does not read as a package.json");
                return;
            }
            (ProcessStartInfo? start, string? problem) = ScriptRuns.Install(package, node);
            if (start is null)
            {
                Message?.Invoke(this, $"{install} cannot run: {problem}");
                return;
            }
            ChildResult result = await BackgroundWork.Run(() => ChildProcesses.RunAsync(start, cancel: ending.Token), ending.Token);
            if (result.Succeeded)
            {
                Message?.Invoke(this, $"{install} finished in {Shown(where)}");
            }
            else
            {
                IdeErrors.Write($"{install} in {where} failed with {ExitCodes.Said(result.ExitCode)}:\n{result.Error}{result.Output}");
                string said = (result.Error + "\n" + result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? "the IDE's log has what it said";
                Message?.Invoke(this, $"{install} failed in {Shown(where)} ({ExitCodes.Said(result.ExitCode)}): {said}");
            }
        }
        catch (OperationCanceledException)
        {
            // The folder was closed.
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            Message?.Invoke(this, $"{install} could not run in {Shown(where)}: {error.Message}");
        }
        finally
        {
            installing.Remove(where);
            Recheck();
        }
    }

    public WebDocument? For(OpenDocument document) => documents.GetValueOrDefault(document);

    /// <summary>What the folder's pages link changed: each open HTML file's classes are checked again.</summary>
    public void StylesheetsChanged()
    {
        UiThread.Check();
        foreach (WebDocument features in documents.Values.Where(d => d.Kind == WebServerKind.Html))
        {
            features.StylesheetsChanged();
        }
    }

    /// <summary>What the status bar says of a file's server while it cannot answer (starting, no Node.js); null once it can.</summary>
    public string? StateOf(OpenDocument document) => ServerOf(document) is { Session: null } server ? server.State : null;

    /// <summary>What the status bar says of a file's running server worth saying: for a script, the TypeScript it runs,
    /// the project's own or, marked so, the one Passenger carries; null otherwise.</summary>
    public string? DetailOf(OpenDocument document) =>
        LanguageOf(document.Path) is { Kind: WebServerKind.TypeScript } && ServerOf(document)?.Session?.Server.TypeScript is { } typescript
            ? $"TypeScript {typescript.Version}{(typescript.Source == "bundled" || !trusted || !ownTypeScript ? " (Passenger's)" : string.Empty)}"
            : null;

    /// <summary>Whether a file's server cannot start (no Node.js, one too old, a server missing), so none will answer for it.</summary>
    public bool CannotStart(OpenDocument document) => ServerOf(document) is { Problem: not null };

    /// <summary>What the status bar says when a command (<paramref name="what"/>) asks a file's server before it can answer.</summary>
    public string Unready(string what, OpenDocument document) =>
        LanguageOf(document.Path) is { } language
            ? $"{what} needs the {Name(language.Kind)} language server: {ServerOf(document) switch { { Problem: { } problem } => problem, { State.Length: > 0 } server => $"it is {server.State}", _ => "it has not started" }}"
            : $"{what} needs a language server, and none reads {document.Name}";

    /// <summary>The HTML server, started if it has not been (Razor's HTML asks it), once it can answer; null without a
    /// Node.js to run it, or once it has stopped. Called on any thread.</summary>
    public Task<LanguageServer?> HtmlAsync(CancellationToken cancel) => UiThread.InvokeAsync(async () =>
    {
        if (disposed)
        {
            return null;
        }
        NodeServer server = ServerFor(new ServerPlace(WebServerKind.Html, null));
        if (server.Session is null && server.Problem is null && server.Starting is null)
        {
            Start(server);
        }
        LanguageSession? started = server.Starting is { } starting ? await starting : server.Session;
        return started?.Server is { HasExited: false } running ? running : null;
    });

    /// <summary>Under the runtime the lockfile says, the servers started again on another once a lockfile now says it.</summary>
    private async Task FollowLockfileAsync()
    {
        if (runtime != RuntimeChoice.FromLockfile || finding is not { IsCompletedSuccessfully: true } found)
        {
            return;
        }
        ScriptRuntime now = await BackgroundWork.Run(() => NodeProjects.RuntimeOf(folder).Runtime);
        if (!disposed && finding == found && found.Result.Kind != now)
        {
            IdeErrors.Write($"The web language servers start again for {folder} on {NodeRuntime.NameOf(now)}, which its lockfile now says");
            Restart();
        }
    }

    /// <summary>Runs the servers on another runtime (Settings changed it), or another Node.js (null for the PATH's): those
    /// running end, and start again on it with their files, as does any that had none to run on.</summary>
    public void UseRuntime(RuntimeChoice choice, string? node)
    {
        UiThread.Check();
        if (disposed || (node == configured && choice == runtime))
        {
            return;
        }
        configured = node;
        runtime = choice;
        Restart();
    }

    /// <summary>The runtime looked for again, and every server started again on it.</summary>
    private void Restart()
    {
        finding = null;
        Tools.Reset();
        IdeErrors.Observe(Prettier.ResetAsync());
        foreach ((ServerPlace place, NodeServer server) in servers.ToList())
        {
            Replace(place, server);
        }
    }

    /// <summary>The folder came to be trusted: TypeScript can use the project's own TypeScript, Buf can read its
    /// configuration, and ESLint and Prettier check and format its files from now on.</summary>
    public void Trust()
    {
        UiThread.Check();
        if (disposed || trusted)
        {
            return;
        }
        trusted = true;
        Tools.Reset();
        IdeErrors.Observe(Prettier.ResetAsync());
        foreach ((ServerPlace place, NodeServer server) in servers.Where(s => s.Key.Kind is WebServerKind.TypeScript or WebServerKind.Protobuf).ToList())
        {
            Replace(place, server);
        }
        Recheck();
    }

    /// <summary>The folder's trust was taken back: ESLint and Prettier end, TypeScript's server starts again on Passenger's
    /// own TypeScript, Buf stops, and a GDScript server on a headless Godot editor, which loads the project's native extensions, ends
    /// for one on the person's own editor where it runs.</summary>
    public void Untrust()
    {
        UiThread.Check();
        if (disposed || !trusted)
        {
            return;
        }
        trusted = false;
        Tools.Reset();
        IdeErrors.Observe(Prettier.ResetAsync());
        foreach ((ServerPlace place, NodeServer server) in servers.Where(s => s.Key.Kind is WebServerKind.TypeScript or WebServerKind.Protobuf
            || s.Key.Kind == WebServerKind.GDScript && (godotAsked.Contains(s.Key) || GodotHeadlessAtOpen)).ToList())
        {
            godotAsked.Remove(place);
            Replace(place, server);
        }
        Recheck();
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
        ServerPlace place = new(WebServerKind.Dockerfile, null);
        if (servers.TryGetValue(place, out NodeServer? server) && server.Problem is null)
        {
            Replace(place, server);
        }
    }

    /// <summary>A place's server, made as a file first needs it, counting its restarts in <paramref name="restarts"/>
    /// (those of the servers before it in its place).</summary>
    private NodeServer ServerFor(ServerPlace place, ServerRestarts? restarts = null)
    {
        if (servers.TryGetValue(place, out NodeServer? server))
        {
            return server;
        }
        WebServerKind kind = place.Kind;
        NodeServer made = null!;
        made = new NodeServer(Name(kind), place.Project ?? folder, NodeAsync, restarts ?? new ServerRestarts())
        {
            Package = Path.Combine(Modules, Package(kind), "package.json"),
            PreloadNeeds = kind == WebServerKind.Yaml ? JsonSchemas.YamlPreloadNode : null,
            Missing = () => Missing(kind),
            Command = node => Starting(kind, node),
            Refused = node => Refused(kind, node),
            Elsewhere = kind switch
            {
                WebServerKind.GDScript => () => GodotStart(place),
                WebServerKind.Protobuf => ProtobufStart,
                _ => null,
            },
            Configure = language => ConfigureAsync(kind, language),
            Listen = started => started.Server.TypeScriptVersion += (_, _) => UiThread.Post(() => StateChanged?.Invoke(this, EventArgs.Empty)),
            Started = started => Started(place, started),
            Unavailable = () => Unavailable(place),
            Unasked = () => Unasked(place),
            Refresh = () =>
            {
                foreach (WebDocument features in Of(place))
                {
                    features.Refresh();
                }
            },
            Lost = what => Lost(place, made, what),
        };
        servers[place] = made;
        return made;
    }

    /// <summary>A project's GDScript server, on the Godot Settings names as its start begins: the person's editor's, or a
    /// headless editor's once one was asked for.</summary>
    private Func<CancellationToken, Task<LanguageSession>> GodotStart(ServerPlace place)
    {
        (string? setting, IReadOnlyDictionary<string, string> environment) = (settings().GodotExecutable, GodotEnvironment);
        bool headless = (godotAsked.Contains(place) || GodotHeadlessAtOpen) && WorkspaceTrust.Trusts(place.Project ?? folder);
        return cancel => Godot.GodotScripts.StartAsync(place.Project, setting, environment, LogFolder, headless, cancel);
    }

    /// <summary>Buf reads the workspace's configuration, which can name check plugins, only in a trusted folder.</summary>
    private Func<CancellationToken, Task<LanguageSession>> ProtobufStart()
    {
        bool allowed = trusted;
        return cancel =>
        {
            if (!allowed)
            {
                throw new NodeServer.CannotStart("the folder must be trusted to start its Buf language server", "folder not trusted");
            }
            string program = Programs.OnPath(OperatingSystem.IsWindows() ? "buf.exe" : "buf")
                ?? throw new NodeServer.CannotStart("install the Buf CLI and put it on the PATH for completion, navigation and diagnostics", "no Buf CLI");
            ProcessStartInfo process = ChildProcesses.Command(program, ["lsp", "serve"], folder);
            return LanguageSession.StartAsync(new ServerStart(process, folder, LogFolder, ClientCapabilities.Web), cancel);
        };
    }

    /// <summary>Godot's server of a script's project asked for, for what Passenger's own reading of it has no answer for:
    /// started, a headless editor where the person's own serves none, unless one serves, is starting or cannot start, or
    /// the project is in a folder not trusted.</summary>
    public void AskGodot(OpenDocument document)
    {
        UiThread.Check();
        if (disposed || !documents.TryGetValue(document, out WebDocument? features) || features.Kind != WebServerKind.GDScript
            || !WorkspaceTrust.Trusts(features.GodotProject ?? folder))
        {
            return;
        }
        ServerPlace place = PlaceOf(features);
        godotAsked.Add(place);
        NodeServer server = ServerFor(place);
        if (server.Session is null && server.Problem is null && server.Starting is null)
        {
            Start(server);
        }
    }

    private void Start(NodeServer server)
    {
        server.Start();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A place's server started: its files connected to it, their banners said again.</summary>
    private void Started(ServerPlace place, LanguageSession started)
    {
        foreach (WebDocument features in Of(place))
        {
            features.Connect(started);
        }
        foreach (OpenDocument open in Open(place))
        {
            Announce(open);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Why a server cannot start that this build is to blame for: its package, or Passenger's preload for the
    /// Dockerfile or YAML server, is not beside the app.</summary>
    private static string? Missing(WebServerKind kind) =>
        kind == WebServerKind.GDScript ? null
        : !File.Exists(Script(kind)) ? $"its language server is missing from the app's folder ({Script(kind)})"
        : Preload(kind) is { } preload && !File.Exists(preload) ? $"Passenger's part of its language server is missing from the app's folder ({preload})"
        : null;

    /// <summary>Passenger's preload a server runs with, null for none: the Dockerfile server's, which keeps it from Docker Hub
    /// unless Settings allows it (<see cref="DockerHubTags"/>), and the YAML server's, which has it ask Passenger for each
    /// schema's file rather than read it itself (<see cref="JsonSchemas.YamlPreload"/>).</summary>
    private static string? Preload(WebServerKind kind) => kind switch
    {
        WebServerKind.Dockerfile => DockerHubTags.Preload,
        WebServerKind.Yaml => JsonSchemas.YamlPreload,
        _ => null,
    };

    /// <summary>Why a server cannot run on a runtime that runs its script: the YAML server's preload has its imports
    /// resolved through <c>module.registerHooks</c>, which Bun lacks.</summary>
    private static string? Refused(WebServerKind kind, NodeRuntime node) =>
        kind == WebServerKind.Yaml && node.Kind == ScriptRuntime.Bun
            ? $"Passenger's part of the YAML language server needs Node.js's module.registerHooks, which Bun lacks: choose Node.js or Deno in {NodeRuntime.SettingsPlace}"
            : null;

    /// <summary>A server that cannot run: its files keep their colours and say why in their banner, the status bar in a
    /// few words.</summary>
    private void Unavailable(ServerPlace place)
    {
        foreach (OpenDocument open in Open(place))
        {
            documents[open].Unreached();
            Announce(open);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A place's server found nothing to start yet: its files wait for no checks but Passenger's own reading's.</summary>
    private void Unasked(ServerPlace place)
    {
        foreach (WebDocument features in Of(place))
        {
            features.Unreached();
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

    /// <summary>How a server starts: its script on the folder's runtime in the folder, with the options its package documents
    /// and its preload (<see cref="Preload"/>), TypeScript's in a trusted Plug'n'Play project with its .pnp.cjs. TypeScript's
    /// never installs type packages from npm (automatic type acquisition) behind the person's back, and renames a name where
    /// it is declared and everywhere it is used from any of them (an import's too, else renamed alone, aliased), answering
    /// from its project once loaded rather than from the open files alone.</summary>
    private ServerStart Starting(WebServerKind kind, NodeRuntime node)
    {
        string? pnp = kind == WebServerKind.TypeScript && trusted ? YarnPnp.ManifestFor(folder) : null;
        ProcessStartInfo process = node.Start(Script(kind), ["--stdio"], folder, Preload(kind), pnp);
        if (kind == WebServerKind.Dockerfile && DockerHubTags.Told(asksDockerHub, DockerHub) is { } told)
        {
            process.Environment[DockerHubTags.Variable] = told;
        }
        string? own = kind == WebServerKind.TypeScript && trusted ? OwnTsserver(folder) : null;
        if (kind == WebServerKind.TypeScript)
        {
            ownTypeScript = own is not null;
        }
        object? options = kind switch
        {
            WebServerKind.TypeScript => new
            {
                hostInfo = "Passenger",
                disableAutomaticTypingAcquisition = true,
                // One tsserver (a second would answer from the open files alone), the project's own only in a folder trusted.
                tsserver = new { logVerbosity = "off", useSyntaxServer = "never", path = own ?? CarriedTsserver },
                preferences = new { includeCompletionsForModuleExports = true, includeCompletionsForImportStatements = true, providePrefixAndSuffixTextForRename = false },
            },
            WebServerKind.Html => new { provideFormatter = true, embeddedLanguages = new { css = true, javascript = true } },
            WebServerKind.Css => new { provideFormatter = true },
            // Both read their settings from workspace/configuration alone.
            WebServerKind.Dockerfile or WebServerKind.Yaml => null,
            // Every schema asked of Passenger, a file's too, so none is read from another machine.
            _ => new { provideFormatter = true, handledSchemaProtocols = Array.Empty<string>() },
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
    /// the rest left to its defaults (the Dockerfile server's checks and layout among them). TypeScript's
    /// <c>formattingOptions</c> stays unanswered: its server keeps a file's first answer while it runs, so each file tells
    /// its tsserver how it indents itself, again as that changes (<see cref="WebDocument"/>).</summary>
    private static object? Section(WebServerKind kind, string? section) => (kind, section) switch
    {
        (WebServerKind.Css, "css" or "scss" or "less") => new { validate = true },
        (WebServerKind.Html, "html") => new { validate = new { scripts = true, styles = true }, format = new { enable = true } },
        (WebServerKind.Html, "css") => new { validate = true },
        (WebServerKind.Yaml, "yaml") => YamlSettings,
        _ => null,
    };

    /// <summary>The YAML server's settings: checks, completion, hover and formatting on; the Compose specification's schema,
    /// carried, for compose files; Compose's merge tags known; and nothing fetched by the server itself, neither
    /// SchemaStore's catalogue nor Kubernetes' schemas (a schema a file names over HTTP is asked of <see cref="JsonSchemas"/>,
    /// as the JSON server's are, once <c>yaml/registerContentRequest</c> has told it to, and a schema's file through its
    /// preload).</summary>
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

    /// <summary>Holds the next server's configuring, its process started, until <paramref name="until"/> ends, for a test of
    /// what goes on meanwhile; its process's id once it is held.</summary>
    public Task<int> HoldNextConfigure(Task until)
    {
        TaskCompletionSource<int> holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        configureHeld = (until, holding);
        return holding.Task;
    }

    private async Task ConfigureAsync(WebServerKind kind, LanguageServer server)
    {
        if (configureHeld is { } hold)
        {
            configureHeld = null;
            hold.Holding.SetResult(server.ProcessId);
            await hold.Until;
        }
        // These servers need no configuration notification from Passenger.
        if (kind is not (WebServerKind.GDScript or WebServerKind.Protobuf))
        {
            await Configure(kind, server);
        }
    }

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

    /// <summary>A server gone: started again with its files, unless servers in its place have gone too often to be worth it,
    /// when it is left stopped until a person starts it again (<see cref="StartAgain"/>).</summary>
    private void Lost(ServerPlace place, NodeServer server, string what)
    {
        if (server.Restarts.Again())
        {
            Replace(place, server);
            Stopped?.Invoke(this, new WebServerLost(ServerRestarts.StartedAgain(what), null));
            return;
        }
        Replace(place, server, stopped: $"its language server stopped {ServerRestarts.Often}");
        Stopped?.Invoke(this, new WebServerLost(ServerRestarts.LeftStopped(what), () => StartAgain(place)));
    }

    /// <summary>Starts a place's server left stopped again at a person's word, with its files, its restarts counted afresh:
    /// it runs again once what ended it is gone.</summary>
    private void StartAgain(ServerPlace place)
    {
        UiThread.Check();
        if (disposed || servers.GetValueOrDefault(place) is not { LeftStopped: true } server)
        {
            return;
        }
        server.Restarts.Forget();
        IdeErrors.Write($"The {Name(place.Kind)} language server is started again for {place.Project ?? folder}, as asked");
        Message?.Invoke(this, $"Starting the {Name(place.Kind)} language server again");
        Replace(place, server);
    }

    /// <summary>Ends a server (or its start) and lets its files go, then gives them a new one: started, or left stopped for
    /// the reason given, which their banners say.</summary>
    private void Replace(ServerPlace place, NodeServer server, string? stopped = null)
    {
        IdeErrors.Observe(server.End());
        List<OpenDocument> open = Open(place);
        foreach (OpenDocument document in open)
        {
            Detach(document);
        }
        servers.Remove(place);
        NodeServer again = ServerFor(place, server.Restarts);
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
        Task linter = Tools.DisposeAsync().AsTask();
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
