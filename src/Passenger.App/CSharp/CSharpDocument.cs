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
    }

    public LspDocument? Lsp { get; private set; }

    /// <summary>The diagnostics last pulled, moved with the text since, so their lines are the text's as it is now.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics.Placed;

    /// <summary>Opens the file in the server unless it is open there already; whether it was opened now.</summary>
    internal bool Connect(RoslynSession session)
    {
        if (Lsp is not null)
        {
            return false;
        }
        Lsp = session.Open(document.Path, document.Text);
        Colour(Lsp, session.Server);
        Pull(TimeSpan.Zero);
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
            Contexts = [.. found.ProjectContexts.Where(c => !c.IsMiscellaneous)];
            ProjectContext? kept = lsp.Context is { } chosen ? Contexts.FirstOrDefault(c => c.Label == chosen.Label) : null;
            if (lsp.Context is not null && kept is null)
            {
                // The project it was chosen in is gone (a framework no longer targeted): the server's choice from now on.
                lsp.Context = null;
                Refresh();
            }
            else
            {
                lsp.Context = kept;
            }
            document.ProjectContexts = [.. Contexts.Select(c => c.Label)];
            document.ProjectContext = (kept ?? found.Default)?.Label;
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
    /// colours and diagnostics are asked for again in it.</summary>
    public void Choose(ProjectContext context)
    {
        if (Lsp is not { } lsp)
        {
            return;
        }
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
    public void RefreshDiagnostics() => Pull(TimeSpan.Zero);

    public void Dispose()
    {
        diagnostics.Stop();
        document.Text.Changed -= OnChanged;
        document.Saved -= OnSaved;
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

    private void OnSaved(object? sender, EventArgs e) => Lsp?.Saved();

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
            document.InlayHints = HintList.Empty;
            return true;
        }
        (IReadOnlyList<LspInlayHint> hints, int hinted) = await lsp.InlayHintsAsync(cancel);
        if (cancel.IsCancellationRequested || hinted != document.Text.Version)
        {
            return false;
        }
        // Placed on the thread pool, in the text and syntax they were asked for: a file of thousands of calls has as many.
        TextSnapshot text = document.Text.Snapshot;
        ParsedText? syntax = hints.Any(h => h.Kind == Passenger.Lsp.InlayHintKind.Type) ? document.Syntax?.Current : null;
        bool razor = lsp.IsRazor;
        HintList shown = await BackgroundWork.Run(() => Shown(text, syntax, razor, hints), cancel);
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
        if (cancel.IsCancellationRequested || version != document.Text.Version || Lsp != lsp)
        {
            return null;
        }
        IReadOnlyList<Diagnostic> own = [.. found, .. aspire, .. compose, .. configuration, .. localization];
        IReadOnlyList<Diagnostic> carried = lsp.PullsProjectWide ? diagnostics.Late : [];
        document.Markers = diagnostics.Take([.. own, .. carried], DiagnosticMarks.Marker, carried.Count);
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
        document.Markers = diagnostics.Take([.. own, .. late], DiagnosticMarks.Marker, late.Count);
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
    /// of configuration keys nothing gives (<see cref="ConfigurationFindingsAsync"/>) and of localized strings a language lacks
    /// (<see cref="LocalizationFindingsAsync"/>); an agent's <c>diagnostics</c> lists them.</summary>
    internal async Task<IReadOnlyList<Diagnostic>> PassengerFindingsAsync(LspDocument lsp, CancellationToken cancel)
    {
        IReadOnlyList<Diagnostic> aspire = await AspireFindingsAsync(lsp, cancel);
        return [.. aspire, .. await ConfigurationFindingsAsync(aspire, cancel), .. await LocalizationFindingsAsync(cancel)];
    }

    /// <summary>The file's diagnostics pulled and shown now, Aspire's with them, as a typing pause's pull shows them: for an
    /// agent's call that reads Passenger's own fixes, which stand on them.</summary>
    internal async Task PullNowAsync(CancellationToken cancel)
    {
        if (Lsp is { } lsp && await ShowPulledAsync(lsp, cancel) is { } own)
        {
            await ShowProjectWideAsync(lsp, own, cancel);
        }
    }

    /// <summary>The hints shown of those the server gave for a text: parameter names but those that say nothing, and
    /// types where <see cref="TypeHints"/> puts them (by the text's <paramref name="syntax"/>), each kept with the
    /// server's own for its hover card. Reads nothing but what it is given, so it runs off the UI thread.</summary>
    private static HintList Shown(TextSnapshot text, ParsedText? syntax, bool razor, IReadOnlyList<LspInlayHint> hints)
    {
        ServerLines lines = ServerLines.Of(text);
        List<InlayHint> shown = new(hints.Count);
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
