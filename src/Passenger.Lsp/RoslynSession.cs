using System.Runtime.CompilerServices;
using System.Text.Json;
using Passenger.Core;
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
        Projects = projects;
        server.ProjectsLoaded += (_, _) =>
        {
            // The server says so after each open it was sent: the projects opened first, the rest, and each reload meanwhile.
            lock (counting)
            {
                if (++completed < opens)
                {
                    firstLoaded.TrySetResult();
                    return;
                }
                IsLoaded = true;
            }
            Loaded?.Invoke(this, EventArgs.Empty);
        };
        // A server that ended by itself opens nothing more (OpenRestAsync).
        server.Exited += (_, _) => ending.Cancel();
        // Builds given as what they read changed (a restore landing) load again: the server never reloads for that itself.
        server.ProjectGiven += (_, built) =>
        {
            if (built.Stale)
            {
                Reload([built.Project]);
            }
        };
    }

    /// <summary>How many opens the server was sent that its load waits for (a reload sent while it loads among them, as the
    /// server's word that one ended names none), and how many it has said it finished.</summary>
    private int opens = 1;
    private int completed;
    private readonly Lock counting = new();

    /// <summary>The start's opens: the projects opened first and the rest.</summary>
    private void Expect(int starting)
    {
        lock (counting)
        {
            opens = starting;
        }
    }

    /// <summary>Done once the projects opened first have loaded, after which the rest are opened.</summary>
    private readonly TaskCompletionSource firstLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Cancelled as the session is disposed or its server ends by itself, which ends the wait for the projects
    /// opened first: its clock would keep the session, its server and what feeds the server (a window's) until it ran out.</summary>
    private readonly CancellationTokenSource ending = new();

    /// <summary>The projects have loaded, so answers now know the whole solution. Raised on any
    /// thread.</summary>
    public event EventHandler? Loaded;

    public LanguageServer Server { get; }
    public string Folder { get; }
    public string? Solution { get; }

    /// <summary>The projects it was started on: those it opened with no solution to open, else those the solution listed
    /// then (with the programs beside it).</summary>
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
    /// names loaded alone before the rest. <paramref name="started"/> is given the server before it opens anything; with
    /// <paramref name="feed"/>, a server that has Passenger's project loader loads the projects from their design-time
    /// builds, asked for as it starts so they are built meanwhile. Cancelled or failed, it ends the server it started, so
    /// one given up (another folder opened) never goes on loading this folder unseen.</summary>
    public static async Task<RoslynSession> StartAsync(string folder, string? solution, IReadOnlyList<string> projects, string logFolder,
        CancellationToken cancel = default, IReadOnlyDictionary<string, object>? options = null, Action<LanguageServer>? started = null,
        Func<Task<IReadOnlyList<string>>>? first = null, IProjectFeed? feed = null, string? stateCache = null)
    {
        Task<FedProjects?>? feeding = feed?.ProjectsAsync(cancel);
        LanguageServer server;
        try
        {
            server = await LanguageServer.StartRoslynAsync(folder, logFolder, cancel, options, stateCache).ConfigureAwait(false);
        }
        catch
        {
            Observe(feeding);
            throw;
        }
        RoslynSession session = new(server, folder, solution, projects);
        try
        {
            cancel.ThrowIfCancellationRequested();
            started?.Invoke(server);
            if (feeding is not null && !await HasLoaderAsync(server, cancel).ConfigureAwait(false))
            {
                // The server builds its projects itself; the builds asked for are kept for the folder's next opening.
                Observe(feeding);
                feeding = null;
            }
            // Awaited from here, its fault the start's own.
            Task<FedProjects?>? asked = feeding;
            feeding = null;
            if (asked is not null && await asked.ConfigureAwait(false) is { } fed)
            {
                server.Feed = feed;
                session.Fed = true;
                server.WatchedFiles.HasLoader = true;
                server.WatchedFiles.ReadFrom(fed.Read);
                (IReadOnlyList<ProjectToOpen> before, IReadOnlyList<ProjectToOpen> rest) = (fed.First, fed.Later);
                if (before.Count > 0)
                {
                    session.Expect(2);
                    await LoadAsync(server, ProjectMethods.Load, new LoadProjectsParams(before, solution)).ConfigureAwait(false);
                    Core.UnawaitedWork.Observe(session.OpenRestAsync(() => LoadAsync(server, ProjectMethods.Load, new LoadProjectsParams(rest, solution))));
                    return session;
                }
                await LoadAsync(server, ProjectMethods.Load, new LoadProjectsParams(rest, solution)).ConfigureAwait(false);
                return session;
            }
            IReadOnlyList<string> ahead = first is null ? [] : await first().ConfigureAwait(false);
            if (ahead.Count > 0)
            {
                session.Expect(2);
                await LoadAsync(server, LspMethods.ProjectOpen, new OpenProjectParams([.. ahead.Select(DocumentUris.FromPath)])).ConfigureAwait(false);
                // The rest once those have loaded: opened at once, the server would take them all in one batch.
                Core.UnawaitedWork.Observe(session.OpenRestAsync(() => solution is not null
                    ? LoadAsync(server, LspMethods.SolutionOpen, new OpenSolutionParams(DocumentUris.FromPath(solution)))
                    : LoadAsync(server, LspMethods.ProjectOpen, new OpenProjectParams([.. projects.Select(DocumentUris.FromPath)]))));
                return session;
            }
            if (solution is not null)
            {
                await LoadAsync(server, LspMethods.SolutionOpen, new OpenSolutionParams(DocumentUris.FromPath(solution))).ConfigureAwait(false);
            }
            else
            {
                await LoadAsync(server, LspMethods.ProjectOpen, new OpenProjectParams([.. projects.Select(DocumentUris.FromPath)])).ConfigureAwait(false);
            }
        }
        catch
        {
            Observe(feeding);
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return session;
    }

    /// <summary>Asks the server to load or open projects, its file watches counting the load as under way until it ends.</summary>
    private static Task LoadAsync(LanguageServer server, string method, object parameters)
    {
        server.WatchedFiles.Loading();
        return server.NotifyAsync(method, parameters);
    }

    /// <summary>The design-time builds asked for as a server started that will not load them: a fault in the asking is
    /// said as it faults.</summary>
    private static void Observe(Task? feeding)
    {
        if (feeding is not null)
        {
            Core.UnawaitedWork.Observe(feeding);
        }
    }

    /// <summary>Opens the rest of the solution once the projects opened first have loaded; a server gone meanwhile opens
    /// nothing more.</summary>
    private async Task OpenRestAsync(Func<Task> open)
    {
        try
        {
            await firstLoaded.Task.WaitAsync(TimeSpan.FromMinutes(5), ending.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Never said: the rest are opened anyway, the server taking them in turn.
        }
        catch (OperationCanceledException)
        {
            return;
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
    /// or what they import changed, which the server watches only in part. One sent while the server loads is waited for
    /// too, before <see cref="IsLoaded"/>.</summary>
    public void Reload(IReadOnlyList<string> projects)
    {
        if (Fed && projects.Count > 0)
        {
            lock (counting)
            {
                if (!IsLoaded)
                {
                    opens++;
                }
            }
            Server.WatchedFiles.Loading();
            Server.Notify(ProjectMethods.Load, new LoadProjectsParams([.. projects.Select(p => new ProjectToOpen(p, null))], Solution));
        }
    }

    /// <summary>Has a server fed by the client let go of projects, which it answers from no longer; done once it has, with
    /// whether it held any of them.</summary>
    public async Task<bool> UnloadAsync(IReadOnlyList<string> projects) =>
        Fed && projects.Count > 0 && await Server.RequestAsync<bool>(ProjectMethods.Unload, new UnloadProjectsParams(projects)).ConfigureAwait(false);

    /// <summary>Has a server fed by the client that has loaded its projects load them again, from the design-time builds
    /// it asks for, and waits for that load as for its first: <see cref="IsLoaded"/> until it says it has. For a server
    /// taken up after files came.</summary>
    public void LoadAgain(IReadOnlyList<string> projects)
    {
        if (!Fed || projects.Count == 0)
        {
            return;
        }
        lock (counting)
        {
            IsLoaded = false;
            opens = completed + 1;
        }
        Server.WatchedFiles.Loading();
        Server.Notify(ProjectMethods.Load, new LoadProjectsParams([.. projects.Select(p => new ProjectToOpen(p, null))], Solution));
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

    /// <summary>The solution's documents written on disk at or after a time (UTC), or gone from it since, and those heard
    /// changed that no command has seen the server read since, which it may hold as they were.</summary>
    public Task<WrittenLately> WrittenSinceAsync(DateTime since, CancellationToken cancel = default) => WrittenLately.AskAsync(Server, since, cancel);

    /// <summary>Has the server read now the assemblies its projects reference that a build wrote; done once it holds them,
    /// nothing pulled before then standing (<see cref="LanguageServer.Changes"/>), however few it read now: hearing of a file
    /// as the build went may have had it read one first, and the word of a file written may come after this.</summary>
    public async Task<Roslyn.Extension.ReferencesTaken> ReadReferencesAsync(CancellationToken cancel = default)
    {
        try
        {
            return await ExtensionAsync<Roslyn.Extension.ReferencesTaken>(Roslyn.Extension.Handlers.ReferencesRead, new Roslyn.Extension.ReferencesAsked(), cancel).ConfigureAwait(false);
        }
        finally
        {
            Server.AnswersMoved();
        }
    }

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

    /// <summary>Places the server named, a source generator's document's each taken to the file its code was written in (a
    /// Razor component's C# to its .razor, as the server takes a reference's), the rest as they were; all as they were when
    /// the server has no extension to ask.</summary>
    public async Task<IReadOnlyList<LspLocation>> WrittenAsync(IReadOnlyList<LspLocation> places, CancellationToken cancel = default)
    {
        int[] generated = [.. places.Index().Where(p => DocumentUris.GeneratedName(p.Item.Uri) is not null).Select(p => p.Index)];
        if (generated.Length == 0 || Server.ExtensionProblem is not null)
        {
            return places;
        }
        Roslyn.Extension.WrittenSpans asked = new([.. generated.Select(i => new Roslyn.Extension.UriSpan(places[i].Uri,
            new Roslyn.Extension.Place(places[i].Range.Start.Line, places[i].Range.Start.Character), new Roslyn.Extension.Place(places[i].Range.End.Line, places[i].Range.End.Character)))]);
        Roslyn.Extension.WrittenSpans written = await ExtensionAsync<Roslyn.Extension.WrittenSpans>(Roslyn.Extension.Handlers.WrittenPlaces, asked, cancel).ConfigureAwait(false);
        LspLocation[] all = [.. places];
        foreach ((int i, Roslyn.Extension.UriSpan span) in generated.Zip(written.Spans))
        {
            all[i] = new LspLocation(span.Uri, new LspRange(new LspPosition(span.Start.Line, span.Start.Character), new LspPosition(span.End.Line, span.End.Character)));
        }
        return all;
    }

    /// <summary>The texts of documents source generators made, with each one's project and generator (Passenger's
    /// extension's <see cref="Roslyn.Extension.GeneratedAsked"/>), in order, null for one no generator makes now; null
    /// when the server has no extension to ask.</summary>
    public async Task<IReadOnlyList<Roslyn.Extension.GeneratedText?>?> GeneratedAsync(IReadOnlyList<Roslyn.Extension.GeneratedSource> documents, CancellationToken cancel = default) =>
        Server.ExtensionProblem is null
            ? (await ExtensionAsync<Roslyn.Extension.GeneratedFound>(Roslyn.Extension.Handlers.GeneratedTexts, new Roslyn.Extension.GeneratedAsked(documents), cancel).ConfigureAwait(false)).Texts
            : null;

    /// <summary>The texts of the documents source generators make under a file name for the projects of a name (Passenger's
    /// extension's <see cref="Roslyn.Extension.GeneratedNameAsked"/>), each with its project and generator; null when the
    /// server has no extension to ask.</summary>
    public async Task<IReadOnlyList<Roslyn.Extension.GeneratedText?>?> GeneratedNamedAsync(string project, string file, CancellationToken cancel = default) =>
        Server.ExtensionProblem is null
            ? (await ExtensionAsync<Roslyn.Extension.GeneratedFound>(Roslyn.Extension.Handlers.GeneratedNamed, new Roslyn.Extension.GeneratedNameAsked(project, file), cancel).ConfigureAwait(false)).Texts
            : null;

    /// <summary>Whether each document the server names is generated code as Roslyn's own check tells it (Passenger's
    /// extension's <see cref="Roslyn.Extension.GeneratedDocumentsAsked"/>), in order; null when the server has no extension
    /// to ask.</summary>
    public async Task<IReadOnlyList<bool>?> GeneratedCodeAsync(IReadOnlyList<string> uris, CancellationToken cancel = default) =>
        Server.ExtensionProblem is null
            ? (await ExtensionAsync<Roslyn.Extension.GeneratedDocumentsFound>(Roslyn.Extension.Handlers.GeneratedDocuments, new Roslyn.Extension.GeneratedDocumentsAsked(uris), cancel).ConfigureAwait(false)).Generated
            : null;

    /// <summary>The class a Razor component's file makes, and where the server takes it (Passenger's extension's
    /// <see cref="Roslyn.Extension.ComponentAsked"/>); null when the server has no extension to ask.</summary>
    public async Task<Roslyn.Extension.ComponentFound?> ComponentAsync(string path, CancellationToken cancel = default) =>
        Server.ExtensionProblem is null
            ? await ExtensionAsync<Roslyn.Extension.ComponentFound>(Roslyn.Extension.Handlers.ComponentClass, new Roslyn.Extension.ComponentAsked(path), cancel).ConfigureAwait(false)
            : null;

    /// <summary>The edit renaming the symbol at a place of a document no editor has open (a source generator's, by its URI);
    /// null where nothing can be renamed.</summary>
    public Task<JsonElement?> RenameAsync(Roslyn.Extension.UriSpan at, string newName, CancellationToken cancel = default) =>
        LspDocument.RenameAsync(Server, new TextDocumentIdentifier(at.Uri), new LspPosition(at.Start.Line, at.Start.Character), newName, cancel);

    /// <summary>Every place the symbol at a place of a document no editor has open is used, its declaration included.</summary>
    public Task<IReadOnlyList<LspLocation>> ReferencesAsync(Roslyn.Extension.UriSpan at, CancellationToken cancel = default) =>
        LspDocument.ReferencesAsync(Server, new TextDocumentIdentifier(at.Uri), new LspPosition(at.Start.Line, at.Start.Character), cancel);

    /// <summary>A hierarchy item's callers, each with where the calls are: of a partial member, the half's that declares it,
    /// which every call to it is bound to.</summary>
    public async Task<IReadOnlyList<IncomingCall>> IncomingCallsAsync(HierarchyItem item, CancellationToken cancel = default) =>
        await Server.IncomingCallsAsync(await HalfAsync(item, body: false, cancel).ConfigureAwait(false), cancel).ConfigureAwait(false);

    /// <summary>What a hierarchy item calls: of a partial member, what the half with its body calls.</summary>
    public async Task<IReadOnlyList<OutgoingCall>> OutgoingCallsAsync(HierarchyItem item, CancellationToken cancel = default) =>
        await Server.OutgoingCallsAsync(await HalfAsync(item, body: true, cancel).ConfigureAwait(false), cancel).ConfigureAwait(false);

    /// <summary>The item of the half of a partial member asked for (Passenger's extension's <see cref="Roslyn.Extension.HalfAsked"/>);
    /// the item itself for any other member, and where the server has no extension to ask.</summary>
    private async Task<HierarchyItem> HalfAsync(HierarchyItem item, bool body, CancellationToken cancel)
    {
        if (Server.ExtensionProblem is not null)
        {
            return item;
        }
        Roslyn.Extension.UriSpan at = new(item.Uri, new Roslyn.Extension.Place(item.SelectionRange.Start.Line, item.SelectionRange.Start.Character),
            new Roslyn.Extension.Place(item.SelectionRange.End.Line, item.SelectionRange.End.Character));
        Roslyn.Extension.HalfFound found = await ExtensionAsync<Roslyn.Extension.HalfFound>(Roslyn.Extension.Handlers.PartialHalf, new Roslyn.Extension.HalfAsked(at, body), cancel).ConfigureAwait(false);
        return found.Half is not { } half ? item
            : (await LspDocument.PrepareCallHierarchyAsync(Server, new TextDocumentIdentifier(half.Uri), new LspPosition(half.Start.Line, half.Start.Character), cancel).ConfigureAwait(false)) ?? item;
    }

    /// <summary>A document the server makes itself (a source generator's, by its URI) to ask about at places in a text showing
    /// it, and never opened there: a generator's document opened in the server would stay as it is, in every compilation,
    /// whatever the code it is made from becomes.</summary>
    public LspDocument Unopened(string uri, TextDocument text) => new(this, uri, text, opened: false);

    /// <summary>Closes the documents, on the caller's thread as they are its, then ends the server without holding it.</summary>
    public async ValueTask DisposeAsync()
    {
        ending.Cancel();
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
/// recognised and dropped. One the server makes itself is only asked about (<see cref="RoslynSession.Unopened"/>).</summary>
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

    /// <summary>A pull of the document's diagnostics, with the text's version and the server's count of changes it was
    /// asked at (<see cref="LastDiagnosticsAsync"/>).</summary>
    private sealed record Pulled(IReadOnlyList<Diagnostic> Items, int Version, long Changes);

    /// <summary>The last pull of each part (<see cref="DiagnosticPull"/>, by its value).</summary>
    private readonly Pulled?[] pulled = new Pulled?[3];

    internal LspDocument(IDocumentHost session, string uri, TextDocument text, [IdentifierOf(typeof(LanguageIds))] string? languageId = null, bool opened = true)
    {
        this.session = session;
        Uri = uri;
        Text = text;
        IsRazor = languageId is null && RazorFiles.Is(uri);
        LanguageId = languageId ?? (IsRazor ? LanguageIds.Razor : LanguageIds.CSharp);
        closed = !opened;
        if (opened)
        {
            Sent(text.Version, changed: true);
            session.Server.Notify(LspMethods.DidOpen,
                new DidOpenTextDocumentParams(new TextDocumentItem(uri, LanguageId, text.Version, text.Snapshot.ToString())));
            text.Changed += OnChanged;
        }
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

    /// <summary>The language the server was told the document is in: C#'s or Razor's (<see cref="LanguageIds"/>), or a web
    /// language's (<c>typescript</c>, <c>javascriptreact</c>, <c>html</c>, <c>scss</c>, <c>jsonc</c>).</summary>
    [IdentifierOf(typeof(LanguageIds))]
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
        CompletionAnswer? answer = await Read(Server.RequestAsync<CompletionAnswer>(LspMethods.Completion,
            new CompletionParams(Identifier, PositionOf(offset), context), cancel));
        return (answer?.List, version);
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

    /// <summary>Where the parts source generators made of the type at an offset declare it (<see cref="Roslyn.Extension.PartsFound"/>);
    /// none for anything else, or from a server with no extension to ask.</summary>
    public async Task<IReadOnlyList<LspLocation>> GeneratedPartsAsync(int offset, CancellationToken cancel = default)
    {
        if (Server.ExtensionProblem is not null)
        {
            return [];
        }
        Roslyn.Extension.PartsFound found = await RoslynExtension.AskWorkspaceAsync<Roslyn.Extension.PartsFound>(Server, Roslyn.Extension.Handlers.GeneratedParts,
            new Roslyn.Extension.PartsAsked(Identifier.Uri, PlaceOf(offset)), cancel).ConfigureAwait(false);
        return [.. found.Parts.Select(p => new LspLocation(p.Uri, new LspRange(new LspPosition(p.Start.Line, p.Start.Character), new LspPosition(p.End.Line, p.End.Character))))];
    }

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
    public Task<IReadOnlyList<LspLocation>> ReferencesAsync(int offset, CancellationToken cancel = default) =>
        ReferencesAsync(Server, Identifier, PositionOf(offset), cancel);

    /// <summary>Every place the symbol at a place of a document is used (<see cref="ReferencesAsync(int, CancellationToken)"/>),
    /// a partial member's two halves among them, of which the server names only one as its declaration.</summary>
    internal static async Task<IReadOnlyList<LspLocation>> ReferencesAsync(LanguageServer server, TextDocumentIdentifier document, LspPosition at, CancellationToken cancel)
    {
        IReadOnlyList<LspLocation> served = Shapes.Locations(await Read(server.RequestAsync<JsonElement>(LspMethods.References,
            new ReferenceParams(document, at, new ReferenceContext(true)), cancel)));
        return [.. served.Concat(await HalvesAsync(server, document, at, cancel)).DistinctBy(place => (place.Uri, place.Range.Start))];
    }

    /// <summary>Where both halves of the partial member at a place are declared, the half that declares it and the half with
    /// its body (Passenger's extension's <see cref="Roslyn.Extension.HalfAsked"/>, which names the other half of the one
    /// asked at); none for any other symbol, and where the server has no extension to ask.</summary>
    private static async Task<IReadOnlyList<LspLocation>> HalvesAsync(LanguageServer server, TextDocumentIdentifier document, LspPosition at, CancellationToken cancel)
    {
        if (server.ExtensionProblem is not null)
        {
            return [];
        }
        Roslyn.Extension.Place place = new(at.Line, at.Character);
        Roslyn.Extension.UriSpan asked = new(document.Uri, place, place);
        Roslyn.Extension.UriSpan? declaring = await HalfAsync(server, asked, body: false, cancel).ConfigureAwait(false);
        Roslyn.Extension.UriSpan? body = await HalfAsync(server, asked, body: true, cancel).ConfigureAwait(false);
        declaring ??= body is null ? null : await HalfAsync(server, body, body: false, cancel).ConfigureAwait(false);
        body ??= declaring is null ? null : await HalfAsync(server, declaring, body: true, cancel).ConfigureAwait(false);
        return [.. new[] { declaring, body }.OfType<Roslyn.Extension.UriSpan>()
            .Select(half => new LspLocation(half.Uri, new LspRange(new LspPosition(half.Start.Line, half.Start.Character), new LspPosition(half.End.Line, half.End.Character))))];
    }

    private static async Task<Roslyn.Extension.UriSpan?> HalfAsync(LanguageServer server, Roslyn.Extension.UriSpan at, bool body, CancellationToken cancel) =>
        (await RoslynExtension.AskWorkspaceAsync<Roslyn.Extension.HalfFound>(server, Roslyn.Extension.Handlers.PartialHalf,
            new Roslyn.Extension.HalfAsked(at, body), cancel).ConfigureAwait(false)).Half;

    /// <summary>A request's answer awaited so that what follows the await, reading it into records, runs on the thread pool
    /// however it came (in already, or where it arrived), never on the caller's thread: the UI thread gets the records made,
    /// as completion's list is (thousands of usages, a file's hints, a project's code actions).</summary>
    private static ConfiguredTaskAwaitable<T?> Read<T>(Task<T?> answer) => answer.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

    public async Task<IReadOnlyList<DocumentHighlight>> HighlightsAsync(int offset, CancellationToken cancel = default) =>
        await Server.RequestAsync<List<DocumentHighlight>>(LspMethods.DocumentHighlight, new TextDocumentPositionParams(Identifier, PositionOf(offset)), cancel) ?? [];

    public Task<JsonElement?> RenameAsync(int offset, string newName, CancellationToken cancel = default) =>
        RenameAsync(Server, Identifier, PositionOf(offset), newName, cancel);

    /// <summary>The edit renaming the symbol at a place of a document to a new name; null where nothing can be renamed.</summary>
    internal static async Task<JsonElement?> RenameAsync(LanguageServer server, TextDocumentIdentifier document, LspPosition at, string newName, CancellationToken cancel)
    {
        JsonElement result = await server.RequestAsync<JsonElement>(LspMethods.Rename, new RenameParams(document, at, newName), cancel);
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

    /// <summary>Whether the document's server runs a command itself (<see cref="LanguageServer.Runs"/>).</summary>
    public bool Runs(LspCommand command) => Server.Runs(command.Command);

    /// <summary>Runs a command on the document's server, what it asks of the client meanwhile taken by <paramref name="run"/>
    /// (<see cref="LanguageServer.ExecuteCommandAsync(LspCommand, CommandRun, CancellationToken)"/>).</summary>
    public Task<JsonElement> ExecuteCommandAsync(LspCommand command, CommandRun run, CancellationToken cancel = default) =>
        Server.ExecuteCommandAsync(command, run, cancel);

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

    /// <summary>The documents of the solution written on disk at or after a time (UTC), or gone from it since, and those
    /// heard changed that no command has seen the document's server read since, which it may hold as they were.</summary>
    public Task<WrittenLately> WrittenSinceAsync(DateTime since, CancellationToken cancel = default) => WrittenLately.AskAsync(Server, since, cancel);

    /// <summary>The symbol at an offset, the one a name there refers to or a declaration there declares, with its
    /// documentation id; none where there is no symbol.</summary>
    public async Task<Roslyn.Extension.NamedSymbol?> SymbolAtAsync(int offset, CancellationToken cancel = default) =>
        (await ExtensionAsync<Roslyn.Extension.SymbolsFound>(Roslyn.Extension.Handlers.SymbolAt, new Roslyn.Extension.Caret(PlaceOf(offset)), cancel).ConfigureAwait(false)).Symbols.FirstOrDefault();

    /// <summary>The uses of what the MVVM Toolkit makes of the field or method at an offset, with the names they take as it
    /// is renamed to <paramref name="name"/> (<see cref="Roslyn.Extension.ToolkitFound"/>); none for any other symbol.</summary>
    public Task<Roslyn.Extension.ToolkitFound> ToolkitRenamesAsync(int offset, string name, CancellationToken cancel = default) =>
        ExtensionAsync<Roslyn.Extension.ToolkitFound>(Roslyn.Extension.Handlers.ToolkitRenames, new Roslyn.Extension.ToolkitAsked(PlaceOf(offset), name), cancel);

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
        return [.. found.EnumerateArray().Select(symbol => JsonMembers.Value(symbol, "location") is not null
            ? symbol.Deserialize<SymbolInformation>(Json.Options) is { } flat ? new DocumentSymbol(flat.Name, null, flat.Kind, flat.Location.Range, flat.Location.Range) : null
            : symbol.Deserialize<DocumentSymbol>(Json.Options)).OfType<DocumentSymbol>()];
    }

    /// <summary>The whole document formatted in its indentation, its end and its trailing whitespace as the text and its
    /// .editorconfig ask (<see cref="FormattingOptions.Of"/>).</summary>
    public async Task<IReadOnlyList<LspTextEdit>> FormatAsync(IndentOptions indent, EditorConfig conventions, CancellationToken cancel = default) =>
        await Server.RequestAsync<List<LspTextEdit>>(LspMethods.Formatting,
            new DocumentFormattingParams(Identifier, FormattingOptions.Of(indent, conventions, Text.Snapshot)), cancel) ?? [];

    /// <summary>Whether the server formats a range of a document, not only the whole.</summary>
    public bool FormatsRanges => Server.Provides("documentRangeFormattingProvider");

    /// <summary>A range of the document formatted, the code around it left as it is, by the options the whole document's
    /// formatting takes.</summary>
    public async Task<IReadOnlyList<LspTextEdit>> FormatAsync(TextRange range, IndentOptions indent, EditorConfig conventions, CancellationToken cancel = default) =>
        await Server.RequestAsync<List<LspTextEdit>>(LspMethods.RangeFormatting,
            new DocumentRangeFormattingParams(Identifier, RangeOf(range), FormattingOptions.Of(indent, conventions, Text.Snapshot)), cancel) ?? [];

    /// <summary>What the server writes on its own after a character typed at an offset, the caret past it (Razor's end
    /// tag after a start tag's '>'); null where it writes nothing.</summary>
    public async Task<AutoInserted?> AutoInsertAsync(int offset, string typed, IndentOptions indent, EditorConfig conventions, CancellationToken cancel = default) =>
        await Server.RequestAsync<AutoInserted>(LspMethods.OnAutoInsert,
            new AutoInsertParams(Identifier, PositionOf(offset), typed, FormattingOptions.Of(indent, conventions, Text.Snapshot)), cancel) is { TextEdit: not null } answer
            ? answer : null;

    /// <summary>A Razor file's HTML as Razor makes it of the text as it stands, its C# blanked out so its places are the
    /// file's: a fold's question, the cheapest, has Razor give it first. Null for a file not Razor's, or one Razor gave
    /// none for.</summary>
    public async Task<string?> RazorHtmlAsync(CancellationToken cancel = default)
    {
        if (!IsRazor)
        {
            return null;
        }
        await Read(Server.RequestAsync<JsonElement>(LspMethods.FoldingRange, new FoldingRangeParams(new TextDocumentIdentifier(Uri)), cancel));
        return Server.RazorHtmlOf(DocumentUris.ToPath(Uri));
    }

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
        SemanticTokens? tokens = await OrLooseAsync(await Server.RequestAsync<SemanticTokens>(LspMethods.SemanticTokensFull, parameters, cancel), parameters, cancel);
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
        SemanticTokens? tokens = await OrLooseAsync(await Server.RequestAsync<SemanticTokens>(LspMethods.SemanticTokensRange, parameters, cancel), parameters, cancel);
        Came(ref coloured, asked);
        return (tokens, version);
    }

    /// <summary>The server's colours, or where it gave none, those Passenger's extension gives a file in no project, which
    /// the server leaves uncoloured; asked with the same document and range.</summary>
    private async Task<SemanticTokens?> OrLooseAsync(SemanticTokens? tokens, object parameters, CancellationToken cancel)
    {
        if (tokens is { Data.Length: > 0 } || Server.ExtensionProblem is not null)
        {
            return tokens;
        }
        return await Server.RequestAsync<SemanticTokens>(ExtensionMethods.LooseColours, parameters, cancel) ?? tokens;
    }

    /// <summary>The symbol at an offset as a call hierarchy's root, if it is one that calls or is called.</summary>
    public Task<HierarchyItem?> PrepareCallHierarchyAsync(int offset, CancellationToken cancel = default) =>
        PrepareCallHierarchyAsync(Server, Identifier, PositionOf(offset), cancel);

    internal static async Task<HierarchyItem?> PrepareCallHierarchyAsync(LanguageServer server, TextDocumentIdentifier document, LspPosition at, CancellationToken cancel) =>
        (await server.RequestAsync<List<HierarchyItem>>(LspMethods.PrepareCallHierarchy, new TextDocumentPositionParams(document, at), cancel).ConfigureAwait(false))?.FirstOrDefault();

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
    /// says nothing. A hint given before the server read the document again (an .editorconfig, or what a build writes,
    /// changed its options) it refuses, so the hint it gives now at that place with those words is asked instead.</summary>
    public async Task<string> InlayHintTooltipAsync(LspInlayHint hint, CancellationToken cancel = default)
    {
        try
        {
            return await TooltipAsync(hint, cancel);
        }
        catch (LanguageServerFailed failed) when (failed.Outdated)
        {
            IReadOnlyList<LspInlayHint> now = (await InlayHintsAsync(cancel)).Hints;
            if (now.FirstOrDefault(h => h.Position == hint.Position && h.Label == hint.Label && h.Kind == hint.Kind) is not { } same)
            {
                throw;
            }
            return await TooltipAsync(same, cancel);
        }
    }

    private async Task<string> TooltipAsync(LspInlayHint hint, CancellationToken cancel)
    {
        JsonElement result = await Read(Server.RequestAsync<JsonElement>(LspMethods.InlayHintResolve,
            new { position = hint.Position, label = hint.Label, kind = hint.Kind, data = hint.Data }, cancel));
        return JsonMembers.Value(result, "tooltip") is { } tooltip ? Shapes.Text(tooltip) : string.Empty;
    }

    /// <summary>Roslyn's source of what analyzers report in a document from analysing its whole project, which it has with
    /// solution-wide analysis on: in a large project it answers a change long after the document's own sources.</summary>
    [IdentifierHome]
    public const string ProjectWideSource = "NonLocal";

    /// <summary>Whether the server has a source of the document's diagnostics that answers from its whole project's
    /// analysis (<see cref="DiagnosticPull.ProjectWide"/>).</summary>
    public bool PullsProjectWide => !IsRazor && session is RoslynSession && Server.DiagnosticSources.Contains(ProjectWideSource);

    /// <summary>Every diagnostic the server has for the document, pulled from each source it
    /// registered (a Razor file's from Razor's one, with its HTML's from the HTML server), and the version they are for;
    /// those it last pushed, from a server that pushes them.</summary>
    public Task<(IReadOnlyList<Diagnostic> Diagnostics, int Version)> DiagnosticsAsync(CancellationToken cancel = default) =>
        DiagnosticsAsync(DiagnosticPull.All, cancel);

    /// <summary>Every diagnostic the server has for the document as <see cref="DiagnosticsAsync(CancellationToken)"/> pulls
    /// them, answered from the last such pull where neither the text nor anything the server was told has changed since
    /// (<see cref="LanguageServer.Changes"/>): an agent's second ask of a file costs the server nothing. Pulled afresh
    /// for a Razor file, another server's, and with solution-wide analysis on (<paramref name="wholeSolution"/>), whose
    /// project-wide part comes later.</summary>
    public async Task<(IReadOnlyList<Diagnostic> Diagnostics, int Version)> LastDiagnosticsAsync(bool wholeSolution, CancellationToken cancel = default)
    {
        int version = Text.Version;
        long changes = Server.Changes;
        bool Stands(Pulled? last) => last is not null && last.Version == version && last.Changes == changes;
        if (!wholeSolution && Stands(pulled[(int)DiagnosticPull.All]))
        {
            return (pulled[(int)DiagnosticPull.All]!.Items, version);
        }
        // The window's own pull of the file's sources, then of its project-wide one, answers as one of every source would.
        if (!wholeSolution && Stands(pulled[(int)DiagnosticPull.Own]) && (!PullsProjectWide || Stands(pulled[(int)DiagnosticPull.ProjectWide])))
        {
            IEnumerable<Diagnostic> wide = PullsProjectWide ? pulled[(int)DiagnosticPull.ProjectWide]!.Items : [];
            return ([.. pulled[(int)DiagnosticPull.Own]!.Items.Concat(wide).DistinctBy(d => (d.Range, d.Message, d.Code))], version);
        }
        return await DiagnosticsAsync(DiagnosticPull.All, cancel);
    }

    /// <summary>The document's diagnostics from the sources <paramref name="part"/> names, as
    /// <see cref="DiagnosticsAsync(CancellationToken)"/> pulls every one; the project-wide part of a server that pushes them,
    /// or has no such source, is empty.</summary>
    public async Task<(IReadOnlyList<Diagnostic> Diagnostics, int Version)> DiagnosticsAsync(DiagnosticPull part, CancellationToken cancel = default)
    {
        int version = Text.Version;
        if (!session.PullsDiagnostics)
        {
            return part == DiagnosticPull.ProjectWide ? ([], version) : Pushed;
        }
        TextSnapshot text = Text.Snapshot;
        TextDocumentIdentifier identifier = Identifier;
        long asked = System.Diagnostics.Stopwatch.GetTimestamp();
        // Counted before the pull is sent, so a change sent while it runs leaves the answer stale.
        long changes = Server.Changes;
        List<Diagnostic> all = [];
        IReadOnlyList<string?> sources = IsRazor || session is not RoslynSession ? (part == DiagnosticPull.ProjectWide ? [] : [null])
            : [.. Server.DiagnosticSources.Where(s => part == DiagnosticPull.All || (s == ProjectWideSource) == (part == DiagnosticPull.ProjectWide))];
        // Gathered on the thread pool, so a file's thousands of warnings reach the caller as one list.
        foreach (string? source in sources)
        {
            // Named by its source, so a long wait says which: the project-wide one is a whole project's analysis.
            DocumentDiagnosticReport? report = await Read(Server.RequestAsync<DocumentDiagnosticReport>(LspMethods.DocumentDiagnostic,
                new DocumentDiagnosticParams(identifier, source), cancel, about: source));
            all.AddRange(report?.Items ?? []);
        }
        if (IsRazor && Server.HtmlServer is not null && part != DiagnosticPull.ProjectWide)
        {
            // Razor makes the file's HTML as a question needs it: a fold's, the cheapest, has the HTML server's copy current.
            await Read(Server.RequestAsync<JsonElement>(LspMethods.FoldingRange, new FoldingRangeParams(new TextDocumentIdentifier(Uri)), cancel));
            all.AddRange(await Server.RazorHtmlDiagnosticsAsync(DocumentUris.ToPath(Uri), text.ToString(), cancel).ConfigureAwait(false));
        }
        List<Diagnostic> distinct = all
            .DistinctBy(d => (d.Range, d.Message, d.Code))
            .ToList();
        Came(ref diagnosed, asked);
        if (session is RoslynSession && !IsRazor)
        {
            pulled[(int)part] = new Pulled(distinct, version, changes);
        }
        return (distinct, version);
    }

    /// <summary>An offset as an LSP position: the server's line (<see cref="ServerLines"/>), and a column in UTF-16
    /// units, which is what the editor's columns already are.</summary>
    public LspPosition PositionOf(int offset) => ServerLines.Of(Text.Snapshot).PositionOf(offset);

    public LspRange RangeOf(TextRange range) => ServerLines.Of(Text.Snapshot).RangeOf(range);

    public int OffsetOf(LspPosition position) => ServerLines.Of(Text.Snapshot).OffsetOf(position);

    /// <summary>The offsets of many places at once, as <see cref="ServerLines.OffsetsOf"/> gives them.</summary>
    public int[] OffsetsOf(IReadOnlyList<LspPosition> positions) => ServerLines.Of(Text.Snapshot).OffsetsOf(positions);

    /// <summary>The edits sent last to first, the order an incremental sync is read in, so each change's place in the text
    /// before the edit is its place in the text the changes sent before it left. Edits that touch go as one change: the
    /// later one can decide whether a '\r' where they meet ends a line. A change that begins or ends between the halves of
    /// "\r\n" takes in the half beside it and puts it back (<see cref="Unsplit"/>). The server's lines are carried to the
    /// new text here, so the next change, and every request until then, finds them without reading the file.</summary>
    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        ServerLines before = ServerLines.Of(e.Before);
        ServerLines.After(e);
        if (Server.TakesWholeTexts)
        {
            // A server that takes only whole texts (Godot's) reads a change's text as the document's.
            Sent(e.Version, changed: !session.Pushes.EachText || Changes(e));
            Server.Notify(LspMethods.DidChange, new DidChangeTextDocumentParams(new VersionedTextDocumentIdentifier(Uri, e.Version), [new(null, e.After.ToString())]));
            return;
        }
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
