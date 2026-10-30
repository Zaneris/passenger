using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Passenger.App.CSharp;
using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Mcp;

/// <summary>Passenger's tools for an agent, over the Model Context Protocol: the window's own workspace,
/// the language server's knowledge of the code, builds and runs, the debugger, and the IDE itself
/// (screenshots, actions, typing), each run on the UI thread as the window's own commands are. Lines and
/// columns count from one, as an editor shows them; a relative path is the open folder's. A tool not ReadOnly says
/// whether it is Destructive: a person's window asks before an agent first uses one that is not declared harmless.
/// What a folder has and runs a tool asks of the window's session (<see cref="ProjectSession"/>) as the call finds it, and
/// what it starts it starts through the session's controllers; the window itself only for what is the window's: opening
/// a folder in it, its editors, its terminal, its actions and what it draws. No tool reads the status bar.
/// <paramref name="connection"/> is the agent's, through which a person's window asks before the agent replaces its folder;
/// none is an agent's own IDE's.</summary>
public sealed partial class PassengerTools(MainWindow window, AgentConnection? connection = null)
{
    private static readonly TimeSpan ServerLimit = TimeSpan.FromSeconds(120);

    /// <summary>How long a file's markers are waited for: its checks are quick once its project is read.</summary>
    private static readonly TimeSpan MarkersLimit = TimeSpan.FromSeconds(30);

    /// <summary>The folder open in the window as this call finds it: an agent's Open makes another.</summary>
    private ProjectSession Session => window.Session;

    /// <summary>The session's builds, runs and debug launches as the window shows them.</summary>
    private BuildRunController Builds => window.Builds;

    private Passenger.App.Editors.EditorArea Editors => window.EditorArea;

    // Workspace and editor

    [McpServerTool(Name = McpTools.Open, Title = "Open a folder, solution, project or file", Destructive = false), Description(
        "Opens a folder, solution or project (a directory, .sln, .slnx or .csproj) as the workspace, or a file in the editor " +
        "(its folder first if none is open; with line, a project or solution file too), its caret at line and column if given, " +
        "before that column's character. Returns the workspace.")]
    public Task<Workspace> Open(string path, int? line = null, int? column = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        string full = Resolve(path);
        // Opened once what another folder ran has ended.
        if (Directory.Exists(full) || (File.Exists(full) && WorkspaceModel.Opens(full) && line is null))
        {
            string opening = Directory.Exists(full) ? PathNames.Canonical(full) : Path.GetDirectoryName(PathNames.Canonical(full))!;
            // The person's folder is theirs: an agent on their window replaces it only with their leave.
            if (connection is { Attached: true } && !window.AgentsUnattended && Session.Folder is { } open && !PathNames.Same(open, opening))
            {
                AgentCall.Current?.Waits($"Waiting for the person at {AgentConnection.Where(window)} to let {connection.Agent} open {full} (at most {AgentConnection.Limit(window)})");
                Admission said = await window.AllowAgentOpenAsync(connection.Agent, full, open, cancel);
                AgentCall.Current?.Waited(said.ToString());
                cancel.ThrowIfCancellationRequested();
                if (said != Admission.Allowed)
                {
                    throw new InvalidOperationException(said switch
                    {
                        Admission.NotAllowed => $"The person at this Passenger window did not let {connection.Agent} open {full} in place of {open}. Ask them, or work in {open}.",
                        Admission.FolderChanged => Refusals.FolderChanged(open),
                        _ => $"Nobody at {AgentConnection.Where(window)} answered within {AgentConnection.Limit(window)} whether {connection.Agent} may open {full} in place of {open}. A later call asks again.",
                    });
                }
            }
            await window.OpenFolder(full, connection).WaitAsync(cancel);
        }
        else if (File.Exists(full))
        {
            if (Session.Folder is null)
            {
                await window.OpenFolder(Path.GetDirectoryName(full)!, connection).WaitAsync(cancel);
            }
            // Its disk's text taken first, as every tool naming a file does, then shown.
            OpenDocument document = await DocumentAsync(full, cancel);
            // On a person's window, the file they have shown keeps their caret, and another comes behind it.
            bool theirs = Beside && Editors.Active == document;
            if (window.OpenFile(full) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }
            await Previewed(document, cancel);
            if (line is int l && !theirs)
            {
                window.NavigateTo(document.Path, new TextPosition(Math.Max(0, l - 1), Math.Max(0, (column ?? 1) - 1)));
            }
        }
        else
        {
            throw new FileNotFoundException($"No folder or file at {full}");
        }
        // Projects come once the walk for them lands, and what MSBuild evaluates once each evaluation does.
        await Session.WorkspaceRead.WaitAsync(cancel);
        await Session.AspireFilesRead.WaitAsync(cancel);
        await Task.Yield();
        await Session.Models.Idle.WaitAsync(cancel);
        return await RunsWorkspace(Snapshot(), cancel);
    });

    [McpServerTool(Name = McpTools.Workspace, Title = "The workspace", ReadOnly = true), Description(
        "The open folder and solution (or solution filter), its projects (output type, frameworks, unloaded marked), run " +
        "configurations (the chosen one marked), the language server's state, the open files (unsaved marked) and the active " +
        "one with its caret, and the Aspire AppHosts and container run under way, each resource with its state, hold, debug " +
        "session and target. routes adds the web projects' routes, read from their code.")]
    public Task<Workspace> GetWorkspace(bool routes = false, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        Workspace answered = await RunsWorkspace(Snapshot(), cancel);
        if (!routes)
        {
            return answered;
        }
        if (Session.CSharp is null)
        {
            throw new InvalidOperationException($"{Refusals.NoCSharp}, and routes are read from code through it");
        }
        await When(() => Session.CSharp is not { } csharp || csharp.IsReady, ServerLimit, cancel, ServerState());
        IReadOnlyList<Roslyn.Extension.WebRoute> found = await window.RoutesAsync(cancel);
        return answered with
        {
            Routes = [.. found.Select(r => new RouteInfo(Path.GetFileNameWithoutExtension(r.Project), r.Kind, Endpoints.RouteRequests.Title(r), r.Handler,
                [.. r.Parameters.Select(p => $"{p.Name}{(p.Constraints is { } c ? ":" + c : p.Type is { } t ? ": " + t : string.Empty)}{(p.Optional ? "?" : string.Empty)} ({p.Source})")],
                Relative(r.Path), ServerPlaces.PositionOf(Editors, r.Path, new LspPosition(r.At.Line, r.At.Character)).Line + 1, r.BodyType))],
        };
    });

    /// <summary>Waits for an image's or a font's decode, which lands off the window's thread: one whose first decode
    /// failed has closed its tab, and the agent hears why rather than that the file is open.</summary>
    private async Task Previewed(OpenDocument document, CancellationToken cancel)
    {
        await document.DecodedAsync().WaitAsync(cancel);
        if (document.PreviewFailure is { } failure)
        {
            throw new InvalidOperationException($"{Relative(document.Path)} could not be opened: {failure}");
        }
    }

    [McpServerTool(Name = McpTools.EditorText, Title = "A file's text in the editor", ReadOnly = true), Description(
        "A file's text as the editor holds it, unsaved changes included, its lines numbered.")]
    public Task<string> EditorText(string path, int? from = null, int? to = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        OpenDocument document = await TextualAsync(path, cancel);
        TextSnapshot text = document.Text.Snapshot;
        int first = LineIn(text, document.Path, from ?? 1);
        if (to is int end && end <= first)
        {
            throw new ArgumentException($"to {end} is before from {first + 1}");
        }
        int last = Math.Min((to ?? text.LineCount) - 1, text.LineCount - 1);
        return string.Join('\n', Enumerable.Range(first, last - first + 1).Select(i => $"{i + 1,6}\t{text.GetLineText(i)}"));
    });

    // Not ide_action's app.saveAll: never asked on a person's window (#278), and its answer names what was left unsaved.
    [McpServerTool(Name = McpTools.SaveAll, Title = "Save every file", Destructive = false), Description("Saves every open file with unsaved changes.")]
    public Task<string> SaveAll(CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        int unsaved = Editors.Documents.Count(d => d.IsModified);
        await SaveLandedAsync(cancel);
        // A read-only file, or one the system refused, keeps its changes, and the agent is told so rather than "Saved".
        List<OpenDocument> left = [.. Editors.Documents.Where(d => d.IsModified)];
        if (left.Count > 0)
        {
            string names = string.Join(", ", left.Select(d => FileText.IsReadOnly(d.Path) ? $"{Relative(d.Path)} (read-only)" : Relative(d.Path)));
            throw new InvalidOperationException($"Saved {unsaved - left.Count} of {unsaved} files; not saved: {names}");
        }
        return unsaved == 0 ? "Nothing to save" : $"Saved {Plural.Of(unsaved, "file")}";
    });

    /// <summary>Saves every changed file and comes back once each save has landed (<see cref="Passenger.App.Editors.EditorArea.LandedAsync"/>), so
    /// the agent, its shell and what it starts read what was saved; a save that never lands fails the call, saying why.</summary>
    private async Task SaveLandedAsync(CancellationToken cancel)
    {
        Editors.SaveAll();
        await LandedAsync(cancel);
    }

    /// <summary>Comes back once each save still landing has (<see cref="Passenger.App.Editors.EditorArea.LandedAsync"/>); one that never lands
    /// fails the call, saying why.</summary>
    private static async Task LandedAsync(CancellationToken cancel)
    {
        if (await Passenger.App.Editors.EditorArea.LandedAsync(null, cancel) is { } unsaved)
        {
            throw new InvalidOperationException(unsaved);
        }
    }

    // Code: the language server's

    // The Problems window's lists are this tool's with no path: a tool of their own cost every session's list more.
    [McpServerTool(Name = McpTools.Diagnostics, Title = "Errors and warnings", ReadOnly = true), Description(
        "A file's errors and warnings as the editor marks them, fresh: C#'s from the language server (its messages too), any " +
        "other's once its checks have run (XAML's, an MSBuild file's evaluation and NuGet advisories, a web language's server " +
        "and ESLint). With no path, what the Problems window lists beyond a file's: toolset (each project's load warnings and " +
        "errors), build (the last build's, whoever started it) and solution (every file's, while settings' solutionWideAnalysis " +
        "is on).")]
    public async Task<object> Diagnostics(string? path = null, CancellationToken cancel = default) =>
        path is null ? await Problems(cancel) : await FileDiagnostics(path, cancel);

    private Task<IReadOnlyList<Finding>> FileDiagnostics(string path, CancellationToken cancel) => OnUi(cancel, async () =>
    {
        if (await DocumentAsync(path, cancel) is { Language.Service: not Languages.LanguageService.CSharp } marked)
        {
            await Checked(marked, cancel);
            List<Finding> missing = [];
            if (marked.Unanswered is { Count: > 0 } unanswered)
            {
                // Each check that has not answered for the text as it stands, within the wait or its own.
                string who = unanswered.Count == 1 ? "it" : "they";
                missing.Add(new Finding("info", null, $"Not yet checked as it stands by {string.Join(", ", unanswered)}: what {who} found before is listed",
                    Relative(marked.Path), 1, 1));
            }
            if (Session.Web is { } web && web.For(marked) is { Lsp: null })
            {
                if (Languages.Language.ForPath(marked.Path) != Languages.Language.Dockerfile && !ComposeDeclarations.IsCompose(marked.Path))
                {
                    // A web file whose server cannot start has no checks: none found would read as none there.
                    throw new InvalidOperationException(web.Unready("Its diagnostics", marked));
                }
                // A Dockerfile's or compose file's container engine checks it without the server: its findings, and a word that the server's are missing.
                missing.Add(new Finding("info", null, web.Unready("Checking it beyond the container engine", marked), Relative(marked.Path), 1, 1));
            }
            TextSnapshot text = marked.Text.Snapshot;
            return (IReadOnlyList<Finding>)[.. marked.Markers.Where(m => m.Kind is MarkerKind.Error or MarkerKind.Warning).Select(m =>
            {
                TextPosition at = text.GetPosition(Math.Min(m.Range.Start, text.Length));
                return new Finding(m.Kind == MarkerKind.Error ? "error" : "warning", null, m.Message, Relative(marked.Path), at.Line + 1, at.Column + 1);
            }), .. missing];
        }
        (OpenDocument document, LspDocument lsp) = await Code(path, cancel);
        (IReadOnlyList<Diagnostic> found, _) = await lsp.DiagnosticsAsync(cancel);
        // Passenger's own markers too: a service's missing references, unset AppHost parameters and configuration keys.
        if (Session.CSharp?.For(document) is { } csharp)
        {
            found = [.. found, .. await csharp.PassengerFindingsAsync(lsp, cancel)];
        }
        TextSnapshot answered = lsp.Text.Snapshot;
        return (IReadOnlyList<Finding>)[.. found.Select(d =>
        {
            TextPosition at = ServerPlaces.PositionOf(answered, d.Range.Start);
            return new Finding(d.Severity switch { DiagnosticSeverity.Error => "error", DiagnosticSeverity.Warning => "warning", DiagnosticSeverity.Information => "info", _ => "hint" }, d.Code, d.Message,
                Relative(document.Path), at.Line + 1, at.Column + 1);
        })];
    });

    /// <summary>Waits, within <see cref="MarkersLimit"/>, for a file's own checks of its text as it stands (<see
    /// cref="OpenDocument.IsChecking"/>), which its markers and the fixes for them answer from; the rest of the IDE's work
    /// (a build, a test run, a busy console) is not waited for.</summary>
    private static Task Checked(OpenDocument document, CancellationToken cancel) =>
        When(() => !document.IsChecking, MarkersLimit, cancel, On(h => document.Checked += h, h => document.Checked -= h));

    [McpServerTool(Name = McpTools.SymbolInfo, Title = "What a symbol is", ReadOnly = true), Description(
        "What the symbol at a position is, as the hover card says: its declaration and documentation (a JSON property's " +
        "schema), in C# its documentation id too, by symbol every symbol the name names; in XAML the type, property, event, resource or element a name is; in an MSBuild file what the evaluation " +
        "makes of a name (a property's value and where it was set, an item's items, a target, an import's files).")]
    public Task<string> SymbolInfo(string? path = null, int? line = null, int? column = null, string? symbol = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (symbol is not null && path is null && line is null && column is null)
        {
            return await SymbolsInfo(symbol, cancel);
        }
        CodePlace at = await AskedAt(path, line, column, symbol, cancel);
        Served served = await Serve(at.Path, cancel);
        int offset = Offset(served.Document, at.Line, at.Column);
        string said = await CardAsync(served.Document, offset, cancel);
        // A C# place on no symbol tells of the one its line declares.
        if (said.Length == 0 && served is ServerServed { Web: null } code)
        {
            said = await CardAsync(served.Document, offset = await DeclaredOn(code, at.Line, at.Column), cancel);
        }
        if (said.Length == 0)
        {
            return "Nothing is known about this position";
        }
        return served is ServerServed { Web: null } csharp && served.Document.Language == Languages.Language.CSharp && await csharp.Lsp.SymbolAtAsync(offset) is { Id: { } id }
            ? $"{said}\n\nDocumentation id: {id}" : said;
    });

    [McpServerTool(Name = McpTools.Definition, Title = "Go to definition", ReadOnly = true), Description(
        "Where the symbol at a position is declared, as Go to Declaration goes (a C# symbol of a package or the framework has " +
        "no source, and says so). Beyond the language servers': from XAML, the C# or XAML declaring a type, property, event, " +
        "resource or element, a library's type decompiled, a theme's XAML by Source Link; package.json's dependencies and npm " +
        "run scripts; a compose file's build (Dockerfile, project), names, variables (.env) and env_files; Aspire's AddProject " +
        "and the resource names a service reads; an MSBuild file's properties (where set, the one in force first), item types, " +
        "targets, imports and SDKs.")]
    public Task<IReadOnlyList<Place>> Definition(string? path = null, int? line = null, int? column = null, string? symbol = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        CodePlace at = await AskedAt(path, line, column, symbol, cancel);
        Served served = await Serve(at.Path, cancel);
        int offset = Offset(served.Document, at.Line, at.Column);
        Declared? declared = await window.Assist.Languages.DeclaredAsync(served.Document, offset);
        // A C# place on no symbol goes from the one its line declares.
        if (declared is { Places.Count: 0, Page: null } && served is ServerServed { Web: null } code && await SymbolOffset(code, at.Line, at.Column, offset) is int named && named != offset)
        {
            declared = await window.Assist.Languages.DeclaredAsync(served.Document, named);
        }
        if (declared is { Page: { } page })
        {
            throw new InvalidOperationException($"{declared.None} is declared on its registry, not in the folder: its page is {page}");
        }
        if (declared is null || declared.Places.Count == 0 && declared.None == Refusals.NoDeclaration)
        {
            return (IReadOnlyList<Place>)[];
        }
        return declared.Places.Count > 0 ? await Places(declared.Places, cancel) : throw new InvalidOperationException(declared.None);
    });

    [McpServerTool(Name = McpTools.Usages, Title = "Find usages", ReadOnly = true), Description(
        "Every place the symbol at a position is used, its declaration included, as Find Usages finds them (a C# symbol's in " +
        "XAML too), by file, each as line:column and its line's text, after the C# symbol the place took; in XAML, an " +
        "element's name in its file and code-behind, a resource's key across the project, or a C# type or member as from its " +
        "declaration. JSON and MSBuild files have none.")]
    public Task<UsagesFound> Usages(string? path = null, int? line = null, int? column = null, string? symbol = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        CodePlace at = await AskedAt(path, line, column, symbol, cancel);
        Served served = await Serve(at.Path, cancel);
        int offset = Offset(served.Document, at.Line, at.Column);
        string place = $"{Relative(served.Document.Path)}:{at.Line}:{at.Column}";
        switch (served)
        {
            case XamlServed xaml:
                return ByFile(null, await XamlUsages(xaml, offset, cancel) is { Count: > 0 } uses ? uses
                    : throw new InvalidOperationException($"No name, resource key or C# symbol at {place}: {XamlAssist.XamlUsesWhat}"));
            case MSBuildServed:
                throw Lacks(served, "Find Usages", MSBuildHas);
            case ServerServed unfound when !unfound.Provides("referencesProvider"):
                throw new InvalidOperationException($"{unfound.Language} has no usages: its language server finds none, so Find Usages finds nothing in {Path.GetFileName(unfound.Document.Path)}");
        }
        ServerServed server = (ServerServed)served;
        // A component's file at its start is the class Razor makes of it, which the server takes in the C# made of the file.
        RoslynSession? roslyn = server.Web is null ? Session.CSharp?.Session : null;
        TypeRename.Component? component = await TypeRename.ComponentAtAsync(roslyn, served.Document.Path, served.Document.Text.Snapshot, offset);
        IReadOnlyList<LspLocation> found = component is not null && roslyn is not null ? await roslyn.ReferencesAsync(component.Name, cancel) : await server.Lsp.ReferencesAsync(offset);
        // A symbol's usages hold its declaration, so none says no symbol is there.
        if (found.Count == 0 && server.Web is null && component is null)
        {
            found = await server.Lsp.ReferencesAsync(offset = await DeclaredOn(server, at.Line, at.Column));
        }
        if (found.Count == 0)
        {
            throw new InvalidOperationException($"No symbol at {place}: {server.Language}'s language server finds no usages there");
        }
        // Passenger's extension reads a C# file's symbols, not a Razor file's.
        Roslyn.Extension.NamedSymbol? named = component?.Symbol
            ?? (served.Document.Language == Languages.Language.CSharp ? await server.Lsp.SymbolAtAsync(offset, cancel) : null);
        string? taken = named is null ? null : $"{named.Shown} ({named.Kind})";
        found = [.. found, .. await window.Assist.Languages.Xaml.CodeUsesAsync(named)];
        // Answered now, saying which files that could use it the server has yet to take in.
        IReadOnlyList<(string Path, string Project)> untaken = server.Web is null && Session.CSharp is { } csharp ? await csharp.UntakenAsync(served.Document, offset, cancel) : [];
        return ByFile(taken, await Places(found, cancel)) with { Note = untaken.Count > 0 ? Refusals.Untaken([.. untaken.Select(u => Relative(u.Path))]) : null };
    });

    /// <summary>usages' answer: its places in order (<see cref="Sorted"/>), each file named once.</summary>
    private static UsagesFound ByFile(string? symbol, IReadOnlyList<Place> places) =>
        new(symbol, places.Count, [.. Sorted(places).GroupBy(p => p.Path, StringComparer.Ordinal)
            .Select(file => new FilePlaces(file.Key, [.. file.Select(p => p.Text.Length == 0 ? $"{p.Line}:{p.Column}" : $"{p.Line}:{p.Column} {p.Text}")]))]);

    [McpServerTool(Name = McpTools.Hierarchy, Title = "Call or type hierarchy", ReadOnly = true), Description(
        "One level of the hierarchy of the symbol at a position, as the Hierarchy window: direction callers (each at its " +
        "call), callees, supertypes, subtypes, or implementations (Go to Implementation's).")]
    public Task<IReadOnlyList<HierarchyEntry>> Hierarchy(string? path = null, int? line = null, int? column = null, string direction = "callers", string? symbol = null,
        CancellationToken cancel = default) => OnUi<IReadOnlyList<HierarchyEntry>>(cancel, async () =>
    {
        CodePlace at = await AskedAt(path, line, column, symbol, cancel);
        (OpenDocument document, LspDocument lsp) = await Code(at.Path, cancel);
        int offset = await SymbolOffset(new ServerServed(document, document.Language.Name, lsp), at.Line, at.Column, Offset(document, at.Line, at.Column));
        if (direction == "implementations")
        {
            return [.. (await Places(await lsp.ImplementationAsync(offset), cancel)).Select(p => new HierarchyEntry(null, null, p.Path, p.Line, p.Column, p.Text))];
        }
        RoslynSession csharp = Session.CSharp?.Session ?? throw new InvalidOperationException("C# is not ready");
        LanguageServer server = csharp.Server;
        bool calls = direction is "callers" or "callees";
        if (!calls && direction is not ("supertypes" or "subtypes"))
        {
            throw new ArgumentException($"No direction '{direction}'; there are callers, callees, supertypes, subtypes and implementations", nameof(direction));
        }
        HierarchyItem item = (calls ? await lsp.PrepareCallHierarchyAsync(offset) : await lsp.PrepareTypeHierarchyAsync(offset))
            ?? throw new InvalidOperationException(calls ? "No method, property or event there" : "No type there");
        IReadOnlyList<(HierarchyItem Item, LspRange At)> found = direction switch
        {
            "callers" => [.. (await server.IncomingCallsAsync(item)).Select(c => (c.From, c.FromRanges.FirstOrDefault() ?? c.From.SelectionRange))],
            "callees" => [.. (await server.OutgoingCallsAsync(item)).Select(c => (c.To, c.To.SelectionRange))],
            "supertypes" => [.. (await server.SupertypesAsync(item)).Select(t => (t, t.SelectionRange))],
            _ => [.. (await server.SubtypesAsync(item)).Select(t => (t, t.SelectionRange))],
        };
        IReadOnlyList<Place> places = await Places(await csharp.WrittenAsync([.. found.Select(f => new LspLocation(f.Item.Uri, f.At))], cancel), cancel);
        return [.. found.Select((f, i) => new HierarchyEntry(f.Item.Name, f.Item.Detail, places[i].Path, places[i].Line, places[i].Column, places[i].Text))];
    });

    [McpServerTool(Name = McpTools.Rename, Title = "Rename a symbol", Destructive = true), Description(
        "Renames the symbol at a position everywhere it is used, as Rename does, and saves the files: a type's files named " +
        "after it too (Foo.cs, Foo.Designer.cs, Foo.axaml) unless renameFiles is false, and always its XAML's x:Class and " +
        "Razor component (Foo.razor, Foo.razor.css); an Aspire resource's name where its AppHost " +
        "declares it and its services read it; a XAML element's name with its code-behind's uses; a JSON property (in " +
        "package.json's scripts, every npm run of one); a compose file's names, its override's too. MSBuild files have none.")]
    public Task<string> Rename(string newName, string? path = null, int? line = null, int? column = null, string? symbol = null,
        bool renameFiles = true,
        CancellationToken cancel = default) => OnUi(cancel, () => Patience.RunAsync(Editors.Commands, async patience =>
    {
        CodePlace at = await AskedAt(path, line, column, symbol, cancel);
        Served served = await Serve(at.Path, cancel);
        int offset = Offset(served.Document, at.Line, at.Column);
        if (served is ServerServed { Web.Kind: WebServerKind.Yaml } yaml && ComposeDeclarations.IsCompose(yaml.Document.Path)
            && await ComposeAssist.ComposeRenamingAsync(yaml.Lsp, yaml.Document, offset) is { } composed)
        {
            if (composed.Name == newName)
            {
                return $"{newName} is its name already";
            }
            RefactorOutcome compose = await window.Assist.Languages.Compose.RenameComposeAsync(yaml.Document, offset, composed.Name, newName);
            if (compose.Problem is { } refused)
            {
                throw new InvalidOperationException(refused);
            }
            await SaveLandedAsync(cancel);
            return $"{compose.Said}: {string.Join(", ", compose.Files.Select(f => Relative(f.Path)))}";
        }
        switch (served)
        {
            case XamlServed xaml:
                return await XamlRename(xaml, offset, newName, cancel);
            case ServerServed json when !json.Provides("renameProvider"):
                return await JsonRename(json, offset, newName);
            case MSBuildServed:
                throw Lacks(served, "Rename", MSBuildHas);
        }
        ServerServed server = (ServerServed)served;
        string? resource = null;
        if (server.Web is null && Session.AspireFiles.RoleOf(served.Document.Path) != Aspire.AspireRole.None)
        {
            // Held on the name through what is typed while the server says whether it is a resource's.
            Anchor held = window.Assist.Actions.HeldAt(served.Document, offset);
            try
            {
                resource = await CSharpAssist.AspireResourceAtAsync(server.Lsp, offset);
                offset = held.Offset;
            }
            finally
            {
                held.Remove();
            }
        }
        if (resource == newName)
        {
            return $"{newName} is its name already";
        }
        if (resource is not null)
        {
            RefactorOutcome made = await window.Assist.Languages.CSharp.RenameResourceAsync(served.Document, offset, resource, newName, patience);
            if (made.Problem is { } problem)
            {
                throw new InvalidOperationException(problem);
            }
            await SaveLandedAsync(cancel);
            return $"{made.Said}: {string.Join(", ", made.Files.Select(f => Relative(f.Path)).Distinct())}";
        }
        RefactorOutcome renaming = await window.Assist.Actions.RenameSymbolAsync(served.Document, offset, newName, renameFiles, patience);
        if (renaming.Problem is { } notRenamed)
        {
            throw new InvalidOperationException(notRenamed);
        }
        if (renaming.Files.Count == 0 && renaming.Left.Count == 0)
        {
            return renaming.Said;
        }
        await SaveLandedAsync(cancel);
        List<string> changed = [.. renaming.Files.Where(f => f.Change == "changed").Select(f => Relative(f.Path))];
        List<string> moved = [.. renaming.Files.Where(f => f.Change == "moved").Select(f => $"{Relative(f.Path)} to {Relative(f.To!)}")];
        string renamed = moved.Count > 0 ? $"; renamed {string.Join(", ", moved)}" : string.Empty;
        string unrenamed = renaming.Left.Count > 0 ? $"; not in {string.Join(", ", renaming.Left)}, edited meanwhile" : string.Empty;
        return $"Renamed in {Plural.Of(changed.Count, "file")}: {string.Join(", ", changed)}{renamed}{unrenamed}";
    }, AgentCall.Say, cancel));

    [McpServerTool(Name = McpTools.CodeActions, Title = "Fixes and refactorings", ReadOnly = true), Description(
        "The fixes and refactorings offered at a position (Alt+Enter), or over a selection to endLine:endColumn (Extract " +
        "Method, Introduce Variable), as 'title [kind]' with the choices each offers (nested actions, a Fix All's scopes, where " +
        "a XAML resource goes), which apply_code_action takes: Passenger's own fixes first, then ESLint's, then the language " +
        "server's; in XAML, the line's problems' fixes (a binding's missing property or command, a missing converter), Extract " +
        "Resource and Extract Style. A Suppress or Configure action's choices are counted, and listed when title names it.")]
    public Task<IReadOnlyList<string>> CodeActions(string path, int line, int column, int? endLine = null, int? endColumn = null, string? title = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        Served served = await Serve(path, cancel);
        IReadOnlyList<(string Title, string Shown)> listed;
        switch (served)
        {
            case XamlServed xaml:
                listed = [.. (await XamlActions(xaml, Offset(xaml.Document, line, column)))
                    .Select(a => (a.Title, a.Choices.Count == 0 ? $"{a.Title} [{a.Kind}]" : $"{a.Title} [{a.Kind}], choices: {string.Join("; ", a.Choices)}"))];
                break;
            case MSBuildServed when window.Assist.OwnFixes(served.Document, Offset(served.Document, line, column)) is { Count: > 0 } own:
                listed = [.. own.Select(f => (f.Title, $"{f.Title} [quickfix]"))];
                break;
            case MSBuildServed:
                throw Lacks(served, "fixes or refactorings", MSBuildHas);
            default:
                int offset = Offset(served.Document, line, column);
                await AspireMarked(served, cancel);
                IReadOnlyList<EslintFix> linted = await LintFixes(served, offset, cancel);
                listed = [.. window.Assist.OwnFixes(served.Document, offset).Select(f => (f.Title, $"{f.Title} [quickfix]")),
                    .. linted.Select(f => (f.Title, f.Action.Kind is { Length: > 0 } kind ? $"{f.Title} [{kind}]" : f.Title)),
                    .. (await Actions((ServerServed)served, line, column, endLine, endColumn)).Select(a => (a.Title, Described(a, all: title is not null)))];
                break;
        }
        if (title is null)
        {
            return (IReadOnlyList<string>)[.. listed.Select(a => a.Shown)];
        }
        return listed.Where(a => a.Title == title).Select(a => a.Shown).ToList() is { Count: > 0 } named ? named
            : throw new InvalidOperationException($"No action '{title}' here; there are: {string.Join("; ", listed.Select(a => a.Title))}");
    });

    [McpServerTool(Name = McpTools.ApplyCodeAction, Title = "Apply a fix or refactoring", Destructive = true), Description(
        "Applies the action titled as code_actions lists it there, as Alt+Enter does, choice naming one of its choices, and " +
        "saves the files. name is XAML's Extract Resource or Extract Style's new key or style (else their dialog's), which " +
        "replace every use of the value, or an Aspire parameter's Set… value.")]
    public Task<string> ApplyCodeAction(string path, int line, int column, string title, int? endLine = null, int? endColumn = null,
        string? choice = null,
        string? name = null,
        CancellationToken cancel = default) => OnUi(cancel, () => Patience.RunAsync(Editors.Commands, async patience =>
    {
        Served served = await Serve(path, cancel);
        switch (served)
        {
            case XamlServed xaml:
                return await ApplyXamlAction(xaml, Offset(xaml.Document, line, column), title, choice, name);
            case MSBuildServed when window.Assist.OwnFixes(served.Document, Offset(served.Document, line, column)).FirstOrDefault(f => f.Title == title) is { } ownFix:
                return await ApplyOwnFix(served.Document, ownFix, patience, name);
            case MSBuildServed:
                throw Lacks(served, "fixes or refactorings", MSBuildHas);
        }
        ServerServed server = (ServerServed)served;
        int offset = Offset(served.Document, line, column);
        await AspireMarked(served, cancel);
        if (window.Assist.OwnFixes(served.Document, offset).FirstOrDefault(f => f.Title == title) is { } own)
        {
            return await ApplyOwnFix(served.Document, own, patience, name);
        }
        IReadOnlyList<EslintFix> linted = await LintFixes(served, offset, cancel);
        if (linted.FirstOrDefault(f => f.Title == title) is { } lint)
        {
            return await ApplyLintFix(server, lint);
        }
        // Listed now, with any edit the server gives them as it lists them; the open files as they were then.
        using Asked asked = WorkspaceEdits.Track(Editors);
        LspDocument lsp = server.Lsp;
        IReadOnlyList<CodeAction> actions = await Actions(server, line, column, endLine, endColumn);
        CodeAction chosen = actions.FirstOrDefault(a => a.Title == title)
            ?? throw new InvalidOperationException($"No action '{title}' here; there are: {string.Join("; ",
                [.. window.Assist.OwnFixes(served.Document, offset).Select(f => f.Title), .. linted.Select(f => f.Title), .. actions.Select(a => a.Title)])}");
        IReadOnlyList<ActionChoice> choices = ClientCommands.Choices(chosen);
        string? scope = null;
        if (choices.Count > 0)
        {
            ActionChoice taken = choices.FirstOrDefault(c => c.Text == choice)
                ?? throw new InvalidOperationException($"'{title}' needs a choice{(choice is null ? "" : $", and '{choice}' is not one")}: {string.Join("; ", choices.Select(c => c.Text))}");
            (chosen, scope) = (taken.Action, taken.Scope);
        }
        else if (choice is not null)
        {
            throw new InvalidOperationException($"'{title}' offers no choices");
        }
        string what = choice is null ? $"'{title}'" : $"'{title}' ({choice})";
        CodeAction resolved = await lsp.ResolveChosenAsync(chosen, scope);
        // Resolved again once the files come under the projects its edit reaches are in them, as the window's are.
        if (resolved.Edit is { } reaching && Session.CSharp is { IsReady: true } csharp)
        {
            (IReadOnlyList<(string Path, string Project)> left, bool waited) = await csharp.ReachedAsync(reaching, Patience.Limit, SayTakingIn, cancel);
            if (left.Count > 0)
            {
                throw new TimeoutException($"{what} was not applied: {NotTakenIn(left)}");
            }
            if (waited)
            {
                resolved = await (Session.CSharp?.For(served.Document)?.Lsp ?? lsp).ResolveChosenAsync(chosen, scope);
            }
        }
        chosen = resolved;
        if (chosen.Command is { } run && lsp.Runs(run))
        {
            return await ApplyByCommand(lsp, chosen, run, title, what, asked, patience, cancel);
        }
        string unrun = chosen.Command is { } command ? $", but not {command.Command}, which Passenger cannot run" : string.Empty;
        if (chosen.Edit is not { } edit)
        {
            return $"{what} changes nothing it can apply{unrun}";
        }
        if (await WorkspaceEdits.StaleAsync(Editors, lsp, edit, asked.Since, patience) is { } stale)
        {
            throw new InvalidOperationException($"{what} was not applied: {stale}");
        }
        List<string> dropped = [];
        int files;
        using (Passenger.App.Editors.CommandHistory.Recording recording = Editors.Commands.Record(title))
        {
            try
            {
                files = WorkspaceEdits.ApplyAsCommand(Editors, edit, recording, asked, dropped);
                await WorkspaceEdits.LandedAsync(recording, null);
            }
            catch (WorkspaceEditFailed failed)
            {
                throw new InvalidOperationException($"{what} was not applied: {failed.Message}");
            }
        }
        await SaveLandedAsync(cancel);
        return $"Applied {what} to {Plural.Of(files, "file")}{unrun}{(dropped.Count > 0 ? $", but not to {string.Join(", ", dropped)}, edited meanwhile" : string.Empty)}";
    }, AgentCall.Say, cancel));

    /// <summary>A code action whose change its server makes by its own command, made as the window makes it
    /// (<see cref="ServerCommands"/>), the files saved: which files it changed and made, and where it put a name to rename.</summary>
    private async Task<string> ApplyByCommand(LspDocument lsp, CodeAction chosen, LspCommand run, string title, string what, Asked asked,
        Patience patience, CancellationToken cancel)
    {
        RefactorOutcome made = await ServerCommands.RunAsync(Editors, title, what, chosen.Edit, taking => lsp.ExecuteCommandAsync(run, taking, cancel), asked, patience, null);
        if (made.Problem is not null)
        {
            throw new InvalidOperationException(made.Problem);
        }
        if (made.Files.Count == 0 && made.Left.Count == 0)
        {
            return $"{what} changed nothing";
        }
        await SaveLandedAsync(cancel);
        string named = made.RenameAt is { } place && Editors.Find(DocumentUris.ToPath(place.Uri)) is { } open
            ? $", naming what it made {open.Text.Snapshot.GetText(TextNavigation.WordAt(open.Text.Snapshot, ServerLines.Of(open.Text.Snapshot).OffsetOf(place.Range.Start)))} at {Relative(open.Path)}:{place.Range.Start.Line + 1}:{place.Range.Start.Character + 1}"
            : string.Empty;
        string changed = string.Join(", ", made.Files.Where(f => f.Change != "created").Select(f => Relative(f.Path)));
        string created = string.Join(", ", made.Files.Where(f => f.Change == "created").Select(f => Relative(f.Path)));
        string where = (changed.Length > 0 ? $" to {changed}" : string.Empty) + (created.Length > 0 ? $"{(changed.Length > 0 ? "," : string.Empty)} making {created}" : string.Empty);
        return $"Applied {what}{where}{named}{(made.Left.Count > 0 ? $", but not to {string.Join(", ", made.Left)}, edited meanwhile" : string.Empty)}";
    }

    /// <summary>A code action as code_actions lists it: its title, its kind, and the choices it offers, a Suppress or
    /// Configure action's (dozens: each scope, each severity) only counted unless <paramref name="all"/>.</summary>
    private static string Described(CodeAction action, bool all)
    {
        string described = action.Kind is { Length: > 0 } kind ? $"{action.Title} [{kind}]" : action.Title;
        IReadOnlyList<ActionChoice> choices = ClientCommands.Choices(action);
        return choices.Count == 0 ? described
            : !all && (action.Title.StartsWith("Suppress", StringComparison.Ordinal) || action.Title.StartsWith("Configure", StringComparison.Ordinal))
                ? $"{described}, {Plural.Of(choices.Count, "choice")}: title lists them"
                : $"{described}, choices: {string.Join("; ", choices.Select(c => c.Text))}";
    }

    [McpServerTool(Name = McpTools.Format, Title = "Reformat a file", Destructive = true), Description(
        "Reformats a file as Reformat Code does, and saves it: XAML by its .editorconfig, a web language by the project's " +
        "Prettier where it formats it, else the language server. MSBuild files have none.")]
    public Task<string> Format(string path, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        OpenDocument document;
        LspDocument? lsp = null;
        if (Passenger.App.Xaml.XamlService.IsXaml(Resolve(path)))
        {
            document = await DocumentAsync(path, cancel);
        }
        else
        {
            Served file = await Serve(path, cancel);
            ServerServed served = file as ServerServed ?? throw Lacks(file, "Reformat Code", MSBuildHas);
            (document, lsp) = (served.Document, served.Lsp);
        }
        CSharp.CodeActions.Reformatted done = await window.Assist.Actions.ReformatAsync(document, null, () => lsp)
            ?? throw new InvalidOperationException($"Reformat Code was not applied: {Relative(document.Path)} was closed as it was reformatted");
        return await SavedAsync(done, document, cancel);
    });

    /// <summary>A XAML file reformatted as the window's Reformat Code and Code Cleanup reformat it, then saved.</summary>
    private async Task<CSharp.CodeActions.Reformatted> FormatXamlAsync(OpenDocument markup, string what)
    {
        CSharp.CodeActions.Reformatted done = await window.Assist.Languages.Xaml.ReformatXamlAsync(markup, null, what);
        await SavedAsync(done, markup, CancellationToken.None);
        return done;
    }

    /// <summary>What a reformat came to, as an agent is told it, the file saved once it is applied and the file holds
    /// what is not saved; one not applied refused, saying why.</summary>
    private async Task<string> SavedAsync(CSharp.CodeActions.Reformatted done, OpenDocument document, CancellationToken cancel)
    {
        if (!done.Applied)
        {
            throw new InvalidOperationException(done.Said);
        }
        if (document.IsModified)
        {
            document.Save();
            await LandedAsync(cancel);
        }
        return done.Said;
    }

    [McpServerTool(Name = McpTools.FindSymbol, Title = "Go to symbol", ReadOnly = true), Description(
        "The solution's own C# and Razor symbols (not packages') whose names match query by substring, initials or camel " +
        "humps (\"GH\" finds GetHashCode), as Go to Symbol finds them, the name itself first, then names starting with it, " +
        "types before members; an indexer by this[] or Item. Other files' names are in their outline.")]
    public Task<IReadOnlyList<SymbolFound>> FindSymbol(
        string query,
        [Description("class, interface, struct, enum, method, property, field, event, constructor...")] string? kind = null,
        int limit = 50,
        [Description("Only the symbols query names exactly, as symbol does, each with its documentation id")] bool exact = false,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        RoslynSession session = await Server(cancel);
        // Go to Symbol finds an indexer by neither of its names, this[] or its metadata's Item: the exact find does.
        bool indexer = query.StartsWith("this[", StringComparison.Ordinal);
        if (exact || indexer)
        {
            return (IReadOnlyList<SymbolFound>)[.. (await NamedAsync(session, query, kind, limit, cancel)).Symbols.Select(Found)];
        }
        IReadOnlyList<SymbolFound> indexers = query == "Item" && kind is null or "property"
            ? [.. (await NamedAsync(session, "this[]", "property", limit, cancel, noneRefused: false)).Symbols.Select(Found)]
            : [];
        IReadOnlyList<SymbolInformation> found = await session.SymbolsAsync(query);
        // Each target framework finds a symbol again; the server's order is not by match, so the name itself first, which no limit cuts.
        List<SymbolInformation> kept = [.. found
            .Where(s => DocumentUris.GeneratedName(s.Location.Uri) is null && (kind is null || SymbolKinds.Name(s.Kind).Equals(kind, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(s => (s.Name, s.Kind, s.Location.Uri, s.Location.Range.Start.Line, s.Location.Range.Start.Character))
            .OrderBy(s => NameMatch(s.Name, query))
            .ThenBy(s => SymbolKinds.Name(s.Kind) is "class" or "interface" or "struct" or "enum" ? 0 : 1)
            .Take(Math.Max(0, limit - indexers.Count))];
        IReadOnlyList<Place> places = await Places([.. kept.Select(s => s.Location)], cancel);
        return (IReadOnlyList<SymbolFound>)[.. indexers,
            .. kept.Select((s, i) => new SymbolFound(s.Name, SymbolKinds.Name(s.Kind), s.Owner, places[i].Path, places[i].Line, places[i].Column))];

        static SymbolFound Found(NamedPlace n) => new(n.Name, n.Kind, n.Container, n.Path, n.Line, n.Column) { Id = n.Id };
    });

    /// <summary>How well a symbol's name matches a query: the name itself, then in another letter case, then one that starts
    /// with it, then one that has it, then Go to Symbol's other matches (initials, humps).</summary>
    private static int NameMatch(string name, string query) =>
        name == query ? 0
        : name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 1
        : name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 2
        : name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 3
        : 4;

    /// <summary>What a code tool asks about: a place an agent gave, or the one C# symbol it named (<see cref="NamedAsync"/>).</summary>
    private sealed record CodePlace(string Path, int Line, int Column);

    /// <summary>The place a code tool asks about: path, line and column, or where the one symbol a name names is declared;
    /// a name that names several is refused, listing each to tell them apart.</summary>
    private async Task<CodePlace> AskedAt(string? path, int? line, int? column, string? symbol, CancellationToken cancel)
    {
        if (symbol is null)
        {
            return path is not null && line is int l && column is int c ? new CodePlace(path, l, c)
                : throw new ArgumentException("Give the symbol's path, line and column, or symbol, its name");
        }
        if (path is not null || line is not null || column is not null)
        {
            throw new ArgumentException("Give symbol, a name, or path, line and column, a place: not both");
        }
        SymbolsNamed named = await NamedAsync(await Server(cancel), symbol, null, 10, cancel);
        return named.Symbols.Count == 1 ? new CodePlace(named.Symbols[0].Path, named.Symbols[0].Line, named.Symbols[0].Column)
            : throw new InvalidOperationException($"{symbol} names {Plural.Of(named.Symbols.Count + named.More, "symbol")} of the solution's own code: "
                + $"{string.Join("; ", named.Symbols.Select(n => n.ToString()))}{(named.More > 0 ? $"; and {named.More} more" : string.Empty)}. Name one by its container, "
                + "its parameters' types or its documentation id");
    }

    /// <summary>A symbol a name names, its first declaration's place in the editor's lines, as an agent is shown it.</summary>
    private sealed record NamedPlace(string Name, string Shown, string Kind, string? Id, string? Container, string Path, int Line, int Column)
    {
        public override string ToString() => $"{Shown} ({Kind}) at {Path}:{Line}:{Column}{(Id is null ? string.Empty : $", {Id}")}";
    }

    private sealed record SymbolsNamed(IReadOnlyList<NamedPlace> Symbols, int More);

    /// <summary>The solution's own C# symbols a name names exactly, of a kind when one is given, at most limit; none is
    /// refused, saying how to find a name like it, unless <paramref name="noneRefused"/> is false. A name none declares is
    /// looked for again once the C# files come under the folder's projects are in them: a file just written may declare it.</summary>
    private async Task<SymbolsNamed> NamedAsync(RoslynSession session, string name, string? kind, int limit, CancellationToken cancel, bool noneRefused = true)
    {
        Roslyn.Extension.SymbolsFound found = await session.SymbolsNamedAsync(name, kind is null ? limit : 1000, cancel);
        List<Roslyn.Extension.NamedSymbol> kept = [.. found.Symbols.Where(s => kind is null || s.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))];
        if (kept.Count == 0 && noneRefused && await TakenInAsync(null, cancel) && Session.CSharp?.Session is { } now)
        {
            found = await now.SymbolsNamedAsync(name, kind is null ? limit : 1000, cancel);
            kept = [.. found.Symbols.Where(s => kind is null || s.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))];
        }
        if (kept.Count == 0 && noneRefused)
        {
            throw new InvalidOperationException($"No {kind ?? "symbol"} of the solution's own code is named {name}: find_symbol finds names like it");
        }
        IReadOnlyList<Place> places = await Lines([.. kept.Select(s => (DocumentUris.FromPath(s.Path!), new LspPosition(s.At!.Line, s.At.Character), true))], cancel);
        List<NamedPlace> named = [.. kept.Select((s, i) => new NamedPlace(s.Name, s.Shown, s.Kind, s.Id, s.Container, places[i].Path, places[i].Line, places[i].Column))];
        return new SymbolsNamed([.. named.Take(limit)], Math.Max(0, named.Count - limit) + (kind is null ? found.More : 0));
    }

    /// <summary>symbol_info of a name: each symbol it names, with its kind, place and documentation id, then what its hover
    /// card says.</summary>
    private async Task<string> SymbolsInfo(string symbol, CancellationToken cancel)
    {
        SymbolsNamed named = await NamedAsync(await Server(cancel), symbol, null, 10, cancel);
        List<string> each = [];
        foreach (NamedPlace place in named.Symbols)
        {
            (OpenDocument document, _) = await Code(place.Path, cancel);
            each.Add($"{place}\n{await CardAsync(document, Offset(document, place.Line, place.Column), cancel)}".TrimEnd());
        }
        return string.Join("\n\n", each) + (named.More > 0 ? $"\n\nAnd {named.More} more: name one by its container, its parameters' types or its documentation id" : string.Empty);
    }

    /// <summary>What the hover card says of an offset of a file, as the languages' one order says it (<see
    /// cref="LanguageAssists.HoverAsync"/>), read as an agent reads it; empty where it says nothing.</summary>
    private async Task<string> CardAsync(OpenDocument document, int offset, CancellationToken cancel)
    {
        HoverWords card = new();
        await window.Assist.Languages.HoverAsync(card, document, offset, cancel);
        return card.Text;
    }

    /// <summary>A hover card's parts as an agent reads them: code and words as they are, a language server's made plain
    /// (<see cref="HoverText"/>) and set apart from the rest by a blank line.</summary>
    private sealed class HoverWords : IHoverCard
    {
        private readonly List<(string Text, bool Served)> parts = [];

        public string Text => string.Concat(parts.Select((p, i) => (i == 0 ? string.Empty : p.Served || parts[i - 1].Served ? "\n\n" : "\n") + p.Text));

        public void AddCode(string code) => Add(code, false);

        public void AddText(string text) => Add(text, false);

        public void AddHover(Hover hover) => Add(HoverText(hover), true);

        private void Add(string text, bool served)
        {
            if (text.Trim() is { Length: > 0 } said)
            {
                parts.Add((said, served));
            }
        }
    }

    /// <summary>A hover card's words as an agent reads them: Markdown made plain text, its code fences, backslash escapes,
    /// entities and line-break spaces taken out, the code in it kept as written, and no image's data.</summary>
    private static string HoverText(Hover hover) =>
        hover.Contents is { ValueKind: System.Text.Json.JsonValueKind.Object } content && content.TryGetProperty("kind", out System.Text.Json.JsonElement kind)
            && kind.ValueKind == System.Text.Json.JsonValueKind.String && kind.GetString() == "plaintext"
            ? DataUri().Replace(Shapes.Text(hover.Contents), string.Empty).Trim()
            : Plain(Shapes.Text(hover.Contents));

    /// <summary>A Markdown image (the CSS server's Baseline icon), whose words an agent has beside it and whose data it cannot read.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)[ \t]*")]
    private static partial System.Text.RegularExpressions.Regex MarkdownImage();

    /// <summary>A data URI's base64 bytes, as text no agent can read.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\bdata:[\w.+/-]*(?:;[\w.+=-]+)*;base64,[A-Za-z0-9+/=]+")]
    private static partial System.Text.RegularExpressions.Regex DataUri();

    /// <summary>Markdown as plain text, a run of blank lines kept as one (<see cref="HoverText"/>).</summary>
    private static string Plain(string markdown)
    {
        System.Text.StringBuilder text = new();
        bool fenced = false;
        bool gap = false;
        foreach (string raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }
            string plain = fenced ? line : Unescaped(DataUri().Replace(MarkdownImage().Replace(line, string.Empty), string.Empty));
            if (plain.Trim().Length == 0)
            {
                gap = text.Length > 0;
                continue;
            }
            text.Append(gap ? "\n" : string.Empty).Append(plain).Append('\n');
            gap = false;
        }
        return text.ToString().TrimEnd();

        // Outside a code span, a backslash before punctuation and an entity are Markdown's, not the text's.
        static string Unescaped(string line)
        {
            System.Text.StringBuilder plain = new(line.Length);
            bool code = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '`')
                {
                    code = !code;
                }
                else if (!code && c == '\\' && i + 1 < line.Length && char.IsAscii(line[i + 1]) && (char.IsPunctuation(line[i + 1]) || char.IsSymbol(line[i + 1])))
                {
                    plain.Append(line[++i]);
                    continue;
                }
                else if (!code && c == '&' && Entities.FirstOrDefault(e => string.CompareOrdinal(line, i, e.Written, 0, e.Written.Length) == 0) is { Written: { } written } entity)
                {
                    plain.Append(entity.Meant);
                    i += written.Length - 1;
                    continue;
                }
                plain.Append(c);
            }
            return plain.ToString();
        }
    }

    private static readonly (string Written, char Meant)[] Entities = [("&nbsp;", ' '), ("&lt;", '<'), ("&gt;", '>'), ("&amp;", '&'), ("&quot;", '"'), ("&#39;", '\'')];

    /// <summary>Usages in the order an agent reads them: by file, then line and column, a source generator's files (their
    /// copies, <see cref="GeneratedCode"/>) last.</summary>
    private static IReadOnlyList<Place> Sorted(IReadOnlyList<Place> places) =>
        [.. places.OrderBy(p => (p.Text.Length == 0 && p.Path.EndsWith(" (generated)", StringComparison.Ordinal)) || GeneratedCode.IsCopy(p.Path)).ThenBy(p => p.Path, StringComparer.Ordinal)
            .ThenBy(p => p.Line).ThenBy(p => p.Column)];

    /// <summary>The offset a C# code tool asks about: the place given when a symbol is there (its hover says what one), else
    /// the one its line declares (<see cref="DeclaredOn"/>).</summary>
    private async Task<int> SymbolOffset(ServerServed served, int line, int column, int offset) =>
        served.Web is not null || await served.Lsp.HoverAsync(offset) is not null ? offset : await DeclaredOn(served, line, column);

    /// <summary>The offset of the one symbol a C# line declares, for a place on it where no symbol is (a column off the
    /// name, the line's start), else why there is none to ask about.</summary>
    private async Task<int> DeclaredOn(ServerServed served, int line, int column)
    {
        TextSnapshot text = served.Lsp.Text.Snapshot;
        List<DocumentSymbol> declared = [];
        Gather(await served.Lsp.OutlineAsync());
        if (declared.Count == 1)
        {
            return served.Lsp.OffsetOf(declared[0].SelectionRange.Start);
        }
        string place = $"{Relative(served.Document.Path)}:{line}:{column}";
        throw new InvalidOperationException(declared.Count == 0
            ? $"No symbol at {place}, and line {line} declares none: give the line and column of a name, or symbol, the name"
            : $"No symbol at {place}, and line {line} declares {string.Join(" and ", declared.Select(d => d.Name))}: give the column of the one meant");

        void Gather(IReadOnlyList<DocumentSymbol> symbols)
        {
            foreach (DocumentSymbol symbol in symbols)
            {
                if (ServerPlaces.PositionOf(text, symbol.SelectionRange.Start).Line == line - 1)
                {
                    declared.Add(symbol);
                }
                Gather(symbol.Children ?? []);
            }
        }
    }

    [McpServerTool(Name = McpTools.Outline, Title = "A file's structure", ReadOnly = true), Description(
        "A file's structure, indented, each part with its line: C#'s types and members, a web language's symbols, XAML's " +
        "elements with what names them (x:Class, x:Name, x:Key, a style's selector), an MSBuild file's properties with their " +
        "values, items, targets and imports.")]
    public Task<string> Outline(string path, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        Served served = await Serve(path, cancel);
        switch (served)
        {
            case XamlServed xaml:
                return XamlOutline(xaml);
            case MSBuildServed msbuild:
                return MSBuildOutline(msbuild);
        }
        LspDocument lsp = ((ServerServed)served).Lsp;
        System.Text.StringBuilder text = new();
        IReadOnlyList<DocumentSymbol> outline = await lsp.OutlineAsync();
        TextSnapshot answered = lsp.Text.Snapshot;
        Write(outline, 0);
        return text.Length == 0 ? "Nothing is declared in this file" : text.ToString().TrimEnd();

        void Write(IReadOnlyList<DocumentSymbol> symbols, int depth)
        {
            foreach (DocumentSymbol symbol in symbols)
            {
                text.Append(' ', depth * 2).Append(symbol.Name);
                if (!string.IsNullOrEmpty(symbol.Detail) && symbol.Detail != symbol.Name)
                {
                    text.Append(" : ").Append(symbol.Detail);
                }
                text.Append(" (").Append(SymbolKinds.Name(symbol.Kind)).Append(", line ").Append(ServerPlaces.PositionOf(answered, symbol.SelectionRange.Start).Line + 1).Append(")\n");
                Write(symbol.Children ?? [], depth + 1);
            }
        }
    });

    // Build and run

    // Publish is build's, as the Build window's: a tool of its own cost every session's list more than the budget has.
    [McpServerTool(Name = McpTools.Build, Title = "Build", Destructive = true), Description(
        "Builds the solution (or the folder's project); with publish (a project), publishes it with profile, else its last. " +
        "Returns whether it succeeded, how it ended, every error and warning, the folder published to, and MSBuild's last " +
        "lines when it failed (output of build has them all).")]
    public Task<BuildResult> Build(string? publish = null, string? profile = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (publish is null && profile is not null)
        {
            throw new ArgumentException($"{profile} is a publish profile: give publish the project it is one of");
        }
        ProjectModel? published = publish is null ? null : Project(publish);
        AgentCall.Follow(window.Actions.Work, published is null ? AppActions.Build : AppActions.Publish);
        // The build or publish goes on without an agent that gives up on it, shown in the Build window.
        BuildOutcome built = await (published is null ? Builds.BuildAsync(BuildCause.Explicit, CancellationToken.None)
            : Builds.PublishAsync(new PublishRequest(published.Path, profile ?? Session.Publishing.ProfileOf(published.Path)))).WaitAsync(cancel);
        IReadOnlyList<Finding> problems = [.. built.Problems.Select(p => new Finding(
            p.Severity == ProblemSeverity.Error ? "error" : "warning", p.Code, p.Message, p.Path is { } at ? Relative(at) : p.Project, p.Position.Line + 1, p.Position.Column + 1))];
        return new BuildResult(built.Succeeded, built.Message, problems.Count(p => p.Severity == "error"), problems.Count(p => p.Severity == "warning"), problems,
            built.Succeeded ? null : string.Join('\n', built.Output)) { Folder = built.Published };
    });

    [McpServerTool(Name = McpTools.Run, Title = "Run a configuration", Destructive = true), Description(
        "Runs a configuration as Run does, built first when it says so, and waits for it to end, an Avalonia or WPF app's " +
        "UI or an AppHost's resources to be up. Returns its output so far and its exit code once it has one; a run refused (one " +
        "running, a failed build with its errors) is an error. A terminal configuration runs in a Terminal tab (terminal_run " +
        "types into it); a container configuration answers once its services are up, with each one's state. With resource, " +
        "that one alone, its configuration started if no run has it.")]
    public Task<RunResult> Run(
        string? configuration = null,
        string? arguments = null,
        int waitSeconds = 30,
        [Description("Under dotnet watch (Settings' choice by default), where hot_reload or a save applies edits")] bool? hotReload = null,
        string? resource = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (resource is not null)
        {
            if (hotReload is not null)
            {
                throw new ArgumentException("resource uses Run Without Debugger; omit hotReload");
            }
            if (await ComposeTargetAsync(resource, configuration, starts: true, cancel) is { } service)
            {
                return arguments is null ? await RunComposeServiceAsync(service, waitSeconds, cancel)
                    : throw new ArgumentException("A compose service runs as its compose files say; omit arguments");
            }
            AgentCall.Follow(window.Actions.Work, AppActions.RunProgram, AppActions.Build);
            using LaunchBuild resourceBuild = new(Builds);
            (Aspire.AspireSelection target, bool started) = await PrepareAspireResource(resource, configuration, RunStart.Run, arguments, cancel);
            if (!started && await window.RunAspireToolResourceAsync(target, "run", cancel) is { } problem)
            {
                throw new InvalidOperationException(WithBuildErrors(problem, resourceBuild.Ended));
            }
            return await AspireResourceUpAsync(target, waitSeconds, cancel);
        }
        Choose(configuration);
        string name = Session.Configurations.Chosen?.Name ?? string.Empty;
        BuildRunController builds = Builds;
        AgentCall.Follow(window.Actions.Work, AppActions.RunProgram, AppActions.Build);
        using (LaunchBuild built = new(builds))
        {
            if (await Started(builds.RunAsync(arguments, hotReload), AppActions.RunProgram, cancel) is { } refused)
            {
                throw new InvalidOperationException(WithBuildErrors(refused, built.Ended));
            }
        }
        if (builds.Containers is { } running)
        {
            // Its services are up once Run answers: what an agent wants is how each stands and what they said.
            return new RunResult(true, null, await ServicesAsync(running, cancel), Tail(Session.Runner.Shown(), 200));
        }
        Task<int> ended = builds.RunEnd;
        LiveTree.LiveApp? app = Session.RunApp;
        if (Session.Configurations.Chosen is { } host && Session.Aspire.IsAspire(host) && Session.Aspire.FindApp(host.Project) is { } aspireApp)
        {
            AgentCall.Say($"Running {name}: waiting up to {Math.Max(0, waitSeconds)} s for it to end or its resources to be up");
            await AppHostUpAsync(aspireApp, TimeSpan.FromSeconds(Math.Max(0, waitSeconds)), cancel);
        }
        else
        {
            AgentCall.Say($"Running {name}: waiting up to {Math.Max(0, waitSeconds)} s for it to end or its UI to be up");
            // An app with a window never ends by itself: the wait is over once its UI is up, which the app_ tools look at.
            await When(() => ended.IsCompleted || app is { State: not LiveTree.LiveAppState.Waiting }, TimeSpan.FromSeconds(Math.Max(0, waitSeconds)), cancel,
                Ends(ended), app is null ? _ => () => { } : On(h => app.StateChanged += h, h => app.StateChanged -= h));
        }
        int? code = ended.IsCompletedSuccessfully ? ended.Result : null;
        string output = RunOutput(200);
        string status = code is int exit ? BuildRunController.RunEnded(name, exit)
            : app is { State: LiveTree.LiveAppState.Connected } ? $"Running {name}, its UI up: app_windows, app_screenshot and app_click look at it and use it"
            : $"Running {name}";
        // What an Aspire run said of itself as it started (its HTTPS over HTTP).
        if (Session.Configurations.Chosen is { } chosen && Session.Aspire.IsAspire(chosen) && Session.Aspire.FindApp(chosen.Project) is { } aspire)
        {
            status += string.Concat(aspire.Notes.Select(n => $"\nNote: {n}"));
        }
        return new RunResult(code is null, code, status, output);
    });

    [McpServerTool(Name = McpTools.Output, Title = "A console's last lines", ReadOnly = true), Description(
        "The last lines of a console. of run (the default): what Run's program wrote (under Hot Reload, dotnet watch's words " +
        "too) or a terminal configuration's tab; with resource, that one's own; with requests, the running web apps' requests " +
        "and their SQL. of debug (the default with session): the debugged program's. of build: the last build's MSBuild " +
        "output, as it runs too. of terminal: the IDE terminal's tab shown, its scrollback and screen (a full-screen program's " +
        "too).")]
    public Task<string> Output(string? of = null, int lines = 200, string? resource = null, bool requests = false, string? session = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (lines < 1)
        {
            throw new ArgumentException($"lines is {lines}: a console's last lines are 1 or more");
        }
        switch (of ?? (session is null ? "run" : "debug"))
        {
            case "build":
                return Tail(Session.Builder.Shown(), lines);
            case "debug":
                return DebugOutput(lines, session);
            case "terminal":
                return window.TerminalPanel.Active is { } shell ? Tail(shell, lines) : throw new InvalidOperationException("The terminal has no shell running");
            case not "run":
                throw new ArgumentException($"No console '{of}'; there are run, debug, build and terminal", nameof(of));
        }
        if (requests)
        {
            return await window.RequestsForAgentAsync(lines, cancel);
        }
        if (resource is not null)
        {
            if (await ComposeTargetAsync(resource, null, starts: false, cancel) is { } compose)
            {
                return await ComposeLogAsync(Builds.Containers!, compose.Service, lines, cancel);
            }
            Aspire.AspireSelection target = (await window.ResolveOwnedAspireResourceAsync(resource, null, cancel))!;
            Aspire.AspireResourceRun service = Session.Aspire.FindService(target)
                ?? throw new InvalidOperationException($"{resource} is no .NET service Passenger runs for its AppHost; telemetry reads its logs");
            service.Console.Flush();
            return Tail(service.Console.Document.Snapshot, lines);
        }
        if (Session.Configurations.Chosen is { } configuration && Session.Aspire.IsAspire(configuration)
            && (Session.Aspire.FindApp(configuration.Project)?.Console ?? Session.Aspire.LastAppConsole) is { } console)
        {
            console.Flush();
            return Tail(console.Document.Snapshot, lines);
        }
        return Builds.Containers is { } running ? $"{await ServicesAsync(running, cancel)}\n{RunOutput(lines)}" : RunOutput(lines);
    });

    /// <summary>A container run's services as the engine has them now, one line each as a resource's run says them
    /// (<see cref="ResourceAnswer"/>): its state, health and ports.</summary>
    private static async Task<string> ServicesAsync(global::Passenger.App.Containers.ContainerRun run, CancellationToken cancel)
    {
        try
        {
            IReadOnlyList<global::Passenger.App.Containers.ServiceState> states = await run.StatesAsync(cancel);
            return $"Running {run.Plan.Name} (Compose project {run.Plan.ProjectName}):\n" + string.Join('\n', ServiceStandings(run, states).Select(s => $"  {s}"))
                + string.Concat(run.Notes.Select(n => $"\nNote: {n}"));
        }
        catch (Exception failed) when (failed is global::Passenger.App.Containers.ContainerCommandException or TimeoutException or System.Text.Json.JsonException)
        {
            return $"Running {run.Plan.Name}: the engine did not say how its services stand ({failed.Message})";
        }
    }

    [McpServerTool(Name = McpTools.Stop, Title = "Stop", Destructive = false), Description("Stops whatever runs or is debugged, as Stop All does, a " +
        "container run's containers taken down; with session or resource, that one alone. With detach, lets go of the program " +
        "the debugger attached to instead, as Detach does: its breakpoints are taken out and it runs on.")]
    public Task<string> Stop(string? session = null, string? resource = null, bool detach = false, CancellationToken cancel = default) => detach
        ? resource is null ? DebugDetach(session, cancel) : throw new ModelContextProtocol.McpException("Detach lets go of a debugged program: name its session, not a resource")
        : OnUi(cancel, async () =>
    {
        if (resource is not null)
        {
            if (session is not null)
            {
                throw new ArgumentException("Name session or resource, not both");
            }
            if (await ComposeTargetAsync(resource, null, starts: false, cancel) is { Service: { } service })
            {
                global::Passenger.App.Containers.ContainerRow row = await NamedContainer($"{Builds.Containers!.Plan.ProjectName}/{service}", cancel);
                if (await window.ContainerActAsync(AppActions.ContainerStop, "stop", row) is { } refused)
                {
                    throw new InvalidOperationException(refused);
                }
                return $"Stopped {service}";
            }
            (Aspire.AspireSelection target, _) = await PrepareAspireResource(resource, null, null, null, cancel);
            if (await window.RunAspireToolResourceAsync(target, "stop", cancel) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }
            return $"Stopped {target.AppHostPath}::{target.Resource.Name}";
        }
        if (session is not null)
        {
            ToolWindows.DebugTab tab = TabFor(session)!;
            await Session.Debug.StopAsync(tab);
            return $"Stopped {tab.Name}";
        }
        await Builds.StopAllAsync().WaitAsync(cancel);
        return "Stopped";
    });

    // Unit tests

    [McpServerTool(Name = McpTools.TestsRun, Title = "Run unit tests", Destructive = true), Description(
        "Builds and runs the unit tests, or those whose full name contains filter, as the Unit Tests window does, each project " +
        "by the runner its build asks for (Microsoft Testing Platform for xUnit v3, MSTest's runner and TUnit; VSTest for the " +
        "rest). With debug, under the debugger: a stop in a test comes back, and debug_continue goes on. Returns the tally and " +
        "every failure with its message and stack trace.")]
    public Task<TestRunInfo> TestsRun(
        string? filter = null,
        bool debug = false, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        string folder = Session.Folder ?? throw new InvalidOperationException(Refusals.NoFolder);
        Testing.TestController tests = Session.Tests;
        AgentCall.Say(filter is null ? "Building and running every test" : $"Building and running the tests whose names contain '{filter}'");
        AgentCall.Follow(window.Actions.Work, AppActions.Build);
        using LaunchBuild built = new(Builds);
        // Matched once built: a test written a moment ago, or one only a runner finds, is among them.
        Task<string?> run = filter is null ? tests.RunAsync(folder, null, debug)
            : tests.RunMatchingAsync(folder, m => m.FullName is { } name && name.Contains(filter, StringComparison.Ordinal), $"No test's name contains '{filter}'", debug);
        if (debug)
        {
            // A debugged test's stop comes back to the agent; looked at every 50 ms: no pause of the test host is told.
            await Until(() => run.IsCompleted || Session.Debug.Session is { State: DebugState.Paused }, TimeSpan.FromMinutes(5), cancel);
            if (!run.IsCompleted)
            {
                await StopReadAsync(Session.Debug.Session, cancel);
                DebugSnapshot stopped = await DebugSnapshotAsync();
                // The run is under way: no tally of its own yet, and the last run's is another's.
                return new TestRunInfo($"Running under the debugger, paused{(stopped.Location is { } at ? $" at {at}" : string.Empty)}: debug_continue goes on", [], stopped);
            }
        }
        if (await run.WaitAsync(cancel) is { } problem)
        {
            throw new InvalidOperationException(WithBuildErrors(problem, built.Ended));
        }
        return TestInfo(tests);
    });

    [McpServerTool(Name = McpTools.TestsList, Title = "The unit tests", ReadOnly = true), Description(
        "The tests the Unit Tests window shows (from code, and their runner once built), each with its full name, state from " +
        "the last run and file:line: those whose full name contains filter and whose state is state, at most limit.")]
    public Task<IReadOnlyList<TestInfoRow>> TestsList(
        [Description("Part of a test's full name (Namespace.Class.Method)")] string? filter = null,
        [Description("passed, failed, skipped or notRun")] string? state = null,
        int limit = 200,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        int named = state is null ? -1 : Array.FindIndex(TestStates, s => s.Name.Equals(state, StringComparison.OrdinalIgnoreCase));
        Testing.TestState? only = state is null ? null
            : named >= 0 ? TestStates[named].State
            : throw new ArgumentException($"No state '{state}'; there are passed, failed, skipped and notRun");
        // Found from code as the folder opens; looked at every 50 ms, as the finder raises nothing as it is done.
        await Until(() => !Session.Tests.IsLooking, TestsLookLimit, cancel);
        return (IReadOnlyList<TestInfoRow>)[.. Session.Tests.Tree.Methods
            .Where(m => (filter is null || (m.FullName ?? m.Name).Contains(filter, StringComparison.Ordinal)) && (only is null || m.State == only))
            .Take(Math.Max(1, limit))
            .Select(m => new TestInfoRow(TestName(m), StateName(m.State), m.Path is { } at ? $"{Relative(at)}:{m.Line}" : null))];
    });

    /// <summary>The states tests_list says and takes, in its description's words.</summary>
    private static readonly (Testing.TestState State, string Name)[] TestStates =
        [(Testing.TestState.Passed, "passed"), (Testing.TestState.Failed, "failed"), (Testing.TestState.Skipped, "skipped"), (Testing.TestState.NotRun, "notRun")];

    /// <summary>A test's state as tests_list says it: one of <see cref="TestStates"/>, else running.</summary>
    private static string StateName(Testing.TestState state) => Array.FindIndex(TestStates, s => s.State == state) is int at and >= 0 ? TestStates[at].Name : "running";

    /// <summary>How long tests_list waits for the tests to be found from the test projects' code.</summary>
    private static readonly TimeSpan TestsLookLimit = TimeSpan.FromSeconds(60);

    /// <summary>The last run's tally and its failures, of the tests it ran alone as it told them (a test renamed as it ran
    /// still told), a method's results together (its failed rows, the rest counted), each message and stack whole and its
    /// output's last lines.</summary>
    private TestRunInfo TestInfo(Testing.TestController tests)
    {
        List<TestFailure> failures = [];
        foreach (IGrouping<(string, string, string?), Testing.TestOutcome> method in tests.RunOutcomes
            .GroupBy(o => (o.Test.Project, o.Test.Method, o.Test.Framework)).Where(m => m.Any(o => o.State == Testing.TestState.Failed)))
        {
            Testing.TestItem test = method.First().Test;
            Testing.TestNode? shown = tests.Tree.Node(test) is { } node ? node.Kind == Testing.TestNodeKind.Row ? node.Parent : node : null;
            bool several = shown?.Framework is not null || tests.RunOutcomes.Any(o => o.Test.Project == test.Project && o.Test.Method == test.Method && o.Test.Framework != test.Framework);
            string? place = shown?.Path is { } at ? $"{Relative(at)}:{shown.Line}" : test.Path is { } found ? $"{Relative(found)}:{test.Line}" : null;
            failures.Add(new TestFailure(shown is null ? $"{test.Method}{(several ? $" ({test.Framework})" : string.Empty)}" : TestName(shown), place,
                Testing.TestNode.FailuresOf([.. method], FailureOutputLines)));
        }
        return new TestRunInfo(tests.Summary, failures);
    }

    /// <summary>How many of a failure's last lines of output an answer gives: an agent's answer holds no test's every line.</summary>
    private const int FailureOutputLines = 50;

    /// <summary>A test's full name, Namespace.Class.Method, and the target framework it ran under when its project has several.</summary>
    private static string TestName(Testing.TestNode method)
    {
        string name = method.FullName ?? method.Name;
        return method.Framework is { } framework ? $"{name} ({framework})" : name;
    }

    // The debugger

    [McpServerTool(Name = McpTools.BreakpointSet, Title = "Set a breakpoint", Destructive = true), Description(
        "Sets, changes or with remove takes away a breakpoint: on a line (path, line; in .axaml where the line's first element " +
        "or attribute is made or set, or attribute's property, as written; in C#, at the statement the line begins, or with " +
        "column inside the lambda there, each time it runs); on an exception type (its full name, or \"Any " +
        "exception\", which remove only turns off), in its scenarios (all by default); XAML Binding Errors (bindingErrors: a " +
        "debugged app stops as a binding fails, $bindingError giving Control, Property, Path, FailedPart, DataContext, Message " +
        "and Xaml); or Break When Value Changes on control's property (as app_details names it) in the app debugged, for the " +
        "session, condition reading newValue, oldValue and control. condition is a C# bool; hitCondition \"==3\", \">=2\" or " +
        "\"%2\"; logMessage (\"count is {count}\") is written each hit; suspend false makes a tracepoint. Bound at once while " +
        "debugging.")]
    public Task<IReadOnlyList<BreakpointInfo>> SetBreakpoint(
        string? path = null,
        int? line = null,
        string? attribute = null,
        int? column = null,
        string? exception = null,
        bool bindingErrors = false,
        string? control = null,
        string? property = null,
        string? condition = null,
        string? hitCondition = null,
        string? logMessage = null,
        bool suspend = true,
        [Description("thrownInUserCode, thrownInExternalCode, handledInUserCode, handledInExternalCode, unhandled: it stops when " +
            "one of each, thrown and handled, holds")] IReadOnlyList<string>? scenarios = null,
        bool remove = false,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (remove)
        {
            return await RemoveBreakpoint(path, line, attribute, column, exception, bindingErrors, control, property, cancel);
        }
        BreakpointStore store = Session.Debug.Breakpoints;
        if (bindingErrors)
        {
            store.Update(store.BindingErrors, enabled: true, suspend, condition, hitCondition, logMessage);
            await InTheProgram(cancel);
            return ExceptionBreakpoints();
        }
        if (control is { Length: > 0 } || property is { Length: > 0 })
        {
            (DebugSession debugged, long element, string described, string name) = await WatchedProperty(control, property, cancel);
            PropertyBreakpoint watched = store.AddProperty(debugged, element, described, name);
            store.Update(watched, enabled: true, suspend, condition, hitCondition, logMessage);
            await InTheApp(cancel);
            return DataBreakpoints();
        }
        if (exception is { Length: > 0 })
        {
            ExceptionBreakpoint breakpoint = store.FindException(exception) ?? store.AddException(exception);
            if (scenarios is not null)
            {
                store.SetScenarios(breakpoint, ExceptionScenarioNames.Parse(scenarios));
            }
            store.Update(breakpoint, enabled: true, suspend, condition, hitCondition, logMessage);
            await InTheProgram(cancel);
            return ExceptionBreakpoints();
        }
        (string full, int at) = LinePlace(path, line);
        TextSnapshot text = (Editors.Find(full) is { HoldsText: true } open ? open.Text.Snapshot : await BackgroundWork.Run(() => Passenger.App.Editors.EditorArea.Read(full)))
            ?? throw new ArgumentException(File.Exists(full) ? $"{Relative(full)} has no text to set a breakpoint in" : $"No file {Relative(full)} is here to set a breakpoint in");
        LineIn(text, full, at + 1);
        LineBreakpoint set = column is int inside ? (await LambdaBreakpointAsync(full, at, inside, set: true))! : store.Set(full, at, attribute is { Length: > 0 } ? attribute : null);
        store.Update(set, enabled: true, suspend, condition, hitCondition, logMessage);
        await InTheProgram(cancel);
        return Breakpoints(full);
    });

    /// <summary>breakpoint_set's remove: the breakpoint its other arguments name taken away, or for "Any exception" and
    /// XAML Binding Errors turned off.</summary>
    private async Task<IReadOnlyList<BreakpointInfo>> RemoveBreakpoint(string? path, int? line, string? attribute, int? column, string? exception, bool bindingErrors,
        string? control, string? property, CancellationToken cancel)
    {
        BreakpointStore store = Session.Debug.Breakpoints;
        if (control is { Length: > 0 } || property is { Length: > 0 })
        {
            if (control is not { Length: > 0 } || property is not { Length: > 0 })
            {
                throw new ArgumentException("A property breakpoint is named by its control and its property, both");
            }
            string wanted = property.Trim();
            // By the id breakpoints shows, else the element the query finds now (the app paused answers no query).
            DebugSession? app = DebugApp() is { } live ? Session.Debug.SessionOf(live) : null;
            PropertyBreakpoint? watched = store.Properties.FirstOrDefault(p => (app is null || p.Session == app) && Id(p.Element) == control.Trim()
                && p.Property.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (watched is null)
            {
                (DebugSession debugged, long element, _, _) = await WatchedElement(control, cancel);
                watched = store.Properties.FirstOrDefault(p => p.Session == debugged && p.Element == element && p.Property.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No property breakpoint is on {wanted} of {control}; debug_state of breakpoints lists those there are");
            }
            store.Remove(watched);
            await InTheApp(cancel);
            return DataBreakpoints();
        }
        if (bindingErrors)
        {
            BindingErrorBreakpoint failures = store.BindingErrors;
            store.Update(failures, enabled: false, failures.Suspend, failures.Condition, failures.HitCondition, failures.LogMessage);
            await InTheProgram(cancel);
            return ExceptionBreakpoints();
        }
        if (exception is { Length: > 0 })
        {
            if (store.FindException(exception) is { IsAny: true } any)
            {
                store.Update(any, enabled: false, any.Suspend, any.Condition, any.HitCondition, any.LogMessage);
            }
            else if (store.FindException(exception) is { } type)
            {
                store.Remove(type);
            }
            await InTheProgram(cancel);
            return ExceptionBreakpoints();
        }
        (string full, int at) = LinePlace(path, line);
        string? on = attribute is { Length: > 0 } ? attribute : null;
        if ((column is int inside ? await LambdaBreakpointAsync(full, at, inside, set: false) : store.In(full).FirstOrDefault(b => b.Line == at && b.Attribute == on && b.Column is null)) is { } set)
        {
            store.Remove(set);
        }
        await InTheProgram(cancel);
        return Breakpoints(full);
    }

    /// <summary>The breakpoint inside the C# lambda holding a column (from one) of a line (from zero), set if
    /// <paramref name="set"/> says to, else null with none there; refused where no lambda written after the line's code
    /// begins holds that column.</summary>
    private async Task<LineBreakpoint?> LambdaBreakpointAsync(string path, int line, int column, bool set)
    {
        Passenger.Languages.CSharp.ParsedText parsed = await LambdaBreakpoints.ParsedAsync(Editors, path)
            ?? throw new ArgumentException($"A column names a lambda's breakpoint in a C# file, which {Relative(path)} is not");
        if (line >= parsed.Text.LineCount)
        {
            throw new ArgumentException($"{Relative(path)} has {parsed.Text.LineCount} lines");
        }
        LineSpan span = parsed.Text.GetLine(line);
        if ((column >= 1 ? LambdaBreakpoints.Of(parsed, line, column - 1) : null) is not { } lambda)
        {
            IReadOnlyList<LambdaBreakpoints.Lambda> lambdas = LambdaBreakpoints.On(parsed, line);
            throw new ArgumentException(lambdas.Count == 0
                ? $"Line {line + 1} holds no lambda after its code begins: a breakpoint on the line, with no column, stops there"
                : $"No lambda holds column {column} of line {line + 1}: its lambdas are at columns {string.Join(", ", lambdas.Select(l => $"{l.Range.Start - span.Start + 1}-{Math.Min(l.Range.End, span.End) - span.Start}"))}");
        }
        BreakpointStore store = Session.Debug.Breakpoints;
        return LambdaBreakpoints.SetOn(store.In(path), parsed, lambda) ?? (set ? store.Set(path, line, column: lambda.Body - span.Start) : null);
    }

    /// <summary>The element of the debugged app a query names, as a property breakpoint describes it (Type#Name), with its
    /// details' properties and the session debugging the app.</summary>
    private async Task<(DebugSession Session, long Element, string Described, IReadOnlyList<Passenger.Inspector.PropertyValue> Properties)> WatchedElement(string control,
        CancellationToken cancel)
    {
        if (DebugApp() is null)
        {
            throw new InvalidOperationException(Session.Debug.IsDebugging
                ? $"A property breakpoint watches an element of the app being debugged, and what is being debugged is not {Inspectable}"
                : "A property breakpoint watches an element of the app being debugged: debug_start one first");
        }
        LiveTree.LiveApp live = await InspectedAsync("debug", cancel);
        DebugSession debugged = Session.Debug.SessionOf(live) ?? throw new InvalidOperationException("A property breakpoint watches an element of the app being debugged: debug_start one first");
        (List<LiveTree.AppNode> nodes, _) = await TreeOf(live, null, cancel);
        Passenger.Inspector.Element element = LiveTree.AppQuery.One(nodes, control).Element;
        Passenger.Inspector.ElementDetails details = await live.DetailsAsync(element.Id);
        return (debugged, element.Id, details.Name is { Length: > 0 } name ? $"{details.Type}#{name}" : details.Type, details.Properties);
    }

    /// <summary>A property breakpoint's element and property, the property by the name the element's details list it under.</summary>
    private async Task<(DebugSession Session, long Element, string Described, string Property)> WatchedProperty(string? control, string? property, CancellationToken cancel)
    {
        if (control is not { Length: > 0 } || property is not { Length: > 0 })
        {
            throw new ArgumentException("A property breakpoint needs both control, the element, and property, the property of it to watch");
        }
        (DebugSession debugged, long element, string described, IReadOnlyList<Passenger.Inspector.PropertyValue> properties) = await WatchedElement(control, cancel);
        string wanted = property.Trim();
        Passenger.Inspector.PropertyValue found = properties.FirstOrDefault(p => p.Name == wanted)
            ?? properties.FirstOrDefault(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"{described} has no property {wanted}; app_details lists the properties it has");
        return (debugged, element, described, found.Name);
    }

    /// <summary>Waits for the debugger and the inspector in the app to take a property breakpoint's change, so the agent's next
    /// click or key finds the property watched; while the app is paused, the inspector watches it once the app runs.</summary>
    private async Task InTheApp(CancellationToken cancel)
    {
        await InTheProgram(cancel);
        await Task.WhenAny(window.PropertyWatches, Task.Delay(BreakpointLimit, cancel));
        cancel.ThrowIfCancellationRequested();
    }

    /// <summary>How long a breakpoint change waits for the debugger to take it into the program running.</summary>
    private static readonly TimeSpan BreakpointLimit = TimeSpan.FromSeconds(10);

    /// <summary>Waits for the debugger to answer the breakpoint changes just made, so the program has them (and the answer
    /// says whether each bound) before the agent's next step: a click or key into the app, which goes no other way.</summary>
    private async Task InTheProgram(CancellationToken cancel)
    {
        if (Session.Debug.Session is { State: DebugState.Running or DebugState.Paused } debugged)
        {
            await Task.WhenAny(debugged.BreakpointsAnswered, Task.Delay(BreakpointLimit, cancel));
            cancel.ThrowIfCancellationRequested();
        }
    }

    private Task<IReadOnlyList<BreakpointInfo>> ListBreakpoints(CancellationToken cancel) =>
        OnUi(cancel, () => Task.FromResult<IReadOnlyList<BreakpointInfo>>([.. Breakpoints(null), .. DataBreakpoints(), .. ExceptionBreakpoints()]));

    [McpServerTool(Name = McpTools.DebugStart, Title = "Debug a configuration", Destructive = true), Description(
        "Builds and launches a configuration under the debugger, as Debug does, and waits for a stop, its end or an Avalonia " +
        "or WPF app's UI. Returns where it is. With resource, that one alone, restarted.")]
    public Task<DebugSnapshot> DebugStart(
        string? configuration = null,
        string? arguments = null,
        int waitSeconds = 60, string? resource = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (resource is not null)
        {
            if (await ComposeTargetAsync(resource, configuration, starts: true, cancel) is { } service)
            {
                return arguments is null ? await DebugComposeServiceAsync(service, attach: false, waitSeconds, cancel)
                    : throw new ArgumentException("A compose service runs as its compose files say; omit arguments");
            }
            return await DebugAspireTarget(resource, configuration, arguments, attach: false, waitSeconds, cancel);
        }
        Choose(configuration);
        AgentCall.Follow(window.Actions.Work, AppActions.Debug, AppActions.Build);
        using (LaunchBuild built = new(Builds))
        {
            if (await Started(Builds.DebugAsync(arguments), AppActions.Debug, cancel) is { } problem)
            {
                throw new InvalidOperationException(WithBuildErrors(problem, built.Ended));
            }
        }
        return await AfterLaunch(TimeSpan.FromSeconds(waitSeconds), cancel);
    });

    /// <summary>A start an agent waits on (Run's, Debug's), ended as Stop ends one when the agent gives up on the call before
    /// it has started: a container run comes down rather than coming up for no one. One started is the person's to end.</summary>
    private async Task<string?> Started(Task<string?> start, string action, CancellationToken cancel)
    {
        using CancellationTokenRegistration givenUp = cancel.Register(() => UiThread.Post(() => window.Actions.Work.Of(action)?.Cancel()));
        return await start.WaitAsync(cancel);
    }

    /// <summary>The build a launch an agent waits on runs first, heard as it ends: a failed one is why the launch was refused,
    /// and what it found is what the refusal names (<see cref="WithBuildErrors"/>).</summary>
    private sealed class LaunchBuild : IDisposable
    {
        private readonly BuildRunController builds;

        public LaunchBuild(BuildRunController builds)
        {
            this.builds = builds;
            builds.BuildFinished += Heard;
        }

        /// <summary>The last build that ended since the launch began, or null.</summary>
        public BuildOutcome? Ended { get; private set; }

        private void Heard(object? sender, BuildOutcome outcome) => Ended = outcome;

        public void Dispose() => builds.BuildFinished -= Heard;
    }

    /// <summary>A run, debug, test or profile refused for a failed build (the one it ran, <paramref name="built"/>), with that
    /// build's errors after it (path:line:column, code and message, at most ten), so an agent fixes them without asking
    /// problems first; a refusal for anything else as it is, whatever an earlier build left in Problems.</summary>
    private string WithBuildErrors(string refused, BuildOutcome? built)
    {
        List<Problem> errors = built is { Status: BuildStatus.Failed } ? [.. built.Problems.Where(p => p.Severity == ProblemSeverity.Error)] : [];
        if (errors.Count == 0)
        {
            return refused;
        }
        IEnumerable<string> lines = errors.Take(10).Select(p =>
            $"{(p.Path is { } at ? $"{Relative(at)}:{p.Position.Line + 1}:{p.Position.Column + 1}" : p.Project)}: {p.Code} {p.Message}");
        return $"{refused}:\n{string.Join('\n', lines)}{(errors.Count > 10 ? $"\n… {errors.Count - 10} more: problems lists them" : "")}";
    }

    /// <summary>How long a launched app whose UI is up is watched for a stop in its first layout.</summary>
    private static readonly TimeSpan LaunchSettle = TimeSpan.FromSeconds(2);

    private static TimeSpan Min(TimeSpan one, TimeSpan other) => one < other ? one : other;

    /// <summary>Where a program just launched under the debugger is once it stops or ends, or, for an app with a window,
    /// which runs until it is closed, once its UI is up, saying so: an agent waits for no more than it needs.</summary>
    private async Task<DebugSnapshot> AfterLaunch(TimeSpan limit, CancellationToken cancel)
    {
        LiveTree.LiveApp? app = Session.LiveApp;
        DateTime until = DateTime.UtcNow + limit;
        AgentCall.Say($"Debugging: waiting up to {limit.TotalSeconds:0} s for a stop, the program's end or its UI");
        Func<bool> going = () => Session.Debug.Session is { State: DebugState.Running or DebugState.Starting };
        // Looked at every 50 ms: the session the launch makes can come after the wait begins, and the debugger tells of none.
        await Until(() => !going() || app is { State: not LiveTree.LiveAppState.Waiting }, limit, cancel);
        if (going() && app is { State: LiveTree.LiveAppState.Connected })
        {
            // A stop in the first layout, just after the inspector's hello, is waited for too; looked at every 50 ms.
            await Until(() => !going(), Min(LaunchSettle, until - DateTime.UtcNow), cancel);
        }
        DebugSnapshot started = await AfterResume(TimeSpan.Zero, cancel);
        return started.Debugged == DebugState.Running && app is { State: LiveTree.LiveAppState.Connected }
            ? started with { Status = $"{started.Status}, its UI up: app_windows, app_screenshot and app_click look at it and use it" }
            : started;
    }

    /// <summary>The programs the debugger can attach to, each command line's secrets masked as Attach to Process shows them.</summary>
    private Task<IReadOnlyList<ProcessInfo>> DebugProcesses(CancellationToken cancel) => OnUi(cancel, async () =>
    {
        Func<string, string> masked = Passenger.App.Containers.ContainerSecrets.CommandLines();
        return (IReadOnlyList<ProcessInfo>)[.. (await window.AttachableAsync()).Where(p => p.WhyNot is null)
            .Select(p => new ProcessInfo(p.Id, p.Name, p.Program, p.User, masked(p.CommandLine), p.Container))];
    });

    [McpServerTool(Name = McpTools.DebugAttach, Title = "Attach to a running program", Destructive = true), Description(
        "Attaches the debugger to a running .NET program, as Attach to Process does: processId, or program when one has that " +
        "name, in container when it runs in one (debug_state of processes lists them), or resource. Waits for a stop or its end and " +
        "returns where it is; stop with detach lets it go on, stop ends it.")]
    public Task<DebugSnapshot> DebugAttach(int? processId = null, string? program = null,
        string? container = null, int waitSeconds = 10, string? resource = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (resource is not null)
        {
            if (processId is not null || program is not null || container is not null)
            {
                throw new ArgumentException("Name resource or a process target, not both");
            }
            return await ComposeTargetAsync(resource, null, starts: false, cancel) is { } service ? await DebugComposeServiceAsync(service, attach: true, waitSeconds, cancel)
                : await DebugAspireTarget(resource, null, null, attach: true, waitSeconds, cancel);
        }
        IReadOnlyList<DotnetProcess> running = [.. (await window.AttachableAsync()).Where(p => p.Container == container)];
        List<DotnetProcess> named = processId is int id and not 0 ? [.. running.Where(p => p.Id == id)]
            : program is { Length: > 0 } name ? [.. running.Where(p => p.Program.Equals(name, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))]
            : throw new ArgumentException("Name the program to attach to by its processId, or by its program's name");
        DotnetProcess chosen = named switch
        {
            [{ } only] => only,
            [] => throw new InvalidOperationException($"No .NET program {(processId is int pid ? $"with process id {pid}" : $"named {program}")} is running for the debugger to attach to; debug_state of processes lists those there are"),
            _ => throw new InvalidOperationException($"{named.Count} running programs are named {program}: name one by its processId ({string.Join(", ", named.Select(p => p.Id))})"),
        };
        if (await window.AttachAsync(chosen) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
        return await AfterResume(TimeSpan.FromSeconds(waitSeconds), cancel);
    });

    private Task<string> DebugDetach(string? session, CancellationToken cancel) => OnUi(cancel, async () =>
    {
        DebugSession attached = await Debugged(session);
        return await Session.Debug.DetachAsync(attached) is { } problem ? throw new InvalidOperationException(problem)
            : Session.Debug.SummaryOf(attached) ?? "Detached";
    });

    // A value's members, the breakpoints and the programs to attach to are this tool's too: tools of their own cost every
    // session's list more.
    [McpServerTool(Name = McpTools.DebugState, Title = "Where the debugger is", ReadOnly = true), Description(
        "The debugger's state and, while paused, the stop: its reason, the thread's frames and the chosen frame's variables, " +
        "and in an Avalonia app the binding values of the .axaml files open in the editor. With several programs debugged, " +
        "sessions names them. With reference (a variable's, debug_evaluate's or an earlier answer's), the members, elements " +
        "or locals behind it. of breakpoints: every breakpoint (line, data and property, exception with its scenarios, XAML " +
        "Binding Errors), each with whether the debugger took it; while Mute Breakpoints (ide_action " +
        "app.debug.muteBreakpoints) is on, none stops. of processes: the .NET programs here and in running containers the " +
        "debugger can attach to, program being the .dll the dotnet host runs (a container's id 0 until debug_attach puts " +
        "Passenger's tools in).")]
    public async Task<object> GetDebugState(string? session = null, string? of = null, int? reference = null, CancellationToken cancel = default) => (of, reference) switch
    {
        (null or "stop", int members) => await DebugVariables(members, session, cancel),
        (null or "stop", null) => await OnUi(cancel, () => DebugSnapshotAsync(session is null ? Unnamed() : TabFor(session)?.Session)),
        ("breakpoints", null) => await ListBreakpoints(cancel),
        ("processes", null) => await DebugProcesses(cancel),
        (_, null) => throw new ModelContextProtocol.McpException($"No '{of}' to give; of takes stop (the default), breakpoints or processes"),
        _ => throw new ModelContextProtocol.McpException("A reference's members are the stop's: give reference or of, not both"),
    };

    [McpServerTool(Name = McpTools.DebugContinue, Title = "Resume", Destructive = false), Description(
        "Resumes the program and waits for it to stop again or end; with path and line, runs to that line as Run to Cursor " +
        "does, stopping sooner at a breakpoint. A debugged test run that ends answers its tally and failures too (tests).")]
    public Task<DebugSnapshot> DebugContinue(int waitSeconds = 30, string? session = null, string? path = null, int? line = null, CancellationToken cancel = default) =>
        (path, line) switch
        {
            (null, null) => Resume(s => s.ContinueAsync(), waitSeconds, session, cancel),
            ({ } file, int to) => Resume(debugged => debugged.RunToCursorAsync(Resolve(file), to - 1), waitSeconds, session, cancel),
            _ => throw new ModelContextProtocol.McpException("Run to a line takes both path and line"),
        };

    [McpServerTool(Name = McpTools.DebugStep, Title = "Step", Destructive = false), Description("Steps (kind) over, into or out of the current line, and waits for the step to stop.")]
    public Task<DebugSnapshot> DebugStep(string kind = "over", int waitSeconds = 30, string? session = null,
        CancellationToken cancel = default) => Resume(kind switch
    {
        "over" => s => s.StepOverAsync(),
        "into" => s => s.StepIntoAsync(),
        "out" => s => s.StepOutAsync(),
        _ => throw new ArgumentException($"A step is over, into or out, not '{kind}'"),
    }, waitSeconds, session, cancel);

    [McpServerTool(Name = McpTools.DebugPause, Title = "Pause", Destructive = false), Description("Pauses the running program wherever it is.")]
    public Task<DebugSnapshot> DebugPause(int waitSeconds = 10, string? session = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        DebugSession debugged = await Debugged(session);
        await debugged.PauseAsync();
        await When(() => debugged.State != DebugState.Running, TimeSpan.FromSeconds(waitSeconds), cancel, StateOf(debugged));
        return await DebugSnapshotAsync(debugged);
    });

    [McpServerTool(Name = McpTools.DebugSetNextStatement, Title = "Set Next Statement", Destructive = false), Description(
        "Moves where the paused program goes on to another line of its method, as Set Next Statement does, skipping or " +
        "repeating the lines between; nothing runs. Returns the new stop.")]
    public Task<DebugSnapshot> DebugSetNextStatement(string path, int line, string? session = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        DebugSession paused = await Paused(session, choose: true);
        if (await Session.Debug.SetNextStatementAsync(paused, Resolve(path), line - 1) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
        await When(() => paused.StopReason == StopReasons.Goto, TimeSpan.FromSeconds(10), cancel, StateOf(paused));
        return await DebugSnapshotAsync(paused);
    });

    [McpServerTool(Name = McpTools.DebugInput, Title = "Type into the program's console", Destructive = true), Description(
        "Sends a line to the debugged program's standard input, as the Debug window's console does, or types it into a " +
        "terminal configuration's tab, Enter for the newline.")]
    public Task<string> DebugInput(string text, bool newline = true, string? session = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        DebugSession debugged = await Debugged(session);
        if (debugged.Terminal is { } inTerminal)
        {
            inTerminal.Session.Write(newline ? text + "\r" : text);
            return $"Typed into {inTerminal.Name}'s tab of the Terminal window";
        }
        await debugged.InputAsync(newline ? text + "\n" : text);
        return "Sent";
    });

    [McpServerTool(Name = McpTools.DebugEvaluate, Title = "Evaluate an expression", Destructive = true), Description(
        "Evaluates a C# expression, which may call the program's methods, in a frame of the stopped thread (0, the top, by " +
        "default), as the Debug window's evaluate box does; a result with members has a reference debug_state opens. With " +
        "value, a C# expression, assigns it to expression (a local, member or element) as Set Value does, answering what it " +
        "now is.")]
    public Task<VariableInfo> DebugEvaluate(string expression, int frame = 0, string? session = null,
        string? value = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        DebugSession paused = await Paused(session, choose: value is not null);
        DapStackFrame chosen = await FrameAt(paused, frame);
        if (value is not null)
        {
            SetExpressionResponse set = await paused.SetExpressionAsync(expression, value, chosen.Id);
            await Session.Debug.RefreshAsync();
            return Variable(expression, set.Value, set.Type, set.VariablesReference, expression);
        }
        EvaluateResponse result = await paused.EvaluateAsync(expression, chosen.Id, "repl");
        return Variable(expression, result.Result, result.Type, result.VariablesReference, expression);
    });

    private Task<IReadOnlyList<VariableInfo>> DebugVariables(int reference, string? session, CancellationToken cancel) => OnUi(cancel, async () =>
    {
        DebugSession paused = await Paused(session);
        return (IReadOnlyList<VariableInfo>)[.. (await Session.Debug.MembersAsync(paused, reference))
            .Select(v => Variable(v.Name, v.Value, v.Type, v.VariablesReference, v.EvaluateName is { } named && named != v.Name ? named : null))];
    });

    /// <summary>A variable as a debug answer gives it, its value cut where it runs long (<see cref="AgentAnswers.Value"/>),
    /// read on by debug_evaluate of <paramref name="expression"/> where one names it at the stopped frame.</summary>
    private static VariableInfo Variable(string name, string value, string? type, int reference, string? expression)
    {
        (string shown, string? cut) = AgentAnswers.Value(value, expression);
        return new VariableInfo(name, shown, type, reference) { Cut = cut };
    }

    /// <summary>output's debug: the last lines the debugged program has written, its Debug tab's console or a terminal
    /// configuration's tab, scrollback and screen.</summary>
    private string DebugOutput(int lines, string? session)
    {
        DebugSession? debugged = session is null ? Unnamed() : TabFor(session)?.Session;
        return debugged?.Terminal is { } inTerminal ? Tail(inTerminal.Session, lines) : Tail(Session.Debug.Output(debugged), lines);
    }

    // The IDE itself

    [McpServerTool(Name = McpTools.IdeAction, Title = "Run an IDE action", Destructive = true), Description(
        "Runs an IDE action by its id (app.build, app.debug.stepOver...; ide_actions lists them) as its shortcut would, waits " +
        "for the work it begins and says what it came to, or says why it cannot. input: app.migrations.add's name, updateTo's " +
        "target; app.saveAsLaunchProfile's configuration (else the chosen one).")]
    public Task<string> IdeAction(string action, string? input = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (action.StartsWith("app.migrations.", StringComparison.Ordinal) && window.Actions[action] is not null)
        {
            return await window.MigrationsForAgentAsync(action, input, cancel);
        }
        if (action == AppActions.SaveAsLaunchProfile)
        {
            return await window.SaveAsLaunchProfileForAgentAsync(input);
        }
        if (input is not null)
        {
            throw new InvalidOperationException($"{action} takes no input");
        }
        IdeAction? named = window.Actions[action];
        if (named is { Group: GitGroup })
        {
            // Read now, as the vcs tools read it: the agent's shell may have begun a rebase a moment ago.
            await Session.Vcs.Opened;
            await Session.Vcs.RefreshAsync();
        }
        if (named is not null)
        {
            AgentCall.Follow(window.Actions.Work, named.WorkId);
        }
        Hearing heard = ActionWork.Hear();
        List<string> unanswered = [];
        // An agent's own IDE has nobody to answer a dialog its action opens: closed unanswered, and said.
        using IDisposable? asking = Beside ? null : Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<Avalonia.Controls.Window>((opened, _) =>
        {
            if (opened.IsDialog && ActionWork.Heard == heard)
            {
                unanswered.Add(opened.Title ?? "a dialog");
                UiThread.Post(opened.Close);
            }
        }, handledEventsToo: true);
        string? why;
        try
        {
            why = Beside ? window.Actions.RunForAgent(action) : window.Actions.Run(action);
        }
        finally
        {
            heard.Ran();
        }
        if (why is not null)
        {
            throw new InvalidOperationException($"{action} did not run: {why}");
        }
        await heard.DoneAsync(cancel);
        if (unanswered.Count > 0)
        {
            throw new InvalidOperationException($"{action} asked in {string.Join(", ", unanswered)}, which nobody is at this IDE to answer: it was closed unanswered"
                + (heard.Outcome is { } then ? $"; {then.Said}" : string.Empty));
        }
        return heard.Outcome switch
        {
            { Failed: true } failed => throw new InvalidOperationException(failed.Said),
            { } came => came.Said,
            null => $"Ran {action}",
        };
    });

    /// <summary>The group of Git's actions, whose state ide_action reads fresh before asking whether one can run.</summary>
    private const string GitGroup = "Git";

    [McpServerTool(Name = McpTools.IdeActions, Title = "The IDE's actions", ReadOnly = true), Description(
        "Every action ide_action runs (those with no shortcut too), as id: name [group] (shortcut on this platform), " +
        "and why one cannot run here now.")]
    public Task<IReadOnlyList<string>> IdeActions(CancellationToken cancel = default) => OnUi(cancel, () =>
    {
        ActionRegistry actions = window.Actions;
        ActionContext context = Beside ? actions.Context().ForAgent() : actions.Context();
        return Task.FromResult((IReadOnlyList<string>)[.. actions.All.Select(a =>
            $"{a.Id}: {a.Title} [{a.Group}]{(actions.Shortcut(a.Id) is { } keys ? $" ({keys})" : string.Empty)}{(a.WhyNot(context) is { } why ? $" - cannot run: {why}" : string.Empty)}")]);
    });

    // The work behind them

    /// <summary>A tool's work on the UI thread, its waits ended by <paramref name="cancel"/> (the agent gave up on the
    /// call, or went), once the folder's projects are read and evaluated, which is what every tool that runs, builds or
    /// tests a project asks about. What it refuses (nothing to debug, no such file) reaches the agent in its own words: the
    /// SDK passes only an McpException's message on, and hides every other's.</summary>
    /// <summary>Whether the agent is attached to a person's window, beside the person, rather than in an IDE of its own.</summary>
    private bool Beside => connection is { Attached: true };

    private async Task<T> OnUi<T>(CancellationToken cancel, Func<Task<T>> work)
    {
        cancel.ThrowIfCancellationRequested();
        try
        {
            return await UiThread.InvokeAsync(async () =>
            {
                // Ended while it waited its turn (its agent shut out as the window left the folder): it does nothing in the next.
                cancel.ThrowIfCancellationRequested();
                // A tool run inside another's call reads its files into that call's, let go as that one ends.
                CallFiles? own = Files.Value is null ? new CallFiles(Editors, Beside) : null;
                if (Beside)
                {
                    AgentAsks.Call();
                }
                if (own is not null)
                {
                    Files.Value = own;
                }
                try
                {
                    if (!Session.WorkspaceRead.IsCompleted || !Session.Models.Idle.IsCompleted)
                    {
                        AgentCall.Say($"Waiting for {Session.Folder ?? "the folder"}'s projects to be read");
                        await Session.WorkspaceRead.WaitAsync(cancel);
                        await Session.Models.Idle.WaitAsync(cancel);
                        AgentCall.Say(null);
                    }
                    return await work();
                }
                finally
                {
                    own?.LetGo();
                }
            });
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or TimeoutException
            or HttpRequestException or DapException or LanguageServerFailed or LiveTree.LiveAppException or global::Passenger.App.Containers.ContainerCommandException
            or global::Passenger.App.Containers.ContainerToolsException)
        {
            throw new ModelContextProtocol.McpException(error is DapException dap ? $"The debugger refused: {dap.Reason}" : error.Message, error);
        }
        catch (OperationCanceledException error) when (!cancel.IsCancellationRequested)
        {
            // Given up inside the IDE, not by the agent (a folder left under the call): said, where the SDK would hide it.
            throw new ModelContextProtocol.McpException($"{Faulted}{error.GetType().Name}: {error.Message}", error);
        }
        catch (Exception error) when (error is not (OperationCanceledException or ModelContextProtocol.McpException))
        {
            // A fault of the IDE's: logged, and its words the agent's, where the SDK would hide them.
            IdeErrors.Report(error);
            throw new ModelContextProtocol.McpException($"{Faulted}{error.GetType().Name}: {error.Message}", error);
        }
    }

    /// <summary>How an answer begins when a tool met a fault of the IDE's own, never one of the refusals an agent can act on.</summary>
    public const string Faulted = "Passenger failed: ";

    /// <summary>Sets the paused program going as the window's keys do, its tab chosen and the edits made while it was paused
    /// applied first.</summary>
    private Task<DebugSnapshot> Resume(Func<DebugSession, Task> resume, int waitSeconds, string? session, CancellationToken cancel) => OnUi(cancel, async () =>
    {
        DebugSession paused = await Paused(session, choose: true);
        // A debugged test run (tests_run's, or the Unit Tests window's Debug) whose host ends is answered with its tally.
        bool testing = Session.Tests.IsBusy;
        if (await Session.Debug.ApplyBeforeResumingAsync(paused))
        {
            await resume(paused);
        }
        DebugSnapshot after = await AfterResume(TimeSpan.FromSeconds(waitSeconds), cancel, paused);
        if (!testing || after.Debugged is not null)
        {
            return after;
        }
        // The host's results come just after it ends; looked at every 50 ms: the next host's session is told nowhere.
        await Until(() => !Session.Tests.IsBusy || Session.Debug.Session is { State: not DebugState.Ended }, TestsTallyLimit, cancel);
        return Session.Tests.IsBusy ? await AfterResume(TimeSpan.FromSeconds(waitSeconds), cancel) : after with { Tests = TestInfo(Session.Tests) };
    });

    /// <summary>How long a resumed debugged test run whose host ended is waited for to tell its results.</summary>
    private static readonly TimeSpan TestsTallyLimit = TimeSpan.FromSeconds(60);

    /// <summary>Waits for a program (the chosen one's by default) to stop or end after it was set going, then says where it is.</summary>
    private async Task<DebugSnapshot> AfterResume(TimeSpan limit, CancellationToken cancel, DebugSession? session = null)
    {
        if (limit > TimeSpan.Zero)
        {
            AgentCall.Say($"The program runs: waiting up to {limit.TotalSeconds:0} s for it to stop or end");
        }
        DebugSession? going = session ?? Session.Debug.Session;
        await When(() => going is not { State: DebugState.Running or DebugState.Starting }, limit, cancel, StateOf(going));
        await StopReadAsync(going, cancel);
        return await DebugSnapshotAsync(going);
    }

    /// <summary>Waits for the Debug window to have read the stop a session is paused at (described, its frames listed), which
    /// comes just after the state says paused, in as long as the adapter takes to answer what it asks; at most
    /// <see cref="StopReadLimit"/>, past which the stop is taken as one the window never shows (its tab not chosen).</summary>
    private async Task StopReadAsync(DebugSession? session, CancellationToken cancel) =>
        await When(() => session is not { State: DebugState.Paused } || Session.Debug.HasRead(session), StopReadLimit, cancel, StateOf(session),
            On(h => Session.Debug.StopRead += h, h => Session.Debug.StopRead -= h));

    private static TimeSpan StopReadLimit => Waits.Of(TimeSpan.FromSeconds(60));

    /// <summary>The session a debugging tool acts on when it names none: the one paused, else the only one debugged; with
    /// none debugged the chosen tab's (ended, or none). Several debugged and none or several paused, the call is refused,
    /// naming them.</summary>
    private DebugSession? Unnamed() => TabFor(null)?.Session;

    /// <summary>The tab a tool's session names, by its program's name as debug_state lists them, else as <see cref="Unnamed"/>
    /// picks one.</summary>
    private ToolWindows.DebugTab? TabFor(string? session)
    {
        DebugController debugger = Session.Debug;
        if (session is { Length: > 0 } named)
        {
            return debugger.Tabs.FirstOrDefault(t => t.Name == named) ?? debugger.Tabs.FirstOrDefault(t => t.Name.Equals(named, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(debugger.Tabs.Count == 0 ? $"No program named {named} is debugged: none is" : $"No program named {named} is debugged; there are {Listed()}");
        }
        List<ToolWindows.DebugTab> live = [.. debugger.Tabs.Where(t => t.IsLive && t.Session is not null)];
        List<ToolWindows.DebugTab> paused = [.. live.Where(t => t.Session!.State == DebugState.Paused)];
        return paused is [var stopped] ? stopped
            : live is [var only] ? only
            : live.Count == 0 ? debugger.Chosen
            : throw new InvalidOperationException($"{live.Count} programs are debugged ({Listed()}), {(paused.Count == 0 ? "none" : $"{paused.Count}")} paused: name the one meant with session");
    }

    /// <summary>The programs debugged and each one's state, as debug_state lists them.</summary>
    private string Listed() => string.Join(", ", Sessions());

    private IReadOnlyList<string> Sessions() =>
        [.. Session.Debug.Tabs.Select(t => $"{t.Name}: {(t.IsLive && t.Session is { } live ? live.State.ToString().ToLowerInvariant() : "ended")}")];

    /// <summary>Where the debugger is in a session's program (the chosen one's by default), from its own model: the session's
    /// state in its words, and while paused the stop with the top frame's locals; with several programs debugged, which one
    /// this is and every one's state.</summary>
    private async Task<DebugSnapshot> DebugSnapshotAsync(DebugSession? session = null)
    {
        DebugController debugger = Session.Debug;
        DebugSession? of = session ?? debugger.Session;
        DebugSnapshot snapshot = await SnapshotOf(of);
        return debugger.Tabs.Count > 1 ? snapshot with { Session = of is null ? null : debugger.TabOf(of)?.Name, Sessions = Sessions() } : snapshot;
    }

    /// <summary>Why a paused session stopped and where, as debug_state says them, with nothing else of the stop read.</summary>
    private async Task<(string? Reason, string? Location)> StoppedAtAsync(DebugSession session)
    {
        if (session.State != DebugState.Paused || session.StoppedThread is null)
        {
            return (null, null);
        }
        (_, DapStackFrame? top) = await StopFramesAsync(session);
        return (session.StopReason, Located(top, session));
    }

    /// <summary>A paused session's frames and the one its stop shows: as the Debug window read them, else from the adapter.</summary>
    private async Task<(IReadOnlyList<DapStackFrame> Frames, DapStackFrame? Top)> StopFramesAsync(DebugSession session)
    {
        DebugController debugger = Session.Debug;
        // The controller's stop is always the one paused at now, since every change drops it; before it is read, the session's.
        DebugStop? read = debugger.Session == session && debugger.Stop is { } stop && stop.Thread == session.StoppedThread ? stop : null;
        IReadOnlyList<DapStackFrame> frames = read?.Frames ?? await Stack(session);
        // The frame the stop shows: past the Live Visual Tree inspector's call a property or binding error breakpoint stops in.
        return (frames, read?.Top ?? frames.FirstOrDefault(f => f.Source?.Path is not null) ?? (frames.Count > 0 ? frames[0] : null));
    }

    /// <summary>A frame's place as an agent reads it, path:line:column at the editor's line; null for one with no source.</summary>
    private string? Located(DapStackFrame? frame, DebugSession session) =>
        frame?.Source?.Path is { } path && Session.Debug.PlaceOf(frame, session) is { } at ? $"{Relative(path)}:{at.Line + 1}:{at.Column + 1}" : null;

    private async Task<DebugSnapshot> SnapshotOf(DebugSession? session)
    {
        DebugController debugger = Session.Debug;
        string? summary = session is null ? debugger.Summary : debugger.SummaryOf(session);
        if (session is null || session.State == DebugState.Ended)
        {
            return new DebugSnapshot(null, null, null, [], [], session?.ExitCode, summary);
        }
        if (session.State != DebugState.Paused || session.StoppedThread is null)
        {
            return new DebugSnapshot(session.State, null, null, [], [], null, summary);
        }
        bool shown = debugger.Session == session;
        (IReadOnlyList<DapStackFrame> frames, DapStackFrame? top) = await StopFramesAsync(session);
        List<VariableInfo> locals = [];
        if (shown && debugger.BindingStop is { } failed)
        {
            locals.Add(Variable(BindingStop.Name, failed.Value, failed.Type, failed.Reference, null));
        }
        if (top is not null)
        {
            foreach (DapScope scope in await session.ScopesAsync(top.Id))
            {
                locals.AddRange((await session.VariablesAsync(scope.VariablesReference)).Select(v => Variable(v.Name, v.Value, v.Type, v.VariablesReference, v.EvaluateName ?? v.Name)));
            }
        }
        string? exception = null;
        if (session.StopReason == StopReasons.Exception)
        {
            try
            {
                ExceptionInfoResponse info = await session.ExceptionInfoAsync(session.StoppedThread.Value);
                exception = Describe(info.Details) ?? $"{info.ExceptionId}: {info.Description}";
            }
            catch (DapException error)
            {
                exception = error.Message;
            }
        }
        return new DebugSnapshot(
            DebugState.Paused,
            session.StopReason,
            Located(top, session),
            Frames(frames, session),
            locals,
            null,
            summary,
            exception)
        {
            BindingValues = shown ? await BindingValuesShownAsync() : null,
        };
    }

    /// <summary>A stop's frames as an agent reads them, each by its index (which debug_evaluate's frame takes): the folder's
    /// own code by its place, and a run of any other (a framework's, with or without source: an Avalonia click is some 25
    /// of them under the handler, and Passenger's inspector's) as one line of their indexes and modules, at most 20 lines.</summary>
    private IReadOnlyList<string> Frames(IReadOnlyList<DapStackFrame> frames, DebugSession session)
    {
        string? folder = Session.Folder;
        bool Own(DapStackFrame frame) => frame.Source?.Path is { } path && (folder is null || PathNames.IsUnder(path, folder));
        // The editor's line (from one) of the frame's code, in its file now: the debugger's is its build's compiler's.
        int LineOf(DapStackFrame frame) => (Session.Debug.PlaceOf(frame, session)?.Line ?? frame.Line - 1) + 1;
        List<string> lines = [];
        for (int i = 0; i < frames.Count && lines.Count < 20; i++)
        {
            if (Own(frames[i]))
            {
                lines.Add($"{i}: {frames[i].Name} at {Relative(frames[i].Source!.Path!)}:{LineOf(frames[i])}");
                continue;
            }
            int last = i;
            while (last + 1 < frames.Count && !Own(frames[last + 1]))
            {
                last++;
            }
            if (last == i)
            {
                lines.Add($"{i}: {frames[i].Name}{(frames[i].Source?.Path is { } path ? $" at {path}:{LineOf(frames[i])}" : "")}");
                continue;
            }
            string[] modules = [.. frames.Skip(i).Take(last - i + 1).Select(f => f.Name.IndexOf('!', StringComparison.Ordinal) is > 0 and int bang ? f.Name[..bang] : null)
                .OfType<string>().Distinct()];
            lines.Add($"{i}-{last}: {last - i + 1} frames outside the folder's code{(modules.Length > 0 ? $" ({string.Join(", ", modules.Take(4))}{(modules.Length > 4 ? ", …" : "")})" : "")}");
            i = last;
        }
        return lines;
    }

    /// <summary>How long a stop's answer waits for the binding values of the .axaml files shown to be read.</summary>
    private static readonly TimeSpan BindingValuesLimit = TimeSpan.FromSeconds(20);

    /// <summary>The binding values after the lines of the .axaml files the editors show, as they show them once read at this
    /// stop (path:line: Property: value, or that it failed and why); null when no Avalonia app is debugged or none is shown.</summary>
    private async Task<IReadOnlyList<string>?> BindingValuesShownAsync()
    {
        if (Session.LiveApp is null)
        {
            return null;
        }
        LiveTree.BindingValuesController values = window.BindingValues;
        values.CatchUp();
        // Looked at every 50 ms: the binding values' reads raise nothing as they end.
        await Until(() => !values.IsReading, BindingValuesLimit, CancellationToken.None);
        List<string> shown = [.. Editors.Groups.Select(g => g.Active).OfType<OpenDocument>().Distinct()
            .SelectMany(d => d.LiveValues.OrderBy(a => a.Line).Select(a => $"{Relative(d.Path)}:{a.Line + 1}: {a.Text}"))];
        return shown.Count == 0 ? null : shown;
    }

    /// <summary>An exception as its type and message, its inner exceptions after it.</summary>
    private static string? Describe(DapExceptionDetails? details)
    {
        if (details is null)
        {
            return null;
        }
        string text = $"{details.FullTypeName ?? details.TypeName}: {details.Message}";
        foreach (DapExceptionDetails inner in details.InnerException ?? [])
        {
            text += $"\n ---> {Describe(inner)}";
        }
        return text;
    }

    private static async Task<IReadOnlyList<DapStackFrame>> Stack(DebugSession session) =>
        await session.StackTraceAsync(session.StoppedThread ?? throw new InvalidOperationException("No thread is stopped"));

    /// <summary>A frame of the stopped thread by its place from the top, as debug_state numbers them; one past the stack
    /// says how deep the stack is.</summary>
    private static async Task<DapStackFrame> FrameAt(DebugSession session, int frame)
    {
        IReadOnlyList<DapStackFrame> frames = await Stack(session);
        return frame >= 0 && frame < frames.Count
            ? frames[frame]
            : throw new ArgumentException(frames.Count switch
            {
                0 => $"There is no frame {frame}: the stopped thread has no frames",
                1 => $"There is no frame {frame}: the stopped thread has 1 frame, 0",
                _ => $"There is no frame {frame}: the stopped thread has {frames.Count} frames, 0 (the top) to {frames.Count - 1}, as debug_state numbers them",
            });
    }

    /// <summary>The program a tool acts on (<see cref="TabFor"/>), being debugged; with <paramref name="choose"/> its tab is
    /// chosen in the Debug window, as a person picks the one their keys act on.</summary>
    private async Task<DebugSession> Debugged(string? session, bool choose = false)
    {
        ToolWindows.DebugTab? tab = TabFor(session);
        if (tab?.Session is not { State: not DebugState.Ended } debugged || !tab.IsLive)
        {
            throw new InvalidOperationException(session is null || tab is null ? "Nothing is being debugged; debug_start starts it" : $"{tab.Name} is no longer debugged; debug_state lists those that are");
        }
        if (choose)
        {
            await Session.Debug.ChooseAsync(tab);
        }
        return debugged;
    }

    private async Task<DebugSession> Paused(string? session, bool choose = false) => await Debugged(session, choose) is { State: DebugState.Paused } paused
        ? paused
        : throw new InvalidOperationException("The program is running; debug_pause stops it where it is");

    private void Choose(string? configuration)
    {
        RunConfigurations configurations = Session.Configurations;
        if (configuration is not null && !configurations.Select(configuration))
        {
            throw new InvalidOperationException($"No run configuration '{configuration}'; there are: {string.Join(", ", configurations.All.Select(c => c.Name))}");
        }
    }

    private IReadOnlyList<BreakpointInfo> Breakpoints(string? path) =>
        [.. (path is null ? Session.Debug.Breakpoints.All : Session.Debug.Breakpoints.In(path))
            .Select(b => new BreakpointInfo(Relative(b.Path), b.Line + 1, b.Enabled, b.Status.ToString().ToLowerInvariant(), b.Condition, b.HitCondition, b.LogMessage, b.Suspend, b.Message,
                Attribute: b.Attribute, Column: b.Column + 1))];

    private IReadOnlyList<BreakpointInfo> DataBreakpoints() =>
        [.. Session.Debug.Breakpoints.Data.Select(b => new BreakpointInfo(null, null, b.Enabled, b.Status.ToString().ToLowerInvariant(), b.Condition, b.HitCondition,
            b.LogMessage, b.Suspend, b.Message, Data: b.Location)),
         .. Session.Debug.Breakpoints.Properties.Select(b => new BreakpointInfo(null, null, b.Enabled, b.Status.ToString().ToLowerInvariant(), b.Condition, b.HitCondition,
            b.LogMessage, b.Suspend, b.Message, Property: b.Location, Control: Id(b.Element)))];

    private IReadOnlyList<BreakpointInfo> ExceptionBreakpoints()
    {
        BindingErrorBreakpoint bindings = Session.Debug.Breakpoints.BindingErrors;
        return [.. Session.Debug.Breakpoints.Exceptions.Select(b => new BreakpointInfo(null, null, b.Enabled, b.Status.ToString().ToLowerInvariant(), b.Condition, b.HitCondition,
                b.LogMessage, b.Suspend, b.Message, b.Location, ExceptionScenarioNames.Of(b.Scenarios))),
            new BreakpointInfo(null, null, bindings.Enabled, bindings.Status.ToString().ToLowerInvariant(), bindings.Condition, bindings.HitCondition, bindings.LogMessage,
                bindings.Suspend, bindings.Message, BindingErrors: true)];
    }

    /// <summary>A line breakpoint's file and line from zero, as an agent names them.</summary>
    private (string Path, int Line) LinePlace(string? path, int? line) => path is { Length: > 0 } && line is int at
        ? (Resolve(path), at - 1)
        : throw new InvalidOperationException("Name a path and a line for a line breakpoint, or an exception type for an exception breakpoint");

    /// <summary>The server's actions at a place or over a selection, asked with the diagnostics on the place's line.</summary>
    private async Task<IReadOnlyList<CodeAction>> Actions(ServerServed served, int line, int column, int? endLine, int? endColumn)
    {
        OpenDocument document = served.Document;
        int offset = Offset(document, line, column);
        int end = endLine is { } last ? Offset(document, last, endColumn ?? 1) : offset;
        IReadOnlyList<Diagnostic> marked = served.Web?.Diagnostics ?? Session.CSharp?.For(document)?.Diagnostics ?? [];
        int at = served.Lsp.PositionOf(offset).Line;
        IReadOnlyList<Diagnostic> here = [.. marked.Where(d => d.Range.Start.Line <= at && at <= d.Range.End.Line)];
        IReadOnlyList<CodeAction> actions = await served.Lsp.CodeActionsAsync(TextRange.FromBounds(Math.Min(offset, end), Math.Max(offset, end)), here);
        return [.. actions.Where(a => a.Disabled is null)];
    }

    /// <summary>The language server, once it has loaded the workspace.</summary>
    private async Task<RoslynSession> Server(CancellationToken cancel)
    {
        if (Session.CSharp is null)
        {
            throw new InvalidOperationException(Refusals.NoCSharp);
        }
        // The session's server, which starts again when its projects change: each look is at the one there is then.
        if (Session.CSharp is { IsReady: false })
        {
            AgentCall.Say("Waiting for the C# language server to load the solution");
            await When(() => Session.CSharp?.IsReady != false, ServerLimit, cancel, ServerState());
            AgentCall.Say(null);
        }
        return Session.CSharp is { IsReady: true, Session: { } server }
            ? server
            : throw new TimeoutException($"The language server has not loaded the workspace ({Session.CSharp?.State ?? "none"})");
    }

    /// <summary>Waits, once the language server has loaded the workspace, until it holds in its project the C# file asked
    /// about, if it came under one of the folder's projects since (an agent's written a moment before looked for first),
    /// else every such file, for up to <see cref="Patience.Limit"/>: of a file in none it answers from its Miscellaneous
    /// Files, and a name declared there it does not find. Refused, naming the first, when the time is up; whether it was
    /// waited for (false while the server still loads the workspace).</summary>
    private async Task<bool> TakenInAsync(string? path, CancellationToken cancel)
    {
        if (Session.CSharp is not { IsReady: true } csharp)
        {
            return false;
        }
        IReadOnlyList<(string Path, string Project)> left = path is null
            ? await csharp.UnplacedAsync(Patience.Limit, looking: true, null, SayTakingIn, cancel)
            : await csharp.PlacedAsync(Resolve(path), Patience.Limit, looking: true, SayTakingIn, cancel);
        return left.Count == 0 ? true : throw new TimeoutException(NotTakenIn(left));
    }

    /// <summary>The agent's progress while a C# file is waited for (null once it is not).</summary>
    private void SayTakingIn(string? file) => AgentCall.Say(file is null ? null : $"Waiting for the C# language server to load {Relative(file)} into its project");

    private string NotTakenIn(IReadOnlyList<(string Path, string Project)> left) =>
        Refusals.NotTakenIn(Relative(left[0].Path), Path.GetFileNameWithoutExtension(left[0].Project));

    /// <summary>A file opened as the editor would, with the language server's document for it once the server has loaded
    /// the workspace and holds the file in its project (<see cref="TakenInAsync"/>, waited for before the file is opened,
    /// so one just written is opened in its project; <paramref name="takenIn"/> when the caller has).</summary>
    private async Task<(OpenDocument Document, LspDocument Lsp)> Code(string path, CancellationToken cancel, bool takenIn = false)
    {
        takenIn = takenIn || await TakenInAsync(path, cancel);
        OpenDocument document = await DocumentAsync(path, cancel);
        if (Session.CSharp is null)
        {
            throw new InvalidOperationException(Refusals.NoCSharp);
        }
        // The server is never given another language's file, so waiting for it to load one would wait out the limit.
        if (document.Language.Service != Languages.LanguageService.CSharp)
        {
            throw new InvalidOperationException($"This is the C# language server's, which answers for C# and Razor files, not {LanguageOf(document)} ones such as {Path.GetFileName(document.Path)}");
        }
        // Answers before the solution has loaded are wrong; looked at every 50 ms: a server document raises nothing.
        await Until(() => Session.CSharp is not { } csharp || csharp.IsReady && csharp.For(document)?.Lsp is not null, ServerLimit, cancel);
        if (!takenIn)
        {
            await TakenInAsync(path, cancel);
        }
        return (document, Session.CSharp?.For(document)?.Lsp
            ?? throw new TimeoutException($"The language server has not loaded {Path.GetFileName(document.Path)} ({Session.CSharp?.State ?? "none"})"));
    }

    /// <summary>A file opened as the editor would, with no tab, held for the call (<see cref="CallFiles"/>). An agent's own
    /// file tools write to disk and ask at once, before the watcher has told the editor: an open file it has not changed
    /// takes the disk's text first, read off the UI thread and only once its length or time changed since the editor read
    /// it (<see cref="OpenDocument.TakeDiskTextAsync"/>). A preview has no text to take, and is decoded again only when
    /// the watcher hears its file change.</summary>
    private async Task<OpenDocument> DocumentAsync(string path, CancellationToken cancel)
    {
        string full = Resolve(path);
        if (Editors.Find(full) is { } open)
        {
            Files.Value?.Hold(open);
            await open.TakeDiskTextAsync(cancel);
            return open;
        }
        if (Files.Value is not { Ended: false } call)
        {
            throw new InvalidOperationException($"{Relative(full)} is opened for a tool's call only while the call runs");
        }
        try
        {
            return call.Lend(full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new FileNotFoundException($"{full} could not be opened: {error.Message}", error);
        }
    }

    /// <summary>The call this flow serves' files (<see cref="OnUi"/>'s), the dispatcher flowing it as it flows <see
    /// cref="AgentCall"/>.</summary>
    private static readonly AsyncLocal<CallFiles?> Files = new();

    /// <summary>The files one call read or changed, held open while it runs so no other call's end closes them under it, and
    /// let go after it (<see cref="Lingering"/>): one with no tab closes then, unless the call left it unsaved, or changed it
    /// in a person's window (<paramref name="keepChanged"/>), where its Undo is the person's, which keeps it in a tab. So an
    /// agent's session never holds open every file it read, nor a person's window a tab they never opened.</summary>
    private sealed class CallFiles(Passenger.App.Editors.EditorArea editors, bool keepChanged)
    {
        private readonly Dictionary<OpenDocument, (IDisposable Hold, int Version)> held = [];

        /// <summary>Whether the call has ended and let its files go, so nothing it left running holds one again.</summary>
        public bool Ended { get; private set; }

        public void Hold(OpenDocument document)
        {
            if (!Ended && !held.ContainsKey(document))
            {
                held[document] = (editors.Hold(document), document.Text.Version);
            }
        }

        public OpenDocument Lend(string full)
        {
            (OpenDocument document, IDisposable hold) = editors.Lend(full);
            if (!held.TryAdd(document, (hold, document.Text.Version)))
            {
                hold.Dispose();
            }
            return document;
        }

        public void LetGo()
        {
            Ended = true;
            List<IDisposable> holds = [];
            foreach ((OpenDocument document, (IDisposable hold, int version)) in held)
            {
                bool kept = document.IsModified || (keepChanged && document.Text.Version != version);
                if (kept && editors.Documents.Contains(document) && !editors.HasTab(document))
                {
                    editors.Activate(document);
                }
                holds.Add(hold);
            }
            held.Clear();
            Lingering.Of(editors).Take(holds);
        }
    }

    /// <summary>The files of the call that ended last, held a moment more: an agent's next call is often on the same file,
    /// which then keeps what its servers worked out as it opened (a linter's findings, a compose file's names). Let go as
    /// the next call ends, or once the window's calls pause for <see cref="Pause"/>.</summary>
    private sealed class Lingering
    {
        private static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Passenger.App.Editors.EditorArea, Lingering> Windows = new();

        private readonly Debounce later;
        private List<IDisposable> holds = [];

        private Lingering() => later = new Debounce(Pause, () =>
        {
            Take([]);
            return Task.CompletedTask;
        });

        public static Lingering Of(Passenger.App.Editors.EditorArea editors) => Windows.GetValue(editors, _ => new Lingering());

        /// <summary>Holds the files of the call that ended, then lets go of those of the one before, so a file both held stays open.</summary>
        public void Take(List<IDisposable> next)
        {
            List<IDisposable> before = holds;
            holds = next;
            foreach (IDisposable hold in before)
            {
                hold.Dispose();
            }
            if (next.Count > 0)
            {
                later.Restart();
            }
        }
    }

    /// <summary>A file opened as <see cref="DocumentAsync"/> opens it, refused when its tab holds no text of the file's (an
    /// image's or a font's preview, a workbook's cells), so no tool reads or edits that empty stand-in as the file.</summary>
    private async Task<OpenDocument> TextualAsync(string path, CancellationToken cancel)
    {
        OpenDocument document = await DocumentAsync(path, cancel);
        return document.HoldsText ? document
            : throw new InvalidOperationException($"{Relative(document.Path)} is {document.ShownAs}: it has no text to read or edit");
    }

    /// <summary>The offset of a line and column an agent names (from one) in an open file, a column past the line's end
    /// at its end; a line outside the file refused (<see cref="LineIn"/>).</summary>
    private int Offset(OpenDocument document, int line, int column)
    {
        TextSnapshot text = document.Text.Snapshot;
        int l = LineIn(text, document.Path, line);
        return Math.Min(text.GetLineStart(l) + Math.Max(0, column - 1), text.GetLine(l).End);
    }

    /// <summary>A line an agent names (from one) as a line of a file's text (from zero); one below the first or past the
    /// last refused, saying the file's lines.</summary>
    private int LineIn(TextSnapshot text, string path, int line) => line >= 1 && line <= text.LineCount ? line - 1
        : throw new ArgumentException($"Line {line} is outside {Relative(path)}, whose lines are 1 to {text.LineCount}");

    /// <summary>The places the language server names, in its lines, as an agent reads them (<see cref="Lines"/>).</summary>
    private async Task<IReadOnlyList<Place>> Places(IReadOnlyList<LspLocation> locations, CancellationToken cancel)
    {
        IReadOnlyList<LspLocation> copied = Session.CSharp is { } csharp ? await csharp.Generated.CopiedAsync(locations, cancel) : locations;
        return await Lines([.. copied.Select(l => (l.Uri, l.Range.Start, true))], cancel);
    }

    /// <summary>Each place's path as the agent is shown it, its line and column in the editor's lines and that line's text,
    /// trimmed: one the server names (<paramref name="places"/>' Server) read in its file's text, as the server's lines
    /// are not the editor's in a file with a lone '\r', U+0085, U+2028 or U+2029. Worked out on the thread pool, each file
    /// read once: a symbol used thousands of times in a large solution never holds the person's window.</summary>
    private Task<IReadOnlyList<Place>> Lines(IReadOnlyList<(string Uri, LspPosition At, bool Server)> places, CancellationToken cancel)
    {
        string? folder = Session.Folder;
        // An editor's snapshot is immutable, so an open file's lines (unsaved edits and all) are read off the UI thread too.
        Dictionary<string, TextSnapshot> open = new(StringComparer.Ordinal);
        foreach (OpenDocument document in Editors.Documents)
        {
            open.TryAdd(document.Path, document.Text.Snapshot);
        }
        return BackgroundWork.Run(() =>
        {
            Dictionary<string, string> byUri = places.Select(p => p.Uri).Distinct(StringComparer.Ordinal).ToDictionary(u => u, DocumentUris.ToPath, StringComparer.Ordinal);
            Dictionary<string, TextSnapshot?> texts = new(StringComparer.Ordinal);
            return (IReadOnlyList<Place>)[.. places.Select(place =>
            {
                cancel.ThrowIfCancellationRequested();
                // A source generator's file is on no disk: named for what it is, in its own lines, with no text to read.
                if (DocumentUris.GeneratedName(place.Uri) is { } generated)
                {
                    return new Place($"{generated} (generated)", place.At.Line + 1, place.At.Character + 1, string.Empty);
                }
                string path = byUri[place.Uri];
                if (!texts.TryGetValue(path, out TextSnapshot? text))
                {
                    texts[path] = text = open.GetValueOrDefault(path) ?? ServerPlaces.Read(path);
                }
                // Gone or refused since the language server read it: its places are still places, without their text.
                TextPosition at = place.Server ? ServerPlaces.PositionOf(text, place.At) : new TextPosition(place.At.Line, place.At.Character);
                string line = text is not null && at.Line < text.LineCount ? text.GetLineText(at.Line).Trim() : string.Empty;
                return new Place(Relative(folder, path), at.Line + 1, at.Column + 1, line);
            })];
        }, cancel);
    }

    private Workspace Snapshot()
    {
        ProjectSession session = Session;
        OpenDocument? active = Editors.Active;
        TextPosition? caret = active is null ? null : Editors.CodeEditor.CaretPosition;
        return new Workspace(
            session.Folder,
            session.Workspace?.Filter?.Path ?? session.Solution?.Path,
            [.. (session.Workspace?.Projects ?? []).Select(p => new ProjectInfo(p.Name, Relative(p.Path), p.OutputType, p.TargetFrameworks)
            {
                Unloaded = session.Unloaded.Contains(p.Path) ? true : null,
            })],
            [.. session.Configurations.All.Select(c => c.Name)],
            session.Configurations.Chosen?.Name,
            session.CSharp is not { } csharp ? "none" : csharp.IsReady ? "ready" : csharp.State,
            [.. Editors.Documents.Select(d => Relative(d.Path) + (d.IsModified ? " (unsaved)" : ""))],
            active is null ? null : $"{Relative(active.Path)}:{caret!.Value.Line + 1}:{caret.Value.Column + 1}",
            Session.Debug.Session?.State.ToString().ToLowerInvariant() ?? "not debugging")
        {
            SdkMissing = session.Models.MissingSdk is { } missing ? Refusals.NoSdk(missing, session.Folder) : null,
            UnreadSolution = session.Workspace?.Unread is { } unread ? UnreadSolutionSaid(unread) : null,
        };
    }

    /// <summary>A solution that does not read as an agent is told it: its path, where its reader stopped as the editor
    /// counts the file's lines, and why.</summary>
    private string UnreadSolutionSaid(global::Passenger.Workspace.UnreadSolution unread) =>
        ServerPlaces.XmlPositionOf(Editors, unread.Path, unread.Line, unread.Column) is { } at
            ? $"{Relative(unread.Path)}:{at.Line + 1}:{at.Column + 1}: {unread.Why}"
            : $"{Relative(unread.Path)}: {unread.Why}";

    /// <summary>A path an agent gave, from the open folder unless rooted, as the IDE names it (in any letter case).</summary>
    private string Resolve(string path) =>
        PathNames.Canonical(Path.IsPathRooted(path) || Session.Folder is not { } folder ? path : Path.Combine(folder, path));

    private string Relative(string path) => Relative(Session.Folder, path);

    private static string Relative(string? folder, string path) => folder is not null && PathNames.IsUnder(path, folder)
        ? Path.GetRelativePath(folder, path)
        : path;

    /// <summary>The last lines of a terminal's scrollback and screen, as a program in it left them.</summary>
    private static string Tail(global::Passenger.Terminal.TerminalSession terminal, int lines)
    {
        global::Passenger.Terminal.TerminalScreen screen = terminal.Screen;
        List<string> all = [.. screen.Scrollback.Select(l => l.Text()).Concat(screen.Lines.Select(l => l.Text()))];
        while (all.Count > 0 && all[^1].Length == 0)
        {
            all.RemoveAt(all.Count - 1);
        }
        return string.Join('\n', all.Skip(Math.Max(0, all.Count - lines)));
    }

    /// <summary>The last lines of what Run's program wrote, where it runs: its tab of the terminal, else the Run console.</summary>
    private string RunOutput(int lines) => Session.RunProgram is TerminalRun terminal ? Tail(terminal.Session, lines) : Tail(Session.Runner.Shown(), lines);

    /// <summary>A console's last lines.</summary>
    private static string Tail(TextSnapshot text, int lines)
    {
        int first = Math.Max(0, text.LineCount - lines);
        return text.GetText(text.GetLineStart(first), text.Length - text.GetLineStart(first));
    }

    /// <summary>Waits on the UI thread, letting it run, until something holds, the time is up, or the agent no longer
    /// wants the answer, looking again every 50 ms: only where what it waits on raises nothing as it changes, each such
    /// place saying why; everywhere else <see cref="When"/>.</summary>
    private static async Task Until(Func<bool> condition, TimeSpan limit, CancellationToken cancel)
    {
        DateTime until = DateTime.UtcNow + limit;
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(50, cancel);
        }
    }

    /// <summary>What can make a wait's condition hold, told to look again (an event of what the condition reads,
    /// <see cref="On"/>, or a task's end, <see cref="Ends"/>), giving back how it lets go.</summary>
    private delegate Action Wake(Action look);

    /// <summary>An event that may make a wait's condition hold, looked at again on the UI thread as it is raised.</summary>
    private static Wake On(Action<EventHandler> add, Action<EventHandler> remove) => look =>
    {
        EventHandler heard = (_, _) => UiThread.Post(look);
        add(heard);
        return () => remove(heard);
    };

    /// <summary>A task whose end may make a wait's condition hold, however it ends.</summary>
    private static Wake Ends(Task task) => look =>
    {
        IdeErrors.Observe(task.ContinueWith(_ => UiThread.Post(look), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default));
        return () => { };
    };

    /// <summary>Waits on the UI thread until something holds, the time is up, or the agent no longer wants the answer: its
    /// condition looked at again only as what can change it says so (<paramref name="wakes"/>), never on a timer, so the
    /// window spends nothing on the wait and the answer comes as the condition holds.</summary>
    private static async Task When(Func<bool> condition, TimeSpan limit, CancellationToken cancel, params Wake[] wakes)
    {
        if (condition() || limit <= TimeSpan.Zero)
        {
            return;
        }
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Look()
        {
            if (!held.Task.IsCompleted && condition())
            {
                held.TrySetResult();
            }
        }
        Action[] letGo = [.. wakes.Select(wake => wake(Look))];
        try
        {
            // What changed between the first look and the events' subscription is looked at once more.
            Look();
            await held.Task.WaitAsync(limit, cancel);
        }
        catch (TimeoutException)
        {
            // The time is up: the caller says what never came, as it reads the condition again.
        }
        finally
        {
            foreach (Action go in letGo)
            {
                go();
            }
        }
    }

    /// <summary>The C# server's state changing, as its load lands or it stops: what a wait for the workspace's load wakes on.</summary>
    private Wake ServerState() => Session.CSharp is { } csharp ? On(h => csharp.StateChanged += h, h => csharp.StateChanged -= h) : _ => () => { };

    /// <summary>A debugged program's state changing: running, paused (a stop's reason with it), ended.</summary>
    private static Wake StateOf(DebugSession? session) => session is null ? _ => () => { } : On(h => session.StateChanged += h, h => session.StateChanged -= h);
}

public sealed record Workspace(
    string? Folder,
    string? Solution,
    IReadOnlyList<ProjectInfo> Projects,
    IReadOnlyList<string> RunConfigurations,
    string? ChosenConfiguration,
    string LanguageServer,
    IReadOnlyList<string> OpenFiles,
    string? ActiveFile,
    string Debugger)
{
    public IReadOnlyList<AspireOwnedAppInfo> AspireApps { get; init; } = [];

    /// <summary>Why the folder has no .NET SDK to load, build and run its projects with, as Run refuses for it; null with one.</summary>
    public string? SdkMissing { get; init; }

    /// <summary>The container run under way, beside the AppHosts, its services as their resources are; null with none.</summary>
    public ContainerRunInfo? ContainerRun { get; init; }

    /// <summary>The web projects' routes, when asked for.</summary>
    public IReadOnlyList<RouteInfo>? Routes { get; init; }

    /// <summary>The solution the folder opens by that does not read ("path:line:column: why"), in whose place the projects
    /// under the folder are open; null with none.</summary>
    public string? UnreadSolution { get; init; }
}

/// <summary>A route as an agent reads it: its project, what declares it, its methods and template ("GET /todos/{id:int}"),
/// its handler, its parameters ("id:int (route)"), where it is declared, and its body's type.</summary>
public sealed record RouteInfo(string Project, string Kind, string Route, string Handler, IReadOnlyList<string> Parameters, string Path, int Line, string? Body);

public sealed record ProjectInfo(string Name, string Path, string? OutputType, IReadOnlyList<string> TargetFrameworks)
{
    /// <summary>True for a project not loaded: one the solution filter opened leaves out, or one the person unloaded; null
    /// for one loaded, which the answer leaves unsaid.</summary>
    public bool? Unloaded { get; init; }
}

public sealed record Finding(string Severity, string? Code, string Message, string? Path, int Line, int Column);

public sealed record Place(string Path, int Line, int Column, string Text);

/// <summary>usages' answer: the C# symbol the place took, as its card names it, how many places, and each file's places as
/// "line:column text".</summary>
/// <summary>usages' answer, and what it went without (files the C# language server has yet to take in), when it did.</summary>
public sealed record UsagesFound(string? Symbol, int Count, IReadOnlyList<FilePlaces> Files, string? Note = null);

public sealed record FilePlaces(string Path, IReadOnlyList<string> Places);

public sealed record HierarchyEntry(string? Name, string? Detail, string Path, int Line, int Column, string Text);

/// <summary>A build's answer: MSBuild's last lines only when it failed, since a build that succeeded has nothing in them
/// an agent acts on (output of build has them).</summary>
public sealed record BuildResult(bool Succeeded, string Outcome, int Errors, int Warnings, IReadOnlyList<Finding> Problems, string? OutputTail)
{
    /// <summary>The folder a publish landed in, once it did.</summary>
    public string? Folder { get; init; }
}

public sealed record RunResult(bool StillRunning, int? ExitCode, string Status, string Output);

/// <summary>A breakpoint as an agent reads it: a line breakpoint's path and line, an exception breakpoint's type and scenarios,
/// what a data breakpoint watches ("count on counter (Counter)"), or a property breakpoint's property and element ("Text on
/// TextBlock#Counter", and the element's @id).</summary>
public sealed record BreakpointInfo(string? Path, int? Line, bool Enabled, string Status, string? Condition, string? HitCondition, string? LogMessage, bool Suspend,
    string? Message, string? Exception = null, IReadOnlyList<string>? Scenarios = null, string? Data = null, string? Attribute = null, string? Property = null,
    bool? BindingErrors = null, string? Control = null, int? Column = null);

public sealed record VariableInfo(string Name, string Value, string? Type, int Reference)
{
    /// <summary>Where the value runs past what an answer shows: its length and how debug_evaluate reads on.</summary>
    public string? Cut { get; init; }
}

/// <summary>A .NET program the debugger can attach to, as Attach to Process lists it, its command line's secrets masked.</summary>
public sealed record ProcessInfo(int ProcessId, string Executable, string Program, string User, string CommandLine, string? Container = null);

/// <summary>Where the debugger is in a program, as the debug tools answer: its session's state (<see cref="Debugged"/>, null
/// with none debugged or once it ended), said as <see cref="State"/>, and while paused the stop.</summary>
public sealed record DebugSnapshot(
    [property: JsonIgnore] DebugState? Debugged,
    string? Reason,
    string? Location,
    IReadOnlyList<string> Frames,
    IReadOnlyList<VariableInfo> Locals,
    int? ExitCode,
    string? Status,
    string? Exception = null)
{
    /// <summary>The state in an agent's words: starting, running or paused, else not debugging.</summary>
    [JsonPropertyOrder(-1)]
    public string State => Debugged is { } state ? state.ToString().ToLowerInvariant() : "not debugging";

    /// <summary>At a stop in a debugged Avalonia app, the binding values after the lines of the .axaml files the editors show,
    /// as the editor shows them (path:line: Property: value).</summary>
    public IReadOnlyList<string>? BindingValues { get; init; }

    /// <summary>A debugged test run that ended as the program went on: its tally and failures, as tests_run answers them.</summary>
    public TestRunInfo? Tests { get; init; }

    /// <summary>With several programs debugged, the one this is.</summary>
    public string? Session { get; init; }

    /// <summary>With several programs debugged, each one's name and state.</summary>
    public IReadOnlyList<string>? Sessions { get; init; }
}

public sealed record TestRunInfo(string Summary, IReadOnlyList<TestFailure> Failures, DebugSnapshot? Debugger = null);

public sealed record TestFailure(string Test, string? Location, string Details);

public sealed record TestInfoRow(string Test, string State, string? Location);

public sealed record SymbolFound(string Name, string Kind, string? Container, string Path, int Line, int Column)
{
    /// <summary>Its documentation id, as an exact find gives it.</summary>
    public string? Id { get; init; }
}
