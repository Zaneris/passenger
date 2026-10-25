using System.Text;
using Avalonia.Controls;
using Passenger.App.CSharp;
using Passenger.App.Editors;
using Passenger.App.Endpoints;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The web projects' routes, read from their code by the language server's extension (<see cref="Handlers.WebRoutes"/>):
/// listed in the Endpoints window by project, an icon on each route's line in an open file that sends its request (from its
/// project's .http file, an entry made there where none calls it), Go to Declaration from an .http request to its route and
/// Find Usages from a route to its requests. A project's routes are read again once a C#, Razor or .proto file of it changes
/// (typed, or on disk), off the UI thread, a pause after the change; an icon stands only while its file's text is the one
/// its route was read from.</summary>
public partial class MainWindow
{
    private Debounce? routesLater;
    private Task? routesRead;
    private CSharpService? routesFrom;
    private readonly Dictionary<OpenDocument, (int Version, string Hash)> routeHashes = [];

    /// <summary>The route icon's menu last opened.</summary>
    public MenuFlyout? RouteMarkMenu { get; private set; }

    private void AddRoutes()
    {
        routesLater = new Debounce(TimeSpan.FromMilliseconds(400), () =>
        {
            IdeErrors.Observe(ReadRoutesAsync());
            return Task.CompletedTask;
        });
        Closed += (_, _) => routesLater.Stop();
        Editors.Opened += (_, document) =>
        {
            if (!IsRouteSource(document.Path))
            {
                return;
            }
            document.Text.Changed += (_, _) =>
            {
                if (session.ProjectOf(document.Path) is { } project && session.Routes.IsWeb(project.Path))
                {
                    session.Routes.Stale(project.Path);
                    // No longer the text its routes were read from: its icons go until the routes are read again.
                    if (document.RouteMarks.Count > 0)
                    {
                        document.RouteMarks = [];
                    }
                    if (ReadsRoutes)
                    {
                        routesLater.Restart();
                    }
                }
            };
            ShowRouteMarks(document);
            WantRoutes(opening: true);
        };
        Editors.Closed += (_, document) => routeHashes.Remove(document);
    }

    /// <summary>The Endpoints window's routes, as it is built: a row's request sent, its declaration gone to, and the routes
    /// read once it is shown.</summary>
    private void FollowRoutes(EndpointsView view)
    {
        view.RouteSendRequested += (_, route) => IdeErrors.Observe(SendRouteAsync(route));
        view.RouteGoToRequested += (_, route) => GoToRoute(route);
        view.AttachedToVisualTree += (_, _) => WantRoutes();
    }

    /// <summary>Files whose text can declare a route.</summary>
    private static bool IsRouteSource(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".razor" or ".cshtml" or ".proto";

    /// <summary>The routes are read once typing pauses when a read is due and someone looks: the Endpoints window shown, or a
    /// file open that could declare one (<paramref name="opening"/> one now, which the open files may not list yet).</summary>
    private void WantRoutes(bool opening = false)
    {
        if (ReadsRoutes && session.Routes.Wants && (opening || Dock.IsShown(ToolWindowIds.Endpoints) || Dock.IsShown(ToolWindowIds.Requests) || requestsAsked
            || Editors.Documents.Any(d => IsRouteSource(d.Path))))
        {
            routesLater?.Restart();
        }
    }

    /// <summary>Whether the language server can read routes now. Until it can, a pause before a read would be waited
    /// out for nothing: what is due stays due, and the server's being ready reads it
    /// (<see cref="RoutesServerChanged"/>).</summary>
    private bool ReadsRoutes => session.CSharp is { IsReady: true, Session: not null };

    /// <summary>A new session: its routes are its own, none shown until read.</summary>
    private void ForgetRoutes(ProjectSession next)
    {
        routesFrom = null;
        routeHashes.Clear();
        endpoints?.ShowRoutes(next.Routes.All);
        foreach (OpenDocument document in Editors.Documents)
        {
            document.RouteMarks = [];
        }
    }

    /// <summary>The language server is ready on the folder's projects (or a new one is): every project's routes are read again.</summary>
    private void RoutesServerChanged(CSharpService service)
    {
        if (service == session.CSharp && !session.IsEnded && service.IsReady && routesFrom != service)
        {
            routesFrom = service;
            session.Routes.Forget();
            WantRoutes();
        }
    }

    /// <summary>The server loaded projects (more of them, or one again): every project's routes are read again, a read before
    /// having found none of a project it had yet to load.</summary>
    private void RoutesProjectsLoaded(CSharpService service)
    {
        if (service == session.CSharp && !session.IsEnded && session.Routes.HasRead)
        {
            session.Routes.Forget();
            WantRoutes();
        }
    }

    /// <summary>Files changed on disk: the routes of the web projects that own them are read again; a project file's change
    /// reads every project's.</summary>
    private void RouteFilesChanged(FolderChanges changes)
    {
        if (changes.Projects || changes.Lost)
        {
            session.Routes.Forget();
        }
        foreach (string path in changes.Paths.Where(IsRouteSource))
        {
            if (session.ProjectOf(path) is { } project)
            {
                session.Routes.Stale(project.Path);
            }
        }
        WantRoutes();
    }

    /// <summary>Reads the routes due, one read at a time: a read asked for while one is under way is that one.</summary>
    private Task ReadRoutesAsync()
    {
        if (routesRead is { IsCompleted: false } under)
        {
            return under;
        }
        routesRead = ReadRoutesCoreAsync();
        return routesRead;
    }

    private async Task ReadRoutesCoreAsync()
    {
        ProjectSession current = session;
        if (!current.Routes.Wants || current.IsEnded || current.CSharp is not { IsReady: true, Session: { } roslyn })
        {
            return;
        }
        IReadOnlyList<string> asked = current.Routes.ToRead();
        RoutesFound? found = await BackgroundWork.Run(async () =>
        {
            try
            {
                RoutesFound answer = await roslyn.ExtensionAsync<RoutesFound>(Handlers.WebRoutes, new RoutesRequest(asked)).ConfigureAwait(false);
                // Paths as the window compares them: canonical, which the server's need not be.
                return new RoutesFound([.. answer.Routes.Select(r => r with
                {
                    Project = PathNames.Canonical(r.Project),
                    Path = PathNames.Canonical(r.Path),
                    HandlerPath = r.HandlerPath is { } handler ? PathNames.Canonical(handler) : null,
                })], [.. answer.Projects.Select(PathNames.Canonical)]);
            }
            catch (LanguageServerFailed)
            {
                return null;
            }
        });
        if (session != current || current.IsEnded || found is null)
        {
            return;
        }
        current.Routes.Took(found, asked);
        requestLog?.Reroute();
        endpoints?.ShowRoutes(current.Routes.All);
        foreach (OpenDocument document in Editors.Documents.Where(d => IsRouteSource(d.Path)))
        {
            ShowRouteMarks(document);
        }
        // A change while it read: read again for it.
        if (current.Routes.Wants)
        {
            routesLater?.Restart();
        }
    }

    /// <summary>The routes as read now, read again first where a read is due, for an agent; the language server is the
    /// caller's to wait for.</summary>
    public async Task<IReadOnlyList<WebRoute>> RoutesAsync(CancellationToken cancel = default)
    {
        for (int turn = 0; turn < 4 && (session.Routes.Wants || routesRead is { IsCompleted: false }); turn++)
        {
            await ReadRoutesAsync().WaitAsync(cancel);
        }
        return session.Routes.All;
    }

    /// <summary>The routes an open file declares in the text it has now (read from that text), each with its editor line.</summary>
    private IEnumerable<(WebRoute Route, TextPosition At, TextPosition End)> RoutesIn(OpenDocument document)
    {
        List<WebRoute> here = [.. session.Routes.All.Where(r => PathNames.Same(r.Path, document.Path))];
        if (here.Count == 0)
        {
            yield break;
        }
        int version = document.Text.Version;
        if (!routeHashes.TryGetValue(document, out (int Version, string Hash) known) || known.Version != version)
        {
            known = (version, TextHash.Of(document.Text.Snapshot.ToString()));
            routeHashes[document] = known;
        }
        TextSnapshot text = document.Text.Snapshot;
        foreach (WebRoute route in here.Where(r => r.SourceHash == known.Hash))
        {
            yield return (route, ServerPlaces.PositionOf(text, new LspPosition(route.At.Line, route.At.Character)),
                ServerPlaces.PositionOf(text, new LspPosition(route.End.Line, route.End.Character)));
        }
    }

    private void ShowRouteMarks(OpenDocument document)
    {
        RunMark[] marks = [.. RoutesIn(document).Where(r => RouteRequests.Sendable(r.Route)).Select(r => r.At.Line).Distinct().Select(l => new RunMark(l))];
        if (!marks.SequenceEqual(document.RouteMarks))
        {
            document.RouteMarks = marks;
        }
    }

    /// <summary>The route icon's menu: Send of each route its line declares.</summary>
    private void ShowRouteMarkMenu(CodeEditor editor, OpenDocument document, int line)
    {
        MenuFlyout menu = new();
        foreach ((WebRoute route, _, _) in RoutesIn(document).Where(r => r.At.Line == line && RouteRequests.Sendable(r.Route)))
        {
            MenuItem send = new() { Header = $"Send {RouteRequests.Title(route)}" };
            send.Click += (_, _) => IdeErrors.Observe(SendRouteAsync(route));
            menu.Items.Add(send);
        }
        if (menu.Items.Count == 0)
        {
            return;
        }
        RouteMarkMenu = menu;
        menu.Placement = PlacementMode.Pointer;
        menu.ShowAt(editor, showAtPointer: true);
    }

    /// <summary>Opens a route where its code declares it.</summary>
    public void GoToRoute(WebRoute route) => NavigateTo(route.Path, new LspPosition(route.At.Line, route.At.Character));

    /// <summary>Sends a route's request through the Endpoints window, as its Test does: the request of the project's .http
    /// files that calls it, else one written into the project's own .http file for it (one undo step), to the running app's
    /// address, else the file's. Says why when it cannot, and returns that.</summary>
    public async Task<string?> SendRouteAsync(WebRoute route)
    {
        UiThread.Check();
        ProjectSession current = session;
        if (!RouteRequests.Sendable(route))
        {
            return Said($"{RouteRequests.Title(route)} is a gRPC method: it takes a gRPC client, not an HTTP request");
        }
        ShowToolWindow(ToolWindowIds.Endpoints);
        string folder = Path.GetDirectoryName(route.Project)!;
        // The open .http files' text as the editor has it, the rest read from disk off the UI thread.
        Dictionary<string, string> open = Editors.Documents.Where(d => HttpFile.Is(d.Path) && PathNames.IsUnder(d.Path, folder))
            .ToDictionary(d => d.Path, d => d.Text.Snapshot.ToString(), PathNames.Comparer);
        IEnumerable<string> files = Projects.FirstOrDefault(p => PathNames.Same(p.Path, route.Project)) is { } project
            ? FolderWalk.ProjectFiles(project, Projects).Where(HttpFile.Is)
            : HttpFilesUnder(folder);
        (EndpointRequest Request, HttpFile File)? found = await BackgroundWork.Run(() => CallerIn(files, open, route));
        if (session != current || current.IsEnded)
        {
            return null;
        }
        if (found is null)
        {
            if (Generate(route) is not { } made)
            {
                return status.Text;
            }
            found = made;
        }
        string? address = await RunningAddressAsync(route.Project);
        if (session != current || current.IsEnded)
        {
            return null;
        }
        await Endpoints.TestAsync(found.Value.Request, address, found.Value.File);
        return null;

        string Said(string problem)
        {
            status.Text = problem;
            return problem;
        }
    }

    /// <summary>The first request of a project's .http files that calls a route, with its file as it reads now.</summary>
    private static (EndpointRequest Request, HttpFile File)? CallerIn(IEnumerable<string> files, Dictionary<string, string> open, WebRoute route)
    {
        foreach (string path in files.Concat(open.Keys).Distinct(PathNames.Comparer).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                HttpFile file = HttpFile.Parse(path, open.TryGetValue(path, out string? text) ? text : File.ReadAllText(path));
                if (file.Requests.FirstOrDefault(r => RouteRequests.Calls(r, route)) is { } request)
                {
                    return (request, file);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    /// <summary>A folder's .http and .rest files, as the folder's walk finds them.</summary>
    private static IEnumerable<string> HttpFilesUnder(string folder) => FolderWalk.Files(folder).Where(HttpFile.Is);

    /// <summary>A route's request written at the end of its project's .http file (made beside the project when it has
    /// none): in the editor as its own undo step when the file is open, else as a command Undo takes back whole.</summary>
    private (EndpointRequest Request, HttpFile File)? Generate(WebRoute route)
    {
        string path = PathNames.Canonical(RouteRequests.FileFor(route.Project));
        string? address = ProfileAddress(route.Project);
        try
        {
            if (Editors.Find(path) is { } document)
            {
                if (document.IsReadOnly)
                {
                    status.Text = $"{Path.GetFileName(path)} is read-only: no request could be written for {RouteRequests.Title(route)}";
                    return null;
                }
                string before = document.Text.Snapshot.ToString();
                string entry = RouteRequests.Entry(route, before, address);
                TextEdit[] edits = [new TextEdit(before.Length, 0, entry)];
                document.Text.Edit(edits, document.Selections, TextEditing.MapThrough(document.Selections, edits));
                return Written(path, document.Text.Snapshot.ToString(), before);
            }
            FileText? had = File.Exists(path) ? FileText.Load(path) : null;
            string existing = had?.Text ?? string.Empty;
            string breaks = existing.Length > 0 ? TextDocument.DetectLineEnding(existing) : "\n";
            string text = existing + RouteRequests.Entry(route, existing.Replace("\r\n", "\n", StringComparison.Ordinal), address).Replace("\n", breaks, StringComparison.Ordinal);
            using (CommandHistory.Recording made = Editors.Commands.Record("Generate Request"))
            {
                made.Watch(path);
                FileText.Save(path, text, had?.Encoding ?? new UTF8Encoding(false), had?.HasBom ?? false);
            }
            endpoints?.Reload(session.Folder);
            return Written(path, text, existing);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ReadOnlyFileException)
        {
            status.Text = $"No request could be written for {RouteRequests.Title(route)} in {Path.GetFileName(path)}: {error.Message}";
            return null;
        }

        (EndpointRequest, HttpFile)? Written(string file, string now, string was)
        {
            HttpFile read = HttpFile.Parse(file, now);
            int from = was.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length - 1;
            status.Text = $"Wrote a request for {RouteRequests.Title(route)} in {PathNames.Shown(file, session.Folder)}";
            return read.Requests.FirstOrDefault(r => r.Line >= from && RouteRequests.Calls(r, route)) is { } request ? (request, read) : null;
        }
    }

    /// <summary>The address a project's app answers at now: the first of the programs the folder runs of it
    /// (<see cref="ProjectSession.Running"/>) that says one, Run's or a Debug tab's by the plan it was launched by, a .NET
    /// service's of a run of services as its run publishes it; null when none runs it.</summary>
    private async Task<string?> RunningAddressAsync(string project)
    {
        RunningProgram[] running = [.. session.Running.Where(p => p.Project is { } of && PathNames.Same(of, project))];
        if (running.Select(p => p.Plan is { } plan ? AddressOf(plan) : null).FirstOrDefault(a => a is not null) is { } here)
        {
            return here;
        }
        using IDisposable held = BackgroundWork.Scope();
        foreach (RunningProgram service in running.Where(p => p.Services is not null))
        {
            try
            {
                if (await service.Services!.AddressAsync(service.Service!, CancellationToken.None) is { } published)
                {
                    return published;
                }
            }
            catch (Exception error) when (service.Services!.Refused(error) || error is InvalidOperationException or System.Text.Json.JsonException)
            {
                // An engine or AppHost that does not answer leaves the file's own address.
            }
        }
        return null;

        static string? AddressOf(LaunchPlan plan) => RouteRequests.AddressOf(plan.Environment.GetValueOrDefault("ASPNETCORE_URLS"))
            ?? (Uri.TryCreate(plan.BrowserUrl, UriKind.Absolute, out Uri? browsed) ? browsed.GetLeftPart(UriPartial.Authority) : null);
    }

    /// <summary>The address a project's first launch profile that runs it gives (its applicationUrl's HTTP one first).</summary>
    private string? ProfileAddress(string project) =>
        session.Workspace?.Projects.FirstOrDefault(p => PathNames.Same(p.Path, project)) is { } model
            ? LaunchProfile.Read(model).Where(p => p.RunsProject).Select(p => RouteRequests.AddressOf(p.ApplicationUrl)).FirstOrDefault(a => a is not null)
            : null;

    /// <summary>The routes the request on an .http file's line calls, for Go to Declaration from it.</summary>
    private IReadOnlyList<LspLocation> HttpRouteTargets(string path, int line)
    {
        if (Editors.Find(path) is not { } document
            || RouteRequests.RequestAt(path, document.Text.Snapshot.ToString(), line) is not { } request)
        {
            return [];
        }
        return [.. session.Routes.All.Where(r => RouteRequests.Calls(request, r))
            .Select(r => new LspLocation(DocumentUris.FromPath(r.Path), new LspRange(new LspPosition(r.At.Line, r.At.Character), new LspPosition(r.End.Line, r.End.Character))))];
    }

    /// <summary>The .http requests that call the route an open file declares at an offset: null where it declares none.</summary>
    private (string Title, IReadOnlyList<LspLocation> Requests)? RouteCallers(string path, int offset)
    {
        if (Editors.Find(path) is not { } document)
        {
            return null;
        }
        TextSnapshot text = document.Text.Snapshot;
        TextPosition caret = text.GetPosition(Math.Min(offset, text.Length));
        List<WebRoute> here = [.. RoutesIn(document).Where(r => Compare(r.At, caret) <= 0 && Compare(caret, r.End) <= 0).Select(r => r.Route)];
        if (here.Count == 0)
        {
            return null;
        }
        List<LspLocation> callers = [];
        string? folder = session.Folder;
        if (folder is not null)
        {
            Dictionary<string, string> open = Editors.Documents.Where(d => HttpFile.Is(d.Path)).ToDictionary(d => d.Path, d => d.Text.Snapshot.ToString(), PathNames.Comparer);
            foreach (string file in HttpFilesUnder(folder).Concat(open.Keys).Distinct(PathNames.Comparer).Order(StringComparer.OrdinalIgnoreCase))
            {
                string written;
                try
                {
                    written = open.TryGetValue(file, out string? held) ? held : File.ReadAllText(file);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (EndpointRequest request in HttpFile.Parse(file, written).Requests.Where(r => here.Any(route => RouteRequests.Calls(r, route))))
                {
                    LspPosition at = new(request.Line, 0);
                    callers.Add(new LspLocation(DocumentUris.FromPath(file), new LspRange(at, at)));
                }
            }
        }
        return (string.Join(", ", here.Select(RouteRequests.Title).Distinct()), callers);

        static int Compare(TextPosition left, TextPosition right) => left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column);
    }
}
