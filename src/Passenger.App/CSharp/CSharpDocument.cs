using System.Diagnostics;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>One open C# file's share of the language server: it is kept in step, coloured by what the
/// compiler knows, and marked with the diagnostics the server reports, pulled once typing pauses.
/// Between pulls the markers move with the text, so a squiggle never drifts off its code. Of one server only: a server
/// started again gives every open file a new one (<see cref="CSharpService"/>), so what asks the server for a file asks
/// <see cref="CSharpService.For"/> each time rather than keep this or its <see cref="Lsp"/>.</summary>
public sealed partial class CSharpDocument : IServerDocument, IDisposable
{
    /// <summary>How long typing must pause before diagnostics are asked for again.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(600);

    /// <summary>Parameter names that say nothing an argument does not (Console.WriteLine's value, a list's item), whose
    /// hints are left off.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal) { "value:", "obj:", "item:", "o:", "arg:" };

    private readonly CSharpService service;
    private readonly OpenDocument document;
    private readonly ServerDiagnostics diagnostics;
    private SemanticHighlighter? semantic;

    internal CSharpDocument(CSharpService service, OpenDocument document)
    {
        this.service = service;
        this.document = document;
        diagnostics = new ServerDiagnostics(document, checker: null);
        // What the server before this one reported, until this one does.
        diagnostics.Adopt(document.Markers);
        document.Text.Changed += OnChanged;
        document.Saved += OnSaved;
        if (document.Syntax is { } syntax)
        {
            syntax.Parsed += OnParsed;
            if (syntax.DeepAt is int at)
            {
                SayTooDeep(at);
            }
        }
    }

    /// <summary>Whether the file's text goes too deep to read (<see cref="CSharpParsing.TooDeep"/>), as its note says: the
    /// server is never given it, as its parser has no such bound and its process would end.</summary>
    public bool TooDeep { get; private set; }

    /// <summary>Whether the file went from too deep to read back to read, which its project's next load takes once it is saved.</summary>
    private bool readAgain;

    public LspDocument? Lsp { get; private set; }

    /// <summary>The diagnostics last pulled, moved with the text since, so their lines are the text's as it is now.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics.Placed;

    /// <summary>Opens the file in the server unless it is open there already; whether it was opened now.</summary>
    internal bool Connect(RoslynSession session)
    {
        if (Lsp is not null || TooDeep || document.Syntax is { } syntax && CSharpParsing.DeepAt(document.Text.Snapshot, syntax.Options) is not null)
        {
            return false;
        }
        Lsp = session.Open(document.Path, document.Text);
        Colour(Lsp, session.Server);
        // A file no tab shows (an agent's) is pulled as its tool asks, or once a tab shows it.
        if (service.Unseen(document))
        {
            Stale = true;
        }
        else
        {
            Pull(TimeSpan.Zero);
        }
        FindContexts();
        return true;
    }

    /// <summary>The projects the server has the file in, by Roslyn's own <c>textDocument/_vs_getProjectContexts</c>,
    /// asked again as the server's projects load: the one chosen stays while the server still has it.</summary>
    public IReadOnlyList<ProjectContext> Contexts { get; private set; } = [];

    /// <summary>When the server first said it has the file in a project of the folder's, not Miscellaneous Files: when
    /// it was asked (what it answers asked after that is that project's), when it said so (Stopwatch timestamps) and
    /// the project; null before. A start's timing reads it.</summary>
    internal (long Asked, long At, string Project)? Placed { get; private set; }

    /// <summary>Asks the server which projects it has the file in, as background work.</summary>
    public void FindContexts()
    {
        if (Lsp is { IsRazor: false } lsp)
        {
            BackgroundWork.Begin();
            IdeErrors.Observe(FindContextsAsync(lsp));
        }
    }

    private async Task FindContextsAsync(LspDocument lsp)
    {
        try
        {
            long asked = Stopwatch.GetTimestamp();
            ProjectContextList? found = await lsp.ProjectContextsAsync();
            if (Lsp != lsp || found is null)
            {
                return;
            }
            Contexts = CSharpService.Offered([.. found.ProjectContexts.Where(c => !c.IsMiscellaneous)]);
            ProjectContext? kept = lsp.Context is { } chosen ? Contexts.FirstOrDefault(c => c.Label == chosen.Label) : null;
            // Its project's framework chosen, else the one chosen for it alone, else the server's own unless that is one not offered.
            ProjectContext? now = service.ChosenContext(document.Path, Contexts) ?? kept
                ?? (found.Default is { } given && !given.IsMiscellaneous && !Contexts.Any(c => c.Label == given.Label) ? Contexts.FirstOrDefault() : null);
            string? was = lsp.Context?.Label ?? found.Default?.Label;
            lsp.Context = now;
            if ((now ?? found.Default)?.Label != was)
            {
                Refresh();
            }
            document.ProjectContexts = [.. Contexts.Select(c => c.Label)];
            document.ProjectContext = (now ?? found.Default)?.Label;
            if (Placed is null && Contexts.Count > 0)
            {
                Placed = (asked, Stopwatch.GetTimestamp(), document.ProjectContext ?? Contexts[0].Label);
            }
        }
        catch (LanguageServerFailed)
        {
            // Not yet able to say (its projects still loading), or gone: asked again as they load.
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    /// <summary>Has the server answer for the file in another of its projects (the target framework switcher): its
    /// colours and diagnostics are asked for again in it. A framework of its one project is that project's choice, which
    /// its other files follow.</summary>
    public void Choose(ProjectContext context)
    {
        if (Lsp is not { } lsp)
        {
            return;
        }
        Answer(lsp, context);
        service.ChooseFramework(document.Path, Contexts, context);
    }

    /// <summary>The framework chosen for the file's project followed: the server answers for the file in it from now on.</summary>
    internal void FollowFramework()
    {
        if (Lsp is { } lsp && service.ChosenContext(document.Path, Contexts) is { } chosen && chosen.Label != document.ProjectContext)
        {
            Answer(lsp, chosen);
        }
    }

    private void Answer(LspDocument lsp, ProjectContext context)
    {
        lsp.Context = context;
        document.ProjectContext = context.Label;
        Refresh();
    }

    /// <summary>The server's colours over the parser's, once the server has said its legend: a Razor file's is the one Razor
    /// registers as it starts, which can come after the file was opened.</summary>
    private void Colour(LspDocument lsp, LanguageServer server)
    {
        if (semantic is null && (lsp.IsRazor ? server.RazorLegend : server.Legend) is { } legend && document.Highlighter is { } syntax)
        {
            semantic = new SemanticHighlighter(syntax, lsp, legend);
            document.Highlighter = semantic;
        }
    }

    public void Refresh()
    {
        RefreshColours();
        RefreshDiagnostics();
    }

    public void RefreshColours()
    {
        if (semantic is null && Lsp is { } lsp && service.Session is { } session)
        {
            Colour(lsp, session.Server);
            return;
        }
        semantic?.Refresh();
    }

    /// <summary>The server's word that its colours moved: asked for again unless the request out already answers with it.</summary>
    public void ColoursMoved()
    {
        if (semantic is null)
        {
            RefreshColours();
            return;
        }
        semantic.Refreshed();
    }

    /// <summary>The server's word that diagnostics may have changed: asked for again, Aspire's warnings with them, whose
    /// answer replaces those shown.</summary>
    public void RefreshDiagnostics()
    {
        Stale = false;
        Pull(TimeSpan.Zero);
    }

    /// <summary>Whether a refresh of the server's passed the file over while no tab showed it, so its marks wait for a pull.</summary>
    public bool Stale { get; set; }

    public void Dispose()
    {
        diagnostics.Stop();
        document.Text.Changed -= OnChanged;
        document.Saved -= OnSaved;
        if (document.Syntax is { } syntax)
        {
            syntax.Parsed -= OnParsed;
        }
        if (semantic is not null)
        {
            semantic.Dispose();
            if (document.Highlighter == semantic)
            {
                document.Highlighter = semantic.Syntax;
            }
        }
        Lsp?.Dispose();
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        // The problems a fix asked for at the caret reads, where the text carried their ranges.
        diagnostics.Moved();
        Pull(Pause);
        service.Generated.Changed();
        // An AppHost's edit may change what its services are told is missing; any other file's changes only its own.
        if (AspireRole == Aspire.AspireRole.AppHost)
        {
            service.AspireGraphChanged();
        }
    }

    private void OnSaved(object? sender, EventArgs e)
    {
        Lsp?.Saved();
        if (readAgain && !TooDeep)
        {
            readAgain = false;
            service.ReloadHolder(document.Path);
        }
    }

    /// <summary>A parse landed for the text as it stands: one gone too deep is said to be and taken from the server, and one
    /// read again goes back to it.</summary>
    private void OnParsed(object? sender, ParsedText parsed)
    {
        if (parsed.DeepAt is int at)
        {
            SayTooDeep(at);
        }
        else if (TooDeep)
        {
            TooDeep = false;
            readAgain = true;
            Show(diagnostics.Take([], DiagnosticMarks.Marker));
            service.FileProblemsChanged(document);
            service.Reconnect();
        }
    }

    /// <summary>The note where the text goes too deep, its only mark: a file the server has is taken from it first, its
    /// features made again for a file it hasn't (<see cref="CSharpService.Reattach"/>).</summary>
    private void SayTooDeep(int at)
    {
        if (Lsp is not null)
        {
            service.Reattach(document);
            return;
        }
        TooDeep = true;
        ServerLines lines = ServerLines.Of(document.Text.Snapshot);
        Diagnostic note = new()
        {
            Range = new LspRange(lines.PositionOf(at), lines.PositionOf(Math.Min(at + 1, lines.Text.Length))),
            Severity = DiagnosticSeverity.Information,
            Source = "Passenger",
            Message = CSharpParsing.TooDeep,
        };
        Show(diagnostics.Take([note], DiagnosticMarks.Marker));
        service.FileProblemsChanged(document);
    }

    /// <summary>The markers a report gave the file, or for one that changed none (null), those the text carries already,
    /// unless the file's own carrying of them went otherwise than the diagnostics' ranges did.</summary>
    private void Show(IReadOnlyList<Marker>? markers)
    {
        if (markers is not null)
        {
            document.Markers = markers;
        }
        else if (!diagnostics.Carries(document.Markers))
        {
            document.Markers = diagnostics.Markers;
        }
    }

    /// <summary>Pulls after <paramref name="delay"/>. An ask for now (the server's refresh, a file the pull reads changed)
    /// while a pull waits out typing's pause is left to that pull, which then asks for the text as it stands: typing
    /// that goes on is never answered by a pull between its keys.</summary>
    private void Pull(TimeSpan delay)
    {
        if (Lsp is not { } lsp || !service.IsReady && delay > TimeSpan.Zero || delay == TimeSpan.Zero && diagnostics.Pausing)
        {
            return;
        }
        diagnostics.Pull(delay, cancel => PullAsync(lsp, cancel), service.FilePulling());
    }

    /// <summary>The file's inlay hints asked for again with its diagnostics: the kinds the editors show changed.</summary>
    public void RefreshHints() => Pull(TimeSpan.Zero);

    /// <summary>The file's diagnostics, with Aspire's for a service's file (<see cref="AspireDiagnosticsAsync"/>) and an
    /// AppHost's (<see cref="AspireUnsetParametersAsync"/>), then its inlay hints (<see cref="ShowHintsAsync"/>), then what
    /// the project's whole analysis reports in it, each for the text as it stands. A later pull (an AppHost's edit, the
    /// server's refresh) cancels this one.</summary>
    private async Task PullAsync(LspDocument lsp, CancellationToken cancel)
    {
        if (await ShowPulledAsync(lsp, cancel) is { } own && await ShowHintsAsync(lsp, cancel))
        {
            await ShowProjectWideAsync(lsp, own, cancel);
        }
    }

    /// <summary>The file's inlay hints of the kinds the editors show, for the text as it stands, none asked for while they
    /// show neither; whether the hints shown answer the text.</summary>
    private async Task<bool> ShowHintsAsync(LspDocument lsp, CancellationToken cancel)
    {
        if (!service.AsksHints)
        {
            // A path's node is what the text before it comes to, shown whatever the hints of the server's kinds are set to.
            document.InlayHints = HintList.Of(GodotHints(document.Text.Version));
            return true;
        }
        (IReadOnlyList<LspInlayHint> hints, int hinted) = await lsp.InlayHintsAsync(cancel);
        if (cancel.IsCancellationRequested || hinted != document.Text.Version)
        {
            return false;
        }
        ParsedText? syntax = hints.Any(h => h.Kind == Passenger.Lsp.InlayHintKind.Type) && document.Syntax is { } parsing ? await parsing.CurrentAsync() : null;
        if (cancel.IsCancellationRequested || hinted != document.Text.Version)
        {
            return false;
        }
        // Placed on the thread pool, in the text and syntax they were asked for: a file of thousands of calls has as many.
        TextSnapshot text = document.Text.Snapshot;
        bool razor = lsp.IsRazor;
        IReadOnlyList<InlayHint> godot = GodotHints(hinted);
        HintList shown = await BackgroundWork.Run(() => Shown(text, syntax, razor, hints, godot), cancel);
        if (cancel.IsCancellationRequested || hinted != document.Text.Version)
        {
            return false;
        }
        document.InlayHints = shown;
        return true;
    }

    /// <summary>The server's diagnostics from the file's own sources, Aspire's and the open compose files' shown for the text
    /// as it stands, with the project-wide ones carried until theirs answers; what was shown but those, or null when they no
    /// longer answer it.</summary>
    private async Task<IReadOnlyList<Diagnostic>?> ShowPulledAsync(LspDocument lsp, CancellationToken cancel)
    {
        if (await diagnostics.PulledAsync(lsp, cancel, DiagnosticPull.Own) is not { } found)
        {
            return null;
        }
        int version = document.Text.Version;
        // Read as the findings are asked (AspireFindingsAsync reads it before it awaits): a later change pulls again.
        pulledAs = AspireRole;
        IReadOnlyList<Diagnostic> aspire = await AspireFindingsAsync(lsp, cancel);
        IReadOnlyList<Diagnostic> compose = await ComposeFindingsAsync(cancel);
        IReadOnlyList<Diagnostic> configuration = await ConfigurationFindingsAsync([.. aspire, .. compose], cancel);
        IReadOnlyList<Diagnostic> localization = await LocalizationFindingsAsync(cancel);
        IReadOnlyList<Diagnostic> godot = await GodotFindingsAsync(cancel);
        IReadOnlyList<Diagnostic> classes = await StylesheetFindingsAsync(lsp, cancel);
        if (cancel.IsCancellationRequested || version != document.Text.Version || Lsp != lsp)
        {
            return null;
        }
        IReadOnlyList<Diagnostic> own = [.. found, .. aspire, .. compose, .. configuration, .. localization, .. godot, .. classes];
        IReadOnlyList<Diagnostic> carried = lsp.PullsProjectWide ? diagnostics.Late : [];
        Show(diagnostics.Take([.. own, .. carried], DiagnosticMarks.Marker, carried.Count));
        ShowGodotMarks();
        service.FileProblemsChanged(document);
        return own;
    }

    /// <summary>What the project's whole analysis reports in the file, shown with its <paramref name="own"/> diagnostics once
    /// it answers the text they were shown for: in a large project long after them, so they never wait for it.</summary>
    private async Task ShowProjectWideAsync(LspDocument lsp, IReadOnlyList<Diagnostic> own, CancellationToken cancel)
    {
        int version = document.Text.Version;
        if (!lsp.PullsProjectWide || await diagnostics.PulledAsync(lsp, cancel, DiagnosticPull.ProjectWide) is not { } late
            || version != document.Text.Version || Lsp != lsp || late.Count == 0 && diagnostics.Late.Count == 0)
        {
            return;
        }
        Show(diagnostics.Take([.. own, .. late], DiagnosticMarks.Marker, late.Count));
        service.FileProblemsChanged(document);
    }

    /// <summary>Aspire's own diagnostics of the file for the text as it stands: a service's references its AppHost lacks, an
    /// AppHost's parameters with no value; none for any other file, which asks nothing for them.</summary>
    internal async Task<IReadOnlyList<Diagnostic>> AspireFindingsAsync(LspDocument lsp, CancellationToken cancel) => AspireRole switch
    {
        Aspire.AspireRole.Service => await AspireDiagnosticsAsync(lsp, cancel),
        Aspire.AspireRole.AppHost => await AspireUnsetParametersAsync(lsp, cancel),
        _ => [],
    };

    /// <summary>Passenger's own diagnostics of the file for the text as it stands, as its pull marks them: Aspire's, its reads
    /// of configuration keys nothing gives (<see cref="ConfigurationFindingsAsync"/>), of localized strings a language lacks
    /// (<see cref="LocalizationFindingsAsync"/>) and what its Godot scenes say of it (<see cref="GodotFindingsAsync"/>); an
    /// agent's <c>diagnostics</c> lists them.</summary>
    internal async Task<IReadOnlyList<Diagnostic>> PassengerFindingsAsync(LspDocument lsp, CancellationToken cancel)
    {
        IReadOnlyList<Diagnostic> aspire = await AspireFindingsAsync(lsp, cancel);
        return [.. aspire, .. await ConfigurationFindingsAsync(aspire, cancel), .. await LocalizationFindingsAsync(cancel), .. await GodotFindingsAsync(cancel)];
    }

    /// <summary>The file's diagnostics pulled and shown now, Aspire's with them, as a typing pause's pull shows them: for an
    /// agent's call that reads Passenger's own fixes, which stand on them.</summary>
    internal async Task PullNowAsync(CancellationToken cancel)
    {
        Stale = false;
        if (Lsp is { } lsp && await ShowPulledAsync(lsp, cancel) is { } own)
        {
            await ShowProjectWideAsync(lsp, own, cancel);
        }
    }

    /// <summary>The hints shown of those the server gave for a text: parameter names but those that say nothing, and
    /// types where <see cref="TypeHints"/> puts them (by the text's <paramref name="syntax"/>), each kept with the
    /// server's own for its hover card; with the scenes' hints of GetNode paths (<paramref name="godot"/>). Reads nothing
    /// but what it is given, so it runs off the UI thread.</summary>
    private static HintList Shown(TextSnapshot text, ParsedText? syntax, bool razor, IReadOnlyList<LspInlayHint> hints, IReadOnlyList<InlayHint> godot)
    {
        ServerLines lines = ServerLines.Of(text);
        List<InlayHint> shown = new(hints.Count + godot.Count);
        shown.AddRange(godot);
        foreach (LspInlayHint hint in hints)
        {
            string label = hint.Label.Trim();
            int offset = lines.OffsetOf(hint.Position);
            if (hint.Kind == Passenger.Lsp.InlayHintKind.Type)
            {
                if (syntax is not null && TypeHints.Place(syntax, offset, label) is { } placed)
                {
                    shown.Add(placed with { Data = hint });
                }
            }
            else if (!Generic.Contains(label) && !(razor && OnDirective(text, offset)))
            {
                shown.Add(new InlayHint(offset, label, hint.PaddingLeft, hint.PaddingRight, Passenger.Editor.InlayHintKind.Parameter, hint));
            }
        }
        return HintList.Of(shown);
    }

    /// <summary>Whether a place is on a Razor directive's line (<c>@page "/counter"</c>), whose value Razor's generated
    /// C# passes to an attribute: the server names the attribute's parameter there, which says nothing.</summary>
    private static bool OnDirective(TextSnapshot text, int offset) =>
        Directive.IsMatch(text.GetLineText(text.GetLineFromOffset(Math.Min(offset, text.Length))));

    private static readonly System.Text.RegularExpressions.Regex Directive =
        new(@"^\s*@(page|layout|inherits|implements|namespace|attribute|typeparam|rendermode|preservewhitespace|model|inject|section)\b");

}
