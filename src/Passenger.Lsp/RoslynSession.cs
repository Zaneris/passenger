using System.Runtime.CompilerServices;
using System.Text.Json;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.Lsp;

/// <summary>Roslyn's language server working on one folder: it opens the solution it is given (or, with
/// none, the projects) and keeps every open C# document in step. Requests are made per document
/// (<see cref="LspDocument"/>), in the editor's own offsets.</summary>
public sealed class RoslynSession : IAsyncDisposable, IDocumentHost
{
    private readonly Dictionary<string, LspDocument> documents = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource coloured = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private RoslynSession(LanguageServer server, string folder, string? solution, IReadOnlyList<string> projects)
    {
        Server = server;
        Folder = folder;
        Solution = solution;
        Projects = solution is null ? projects : [];
        server.ProjectsLoaded += (_, _) =>
        {
            // The server says so after each open it was sent: the projects opened first, then the rest.
            if (Interlocked.Increment(ref completed) < opens)
            {
                firstLoaded.TrySetResult();
                return;
            }
            IsLoaded = true;
            Loaded?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>How many opens the server was sent, and how many it has said it finished.</summary>
    private int opens = 1;
    private int completed;

    /// <summary>Done once the projects opened first have loaded, after which the rest are opened.</summary>
    private readonly TaskCompletionSource firstLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The projects have loaded, so answers now know the whole solution. Raised on any
    /// thread.</summary>
    public event EventHandler? Loaded;

    public LanguageServer Server { get; }
    public string Folder { get; }
    public string? Solution { get; }

    /// <summary>The projects it opened, with no solution to open.</summary>
    public IReadOnlyList<string> Projects { get; }

    public bool IsLoaded { get; private set; }

    /// <summary>Done once the server has first given a document its colours.</summary>
    public Task FirstColours => coloured.Task;

    internal void Coloured() => coloured.TrySetResult();

    void IDocumentHost.Coloured() => Coloured();

    void IDocumentHost.Forget(LspDocument document) => Forget(document);

    /// <summary>Roslyn's diagnostics are pulled, from each source it registers.</summary>
    bool IDocumentHost.PullsDiagnostics => true;

    PushPace IDocumentHost.Pushes => PushPace.Latest;

    /// <summary>Starts a server on a folder and has it open the solution, else the projects, those <paramref name="first"/>
    /// names loaded alone before the rest. <paramref name="started"/> is given the server before it opens anything, so
    /// what it says of the load is heard from the start; with <paramref name="feed"/>, a server that has Passenger's
    /// project loader loads the projects from its design-time builds. Cancelled or failed, it ends the server it started,
    /// so one given up (another folder opened) never goes on loading this folder unseen.</summary>
    public static async Task<RoslynSession> StartAsync(string folder, string? solution, IReadOnlyList<string> projects, string logFolder,
        CancellationToken cancel = default, IReadOnlyDictionary<string, object?>? options = null, Action<LanguageServer>? started = null,
        Func<Task<IReadOnlyList<string>>>? first = null, IProjectFeed? feed = null, string? stateCache = null)
    {
        LanguageServer server = await LanguageServer.StartRoslynAsync(folder, logFolder, cancel, options, stateCache).ConfigureAwait(false);
        RoslynSession session = new(server, folder, solution, projects);
        try
        {
            cancel.ThrowIfCancellationRequested();
            started?.Invoke(server);
            if (feed is not null && await HasLoaderAsync(server, cancel).ConfigureAwait(false) && await feed.ProjectsAsync(cancel).ConfigureAwait(false) is { } fed)
            {
                server.Feed = feed;
                session.Fed = true;
                (IReadOnlyList<ProjectToOpen> before, IReadOnlyList<ProjectToOpen> rest) = fed;
                if (before.Count > 0)
                {
                    session.opens = 2;
                    await server.NotifyAsync(ProjectMethods.Load, new LoadProjectsParams(before, solution)).ConfigureAwait(false);
                    Core.UnawaitedWork.Observe(session.OpenRestAsync(() => server.NotifyAsync(ProjectMethods.Load, new LoadProjectsParams(rest, solution))));
                    return session;
                }
                await server.NotifyAsync(ProjectMethods.Load, new LoadProjectsParams(rest, solution)).ConfigureAwait(false);
                return session;
            }
            IReadOnlyList<string> ahead = first is null ? [] : await first().ConfigureAwait(false);
            if (ahead.Count > 0)
            {
                session.opens = 2;
                await server.NotifyAsync(LspMethods.ProjectOpen, new OpenProjectParams([.. ahead.Select(DocumentUris.FromPath)])).ConfigureAwait(false);
                // The rest once those have loaded: opened at once, the server would take them all in one batch.
                Core.UnawaitedWork.Observe(session.OpenRestAsync(() => solution is not null
                    ? server.NotifyAsync(LspMethods.SolutionOpen, new OpenSolutionParams(DocumentUris.FromPath(solution)))
                    : server.NotifyAsync(LspMethods.ProjectOpen, new OpenProjectParams([.. projects.Select(DocumentUris.FromPath)]))));
                return session;
            }
            if (solution is not null)
            {
                await server.NotifyAsync(LspMethods.SolutionOpen, new OpenSolutionParams(DocumentUris.FromPath(solution))).ConfigureAwait(false);
            }
            else
            {
                await server.NotifyAsync(LspMethods.ProjectOpen, new OpenProjectParams([.. projects.Select(DocumentUris.FromPath)])).ConfigureAwait(false);
            }
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return session;
    }

    /// <summary>Opens the rest of the solution once the projects opened first have loaded; a server gone meanwhile opens
    /// nothing more.</summary>
    private async Task OpenRestAsync(Func<Task> open)
    {
        try
        {
            await firstLoaded.Task.WaitAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Never said: the rest are opened anyway, the server taking them in turn.
        }
        try
        {
            await open().ConfigureAwait(false);
        }
        catch (LanguageServerFailed)
        {
            // Gone: a server started again opens everything afresh.
        }
    }

    /// <summary>Whether the server has Passenger's project loader (the extension composed with it), which answers so.</summary>
    private static async Task<bool> HasLoaderAsync(LanguageServer server, CancellationToken cancel)
    {
        try
        {
            return await server.RequestAsync<bool>(ProjectMethods.Ready, new { }, cancel).ConfigureAwait(false);
        }
        catch (LanguageServerFailed)
        {
            return false;
        }
    }

    /// <summary>Whether the server loads its projects from the client's design-time builds (<see cref="IProjectFeed"/>)
    /// rather than building them itself.</summary>
    public bool Fed { get; private set; }

    /// <summary>Has a server fed by the client load projects again, from the design-time builds it asks for: their files
    /// or what they import changed, which the server watches only in part.</summary>
    public void Reload(IReadOnlyList<string> projects)
    {
        if (Fed && projects.Count > 0)
        {
            Server.Notify(ProjectMethods.Load, new LoadProjectsParams([.. projects.Select(p => new ProjectToOpen(p, null))], Solution));
        }
    }

    /// <summary>Starts following a document open in an editor: the server is sent its text, then every
    /// change to it, in order.</summary>
    public LspDocument Open(string path, TextDocument text)
    {
        string uri = DocumentUris.FromPath(path);
        if (documents.TryGetValue(uri, out LspDocument? open))
        {
            return open;
        }
        LspDocument document = new(this, uri, text);
        documents[uri] = document;
        return document;
    }

    internal void Forget(LspDocument document) => documents.Remove(document.Uri);

    /// <summary>A message to Passenger's extension in the server about the whole solution, and its answer
    /// (<see cref="RoslynExtension"/>).</summary>
    public Task<T> ExtensionAsync<T>(string handler, object message, CancellationToken cancel = default) =>
        RoslynExtension.AskWorkspaceAsync<T>(Server, handler, message, cancel);

    /// <summary>The texts the server holds for files of the solution, by their fingerprints (null for one it has no
    /// document of), which an edit placed by its lines and columns needs the disk to match.</summary>
    public async Task<IReadOnlyList<Roslyn.Extension.TextHeld>> HeldTextsAsync(IReadOnlyList<string> paths, CancellationToken cancel = default) =>
        (await ExtensionAsync<Roslyn.Extension.TextsHeld>(Roslyn.Extension.Handlers.DocumentTexts, new Roslyn.Extension.TextsAsked(paths), cancel).ConfigureAwait(false)).Texts;

    /// <summary>A message to Passenger's extension about the solution that nothing waits on, as a debugging session ends.</summary>
    public void TellExtension(string handler, object message) => RoslynExtension.TellWorkspace(Server, handler, message);

    /// <summary>Symbols across the workspace whose names match a query (Roslyn matches by pattern: initials and camel
    /// humps as well as substrings).</summary>
    public async Task<IReadOnlyList<SymbolInformation>> SymbolsAsync(string query, CancellationToken cancel = default) =>
        await Server.RequestAsync<List<SymbolInformation>>(LspMethods.WorkspaceSymbol, new WorkspaceSymbolParams(query), cancel) ?? [];

    /// <summary>The solution's own symbols a name names exactly (Passenger's extension's <see cref="Roslyn.Extension.SymbolsAsked"/>),
    /// at most <paramref name="limit"/>.</summary>
    public Task<Roslyn.Extension.SymbolsFound> SymbolsNamedAsync(string name, int limit, CancellationToken cancel = default) =>
        ExtensionAsync<Roslyn.Extension.SymbolsFound>(Roslyn.Extension.Handlers.SymbolsNamed, new Roslyn.Extension.SymbolsAsked(name, limit), cancel);

    /// <summary>Closes the documents, on the caller's thread as they are its, then ends the server without holding it.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (LspDocument document in documents.Values.ToList())
        {
            document.Dispose();
        }
        await Server.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>What keeps a server's open documents: Roslyn's session, or another server's (<see cref="LanguageSession"/>).</summary>
internal interface IDocumentHost
{
    LanguageServer Server { get; }

    void Forget(LspDocument document);

    /// <summary>The server has first given a document its colours.</summary>
    void Coloured();

    /// <summary>Whether the server's diagnostics are asked for (<c>textDocument/diagnostic</c>), rather than sent as it
    /// finds them (<c>textDocument/publishDiagnostics</c>).</summary>
    bool PullsDiagnostics { get; }

    /// <summary>How the diagnostics it pushes naming no version follow the texts it is sent.</summary>
    PushPace Pushes { get; }
}

/// <summary>One document as the server knows it: opened with its text, then sent every change as it
/// happens (an LSP incremental sync), closed when the editor lets go. Each request is made against
/// the document as it is now, and reports which version it answered for, so a late answer can be
/// recognised and dropped.</summary>
public sealed class LspDocument : IDisposable
{
    private readonly IDocumentHost session;
    private readonly Lock gate = new();
    private bool closed;

    /// <summary>The diagnostics a server that pushes them last sent, with the version they answer (told as they came; null
    /// before any came, or for a push that answers the text whenever it is read).</summary>
    private (IReadOnlyList<Diagnostic> Diagnostics, int? Version) pushed = ([], null);

    /// <summary>The version of the text last sent (opened, or changed) and when it was sent; and, for a server that pushes
    /// once for each text that changed, the versions it has yet to answer, in order.</summary>
    private int sent;
    private long sentAt;
    private readonly Queue<int> unanswered = new();

    /// <summary>The version a push answers that answers no text the editor has had: one before a debouncing server's rest.</summary>
    private const int NoVersion = -1;

    /// <summary>When the latest colours and diagnostics the server gave were asked for and when they came.</summary>
    private (long Asked, long Came) coloured;
    private (long Asked, long Came) diagnosed;

    internal LspDocument(IDocumentHost session, string uri, TextDocument text, string? languageId = null)
    {
        this.session = session;
        Uri = uri;
        Text = text;
        IsRazor = languageId is null && IsRazorPath(uri);
        LanguageId = languageId ?? (IsRazor ? "aspnetcorerazor" : "csharp");
        Sent(text.Version, changed: true);
        session.Server.Notify(LspMethods.DidOpen,
            new DidOpenTextDocumentParams(new TextDocumentItem(uri, LanguageId, text.Version, text.Snapshot.ToString())));
        text.Changed += OnChanged;
    }

    /// <summary>A text sent, before it goes, so a push that answers it, however soon it comes, is told so.</summary>
    private void Sent(int version, bool changed)
    {
        lock (gate)
        {
            sent = version;
            sentAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (changed && session.Pushes.EachText)
            {
                unanswered.Enqueue(version);
            }
        }
    }

    /// <summary>The version a push naming none answers, as it arrives (under the gate): the next a server that pushes once
    /// a text has yet to answer (the latest sent for the last, which changes since left as it was); for a debouncing one
    /// the latest sent, once it has rested, else none, but null (the text whenever it is read) for a clean push from one
    /// that pushes nothing for a clean text after it, so it is never kept from clearing what the text before showed; else
    /// the latest sent.</summary>
    private int? Answered(bool clean)
    {
        PushPace pace = session.Pushes;
        if (pace.EachText)
        {
            return unanswered.TryDequeue(out int version) && unanswered.Count > 0 ? version : sent;
        }
        if (clean && pace.QuietWhenClean)
        {
            return null;
        }
        return System.Diagnostics.Stopwatch.GetElapsedTime(sentAt) >= pace.Rest ? sent : NoVersion;
    }

    public string Uri { get; }
    public TextDocument Text { get; }

    /// <summary>The language the server was told the document is in: <c>csharp</c>, <c>aspnetcorerazor</c>, or a web
    /// language's (<c>typescript</c>, <c>javascriptreact</c>, <c>html</c>, <c>scss</c>, <c>jsonc</c>).</summary>
    public string LanguageId { get; }

    /// <summary>The server sent the document's diagnostics, as one that pushes them does; raised on any thread, the list
    /// then in <see cref="DiagnosticsAsync"/>.</summary>
    public event EventHandler? DiagnosticsPushed;

    /// <summary>The diagnostics the server pushed, kept for the version they answer: the one it named, else the one its
    /// <see cref="PushPace"/> says, told as they arrive (a version read later, on another thread, may be a text sent since).</summary>
    internal void Publish(PublishDiagnosticsParams published)
    {
        lock (gate)
        {
            pushed = (published.Diagnostics, published.Version ?? Answered(published.Diagnostics.Count == 0));
        }
        DiagnosticsPushed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The diagnostics the server last pushed and the version they answer (none for the text now before any came).</summary>
    public (IReadOnlyList<Diagnostic> Diagnostics, int Version) Pushed
    {
        get
        {
            lock (gate)
            {
                return (pushed.Diagnostics, pushed.Version ?? Text.Version);
            }
        }
    }

    /// <summary>When the latest colours the server gave the document (the whole file's or a range's) were asked for and
    /// when they came, as Stopwatch timestamps (zeros before any came), an answer to an earlier question never taking
    /// the place of a later one's.</summary>
    public (long Asked, long Came) Coloured
    {
        get
        {
            lock (gate)
            {
                return coloured;
            }
        }
    }

    /// <summary>When the latest diagnostics pulled for the document were asked for and when they came, as
    /// <see cref="Coloured"/> are.</summary>
    public (long Asked, long Came) Diagnosed
    {
        get
        {
            lock (gate)
            {
                return diagnosed;
            }
        }
    }

    /// <summary>An answer come to a question asked at <paramref name="asked"/>, kept unless a later question's came
    /// first.</summary>
    private void Came(ref (long Asked, long Came) latest, long asked)
    {
        long came = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (gate)
        {
            if (asked >= latest.Asked)
            {
                latest = (asked, came);
            }
        }
    }

    /// <summary>The colours the document writes (CSS's), each with its range, as the server finds them.</summary>
    public async Task<(IReadOnlyList<ColorInformation> Colours, int Version)> ColoursAsync(CancellationToken cancel = default)
    {
        int version = Text.Version;
        return (await Server.RequestAsync<List<ColorInformation>>(LspMethods.DocumentColor, new DocumentColorParams(Identifier), cancel) ?? [], version);
    }

    /// <summary>The project the server answers for the document in, chosen from its <see cref="ProjectContextsAsync"/>;
    /// null for the one the server chooses.</summary>
    public ProjectContext? Context { get; set; }

    /// <summary>The document as each request names it, in its <see cref="Context"/>.</summary>
    public TextDocumentIdentifier Identifier => new(Uri, Context);

    private LanguageServer Server => session.Server;

    /// <summary>A Razor file (.razor, .cshtml), which the server hands to Razor, cohosted in it: its colours have Razor's
    /// legend (<see cref="LanguageServer.RazorLegend"/>) and its diagnostics are Razor's one source.</summary>
    public bool IsRazor { get; }

    /// <summary>Whether a file (its path or its URI) is one Razor answers for in the server: a component or a page.</summary>
    public static bool IsRazorPath(string path) =>
        path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

    public void Saved() => Server.Notify(LspMethods.DidSave, new DidSaveTextDocumentParams(new TextDocumentIdentifier(Uri)));

    public void Dispose()
    {
        if (closed)
        {
            return;
        }
        closed = true;
        Text.Changed -= OnChanged;
        session.Forget(this);
        if (!Server.HasExited)
        {
            Server.Notify(LspMethods.DidClose, new DidCloseTextDocumentParams(new TextDocumentIdentifier(Uri)));
            if (IsRazor)
            {
                // Razor's own word that the file closed, so it lets go of its HTML too.
                Server.Tell(LspMethods.RazorDocumentClosed, new TextDocumentIdentifier(Uri));
            }
        }
        if (IsRazor)
        {
            Server.ForgetRazorHtml(DocumentUris.ToPath(Uri));
        }
    }

    /// <summary>Completion at an offset: invoked, after a trigger character, or asked again as more is typed into a list
    /// the server said was incomplete.</summary>
    public async Task<(CompletionList? List, int Version)> CompletionAsync(int offset, string? trigger, CancellationToken cancel = default, bool incomplete = false)
    {
        int version = Text.Version;
        CompletionContext context = incomplete ? new CompletionContext(CompletionTriggerKind.TriggerForIncompleteCompletions)
            : trigger is null ? new CompletionContext(CompletionTriggerKind.Invoked) : new CompletionContext(CompletionTriggerKind.TriggerCharacter, trigger);
        // Sent now, after the changes before it; read off the caller's thread: a list may hold every type in reach.
        JsonElement result = await Read(Server.RequestAsync<JsonElement>(LspMethods.Completion,
            new CompletionParams(Identifier, PositionOf(offset), context), cancel));
        return (Shapes.CompletionList(result), version);
    }

    public Task<CompletionItem?> ResolveAsync(CompletionItem item, CancellationToken cancel = default) =>
        Server.RequestAsync<CompletionItem>(LspMethods.CompletionResolve, item, cancel);

    public async Task<Hover?> HoverAsync(int offset, CancellationToken cancel = default) =>
        await Server.RequestAsync<Hover>(LspMethods.Hover, new TextDocumentPositionParams(Identifier, PositionOf(offset)), cancel);

    public async Task<SignatureHelp?> SignatureHelpAsync(int offset, CancellationToken cancel = default) =>
        await Server.RequestAsync<SignatureHelp>(LspMethods.SignatureHelp, new TextDocumentPositionParams(Identifier, PositionOf(offset)), cancel);

    /// <summary>Where the symbol at an offset is declared. Like every answer that grows with the solution or the file, read
    /// on the thread pool before the caller resumes (<see cref="Read"/>).</summary>
    public async Task<IReadOnlyList<LspLocation>> DefinitionAsync(int offset, CancellationToken cancel = default) =>
        Shapes.Locations(await Read(Server.RequestAsync<JsonElement>(LspMethods.Definition, new TextDocumentPositionParams(Identifier, PositionOf(offset)), cancel)));

    /// <summary>Where the type of the symbol at an offset (a variable's, a member's, a parameter's) is declared.</summary>
    public async Task<IReadOnlyList<LspLocation>> TypeDefinitionAsync(int offset, CancellationToken cancel = default) =>
        Shapes.Locations(await Read(Server.RequestAsync<JsonElement>(LspMethods.TypeDefinition, new TextDocumentPositionParams(Identifier, PositionOf(offset)), cancel)));

    /// <summary>The implementations of the symbol at an offset: an interface's in a solution may be thousands.</summary>
    public async Task<IReadOnlyList<LspLocation>> ImplementationAsync(int offset, CancellationToken cancel = default) =>
        Shapes.Locations(await Read(Server.RequestAsync<JsonElement>(LspMethods.Implementation, new TextDocumentPositionParams(Identifier, PositionOf(offset)), cancel)));

    /// <summary>Every place the symbol at an offset is used, its declaration included, each once: Roslyn names a place
    /// once for each symbol declared there (a type and its primary constructor, a record's positional property and its
    /// parameter), so such a declaration came twice. Told apart by the server's own URI text, never a path made
    /// canonical, which asks the disk once a place.</summary>
    public async Task<IReadOnlyList<LspLocation>> ReferencesAsync(int offset, CancellationToken cancel = default) =>
        [.. Shapes.Locations(await Read(Server.RequestAsync<JsonElement>(LspMethods.References,
                new ReferenceParams(Identifier, PositionOf(offset), new ReferenceContext(true)), cancel)))
            .DistinctBy(place => (place.Uri, place.Range.Start))];

    /// <summary>A request's answer awaited so that what follows the await, reading it into records, runs on the thread pool
    /// however it came (in already, or where it arrived), never on the caller's thread: the UI thread gets the records made,
    /// as completion's list is (thousands of usages, a file's hints, a project's code actions).</summary>
    private static ConfiguredTaskAwaitable<T?> Read<T>(Task<T?> answer) => answer.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

    public async Task<IReadOnlyList<DocumentHighlight>> HighlightsAsync(int offset, CancellationToken cancel = default) =>
        await Server.RequestAsync<List<DocumentHighlight>>(LspMethods.DocumentHighlight, new TextDocumentPositionParams(Identifier, PositionOf(offset)), cancel) ?? [];

    public async Task<JsonElement?> RenameAsync(int offset, string newName, CancellationToken cancel = default)
    {
        JsonElement result = await Server.RequestAsync<JsonElement>(LspMethods.Rename, new RenameParams(Identifier, PositionOf(offset), newName), cancel);
        return result.ValueKind == JsonValueKind.Object ? result : null;
    }

    /// <summary>The actions for a range, asked without the whitespace at either end of it: lines selected with Shift+Down
    /// from a statement's first word end in the next line's indentation, which Roslyn's Extract Method replaced with the
    /// call, joining the statement after it onto the call's line.</summary>
    public async Task<IReadOnlyList<CodeAction>> CodeActionsAsync(TextRange range, IReadOnlyList<Diagnostic> diagnostics, CancellationToken cancel = default)
    {
        JsonElement result = await Read(Server.RequestAsync<JsonElement>(LspMethods.CodeAction,
            new CodeActionParams(Identifier, RangeOf(Trimmed(Text.Snapshot, range)), new CodeActionContext(diagnostics, TriggerKind: CodeActionTriggerKind.Invoked)), cancel));
        return Shapes.CodeActions(result);
    }

    /// <summary>A range without the spaces, tabs and line breaks at its ends; one of nothing else is left as it is.</summary>
    private static TextRange Trimmed(TextSnapshot text, TextRange range)
    {
        int start = range.Start;
        int end = range.End;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }
        return start == end ? range : TextRange.FromBounds(start, end);
    }

    /// <summary>The actions of one kind the server has for the whole document (ESLint's <c>source.fixAll.eslint</c>).</summary>
    public async Task<IReadOnlyList<CodeAction>> SourceActionsAsync(string kind, CancellationToken cancel = default)
    {
        LspRange whole = new(new LspPosition(0, 0), PositionOf(Text.Snapshot.Length));
        JsonElement result = await Read(Server.RequestAsync<JsonElement>(LspMethods.CodeAction,
            new CodeActionParams(Identifier, whole, new CodeActionContext([], [kind], TriggerKind: CodeActionTriggerKind.Automatic)), cancel));
        return Shapes.CodeActions(result);
    }

    public Task<CodeAction?> ResolveAsync(CodeAction action, CancellationToken cancel = default) =>
        Server.RequestAsync<CodeAction>(LspMethods.CodeActionResolve, action, cancel);

    /// <summary>A chosen code action with its edit: as it came when it had one, resolved when the server left it for later,
    /// and a fix-all's resolved for the scope chosen (Roslyn's codeAction/resolveFixAll).</summary>
    public async Task<CodeAction> ResolveChosenAsync(CodeAction action, string? scope, CancellationToken cancel = default)
    {
        if (scope is not null)
        {
            return await Server.RequestAsync<CodeAction>(LspMethods.CodeActionResolveFixAll, ClientCommands.FixAll(action, scope), cancel) ?? action;
        }
        return action.Edit is null && action.Data is not null ? await ResolveAsync(action, cancel) ?? action : action;
    }

    /// <summary>A message to Passenger's extension in the server about this document, and its answer
    /// (<see cref="RoslynExtension"/>).</summary>
    public Task<T> ExtensionAsync<T>(string handler, object message, CancellationToken cancel = default) =>
        RoslynExtension.AskAsync<T>(Server, handler, message, Identifier, cancel);

    /// <summary>The texts the document's server holds for files of its solution, by their fingerprints (null for one it
    /// has no document of), which an edit placed by its lines and columns needs the disk to match.</summary>
    public async Task<IReadOnlyList<Roslyn.Extension.TextHeld>> HeldTextsAsync(IReadOnlyList<string> paths, CancellationToken cancel = default) =>
        (await RoslynExtension.AskWorkspaceAsync<Roslyn.Extension.TextsHeld>(Server, Roslyn.Extension.Handlers.DocumentTexts, new Roslyn.Extension.TextsAsked(paths), cancel).ConfigureAwait(false)).Texts;

    /// <summary>The symbol at an offset, the one a name there refers to or a declaration there declares, with its
    /// documentation id; none where there is no symbol.</summary>
    public async Task<Roslyn.Extension.NamedSymbol?> SymbolAtAsync(int offset, CancellationToken cancel = default) =>
        (await ExtensionAsync<Roslyn.Extension.SymbolsFound>(Roslyn.Extension.Handlers.SymbolAt, new Roslyn.Extension.Caret(PlaceOf(offset)), cancel).ConfigureAwait(false)).Symbols.FirstOrDefault();

    /// <summary>An offset as the extension's messages give a place.</summary>
    public Roslyn.Extension.Place PlaceOf(int offset)
    {
        LspPosition position = PositionOf(offset);
        return new Roslyn.Extension.Place(position.Line, position.Character);
    }

    /// <summary>The projects the server compiles the document in, a target framework of a multi-targeted project each,
    /// and the one it answers for; null when it has the document in none.</summary>
    public Task<ProjectContextList?> ProjectContextsAsync(CancellationToken cancel = default) =>
        Server.RequestAsync<ProjectContextList>(LspMethods.GetProjectContexts,
            new GetProjectContextsParams(new TextDocumentItem(Uri, LanguageId, Text.Version, string.Empty)), cancel);

    /// <summary>The document's outline: its types, and their members under them. A server that answers with a flat list
    /// (the Dockerfile server's instructions, as <see cref="SymbolInformation"/>) gives each as a symbol with no children.</summary>
    public async Task<IReadOnlyList<DocumentSymbol>> OutlineAsync(CancellationToken cancel = default)
    {
        JsonElement found = await Read(Server.RequestAsync<JsonElement>(LspMethods.DocumentSymbol, new DocumentSymbolParams(Identifier), cancel));
        if (found.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return [.. found.EnumerateArray().Select(symbol => symbol.TryGetProperty("location", out _)
            ? symbol.Deserialize<SymbolInformation>(Json.Options) is { } flat ? new DocumentSymbol(flat.Name, null, flat.Kind, flat.Location.Range, flat.Location.Range) : null
            : symbol.Deserialize<DocumentSymbol>(Json.Options)).OfType<DocumentSymbol>()];
    }

    public async Task<IReadOnlyList<LspTextEdit>> FormatAsync(IndentOptions indent, CancellationToken cancel = default) =>
        await Server.RequestAsync<List<LspTextEdit>>(LspMethods.Formatting,
            new DocumentFormattingParams(Identifier, new FormattingOptions(indent.Size, !indent.UseTabs)), cancel) ?? [];

    /// <summary>The whole document's tokens; <paramref name="sent"/>, when given, is told where the request reached the
    /// server among its words that its colours moved.</summary>
    public async Task<(SemanticTokens? Tokens, int Version)> SemanticTokensAsync(CancellationToken cancel = default, SentRequest? sent = null)
    {
        int version = Text.Version;
        long asked = System.Diagnostics.Stopwatch.GetTimestamp();
        SemanticTokensParams parameters = new(Identifier);
        if (sent is not null)
        {
            Server.Follow(parameters, sent);
        }
        SemanticTokens? tokens = await Server.RequestAsync<SemanticTokens>(LspMethods.SemanticTokensFull, parameters, cancel);
        Came(ref coloured, asked);
        if (tokens is { Data.Length: > 0 })
        {
            session.Coloured();
        }
        return (tokens, version);
    }

    /// <summary>The tokens of a range only, encoded from the document's start as the whole file's are: Roslyn colours a
    /// screen of lines in milliseconds, where a long file takes it about a second.</summary>
    public async Task<(SemanticTokens? Tokens, int Version)> SemanticTokensAsync(TextRange range, CancellationToken cancel = default, SentRequest? sent = null)
    {
        int version = Text.Version;
        long asked = System.Diagnostics.Stopwatch.GetTimestamp();
        SemanticTokensRangeParams parameters = new(Identifier, RangeOf(range));
        if (sent is not null)
        {
            Server.Follow(parameters, sent);
        }
        SemanticTokens? tokens = await Server.RequestAsync<SemanticTokens>(LspMethods.SemanticTokensRange, parameters, cancel);
        Came(ref coloured, asked);
        return (tokens, version);
    }

    /// <summary>The symbol at an offset as a call hierarchy's root, if it is one that calls or is called.</summary>
    public async Task<HierarchyItem?> PrepareCallHierarchyAsync(int offset, CancellationToken cancel = default) =>
        (await Server.RequestAsync<List<HierarchyItem>>(LspMethods.PrepareCallHierarchy, new TextDocumentPositionParams(Identifier, PositionOf(offset)), cancel))?.FirstOrDefault();

    /// <summary>The type at an offset as a type hierarchy's root.</summary>
    public async Task<HierarchyItem?> PrepareTypeHierarchyAsync(int offset, CancellationToken cancel = default) =>
        (await Server.RequestAsync<List<HierarchyItem>>(LspMethods.PrepareTypeHierarchy, new TextDocumentPositionParams(Identifier, PositionOf(offset)), cancel))?.FirstOrDefault();

    /// <summary>The words the server would show inside the document's lines (parameter names before their
    /// arguments), and the version they are for.</summary>
    public async Task<(IReadOnlyList<LspInlayHint> Hints, int Version)> InlayHintsAsync(CancellationToken cancel = default)
    {
        int version = Text.Version;
        LspRange whole = new(new LspPosition(0, 0), PositionOf(Text.Snapshot.Length));
        JsonElement result = await Read(Server.RequestAsync<JsonElement>(LspMethods.InlayHint, new InlayHintParams(Identifier, whole), cancel));
        return (Shapes.InlayHints(result), version);
    }

    /// <summary>What the server says of a hint it gave (a type's description, a parameter's), as markdown; empty when it
    /// says nothing.</summary>
    public async Task<string> InlayHintTooltipAsync(LspInlayHint hint, CancellationToken cancel = default)
    {
        JsonElement result = await Read(Server.RequestAsync<JsonElement>(LspMethods.InlayHintResolve,
            new { position = hint.Position, label = hint.Label, kind = hint.Kind, data = hint.Data }, cancel));
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("tooltip", out JsonElement tooltip) ? Shapes.Text(tooltip) : string.Empty;
    }

    /// <summary>Every diagnostic the server has for the document, pulled from each source it
    /// registered (a Razor file's from Razor's one, with its HTML's from the HTML server), and the version they are for;
    /// those it last pushed, from a server that pushes them.</summary>
    public async Task<(IReadOnlyList<Diagnostic> Diagnostics, int Version)> DiagnosticsAsync(CancellationToken cancel = default)
    {
        if (!session.PullsDiagnostics)
        {
            return Pushed;
        }
        int version = Text.Version;
        TextSnapshot text = Text.Snapshot;
        TextDocumentIdentifier identifier = Identifier;
        long asked = System.Diagnostics.Stopwatch.GetTimestamp();
        List<Diagnostic> all = [];
        IReadOnlyList<string?> sources = IsRazor || session is not RoslynSession ? [null] : Server.DiagnosticSources;
        // Gathered on the thread pool, so a file's thousands of warnings reach the caller as one list.
        foreach (string? source in sources)
        {
            DocumentDiagnosticReport? report = await Read(Server.RequestAsync<DocumentDiagnosticReport>(LspMethods.DocumentDiagnostic,
                new DocumentDiagnosticParams(identifier, source), cancel));
            all.AddRange(report?.Items ?? []);
        }
        if (IsRazor && Server.HtmlServer is not null)
        {
            // Razor makes the file's HTML as a question needs it: a fold's, the cheapest, has the HTML server's copy current.
            await Read(Server.RequestAsync<JsonElement>(LspMethods.FoldingRange, new FoldingRangeParams(new TextDocumentIdentifier(Uri)), cancel));
            all.AddRange(await Server.RazorHtmlDiagnosticsAsync(DocumentUris.ToPath(Uri), text.ToString(), cancel).ConfigureAwait(false));
        }
        List<Diagnostic> distinct = all
            .DistinctBy(d => (d.Range, d.Message, d.Code?.ToString()))
            .ToList();
        Came(ref diagnosed, asked);
        return (distinct, version);
    }

    /// <summary>An offset as an LSP position: the server's line (<see cref="ServerLines"/>), and a column in UTF-16
    /// units, which is what the editor's columns already are.</summary>
    public LspPosition PositionOf(int offset) => ServerLines.Of(Text.Snapshot).PositionOf(offset);

    public LspRange RangeOf(TextRange range) => ServerLines.Of(Text.Snapshot).RangeOf(range);

    public int OffsetOf(LspPosition position) => ServerLines.Of(Text.Snapshot).OffsetOf(position);

    /// <summary>The edits sent last to first, the order an incremental sync is read in, so each change's place in the text
    /// before the edit is its place in the text the changes sent before it left. Edits that touch go as one change: the
    /// later one can decide whether a '\r' where they meet ends a line. A change that begins or ends between the halves of
    /// "\r\n" takes in the half beside it and puts it back (<see cref="Unsplit"/>). The server's lines are carried to the
    /// new text here, so the next change, and every request until then, finds them without reading the file.</summary>
    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        ServerLines before = ServerLines.Of(e.Before);
        ServerLines.After(e);
        List<TextDocumentContentChangeEvent> changes = [];
        for (int last = e.Edits.Count - 1; last >= 0;)
        {
            int first = last;
            while (first > 0 && e.Edits[first - 1].End == e.Edits[first].Start)
            {
                first--;
            }
            string text = first == last ? e.Edits[last].Text : string.Concat(e.Edits.Skip(first).Take(last - first + 1).Select(edit => edit.Text));
            (TextRange range, text) = Unsplit(e.Before, TextRange.FromBounds(e.Edits[first].Start, e.Edits[last].End), text);
            changes.Add(new TextDocumentContentChangeEvent(before.RangeOf(range), text));
            last = first - 1;
        }
        Sent(e.Version, changed: !session.Pushes.EachText || Changes(e));
        Server.Notify(LspMethods.DidChange, new DidChangeTextDocumentParams(new VersionedTextDocumentIdentifier(Uri, e.Version), changes));
    }

    /// <summary>Whether a change leaves the text otherwise than it was, as a server pushing once a text pushes only then.</summary>
    private static bool Changes(DocumentChangedEventArgs e)
    {
        foreach (TextEdit edit in e.Edits)
        {
            if (edit.Text.Length != edit.End - edit.Start || e.Before.GetText(edit.Start, edit.End - edit.Start) != edit.Text)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A change as the server can be sent it. Roslyn reads a column past a line's end as the end, before its break,
    /// as the LSP says to, so no position is between a "\r\n"'s halves: a change made there would land before the '\r',
    /// and the server's lines would differ from the editor's from then on, until a change sent past its last line ended
    /// it. Such an end moves out past the half beside it, which the change's text puts back; no neighbouring change
    /// overlaps it then, since one that touched it was sent as part of it.</summary>
    private static (TextRange Range, string Text) Unsplit(TextSnapshot before, TextRange range, string text)
    {
        int start = range.Start;
        int end = range.End;
        if (Splits(before, start))
        {
            start--;
            text = "\r" + text;
        }
        if (Splits(before, end))
        {
            end++;
            text += "\n";
        }
        return (TextRange.FromBounds(start, end), text);
    }

    /// <summary>Whether an offset is between the '\r' and the '\n' of a "\r\n".</summary>
    private static bool Splits(TextSnapshot text, int offset) => offset > 0 && offset < text.Length && text[offset - 1] == '\r' && text[offset] == '\n';
}
