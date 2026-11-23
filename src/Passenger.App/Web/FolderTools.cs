using Passenger.App.Settings;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.App.Web;

/// <summary>The folder's own linters and formatters for the web languages (<see cref="WebTool"/>): ESLint, as Settings
/// configure it, through the language server Passenger carries; Oxlint, Oxfmt and Biome, wherever the folder's configuration
/// names one, through the language server of the package it installs, one for each package, working in the project the
/// package is installed for; only in a trusted folder, whose configuration is code. Each server runs on the web servers'
/// runtime (<see cref="NodeRuntime.Start"/>, a Yarn Plug'n'Play project's .pnp.cjs loaded), started with the first file it
/// serves, its files connected as it starts (a <see cref="ToolDocument"/> each); one
/// that ends (as it starts too) is started again with its files, a few times at most, then left stopped until a person
/// starts it again. A UI-thread object.</summary>
public sealed partial class FolderTools : IAsyncDisposable
{
    private readonly string folder;
    private readonly Func<AppSettings> settings;
    private readonly Func<Task<NodeRuntime>> node;
    private readonly Dictionary<Place, Served> servers = [];
    private readonly HashSet<string> said = new(StringComparer.Ordinal);
    private bool disposed;

    /// <summary>What a server serves: a tool, for the package it runs (ESLint's, for the folder open).</summary>
    private readonly record struct Place(WebTool Tool, string Package)
    {
        public bool Equals(Place other) => Tool == other.Tool && PathNames.Comparer.Equals(Package, other.Package);

        public override int GetHashCode() => HashCode.Combine(Tool, PathNames.Comparer.GetHashCode(Package));
    }

    /// <summary>One place's server, null before the first file it serves, with its files.</summary>
    private sealed class Served
    {
        public NodeServer? Server { get; set; }

        public Dictionary<OpenDocument, ToolDocument> Documents { get; } = [];
    }

    /// <summary>Each place's restarts, counted across its servers for as long as the folder is open.</summary>
    private readonly Dictionary<Place, ServerRestarts> restarts = [];

    private ServerRestarts RestartsOf(Place place)
    {
        if (!restarts.TryGetValue(place, out ServerRestarts? counted))
        {
            restarts[place] = counted = new ServerRestarts();
        }
        return counted;
    }

    /// <summary>The tools for a folder, ESLint configured as Settings says, on the runtime <paramref name="node"/> finds.</summary>
    public FolderTools(string folder, Func<AppSettings> settings, Func<Task<NodeRuntime>> node)
    {
        this.folder = folder;
        this.settings = settings;
        this.node = node;
    }

    /// <summary>Something for the status bar: a server could not start, or said a file has no configuration.</summary>
    public event EventHandler<string>? Message;

    /// <summary>A server ended or stopped answering, with what was done about it, for the status bar.</summary>
    public event EventHandler<WebServerLost>? Stopped;

    /// <summary>A rule's page a server asked to be opened.</summary>
    public event EventHandler<Uri>? Browse;

    /// <summary>The package a tool serves a file with, null for a file it does not serve.</summary>
    public string? PackageFor(WebTool tool, string path) => tool == WebTool.Eslint ? EslintPackageFor(path) : tool.PackageFor(path);

    /// <summary>The file's share of each tool that serves it, in <see cref="WebTool.All"/>'s order.</summary>
    public IReadOnlyList<ToolDocument> For(OpenDocument document) =>
        [.. servers.Values.Select(s => s.Documents.GetValueOrDefault(document)).OfType<ToolDocument>().OrderBy(d => IndexOf(d.Tool))];

    /// <summary>The file's share of one tool, null where it does not serve it.</summary>
    public ToolDocument? For(OpenDocument document, WebTool tool) =>
        servers.Where(s => s.Key.Tool == tool).Select(s => s.Value.Documents.GetValueOrDefault(document)).FirstOrDefault(d => d is not null);

    private static int IndexOf(WebTool tool)
    {
        int at = 0;
        while (WebTool.All[at] != tool)
        {
            at++;
        }
        return at;
    }

    /// <summary>Serves an open file with each tool that serves it, now or once its server has started (starting it if it has
    /// not); a tool serving it already keeps it.</summary>
    public void Attach(OpenDocument document, WebDocument web)
    {
        UiThread.Check();
        if (disposed)
        {
            return;
        }
        foreach (WebTool tool in WebTool.All)
        {
            if (For(document, tool) is not null || PackageFor(tool, document.Path) is not { } package)
            {
                continue;
            }
            Place place = new(tool, tool == WebTool.Eslint ? folder : package);
            if (!servers.TryGetValue(place, out Served? served))
            {
                servers[place] = served = new Served();
            }
            ToolDocument share = new(document, web, tool);
            served.Documents[document] = share;
            web.Tools.Add(share);
            web.Tools.Sort((a, b) => IndexOf(a.Tool).CompareTo(IndexOf(b.Tool)));
            if (served.Server is null)
            {
                served.Server = Server(place, served);
                served.Server.Start();
            }
            else if (served.Server.Session is { } running)
            {
                Connect(document, share, running, served.Server.Runtime!);
            }
            else if (served.Server.Problem is not null)
            {
                // A server that cannot run, or was left stopped, holds none of its files' checks.
                share.Unreached();
            }
            // Else the start under way connects it with the rest.
        }
    }

    /// <summary>Connects a file to its server, unless ESLint's own package needs a later Node.js than the server runs on,
    /// which is said once for each package.</summary>
    private void Connect(OpenDocument document, ToolDocument served, LanguageSession running, NodeRuntime runtime)
    {
        if (served.Tool == WebTool.Eslint)
        {
            string package = EslintPackageFor(document.Path) is { } found ? Path.Combine(found, "package.json") : string.Empty;
            if (runtime.TooOldFor(package, "ESLint") is { } old)
            {
                if (said.Add("too old" + package))
                {
                    Message?.Invoke(this, $"ESLint cannot check {document.Name}: {old}");
                }
                served.Unreached();
                return;
            }
        }
        served.Connect(running);
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        foreach (Served served in servers.Values)
        {
            if (served.Documents.Remove(document, out ToolDocument? gone))
            {
                gone.Dispose();
            }
        }
    }

    /// <summary>Files changed on disk: each server is told of those that configure its tool (ESLint's configuration and
    /// ignore files, Biome's biome.json, a package.json), and checks its files again.</summary>
    public void FilesChanged(IReadOnlyCollection<string> paths)
    {
        UiThread.Check();
        foreach ((Place place, Served served) in servers)
        {
            List<object> changes = [.. paths.Where(p => place.Tool == WebTool.Eslint ? EslintConfigures(Path.GetFileName(p)) : place.Tool.Configures(Path.GetFileName(p)))
                .Select(p => new { uri = DocumentUris.FromPath(p), type = File.Exists(p) ? 2 : 3 })];
            if (changes.Count > 0 && served.Server?.Session is { } running)
            {
                running.Server.Notify(LspMethods.DidChangeWatchedFiles, new { changes });
            }
        }
    }

    /// <summary>Ends the servers of a tool, or of every tool, and lets their files go, for Settings, the folder's trust or the
    /// runtime changed: the caller attaches them again.</summary>
    public void Reset(WebTool? only = null)
    {
        UiThread.Check();
        foreach ((Place place, Served served) in servers.Where(s => only is null || s.Key.Tool == only).ToList())
        {
            LetGo(served);
            servers.Remove(place);
        }
        said.Clear();
    }

    /// <summary>A place's files let go and its server ended.</summary>
    private static void LetGo(Served served)
    {
        foreach (ToolDocument document in served.Documents.Values)
        {
            document.Dispose();
        }
        served.Documents.Clear();
        IdeErrors.Observe(served.Server?.End() ?? Task.CompletedTask);
        served.Server = null;
    }

    /// <summary>The server for a place: its files connected once it has started, its own messages heard, and its end a
    /// replacement. Without a runtime, the file's banner says why for its language server, which needs one as much.</summary>
    private NodeServer Server(Place place, Served served)
    {
        bool eslint = place.Tool == WebTool.Eslint;
        string? script = eslint ? EslintScript : place.Tool.ScriptOf(place.Package);
        string root = eslint ? folder : WebTool.ProjectOf(place.Package);
        NodeServer made = null!;
        made = new NodeServer(place.Tool.Name, root, node, RestartsOf(place))
        {
            Package = eslint ? Path.Combine(WebLanguages.Modules, "vscode-langservers-extracted", "package.json") : Path.Combine(place.Package, "package.json"),
            Missing = () => eslint ? (File.Exists(EslintScript) ? null : $"the ESLint language server is missing from the app's folder ({EslintScript})")
                : script is null ? $"{place.Tool.Package} in {PathNames.Shown(place.Package, folder)} has no {place.Tool.Command} command" : null,
            Command = runtime => new ServerStart(runtime.Start(script!, eslint ? ["--stdio"] : place.Tool.Arguments, root, pnp: YarnPnp.ManifestFor(root)), root, WebLanguages.LogFolder, ClientCapabilities.Web)
            {
                Configuration = eslint ? Section : _ => null,
            },
            HearsOwnLog = eslint,
            Listen = eslint ? Hear : null,
            Started = started =>
            {
                foreach ((OpenDocument document, ToolDocument tooled) in served.Documents)
                {
                    Connect(document, tooled, started, made.Runtime!);
                }
            },
            Unavailable = () =>
            {
                foreach (ToolDocument tooled in served.Documents.Values)
                {
                    tooled.Unreached();
                }
                if (!made.WithoutRuntime)
                {
                    Message?.Invoke(this, $"{place.Tool.Name} cannot run: {made.Problem}");
                }
            },
            Refresh = () =>
            {
                foreach (ToolDocument tooled in served.Documents.Values)
                {
                    tooled.Refresh();
                }
            },
            Lost = what => Lost(place, what),
        };
        return made;
    }

    /// <summary>A server gone: started again with its files, unless one has gone too often to be worth it, when it is left
    /// stopped until a person starts it again (<see cref="StartAgain"/>).</summary>
    private void Lost(Place place, string what)
    {
        if (!servers.TryGetValue(place, out Served? served))
        {
            return;
        }
        if (RestartsOf(place).Again())
        {
            Replace(place, served, stopped: null);
            Stopped?.Invoke(this, new WebServerLost(ServerRestarts.StartedAgain(what), null));
            return;
        }
        Replace(place, served, stopped: $"it stopped {ServerRestarts.Often}");
        Stopped?.Invoke(this, new WebServerLost(ServerRestarts.LeftStopped(what), () => StartAgain(place)));
    }

    /// <summary>Starts a server left stopped again at a person's word, with the files it serves, its restarts counted
    /// afresh: it runs again once what ended it is gone.</summary>
    private void StartAgain(Place place)
    {
        UiThread.Check();
        if (disposed || !servers.TryGetValue(place, out Served? served) || served.Server is not { LeftStopped: true })
        {
            return;
        }
        RestartsOf(place).Forget();
        IdeErrors.Write($"The {place.Tool.Name} language server is started again for {PathNames.Shown(place.Package, folder)}, as asked");
        Message?.Invoke(this, $"Starting the {place.Tool.Name} language server again");
        Replace(place, served, stopped: null);
    }

    /// <summary>Ends a place's server and gives its files a new one: started, or left stopped for the reason given, keeping
    /// them for a start at a person's word.</summary>
    private void Replace(Place place, Served served, string? stopped)
    {
        List<(OpenDocument Document, WebDocument Web)> open = [.. served.Documents.Select(d => (d.Key, d.Value.Web))];
        LetGo(served);
        if (stopped is null)
        {
            servers.Remove(place);
        }
        else
        {
            served.Server = Server(place, served);
            served.Server.Stop(stopped);
        }
        foreach ((OpenDocument document, WebDocument web) in open)
        {
            Attach(document, web);
        }
    }

    public ValueTask DisposeAsync()
    {
        UiThread.Check();
        disposed = true;
        List<Task> ending = [];
        foreach (Served served in servers.Values)
        {
            foreach (ToolDocument document in served.Documents.Values)
            {
                document.Dispose();
            }
            served.Documents.Clear();
            ending.Add(served.Server?.End() ?? Task.CompletedTask);
            served.Server = null;
        }
        servers.Clear();
        return ending.All(t => t.IsCompleted) ? ValueTask.CompletedTask : new ValueTask(Task.WhenAll(ending));
    }
}
