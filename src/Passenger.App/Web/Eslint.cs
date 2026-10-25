using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.App.CSharp;
using Passenger.App.Settings;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.Web;

/// <summary>ESLint for the folder open: VS Code's ESLint language server (carried under web/ in
/// vscode-langservers-extracted) on the web servers' Node.js, started with the first file it checks, running the
/// project's own eslint package (found from each file's folder up, or the one Settings names) with its own
/// configuration, for the files "Run for files" covers. Its problems are each file's as typed
/// (<see cref="EslintDocument"/>); a server that ends is started again with its files, a few times at most. A UI-thread
/// object.</summary>
public sealed class EslintService : IAsyncDisposable
{
    private readonly string folder;
    private readonly Func<AppSettings> settings;
    private readonly Func<Task<NodeRuntime>> node;
    private readonly Dictionary<OpenDocument, EslintDocument> documents = [];
    private readonly ServerRestarts restarts = new();
    private readonly HashSet<string> said = new(StringComparer.Ordinal);

    /// <summary>The server, running, starting or unable to run; null before the first file it checks.</summary>
    private NodeServer? server;
    private bool disposed;

    /// <summary>ESLint for a folder, configured as Settings says, on the Node.js <paramref name="node"/> finds.</summary>
    public EslintService(string folder, Func<AppSettings> settings, Func<Task<NodeRuntime>> node)
    {
        this.folder = folder;
        this.settings = settings;
        this.node = node;
    }

    /// <summary>Something for the status bar: the server could not start, ended, or said a file has no configuration.</summary>
    public event EventHandler<string>? Message;

    /// <summary>A rule's page the server asked to be opened.</summary>
    public event EventHandler<Uri>? Browse;

    /// <summary>Makes a workspace edit ESLint's command makes (a fix), on the UI thread: null once made, else why not.</summary>
    public Func<JsonElement, string?>? ApplyEdit { get; set; }

    /// <summary>The server's script in the package carried under web/.</summary>
    public static string Script => Path.Combine(WebLanguages.Modules, "vscode-langservers-extracted", "lib", "eslint-language-server", "eslintServer.js");

    /// <summary>The ESLint package that checks a file as Settings configure it, null for a file it does not check (Disabled,
    /// outside "Run for files", or no package found).</summary>
    public string? PackageFor(string path)
    {
        AppSettings now = settings();
        if (now.Eslint == ToolConfiguration.Disabled || !ProjectPackages.Covers(now.EslintFiles, folder, path))
        {
            return null;
        }
        return now.Eslint == ToolConfiguration.Manual ? ProjectPackages.Named(now.EslintPackage, folder) : ProjectPackages.Installed(path, "eslint");
    }

    /// <summary>The package.json that lists eslint for a file it is not installed for, under Automatic configuration.</summary>
    public string? Missing(string path)
    {
        AppSettings now = settings();
        return now.Eslint == ToolConfiguration.Automatic && ProjectPackages.Covers(now.EslintFiles, folder, path) ? ProjectPackages.Missing(path, "eslint") : null;
    }

    public EslintDocument? For(OpenDocument document) => documents.GetValueOrDefault(document);

    /// <summary>"Run eslint --fix on save": a save fixes first what ESLint can fix in each file it checks.</summary>
    public bool FixesOnSave => settings().EslintFixOnSave;

    /// <summary>Checks an open file, now or once the server has started (starting it if it has not), when ESLint covers it.</summary>
    public void Attach(OpenDocument document, WebDocument web)
    {
        UiThread.Check();
        if (disposed || documents.ContainsKey(document) || PackageFor(document.Path) is null)
        {
            return;
        }
        EslintDocument lint = new(document, web);
        documents[document] = lint;
        web.Lint = lint;
        if (server is null)
        {
            server = Server();
            server.Start();
        }
        else if (server.Session is { } running)
        {
            Connect(document, lint, running, server.Runtime!);
        }
        // Else the start under way connects it with the rest, or the server cannot run.
    }

    /// <summary>Connects a file to the server unless its eslint needs a later Node.js than the server runs on, which is
    /// said once for each package.</summary>
    private void Connect(OpenDocument document, EslintDocument lint, LanguageSession running, NodeRuntime runtime)
    {
        string package = PackageFor(document.Path) is { } found ? Path.Combine(found, "package.json") : string.Empty;
        if (runtime.TooOldFor(package, "ESLint") is { } old)
        {
            if (said.Add("too old" + package))
            {
                Message?.Invoke(this, $"ESLint cannot check {document.Name}: {old}");
            }
            return;
        }
        lint.Connect(running);
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.Remove(document, out EslintDocument? lint))
        {
            lint.Dispose();
        }
    }

    /// <summary>Files changed on disk: ESLint's configuration files, its ignore file and package.json are told to the
    /// server, which checks every file again.</summary>
    public void FilesChanged(IReadOnlyCollection<string> paths)
    {
        UiThread.Check();
        List<object> changes = [.. paths.Where(p => Configures(Path.GetFileName(p)))
            .Select(p => new { uri = DocumentUris.FromPath(p), type = File.Exists(p) ? 2 : 3 })];
        if (changes.Count > 0 && server?.Session is { } running)
        {
            running.Server.Notify(LspMethods.DidChangeWatchedFiles, new { changes });
        }
    }

    /// <summary>Whether a file configures ESLint: a configuration (flat or eslintrc), the ignore file, or package.json
    /// (its eslintConfig).</summary>
    private static bool Configures(string name) =>
        name is ".eslintignore" or "package.json" or ".eslintrc"
        || (name.StartsWith("eslint.config.", StringComparison.Ordinal) && Path.GetExtension(name) is ".js" or ".mjs" or ".cjs" or ".ts" or ".mts" or ".cts")
        || (name.StartsWith(".eslintrc.", StringComparison.Ordinal) && Path.GetExtension(name) is ".js" or ".cjs" or ".yaml" or ".yml" or ".json");

    /// <summary>Ends the server and lets every file go, for Settings or the Node.js changed: the caller attaches them again.</summary>
    public void Reset()
    {
        UiThread.Check();
        foreach (EslintDocument lint in documents.Values)
        {
            lint.Dispose();
        }
        documents.Clear();
        IdeErrors.Observe(server?.End() ?? Task.CompletedTask);
        server = null;
        said.Clear();
    }

    /// <summary>The server for the folder: its files connected once it has started, its own messages heard, and its end
    /// a replacement. Without a Node.js, the file's banner says why for its language server, which needs one as much.</summary>
    private NodeServer Server()
    {
        NodeServer made = null!;
        made = new NodeServer("ESLint", folder, node, restarts)
        {
            Package = Path.Combine(WebLanguages.Modules, "vscode-langservers-extracted", "package.json"),
            Missing = () => File.Exists(Script) ? null : $"the ESLint language server is missing from the app's folder ({Script})",
            Command = runtime => new ServerStart(ChildProcesses.Command(runtime, [Script, "--stdio"], folder), folder, WebLanguages.LogFolder, ClientCapabilities.Web)
            {
                Configuration = Section,
                ApplyEdit = edit => UiThread.InvokeAsync<string?>(() => disposed ? "the folder was closed" : ApplyEdit?.Invoke(edit) ?? "Passenger cannot make ESLint's edit here"),
            },
            HearsOwnLog = true,
            Listen = Hear,
            Started = started =>
            {
                foreach ((OpenDocument document, EslintDocument lint) in documents)
                {
                    Connect(document, lint, started, made.Runtime!);
                }
            },
            Unavailable = () =>
            {
                if (!made.WithoutNode)
                {
                    Message?.Invoke(this, $"ESLint cannot run: {made.Problem}");
                }
            },
            Refresh = () =>
            {
                foreach (EslintDocument lint in documents.Values)
                {
                    lint.Refresh();
                }
            },
            Lost = Lost,
        };
        return made;
    }

    /// <summary>What the server reads as each file's settings, asked for whole: lint as typed, the package found from
    /// the file's folder up unless Manual names one (by the node_modules it is in), the project's flat or eslintrc
    /// configuration as its eslint decides, and fixes and suppressions offered.</summary>
    private object? Section(string? section)
    {
        if (section is { Length: > 0 })
        {
            return null;
        }
        AppSettings now = settings();
        string? nodePath = now.Eslint == ToolConfiguration.Manual && ProjectPackages.Named(now.EslintPackage, folder) is { } package ? Path.GetDirectoryName(package) : null;
        JsonObject answer = new()
        {
            ["validate"] = "on",
            ["packageManager"] = "npm",
            ["useESLintClass"] = false,
            // Neither flat nor eslintrc forced: the project's eslint takes the configuration it finds.
            ["experimental"] = new JsonObject(),
            ["codeAction"] = new JsonObject
            {
                ["disableRuleComment"] = new JsonObject { ["enable"] = true, ["location"] = "separateLine", ["commentStyle"] = "line" },
                ["showDocumentation"] = new JsonObject { ["enable"] = true },
            },
            ["codeActionOnSave"] = new JsonObject { ["mode"] = "all" },
            ["format"] = false,
            ["quiet"] = false,
            ["onIgnoredFiles"] = "off",
            ["options"] = new JsonObject(),
            ["rulesCustomizations"] = new JsonArray(),
            ["run"] = "onType",
            ["problems"] = new JsonObject { ["shortenToSingleLine"] = false },
            ["nodePath"] = nodePath,
            ["workspaceFolder"] = new JsonObject { ["name"] = Path.GetFileName(folder), ["uri"] = DocumentUris.FromPath(folder) },
        };
        // As JSON whole, so nodePath goes as null rather than left out (the server takes a missing one for a path).
        return JsonDocument.Parse(answer.ToJsonString()).RootElement.Clone();
    }

    /// <summary>Listens to a server once it has started: its errors to the IDE's log, its own messages to the status bar
    /// once a kind and file.</summary>
    private void Hear(LanguageSession started)
    {
        LanguageServer server = started.Server;
        // Touched only by the server's messages, which come one at a time in order.
        string? introduced = null;
        bool configMissing = false;
        server.Logged += (_, message) =>
        {
            if (message.Type is not (MessageType.Error or MessageType.Warning))
            {
                return;
            }
            if (message.Message == UnexpectedError)
            {
                introduced = message.Message;
                return;
            }
            string said = introduced is null ? message.Message : $"{introduced} {message.Message}";
            introduced = null;
            if (IsConfigMissing(message.Message))
            {
                configMissing = true;
                return;
            }
            IdeErrors.Write($"ESLint language server (process {server.ProcessId}) {(message.Type == MessageType.Error ? "error" : "warning")}: {said}");
        };
        server.Said += (_, message) =>
        {
            bool unconfigured = false;
            if (message.Method == "eslint/status")
            {
                (unconfigured, configMissing) = (configMissing, false);
            }
            UiThread.Post(() => Heard(started, message, unconfigured));
        };
    }

    /// <summary>The first line vscode-eslint's catch-all writes before the error's stack, as a message of its own.</summary>
    private const string UnexpectedError = "An unexpected error occurred:";

    /// <summary>Whether what the server logged is ESLint's flat configuration not found (its config-file-missing, which
    /// the server takes for a failure): a project with eslint installed and no configuration, which is an answer for
    /// the status bar, not an error for the log.</summary>
    private static bool IsConfigMissing(string logged)
    {
        string first = logged.Split('\n', 2)[0].Trim();
        foreach (string lead in (string[])["An unexpected error occurred: ", "Error: "])
        {
            first = first.StartsWith(lead, StringComparison.Ordinal) ? first[lead.Length..] : first;
        }
        return first.StartsWith("Could not find config file", StringComparison.Ordinal);
    }

    /// <summary>One of the server's own messages: a rule's page to open, or a file it cannot check, said once; a file's
    /// status after its configuration was not found (<paramref name="unconfigured"/>) is said as eslint/noConfig is.</summary>
    private void Heard(LanguageSession from, ServerMessage message, bool unconfigured)
    {
        if (server?.Session != from)
        {
            return;
        }
        JsonElement body = message.Params;
        string? Text(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        string? Document() => body.ValueKind == JsonValueKind.Object && body.TryGetProperty("document", out JsonElement document) && document.ValueKind == JsonValueKind.Object
            && document.TryGetProperty("uri", out JsonElement uri) && uri.ValueKind == JsonValueKind.String ? Path.GetFileName(DocumentUris.ToPath(uri.GetString()!)) : null;
        switch (message.Method)
        {
            case "eslint/openDoc" when Text("url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out Uri? page):
                Browse?.Invoke(this, page);
                break;
            case "eslint/noConfig" when said.Add(message.Method + Document()):
                Message?.Invoke(this, $"ESLint: no configuration found for {Document() ?? "the file"}, so it is not checked");
                break;
            case "eslint/status" when unconfigured && Text("uri") is { } uri && Path.GetFileName(DocumentUris.ToPath(uri)) is { } name && said.Add("eslint/noConfig" + name):
                Message?.Invoke(this, $"ESLint: no configuration found for {name}, so it is not checked");
                break;
            case "eslint/noLibrary" when said.Add(message.Method + Document()):
                Message?.Invoke(this, $"ESLint: the eslint package could not be loaded for {Document() ?? "the file"}");
                break;
            case "eslint/exitCalled":
                IdeErrors.Write($"ESLint language server (process {from.Server.ProcessId}): a plugin called process.exit: {body}");
                break;
        }
    }

    /// <summary>A server gone: started again with its files, unless one has gone too often to be worth it.</summary>
    private void Lost(string what)
    {
        bool again = restarts.Again();
        List<(OpenDocument Document, WebDocument Web)> open = [.. documents.Select(d => (d.Key, d.Value.Web))];
        Reset();
        if (again)
        {
            foreach ((OpenDocument document, WebDocument web) in open)
            {
                Attach(document, web);
            }
        }
        else
        {
            server = Server();
            server.Stop($"it stopped {ServerRestarts.Often}");
        }
        Message?.Invoke(this, ServerRestarts.Said(what, again));
    }

    public ValueTask DisposeAsync()
    {
        UiThread.Check();
        disposed = true;
        foreach (EslintDocument lint in documents.Values)
        {
            lint.Dispose();
        }
        documents.Clear();
        Task ending = server?.End() ?? Task.CompletedTask;
        server = null;
        return new ValueTask(ending);
    }
}

/// <summary>One of ESLint's fixes, named as "ESLint: Fix 'prefer-const'", with the server's code action that makes
/// it.</summary>
public sealed record EslintFix(string Title, CodeAction Action, string? Rule)
{
    /// <summary>A rule's page to open rather than an edit to make.</summary>
    public bool OpensDocumentation => Action.Command?.Command == "eslint.openRuleDoc";
}

/// <summary>One open file as ESLint checks it: kept in step with the ESLint server, its problems asked for as typing pauses
/// and shown with the rest of the file's (<see cref="WebDocument.ShowLint"/>) as "ESLint: message (rule)" at their
/// severity, the rule's page their link, carried with the text between answers.</summary>
public sealed class EslintDocument : IDisposable
{
    /// <summary>How long typing must pause before the file is checked again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    private readonly OpenDocument document;
    private readonly ServerDiagnostics diagnostics;
    private LanguageSession? session;
    private bool disposed;

    internal EslintDocument(OpenDocument document, WebDocument web)
    {
        this.document = document;
        diagnostics = new ServerDiagnostics(document, holdsChecks: true);
        Web = web;
        document.Text.Changed += OnChanged;
        document.Saved += OnSaved;
    }

    public WebDocument Web { get; }

    public LspDocument? Lsp { get; private set; }

    /// <summary>The problems last reported, on the server's lines of the text as it stands.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics.Placed;

    /// <summary>The markers the problems draw as, where the text has carried them.</summary>
    public IReadOnlyList<Marker> Markers => diagnostics.Markers;

    internal void Connect(LanguageSession started)
    {
        if (Lsp is not null || disposed)
        {
            return;
        }
        session = started;
        Lsp = started.Open(document.Path, document.Text, Web.LanguageId);
        Refresh();
    }

    /// <summary>The problems asked for again (the server's refresh).</summary>
    public void Refresh() => Pull(TimeSpan.Zero);

    /// <summary>The fixes for the problems on the caret's line: each fixable rule's, its suggestions, the file's, then
    /// the suppressions and the rule's page.</summary>
    public async Task<IReadOnlyList<EslintFix>> FixesAsync(int caret, CancellationToken cancel = default)
    {
        if (Lsp is not { } lsp)
        {
            return [];
        }
        int line = lsp.PositionOf(caret).Line;
        List<Diagnostic> here = [.. Diagnostics.Where(d => d.Range.Start.Line <= line && line <= d.Range.End.Line)];
        if (here.Count == 0)
        {
            return [];
        }
        IReadOnlyList<CodeAction> actions = await lsp.CodeActionsAsync(new TextRange(caret, 0), here, cancel);
        return [.. actions.Where(a => a.Command is not null && a.Disabled is null).Select(Named).OrderBy(Rank)];
    }

    /// <summary>The edit that fixes every problem ESLint can fix in the file (its <c>source.fixAll.eslint</c>, what
    /// <c>eslint --fix</c> would write), for the text as it is now; null when the server has none to give.</summary>
    public async Task<JsonElement?> FixAllAsync(CancellationToken cancel = default) =>
        Lsp is { } lsp ? (await lsp.SourceActionsAsync("source.fixAll.eslint", cancel)).FirstOrDefault(a => a.Edit is not null)?.Edit : null;

    /// <summary>Makes a fix: the server's command runs, and makes its edit through the client.</summary>
    public async Task RunAsync(EslintFix fix, CancellationToken cancel = default)
    {
        if (Lsp is null || session is not { } running || fix.Action.Command is not { } command)
        {
            return;
        }
        await running.Server.ExecuteCommandAsync(command, cancel);
    }

    private static EslintFix Named(CodeAction action)
    {
        string? rule = action.Command?.Arguments is [JsonElement first, ..] && first.ValueKind == JsonValueKind.Object
            && first.TryGetProperty("ruleId", out JsonElement id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
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
        return new EslintFix(title, action, rule);
    }

    private static int Rank(EslintFix fix) => fix.Action.Command?.Command switch
    {
        "eslint.applySingleFix" => 0,
        "eslint.applySuggestion" => 1,
        "eslint.applyAllFixes" => 2,
        "eslint.applySameFixes" => 3,
        "eslint.applyDisableLine" => 4,
        "eslint.applyDisableFile" => 5,
        _ => 6,
    };

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        diagnostics.Stop();
        document.Text.Changed -= OnChanged;
        document.Saved -= OnSaved;
        Lsp?.Dispose();
        Lsp = null;
        if (Web.Lint == this)
        {
            Web.Lint = null;
            Web.ShowLint();
        }
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        diagnostics.Moved();
        Pull(Settle);
    }

    private void OnSaved(object? sender, EventArgs e) => Lsp?.Saved();

    private void Pull(TimeSpan delay)
    {
        if (Lsp is not { } lsp)
        {
            return;
        }
        diagnostics.Pull(delay, async cancel =>
        {
            if (await diagnostics.PulledAsync(lsp, cancel) is { } pulled && lsp == Lsp)
            {
                diagnostics.Take(pulled, Marker);
                if (Web.Lint == this)
                {
                    Web.ShowLint();
                }
            }
        });
    }

    /// <summary>A problem as "ESLint: message (rule)", the rule's name its page's link, at the severity the
    /// configuration gives the rule, never faded as unnecessary (the server tags no-unused-vars so).</summary>
    private static Marker? Marker(Diagnostic diagnostic, TextRange range)
    {
        if (DiagnosticMarks.Kind(diagnostic.Severity) is not { } k)
        {
            return null;
        }
        string? rule = diagnostic.Code is { ValueKind: JsonValueKind.String } code ? code.GetString() : null;
        string message = rule is null ? $"ESLint: {diagnostic.Message}" : $"ESLint: {diagnostic.Message} ({rule})";
        return new Marker(range, k, message) { Link = rule is null ? null : diagnostic.CodePage, LinkText = rule };
    }
}
