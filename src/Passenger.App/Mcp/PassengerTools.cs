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
/// whether it is Destructive: a person's window asks before an agent's call of one that is not declared harmless.
/// What a folder has and runs a tool asks of the window's session (<see cref="ProjectSession"/>) as the call finds it, and
/// what it starts it starts through the session's controllers; the window itself only for what is the window's: opening
/// a folder in it, its editors, its terminal, its actions and what it draws. No tool reads the status bar.
/// <paramref name="connection"/> is the agent's, through which a person's window asks before the agent replaces its folder;
/// none is an agent's own IDE's.</summary>
public sealed partial class PassengerTools(MainWindow window, AgentConnection? connection = null)
{
    /// <summary>The variable that gives <see cref="ServerLimit"/> in milliseconds.</summary>
    [IdentifierHome]
    public const string ServerLimitVariable = "PASSENGER_MCP_SERVER_WAIT_MS";

    /// <summary>How long a code tool waits for its language server to be ready for its question before it refuses: 2
    /// minutes, made longer on a slower machine as the IDE's other waits on what it started are (<see cref="Waits"/>), or
    /// what <see cref="ServerLimitVariable"/> gives; a value there that is no whole number of milliseconds above 0 is fatal.</summary>
    public static TimeSpan ServerLimit => EnvironmentValues.Milliseconds(ServerLimitVariable, Waits.Of(TimeSpan.FromSeconds(120)));

    /// <summary>How long a file's markers are waited for: its checks are quick once its project is read.</summary>
    private static readonly TimeSpan MarkersLimit = TimeSpan.FromSeconds(30);

    /// <summary>How long an action waits for a grammar to read the file it asks of.</summary>
    private static TimeSpan GrammarLimit => Waits.Of(TimeSpan.FromSeconds(60));

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
        string full = Named(path);
        // Opened once what another folder ran has ended.
        if (Directory.Exists(full) || (File.Exists(full) && WorkspaceModel.Opens(full) && line is null))
        {
            string opening = Directory.Exists(full) ? PathNames.Canonical(full) : Path.GetDirectoryName(PathNames.Canonical(full))!;
            if (Session.Folder is { } left && !PathNames.Same(left, opening) && window.WhyNotLeave($"opening {full}") is { } held)
            {
                throw new InvalidOperationException(held);
            }
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
            // Its disk's text taken first, as every tool naming a file does (refused where the agent may not read it), then shown.
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
        await Session.NodeFilesRead.WaitAsync(cancel);
        await Task.Yield();
        await Session.Models.Idle.WaitAsync(cancel);
        return HeldWorkspace(await RunsWorkspace(Snapshot(), cancel));
    });

    [McpServerTool(Name = McpTools.RequestFullAccess, Title = "Ask for every tool", Destructive = false), Description(
        "On a person's window, asks them once to let every tool go on without a question at each call until the agent disconnects " +
        "or the window opens another folder, and answers whether they did; denied, a later request is refused unasked, each tool still asking.")]
    public Task<string> RequestFullAccess(CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        (string said, bool refused) = connection is null ? (AgentConnection.NothingAsks, false) : await connection.AllowEveryToolAsync(window, AgentCall.Current, cancel);
        cancel.ThrowIfCancellationRequested();
        return refused ? throw new InvalidOperationException(said) : said;
    }, projects: false);

    [McpServerTool(Name = McpTools.Workspace, Title = "The workspace", ReadOnly = true), Description(
        "The open folder and solution (or solution filter), its projects (kind, output type, frameworks and the one chosen, unloaded marked; a shared one with sharedBy), run " +
        "configurations (the chosen one marked), the build configuration every build takes and the others, whether the folder is trusted, the language server's state, the open files (unsaved marked) and the active " +
        "one with its caret, and the Aspire AppHosts and container run under way, each resource with its state, hold, debug " +
        "session and target. routes adds the web projects' routes, read from their code.")]
    public Task<Workspace> GetWorkspace(bool routes = false, string? within = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        string? inside = Within(within);
        Workspace answered = await RunsWorkspace(Snapshot(inside), cancel);
        if (!routes)
        {
            return HeldWorkspace(answered);
        }
        if (Session.CSharp is null)
        {
            throw new InvalidOperationException($"{Session.WhyNoCSharp}, and routes are read from code through it");
        }
        await SettledServer(cancel);
        IReadOnlyList<Roslyn.Extension.WebRoute> found = await window.RoutesAsync(cancel);
        return HeldWorkspace(answered with
        {
            Routes = [.. found.Where(r => inside is null || PathNames.Within(r.Path, inside)).Select(r => new RouteInfo(Path.GetFileNameWithoutExtension(r.Project), r.Kind,
                Endpoints.RouteRequests.Title(r), r.Handler,
                [.. r.Parameters.Select(p => $"{p.Name}{(p.Constraints is { } c ? ":" + c : p.Type is { } t ? ": " + t : string.Empty)}{(p.Optional ? "?" : string.Empty)} ({p.Source})")],
                Relative(r.Path), ServerPlaces.PositionOf(Editors, r.Path, new LspPosition(r.At.Line, r.At.Character)).Line + 1, r.BodyType))],
        });
    });

    /// <summary>workspace's answer held within <see cref="AgentAnswers.MostCharacters"/>: its projects, run configurations,
    /// open files and routes whole where they fit; else each list in turn given its share of the room left beside those
    /// after it (<see cref="AgentAnswers.Shares"/>), the first projects whole and those after named alone where room is
    /// short (<see cref="AgentAnswers.Grouped"/>), the first of each other list whole and the rest left out, its more saying
    /// how many and that within lists those of a folder or file.</summary>
    private static Workspace HeldWorkspace(Workspace answered)
    {
        IReadOnlyList<ProjectInfo> projects = answered.Projects;
        IReadOnlyList<RouteInfo> routes = answered.Routes ?? [];
        int room = Room(answered with { Projects = [], RunConfigurations = [], OpenFiles = [], Routes = answered.Routes is null ? null : [] });
        int[] needs = [Need(projects), Need(answered.RunConfigurations), Need(answered.OpenFiles), Need(routes)];
        if (needs.Sum() <= room)
        {
            return answered;
        }
        (int whole, int named) = AgentAnswers.Grouped(projects, Size, p => Size(Named(p)), Share(0));
        ProjectInfo[] kept = [.. projects.Take(whole), .. projects.Skip(whole).Take(named).Select(Named)];
        room -= Need(kept);
        int configurations = Kept(answered.RunConfigurations, 1);
        int files = Kept(answered.OpenFiles, 2);
        int routesKept = Kept(routes, 3);
        int projectsLeft = projects.Count - kept.Length;
        int configurationsLeft = answered.RunConfigurations.Count - configurations;
        int filesLeft = answered.OpenFiles.Count - files;
        int routesLeft = routes.Count - routesKept;
        string?[] said =
        [
            named == 0 ? null : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{named:N0} {Plural.Noun(named, "project")} named alone"),
            projectsLeft == 0 ? null : Left(projectsLeft, "project"),
            configurationsLeft == 0 ? null : Left(configurationsLeft, "run configuration"),
            filesLeft == 0 ? null : Left(filesLeft, "open file"),
            routesLeft == 0 ? null : Left(routesLeft, "route"),
        ];
        string?[] cut =
        [
            named + projectsLeft == 0 ? null : "projects",
            configurationsLeft == 0 ? null : "run configurations",
            filesLeft == 0 ? null : "open files",
            routesLeft == 0 ? null : "routes",
        ];
        return answered with
        {
            Projects = kept,
            RunConfigurations = [.. answered.RunConfigurations.Take(configurations)],
            OpenFiles = [.. answered.OpenFiles.Take(files)],
            Routes = answered.Routes is null ? null : [.. routes.Take(routesKept)],
            More = $"{Plural.Listed([.. said.OfType<string>()])}: within, a folder or file, lists the {Plural.Listed([.. cut.OfType<string>()])} there whole",
        };

        // A list's share of the room left, beside the lists after it.
        int Share(int list) => AgentAnswers.Shares(needs[list..], room)[0];

        // How many of a list's first entries its share takes, the room they fill no longer left.
        int Kept<T>(IReadOnlyList<T> list, int at)
        {
            int count = AgentAnswers.Fitting(list, Size, Share(at));
            room -= Need(list.Take(count));
            return count;
        }

        static int Need<T>(IEnumerable<T> list) => list.Sum(entry => Size(entry) + 1);

        // A project named alone, its kind and frameworks left to within.
        static ProjectInfo Named(ProjectInfo project) => new(project.Name, project.Path, null, null);

        static string Left(int left, string noun) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{left:N0} more {Plural.Noun(left, noun)} left out");
    }

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
    public Task<string> EditorText(string path, int? from = null, int? to = null, int? column = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        OpenDocument document = await TextualAsync(path, cancel);
        TextSnapshot text = document.Text.Snapshot;
        int first = LineIn(text, document.Path, from ?? 1);
        if (to is int end && end <= first)
        {
            throw new ArgumentException($"to {end} is before from {first + 1}");
        }
        int length = text.GetLine(first).Length;
        if (column is int at && (at < 1 || at > length + 1))
        {
            throw new ArgumentException($"Column {at} is outside line {first + 1}, whose columns are 1 to {length + 1}");
        }
        int last = Math.Min((to ?? text.LineCount) - 1, text.LineCount - 1);
        return Numbered(text, first, last, (column ?? 1) - 1);
    });

    /// <summary>A text's lines from <paramref name="first"/> to <paramref name="last"/> (from 0) numbered as editor_text
    /// numbers them, the first from its <paramref name="column"/> (from 0), held within <see cref="AgentAnswers.MostCharacters"/>:
    /// those that fit, at least the first, cut where it alone runs past, then what was left out and the from (and column)
    /// that reads on, a cut line's pieces joining whole.</summary>
    private static string Numbered(TextSnapshot text, int first, int last, int column = 0)
    {
        System.Text.StringBuilder shown = new();
        int room = AgentAnswers.MostCharacters - WordsRoom;
        int at = first;
        for (; at <= last; at++)
        {
            LineSpan line = text.GetLine(at);
            int from = at == first ? column : 0;
            string number = $"{at + 1,6}\t";
            int length = number.Length + line.Length - from;
            if (length + 1 > room && at > first)
            {
                break;
            }
            int kept = Math.Min(line.Length - from, Math.Max(0, room - number.Length));
            if (kept < line.Length - from && kept > 0 && char.IsHighSurrogate(text[line.Start + from + kept - 1]))
            {
                kept--;
            }
            shown.Append(at > first ? "\n" : string.Empty).Append(number).Append(text.GetText(line.Start + from, kept));
            if (kept < line.Length - from)
            {
                int after = last - at;
                return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{shown}\nLine {at + 1} is cut short, {line.Length - from - kept:N0} of its {line.Length:N0} characters left out"
                    + $"{(after > 0 ? $" and {Numbers.Said(after)} more {Plural.Noun(after, "line")} after it" : string.Empty)}: from {at + 1} with column {from + kept + 1} reads on");
            }
            room -= length + 1;
        }
        return at > last ? shown.ToString() : $"{shown}\n{AgentAnswers.More(last - at + 1, "line", $"from {at + 1} reads on")}";
    }

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
            string names = string.Join(", ", left.Select(d => FileText.IsReadOnly(d.Path) ? $"{Relative(d.Path)} (read-only)"
                : Editors.HeldElsewhere(d.Path) is { } held ? $"{Relative(d.Path)} ({Refusals.HeldInGodot(held)})" : Relative(d.Path)));
            throw new InvalidOperationException($"Saved {Numbers.Said(unsaved - left.Count)} of {Numbers.Said(unsaved)} files; not saved: {names}");
        }
        return unsaved == 0 ? "Nothing to save" : $"Saved {Plural.Of(unsaved, "file")}";
    });

    /// <summary>Saves every changed file and comes back once each save has landed (<see cref="Passenger.App.Editors.EditorArea.LandedAsync"/>)
    /// and each file moved is moved in Git's index too (<see cref="Vcs.VcsController.MovesIndexed"/>), so the agent, its shell and
    /// what it starts read what was saved and git status shows a rename; a save that never lands fails the call, saying why.</summary>
    private async Task SaveLandedAsync(CancellationToken cancel)
    {
        Editors.SaveAll();
        await LandedAsync(cancel);
        await Session.Vcs.MovesIndexed.WaitAsync(cancel);
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
        "A file's errors, warnings and notes as the editor marks them, fresh: C#'s from the language server (its messages too), any " +
        "other's once its checks have run (XAML's, an MSBuild file's evaluation and NuGet advisories, a web language's server " +
        "and the folder's linters). With no path, or a folder's, what the Problems window lists beyond a file's (within it): toolset (each " +
        "project's load warnings and errors), build (the last build's, whoever started it) and solution (every file's, while " +
        "settings' solutionWideAnalysis is on), errors first, by file, at most 200 in all; project (a name) narrows these. " +
        "severity (error, warning, info, hint) narrows either.")]
    public async Task<object> Diagnostics(string? path = null, string? severity = null, string? project = null, int? from = null, CancellationToken cancel = default)
    {
        if (severity is not null && Rank(severity) is null)
        {
            throw new ArgumentException($"severity takes error, warning, info or hint, not '{severity}'");
        }
        string? full = path is null ? null : await OnUi(cancel, () => Task.FromResult(Resolve(path)));
        if (path is not null && !Directory.Exists(full))
        {
            if (project is not null)
            {
                throw new ArgumentException("project narrows the Problems window's lists, which diagnostics answers with no path or a folder's; a file's are its own");
            }
            IReadOnlyList<Finding> found = await FileDiagnostics(path, cancel);
            return HeldFindings([.. found.Where(f => (severity is null || f.Severity == severity) && (from is null || f.Line >= from))]);
        }
        return from is null ? await Problems(full, severity, project, cancel)
            : throw new ArgumentException("from reads on a file's findings: the Problems window's lists, with no path or a folder's, take severity, project or a path within", nameof(from));
    }

    /// <summary>A file's findings as an answer holds them (<see cref="AgentAnswers.Severest"/>): all of them in their order
    /// where they fit within <see cref="AgentAnswers.MostCharacters"/>, else the severest first, each severity's by place,
    /// followed by how many of each severity were left out and the severity and from that read on.</summary>
    private static object HeldFindings(IReadOnlyList<Finding> found)
    {
        (IReadOnlyList<Finding>[] kept, int[] left) = AgentAnswers.Severest<Finding>([[.. found.OrderBy(f => f.Line).ThenBy(f => f.Column)]], f => Rank(f.Severity) ?? 2, 4,
            int.MaxValue, Size, AgentAnswers.MostCharacters - WordsRoom);
        if (left.Sum() == 0)
        {
            return found;
        }
        Finding next = found.Except(kept[0]).OrderBy(f => Rank(f.Severity) ?? 2).ThenBy(f => f.Line).ThenBy(f => f.Column).First();
        return Listed(kept[0], AgentAnswers.More(left.Sum(), "finding", $"{LeftOut(left)}; severity {next.Severity} with from {next.Line} lists those from that line on"));
    }

    /// <summary>How many findings of each severity an answer left out, the severest first: "1,800 warnings and 3 hints".</summary>
    private static string LeftOut(int[] left)
    {
        string[] nouns = ["error", "warning", "message", "hint"];
        return Plural.Listed([.. left.Select((count, rank) => count == 0 ? null
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{count:N0} {Plural.Noun(count, nouns[rank])}")).OfType<string>()]);
    }

    /// <summary>A finding's severity as diagnostics orders them, the severest first; null for no severity it names.</summary>
    private static int? Rank(string severity) => severity switch
    {
        "error" => 0,
        "warning" => 1,
        "info" => 2,
        "hint" => 3,
        _ => null,
    };

    private Task<IReadOnlyList<Finding>> FileDiagnostics(string path, CancellationToken cancel) => OnUi(cancel, async () =>
    {
        if (await DocumentAsync(path, cancel) is { Language.Service: not Languages.LanguageService.CSharp } marked)
        {
            if (Session.ChecksOf(marked).Count == 0)
            {
                // No marks from no check would read as a file found clean.
                throw new InvalidOperationException($"No check of Passenger's reads {LanguageOf(marked)} files such as {Path.GetFileName(marked.Path)}: diagnostics answers C#, "
                    + "Razor, a C# project's XAML, MSBuild files, JSON, YAML, HTML, CSS, SCSS, Less, JavaScript, TypeScript, Dockerfiles and compose files, "
                    + "Godot's scenes, shaders and scripts, and .editorconfig, .slnx and .http files");
            }
            string? unmatched = Bdd.FeatureChecks.IsFeature(marked.Path) ? await FeatureMatchedAsync(marked, cancel) : null;
            TimeSpan waited = await Checked(marked, cancel);
            List<Finding> missing = [];
            if (unmatched is not null)
            {
                missing.Add(new Finding("info", null, unmatched, Relative(marked.Path), 1, 1));
            }
            if (marked.Unanswered is { Count: > 0 } unanswered)
            {
                // Each check that has not answered for the text as it stands, within the wait or its own.
                string who = Plural.Agreeing(unanswered.Count, "it", "they");
                string within = marked.IsChecking ? $", still checking after a wait of {Durations.Said(waited)}" : string.Empty;
                missing.Add(new Finding("info", null, $"Not yet checked as it stands by {string.Join(", ", unanswered)}{within}: what {who} found before is listed",
                    Relative(marked.Path), 1, 1));
            }
            // A GDScript's own checks are Passenger's reading's, Godot's server or none.
            if (Session.Web is { } web && web.For(marked) is { Lsp: null, Kind: not WebServerKind.GDScript })
            {
                if (Languages.Language.ForPath(marked.Path) != Languages.Language.Dockerfile && !global::Passenger.App.Containers.ComposeFiles.IsCompose(marked.Path))
                {
                    // A web file whose server cannot start has no checks: none found would read as none there.
                    throw new InvalidOperationException(web.Unready("Its diagnostics", marked));
                }
                // A Dockerfile's or compose file's container engine checks it without the server: its findings, and a word that the server's are missing.
                missing.Add(new Finding("info", null, web.Unready("Checking it beyond the container engine", marked), Relative(marked.Path), 1, 1));
            }
            TextSnapshot text = marked.Text.Snapshot;
            return (IReadOnlyList<Finding>)[.. marked.Markers.Where(m => m.Kind is not MarkerKind.Unnecessary).Select(m =>
            {
                TextPosition at = text.GetPosition(Math.Min(m.Range.Start, text.Length));
                return new Finding(m.Kind switch { MarkerKind.Error => "error", MarkerKind.Warning => "warning", _ => "info" }, m.Code, m.Said, Relative(marked.Path), at.Line + 1, at.Column + 1);
            }), .. missing];
        }
        if (await DocumentAsync(path, cancel) is { Syntax: { } syntax } deep && (await syntax.CurrentAsync()).DeepAt is int at)
        {
            // Read as nothing, a file is never the server's: its note is all there is to say of it.
            TextPosition place = deep.Text.Snapshot.GetPosition(Math.Min(at, deep.Text.Snapshot.Length));
            return (IReadOnlyList<Finding>)[new Finding("info", null, Languages.CSharp.CSharpParsing.TooDeep, Relative(deep.Path), place.Line + 1, place.Column + 1)];
        }
        (OpenDocument document, LspDocument lsp) = await Code(path, cancel);
        (IReadOnlyList<Diagnostic> found, _) = await lsp.LastDiagnosticsAsync(Session.CSharp?.SolutionWide == true, cancel);
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

    /// <summary>Waits until a feature's marks are of its steps matched against the step definitions as the code stands: the
    /// C# language server settled on the folder, as every C# question waits for it (refused saying so while it still loads),
    /// the bindings read and the file read against them. Null once they are; else why its steps are matched against none,
    /// its project's failed load among them, the server then holding none of the project's definitions.</summary>
    private async Task<string?> FeatureMatchedAsync(OpenDocument feature, CancellationToken cancel)
    {
        if (Session.CSharp is null)
        {
            return Refusals.StepsUnmatched(serverRuns: false);
        }
        await ServerSettledAsync(feature.Path, _ => true, cancel);
        await window.StepsAsync(cancel);
        if (!Session.Steps.HasRead)
        {
            return Refusals.StepsUnmatched(serverRuns: Session.CSharp is { IsReady: true });
        }
        Bdd.FeatureAnalysis? read = await Session.Features.CurrentAsync(feature);
        // A file read with no step (one its reader finds no feature in) has none to match.
        return read is { Reading.Steps.Count: > 0 } && Session.ProjectOf(feature.Path)?.Path is { } project && Session.CSharp?.FailedLoadOf(project) is { } failed
            ? Refusals.StepsUnmatched(Relative(project), failed.Message)
            : null;
    }

    /// <summary>Waits, within <see cref="MarkersLimit"/>, for a file's own checks of its text as it stands (<see
    /// cref="OpenDocument.IsChecking"/>), which its markers and the fixes for them answer from, until the turn it comes back in
    /// finds none under way: a check begun as the last one ended is waited for too, and its caller reads them in that turn.
    /// The rest of the IDE's work (a build, a test run, a busy console) is not waited for. Gives back how long it waited.</summary>
    private static async Task<TimeSpan> Checked(OpenDocument document, CancellationToken cancel)
    {
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        while (document.IsChecking && System.Diagnostics.Stopwatch.GetElapsedTime(began) is var waited && waited < MarkersLimit)
        {
            await When(() => !document.IsChecking, MarkersLimit - waited, cancel, On(h => document.Checked += h, h => document.Checked -= h));
        }
        return System.Diagnostics.Stopwatch.GetElapsedTime(began);
    }

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
        await ExpressionSettledAsync(served, offset, cancel);
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
        "and AddDotnetProject and the resource names a service reads; an MSBuild file's properties (where set, the one in force first), item types, " +
        "targets, imports and SDKs.")]
    public Task<IReadOnlyList<Place>> Definition(string? path = null, int? line = null, int? column = null, string? symbol = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        CodePlace at = await AskedAt(path, line, column, symbol, cancel);
        Served served = await Serve(at.Path, cancel);
        int offset = Offset(served.Document, at.Line, at.Column);
        await ExpressionSettledAsync(served, offset, cancel);
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
        "element's name in its file, code-behind and C# expressions, a resource's key across the project, or a C# type or " +
        "member as from its declaration; a Reqnroll step's or step definition's, every step it binds in .feature files. JSON " +
        "and MSBuild files have none.")]
    public Task<UsagesFound> Usages(string? path = null, int? line = null, int? column = null, string? symbol = null, string? within = null,
        int? from = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        string? inside = Within(within);
        if (from is not null && (inside is null || !File.Exists(inside)))
        {
            throw new ArgumentException("from reads on a file's places: within names that file", nameof(from));
        }
        CodePlace at = await AskedAt(path, line, column, symbol, cancel);
        // A GDScript's member or local is found by Passenger's reading of the project's scripts, before Godot's server serves it.
        if (Godot.GodotScriptTypes.IsScript(at.Path))
        {
            OpenDocument script = await DocumentAsync(at.Path, cancel);
            if (await window.Assist.GDScriptUsesAsync(script, Offset(script, at.Line, at.Column)) is ({ } scriptUses, { } unsure))
            {
                return ByFile(inside, from, null, await Places(scriptUses, cancel)) with
                {
                    Note = unsure.Count > 0 ? $"May use it where Passenger can't see whose member is read: {Passenger.Workspace.GDScript.GDScriptRefactorings.Places(unsure)}" : null,
                };
            }
        }
        Served served = await Serve(at.Path, cancel);
        int offset = Offset(served.Document, at.Line, at.Column);
        string place = $"{Relative(served.Document.Path)}:{at.Line}:{at.Column}";
        switch (served)
        {
            case XamlServed xaml:
                return ByFile(inside, from, null, await XamlUsages(xaml, offset, cancel) is { Count: > 0 } uses ? uses
                    : throw new InvalidOperationException($"No name, resource key or C# symbol at {place}: {XamlAssist.XamlUsesWhat}"));
            case MSBuildServed:
                throw Lacks(served, "Find Usages", MSBuildHas);
            case GodotServed:
                throw Lacks(served, "Find Usages", GodotHas);
            case ScriptServed script:
                script.Web.AskServer?.Invoke();
                throw new InvalidOperationException($"No member or local of a script is at {place} as Passenger reads the project's scripts, and no Godot server serves "
                    + $"{Path.GetFileName(script.Document.Path)} yet to be asked: one is starting for it");
            case ShaderServed shader:
                return await window.Assist.Languages.Shaders.UsesAsync(shader.Document, offset) is (_, { } shaderUses)
                    ? ByFile(inside, from, null, await Places(shaderUses, cancel))
                    : throw new InvalidOperationException($"No uniform, varying, function, struct or constant the shader declares is at {place}");
            case FeatureServed feature:
                return await window.Assist.Languages.Gherkin.AgentUsesAsync(feature.Document, offset) is ({ } stepsOf, { } stepPlaces)
                    ? ByFile(inside, from, stepsOf, await Places(stepPlaces, cancel))
                    : throw new InvalidOperationException($"No step a step definition binds is at {place}");
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
        // A Godot name's uses Passenger finds itself (a GDScript's member in scenes and C#, a string Godot's calls take), as Find Usages does.
        IReadOnlyList<LspLocation> godot = component is null ? await window.Assist.Languages.CSharp.GodotUsesAsync(served.Document, server.Lsp, offset) : [];
        if (found.Count == 0 && godot.Count == 0)
        {
            throw new InvalidOperationException($"No symbol at {place}: {server.Language}'s language server finds no usages there");
        }
        // Passenger's extension reads a C# file's symbols, not a Razor file's.
        Roslyn.Extension.NamedSymbol? named = component?.Symbol
            ?? (served.Document.Language == Languages.Language.CSharp ? await server.Lsp.SymbolAtAsync(offset, cancel) : null);
        string? taken = named is null ? null : $"{named.Shown} ({named.Kind})";
        // A step definition's steps, which no language server knows, beside its C# uses.
        IReadOnlyList<LspLocation> steps = served.Document.Language == Languages.Language.CSharp
            && await window.Assist.Languages.Gherkin.AgentUsesAsync(served.Document, offset) is (_, { } bound) ? bound : [];
        found = [.. found, .. await window.Assist.Languages.Xaml.CodeUsesAsync(named, served.Document.Path), .. DocumentUris.Besides(godot, found), .. steps];
        // Answered now, saying which files that could use it the server has yet to take in.
        IReadOnlyList<(string Path, string Project)> untaken = server.Web is null && Session.CSharp is { } csharp ? await csharp.UntakenAsync(served.Document, offset, cancel) : [];
        // A project whose scripts no scene reaches lists none of its scenes, and says why.
        string[] notes = [.. new[] { untaken.Count > 0 ? Refusals.Untaken([.. untaken.Select(u => Relative(u.Path))]) : null, Session.ProjectOf(served.Document.Path)?.GodotLayoutProblem }.OfType<string>()];
        (IReadOnlyList<Place> places, IReadOnlySet<string> generated) = await SetApart(found, cancel);
        return ByFile(inside, from, taken, places, generated, notes.Length > 0 ? string.Join(". ", notes) : null);
    });

    /// <summary>usages' answer: its places within <paramref name="within"/> (a full path, else every one), from the line
    /// <paramref name="from"/> on where given, in order (<see cref="Sorted"/>), each file named once, those of
    /// <paramref name="generated"/> said to be generated code; held within <see cref="AgentAnswers.MostCharacters"/> (<see
    /// cref="AgentAnswers.Grouped"/>): every file it can counted, the first files' places listed, and one file alone its
    /// first places, saying what it left out.</summary>
    private UsagesFound ByFile(string? within, int? from, string? symbol, IReadOnlyList<Place> places, IReadOnlySet<string>? generated = null, string? note = null)
    {
        IReadOnlyList<Place> asked = Sorted([.. places.Where(p => (within is null || Inside(p.Path, within)) && (from is null || p.Line >= from))], generated);
        List<FilePlaces> files = [.. asked.GroupBy(p => p.Path, StringComparer.Ordinal)
            .Select(file => new FilePlaces(file.Key, [.. file.Select(Shown)])
            {
                Generated = generated?.Contains(file.Key) == true ? true : null,
            })];
        UsagesFound found = new(symbol, asked.Count, files, note);
        (int whole, int named) = AgentAnswers.Grouped(files, Size, f => Size(Counted(f)), Room(found with { Files = [] }));
        if (whole == files.Count)
        {
            return found;
        }
        if (files is [{ } only])
        {
            return OneFile(found, only, asked);
        }
        string[] said = [.. new[] { Said(files.Skip(whole).Take(named), "counted, not listed"), Said(files.Skip(whole + named), "left out") }.OfType<string>()];
        return found with
        {
            Files = [.. files.Take(whole), .. files.Skip(whole).Take(named).Select(Counted)],
            More = $"{Plural.Listed(said)}: within, a folder or file, lists their places",
        };

        static string? Said(IEnumerable<FilePlaces> those, string what) => those.Sum(f => f.Places!.Count) is int places && places == 0 ? null
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{places:N0} {Plural.Noun(places, "place")} in {those.Count():N0} {Plural.Noun(those.Count(), "file")} {what}");

        static FilePlaces Counted(FilePlaces file) => file with { Places = null, Count = file.Places!.Count };
    }

    /// <summary>A usage as its file's places list it: "line:column text".</summary>
    private static string Shown(Place place) => place.Text.Length == 0 ? $"{place.Line}:{place.Column}" : $"{place.Line}:{place.Column} {place.Text}";

    /// <summary>usages' answer of one file's places past the limit: its first places that fit, ending before a line whose
    /// places it could not list whole (unless that is its first), and the from that reads on from the next.</summary>
    private UsagesFound OneFile(UsagesFound found, FilePlaces file, IReadOnlyList<Place> places)
    {
        int kept = AgentAnswers.Fitting(file.Places!, Size, Room(found with { Files = [file with { Places = [] }] }));
        if (kept == places.Count)
        {
            return found;
        }
        int next = places[kept].Line;
        while (kept > 0 && places[kept - 1].Line == next && places[0].Line != next)
        {
            kept--;
        }
        return found with
        {
            Files = [file with { Places = [.. file.Places!.Take(kept)] }],
            More = AgentAnswers.More(places.Count - kept, "place", $"from {next} with within {file.Path} lists those from that line on"),
        };
    }

    /// <summary>The folder or file a tool's within names, in full, refused where there is none; null where none is named.</summary>
    private string? Within(string? within)
    {
        if (within is null)
        {
            return null;
        }
        string full = Resolve(within);
        return Directory.Exists(full) || File.Exists(full) ? full : throw new ArgumentException($"within names {within}, which is no folder or file here");
    }

    /// <summary>Whether a path an answer shows (<see cref="Relative(string)"/>) lies within a folder or file in full, read
    /// from its text alone.</summary>
    private bool Inside(string shown, string within) =>
        PathNames.Within(Path.IsPathRooted(shown) || Session.Folder is not { } folder ? shown : Path.Join(folder, shown), within);

    /// <summary>The places the language server names as <see cref="Places"/> reads them, with the paths among them that are
    /// generated code as Roslyn tells it (<see cref="GeneratedCode.SetApartAsync"/>).</summary>
    private async Task<(IReadOnlyList<Place> Places, IReadOnlySet<string> Generated)> SetApart(IReadOnlyList<LspLocation> locations, CancellationToken cancel)
    {
        if (Session.CSharp is not { } csharp)
        {
            return (await Places(locations, cancel), new HashSet<string>());
        }
        (IReadOnlyList<LspLocation> copied, IReadOnlySet<string> generated) = await csharp.Generated.SetApartAsync(locations, cancel);
        IReadOnlyList<Place> places = await Lines([.. copied.Select(l => (l.Uri, l.Range.Start, true))], cancel);
        return (places, new HashSet<string>(places.Where((_, i) => generated.Contains(copied[i].Uri)).Select(p => p.Path), StringComparer.Ordinal));
    }

    /// <summary>A Godot node renamed for an agent (<see cref="CSharpAssist.RenameNodesAsync"/>), its files saved; refused where no
    /// node's name is at the place.</summary>
    private async Task<string> GodotRename((Godot.GodotRead Read, Passenger.Workspace.GodotTreeNode Node)? at, string newName, string none, Patience patience, CancellationToken cancel,
        IReadOnlyList<Passenger.Workspace.GodotTreeNode>? nodes = null, string? old = null)
    {
        if (at is not ({ } read, { } node))
        {
            throw new InvalidOperationException(none);
        }
        string name = old ?? node.Name;
        if (name == newName)
        {
            return $"{newName} is its name already";
        }
        RefactorOutcome made = await window.Assist.Languages.CSharp.RenameNodesAsync(read, nodes ?? [node], name, newName, patience);
        if (made.Problem is { } refused)
        {
            throw new InvalidOperationException(refused);
        }
        await SaveLandedAsync(cancel);
        return $"{made.Said}: {AgentAnswers.Named([.. made.Files.Select(f => Relative(f.Path))], "file", FilesOfTheChange)}";
    }

    [McpServerTool(Name = McpTools.Hierarchy, Title = "Call or type hierarchy", ReadOnly = true), Description(
        "One level of the hierarchy of the symbol at a position, as the Hierarchy window: direction callers (each at its " +
        "call), callees, supertypes, subtypes (a member's: what it overrides or implements, or what overrides or implements " +
        "it, every level), or implementations (Go to Implementation's).")]
    public Task<CallToolResult> Hierarchy(string? path = null, int? line = null, int? column = null, string direction = "callers", string? symbol = null,
        string? within = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        string? inside = Within(within);
        IReadOnlyList<HierarchyEntry> found = await HierarchyOfAsync(path, line, column, direction, symbol, cancel);
        return Held(inside is null ? found : [.. found.Where(e => Inside(e.Path, inside))], direction[..^1], "within, a folder or file, lists those there");
    });

    [McpServerTool(Name = McpTools.Dependencies, Title = "Dependency map", ReadOnly = true), Description(
        "A level of the solution's dependency map, as the compiler binds the code: with no node, the C# projects and their " +
        "references; with a node's id, a project's namespaces, a namespace's types or a type's members. Each edge goes from a " +
        "node to the id of what it uses (a namespace's to a namespace, a type's or member's to a type or member), counted by " +
        "kind: calls, inherits, implements, fieldType, parameterType, attribute, uses, references.")]
    public Task<DependencyLevel> Dependencies(string? node = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        await SettledServer(cancel);
        CSharp.CSharpService csharp = Session.CSharp ?? throw new InvalidOperationException(Refusals.Sentence(Refusals.CSharpNotRunning));
        if (node is not null && Roslyn.Extension.DependencyIds.Read(node) is null)
        {
            throw new ArgumentException($"No node '{node}': a node's id is one a dependencies answer gave", nameof(node));
        }
        Roslyn.Extension.DependenciesFound found = await csharp.DependenciesAsync(node);
        DependencyNodeInfo[] nodes = [.. found.Nodes.Select(n => new DependencyNodeInfo(n.Id, n.Name, n.Kind, n.Path is { } path ? Relative(path) : null,
            n.Path is { } at && n.At is { } place ? ServerPlaces.PositionOf(Editors, at, new LspPosition(place.Line, place.Character)).Line + 1 : null, n.Expands ? true : null))];
        DependencyEdgeInfo[] edges = [.. found.Edges.Select(e => new DependencyEdgeInfo(e.From, e.To[^1], e.Kinds))];
        return HeldDependencies(new DependencyLevel(nodes, edges));
    });

    /// <summary>dependencies' answer held within <see cref="AgentAnswers.MostCharacters"/>: its nodes and edges whole where
    /// they fit, else each list given its share of the room (<see cref="AgentAnswers.Shares"/>), its first entries whole and
    /// the rest left out, its more saying how many.</summary>
    private static DependencyLevel HeldDependencies(DependencyLevel answered)
    {
        int room = Room(answered with { Nodes = [], Edges = [] });
        int[] needs = [answered.Nodes.Sum(n => Size(n) + 1), answered.Edges.Sum(e => Size(e) + 1)];
        if (needs.Sum() <= room)
        {
            return answered;
        }
        int[] shares = AgentAnswers.Shares(needs, room);
        int nodes = AgentAnswers.Fitting(answered.Nodes, Size, shares[0]);
        int edges = AgentAnswers.Fitting(answered.Edges, Size, shares[1]);
        int left = answered.Nodes.Count - nodes + answered.Edges.Count - edges;
        return new DependencyLevel([.. answered.Nodes.Take(nodes)], [.. answered.Edges.Take(edges)],
            AgentAnswers.More(left, nodes < answered.Nodes.Count ? "node or edge" : "edge", "a node's own level lists less", nodes < answered.Nodes.Count ? "nodes and edges" : null));
    }

    /// <summary>One level of the hierarchy hierarchy answers, every entry of it.</summary>
    private async Task<IReadOnlyList<HierarchyEntry>> HierarchyOfAsync(string? path, int? line, int? column, string direction, string? symbol, CancellationToken cancel)
    {
        CodePlace at = await AskedAt(path, line, column, symbol, cancel);
        (OpenDocument document, LspDocument lsp) = await CodeOrCopy(at.Path, cancel);
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
        string name = CSharp.AssistContext.NameAt(document.Text.Snapshot, offset);
        HierarchyItem item = (calls ? await lsp.PrepareCallHierarchyAsync(offset) : await lsp.PrepareTypeHierarchyAsync(offset))
            ?? throw new InvalidOperationException(await NoHierarchyAsync(lsp, offset, name, calls));
        if (!calls && !CSharp.RelatedSymbols.IsType(item, name))
        {
            // A member's own, as Base and Derived Symbols go: the server's type hierarchy at a member is its type's.
            IReadOnlyList<CSharp.RelatedSymbols.Target> related = direction == "supertypes"
                ? await CSharp.RelatedSymbols.BaseAsync(lsp, offset, name, document.Syntax?.Current, () => Session.CSharp?.Session)
                : await CSharp.RelatedSymbols.DerivedAsync(lsp, offset, name, () => Session.CSharp?.Session);
            IReadOnlyList<Place> members = await Places(await csharp.WrittenAsync([.. related.Select(r => r.Location)], cancel), cancel);
            return [.. members.Select(p => new HierarchyEntry(null, null, p.Path, p.Line, p.Column, p.Text))];
        }
        IReadOnlyList<(HierarchyItem Item, LspRange At)> found = direction switch
        {
            "callers" => [.. (await csharp.IncomingCallsAsync(item)).Select(c => (c.From, c.FromRanges.FirstOrDefault() ?? c.From.SelectionRange))],
            "callees" => [.. (await csharp.OutgoingCallsAsync(item)).Select(c => (c.To, c.To.SelectionRange))],
            "supertypes" => [.. (await server.SupertypesAsync(item)).Select(t => (t, t.SelectionRange))],
            _ => [.. (await server.SubtypesAsync(item)).Select(t => (t, t.SelectionRange))],
        };
        IReadOnlyList<Place> places = await Places(await csharp.WrittenAsync([.. found.Select(f => new LspLocation(f.Item.Uri, f.At))], cancel), cancel);
        return [.. found.Select((f, i) => new HierarchyEntry(f.Item.Name, f.Item.Detail, places[i].Path, places[i].Line, places[i].Column, places[i].Text))];
    }

    /// <summary>Why the symbol at an offset, of the name there, has no hierarchy of the kind asked, naming the directions
    /// that follow it where it has the other kind: a type no callers, a member in no type no supertypes. The server's type
    /// hierarchy at a member is the type the member is in, so only a type of the name there is one.</summary>
    private static async Task<string> NoHierarchyAsync(LspDocument lsp, int offset, string name, bool calls)
    {
        if (calls && await lsp.PrepareTypeHierarchyAsync(offset) is { } type && CSharp.RelatedSymbols.IsType(type, name))
        {
            return $"{type.Name} is a type, which has no callers or callees: direction supertypes, subtypes or implementations follows it";
        }
        if (!calls && await lsp.PrepareCallHierarchyAsync(offset) is { } member)
        {
            return $"{member.Name} is in no type, so it has no supertypes or subtypes: direction callers, callees or implementations follows it";
        }
        return calls ? "No method, property or event there" : "No type there";
    }

    [McpServerTool(Name = McpTools.Rename, Title = "Rename a symbol", Destructive = true), Description(
        "Renames the symbol at a position everywhere it is used, as Rename does, and saves the files: a C# symbol's XAML too " +
        "(bindings, x:DataType, x:Static, handlers, tags; from any of them as from its declaration) and the MVVM Toolkit's " +
        "property or command made of it, a type's files named " +
        "after it (Foo.cs, Foo.Designer.cs, Foo.axaml) unless renameFiles is false, and always its XAML's x:Class and " +
        "Razor component (Foo.razor, Foo.razor.css); an Aspire resource's name where its AppHost " +
        "declares it and its services read it; a Godot node's name (in a scene, or a GetNode path) in its scenes and the code's paths; " +
        "a Godot input action, group, animation, autoload, shader parameter or translation key a string names, where defined and in each such string; " +
        "a XAML element's name with its code-behind's uses; a Reqnroll step (from a step, or its definition's expression: newName " +
        "the new expression, its parameters as they were), in its attribute and every step; a JSON property (in " +
        "package.json's scripts, every npm run of one); a compose file's names, its override's too. MSBuild files have none. " +
        "force renames a GDScript member where uses Passenger can't see remain, leaving them.")]
    public Task<string> Rename(string newName, string? path = null, int? line = null, int? column = null, string? symbol = null,
        bool renameFiles = true, bool force = false,
        CancellationToken cancel = default) => OnUi(cancel, () => Patience.RunAsync(Editors.Commands, () => Session.CSharp?.Session, async patience =>
    {
        CodePlace at = await AskedAt(path, line, column, symbol, cancel);
        using AgentsChange naming = window.NameAgentsChange($"Rename to {newName}");
        // A GDScript's member or local is renamed by Passenger's reading of the project's scripts, before Godot's server serves it.
        if (Godot.GodotScriptTypes.IsScript(at.Path))
        {
            OpenDocument script = await DocumentAsync(at.Path, cancel);
            naming.Names(Renaming(script, Offset(script, at.Line, at.Column), newName));
            if (await window.Assist.GDScriptRenameAsync(script, Offset(script, at.Line, at.Column), newName, force, patience) is { } scriptRenamed)
            {
                if (scriptRenamed.Problem is { } refused)
                {
                    throw new InvalidOperationException(scriptRenamed.Anyway is null ? refused : $"{refused}; force: true renames it anyway, leaving them");
                }
                await SaveLandedAsync(cancel);
                return $"{scriptRenamed.Said}: {AgentAnswers.Named([.. scriptRenamed.Files.Select(f => Relative(f.Path))], "file", FilesOfTheChange)}";
            }
        }
        Served served = await Serve(at.Path, cancel);
        if (served.Document.ReadOnlyWhy is { } readOnly)
        {
            throw new InvalidOperationException($"Rename was not applied: {readOnly}");
        }
        int offset = Offset(served.Document, at.Line, at.Column);
        naming.Names(Renaming(served.Document, offset, newName));
        // A Reqnroll step, from a step or its definition's expression (its method's name is C#'s to rename).
        if ((served is FeatureServed || served.Document.Language == Languages.Language.CSharp)
            && await window.Assist.Languages.Gherkin.StepAtAsync(served.Document, offset) is { } stepped)
        {
            RefactorOutcome step = await window.Assist.Languages.Gherkin.RenameStepAsync(stepped, newName);
            if (step.Problem is { } refused)
            {
                throw new InvalidOperationException(refused);
            }
            await SaveLandedAsync(cancel);
            return $"{step.Said}: {AgentAnswers.Named([.. step.Files.Select(f => Relative(f.Path))], "file", FilesOfTheChange)}";
        }
        if (served is ServerServed { Web.Kind: WebServerKind.Yaml } yaml && global::Passenger.App.Containers.ComposeFiles.IsCompose(yaml.Document.Path)
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
            return $"{compose.Said}: {AgentAnswers.Named([.. compose.Files.Select(f => Relative(f.Path))], "file", FilesOfTheChange)}";
        }
        // A Godot name a string gives (an input action, a group, an autoload…) is renamed where the project defines it, as from the window.
        if (served is ServerServed { Web: null } named && named.Document.Language == Languages.Language.CSharp
            && await window.Assist.Languages.CSharp.GodotKindAtAsync(named.Document, offset) is { } kind)
        {
            if (kind.Name == newName)
            {
                return $"{newName} is its name already";
            }
            RefactorOutcome kindRenamed = await window.Assist.Languages.CSharp.RenameKindAsync(kind, newName, patience);
            if (kindRenamed.Problem is { } refused)
            {
                throw new InvalidOperationException(refused);
            }
            await SaveLandedAsync(cancel);
            return $"{kindRenamed.Said}: {AgentAnswers.Named([.. kindRenamed.Files.Select(f => Relative(f.Path))], "file", FilesOfTheChange)}";
        }
        // A Godot node a GetNode path names is renamed in its scenes, as from its scene.
        if (served is ServerServed { Web: null } code && code.Document.Language == Languages.Language.CSharp
            && await window.Assist.Languages.CSharp.GodotNodeAtAsync(code.Document, offset) is ({ } read, { } name, { Count: > 0 } nodes))
        {
            return await GodotRename((read, nodes[0]), newName, string.Empty, patience, cancel, nodes, name);
        }
        switch (served)
        {
            case GodotServed scene:
                return await GodotRename(await window.Assist.Languages.Godot.NodeAtAsync(scene.Document, offset), newName, $"No node's name is at {Relative(scene.Document.Path)}:{at.Line}:{at.Column}", patience, cancel);
            case XamlServed xaml:
                return await XamlRename(xaml, offset, newName, renameFiles, patience, cancel);
            case ServerServed json when !json.Provides("renameProvider"):
                return await JsonRename(json, offset, newName);
            case MSBuildServed:
                throw Lacks(served, "Rename", MSBuildHas);
            case ShaderServed:
                throw Lacks(served, "Rename", ShaderHas);
            case FeatureServed feature:
                throw new InvalidOperationException($"No step one step definition binds is at {Relative(feature.Document.Path)}:{at.Line}:{at.Column}");
            case ScriptServed script:
                script.Web.AskServer?.Invoke();
                throw new InvalidOperationException($"No member or local of a script is at {Relative(script.Document.Path)}:{at.Line}:{at.Column} as Passenger reads the "
                    + $"project's scripts, and no Godot server serves it yet to be asked: one is starting for it");
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
            return $"{made.Said}: {AgentAnswers.Named([.. made.Files.Select(f => Relative(f.Path)).Distinct()], "file", FilesOfTheChange)}";
        }
        RefactorOutcome renaming = await window.Assist.Actions.RenameSymbolAsync(served.Document, offset, newName, renameFiles, patience);
        // A C# string naming a GDScript member renames it as from its script, uses Passenger can't see held back the same.
        if (renaming.Anyway is { } anyway)
        {
            renaming = force ? await anyway.Make(patience) : renaming with { Problem = $"{renaming.Problem}; force: true renames it anyway, leaving them" };
        }
        return await SymbolRenamed(renaming, cancel);
    }, AgentCall.Say, cancel));

    /// <summary>What Local History names an agent's rename of the name at an offset of a file.</summary>
    private string Renaming(OpenDocument document, int offset, string newName) =>
        NameAt(document, offset) is { } old ? $"Rename {old} to {newName}" : $"Rename to {newName}";

    /// <summary>A symbol's rename's answer, its files saved: the files it changed and moved, those left as edited meanwhile,
    /// and a file that could not follow its type.</summary>
    private async Task<string> SymbolRenamed(RefactorOutcome renaming, CancellationToken cancel)
    {
        if (renaming.Problem is { } notRenamed)
        {
            throw new InvalidOperationException(notRenamed);
        }
        if (renaming.Files.Count == 0 && renaming.Left.Count == 0)
        {
            return renaming.Said;
        }
        await SaveLandedAsync(cancel);
        List<string> changed = [.. renaming.Files.Where(f => f.Change == RefactoredChange.Changed).Select(f => Relative(f.Path))];
        List<string> moved = [.. renaming.Files.Where(f => f.Change == RefactoredChange.Moved).Select(f => $"{Relative(f.Path)} to {Relative(f.To!)}")];
        string renamed = moved.Count > 0 ? $"; renamed {string.Join(", ", moved)}" : string.Empty;
        return $"{Refusals.EditedMeanwhile($"Renamed in {Plural.Of(changed.Count, "file")}: {string.Join(", ", changed)}{renamed}", renaming.Left)}{(renaming.Unmoved is { } unmoved ? $"; but {unmoved}" : string.Empty)}";
    }

    [McpServerTool(Name = McpTools.CodeActions, Title = "Fixes and refactorings", ReadOnly = true), Description(
        "The fixes and refactorings offered at a position (Show Context Actions), or over a selection to endLine:endColumn (Extract " +
        "Method, Introduce Variable), as 'title [kind]' with the choices each offers (nested actions, a Fix All's scopes, where " +
        "a XAML resource goes), which apply_code_action takes: Passenger's own fixes first (a GDScript's are all Passenger's), then " +
        "the folder's linters' (ESLint's, Oxlint's, Biome's), then the language server's; in XAML, the line's problems' fixes (a binding's missing property or command, a missing converter), Extract " +
        "Resource and Extract Style. A Suppress or Configure action's choices are counted, and listed when title names it.")]
    public Task<IReadOnlyList<string>> CodeActions(string path, int line, int column, int? endLine = null, int? endColumn = null, string? title = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        IReadOnlyList<(string Title, string Shown)> listed;
        // A GDScript's fixes are Passenger's own, listed as Show Context Actions lists them, Godot's server running or not.
        if (Godot.GodotScriptTypes.IsScript(Resolve(path)))
        {
            OpenDocument script = await DocumentAsync(path, cancel);
            listed = [.. (await window.Assist.OwnFixesAsync(script, Offset(script, line, column))).Select(f => (f.Title, $"{f.Title} [quickfix]"))];
            return title is null ? (IReadOnlyList<string>)[.. listed.Select(a => a.Shown)]
                : listed.Where(a => a.Title == title).Select(a => a.Shown).ToList() is { Count: > 0 } fixes ? fixes
                : throw new InvalidOperationException(listed.Count == 0 ? $"No fixes here in {Relative(script.Path)}" : $"No action '{title}' here; there are: {string.Join("; ", listed.Select(a => a.Title))}");
        }
        Served served = await Serve(path, cancel);
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
            case GodotServed when await SceneFixes(served, line, column, cancel) is { Count: > 0 } own:
                listed = [.. own.Select(f => (f.Title, $"{f.Title} [quickfix]"))];
                break;
            case GodotServed:
                throw new InvalidOperationException(SceneFixesHere);
            case ShaderServed:
                throw Lacks(served, "fixes or refactorings", ShaderHas);
            case FeatureServed feature:
                listed = [.. (await window.Assist.OwnFixesAsync(feature.Document, Offset(feature.Document, line, column))).Select(f => (f.Title, $"{f.Title} [quickfix]"))];
                break;
            default:
                int offset = Offset(served.Document, line, column);
                await AspireMarked(served, cancel);
                IReadOnlyList<LintFix> linted = await LintFixes(served, offset, cancel);
                listed = [.. (await window.Assist.OwnFixesAsync(served.Document, offset)).Select(f => (f.Title, $"{f.Title} [quickfix]")),
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
        "Applies the action titled as code_actions lists it there, as Show Context Actions does, choice naming one of its choices, and " +
        "saves the files. name is XAML's Extract Resource or Extract Style's new key or style (else their dialog's), which " +
        "replace every use of the value, or an Aspire parameter's Set… value.")]
    public Task<string> ApplyCodeAction(string path, int line, int column, string title, int? endLine = null, int? endColumn = null,
        string? choice = null,
        string? name = null,
        CancellationToken cancel = default) => OnUi(cancel, () => Patience.RunAsync(Editors.Commands, () => Session.CSharp?.Session, async patience =>
    {
        using AgentsChange naming = window.NameAgentsChange(choice is null ? title : $"{title} ({choice})");
        string what = choice is null ? $"'{title}'" : $"'{title}' ({choice})";
        // A C# file is opened only once its project has it; any other is refused before its language is asked.
        if (Languages.Language.ForPath(path).Service != Languages.LanguageService.CSharp)
        {
            RefuseReadOnly(await DocumentAsync(path, cancel), what);
        }
        if (Godot.GodotScriptTypes.IsScript(Resolve(path)))
        {
            OpenDocument script = await DocumentAsync(path, cancel);
            IReadOnlyList<EditorFix> fixes = await window.Assist.OwnFixesAsync(script, Offset(script, line, column));
            return await ApplyOwnFix(script, fixes.FirstOrDefault(f => f.Title == title)
                ?? throw new InvalidOperationException(fixes.Count == 0 ? $"No fixes here in {Relative(script.Path)}" : $"No action '{title}' here; there are: {string.Join("; ", fixes.Select(f => f.Title))}"),
                patience, name);
        }
        Served served = await Serve(path, cancel);
        RefuseReadOnly(served.Document, what);
        switch (served)
        {
            case XamlServed xaml:
                return await ApplyXamlAction(xaml, Offset(xaml.Document, line, column), title, choice, name);
            case MSBuildServed when window.Assist.OwnFixes(served.Document, Offset(served.Document, line, column)).FirstOrDefault(f => f.Title == title) is { } ownFix:
                return await ApplyOwnFix(served.Document, ownFix, patience, name);
            case MSBuildServed:
                throw Lacks(served, "fixes or refactorings", MSBuildHas);
            case GodotServed when (await SceneFixes(served, line, column, cancel)).FirstOrDefault(f => f.Title == title) is { } sceneFix:
                return await ApplyOwnFix(served.Document, sceneFix, patience, name);
            case GodotServed:
                throw new InvalidOperationException(SceneFixesHere);
            case ShaderServed:
                throw Lacks(served, "fixes or refactorings", ShaderHas);
            case FeatureServed feature:
                IReadOnlyList<EditorFix> stepFixes = await window.Assist.OwnFixesAsync(feature.Document, Offset(feature.Document, line, column));
                return await ApplyOwnFix(feature.Document, stepFixes.FirstOrDefault(f => f.Title == title)
                    ?? throw new InvalidOperationException(stepFixes.Count == 0 ? $"No fixes here in {Relative(feature.Document.Path)}" : $"No action '{title}' here; there are: {string.Join("; ", stepFixes.Select(f => f.Title))}"),
                    patience, name);
        }
        ServerServed server = (ServerServed)served;
        int offset = Offset(served.Document, line, column);
        await AspireMarked(served, cancel);
        if ((await window.Assist.OwnFixesAsync(served.Document, offset)).FirstOrDefault(f => f.Title == title) is { } own)
        {
            return await ApplyOwnFix(served.Document, own, patience, name);
        }
        IReadOnlyList<LintFix> linted = await LintFixes(served, offset, cancel);
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
        // Made as the window makes it, under the action's title in Undo, a type it renames taking its files along.
        RefactorOutcome made = await window.Assist.Actions.ApplyEditAsync(edit, asked, title, what);
        if (made.Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
        await SaveLandedAsync(cancel);
        return ActionApplied(what, made, unrun);
    }, AgentCall.Say, cancel));

    /// <summary>Refuses a code action on a file the editor shows read-only, before its fixes or any server's actions are
    /// asked for.</summary>
    private static void RefuseReadOnly(OpenDocument document, string what)
    {
        if (document.ReadOnlyWhy is { } readOnly)
        {
            throw new InvalidOperationException($"{what} was not applied: {readOnly}");
        }
    }

    /// <summary>What an agent hears of a code action's edit made: the files it changed, made or deleted, counted, those it
    /// renamed, a command it carries that Passenger cannot run, those left as edited meanwhile, and a file that could not
    /// follow its type.</summary>
    private string ActionApplied(string what, RefactorOutcome made, string unrun)
    {
        int changed = made.Files.Count(f => f.Change != RefactoredChange.Moved) - made.Left.Count;
        List<string> moved = [.. made.Files.Where(f => f.Change == RefactoredChange.Moved).Select(f => $"{Relative(f.Path)} to {Relative(f.To!)}")];
        string to = changed > 0 || moved.Count == 0 ? $" to {Plural.Of(changed, "file")}" : string.Empty;
        string renaming = moved.Count > 0 ? $", renaming {string.Join(", ", moved)}" : string.Empty;
        return $"{Refusals.EditedMeanwhile($"Applied {what}{to}{renaming}{unrun}", made.Left)}{(made.Unmoved is { } unmoved ? $"; but {unmoved}" : string.Empty)}";
    }

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
        string changed = AgentAnswers.Named([.. made.Files.Where(f => f.Change != RefactoredChange.Created).Select(f => Relative(f.Path))], "file", FilesOfTheChange);
        string created = AgentAnswers.Named([.. made.Files.Where(f => f.Change == RefactoredChange.Created).Select(f => Relative(f.Path))], "file", FilesOfTheChange);
        string where = (changed.Length > 0 ? $" to {changed}" : string.Empty) + (created.Length > 0 ? $"{(changed.Length > 0 ? "," : string.Empty)} making {created}" : string.Empty);
        return Refusals.EditedMeanwhile($"Applied {what}{where}{named}", made.Left);
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
        "Reformats a file as Reformat Code does, and saves it: XAML by its .editorconfig, GDScript by Godot's style guide, a " +
        ".feature file by its gherkin_* editorconfig keys, a web " +
        "language by the formatter its folder names (Oxfmt, Biome) or its Prettier where it formats it, else the language server. MSBuild files have none.")]
    public Task<string> Format(string path, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (Resolve(path) is { } full && !Reaches(full, changes: true))
        {
            throw new InvalidOperationException(CSharp.CodeActions.Reformatted.Refused("Reformat Code", ReadOnlyHere(full, Path.GetFileName(full))).Said);
        }
        OpenDocument document;
        LspDocument? lsp = null;
        if (Passenger.App.Xaml.XamlService.IsXaml(Resolve(path)) || Godot.GodotScriptTypes.IsScript(Resolve(path)) || Bdd.FeatureChecks.IsFeature(Resolve(path)))
        {
            document = await DocumentAsync(path, cancel);
        }
        else
        {
            Served file = await Serve(path, cancel);
            ServerServed served = file as ServerServed ?? throw Lacks(file, "Reformat Code", file switch { GodotServed => GodotHas, ShaderServed => ShaderHas, _ => MSBuildHas });
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
        "types before members, then what only its source generators declare, in a read-only copy of their code; an indexer " +
        "by this[] or Item. Other files' names are in their outline.")]
    public Task<CallToolResult> FindSymbol(
        string query,
        [Description("class, interface, struct, enum, method, property, field, event, constructor...")] string? kind = null,
        int limit = 50,
        [Description("Only the symbols query names exactly, as symbol does, each with its documentation id")] bool exact = false,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        // Go to Symbol finds an indexer by neither of its names, this[] or its metadata's Item: the exact find does.
        bool indexer = query.StartsWith("this[", StringComparison.Ordinal);
        if (limit < 1)
        {
            throw new ArgumentException($"limit is {limit}: find_symbol lists 1 or more", nameof(limit));
        }
        if (!exact && !indexer && kind is not null && !SymbolKinds.All.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"No kind '{kind}'; Go to Symbol's kinds are {string.Join(", ", SymbolKinds.All)}", nameof(kind));
        }
        // Found once the C# files come under the folder's projects are in them (one just made may declare it), saying any left out.
        await SettledServer(cancel);
        IReadOnlyList<(string Path, string Project)> untaken = await UntakenAsync(cancel) ?? [];
        RoslynSession session = await Server(cancel);
        string? without = WentWithout(untaken);
        if (exact || indexer)
        {
            SymbolsNamed named = await NamedAsync(session, query, kind, limit, cancel, waited: untaken);
            return Held([.. named.Symbols.Select(Found)], "symbol", NarrowedSymbols, named.More, LargerLimit, note: without);
        }
        IReadOnlyList<SymbolFound> indexers = query == "Item" && kind is null or "property"
            ? [.. (await NamedAsync(session, "this[]", "property", limit, cancel, noneRefused: false)).Symbols.Select(Found)]
            : [];
        IReadOnlyList<SymbolInformation> found = await session.SymbolsAsync(query);
        // Each target framework finds a symbol again; the server's order is not by match, so the name itself first, a generator's too, which no limit cuts.
        List<SymbolInformation> matching = [.. WorkspaceSymbols.Kept(found)
            .Where(s => kind is null || SymbolKinds.Name(s.Kind).Equals(kind, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(s => (s.Name, s.Kind, s.Location.Uri, s.Location.Range.Start.Line, s.Location.Range.Start.Character))
            .OrderBy(s => NameMatch(s.Name, query) > 0)
            .ThenBy(WorkspaceSymbols.Generated)
            .ThenBy(s => NameMatch(s.Name, query))
            .ThenBy(s => SymbolKinds.Name(s.Kind) is "class" or "interface" or "struct" or "enum" ? 0 : 1)];
        List<SymbolInformation> kept = [.. matching.Take(Math.Max(0, limit - indexers.Count))];
        if (indexers.Count == 0 && kept.Count == 0 && FailedLoads() is { Count: > 0 } failed)
        {
            throw new InvalidOperationException(Refusals.NoneWhileUnloaded($"No {kind ?? "symbol"} of the solution's own code matches {query}", failed));
        }
        // A Razor file's C# at its place in the Razor file, another generator's in a copy of its code.
        IReadOnlyList<Place> places = await Places(await session.WrittenAsync([.. kept.Select(s => s.Location)], cancel), cancel);
        // Each one's container as the exact find names it, a type's namespace too.
        IReadOnlyList<string?> containers = await session.ContainersAsync([.. kept.Select(s => s.Location)], cancel) ?? [.. kept.Select(s => s.Owner)];
        return Held([.. indexers, .. kept.Select((s, i) => new SymbolFound(s.Name, SymbolKinds.Name(s.Kind), containers[i], places[i].Path, places[i].Line, places[i].Column))],
            "symbol", NarrowedSymbols, matching.Count - kept.Count, LargerLimit, note: without);

        static SymbolFound Found(NamedPlace n) => new(n.Name, n.Kind, n.Container, n.Path, n.Line, n.Column) { Id = n.Id };
    });

    /// <summary>How the files of a change that an answer named none of are read.</summary>
    private const string FilesOfTheChange = "local_history of a folder within names those there";

    /// <summary>How find_symbol's symbols left out are read.</summary>
    private const string NarrowedSymbols = "kind or a longer query narrows them";

    /// <summary>How the entries an agent's limit left out are read.</summary>
    private const string LargerLimit = "a larger limit lists them";

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
        SymbolsNamed named = await NamedAsync(await SettledServer(cancel), symbol, null, 10, cancel);
        return named.Symbols.Count == 1 ? new CodePlace(named.Symbols[0].Path, named.Symbols[0].Line, named.Symbols[0].Column)
            : throw new InvalidOperationException($"{symbol} names {Plural.Of(named.Symbols.Count + named.More, "symbol")} of the solution's own code: "
                + $"{string.Join("; ", named.Symbols.Select(n => n.ToString()))}{(named.More > 0 ? $"; and {Numbers.Said(named.More)} more" : string.Empty)}. Name one by its container, "
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
    /// looked for again once the C# files come under the folder's projects are in them, a file just written may declare it,
    /// unless the caller waited for them (<paramref name="waited"/>, those still in none); none found is said to be without
    /// those still in none after the wait (<see cref="UntakenAsync"/>).</summary>
    private async Task<SymbolsNamed> NamedAsync(RoslynSession session, string name, string? kind, int limit, CancellationToken cancel, bool noneRefused = true,
        IReadOnlyList<(string Path, string Project)>? waited = null)
    {
        Roslyn.Extension.SymbolsFound found = await session.SymbolsNamedAsync(name, kind is null ? limit : 1000, cancel);
        List<Roslyn.Extension.NamedSymbol> kept = [.. found.Symbols.Where(s => kind is null || s.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))];
        IReadOnlyList<(string Path, string Project)> untaken = waited ?? [];
        if (kept.Count == 0 && noneRefused && waited is null && await UntakenAsync(cancel) is { } left && Session.CSharp?.Session is { } now)
        {
            untaken = left;
            found = await now.SymbolsNamedAsync(name, kind is null ? limit : 1000, cancel);
            kept = [.. found.Symbols.Where(s => kind is null || s.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))];
        }
        if (kept.Count == 0 && noneRefused)
        {
            string none = $"No {kind ?? "symbol"} of the solution's own code is named {name}";
            throw new InvalidOperationException(FailedLoads() is { Count: > 0 } failed ? Refusals.NoneWhileUnloaded(none, failed)
                : $"{none}: find_symbol finds names like it{(WentWithout(untaken) is { } without ? $". {without}" : string.Empty)}");
        }
        // A symbol only a source generator declares is at its place in a copy of the generator's code.
        IReadOnlyList<Place> places = await Places([.. kept.Select(s => new LspLocation(s.Path is { } path ? DocumentUris.FromPath(path) : s.Generated!,
            new LspRange(new LspPosition(s.At!.Line, s.At.Character), new LspPosition(s.At.Line, s.At.Character))))], cancel);
        List<NamedPlace> named = [.. kept.Select((s, i) => new NamedPlace(s.Name, s.Shown, s.Kind, s.Id, s.Container, places[i].Path, places[i].Line, places[i].Column))];
        return new SymbolsNamed([.. named.Take(limit)], Math.Max(0, named.Count - limit) + (kind is null ? found.More : 0));
    }

    /// <summary>symbol_info of a name: each symbol it names, with its kind, place and documentation id, then what its hover
    /// card says.</summary>
    private async Task<string> SymbolsInfo(string symbol, CancellationToken cancel)
    {
        SymbolsNamed named = await NamedAsync(await SettledServer(cancel), symbol, null, 10, cancel);
        List<string> each = [];
        foreach (NamedPlace place in named.Symbols)
        {
            // A symbol only a source generator declares is read in a copy of its code.
            (OpenDocument document, _) = await CodeOrCopy(place.Path, cancel);
            each.Add($"{place}\n{await CardAsync(document, Offset(document, place.Line, place.Column), cancel)}".TrimEnd());
        }
        return string.Join("\n\n", each) + (named.More > 0 ? $"\n\nAnd {Numbers.Said(named.More)} more: name one by its container, its parameters' types or its documentation id" : string.Empty);
    }

    /// <summary>What the hover card says of an offset of a file, as the languages' one order says it (<see
    /// cref="LanguageAssists.HoverAsync"/>), read as an agent reads it; empty where it says nothing.</summary>
    private async Task<string> CardAsync(OpenDocument document, int offset, CancellationToken cancel)
    {
        HoverWords card = new();
        await window.Assist.Languages.HoverAsync(card, document, offset, cancel);
        return card.Text;
    }

    /// <summary>Usages in the order an agent reads them: by file, then line and column, generated code's files last: a
    /// source generator's (their copies, <see cref="GeneratedCode"/>) and those of <paramref name="generated"/>.</summary>
    private static IReadOnlyList<Place> Sorted(IReadOnlyList<Place> places, IReadOnlySet<string>? generated) =>
        [.. places.OrderBy(p => (p.Text.Length == 0 && p.Path.EndsWith(" (generated)", StringComparison.Ordinal)) || GeneratedCode.IsCopy(p.Path) || generated?.Contains(p.Path) == true)
            .ThenBy(p => p.Path, StringComparer.Ordinal).ThenBy(p => p.Line).ThenBy(p => p.Column)];

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
            : $"No symbol at {place}, and line {line} declares {Plural.Listed([.. declared.Select(d => d.Name)])}: give the column of the one meant");

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
        "values, items, targets and imports, a Godot scene's nodes with their scripts, instances and signals' connections, a " +
        "Markdown file's headings.")]
    public Task<string> Outline(string path, int? from = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        List<string> parts = [.. (await OutlineTextAsync(path, cancel)).Split('\n')];
        if (from is int first)
        {
            int at = parts.FindIndex(p => OutlineLine(p) >= first);
            parts = at < 0 ? [$"Nothing is declared from line {first} on"] : parts[at..];
        }
        return HeldLines(parts, "part", next => OutlineLine(parts[next]) is int line ? $"from {line} lists those from that line on" : "from, a later line, lists those from it on");
    });

    /// <summary>The line of the file an outline's part says it is at, null where it says none of the file's own.</summary>
    private static int? OutlineLine(string part) =>
        System.Text.RegularExpressions.Regex.Match(part, @"line (\d+)\)$") is { Success: true } found && int.TryParse(found.Groups[1].ValueSpan, out int line) ? line : null;

    /// <summary>A file's structure as outline answers it, whole.</summary>
    private async Task<string> OutlineTextAsync(string path, CancellationToken cancel)
    {
        if (Languages.Language.ForPath(Resolve(path)) == Languages.Language.Markdown)
        {
            OpenDocument markdown = await DocumentAsync(path, cancel);
            Outline<Navigation.SymbolRow> headings = await new Editors.MarkdownOutline(markdown.Text).ReadAsync(markdown.Path, null, cancel);
            return headings.Rows.Count == 0 ? "No heading is in this file"
                : string.Join('\n', headings.Rows.Select(row => $"{new string(' ', row.Depth * 2)}{row.Name} (heading, line {row.Position.Line + 1})"));
        }
        // A GDScript's members are Passenger's own reading, whether or not Godot's server serves it.
        if (Godot.GodotScriptTypes.IsScript(Resolve(path)))
        {
            OpenDocument script = await DocumentAsync(path, cancel);
            Outline<Navigation.SymbolRow> members = await new Godot.GDScriptOutline(script.Text).ReadAsync(script.Path, null, cancel);
            return members.Rows.Count == 0 ? "Nothing is declared in this file"
                : string.Join('\n', members.Rows.Select(row => $"{new string(' ', row.Depth * 2)}{row.Name}{(row.Detail is { } detail ? " " + detail : string.Empty)} ({SymbolKinds.Name(row.Kind)}, line {row.Position.Line + 1})"));
        }
        // A generator's code copied has no server document: its own syntax says what it declares.
        if (GeneratedCode.IsCopy(Resolve(path)) && (await DocumentAsync(path, cancel)).Syntax?.Current is { } parsed)
        {
            string declared = await BackgroundWork.Run(() => Declarations.Outline(parsed), cancel);
            return declared.Length == 0 ? "Nothing is declared in this file" : declared;
        }
        Served served = await Serve(path, cancel);
        switch (served)
        {
            case XamlServed xaml:
                return XamlOutline(xaml);
            case MSBuildServed msbuild:
                return MSBuildOutline(msbuild);
            case GodotServed scene:
                return await SceneOutline(scene, cancel);
            case ShaderServed shader:
                return CSharp.GodotShaderAssist.Outline(shader.Document.Text.Snapshot.ToString()) is { Count: > 0 } declared
                    ? string.Join('\n', declared.Select(d => $"{d.Written} ({d.Kind}, line {d.Line})")) : "Nothing is declared in this file";
            case FeatureServed feature:
                Outline<Navigation.SymbolRow> parts = await new Editors.FeatureOutline(feature.Document.Text, () => Session.FeatureProjectFolder(feature.Document.Path))
                    .ReadAsync(feature.Document.Path, null, cancel);
                return parts.Rows.Count == 0 ? "The file has no feature"
                    : string.Join('\n', parts.Rows.Select(row => $"{new string(' ', row.Depth * 2)}{row.Detail ?? row.Name}: {row.Name} (line {row.Position.Line + 1})"));
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
    }

    /// <summary>A scene's outline as Structure shows it: each node by its name and class at its line, its script's and
    /// instance's files, and its signals' connections with the method each calls.</summary>
    private async Task<string> SceneOutline(GodotServed scene, CancellationToken cancel)
    {
        Navigation.FileOutline source = Session.OutlineOf(scene.Document) ?? throw Lacks(scene, "outline", GodotHas);
        Outline<Navigation.SymbolRow> outline = await source.ReadAsync(scene.Document.Path, null, cancel);
        System.Text.StringBuilder text = new();
        foreach (Navigation.SymbolRow row in outline.Rows)
        {
            text.Append(' ', row.Depth * 2).Append(row.Name);
            if (row.Detail is { } detail)
            {
                text.Append(' ').Append(detail);
            }
            bool here = PathNames.Same(row.Path, scene.Document.Path);
            text.Append(row.Kind switch
            {
                Navigation.SymbolRow.FileKind => here ? $" (no such file, named at line {row.Position.Line + 1})" : string.Empty,
                Navigation.SymbolRow.NodeKind => $" (node{(row.Where is { } unique ? $" {unique}" : string.Empty)}, line {row.Position.Line + 1}{(here ? string.Empty : $" of {Relative(row.Path)}")})",
                _ => $" (connection, line {row.Position.Line + 1})",
            }).Append('\n');
        }
        return text.Length == 0 ? "Nothing is declared in this file" : text.ToString().TrimEnd();
    }

    // Build and run

    // Publish is build's, as the Build window's: a tool of its own cost every session's list more than the budget has.
    [McpServerTool(Name = McpTools.Build, Title = "Build", Destructive = true), Description(
        "Builds the solution (or the folder's project); with publish (a project), publishes it with profile, else its last " +
        "(DefaultContainer: its container image, into the engine; an AppHost through its publishers, to its last folder or aspire-output); " +
        "with preset, exports the Godot project's game through that export_presets.cfg preset (with debug, a debug export; to exportPath, from " +
        "the project's folder, in place of its export_path), Godot's output in run's. " +
        "Returns whether it succeeded, how it ended, its errors and warnings, the folder or image published to, and MSBuild's last " +
        "lines when it failed (output of build has them all).")]
    public Task<BuildResult> Build(string? publish = null, string? profile = null, string? preset = null, bool debug = false, string? exportPath = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if ((debug || exportPath is not null) && preset is null)
        {
            throw new ArgumentException($"{(debug ? "debug" : "exportPath")} is a Godot export's: give preset, the export_presets.cfg preset it exports through");
        }
        if (publish is null && profile is not null)
        {
            throw new ArgumentException($"{profile} is a publish profile: give publish the project it is one of");
        }
        if (preset is not null && publish is not null)
        {
            throw new ArgumentException($"{preset} is a Godot export preset: omit publish, a .NET project's");
        }
        string? game = preset is null ? null : window.GodotExportFolder() ?? throw new InvalidOperationException(Refusals.NoGodotProject);
        // An export the agent places is written where it may change files.
        string? exported = game is null || exportPath is null ? null : Resolve(global::Passenger.Workspace.GodotResPaths.Globalized(game, exportPath), changes: true);
        ProjectModel? published = publish is null ? null : Project(publish);
        AgentCall.Follow(window.Actions.Work, published is null && game is null ? AppActions.Build : AppActions.Publish);
        // The build, publish or export goes on without an agent that gives up on it, shown in the Build or Run window.
        BuildOutcome built = await (game is not null ? Builds.ExportGodotAsync(new GodotExportRequest(game, preset!, debug, exported))
            : published is null ? Builds.BuildAsync(BuildCause.Explicit, CancellationToken.None)
            : Builds.PublishAsync(new PublishRequest(published.Path, profile ?? Session.Publishing.ProfileOf(published.Path)))).WaitAsync(cancel);
        // So the code tools the agent calls next know what it built.
        await (Session.CSharp?.ReferencesHeld ?? Task.CompletedTask).WaitAsync(cancel);
        IReadOnlyList<Finding> problems = [.. built.Problems.Select(p => new Finding(
            p.Severity == ProblemSeverity.Error ? "error" : "warning", p.Code, p.Message, p.Path is { } at ? Relative(at) : p.Project, p.Position.Line + 1, p.Position.Column + 1))];
        BuildResult result = new(built.Succeeded, built.Message, problems.Count(p => p.Severity == "error"), problems.Count(p => p.Severity == "warning"), [],
            built.Succeeded || built.Output.Count == 0 ? null : Masked(string.Join('\n', built.Output))) { Folder = built.Published, Image = built.Image };
        (IReadOnlyList<Finding>[] kept, int[] left) = AgentAnswers.Severest<Finding>([problems], p => Rank(p.Severity) ?? 2, 4, AgentAnswers.MostFindings, Size, Room(result));
        return result with
        {
            Problems = left.Sum() == 0 ? problems : kept[0],
            More = left.Sum() == 0 ? null : AgentAnswers.More(left.Sum(), "problem", $"{LeftOut(left)}; diagnostics with no path, a folder's, project or severity lists the build's"),
        };
    });

    [McpServerTool(Name = McpTools.Run, Title = "Run a configuration", Destructive = true), Description(
        "Runs a configuration as Run does, built first when it says so, and waits for it to end, an Avalonia or WPF app's " +
        "UI or an AppHost's resources to be up, its host's start (where it listens), or its app's start on a device. Returns its output so far and its exit code once it has one; a run refused (one " +
        "running, a failed build with its errors) is an error. A terminal configuration runs in a Terminal tab (terminal_run " +
        "types into it); a container configuration answers once its services are up, with each one's state. With resource, " +
        "that one alone, its configuration started if no run has it.")]
    public Task<RunResult> Run(
        string? configuration = null,
        string? arguments = null,
        int waitSeconds = 30,
        [Description("Under dotnet watch (Settings' choice by default), where hot_reload or a save applies edits")] bool? hotReload = null,
        string? resource = null,
        [Description("Collect its logs and traces from now on (false stops), as Edit Configurations' box does; telemetry reads them")] bool? telemetry = null,
        bool? terminal = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (resource is not null)
        {
            if (hotReload is not null)
            {
                throw new ArgumentException("resource uses Run Without Debugger; omit hotReload");
            }
            if (telemetry is not null)
            {
                throw new ArgumentException("An AppHost's resource sends its logs and traces to the AppHost's own dashboard; omit telemetry");
            }
            NoTerminalFor(terminal);
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
        using Choosing choice = Choose(configuration);
        string name = Session.Configurations.Chosen?.Name ?? string.Empty;
        if (Session.Configurations.Chosen is { IsMultiple: true })
        {
            return arguments is null && telemetry is null && terminal is null ? await RunProjectsAsync(name, hotReload, waitSeconds, choice, cancel)
                : throw new ArgumentException($"{name} starts each of its projects as its own configuration does: omit arguments, telemetry and terminal, or run that configuration");
        }
        UseTerminal(name, terminal);
        if (telemetry is { } collect)
        {
            if (Session.Configurations.Chosen is { } collecting && Session.Aspire.IsAspire(collecting))
            {
                throw new ArgumentException($"{name} is an AppHost's, whose dashboard has its logs and traces already; omit telemetry");
            }
            Session.Configurations.Collect(name, collect);
        }
        BuildRunController builds = Builds;
        AgentCall.Follow(window.Actions.Work, AppActions.RunProgram, AppActions.Build);
        using (LaunchBuild built = new(builds))
        {
            if (await Started(builds.RunAsync(arguments, hotReload), AppActions.RunProgram, cancel) is { } refused)
            {
                throw new InvalidOperationException(WithBuildErrors(refused, built.Ended));
            }
        }
        choice.Began = true;
        if (builds.Containers is { } running)
        {
            // Its services are up once Run answers: what an agent wants is how each stands and what they said.
            return new RunResult(true, null, await ServicesAsync(running, cancel), Tail(Session.Runner.Shown(), 200));
        }
        Task<int> ended = builds.RunEnd;
        LiveTree.LiveApp? app = Session.RunApp;
        // A Godot game Run started, Passenger its remote debugger.
        Godot.GodotGame? game = Session.RunPlan is { Godot: not null, GodotEditor: false } ? Session.Game : null;
        HeardServing serving = HeardServing.Ran(Session);
        // An app Run installed and started on a device or emulator, up once its shell there found its process.
        Run.Devices.DeviceLaunch? device = Session.RunPlan?.Device is { App: not null } launched ? launched : null;
        string? onDevice = null;
        if (Session.Configurations.Chosen is { } host && Session.Aspire.IsAspire(host) && Session.Aspire.FindApp(host.Project) is { } aspireApp)
        {
            AgentCall.Say($"Running {name}: waiting up to {Math.Max(0, waitSeconds)} s for it to end or its resources to be up");
            await AppHostUpAsync(aspireApp, TimeSpan.FromSeconds(Math.Max(0, waitSeconds)), cancel);
        }
        else
        {
            AgentCall.Say(device is not null ? $"Running {name}: waiting up to {Math.Max(0, waitSeconds)} s for it to end or start on {device.Target.Name}"
                : $"Running {name}: waiting up to {Math.Max(0, waitSeconds)} s for it to end, its UI to be up or its host to start");
            // A window, a serving host, a game's frames or an app on a device never end by themselves; looked at every 50 ms, its words reaching the console unannounced.
            await Until(() => ended.IsCompleted || app is { State: not LiveTree.LiveAppState.Waiting } || game is { Monitors: not null } or { State: LiveTree.LiveAppState.Ended }
                || serving.Serves() || (onDevice = device?.App!.StartedIn(Session.Runner.Shown())) is not null, TimeSpan.FromSeconds(Math.Max(0, waitSeconds)), cancel);
        }
        if (app is { State: LiveTree.LiveAppState.Refused or LiveTree.LiveAppState.Ended })
        {
            // An app ended for the person's screen says so as it exits, its inspector's end heard before the run's or after: an answer is not to call it running.
            await When(() => ended.IsCompleted, Waits.Of(TimeSpan.FromSeconds(3)), cancel, Ends(ended));
        }
        int? code = ended.IsCompletedSuccessfully ? ended.Result : null;
        string output = AppHostOutput(200) ?? RunOutput(200);
        // A game, a XAML app or an app on a device Run attached to quietly takes hot_reload; one asked for with it and not attached says why not.
        string reloads = Session.Debug.Running.Any(r => r.Name == name) ? $" with Hot Reload: {McpTools.HotReload} applies the editors' edits"
            : hotReload == true && window.Status is { } told && told.StartsWith($"Running {name} without Hot Reload: ", StringComparison.Ordinal) ? told[$"Running {name}".Length..]
            : string.Empty;
        string status = app is { State: LiveTree.LiveAppState.Refused or LiveTree.LiveAppState.Ended, Problem: { } uninspected }
            ? $"{(code is int gone ? BuildRunController.RunEnded(name, gone) : $"Running {name}{reloads}")}. {uninspected}"
            : code is int exit ? BuildRunController.RunEnded(name, exit)
            : app is { State: LiveTree.LiveAppState.Connected } ? $"Running {name}{reloads}, {UiUp(app)}"
            : game is { State: LiveTree.LiveAppState.Connected, Monitors: not null } ? $"Running {name}{reloads}, {GameUp}"
            : serving.Said is { } said ? $"Running {name}, {said}"
            : device is not null && onDevice is not null ? $"Running {name}{reloads}, {device.App!.Package} started on {device.Target.Name} as process {onDevice}"
            : $"Running {name}";
        // What an Aspire run said of itself as it started (its HTTPS over HTTP).
        if (Session.Configurations.Chosen is { } chosen && Session.Aspire.IsAspire(chosen) && Session.Aspire.FindApp(chosen.Project) is { } aspire)
        {
            status += string.Concat(aspire.Notes.Select(n => $"\nNote: {n}"));
        }
        // A program that said it needs a terminal, as the Run window offers one.
        if (builds.RunNeed is { } need)
        {
            status += $". {need.Said}: {McpTools.Run} with terminal true runs it in a Terminal tab";
        }
        return new RunResult(code is null, code, status, output);
    });

    /// <summary>terminal, as run and debug_start take it: the chosen configuration's program started in a Terminal tab from now
    /// on, or in the Run and Debug windows' consoles, as Edit Configurations keeps its Use external console.</summary>
    private void UseTerminal(string name, bool? terminal)
    {
        if (terminal is not { } inTerminal)
        {
            return;
        }
        if (Session.Configurations.Chosen is { } elsewhere && (elsewhere.IsContainer || elsewhere.IsScript || Session.Aspire.IsAspire(elsewhere)))
        {
            throw new ArgumentException($"{name} runs {(elsewhere.IsContainer ? "containers" : elsewhere.IsScript ? "a package.json script" : "an AppHost")}, whose console is its own; omit terminal");
        }
        Session.Configurations.UseConsole(name, inTerminal ? ConsoleKind.Terminal : ConsoleKind.Internal);
    }

    /// <summary>terminal refused with resource: an AppHost's resource or a compose service writes to its run's own console.</summary>
    private static void NoTerminalFor(bool? terminal)
    {
        if (terminal is not null)
        {
            throw new ArgumentException("A resource writes to its AppHost's or its compose run's console; omit terminal");
        }
    }

    /// <summary>run of a multi-project configuration: its projects started, then each waited for until it ends or its host
    /// starts, within the wait; how each stands, and each one's last lines under its name.</summary>
    private async Task<RunResult> RunProjectsAsync(string name, bool? hotReload, int waitSeconds, Choosing choice, CancellationToken cancel)
    {
        BuildRunController builds = Builds;
        AgentCall.Follow(window.Actions.Work, AppActions.RunProgram, AppActions.Build);
        using (LaunchBuild built = new(builds))
        {
            if (await Started(builds.RunAsync(null, hotReload), AppActions.RunProgram, cancel) is { } refused)
            {
                throw new InvalidOperationException(WithBuildErrors(refused, built.Ended));
            }
        }
        choice.Began = true;
        (IReadOnlyList<string> debugged, IReadOnlyList<TerminalRun> ran, IReadOnlyList<string> notes) = builds.ProjectsOf(name);
        List<(TerminalRun Run, HeardServing Serving)> each = [.. ran.Select(r => (r, new HeardServing(() => r, () => r.Session, () => TextSnapshot.Empty)))];
        AgentCall.Say($"Running {name}: waiting up to {Math.Max(0, waitSeconds)} s for each of its programs to end or its host to start");
        // A terminal's screen raises nothing an agent's wait can hear: looked at every 50 ms.
        await Until(() => each.All(e => e.Run.Ended.IsCompleted || e.Serving.Serves()), TimeSpan.FromSeconds(Math.Max(0, waitSeconds)), cancel);
        IEnumerable<string> stands = each.Select(e => Stands(e.Run, e.Serving));
        string output = string.Join("\n", each.Select(e => $"{e.Run.Name}:\n{Tail(e.Run.Session, 40)}"));
        return new RunResult(each.Any(e => !e.Run.Ended.IsCompleted), null,
            $"{BuildRunController.ProjectsStarted(name, debugged, [.. ran.Select(r => r.Name)])}. {string.Join("; ", stands)}{string.Concat(notes.Select(n => $"\nNote: {n}"))}", output);

        static string Stands(TerminalRun run, HeardServing serving)
        {
            Task<int> ended = run.Ended;
            return ended.IsCompletedSuccessfully ? BuildRunController.RunEnded(run.Name, ended.Result)
                : serving.Said is { } said ? $"{run.Name}, {said}" : $"{run.Name}, running";
        }
    }

    [McpServerTool(Name = McpTools.Output, Title = "A console's last lines", ReadOnly = true), Description(
        "The last lines of a console. of run (the default): what Run's program wrote (under Hot Reload, dotnet watch's words " +
        "too) or a terminal configuration's tab; with resource, that one's own; with requests, the running web apps' requests " +
        "and their SQL. of debug (the default with session, or while a program is debugged and Run's is not running): the " +
        "debugged program's. Either's first line names whose output it is. of build: the last build's MSBuild output, as it " +
        "runs too. of terminal: the IDE terminal's tab shown, its scrollback and screen (a full-screen program's too).")]
    public Task<string> Output(string? of = null, int lines = 200, string? resource = null, bool requests = false, string? session = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (lines < 1)
        {
            throw new ArgumentException($"lines is {lines}: a console's last lines are 1 or more");
        }
        // Unnamed, the console of the program most likely meant: a debugged one before a run that has ended.
        bool debugged = session is not null || (resource is null && !requests && !Builds.IsRunning && Session.Debug.IsDebugging);
        switch (of ?? (debugged ? "debug" : "run"))
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
        if (AppHostOutput(lines) is { } appHost)
        {
            return appHost;
        }
        return Builds.Containers is { } running ? $"{await ServicesAsync(running, cancel)}\n{RunOutput(lines)}" : RunHeaded(lines);
    });

    /// <summary>The last lines of the console of the AppHost the chosen configuration runs (or ran last), which Run's own
    /// console never holds; null where the configuration is no AppHost's.</summary>
    private string? AppHostOutput(int lines)
    {
        if (Session.Configurations.Chosen is not { } configuration || !Session.Aspire.IsAspire(configuration)
            || (Session.Aspire.FindApp(configuration.Project)?.Console ?? Session.Aspire.LastAppConsole) is not { } console)
        {
            return null;
        }
        console.Flush();
        return Tail(console.Document.Snapshot, lines);
    }

    /// <summary>output's run: the console's last lines under a line naming the program Run last started and how it stands,
    /// and any program debugged meanwhile, whose console is output's debug.</summary>
    private string RunHeaded(int lines)
    {
        string output = RunOutput(lines);
        if (Session.RunPlan?.Name is not { } name)
        {
            return output.Length > 0 ? output : "Run has started nothing yet, so its console is empty: run starts a configuration (debug_start's is output of debug)";
        }
        IRunningProgram program = Session.RunProgram;
        Task<int> ended = program.Ended;
        string stands = program.IsRunning ? $"{name}, running"
            : ended.IsCompletedSuccessfully ? BuildRunController.RunEnded(name, ended.Result)
            : $"{name}, not running";
        string[] debugged = [.. Session.Debug.Tabs.Where(t => t.IsLive && t.Session is not null).Select(t => t.Name)];
        string also = debugged.Length > 0 ? $"; debugged meanwhile, output of debug: {Plural.Listed(debugged)}" : string.Empty;
        return $"{stands}{also}:\n{output}";
    }

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
            // A session's app on a device ends there after the session.
            await Session.EndedOnDevicesAsync(tab.Name);
            return $"Stopped {tab.Name}";
        }
        string? idle = Builds.WhyNotStop();
        await Builds.StopAllAsync().WaitAsync(cancel);
        return idle is null ? "Stopped" : $"{idle}, so nothing was stopped";
    });

    // Unit tests

    [McpServerTool(Name = McpTools.TestsRun, Title = "Run unit tests", Destructive = true), Description(
        "Builds and runs the unit tests, or those whose full name contains filter, as the Unit Tests window does, each project " +
        "by the runner its build asks for (Microsoft Testing Platform for xUnit v3, MSTest's and NUnit's runners and TUnit; a Godot game's GUT " +
        "and GoDotTest in Godot; VSTest for the rest). With debug, under the debugger: a stop in a test comes back, and debug_continue goes on. Returns the tally and " +
        "every failure with its message and stack trace; a failed Reqnroll or SpecFlow scenario's steps each with how it went, one that did not pass with the " +
        "definition its text binds, its arguments, what was said and the stack's frames in the folder. With coverage, measures which lines and branches the tests ran " +
        "(Coverlet, never added to a project): the share of lines and branches, and each file's, least covered first, with its lines never run and those with a branch never taken.")]
    public Task<TestRunInfo> TestsRun(
        string? filter = null,
        bool debug = false,
        bool coverage = false, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        string folder = Session.Folder ?? throw new InvalidOperationException(Refusals.NoFolder);
        if (coverage && debug)
        {
            throw new ArgumentException(Refusals.CoverageDebugged);
        }
        Testing.TestController tests = Session.Tests;
        AgentCall.Say(filter is null ? "Building and running every test" : $"Building and running the tests whose names contain '{filter}'");
        AgentCall.Follow(window.Actions.Work, AppActions.Build);
        using LaunchBuild built = new(Builds);
        // Those the code declares found first, as tests_list waits for them; looked at every 50 ms, as the finder raises nothing as it is done.
        await Until(() => !tests.IsLooking, TestsLookLimit, cancel);
        // Matched once built: a test written a moment ago, or one only a runner finds, is among them.
        Task<string?> run = filter is null ? tests.RunAsync(folder, null, debug, cover: coverage)
            : tests.RunMatchingAsync(folder, m => m.FullName is { } name && name.Contains(filter, StringComparison.Ordinal),
                unfound => $"No test's name contains '{filter}'" + (unfound is null ? string.Empty : $": {unfound}"), debug, coverage);
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
        TestRunInfo ran = TestInfo(tests, await StepsOfAsync([.. tests.RunOutcomes.Where(o => o.State == Testing.TestState.Failed)], cancel));
        return coverage && tests.Coverage is { } measured ? ran with { Coverage = CoverageOf(measured, tests.Unmeasured, Room(ran)) } : ran;
    });

    /// <summary>A run's coverage as tests_run answers it: what it came to, then each file's, least covered first, its lines
    /// never run as stretches ("12-15", the lines between that hold no code taken in) and those with a branch never taken, as
    /// many files as <paramref name="room"/> holds.</summary>
    private CoverageInfo CoverageOf(Testing.CoverageReport measured, IReadOnlyList<string> unmeasured, int room)
    {
        List<CoveredFile> files = [];
        foreach ((string path, IReadOnlyList<Testing.CoveredLine> lines) in measured.Files)
        {
            Testing.CoverageCounts counts = lines.Aggregate(default(Testing.CoverageCounts), (c, l) => c.Plus(l));
            List<string> missed = [];
            for (int at = 0; at < lines.Count; at++)
            {
                if (lines[at].State != Testing.CoverageState.Missed)
                {
                    continue;
                }
                int from = at;
                while (at + 1 < lines.Count && lines[at + 1].State == Testing.CoverageState.Missed)
                {
                    at++;
                }
                missed.Add(at == from ? $"{lines[from].Line}" : $"{lines[from].Line}-{lines[at].Line}");
            }
            List<string> partial = [.. lines.Where(l => l.State == Testing.CoverageState.Partial).Select(l => $"{l.Line} ({l.BranchesTaken} of {l.Branches} branches)")];
            files.Add(new CoveredFile(Relative(path), counts.LinesSaid, counts.Branches == 0 ? null : counts.BranchesSaid)
            {
                NeverRun = missed.Count == 0 ? null : string.Join(", ", missed),
                BranchesNotTaken = partial.Count == 0 ? null : string.Join(", ", partial),
                Rate = counts.LineRate ?? 1,
            });
        }
        files = [.. files.OrderBy(f => f.Rate).ThenBy(f => f.Path, StringComparer.Ordinal)];
        CoverageInfo info = new(measured.Files.Count == 0 ? "No coverage was measured" : $"{measured.Counts.LinesSaid} and {measured.Counts.BranchesSaid} run", [])
        {
            Unmeasured = unmeasured.Count == 0 ? null : unmeasured,
        };
        int kept = AgentAnswers.Fitting(files, Size, room - Size(info));
        return info with
        {
            Files = kept == files.Count ? files : [.. files.Take(kept)],
            More = kept == files.Count ? null : AgentAnswers.More(files.Count - kept, "file", "tests_run with a filter measures fewer tests' coverage"),
        };
    }

    [McpServerTool(Name = McpTools.TestsList, Title = "The unit tests", ReadOnly = true), Description(
        "The tests the Unit Tests window shows (from code, and their runner once built), each with its full name, state from " +
        "the last run (earlier: when, if an earlier session's) and file:line: those whose full name contains filter and whose state is state, at most limit.")]
    public Task<CallToolResult> TestsList(
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
        List<Testing.TestNode> asked = [.. Session.Tests.Tree.Methods
            .Where(m => (filter is null || (m.FullName ?? m.Name).Contains(filter, StringComparison.Ordinal)) && (only is null || m.State == only))];
        return Held([.. asked.Take(Math.Max(1, limit)).Select(m => new TestInfoRow(TestName(m), StateName(m.State), m.Path is { } at ? $"{Relative(at)}:{m.Line}" : null)
            {
                Earlier = m.Standing,
            })], "test", "filter or state narrows them", Math.Max(0, asked.Count - Math.Max(1, limit)), LargerLimit);
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
    private TestRunInfo TestInfo(Testing.TestController tests, IReadOnlyDictionary<Testing.TestOutcome, IReadOnlyList<StepInfo>>? steps = null)
    {
        List<TestFailure> failures = [];
        int stepsGiven = 0;
        int stepsLeft = 0;
        foreach (IGrouping<(string, string, string?), Testing.TestOutcome> method in tests.RunOutcomes
            .GroupBy(o => (o.Test.Project, o.Test.Method, o.Test.Framework)).Where(m => m.Any(o => o.State == Testing.TestState.Failed)))
        {
            Testing.TestItem test = method.First().Test;
            Testing.TestNode? shown = tests.Tree.Node(test) is { } node ? node.Kind == Testing.TestNodeKind.Row ? node.Parent : node : null;
            bool several = shown?.Framework is not null || tests.RunOutcomes.Any(o => o.Test.Project == test.Project && o.Test.Method == test.Method && o.Test.Framework != test.Framework);
            string? place = shown?.Path is { } at ? $"{Relative(at)}:{shown.Line}" : test.Path is { } found ? $"{Relative(found)}:{test.Line}" : null;
            List<StepInfo> its = steps is null ? [] : [.. method.SelectMany(o => steps.GetValueOrDefault(o) ?? [])];
            if (its.Count > 0 && stepsGiven + its.Count > AgentAnswers.MostFindings)
            {
                stepsLeft++;
                its = [];
            }
            stepsGiven += its.Count;
            failures.Add(new TestFailure(shown is null ? $"{test.Method}{(several ? $" ({test.Framework})" : string.Empty)}" : TestName(shown), place,
                Masked(Testing.TestNode.FailuresOf([.. method], FailureOutputLines)))
            {
                Steps = its.Count > 0 ? its : null,
            });
        }
        TestRunInfo run = new(tests.Summary, [])
        {
            StepsLeftOut = stepsLeft == 0 ? null : $"The steps of {Plural.Of(stepsLeft, "more failed scenario")} are left out; tests_run with a filter naming one answers its steps",
        };
        int kept = AgentAnswers.Fitting(failures, Size, Room(run));
        return run with
        {
            Failures = kept == failures.Count ? failures : [.. failures.Take(kept)],
            More = kept == failures.Count ? null : AgentAnswers.More(failures.Count - kept, "failed test", "tests_list with state failed names them, and tests_run with a filter answers theirs"),
        };
    }

    /// <summary>How many of a failure's last lines of output an answer gives: an agent's answer holds no test's every line.</summary>
    private const int FailureOutputLines = 50;

    /// <summary>Each failed Reqnroll or SpecFlow scenario's steps as its run went, by its outcome (<see cref="MainWindow.FeatureRunsAsync"/>):
    /// each step's text, place and outcome; one that did not pass with the definition its text binds as the editor matches it,
    /// the arguments it ran with as converted where the run says them, else as its text gives them, what the run said, the
    /// failure's frames in the folder, and for one undefined or ambiguous what came close, in the editor's words.</summary>
    private async Task<Dictionary<Testing.TestOutcome, IReadOnlyList<StepInfo>>> StepsOfAsync(IReadOnlyList<Testing.TestOutcome> failed, CancellationToken cancel)
    {
        Dictionary<Testing.TestOutcome, IReadOnlyList<StepInfo>> steps = new(ReferenceEqualityComparer.Instance);
        foreach (Bdd.FeatureRun feature in await window.FeatureRunsAsync(Session, failed))
        {
            try
            {
                // The bindings the definitions are named from, read from the code as the server has it now.
                await ServerSettledAsync(feature.Path, _ => true, cancel);
                await window.StepsAsync(cancel);
            }
            catch (Exception unread) when (unread is TimeoutException or InvalidOperationException)
            {
                AgentCall.Say($"The steps' definitions are not named: {unread.Message}");
            }
            Bdd.FeatureAnalysis analysis = await Session.Features.ReadAsync(feature.Path, feature.Text);
            foreach (IGrouping<Testing.TestOutcome, (Passenger.Workspace.Gherkin.StepResult Result, Testing.TestOutcome Test)> test in feature.Steps.GroupBy(s => s.Test, ReferenceEqualityComparer.Instance as IEqualityComparer<Testing.TestOutcome>))
            {
                steps[test.Key] = [.. test.Select(s => StepOf(feature, analysis, s.Result, s.Test))];
            }
        }
        return steps;
    }

    private StepInfo StepOf(Bdd.FeatureRun feature, Bdd.FeatureAnalysis analysis, Passenger.Workspace.Gherkin.StepResult result, Testing.TestOutcome test)
    {
        StepInfo step = new(result.Said, $"{Relative(feature.Path)}:{result.Line + 1}", result.Outcome.ToString().ToLowerInvariant())
        {
            Row = test.Row,
        };
        if (result.Outcome == Passenger.Workspace.Gherkin.StepOutcome.Passed)
        {
            return step;
        }
        Passenger.Workspace.Gherkin.FeatureStep? written = analysis.Reading.Steps.FirstOrDefault(s => s.Line == result.Line);
        Passenger.Workspace.Gherkin.StepMatch? match = null;
        if (written is not null && analysis.Matcher is { } matcher && result.Said.StartsWith(written.Keyword.TrimEnd(), StringComparison.Ordinal))
        {
            string text = result.Said[written.Keyword.TrimEnd().Length..].TrimStart();
            Passenger.Workspace.Gherkin.FeatureScenario scenario = written.Scenario;
            match = matcher.Match(written.Kind, text, written.DocString is not null, written.Table is not null,
                new Passenger.Workspace.Gherkin.StepContext(analysis.Reading.Feature?.Title, scenario.Title, scenario.AllTags));
            if (match.Bindings.FirstOrDefault() is { } bound)
            {
                step = step with
                {
                    Definition = bound.Binding.MethodPlace is { } place ? $"{Relative(place.Path)}:{place.Line + 1} {bound.Binding.Type}.{bound.Binding.Method}" : $"{bound.Binding.Type}.{bound.Binding.Method}",
                    Arguments = result.Arguments ?? string.Join(", ", bound.Arguments.Select((a, i) =>
                        $"{(i < bound.Binding.Parameters.Count ? $"{bound.Binding.Parameters[i].Name} ({bound.Binding.Parameters[i].Type}): " : string.Empty)}{text.Substring(a.Start, a.Length)}")),
                };
            }
        }
        string? said = result.Outcome is Passenger.Workspace.Gherkin.StepOutcome.Undefined or Passenger.Workspace.Gherkin.StepOutcome.Ambiguous
            ? string.Join("\n", analysis.Marks.Where(m => analysis.Text.GetLineFromOffset(m.Range.Start) == result.Line).Select(m => m.Message)) is { Length: > 0 } marked ? marked : result.Message
            : result.Message ?? (result.Outcome == Passenger.Workspace.Gherkin.StepOutcome.Failed ? test.Message : null);
        return step with
        {
            Message = said is null ? null : Masked(said),
            Frames = result.Outcome == Passenger.Workspace.Gherkin.StepOutcome.Failed && OwnFrames(test.StackTrace) is { Count: > 0 } frames ? frames : null,
        };
    }

    /// <summary>The frames of a stack in the open folder's files, each "at Type.Method(…) in path:line" with the path the folder's own.</summary>
    private List<string> OwnFrames(string? stack)
    {
        List<string> frames = [];
        foreach (string line in (stack ?? string.Empty).Split('\n'))
        {
            string frame = line.Trim();
            int @in = frame.LastIndexOf(" in ", StringComparison.Ordinal);
            int at = frame.LastIndexOf(":line ", StringComparison.Ordinal);
            if (!frame.StartsWith("at ", StringComparison.Ordinal) || @in < 0 || at < @in || Session.Folder is not { } folder)
            {
                continue;
            }
            string path = frame[(@in + 4)..at];
            if (Path.IsPathFullyQualified(path) && PathNames.IsUnder(PathNames.Canonical(path), folder))
            {
                frames.Add($"{frame[..@in]} in {Relative(PathNames.Canonical(path))}:{frame[(at + 6)..]}");
            }
        }
        return frames;
    }

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
        "and Xaml); GDScript Errors (scriptErrors: a debugged Godot game stops at a script error, on by default); or Break When " +
        "Value Changes on control's property (as app_details names it) in the app debugged, for the " +
        "session, condition reading newValue, oldValue and control. condition is a C# bool (GDScript in .gd); hitCondition \"==3\", \">=2\" or " +
        "\"%2\"; logMessage (\"count is {count}\", \\{ for a brace) is written each hit; suspend false makes a tracepoint. Or on a method " +
        "(method: Name, Type.Name or Namespace.Type.Name(int, ref string?) for one overload, Type..ctor a constructor), stopping as it begins. A line or method " +
        "breakpoint takes temporary (removed once hit) and waitFor, another's place (\"Program.cs:12\") or method, whose hit it waits " +
        "for. Bound at once while debugging.")]
    public Task<IReadOnlyList<BreakpointInfo>> SetBreakpoint(
        string? path = null,
        int? line = null,
        string? attribute = null,
        int? column = null,
        string? exception = null,
        bool bindingErrors = false,
        bool scriptErrors = false,
        string? control = null,
        string? property = null,
        string? condition = null,
        string? hitCondition = null,
        string? logMessage = null,
        bool suspend = true,
        [Description("thrownInUserCode, thrownInExternalCode, handledInUserCode, handledInExternalCode, unhandled: it stops when " +
            "one of each, thrown and handled, holds")] IReadOnlyList<string>? scenarios = null,
        bool remove = false,
        string? method = null,
        bool temporary = false,
        string? waitFor = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        BreakpointStore store = Session.Debug.Breakpoints;
        if ((temporary || waitFor is not null) && (remove || bindingErrors || scriptErrors || control is not null || property is not null || exception is not null))
        {
            throw new ArgumentException("temporary and waitFor take a line or method breakpoint");
        }
        if (method is { Length: > 0 })
        {
            if (remove)
            {
                if (store.FindMethod(method) is { } there)
                {
                    store.Remove(there);
                }
            }
            else
            {
                MethodBreakpoint named = store.AddMethod(method);
                store.Update(named, enabled: true, suspend, condition, hitCondition, logMessage);
                store.Arrange(named, temporary, WaitedFor(waitFor));
            }
            await InTheProgram(cancel);
            return MethodBreakpointsInfo();
        }
        if (remove)
        {
            return await RemoveBreakpoint(path, line, attribute, column, exception, bindingErrors, scriptErrors, control, property, cancel);
        }
        if (bindingErrors || scriptErrors)
        {
            store.Update(scriptErrors ? store.ScriptErrors : store.BindingErrors, enabled: true, suspend, condition, hitCondition, logMessage);
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
        // A file the agent may not read is told nothing of, there or not: its breakpoint is set as named, for the debugger to place.
        bool readable = Reaches(full, changes: false);
        if (readable)
        {
            TextSnapshot text = (Editors.Find(full) is { HoldsText: true } open ? open.Text.Snapshot : await BackgroundWork.Run(() => Passenger.App.Editors.EditorArea.Read(full)))
                ?? throw new ArgumentException(File.Exists(full) ? $"{Relative(full)} has no text to set a breakpoint in" : $"No file {Relative(full)} is here to set a breakpoint in");
            LineIn(text, full, at + 1);
        }
        LineBreakpoint set = column is int inside ? (await LambdaBreakpointAsync(full, at, inside, set: true))! : store.Set(full, at, attribute is { Length: > 0 } ? attribute : null);
        store.Update(set, enabled: true, suspend, condition, hitCondition, logMessage);
        store.Arrange(set, temporary, WaitedFor(waitFor));
        await InTheProgram(cancel);
        Passenger.Languages.CSharp.ParsedText? parsed = Session.Debug.IsDebugging || !readable ? null : await LambdaBreakpoints.ParsedAsync(Editors, full);
        // An open file's parse only as its text stands: its lines may have moved since.
        if (parsed is not null && Editors.Find(full) is { HoldsText: true } shown && !ReferenceEquals(parsed.Text, shown.Text.Snapshot))
        {
            parsed = null;
        }
        return Breakpoints(full, parsed);
    });

    /// <summary>breakpoint_set's remove: the breakpoint its other arguments name taken away, or for "Any exception" and
    /// XAML Binding Errors turned off.</summary>
    private async Task<IReadOnlyList<BreakpointInfo>> RemoveBreakpoint(string? path, int? line, string? attribute, int? column, string? exception, bool bindingErrors,
        bool scriptErrors, string? control, string? property, CancellationToken cancel)
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
        if (bindingErrors || scriptErrors)
        {
            Breakpoint failures = scriptErrors ? store.ScriptErrors : store.BindingErrors;
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
    /// begins holds that column, and in a file the agent may not read.</summary>
    private async Task<LineBreakpoint?> LambdaBreakpointAsync(string path, int line, int column, bool set)
    {
        if (!Reaches(path, changes: false))
        {
            throw new InvalidOperationException(Refusals.OutOfReach(path, Session.Folder, changes: false));
        }
        Passenger.Languages.CSharp.ParsedText parsed = await LambdaBreakpoints.ParsedAsync(Editors, path)
            ?? throw new ArgumentException($"A column names a lambda's breakpoint in a C# file, which {Relative(path)} is not");
        if (line >= parsed.Text.LineCount)
        {
            throw new ArgumentException($"{Relative(path)} has {Numbers.Said(parsed.Text.LineCount)} lines");
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
        OnUi(cancel, () => Task.FromResult<IReadOnlyList<BreakpointInfo>>([.. Breakpoints(null), .. MethodBreakpointsInfo(), .. DataBreakpoints(), .. ExceptionBreakpoints()]));

    /// <summary>The line or method breakpoint breakpoint_set's waitFor names: a method breakpoint by its method, else a line
    /// breakpoint by its file and line from one ("Program.cs:12"), the line's own before its parts'.</summary>
    private CodeBreakpoint? WaitedFor(string? waitFor)
    {
        if (waitFor is not { Length: > 0 } named)
        {
            return null;
        }
        BreakpointStore store = Session.Debug.Breakpoints;
        if (store.FindMethod(named) is { } method)
        {
            return method;
        }
        int colon = named.LastIndexOf(':');
        if (colon > 0 && int.TryParse(named.AsSpan(colon + 1), out int line))
        {
            string path = Named(named[..colon]);
            if (store.In(path).Where(b => b.Line == line - 1).OrderBy(b => b.OnPart).FirstOrDefault() is { } there)
            {
                return there;
            }
        }
        throw new ArgumentException($"No breakpoint is at {named} for this one to wait for: set it first (debug_state of breakpoints lists those there are)");
    }

    /// <summary>The breakpoint one waits for as waitFor names it: a line's file from the folder and line from one, or a method.</summary>
    private string? WaitPlace(CodeBreakpoint? waited) => waited switch
    {
        LineBreakpoint line => $"{Relative(line.Path)}:{line.Line + 1}",
        MethodBreakpoint method => method.Name,
        _ => null,
    };

    private IReadOnlyList<BreakpointInfo> MethodBreakpointsInfo()
    {
        DebugController debugger = Session.Debug;
        return [.. debugger.Breakpoints.Methods.Select(m => new BreakpointInfo(m.Path is { } path ? Relative(path) : null, m.Path is null ? null : m.Line + 1, m.Enabled,
            BreakpointStates.StateOf(m, debugger.IsDebugging, debugger.Breakpoints.Muted), m.Condition, m.HitCondition, m.LogMessage, m.Suspend,
            m.Status == BreakpointStatus.Bound ? null : m.Message, Method: m.Name, Temporary: m.Temporary ? true : null, WaitsFor: WaitPlace(m.WaitsFor)))];
    }

    [McpServerTool(Name = McpTools.DebugStart, Title = "Debug a configuration", Destructive = true), Description(
        "Builds and launches a configuration under the debugger, as Debug does, and waits for a stop, its end, an Avalonia " +
        "or WPF app's UI, its host's start (where it listens) or its app's start on a device. Returns where it is. With " +
        "resource, that one alone, restarted.")]
    public Task<DebugSnapshot> DebugStart(
        string? configuration = null,
        string? arguments = null,
        int waitSeconds = 60, string? resource = null, bool? terminal = null, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        if (resource is not null)
        {
            NoTerminalFor(terminal);
            if (await ComposeTargetAsync(resource, configuration, starts: true, cancel) is { } service)
            {
                return arguments is null ? await DebugComposeServiceAsync(service, attach: false, waitSeconds, cancel)
                    : throw new ArgumentException("A compose service runs as its compose files say; omit arguments");
            }
            return await DebugAspireTarget(resource, configuration, arguments, attach: false, waitSeconds, cancel);
        }
        using Choosing choice = Choose(configuration);
        AgentCall.Follow(window.Actions.Work, AppActions.Debug, AppActions.Build);
        bool several = Session.Configurations.Chosen is { IsMultiple: true };
        if (several && (arguments is not null || terminal is not null))
        {
            throw new ArgumentException($"{Session.Configurations.Chosen!.Name} starts each of its projects as its own configuration does: omit arguments and terminal, or debug that configuration");
        }
        UseTerminal(Session.Configurations.Chosen?.Name ?? string.Empty, terminal);
        string? started = null;
        using (LaunchBuild built = new(Builds))
        {
            if (await Started(Builds.DebugAsync(arguments), AppActions.Debug, cancel) is { } problem)
            {
                throw new InvalidOperationException(WithBuildErrors(problem, built.Ended));
            }
            choice.Began = true;
            // What a multi-project start started, the answer's own being the program the debugger shows.
            if (several && Session.Configurations.Chosen is { } chosen)
            {
                (IReadOnlyList<string> debugged, IReadOnlyList<TerminalRun> ran, _) = Builds.ProjectsOf(chosen.Name);
                started = BuildRunController.ProjectsStarted(chosen.Name, debugged, [.. ran.Select(r => r.Name)]);
            }
        }
        DebugSnapshot snapshot = await AfterLaunch(TimeSpan.FromSeconds(waitSeconds), cancel);
        return started is null ? snapshot : snapshot with { Status = $"{started}. The debugger shows {Session.Debug.Chosen?.Name ?? "none of them"}: {snapshot.Status}" };
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
            // Where the person chooses beside the configuration, an agent chooses with one call.
            return refused.EndsWith(Refusals.ChooseRunTarget, StringComparison.Ordinal) && Session.Configurations.Chosen is { } chosen
                ? $"{refused} ({McpTools.IdeAction} {AppActions.ChooseRunTarget} with input \"{chosen.Name}:\" lists them, \"{chosen.Name}:name\" chooses one)"
                : refused;
        }
        IEnumerable<string> lines = errors.Take(10).Select(p =>
            $"{(p.Path is { } at ? $"{Relative(at)}:{p.Position.Line + 1}:{p.Position.Column + 1}" : p.Project)}: {p.Code} {p.Message}");
        return $"{refused}:\n{string.Join('\n', lines)}{(errors.Count > 10 ? $"\n… {Numbers.Said(errors.Count - 10)} more: problems lists them" : "")}";
    }

    /// <summary>What an answer says of an app whose UI is up: where it draws, where nobody sees it, and what looks at it.</summary>
    private static string UiUp(LiveTree.LiveApp app) =>
        (app.MainLeftOut ? "its UI up on Avalonia's headless platform, where nobody sees it, its BuildAvaloniaApp() started in place of its Main"
            : app.Headless ? "its UI up on Avalonia's headless platform, where nobody sees it"
            : "its UI up") + ": app_windows, app_screenshot and app_click look at it and use it";

    /// <summary>What an answer says of a Godot game once it has connected to Passenger, its remote debugger.</summary>
    private const string GameUp = "Passenger its remote debugger: app_tree, app_details and app_click look at its nodes and use them";

    /// <summary>How long a launched app whose UI is up is watched for a stop in its first layout.</summary>
    private static readonly TimeSpan LaunchSettle = TimeSpan.FromSeconds(2);

    private static TimeSpan Min(TimeSpan one, TimeSpan other) => one < other ? one : other;

    /// <summary>Where a program just launched under the debugger is once it stops or ends, or, for an app with a window,
    /// which runs until it is closed, once its UI is up, for a Godot game once it has connected (a breakpoint set in its
    /// files waited for as a stop), for a host that serves until it is stopped, once it has started, saying so and where it
    /// listens, and for an app on a device, once it has started there: an agent waits for no more than it needs.</summary>
    private async Task<DebugSnapshot> AfterLaunch(TimeSpan limit, CancellationToken cancel)
    {
        LiveTree.LiveApp? app = Session.LiveApp;
        Godot.GodotGame? game = Session.Game is { Debugged: not null } debugged ? debugged : null;
        // An app debugged on a device or emulator, up once its shell there found its process, which its tab's console says.
        ToolWindows.DebugTab? device = Session.Debug.Chosen is { Plan.Device.App: not null } onDevice ? onDevice : null;
        string? process = null;
        Deadline until = Deadline.In(limit);
        // Running, its GDScript or C# beside it running too: a game stopped in either language is stopped.
        Func<bool> going = () => Session.Debug.Session is { State: DebugState.Running or DebugState.Starting } chosen && !Companions(chosen).Any(b => b.State == DebugState.Paused);
        HeardServing serving = HeardServing.Debugged(Session.Debug);
        // A game's frames reach a breakpoint in its files once the debugger binds it, seconds after it connects: waited for as any stop.
        bool breaks = game is not null && Session.Debug.Breakpoints.All.Any(b => b is { Enabled: true, Suspend: true } && PathNames.IsUnder(b.Path, game.Folder));
        string? stops = StopsWaitedFor(game?.Folder);
        AgentCall? call = AgentCall.Current;
        int? phase = null;
        bool Done()
        {
            bool done = !going() || app is { State: not LiveTree.LiveAppState.Waiting } || (!breaks && game is { Monitors: not null })
                || game is { State: LiveTree.LiveAppState.Ended } || serving.Serves()
                || (device is not null && Session.Debug.Session is { State: DebugState.Running } && (process = StartedOnDevice(device)) is not null);
            // What the call waits for now, told as its progress over whatever the launch's own work says meanwhile.
            int now = Session.Debug.Session is not { State: not DebugState.Starting } ? 0 : game is { State: LiveTree.LiveAppState.Connected } ? 1
                : app is { State: LiveTree.LiveAppState.Waiting } ? 2 : device is not null ? 4 : 3;
            if (!done && now != phase)
            {
                phase = now;
                call?.Waits(Awaited(now, game?.Name, stops, limit, device?.Plan?.Device?.Target.Name));
            }
            return done;
        }
        try
        {
            // Looked at every 50 ms: the session the launch makes can come after the wait begins, and the debugger tells of none.
            await Until(Done, limit, cancel);
            bool waitedOut = !Done();
            if (going() && app is { State: LiveTree.LiveAppState.Connected })
            {
                // A stop in the first layout, just after the inspector's hello, is waited for too; looked at every 50 ms.
                await Until(() => !going(), Min(LaunchSettle, until.Left), cancel);
            }
            DebugSnapshot started = await AfterResume(TimeSpan.Zero, cancel);
            DebugSnapshot shown = app is { State: LiveTree.LiveAppState.Refused or LiveTree.LiveAppState.Ended, Problem: { } refused } ? started with { Status = $"{started.Status}. {refused}" }
                : started.Debugged != DebugState.Running ? started
                : app is { State: LiveTree.LiveAppState.Connected } ? started with { Status = $"{started.Status}, {UiUp(app)}" }
                : game is { State: LiveTree.LiveAppState.Connected, Monitors: not null } ? started with { Status = $"{started.Status}, {GameUp}" }
                : serving.Said is { } said ? started with { Status = $"{started.Status}, {await ServedFromHereAsync(said, cancel)}" }
                : process is not null && device?.Plan?.Device is { App: { } onIt } launch
                    ? started with { Status = $"{started.Status}, {onIt.Package} started on {launch.Target.Name} as process {process}" }
                : started;
            // The whole wait out with the program running: what it was waited for never stopped it.
            return waitedOut && shown.Debugged == DebugState.Running && stops is not null
                ? shown with { Status = $"{shown.Status}; in the {Durations.Said(limit)} waited it never stopped at {stops}" } : shown;
        }
        finally
        {
            call?.Waited(Session.Debug.Session?.State.ToString().ToLowerInvariant() ?? "no session");
        }
    }

    /// <summary>Where the program the debugger shows serves, in an agent's words, once it has said (<paramref name="heard"/>):
    /// a service of a run of services where its run reaches it from this machine (a container's published port, an AppHost's
    /// endpoint), as the window's own links do (<see cref="ServiceRun.AddressAsync"/>); the program's own words for any other.
    /// A container's own address reaches nothing here, so one whose run gives none says so rather than naming it.</summary>
    private async Task<string> ServedFromHereAsync(string heard, CancellationToken cancel)
    {
        if (Session.Debug.Session is not { } shown || ServiceDebuggedBy(shown) is not { } debugged)
        {
            return heard;
        }
        (ServiceRun run, IRunService service) = debugged;
        try
        {
            if (await run.AddressAsync(service, cancel) is { } address)
            {
                return $"listening at {address}";
            }
        }
        catch (Exception unread) when (run.Refused(unread) || unread is InvalidOperationException or System.Text.Json.JsonException or TimeoutException)
        {
            return run is Containers.ContainerRun ? $"its host started in its container, whose published port could not be read: {unread.Message}" : heard;
        }
        return run is Containers.ContainerRun ? "its host started in its container, which publishes none of its ports to this machine" : heard;
    }

    /// <summary>The run of services and the service of it a session debugs, as its run names the session debugging each; null
    /// for a program of no run of services.</summary>
    private (ServiceRun Run, IRunService Service)? ServiceDebuggedBy(DebugSession session)
    {
        foreach (ServiceRun run in Session.ServiceRuns)
        {
            if (run.Services.FirstOrDefault(s => run.SessionOf(Session.Debug, s) == session) is { } service)
            {
                return (run, service);
            }
        }
        return null;
    }

    /// <summary>What a launch under the debugger is waited for at a phase of its start, within <paramref name="limit"/> in
    /// all: the program to start, a game that runs to stop or end, an app's UI, its start on <paramref name="device"/>, else
    /// a stop, the end or a host's start; <paramref name="stops"/> names the breakpoints it may stop at.</summary>
    private static string Awaited(int phase, string? game, string? stops, TimeSpan limit, string? device = null)
    {
        string stop = stops is null ? "a stop" : $"a stop at {stops}";
        string within = $"up to {Durations.Said(limit)} in all";
        return phase switch
        {
            0 => $"Debugging: waiting for the program to start, then for {stop}, its end, its UI or its host's start ({within})",
            1 => $"Debugging: {game} runs; waiting for {stop} or its end ({within})",
            2 => $"Debugging: the program runs; waiting for its UI, {stop} or its end ({within})",
            4 => $"Debugging: the program runs; waiting for its start on {device}, {stop} or its end ({within})",
            _ => $"Debugging: the program runs; waiting for {stop}, its end or its host's start ({within})",
        };
    }

    /// <summary>The app's process on its device as its shell said it started there, in its Debug tab's console; null until it has.</summary>
    private static string? StartedOnDevice(ToolWindows.DebugTab tab)
    {
        tab.Console.Flush();
        return tab.Plan?.Device?.App?.StartedIn(tab.Console.Document.Snapshot);
    }

    /// <summary>The breakpoints that may stop a program launched under the debugger (a game's, those in its folder), as an
    /// agent reads them: a few by file and line, more by their count; null for none, or while Mute Breakpoints is on.</summary>
    private string? StopsWaitedFor(string? folder)
    {
        List<LineBreakpoint> set = Session.Debug.Breakpoints.Muted ? []
            : [.. Session.Debug.Breakpoints.All.Where(b => b is { Enabled: true, Suspend: true } && (folder is null || PathNames.IsUnder(b.Path, folder)))];
        return set.Count switch
        {
            0 => null,
            1 => $"its breakpoint at {Relative(set[0].Path)}:{set[0].Line + 1}",
            <= 3 => $"its breakpoints at {Plural.Listed([.. set.Select(b => $"{Relative(b.Path)}:{b.Line + 1}")])}",
            _ => $"its {Numbers.Said(set.Count)} breakpoints",
        };
    }

    /// <summary>A program's output heard as it comes, each line once it is whole, from its console or its terminal tab, for
    /// the words its host says as it starts serving (<see cref="ListeningLines"/>): the debugged program's, or Run's.</summary>
    private sealed class HeardServing(Func<object?> program, Func<global::Passenger.Terminal.TerminalSession?> terminal, Func<TextSnapshot> console)
    {
        /// <summary>How long more addresses are listened for after the first, where the host never says it started.</summary>
        private static readonly TimeSpan MoreAddresses = TimeSpan.FromSeconds(1);

        private object? heard;
        private ListeningLines lines = new();
        private int consoleNext;
        private long terminalNext;
        private long firstAddress;

        /// <summary>The program the debugger shows (<see cref="DebugController.Session"/>), from its Debug console or its tab.</summary>
        public static HeardServing Debugged(DebugController debugger) =>
            new(() => debugger.Session, () => debugger.Session?.Terminal?.Session, () => debugger.Output(debugger.Session));

        /// <summary>Run's program, from the Run console or its terminal configuration's tab.</summary>
        public static HeardServing Ran(ProjectSession session) =>
            new(() => session.RunProgram, () => (session.RunProgram as TerminalRun)?.Session, session.Runner.Shown);

        /// <summary>Whether the program serves now: its host said it started, or an address was heard a moment ago.</summary>
        public bool Serves()
        {
            Hear();
            return lines.HostStarted || lines.Addresses.Count > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(firstAddress) >= MoreAddresses;
        }

        /// <summary>Where the program serves, in an agent's words, once it has said; else null.</summary>
        public string? Said => lines.Addresses.Count > 0 ? $"listening at {string.Join(", ", lines.Addresses)}"
            : lines.HostStarted ? "its host started" : null;

        private void Hear()
        {
            if (program() is not { } now)
            {
                return;
            }
            if (now != heard)
            {
                heard = now;
                lines = new ListeningLines();
                consoleNext = 0;
                terminalNext = 0;
            }
            int had = lines.Addresses.Count;
            if (terminal() is { } inTerminal)
            {
                global::Passenger.Terminal.TerminalScreen screen = inTerminal.Screen;
                // The cursor's line may still be written; those above it are whole.
                long whole = screen.ScreenTop + screen.CursorRow;
                for (long line = Math.Max(terminalNext, screen.FirstLine); line < whole; line++)
                {
                    lines.Hear(screen.LineAt(line).Text());
                }
                terminalNext = Math.Max(terminalNext, whole);
            }
            else
            {
                TextSnapshot text = console();
                // The last line has no break yet; a console that dropped its oldest lines is read again from its first.
                int whole = text.LineCount - 1;
                consoleNext = whole < consoleNext ? 0 : consoleNext;
                for (int line = consoleNext; line < whole; line++)
                {
                    lines.Hear(text.GetLineText(line));
                }
                consoleNext = whole;
            }
            if (had == 0 && lines.Addresses.Count > 0)
            {
                firstAddress = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>The programs the debugger can attach to, each command line's secrets masked as Attach to Process shows them.</summary>
    /// <summary>debug_state's modules: the program's own first, then the rest, each in the order it loaded.</summary>
    private Task<object> DebugModules(string? session, CancellationToken cancel) => OnUi<object>(cancel, async () =>
    {
        DebugSession debugged = await Debugged(session);
        if (debugged.ModulesUnlisted is { } why)
        {
            throw new InvalidOperationException(why);
        }
        return Held([.. debugged.Modules.OrderBy(m => m.IsUserCode != true).Select(m => new ModuleListing(m.Name, m.Version, m.SymbolStatus ?? DapSymbolStatus.None,
            m.IsUserCode == true, m.IsOptimized == true, m.Path))], "module", "the program's own modules are listed first");
    });

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
            _ => throw new InvalidOperationException($"{Numbers.Said(named.Count)} running programs are named {program}: name one by its processId ({string.Join(", ", named.Select(p => p.Id))})"),
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
        "or locals behind it. of breakpoints: every breakpoint (line, method, data and property, exception with its scenarios, XAML " +
        "Binding Errors), each with whether the debugger took it; while Mute Breakpoints (ide_action " +
        "app.debug.muteBreakpoints) is on, none stops. of modules: the program's, each with its version, symbols and Just My " +
        "Code's verdict (userCode), as the Modules window lists them. of processes: the .NET programs here and in running containers the " +
        "debugger can attach to, program being the .dll the dotnet host runs (a container's id 0 until debug_attach puts " +
        "Passenger's tools in).")]
    public async Task<object> GetDebugState(string? session = null, string? of = null, int? reference = null, CancellationToken cancel = default) => (of, reference) switch
    {
        (null or "stop", int members) => await DebugVariables(members, session, cancel),
        (null or "stop", null) => await OnUi(cancel, () => DebugSnapshotAsync(session is null ? Unnamed() : TabFor(session)?.Session)),
        ("breakpoints", null) => await ListBreakpoints(cancel),
        ("processes", null) => await DebugProcesses(cancel),
        ("modules", null) => await DebugModules(session, cancel),
        (_, null) => throw new ModelContextProtocol.McpException($"No '{of}' to give; of takes stop (the default), breakpoints, modules or processes"),
        _ => throw new ModelContextProtocol.McpException("A reference's members are the stop's: give reference or of, not both"),
    };

    [McpServerTool(Name = McpTools.DebugContinue, Title = "Resume", Destructive = false), Description(
        "Resumes the program and waits for it to stop again or end; with path and line, runs to that line as Run to Cursor " +
        "does, stopping sooner at a breakpoint. A debugged test run that ends answers its tally and failures too (tests).")]
    public Task<DebugSnapshot> DebugContinue(int waitSeconds = 30, string? session = null, string? path = null, int? line = null, CancellationToken cancel = default) =>
        (path, line) switch
        {
            (null, null) => Resume(s => s.ContinueAsync(), waitSeconds, session, cancel),
            ({ } file, int to) => Resume(debugged => debugged.RunToCursorAsync(Named(file), to - 1), waitSeconds, session, cancel),
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
        // The move's own stop, whichever comes first, it or the answer: an earlier move's stop has the same reason.
        int before = paused.Stops;
        if (await Session.Debug.SetNextStatementAsync(paused, Named(path), line - 1) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
        await When(() => paused.Stops > before || paused.State != DebugState.Paused, TimeSpan.FromSeconds(10), cancel, StateOf(paused));
        await StopReadAsync(paused, cancel);
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
        // What the Debug console types into nothing is refused, saying where the program reads its input.
        ToolWindows.DebugTab tab = TabFor(session)!;
        if (tab.Companion is { IsLive: true } beside)
        {
            throw new InvalidOperationException($"{Refusals.InputBeside(tab.Name, beside.Name)} ({McpTools.DebugInput} with session \"{beside.Name}\")");
        }
        if (tab.InputFrom is { } from)
        {
            throw new InvalidOperationException(Refusals.InputFromStarter(tab.Name, from));
        }
        if (tab.Console.InputEnded)
        {
            throw new InvalidOperationException(Refusals.InputEnded(tab.Name));
        }
        await debugged.InputAsync(newline ? text + "\n" : text);
        return "Sent";
    });

    [McpServerTool(Name = McpTools.DebugEvaluate, Title = "Evaluate an expression", Destructive = true), Description(
        "Evaluates a C# expression, which may call the program's methods, in a frame of the stopped thread (by default the " +
        "stop's, whose locals debug_state lists), as the Debug window's evaluate box does; a result with members has a " +
        "reference debug_state opens. With value, a C# expression, assigns it to expression (a local, member or element) as " +
        "Set Value does, answering what it now is. With letThreadsRun, one that said it probably needs another thread runs " +
        "with every thread let go, as the row's Run with every thread does, what they meet meanwhile passed over. With mark, " +
        "marks the object its value is as Mark Object does ($1), which any expression names at every stop after.")]
    public Task<VariableInfo> DebugEvaluate(string expression, int? frame = null, string? session = null,
        string? value = null, bool letThreadsRun = false, bool mark = false, CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        DebugSession paused = await Paused(session, choose: value is not null);
        (DapStackFrame chosen, string? below) = frame is int at ? (await FrameAt(paused, at), null) : await StopFrameAsync(paused);
        if (mark)
        {
            if (paused.MarksWhyNot is { } why)
            {
                throw new InvalidOperationException(why);
            }
            string label;
            try
            {
                label = await paused.MarkObjectAsync(expression, chosen.Id);
            }
            catch (DapException refused)
            {
                throw new InvalidOperationException(refused.Reason);
            }
            await Session.Debug.RefreshAsync();
            EvaluateResponse marked = await paused.EvaluateAsync(label, chosen.Id, EvaluateContexts.Watch);
            return Variable(expression, marked.Result, marked.Type, marked.VariablesReference, label) with { Frame = below, Mark = label };
        }
        if (value is not null)
        {
            SetExpressionResponse set = await paused.SetExpressionAsync(expression, value, chosen.Id);
            await Session.Debug.RefreshAsync();
            return Variable(expression, set.Value, set.Type, set.VariablesReference, expression) with { Frame = below };
        }
        EvaluateResponse result = await paused.EvaluateAsync(expression, chosen.Id, EvaluateContexts.Repl, letThreadsRun);
        if (letThreadsRun)
        {
            // The window shows where the threads are now, as it does after its own.
            await Session.Debug.ThreadsRanAsync(paused, expression, result);
        }
        return Variable(expression, result.Result, result.Type, result.VariablesReference, expression) with { Frame = below, PassedOver = result.PassedOver, Mark = result.Mark };
    });

    private Task<IReadOnlyList<VariableInfo>> DebugVariables(int reference, string? session, CancellationToken cancel) => OnUi(cancel, async () =>
    {
        DebugSession paused = await Paused(session);
        return (IReadOnlyList<VariableInfo>)[.. (await Session.Debug.MembersAsync(paused, reference))
            .Select(v => Variable(v.Name, v.Value, v.Type, v.VariablesReference, v.EvaluateName is { } named && named != v.Name ? named : null) with { Mark = v.Mark })];
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
        ToolWindows.DebugTab? tab = TabFor(session);
        DebugSession? debugged = tab?.Session;
        string output = debugged?.Terminal is { } inTerminal ? Tail(inTerminal.Session, lines) : Tail(Session.Debug.Output(debugged), lines);
        return tab is not null ? $"{tab.Name}, debugged, {StateOf(tab)}:\n{output}"
            : output.Length > 0 ? output
            : "Nothing has been debugged yet, so the Debug console is empty: debug_start starts a configuration under the debugger";
    }

    // The IDE itself

    [McpServerTool(Name = McpTools.IdeAction, Title = "Run an IDE action", Destructive = true), Description(
        "Runs an IDE action by its id (app.build, app.debug.stepOver...; ide_actions lists them) as its shortcut would, waits " +
        "for the work it begins and says what it came to, or says why it cannot. input: app.migrations.add's name (then its output folder, else the context's last), updateTo's " +
        "target, environment's name; app.saveAsLaunchProfile's configuration (else the chosen one); app.targetFramework's framework, as " +
        "Project:net9.0 where several projects target several; app.chooseRunTarget's device, emulator or simulator by name " +
        "(Configuration:name for another configuration's), app.chooseBuildConfiguration's configuration (Release, or " +
        "Release|x64; none lists them for either).")]
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
        if (action == AppActions.TargetFramework && input is not null)
        {
            return window.ChooseFrameworkForAgent(input);
        }
        if (action == AppActions.ChooseRunTarget)
        {
            return await window.ChooseRunTargetForAgentAsync(input);
        }
        if (action == AppActions.ChooseBuildConfiguration)
        {
            return await window.ChooseBuildConfigurationForAgentAsync(input);
        }
        if (input is not null)
        {
            throw new InvalidOperationException($"{action} takes no input");
        }
        IdeAction? named = window.Actions[action];
        if (named is null && window.Actions[AppActions.Now(action)] is null)
        {
            throw new ArgumentException($"No action is called {action}: ide_actions lists them, a query narrowing it to those whose id, name or group holds it", nameof(action));
        }
        if (named is { Group: GitGroup })
        {
            // Read now, as the vcs tools read it: the agent's shell may have begun a rebase a moment ago.
            await Session.Vcs.Opened;
            await Session.Vcs.RefreshAsync();
        }
        await GrammarReadForAsync(action, cancel);
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

    /// <summary>Waits for the file in front's grammar where that alone stands in an action's way: a file opened or edited a moment
    /// before is read as the call waits, so an agent never has to ask again, and one whose grammar fails meanwhile is refused at
    /// once. Past the limit the action says what stands in its way.</summary>
    private async Task GrammarReadForAsync(string action, CancellationToken cancel)
    {
        ActionContext context = Beside ? window.Actions.Context().ForAgent() : window.Actions.Context();
        if (context.File is { Grammar: { } grammar } file && window.Actions.WhyNot(action, context) == Refusals.ReadingStrings(file.Name))
        {
            Wake failed = look =>
            {
                EventHandler<Exception> heard = (_, _) => UiThread.Post(look);
                grammar.Failed += heard;
                return () => grammar.Failed -= heard;
            };
            await When(() => grammar.HasRead(file.Text.Snapshot) || grammar.Failure is not null, GrammarLimit, cancel,
                On(h => grammar.Changed += h, h => grammar.Changed -= h), failed);
        }
    }

    /// <summary>The group of Git's actions, whose state ide_action reads fresh before asking whether one can run.</summary>
    private const string GitGroup = "Git";

    [McpServerTool(Name = McpTools.IdeActions, Title = "The IDE's actions", ReadOnly = true), Description(
        "Every action ide_action runs (those with no shortcut too), as id: name [group] (shortcut on this platform), " +
        "(on) or (off) for one that turns something on and off, and why one cannot run here now.")]
    public Task<CallToolResult> IdeActions(
        [Description("Only those whose id, name or group holds it (\"rebase\", \"Git\")")] string? query = null,
        CancellationToken cancel = default) => OnUi(cancel, () =>
    {
        ActionRegistry actions = window.Actions;
        ActionContext context = Beside ? actions.Context().ForAgent() : actions.Context();
        IdeAction[] asked = query is not { Length: > 0 } words ? [.. actions.All]
            : [.. actions.All.Where(a => a.Id.Contains(words, StringComparison.OrdinalIgnoreCase) || a.Title.Contains(words, StringComparison.OrdinalIgnoreCase)
                || a.Group.Contains(words, StringComparison.OrdinalIgnoreCase))];
        string[] lines = [.. asked.Select(a =>
            $"{a.Id}: {a.Title} [{a.Group}]{(actions.Shortcut(a.Id) is { } keys ? $" ({keys})" : string.Empty)}"
            + $"{(a.IsChecked?.Invoke(context) is { } on ? on ? " (on)" : " (off)" : string.Empty)}{(a.WhyNot(context) is { } why ? $" - cannot run: {why}" : string.Empty)}")];
        // Cut short, it names the groups of those it left out, which a query of each lists.
        return Task.FromResult(Held(lines, "action", "query, a group's name or a word of the action's, narrows them", readOn: last =>
        {
            string[] groups = [.. asked.Skip(Array.IndexOf(lines, last) + 1).Select(a => $"\"{a.Group}\"").Distinct(StringComparer.Ordinal)];
            return $"they are of the {Plural.Noun(groups.Length, "group")} {Plural.Listed(groups)}";
        }));
    });

    // The work behind them

    /// <summary>Whether the agent is attached to a person's window, beside the person, rather than in an IDE of its own.</summary>
    private bool Beside => connection is { Attached: true };

    /// <summary>How long a call waits for the look its coming asked for (<see cref="LookedAgainAsync"/>): one queued behind a
    /// walk of a whole folder lets the call go on, what it finds coming as heard.</summary>
    private static TimeSpan LookLimit => Waits.Of(TimeSpan.FromSeconds(5));

    /// <summary>An agent's call coming to the window: the window looks again for what the system dropped, as it does coming
    /// in front, when the agent's calls had stopped a while (<see cref="MainWindow.AgentCalling"/>), and the call waits until
    /// what it found reached the window's thread, so the call's own work, queued after, reads it.</summary>
    internal async Task LookedAgainAsync(CancellationToken cancel)
    {
        Task look = await UiThread.InvokeAsync(() => window.AgentCalling());
        // A wait with no fault to leave behind: a look ends however it went.
        await Task.WhenAny(look, Task.Delay(LookLimit, cancel));
        cancel.ThrowIfCancellationRequested();
    }

    /// <summary>A tool's work on the UI thread, its waits ended by <paramref name="cancel"/> (the agent gave up on the call,
    /// or went), once the folder's projects are read and evaluated as written (<see cref="ProjectSession.ProjectsAsWrittenAsync"/>),
    /// which every tool that runs, builds or tests a project asks about (unless <paramref name="projects"/> is false). What it
    /// refuses (nothing to debug, no such file) reaches the agent in its own words: the SDK passes only an McpException's
    /// message on, and hides every other's.</summary>
    private async Task<T> OnUi<T>(CancellationToken cancel, Func<Task<T>> work, bool projects = true)
    {
        cancel.ThrowIfCancellationRequested();
        try
        {
            return await UiThread.InvokeAsync(async () =>
            {
                // Ended while it waited its turn (its agent shut out as the window left the folder): it does nothing in the next.
                cancel.ThrowIfCancellationRequested();
                // A tool run inside another's call reads its files into that call's, let go as that one ends.
                CallFiles? own = Files.Value is null ? new CallFiles(Editors, Beside, kept) : null;
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
                    if (projects && (!Session.WorkspaceRead.IsCompleted || !Session.Models.Idle.IsCompleted))
                    {
                        AgentCall.Say($"Waiting for {Session.Folder ?? "the folder"}'s projects to be read");
                        await Session.WorkspaceRead.WaitAsync(cancel);
                        await Session.Models.Idle.WaitAsync(cancel);
                        AgentCall.Say(null);
                    }
                    if (projects)
                    {
                        // A project file the agent edited a moment ago, through edit or its own shell, the folder's watcher may not have heard of yet.
                        await Session.ProjectsAsWrittenAsync(AgentCall.Say, cancel);
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
            AgentCall.Say($"The program runs: waiting up to {Durations.Said(limit)} for it to stop or end");
        }
        DebugSession? going = session ?? Session.Debug.Session;
        // A Godot game's other language stops for it too: its GDScript beside its C#, or the C# its GDScript is beside.
        List<DebugSession> beside = [.. Companions(going)];
        await When(() => going is not { State: DebugState.Running or DebugState.Starting } || beside.Any(b => b.State == DebugState.Paused), limit, cancel,
            [StateOf(going), .. beside.Select(StateOf)]);
        DebugSession? stopped = going is { State: DebugState.Running or DebugState.Starting } && beside.FirstOrDefault(b => b.State == DebugState.Paused) is { } other ? other : going;
        await StopReadAsync(stopped, cancel);
        return await DebugSnapshotAsync(stopped);
    }

    /// <summary>The live sessions debugged beside a session as one program: a Godot game's GDScript tab and its C# tab, each
    /// the other's companion.</summary>
    private IEnumerable<DebugSession> Companions(DebugSession? session)
    {
        if (session is null || Session.Debug.Tabs.FirstOrDefault(t => t.Session == session) is not { } tab)
        {
            return [];
        }
        return Session.Debug.Tabs.Where(t => t != tab && t.IsLive && t.Session is not null && (t.Companion == tab || tab.Companion == t)).Select(t => t.Session!);
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
        // A Godot game's GDScript session, running, is its C#'s companion, which acts for both.
        List<ToolWindows.DebugTab> programs = [.. live.Where(t => t.Companion is not { IsLive: true })];
        return paused is [var stopped] ? stopped
            : programs is [var only] ? only
            : live.Count == 0 ? debugger.Chosen
            : throw new InvalidOperationException($"{Numbers.Said(live.Count)} programs are debugged ({Listed()}), {(paused.Count == 0 ? "none" : Numbers.Said(paused.Count))} paused: name the one meant with session");
    }

    /// <summary>The programs debugged and each one's state, as debug_state lists them.</summary>
    private string Listed() => string.Join(", ", Sessions());

    private IReadOnlyList<string> Sessions() => [.. Session.Debug.Tabs.Select(t => $"{t.Name}: {StateOf(t)}")];

    /// <summary>A debugged program's state as an agent reads it: paused, running, starting, or ended.</summary>
    private static string StateOf(ToolWindows.DebugTab tab) => tab.IsLive && tab.Session is { } live ? live.State.ToString().ToLowerInvariant() : "ended";

    /// <summary>Where the debugger is in a session's program (the chosen one's by default), from its own model: the session's
    /// state in its words, and while paused the stop with its shown frame's locals; with several programs debugged, which one
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
        // A program that said it needs a terminal, as the Debug window offers one.
        if ((session is null ? debugger.Chosen : debugger.TabOf(session)) is { } tab && Builds.DebugNeedOf(tab) is { } need)
        {
            summary += $". {need.Said}: {McpTools.DebugStart} with terminal true debugs it in a Terminal tab";
        }
        if (session is null || session.State == DebugState.Ended)
        {
            return new DebugSnapshot(null, null, null, [], [], session?.ExitCode, summary);
        }
        if (session.State != DebugState.Paused || session.StoppedThread is null)
        {
            return new DebugSnapshot(session.State, null, null, [], [], null, summary) { Unbound = Unbound() };
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
                locals.AddRange((await session.VariablesAsync(scope.VariablesReference)).Select(v => Variable(v.Name, v.Value, v.Type, v.VariablesReference, v.EvaluateName ?? v.Name) with { Mark = v.Mark }));
            }
        }
        string? exception = null;
        if (session.StopReason == StopReasons.Exception)
        {
            try
            {
                ExceptionInfoResponse info = await session.ExceptionInfoAsync(session.StoppedThread.Value);
                exception = Masked(Describe(info.Details) ?? $"{info.ExceptionId}: {info.Description}");
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
            Elapsed = session.Ran is { } ran ? DebugController.Elapsed(ran) : null,
            BindingValues = shown ? await BindingValuesShownAsync() : null,
            Unbound = Unbound(),
        };
    }

    /// <summary>The breakpoints the programs debugged will not stop at, path:line: why, as Problems' Breakpoints lists them;
    /// null with none.</summary>
    private IReadOnlyList<string>? Unbound() =>
        Session.Debug.BreakpointProblems() is { Count: > 0 } problems ? [.. problems.Select(p => $"{Relative(p.Path!)}:{p.Position.Line + 1}: {p.Message}")] : null;

    /// <summary>A stop's frames as an agent reads them, each by its index (which debug_evaluate's frame takes): the folder's
    /// own code by its place, and a run of any other (a framework's, with or without source: an Avalonia click is some 25
    /// of them under the handler, and Passenger's inspector's) as one line of their indexes and modules, at most 20 lines.</summary>
    private IReadOnlyList<string> Frames(IReadOnlyList<DapStackFrame> frames, DebugSession session)
    {
        string? folder = Session.Folder;
        bool Own(DapStackFrame frame) => frame.Source?.Path is { } path && (folder is null || PathNames.IsUnder(path, folder));
        List<string> lines = [];
        for (int i = 0; i < frames.Count && lines.Count < 20; i++)
        {
            if (Own(frames[i]))
            {
                lines.Add(FrameLine(i, frames[i], session));
                continue;
            }
            int last = i;
            while (last + 1 < frames.Count && !Own(frames[last + 1]))
            {
                last++;
            }
            if (last == i)
            {
                lines.Add(FrameLine(i, frames[i], session));
                continue;
            }
            string[] modules = [.. frames.Skip(i).Take(last - i + 1).Select(f => f.Name.IndexOf('!', StringComparison.Ordinal) is > 0 and int bang ? f.Name[..bang] : null)
                .OfType<string>().Distinct()];
            lines.Add($"{i}-{last}: {Numbers.Said(last - i + 1)} frames outside the folder's code{(modules.Length > 0 ? $" ({string.Join(", ", modules.Take(4))}{(modules.Length > 4 ? ", …" : "")})" : "")}");
            i = last;
        }
        return lines;
    }

    /// <summary>One frame as <see cref="Frames"/> lists it: its index, name and place, the folder's own code relative to it,
    /// at the editor's line of its file now (the debugger's is its build's compiler's).</summary>
    private string FrameLine(int index, DapStackFrame frame, DebugSession session) =>
        $"{index}: {frame.Name}{(frame.Source?.Path is { } path ? $" at {Relative(path)}:{(Session.Debug.PlaceOf(frame, session)?.Line ?? frame.Line - 1) + 1}" : "")}";

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
                _ => $"There is no frame {frame}: the stopped thread has {Numbers.Said(frames.Count)} frames, 0 (the top) to {frames.Count - 1}, as debug_state numbers them",
            });
    }

    /// <summary>The frame a stop shows, whose locals debug_state lists, and where it is not the top, its line as
    /// <see cref="Frames"/> lists it.</summary>
    private async Task<(DapStackFrame Frame, string? Below)> StopFrameAsync(DebugSession session)
    {
        (IReadOnlyList<DapStackFrame> frames, DapStackFrame? shown) = await StopFramesAsync(session);
        if (shown is null)
        {
            return (await FrameAt(session, 0), null);
        }
        int index = frames.TakeWhile(f => f != shown).Count();
        return (shown, index > 0 ? FrameLine(index, shown, session) : null);
    }

    /// <summary>The program a tool acts on (<see cref="TabFor"/>), being debugged; with <paramref name="choose"/> its tab is
    /// chosen in the Debug window, as a person picks the one their keys act on.</summary>
    private async Task<DebugSession> Debugged(string? session, bool choose = false)
    {
        ToolWindows.DebugTab? tab = TabFor(session);
        if (tab?.Session is not { State: not DebugState.Ended } debugged || !tab.IsLive)
        {
            throw new InvalidOperationException(session is null || tab is null ? $"{Refusals.NothingDebugged}; debug_start starts it" : $"{tab.Name} is no longer debugged; debug_state lists those that are");
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

    /// <summary>A launching call's configuration chosen as a person's Run chooses it, the choice put back as it stood once
    /// the call ends unless its launch began (<see cref="Choosing.Began"/>) or another choice was made meanwhile: a call
    /// refused leaves the window's choice as it was.</summary>
    private Choosing Choose(string? configuration)
    {
        RunConfigurations configurations = Session.Configurations;
        RunChoice held = configurations.Held;
        if (configuration is not null && !configurations.Select(configuration))
        {
            throw new InvalidOperationException($"No run configuration '{configuration}'; there are: {string.Join(", ", configurations.All.Select(c => c.Name))}");
        }
        return new Choosing(this, configurations, held, configurations.Held);
    }

    /// <summary>A launching call's choice of its configuration (<see cref="Choose"/>), put back as it was when disposed
    /// before the launch began.</summary>
    private sealed class Choosing(PassengerTools tools, RunConfigurations configurations, RunChoice held, RunChoice made) : IDisposable
    {
        public bool Began { get; set; }

        public void Dispose()
        {
            if (!Began && tools.Session.Configurations == configurations && configurations.Held == made)
            {
                configurations.PutBack(held);
            }
        }
    }

    /// <summary>The line breakpoints of a file (or all), each in the words the gutter's hover says it (<see cref="BreakpointStates"/>).</summary>
    /// <summary>The line breakpoints of a file, or every file's, as an agent reads them; with the file's parse, one set on a line
    /// no code runs on says where it moves.</summary>
    private IReadOnlyList<BreakpointInfo> Breakpoints(string? path, Passenger.Languages.CSharp.ParsedText? parsed = null)
    {
        DebugController debugger = Session.Debug;
        return [.. (path is null ? debugger.Breakpoints.All : debugger.Breakpoints.In(path))
            .Select(b => new BreakpointInfo(Relative(b.Path), b.Line + 1, b.Enabled, BreakpointStates.StateOf(b, debugger.IsDebugging, debugger.Breakpoints.Muted), b.Condition,
                b.HitCondition, b.LogMessage, b.Suspend, b.Status == BreakpointStatus.Bound ? null
                    : b.Message ?? BreakpointStates.Moves(b, debugger.IsDebugging, debugger.Breakpoints.Muted, BreakpointStates.UnrunAt(parsed, b)),
                Attribute: b.Attribute, Column: b.Column + 1, Temporary: b.Temporary ? true : null, WaitsFor: WaitPlace(b.WaitsFor)))];
    }

    private IReadOnlyList<BreakpointInfo> DataBreakpoints() =>
        [.. Session.Debug.Breakpoints.Data.Select(b => new BreakpointInfo(null, null, b.Enabled, b.Status.ToString().ToLowerInvariant(), b.Condition, b.HitCondition,
            b.LogMessage, b.Suspend, b.Message, Data: b.Location)),
         .. Session.Debug.Breakpoints.Properties.Select(b => new BreakpointInfo(null, null, b.Enabled, b.Status.ToString().ToLowerInvariant(), b.Condition, b.HitCondition,
            b.LogMessage, b.Suspend, b.Message, Property: b.Location, Control: Id(b.Element)))];

    private IReadOnlyList<BreakpointInfo> ExceptionBreakpoints()
    {
        BindingErrorBreakpoint bindings = Session.Debug.Breakpoints.BindingErrors;
        ScriptErrorBreakpoint scripts = Session.Debug.Breakpoints.ScriptErrors;
        // One whose scenarios leave it never stopping is inactive, whatever a debugger said of it, in Suspend If's words.
        return [.. Session.Debug.Breakpoints.Exceptions.Select(b => new BreakpointInfo(null, null, b.Enabled, b.Inactive is null ? b.Status.ToString().ToLowerInvariant() : "inactive",
                b.Condition, b.HitCondition, b.LogMessage, b.Suspend, b.Inactive ?? b.Message, b.Location, ExceptionScenarioNames.Of(b.Scenarios))),
            new BreakpointInfo(null, null, bindings.Enabled, bindings.Status.ToString().ToLowerInvariant(), bindings.Condition, bindings.HitCondition, bindings.LogMessage,
                bindings.Suspend, bindings.Message, BindingErrors: true),
            new BreakpointInfo(null, null, scripts.Enabled, scripts.Status.ToString().ToLowerInvariant(), scripts.Condition, scripts.HitCondition, scripts.LogMessage,
                scripts.Suspend, scripts.Message, ScriptErrors: true)];
    }

    /// <summary>A line breakpoint's file and line from zero, as an agent names them.</summary>
    private (string Path, int Line) LinePlace(string? path, int? line) => path is { Length: > 0 } && line is int at
        ? (Named(path), at - 1)
        : throw new InvalidOperationException("Name a path and a line for a line breakpoint, or an exception type for an exception breakpoint");

    /// <summary>A C# file no tab shows, whose marks the server's refresh or its opening left unpulled, pulled now, so the
    /// actions asked with them stand on the text as it is; a pull a later one overtook leaves the later one's.</summary>
    private static async Task MarkedNowAsync(CSharpDocument? csharp)
    {
        if (csharp is { Stale: true })
        {
            try
            {
                await csharp.PullNowAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>The server's actions at a place or over a selection, asked with the diagnostics on the place's line.</summary>
    private async Task<IReadOnlyList<CodeAction>> Actions(ServerServed served, int line, int column, int? endLine, int? endColumn)
    {
        OpenDocument document = served.Document;
        int offset = Offset(document, line, column);
        int end = endLine is { } last ? Offset(document, last, endColumn ?? 1) : offset;
        await MarkedNowAsync(served.Web is null ? Session.CSharp?.For(document) : null);
        IReadOnlyList<Diagnostic> marked = served.Web?.Diagnostics ?? Session.CSharp?.For(document)?.Diagnostics ?? [];
        int at = served.Lsp.PositionOf(offset).Line;
        IReadOnlyList<Diagnostic> here = [.. marked.Where(d => d.Range.Start.Line <= at && at <= d.Range.End.Line)];
        IReadOnlyList<CodeAction> actions = await served.Lsp.CodeActionsAsync(TextRange.FromBounds(Math.Min(offset, end), Math.Max(offset, end)), here);
        return [.. actions.Where(a => a.Disabled is null)];
    }

    /// <summary>The language server, once it has loaded the workspace and taken in what was written on disk since it was
    /// last asked (<see cref="WrittenTakenInAsync"/>); refused, saying why, while the folder's projects have no SDK to load
    /// with (<see cref="Unloadable"/>) or are still loading after the wait.</summary>
    private async Task<RoslynSession> Server(CancellationToken cancel)
    {
        await LookedAgainForCodeAsync(cancel);
        if (Unloadable() is { } unloadable)
        {
            throw new InvalidOperationException(unloadable);
        }
        if (Session.CSharp is null)
        {
            throw new InvalidOperationException(Session.WhyNoCSharp);
        }
        // The session's server, which starts again when its projects change: each look is at the one there is then.
        if (Session.CSharp is { IsReady: false } && ServerDown() is null)
        {
            AgentCall.Say("Waiting for the C# language server to load the solution");
            await When(() => Session.CSharp is not { IsReady: false } || ServerDown() is not null || Unloadable() is not null, ServerLimit, cancel, ServerState(), SdkState());
            AgentCall.Say(null);
        }
        if (Unloadable() is null && Session.CSharp is { IsReady: true })
        {
            await WrittenTakenInAsync(cancel);
        }
        return Unloadable() is { } found ? throw new InvalidOperationException(found)
            : Session.CSharp is { IsReady: true, Session: { } server } ? server
            : ServerDown() is { } down ? throw new InvalidOperationException(down)
            : throw new TimeoutException(Refusals.StillLoading(ServerLimit, Session.CSharp?.State ?? "none"));
    }

    /// <summary>Why the C# language server will answer nothing however long a question waits, in Refusals' words: left
    /// stopped, or unable to start; null while it runs or starts.</summary>
    private string? ServerDown() => Session.CSharp switch
    {
        { IsStopped: true } => Refusals.CSharpStopped,
        { Unavailable: { } why } => Refusals.CSharpUnavailable(why),
        _ => null,
    };

    /// <summary>The folder's projects found to have no .NET SDK, or one again: what a wait for the server's load wakes on too.</summary>
    private Wake SdkState()
    {
        ProjectModels models = Session.Models;
        return On(h => models.SdkChanged += h, h => models.SdkChanged -= h);
    }

    /// <summary>The projects the C# language server met an error loading, each with the first it met, by name: what it may
    /// hold none of the code of.</summary>
    private IReadOnlyList<(string Project, string Error)> FailedLoads() =>
        [.. (Session.CSharp?.ProjectProblems ?? []).Where(p => p.Severity == ProblemSeverity.Error).GroupBy(p => p.Project, PathNames.Comparer)
            .Select(g => (Path.GetFileNameWithoutExtension(g.Key), g.First().Message))];

    /// <summary>Why the server holds a C# file of one of the folder's projects in none: the project met an error loading,
    /// said with it; null where it holds the file, or the project met none.</summary>
    private async Task<string?> NotLoadedAsync(string path, CancellationToken cancel)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || Session.CSharp is not { Session: { } server } csharp || Session.ProjectOf(path) is not { } project
            || csharp.ProjectProblems.FirstOrDefault(p => p.Severity == ProblemSeverity.Error && PathNames.Same(p.Project, project.Path)) is not { } error)
        {
            return null;
        }
        try
        {
            return (await server.HeldTextsAsync([path], cancel)).Any(h => h.Hash is not null) ? null : Refusals.ProjectNotLoaded(project.Name, error.Message);
        }
        catch (LanguageServerFailed)
        {
            return null;
        }
    }

    /// <summary>Waits, once the language server has loaded the workspace, until it holds in their projects the C# files come
    /// under the folder's projects since (an agent's just written first) that the file asked about sees (<see
    /// cref="CSharpService.SeenFrom"/>), up to <see cref="Patience.Limit"/>, as it answers without a file in none: refused,
    /// naming the first, when the time is up, and at once while the projects have no SDK (<see cref="Unloadable"/>); whether
    /// it was waited for (false while the server still loads the workspace).</summary>
    private async Task<bool> TakenInAsync(string path, CancellationToken cancel)
    {
        await LookedAgainForCodeAsync(cancel);
        if (Session.ProjectOf(Resolve(path)) is not null && Unloadable() is { } unloadable)
        {
            throw new InvalidOperationException(unloadable);
        }
        if (Session.CSharp is not { IsReady: true } csharp)
        {
            return false;
        }
        IReadOnlyList<(string Path, string Project)> left = await csharp.UnplacedAsync(Patience.Limit, looking: true, csharp.SeenFrom(Resolve(path)), SayTakingIn, cancel);
        return left.Count == 0 ? true : throw new TimeoutException(NotTakenIn(left));
    }

    /// <summary>Waits, as <see cref="TakenInAsync"/> does for a file, until the server holds in their projects every C# file
    /// come under the folder's projects since, for a question of the solution's whole code: those still in none when the
    /// time is up, and the projects not loaded again, which its answer goes without, saying so (<see cref="WentWithout"/>),
    /// rather than refuse whole; refused at once while the projects have no SDK (<see cref="Unloadable"/>); null while the
    /// server still loads the workspace.</summary>
    private async Task<IReadOnlyList<(string Path, string Project)>?> UntakenAsync(CancellationToken cancel)
    {
        await LookedAgainForCodeAsync(cancel);
        if (Unloadable() is { } unloadable)
        {
            throw new InvalidOperationException(unloadable);
        }
        return Session.CSharp is { IsReady: true } csharp ? await csharp.UnplacedAsync(Patience.Limit, looking: true, null, SayTakingIn, cancel) : null;
    }

    /// <summary>What an answer of the solution's whole code went without (<see cref="UntakenAsync"/>): the files the server
    /// holds in no project yet, and the projects it answered from as they were; null for none.</summary>
    private string? WentWithout(IReadOnlyList<(string Path, string Project)> untaken) => untaken.Count == 0 ? null
        : Refusals.WentWithout([.. untaken.Where(u => !CSharpService.IsReload(u)).Select(u => Relative(u.Path))],
            [.. untaken.Where(CSharpService.IsReload).Select(u => Path.GetFileNameWithoutExtension(u.Project))]);

    /// <summary>The agent's progress while a C# file is waited for (null once it is not).</summary>
    private void SayTakingIn(string? file) => AgentCall.Say(file is null ? null
        : global::Passenger.Workspace.WorkspaceModel.IsProjectFile(file) ? $"Waiting for the C# language server to load {Relative(file)} again"
        : $"Waiting for the C# language server to load {Relative(file)} into its project");

    private string NotTakenIn(IReadOnlyList<(string Path, string Project)> left) => CSharpService.IsReload(left[0])
        ? Refusals.NotLoadedAgain(Path.GetFileNameWithoutExtension(left[0].Project))
        : Refusals.NotTakenIn(Relative(left[0].Path), Path.GetFileNameWithoutExtension(left[0].Project));

    /// <summary>A file opened as the editor would, with the language server's document for it once the server has loaded
    /// the workspace and holds the file in its project (<see cref="TakenInAsync"/>, waited for before the file is opened,
    /// so one just written is opened in its project; <paramref name="takenIn"/> when the caller has).</summary>
    private async Task<(OpenDocument Document, LspDocument Lsp)> Code(string path, CancellationToken cancel, bool takenIn = false)
    {
        NotGenerated(path);
        bool ofProject = Session.ProjectOf(Resolve(path)) is not null;
        if (ofProject && Unloadable() is { } unloadable)
        {
            throw new InvalidOperationException(unloadable);
        }
        takenIn = takenIn || await TakenInAsync(path, cancel);
        OpenDocument document = await DocumentAsync(path, cancel);
        if (Session.CSharp is null)
        {
            throw new InvalidOperationException(Session.WhyNoCSharp);
        }
        // The server is never given another language's file, so waiting for it to load one would wait out the limit.
        if (document.Language.Service != Languages.LanguageService.CSharp)
        {
            throw new InvalidOperationException($"This is the C# language server's, which answers for C# and Razor files, not {LanguageOf(document)} ones such as {Path.GetFileName(document.Path)}");
        }
        if (document.Syntax is { } syntax && (await syntax.CurrentAsync()).DeepAt is not null)
        {
            throw new InvalidOperationException(Refusals.TooDeep(Path.GetFileName(document.Path)));
        }
        await ServerSettledAsync(document.Path, csharp => csharp.For(document)?.Lsp is not null, cancel);
        if (!takenIn)
        {
            await TakenInAsync(path, cancel);
        }
        // Found meanwhile to have no SDK, the folder's projects are in no project the server holds either.
        if ((ofProject && Unloadable() is { } found ? found : await NotLoadedAsync(document.Path, cancel)) is { } notLoaded)
        {
            throw new InvalidOperationException(notLoaded);
        }
        return (document, Session.CSharp?.For(document)?.Lsp
            ?? throw new TimeoutException(Session.CSharp is { IsReady: false } loading ? Refusals.StillLoading(ServerLimit, loading.State)
                : $"The language server has not loaded {Path.GetFileName(document.Path)} ({Session.CSharp?.State ?? "none"})"));
    }

    /// <summary>Waits until the C# language server answers for a file of the folder's, or with none the whole solution, as
    /// its projects stand, and <paramref name="held"/> holds: answers before the solution has loaded, or before what a restore
    /// as it loaded brought (the projects' load after it, their generators' run, the files its builds wrote read again), are
    /// of the code before, so the agent is told what it waits for. Refused once the server has stopped, when the time is up
    /// with any of it still to come, and when the generators' wait ran out meanwhile with what it waited for unread.</summary>
    private async Task ServerSettledAsync(string? path, Func<CSharpService, bool> held, CancellationToken cancel)
    {
        await LookedAgainForCodeAsync(cancel);
        string? reloading = null;
        bool waited = false;
        bool settled = false;
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        // Looked at every 50 ms.
        await Until(Settled, ServerLimit, cancel);
        if (settled)
        {
            // The load's end is heard on the server's thread, what it said before it on this one only in its turn: heard, then looked at again.
            await PostedHeardAsync().WaitAsync(cancel);
            await Until(Settled, ServerLimit - System.Diagnostics.Stopwatch.GetElapsedTime(began), cancel);
        }
        if (reloading is not null)
        {
            AgentCall.Say(null);
        }
        if (ServerDown() is { } down)
        {
            throw new InvalidOperationException(down);
        }
        if (waited && Session.CSharp?.RanOutReading is { } ranOut && ranOut.At > began)
        {
            throw new TimeoutException(Refusals.StillComing(System.Diagnostics.Stopwatch.GetElapsedTime(began, ranOut.At), ranOut.What));
        }
        if (!settled && Session.CSharp is { IsReady: false } unready)
        {
            throw new TimeoutException(Refusals.StillLoading(ServerLimit, unready.State));
        }
        if (!settled && (reloading ?? (Session.CSharp is { } restoring && RestoreAwaited(restoring) is { } project ? $"the C# language server to restore {project}" : null)) is { } coming)
        {
            throw new TimeoutException(Refusals.StillComing(ServerLimit, coming));
        }
        await WrittenTakenInAsync(cancel);

        bool Settled()
        {
            if (Session.CSharp is not { } csharp || ServerDown() is not null)
            {
                return settled = true;
            }
            if (csharp.IsReady && csharp.ReloadingWhat(path) is var what && what != reloading)
            {
                reloading = what;
                waited |= what is not null;
                AgentCall.Say(what is null ? null : $"Waiting for {what}");
            }
            return settled = csharp.IsReady && reloading is null && RestoreAwaited(csharp) is null && held(csharp);
        }

        // The project whose restore the answer waits for, by name: the file's, or with none, any the server loaded.
        string? RestoreAwaited(CSharpService csharp) => path is null ? csharp.RestoreAwaited()
            : csharp.AwaitsRestore(path) ? Session.ProjectOf(path)?.Name ?? string.Empty : null;
    }

    /// <summary>Done once the window's thread has run what was posted to it before now at <see cref="UiThread.Post(Action,
    /// string, int)"/>'s priority, below a wait's own turns: the C# language server's words among it.</summary>
    private static Task PostedHeardAsync()
    {
        TaskCompletionSource heard = new(TaskCreationOptions.RunContinuationsAsynchronously);
        UiThread.Post(() => heard.TrySetResult());
        return heard.Task;
    }

    /// <summary>The language server once it answers for the whole solution as its projects stand (<see cref="Server"/>, then
    /// <see cref="ServerSettledAsync"/> with no file), for a question of the solution's whole code: what it says of the
    /// projects' load, their symbols, routes and findings. Refused, saying why, as each of those refuses.</summary>
    private async Task<RoslynSession> SettledServer(CancellationToken cancel)
    {
        await Server(cancel);
        await ServerSettledAsync(null, _ => true, cancel);
        return await Server(cancel);
    }

    /// <summary>Waits, up to <see cref="LookLimit"/>, for the watchers to look again once in this call for what another program
    /// made or deleted that the system never told of (<see cref="MainWindow.AgentAsksOfCode"/>), so a question of the code
    /// waits for what they find.</summary>
    private async Task LookedAgainForCodeAsync(CancellationToken cancel)
    {
        // A wait with no fault to leave behind: a look ends however it went.
        await Task.WhenAny(window.AgentAsksOfCode(), Task.Delay(LookLimit, cancel));
        cancel.ThrowIfCancellationRequested();
    }

    /// <summary>Waits for the C# language server to take in what another program wrote on disk that it may hold as it was,
    /// the folder's watcher having yet to tell it (<see cref="CSharpService.TakeInWrittenAsync"/>): refused once the time is
    /// up, as the answer would be of the code before it.</summary>
    private async Task WrittenTakenInAsync(CancellationToken cancel)
    {
        if (Session.CSharp is { } csharp && await csharp.TakeInWrittenAsync(Patience.Limit, SayReading, cancel) is { } unread)
        {
            throw new TimeoutException(Refusals.StillComing(Patience.Limit, $"the C# language server to read {Relative(unread)} as it is on disk"));
        }
    }

    /// <summary>The agent's progress while a file written on disk is waited for (null once it is not).</summary>
    private void SayReading(string? file) => AgentCall.Say(file is null ? null : $"Waiting for the C# language server to read {Relative(file)} as it is on disk");

    /// <summary><see cref="Code"/>, or a source generator's code copied, as the document the server has now and only asked
    /// about (<see cref="GeneratedCode.AskableAsync"/>).</summary>
    private async Task<(OpenDocument Document, LspDocument Lsp)> CodeOrCopy(string path, CancellationToken cancel)
    {
        if (!GeneratedCode.IsCopy(Resolve(path)))
        {
            return await Code(path, cancel);
        }
        OpenDocument copy = await DocumentAsync(path, cancel);
        await When(() => Session.CSharp is not { IsReady: false } || ServerDown() is not null, ServerLimit, cancel, ServerState());
        if (ServerDown() is { } down)
        {
            throw new InvalidOperationException(down);
        }
        (LspDocument? lsp, string? why) = Session.CSharp is { } service ? await service.Generated.AskableAsync(copy, cancel) : (null, Session.WhyNoCSharp);
        return lsp is null ? throw new InvalidOperationException(why) : (copy, lsp);
    }

    /// <summary>Refuses at once, in the window's words, a source generator's code copied to read (<see cref="GeneratedCode"/>),
    /// which the C# language server never holds, so waiting for its document would wait out the limit.</summary>
    private void NotGenerated(string path)
    {
        string full = Resolve(path);
        if (GeneratedCode.IsCopy(full))
        {
            throw new InvalidOperationException(Refusals.Generated(Path.GetFileName(full), Session.CSharp?.Generated.OriginOf(full)));
        }
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
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(Directory.Exists(full)
                ? $"{Relative(full)} is a folder, not a file: workspace and find_in_files read a folder"
                : $"No file is at {Relative(full)}: a new file is written on disk, then edited; find_in_files finds where a text is", full);
        }
        try
        {
            return call.Lend(full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new FileNotFoundException($"{Relative(full)} could not be opened: {error.Message}", error);
        }
    }

    /// <summary>The call this flow serves' files (<see cref="OnUi"/>'s), the dispatcher flowing it as it flows <see
    /// cref="AgentCall"/>.</summary>
    private static readonly AsyncLocal<CallFiles?> Files = new();

    /// <summary>The files one call read or changed, held open while it runs so no other call's end closes them under it, and
    /// kept a while after it (<see cref="KeptFiles"/>): one with no tab closes then, unless the call left it unsaved, or
    /// changed it in a person's window (<paramref name="keepChanged"/>), where its Undo is the person's, which keeps it in a
    /// tab. So an agent's session never holds open every file it read, nor a person's window a tab they never opened.</summary>
    private sealed class CallFiles(Passenger.App.Editors.EditorArea editors, bool keepChanged, KeptFiles kept)
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
            List<(OpenDocument, IDisposable)> holds = [];
            foreach ((OpenDocument document, (IDisposable hold, int version)) in held)
            {
                bool shown = document.IsModified || (keepChanged && document.Text.Version != version);
                if (shown && editors.Documents.Contains(document) && !editors.HasTab(document))
                {
                    editors.Activate(document);
                }
                holds.Add((document, hold));
            }
            held.Clear();
            kept.Take(holds);
        }
    }

    /// <summary>How many files of an agent's calls stay open after them, so its next ask of one finds what its servers
    /// worked out as it opened (the C# server's diagnostics, a linter's findings, a compose file's names).</summary>
    public const int FilesKept = 4;

    private readonly KeptFiles kept = KeptFiles.For(window, connection);

    /// <summary>The files an agent's session asked about last, held open after their calls: at most <see cref="FilesKept"/>,
    /// the least recently asked let go first, every one once its calls pause for <see cref="Idle"/> or the agent goes. Its
    /// holds count beside every other's, so letting one go never closes a file a person has open in a tab.</summary>
    private sealed class KeptFiles
    {
        private static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);

        private readonly List<(OpenDocument Document, IDisposable Hold)> held = [];
        private CancellationTokenSource? later;

        /// <summary>Lets every file go once no call has come for <see cref="Idle"/>.</summary>
        private void LetGoLater()
        {
            later?.Cancel();
            CancellationTokenSource waiting = later = new();
            Task idle;
            // Started apart from the call that kept the files, whose flow would carry its files, editors and window along.
            using (ExecutionContext.SuppressFlow())
            {
                // Unheld: what settles on an agent's call waits for what the call did, never for files kept for its next one.
                idle = BackgroundWork.Unheld(async () =>
                {
                    try
                    {
                        await Task.Delay(Idle, waiting.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    UiThread.Post(() =>
                    {
                        if (later == waiting)
                        {
                            LetGo();
                        }
                    });
                });
            }
            IdeErrors.Observe(idle);
        }

        /// <summary>The kept files of an agent's session in a window, let go when its agent goes or its window closes.</summary>
        public static KeptFiles For(MainWindow window, AgentConnection? connection)
        {
            KeptFiles files = new();
            connection?.WhenLeft(files.LetGo);
            // Its files and its idle wait are the window's: a closed window's would be kept until the wait ended.
            window.Closed += (_, _) => files.LetGo();
            return files;
        }

        /// <summary>The files of a call that ended, kept as the most recently asked, their earlier holds let go after.</summary>
        public void Take(List<(OpenDocument Document, IDisposable Hold)> files)
        {
            foreach ((OpenDocument document, IDisposable hold) in files)
            {
                int at = held.FindIndex(k => k.Document == document);
                held.Add((document, hold));
                if (at >= 0)
                {
                    held[at].Hold.Dispose();
                    held.RemoveAt(at);
                }
            }
            while (held.Count > FilesKept)
            {
                held[0].Hold.Dispose();
                held.RemoveAt(0);
            }
            if (held.Count > 0)
            {
                LetGoLater();
            }
        }

        /// <summary>Whether a file is kept, so open for the agent's next call alone where no tab shows it.</summary>
        public bool Keeps(OpenDocument document) => held.Any(k => k.Document == document);

        public void LetGo()
        {
            List<(OpenDocument Document, IDisposable Hold)> going = [.. held];
            held.Clear();
            later?.Cancel();
            later = null;
            foreach ((_, IDisposable hold) in going)
            {
                hold.Dispose();
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
    private int LineIn(TextSnapshot text, string path, int line)
    {
        int last = text.LineCount;
        return line >= 1 && line <= last ? line - 1 : throw new ArgumentException($"Line {line} is outside {Relative(path)}, whose lines are 1 to {last}");
    }

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
                string line = text is not null && at.Line < text.LineCount ? AgentAnswers.Line(text.GetLineText(at.Line), at.Column + 1) : string.Empty;
                return new Place(Relative(folder, path), at.Line + 1, at.Column + 1, line);
            })];
        }, cancel);
    }

    /// <summary>What an answer's entry takes of <see cref="AgentAnswers.MostCharacters"/>: its JSON as the answer writes it.</summary>
    private static int Size<T>(T entry) => System.Text.Json.JsonSerializer.Serialize(entry, McpHost.AnswerJson).Length;

    /// <summary>What of <see cref="AgentAnswers.MostCharacters"/> an answer's list may take: the rest is kept for what the
    /// answer says beside it (<paramref name="beside"/>'s JSON) and the words saying what it left out.</summary>
    private static int Room<T>(T beside) => AgentAnswers.MostCharacters - Size(beside) - WordsRoom;

    /// <summary>The room kept for an answer's words of what it left out and how to read it.</summary>
    private const int WordsRoom = 1_000;

    /// <summary>A list's answer held within <see cref="AgentAnswers.MostCharacters"/>: its first entries as JSON, then words of
    /// how many it left out (<paramref name="noun"/>) and how to read them (<paramref name="how"/>), <paramref name="beyond"/>
    /// more past an agent's limit counted too, read as <paramref name="beyondHow"/> says where the limit alone cut them, and
    /// where the size cut them the last entry kept's words first (<paramref name="readOn"/>); then <paramref name="note"/>'s.</summary>
    private static CallToolResult Held<T>(IReadOnlyList<T> entries, string noun, string how, int beyond = 0, string? beyondHow = null, Func<T, string>? readOn = null,
        string? note = null)
    {
        int kept = AgentAnswers.Fitting(entries, Size, AgentAnswers.MostCharacters - WordsRoom);
        int left = entries.Count - kept + beyond;
        string said = kept == entries.Count && beyondHow is not null ? beyondHow
            : kept < entries.Count && kept > 0 && readOn is not null ? $"{readOn(entries[kept - 1])}; {how}"
            : how;
        return Listed(kept == entries.Count ? entries : [.. entries.Take(kept)], left == 0 ? null : AgentAnswers.More(left, noun, said), note);
    }

    /// <summary>A text cut, where its JSON runs past <paramref name="room"/> characters, to a part whose JSON does not.</summary>
    private static string JsonWithin(string text, int room)
    {
        string cut = text;
        for (int length = room; Size(cut) > room; length /= 2)
        {
            cut = AgentAnswers.Cut(text, length);
        }
        return cut;
    }

    /// <summary>A list's answer: its entries as JSON and, after them, <paramref name="more"/>'s words where it has some, then
    /// <paramref name="note"/>'s, what the whole list went without.</summary>
    private static CallToolResult Listed<T>(IReadOnlyList<T> kept, string? more, string? note = null)
    {
        TextContentBlock json = new() { Text = System.Text.Json.JsonSerializer.Serialize(kept, McpHost.AnswerJson) };
        return new CallToolResult { Content = [json, .. new[] { more, note }.OfType<string>().Select(words => new TextContentBlock { Text = words })] };
    }

    /// <summary>A text answer's lines held within <see cref="AgentAnswers.MostCharacters"/>: its first lines, and after them
    /// how many more it left out and how to read them (<paramref name="how"/>, given the first line left out, from 0); at
    /// least the first line, cut where it alone runs past.</summary>
    private static string HeldLines(IReadOnlyList<string> lines, string noun, Func<int, string> how)
    {
        int kept = AgentAnswers.LinesWithin(lines, AgentAnswers.MostCharacters - WordsRoom);
        if (kept == lines.Count)
        {
            return string.Join('\n', lines);
        }
        string shown = kept > 0 ? string.Join('\n', lines.Take(kept)) : AgentAnswers.Cut(lines[0], AgentAnswers.MostCharacters - WordsRoom);
        return $"{shown}\n{AgentAnswers.More(lines.Count - Math.Max(kept, 1), noun, how(Math.Max(kept, 1)))}";
    }

    /// <summary>A diff's tab as the workspace names it: a diff of its file, never the file's own tab.</summary>
    private string DiffNamed(OpenDocument tab) => $"diff of {Relative(tab.Path)} ({tab.Diff!.Shown.Title})";

    /// <summary>The workspace as it stands; with <paramref name="inside"/>, a folder or file, only the projects, run
    /// configurations (by their projects' and files' places) and open files there.</summary>
    private Workspace Snapshot(string? inside = null)
    {
        ProjectSession session = Session;
        OpenDocument? active = Editors.Active;
        TextPosition? caret = active is { Diff: { } shown } ? shown.View?.Right.CaretPosition ?? new TextPosition(0, 0)
            : active is null ? null : Editors.CodeEditor.CaretPosition;
        return new Workspace(
            session.Folder,
            session.Workspace?.Filter?.Path ?? session.Solution?.Path,
            [.. (session.Workspace?.Projects ?? []).Where(p => Here(p.Path)).Select(p => new ProjectInfo(p.Name, Relative(p.Path), p.IsAndroidApp ? "Exe" : p.OutputType, p.TargetFrameworks)
            {
                Kind = session.Unloaded.Contains(p.Path) ? null : ProjectKinds.Words(ProjectKinds.Of(p)),
                Unloaded = session.Unloaded.Contains(p.Path) ? true : null,
                ChosenFramework = session.Frameworks.Of(p),
            }), .. (session.Workspace?.SharedProjects ?? []).Where(Here).Select(p => new ProjectInfo(Path.GetFileNameWithoutExtension(p), Relative(p), null, [])
            {
                Kind = ProjectKinds.Words(ProjectKind.Shared),
                SharedBy = [.. ImportersOf(session, p)],
            })],
            [.. session.Configurations.All.Where(c => inside is null || PlacesOf(c).Any(Here)).Select(c => c.Name)],
            session.Configurations.Chosen?.Name,
            session.CSharp is not { } csharp ? "none" : csharp.IsReady ? "ready" : csharp.State,
            [.. Editors.Documents.Where(d => (d.IsModified || Editors.HasTab(d) || !kept.Keeps(d)) && Here(d.Path)).Select(d => Relative(d.Path) + (d.IsModified ? " (unsaved)" : "")),
                .. Editors.Diffs.Where(d => Here(d.Path)).Select(DiffNamed)],
            active is null ? null : $"{(active.Diff is null ? Relative(active.Path) : DiffNamed(active))}:{caret!.Value.Line + 1}:{caret.Value.Column + 1}",
            Session.Debug.Session?.State.ToString().ToLowerInvariant() ?? "not debugging")
        {
            SdkMissing = SdkMissingSaid(session),
            Trust = session.Folder is null ? null : session.Trusted ? "trusted" : Refusals.Untrusted,
            MobileNeeds = window.MobileWant.Said,
            UnreadSolution = session.Workspace?.Unread is { } unread ? UnreadSolutionSaid(unread) : null,
            BuildConfiguration = session.BuildChoices.Chosen is not { } built ? null
                : session.BuildChoices.Offered.Where(o => !o.Is(built)).Select(o => o.Key).ToList() is { Count: > 0 } others
                    ? $"{built.Key} (ide_action {AppActions.ChooseBuildConfiguration} with input {Plural.Either(others)} switches it)"
                    : built.Key,
            MultiProject = session.Configurations.Profiles.Count > 0 || session.Configurations.ProfileProblems.Count > 0
                ? [.. session.Configurations.Profiles.Select(p => $"{p.Name}: {string.Join(", ", p.Starts.Select(s => $"{Path.GetFileNameWithoutExtension(s.Project)} {s.Action?.ToString() ?? "(unknown action)"}"
                    + (s.LaunchProfile.Length > 0 ? $" in {s.LaunchProfile}" : string.Empty)))}"),
                    .. session.Configurations.ProfileProblems]
                : null,
        };

        bool Here(string path) => inside is null || PathNames.Within(path, inside);
    }

    /// <summary>The files a run configuration starts from: its project's (a script's package.json), its Dockerfile, its
    /// Compose files and the projects a multi-project one starts, each canonical as the window holds them.</summary>
    private static IEnumerable<string> PlacesOf(RunConfiguration configuration) =>
        new[] { configuration.Project, configuration.Dockerfile }.Concat(configuration.ComposeFiles).Concat(configuration.Starts.Select(s => s.Project))
            .Where(place => place.Length > 0);

    /// <summary>The projects importing a shared project's items, by name, as last evaluated.</summary>
    private static IEnumerable<string> ImportersOf(ProjectSession session, string shared)
    {
        string items = SharedProjects.ItemsFileOf(shared);
        return session.Projects.Where(p => p.Evaluated?.Inputs.Any(i => PathNames.Same(i.Path, items)) == true).Select(p => p.Name);
    }

    /// <summary>What .NET the machine or the folder lacks and how it is mended, as workspace says it; null lacking nothing.</summary>
    private string? SdkMissingSaid(ProjectSession session) =>
        window.DotnetWant is { Installs: not null } wanted && Refusals.Dotnet(wanted, session.Folder) is { } lacking
            ? window.AgentsOwn
                ? $"{lacking}. This Passenger, an agent's own, installs nothing: the person installs it with Install .NET SDK in the banner over their Passenger's window"
                : $"{lacking}. Install .NET SDK installs it: the person's, from the banner over Passenger's window, or an agent's ide_action {AppActions.InstallDotnetSdk}, which asks them first"
            : window.DotnetWant is { Lack: DotnetLack.OtherProcessor } or { Translated: true, Install: not null } && Refusals.Dotnet(window.DotnetWant, session.Folder) is { } loadsNone
                ? $"{loadsNone}{(window.DotnetWant.Install is null ? string.Empty : $". {Refusals.InstallsOnlyNative}")}. "
                    + $"The person downloads the Passenger built for this computer from {Updates.Releases.ThisSystemsDownload}"
            : session.Models.MissingSdk is { } missing ? Refusals.NoSdk(missing, session.Folder) : null;

    /// <summary>Why the folder's projects cannot load for the C# code tools to search: they have no .NET SDK to load with,
    /// said as workspace says it; null while they have one, or the folder has no project.</summary>
    private string? Unloadable() =>
        Session is { Projects.Count: > 0 } session && (window.DotnetWant.NoSdkAtAll || session.Models.MissingSdk is not null) && SdkMissingSaid(session) is { } lacking
            ? Refusals.ProjectsUnloadable(lacking)
            : null;

    /// <summary>A solution that does not read as an agent is told it: its path, where its reader stopped as the editor
    /// counts the file's lines, and why.</summary>
    private string UnreadSolutionSaid(global::Passenger.Workspace.UnreadSolution unread) =>
        ServerPlaces.XmlPositionOf(Editors, unread.Path, unread.Line, unread.Column) is { } at
            ? $"{Relative(unread.Path)}:{at.Line + 1}:{at.Column + 1}: {unread.Why}"
            : $"{Relative(unread.Path)}: {unread.Why}";

    /// <summary>A path an agent gave (<see cref="Named"/>), refused in the one sentence where the agent may not read it, or
    /// with <paramref name="changes"/> change it (<see cref="AgentReach"/>): every path a tool takes but those
    /// <see cref="Named"/> lists. One it reads but never changes is read-only by where it lies (a package's, the SDK's, a
    /// copy made to be read), whatever its path says, a package's naming the package (<see cref="ReadOnlyHere"/>).</summary>
    private string Resolve(string path, bool changes = false)
    {
        string full = Named(path);
        return Reaches(full, changes) ? full
            : throw new InvalidOperationException(changes && Reaches(full, changes: false) ? $"{ReadOnlyHere(full, Relative(full))}, so nothing changed"
                : Refusals.OutOfReach(full, Session.Folder, changes));
    }

    /// <summary>A change refused of a file (a canonical path) an agent reads but may not change, the file called
    /// <paramref name="name"/>: read-only by where it lies, a package's naming the package (<see cref="ReadOnlyFiles.Refused"/>).</summary>
    private static string ReadOnlyHere(string full, string name) => ReadOnlyFiles.Refused(full, name) ?? Refusals.ReadOnly(name);

    /// <summary>Whether the agent may read a file or folder (a canonical path), or with <paramref name="changes"/> change it,
    /// as the window's folder and the folders its trusted solution reaches stand (<see cref="AgentReach"/>).</summary>
    private bool Reaches(string full, bool changes) =>
        changes ? AgentReach.Changes(full, Session.Folder, Session.Reached) : AgentReach.Reads(full, Session.Folder, Session.Reached);

    /// <summary>A path an agent gave, from the open folder unless rooted, as the IDE names it (in any letter case), wherever
    /// it is: taken so only by open for a folder or solution, the workspace the agent chooses, and as a place in a debugged
    /// program's code, which may be built anywhere and none of whose text the debugger's tools answer. One whose asking would
    /// reach another machine or open a device is refused unasked (<see cref="FoundPaths"/>).</summary>
    private string Named(string path)
    {
        string given = Path.IsPathRooted(path) || Session.Folder is not { } folder ? path : Path.Combine(folder, path);
        return FoundPaths.Askable(given, Session.Folder is { } open ? [open] : []) ? PathNames.Canonical(given)
            : throw new InvalidOperationException(Refusals.NeverAsked(given));
    }

    private string Relative(string path) => Relative(Session.Folder, path);

    private static string Relative(string? folder, string path) => folder is not null && PathNames.IsUnder(path, folder)
        ? Path.GetRelativePath(folder, path)
        : path;

    /// <summary>What a program, a terminal, a build or a container said, as an agent's answer carries it: its secrets masked by
    /// the one rule (<see cref="global::Passenger.App.Containers.ContainerSecrets.InSaid"/>), the open folder's .env among those it knows.</summary>
    private string Masked(string said) => global::Passenger.App.Containers.ContainerSecrets.InSaid(said, Session.Folder);

    /// <summary>The last lines of a terminal's scrollback and screen, as a program in it left them, masked.</summary>
    private string Tail(global::Passenger.Terminal.TerminalSession terminal, int lines)
    {
        global::Passenger.Terminal.TerminalScreen screen = terminal.Screen;
        List<string> all = [.. screen.Scrollback.Select(l => l.Text()).Concat(screen.Lines.Select(l => l.Text()))];
        while (all.Count > 0 && all[^1].Length == 0)
        {
            all.RemoveAt(all.Count - 1);
        }
        return Masked(string.Join('\n', all.Skip(Math.Max(0, all.Count - lines))));
    }

    /// <summary>The last lines of what Run's program wrote, where it runs: its tab of the terminal, else the Run console.</summary>
    private string RunOutput(int lines) => Session.RunProgram is TerminalRun terminal ? Tail(terminal.Session, lines) : Tail(Session.Runner.Shown(), lines);

    /// <summary>A console's last lines, masked.</summary>
    private string Tail(TextSnapshot text, int lines)
    {
        int first = Math.Max(0, text.LineCount - lines);
        return Masked(text.GetText(text.GetLineStart(first), text.Length - text.GetLineStart(first)));
    }

    /// <summary>Waits on the UI thread, letting it run, until something holds, the time is up, or the agent no longer
    /// wants the answer, looking again every 50 ms: only where what it waits on raises nothing as it changes, each such
    /// place saying why; everywhere else <see cref="When"/>.</summary>
    private static async Task Until(Func<bool> condition, TimeSpan limit, CancellationToken cancel)
    {
        Deadline until = Deadline.In(limit);
        while (!condition() && !until.Passed)
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

    /// <summary>What .NET the machine or the folder lacks (no .NET, a runtime alone, global.json's SDK, an SDK older than a
    /// project's framework) and how the person mends it; null lacking nothing.</summary>
    public string? SdkMissing { get; init; }

    /// <summary>Whether the folder is trusted, so Passenger runs its code ("trusted"), or why none of it runs; null with no
    /// folder open.</summary>
    public string? Trust { get; init; }

    /// <summary>What the folder's mobile projects need that the machine lacks, and where they build if not on it, as the
    /// banner over Passenger's window says it; null with nothing to say.</summary>
    public string? MobileNeeds { get; init; }

    /// <summary>The container run under way, beside the AppHosts, its services as their resources are; null with none.</summary>
    public ContainerRunInfo? ContainerRun { get; init; }

    /// <summary>The run configurations that start several projects, the solution's launch profiles, each with what it does
    /// with each project (Start debugs it when debugged) and its launch profile, then why a file of them did not read; null
    /// with none.</summary>
    public IReadOnlyList<string>? MultiProject { get; init; }

    /// <summary>The build configuration every build, run and test takes (Release|Any CPU), then the others the solution or
    /// projects declare; null before they are read.</summary>
    public string? BuildConfiguration { get; init; }

    /// <summary>The web projects' routes, when asked for.</summary>
    public IReadOnlyList<RouteInfo>? Routes { get; init; }

    /// <summary>The solution the folder opens by that does not read ("path:line:column: why"), in whose place the projects
    /// under the folder are open; null with none.</summary>
    public string? UnreadSolution { get; init; }

    /// <summary>How many projects the answer named alone or left out, and run configurations, open files and routes it left
    /// out, and how to read them; null where it holds them all.</summary>
    public string? More { get; init; }
}

/// <summary>A route as an agent reads it: its project, what declares it, its methods and template ("GET /todos/{id:int}"),
/// its handler, its parameters ("id:int (route)"), where it is declared, and its body's type.</summary>
public sealed record RouteInfo(string Project, string Kind, string Route, string Handler, IReadOnlyList<string> Parameters, string Path, int Line, string? Body);

/// <summary>A project as workspace lists it: its output type as evaluated, but Exe for an Android app (a .NET MAUI app's
/// Android build), which .NET for Android evaluates as Library though it runs.</summary>
public sealed record ProjectInfo(string Name, string Path, string? OutputType, IReadOnlyList<string>? TargetFrameworks)
{
    /// <summary>What it builds as its evaluation says, in the Solution view's words ("web API", "class library"); null
    /// where none is said: not yet evaluated, unloaded, or of no kind told apart.</summary>
    public string? Kind { get; init; }

    /// <summary>True for a project not loaded: one the solution filter opened leaves out, or one the person unloaded; null
    /// for one loaded, which the answer leaves unsaid.</summary>
    public bool? Unloaded { get; init; }

    /// <summary>A multi-targeted project's framework chosen, which its XAML preview, its XAML and the language server's
    /// answers for its C# follow; null for a project of one.</summary>
    public string? ChosenFramework { get; init; }

    /// <summary>For a shared project (.shproj), which builds nothing of its own, the projects importing its items and so
    /// compiling its files; null for every other project.</summary>
    public IReadOnlyList<string>? SharedBy { get; init; }
}

public sealed record Finding(string Severity, string? Code, string Message, string? Path, int Line, int Column);

public sealed record Place(string Path, int Line, int Column, string Text);

/// <summary>usages' answer: the C# symbol the place took, as its card names it, how many places, each file's places as
/// "line:column text", and what it went without (files the C# language server has yet to take in, a Godot project's scenes
/// its build doesn't reach), when it did.</summary>
public sealed record UsagesFound(string? Symbol, int Count, IReadOnlyList<FilePlaces> Files, string? Note = null)
{
    /// <summary>How many places the answer only counted or left out, and how to read them.</summary>
    public string? More { get; init; }
}

/// <summary>A file's places, as "line:column text"; null where the answer only counts them (<see cref="Count"/>).</summary>
public sealed record FilePlaces(string Path, IReadOnlyList<string>? Places)
{
    /// <summary>How many places the file has, where the answer counts them rather than listing them.</summary>
    public int? Count { get; init; }

    /// <summary>True for a file of generated code, as Roslyn tells it (a source generator's, or one a tool wrote: a .g.cs,
    /// a .designer.cs, one marked &lt;auto-generated&gt;), listed after the rest; null for one a person wrote, which the
    /// answer leaves unsaid.</summary>
    public bool? Generated { get; init; }
}

public sealed record HierarchyEntry(string? Name, string? Detail, string Path, int Line, int Column, string Text);

/// <summary>A level of the dependency map as an agent reads it: its nodes, its edges, and what was left out.</summary>
public sealed record DependencyLevel(IReadOnlyList<DependencyNodeInfo> Nodes, IReadOnlyList<DependencyEdgeInfo> Edges, string? More = null);

/// <summary>A node of the map: its id, name and kind, where it is written, and whether a level stands below it.</summary>
public sealed record DependencyNodeInfo(string Id, string Name, string Kind, string? Path, int? Line, bool? Opens);

/// <summary>An edge of the map: the node that uses, the id of what it uses, and how many uses of each kind.</summary>
public sealed record DependencyEdgeInfo(string From, string To, IReadOnlyDictionary<string, int> Kinds);

/// <summary>A build's answer: MSBuild's last lines only when it failed, since a build that succeeded has nothing in them
/// an agent acts on (output of build has them).</summary>
public sealed record BuildResult(bool Succeeded, string Outcome, int Errors, int Warnings, IReadOnlyList<Finding> Problems, string? OutputTail)
{
    /// <summary>How many of its errors and warnings were left out, and how to read them.</summary>
    public string? More { get; init; }

    /// <summary>The folder a publish landed in, once it did.</summary>
    public string? Folder { get; init; }

    /// <summary>The image a container publish made, with its registry where it pushed it.</summary>
    public string? Image { get; init; }
}

public sealed record RunResult(bool StillRunning, int? ExitCode, string Status, string Output);

/// <summary>A breakpoint as an agent reads it: a line breakpoint's path and line, an exception breakpoint's type and scenarios,
/// what a data breakpoint watches ("count on counter (Counter)"), or a property breakpoint's property and element ("Text on
/// TextBlock#Counter", and the element's @id).</summary>
public sealed record BreakpointInfo(string? Path, int? Line, bool Enabled, string Status, string? Condition, string? HitCondition, string? LogMessage, bool Suspend,
    string? Message, string? Exception = null, IReadOnlyList<string>? Scenarios = null, string? Data = null, string? Attribute = null, string? Property = null,
    bool? BindingErrors = null, string? Control = null, int? Column = null, bool? ScriptErrors = null, string? Method = null, bool? Temporary = null, string? WaitsFor = null);

public sealed record VariableInfo(string Name, string Value, string? Type, int Reference)
{
    /// <summary>Where the value runs past what an answer shows: its length and how debug_evaluate reads on.</summary>
    public string? Cut { get; init; }

    /// <summary>The frame debug_evaluate used by default where it is not the top, as debug_state lists it.</summary>
    public string? Frame { get; init; }

    /// <summary>What other threads passed over while debug_evaluate ran with every thread let go.</summary>
    public string? PassedOver { get; init; }

    /// <summary>The label of the marked object the value is ($1), which any expression names.</summary>
    public string? Mark { get; init; }
}

/// <summary>A .NET program the debugger can attach to, as Attach to Process lists it, its command line's secrets masked.</summary>
public sealed record ProcessInfo(int ProcessId, string Executable, string Program, string User, string CommandLine, string? Container = null);

/// <summary>A module a debugged program loaded, as debug_state's modules lists it: its symbols' state, Just My Code's verdict
/// and whether the runtime compiles it optimized; no path for one made as the program runs.</summary>
public sealed record ModuleListing(string Name, string? Version, string Symbols, bool UserCode, bool Optimized, string? Path);

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

    /// <summary>At a stop, how long the program ran to it, as its line in the editor says.</summary>
    public string? Elapsed { get; init; }

    /// <summary>At a stop in a debugged Avalonia app, the binding values after the lines of the .axaml files the editors show,
    /// as the editor shows them (path:line: Property: value).</summary>
    public IReadOnlyList<string>? BindingValues { get; init; }

    /// <summary>A debugged test run that ended as the program went on: its tally and failures, as tests_run answers them.</summary>
    public TestRunInfo? Tests { get; init; }

    /// <summary>The breakpoints the programs debugged will not stop at, each path:line: why and how to put it right.</summary>
    public IReadOnlyList<string>? Unbound { get; init; }

    /// <summary>With several programs debugged, the one this is.</summary>
    public string? Session { get; init; }

    /// <summary>With several programs debugged, each one's name and state.</summary>
    public IReadOnlyList<string>? Sessions { get; init; }
}

public sealed record TestRunInfo(string Summary, IReadOnlyList<TestFailure> Failures, DebugSnapshot? Debugger = null)
{
    /// <summary>How many more failed tests were left out, and how to read them.</summary>
    public string? More { get; init; }

    /// <summary>What a run with coverage measured.</summary>
    public CoverageInfo? Coverage { get; init; }

    /// <summary>What of the failed scenarios' steps an answer of many left out.</summary>
    public string? StepsLeftOut { get; init; }
}

/// <summary>A run's coverage: what its lines and branches came to, each file's least covered first, and what it did not
/// measure, with why.</summary>
public sealed record CoverageInfo(string Summary, IReadOnlyList<CoveredFile> Files)
{
    public IReadOnlyList<string>? Unmeasured { get; init; }

    /// <summary>How many more files were left out, and how to read them.</summary>
    public string? More { get; init; }
}

/// <summary>A file's coverage: its lines and branches run, its lines never run as stretches, and those with a branch never
/// taken.</summary>
public sealed record CoveredFile(string Path, string Lines, string? Branches)
{
    public string? NeverRun { get; init; }

    public string? BranchesNotTaken { get; init; }

    /// <summary>The share of its lines run, which orders the files and is not said.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double Rate { get; init; }
}

public sealed record TestFailure(string Test, string? Location, string Details)
{
    /// <summary>A failed Reqnroll or SpecFlow scenario's steps, as its run went.</summary>
    public IReadOnlyList<StepInfo>? Steps { get; init; }
}

/// <summary>A scenario's step as its run went: its text with an example's values, place and outcome, the example row it ran for;
/// for one that did not pass, the definition its text binds (path:line Type.Method), its arguments, what was said, and a
/// failure's frames in the folder.</summary>
public sealed record StepInfo(string Step, string Location, string Outcome)
{
    public string? Row { get; init; }

    public string? Definition { get; init; }

    public string? Arguments { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<string>? Frames { get; init; }
}

public sealed record TestInfoRow(string Test, string State, string? Location)
{
    /// <summary>When the run its state is from began, where that was an earlier session's, and whether its code changed since
    /// (this session's run too).</summary>
    public string? Earlier { get; init; }
}

public sealed record SymbolFound(string Name, string Kind, string? Container, string Path, int Line, int Column)
{
    /// <summary>Its documentation id, as an exact find gives it.</summary>
    public string? Id { get; init; }
}
