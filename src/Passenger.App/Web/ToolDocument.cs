using System.Text.Json;
using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.Web;

/// <summary>One open file as one of the folder's linters or formatters serves it (<see cref="WebTool"/>): kept in step with
/// the tool's server; a linter's problems, pulled as typing pauses or taken as its server pushes them, shown with the rest of
/// the file's (<see cref="WebDocument.ShowLint"/>) as "Oxlint: message (rule)" at their severity, the rule's page their link,
/// carried with the text between answers; its fixes for a line; and a formatter's layout of the file.</summary>
public sealed class ToolDocument : IDisposable
{
    /// <summary>How long typing must pause before the file is checked again.</summary>
    private readonly TimeSpan settle = Editors.TypingPauses.WebChecks;

    /// <summary>How long a push of the file's problems is waited for after a change before its checks let it go.</summary>
    private static readonly TimeSpan PushLimit = TimeSpan.FromSeconds(5);

    private readonly OpenDocument document;
    private readonly ServerDiagnostics diagnostics;
    private LanguageSession? session;
    private bool disposed;

    /// <summary>The file's checks held from its attaching until its server has connected it, which asks its problems then,
    /// or cannot.</summary>
    private OpenDocument.CheckHold? waiting;

    /// <summary>The file's checks held, for a server that pushes its problems, from a change until a push answering it lands
    /// or <see cref="PushLimit"/> passes.</summary>
    private OpenDocument.CheckHold? awaitingPush;
    private CancellationTokenSource? pushLimit;

    internal ToolDocument(OpenDocument document, WebDocument web, WebTool tool)
    {
        this.document = document;
        Tool = tool;
        Web = web;
        diagnostics = new ServerDiagnostics(document, tool.Name);
        document.Text.Changed += OnChanged;
        document.Saved += OnSaved;
        waiting = tool.Lints ? document.Checking(tool.Name) : null;
    }

    public WebTool Tool { get; }

    public WebDocument Web { get; }

    public LspDocument? Lsp { get; private set; }

    /// <summary>The problems last reported, on the server's lines of the text as it stands.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics.Placed;

    /// <summary>The markers the problems draw as, where the text has carried them.</summary>
    public IReadOnlyList<Marker> Markers => diagnostics.Markers;

    /// <summary>Whether its server pushes the file's problems rather than being asked for them.</summary>
    private bool Pushes => session?.Server.Provides("diagnosticProvider") != true;

    internal void Connect(LanguageSession started)
    {
        if (Lsp is not null || disposed)
        {
            return;
        }
        session = started;
        Lsp = started.Open(document.Path, document.Text, Web.LanguageId);
        if (Tool.Lints && Pushes)
        {
            Lsp.DiagnosticsPushed += OnPushed;
            AwaitPush();
        }
        Refresh();
        Unreached();
    }

    /// <summary>The server cannot serve the file (it cannot run, or not this file's package): its checks wait for nothing of its.</summary>
    internal void Unreached()
    {
        waiting?.Dispose();
        waiting = null;
    }

    /// <summary>The problems asked for again (the server's refresh).</summary>
    public void Refresh() => Pull(TimeSpan.Zero);

    /// <summary>The fixes for the problems on the caret's line, then the file's whole fix where the tool has one beyond
    /// them; none from a tool that finds no problems there.</summary>
    public async Task<IReadOnlyList<LintFix>> FixesAsync(int caret, CancellationToken cancel = default)
    {
        if (Lsp is not { } lsp || !Tool.Lints)
        {
            return [];
        }
        int line = lsp.PositionOf(caret).Line;
        List<Diagnostic> here = [.. Diagnostics.Where(d => d.Range.Start.Line <= line && line <= d.Range.End.Line)];
        if (here.Count == 0)
        {
            return [];
        }
        // Another tool than ESLint answers only where the range asked reaches a problem: it is asked the caret's whole line.
        TextSnapshot text = document.Text.Snapshot;
        LineSpan caretLine = text.GetLine(text.GetLineFromOffset(caret));
        TextRange asked = Tool == WebTool.Eslint ? new TextRange(caret, 0) : TextRange.FromBounds(caretLine.Start, caretLine.End);
        IReadOnlyList<CodeAction> actions = await lsp.CodeActionsAsync(asked, here, cancel);
        List<LintFix> fixes = [.. actions.Where(a => a.Disabled is null && (a.Command is not null || a.Edit is not null || a.Data is not null))
            .Select(a => Tool.Named(a, this)).OrderBy(Tool.Rank)];
        // ESLint lists the file's own among a line's; another tool's is a source action of its own, its title Passenger's.
        if (Tool != WebTool.Eslint && Tool.FixAll is { } kind && (await lsp.SourceActionsAsync(kind, cancel)).FirstOrDefault(a => a.Disabled is null) is { } whole)
        {
            fixes.Add(new LintFix($"{Tool.Name}: Fix current file", whole, null, this));
        }
        return fixes;
    }

    /// <summary>The edit that fixes every problem the tool can fix in the file (its <c>source.fixAll</c> action, what
    /// <c>eslint --fix</c> would write), for the text as it is now; null when the server has none to give.</summary>
    public async Task<JsonElement?> FixAllAsync(CancellationToken cancel = default)
    {
        if (Lsp is not { } lsp || Tool.FixAll is not { } kind || (await lsp.SourceActionsAsync(kind, cancel)).FirstOrDefault() is not { } action)
        {
            return null;
        }
        return (await lsp.ResolveChosenAsync(action, null, cancel)).Edit;
    }

    /// <summary>The edit a fix carries, asked of its server where it left it for later (Biome's); null for one made by its
    /// command alone (ESLint's).</summary>
    public async Task<JsonElement?> EditOfAsync(LintFix fix, CancellationToken cancel = default) =>
        Lsp is { } lsp && fix.Action.Command is null ? (await lsp.ResolveChosenAsync(fix.Action, null, cancel)).Edit : fix.Action.Edit;

    /// <summary>Makes a fix's command, where it has one: the server's command runs, and asks <paramref name="run"/> to make its edit.</summary>
    public async Task RunAsync(LintFix fix, CommandRun run, CancellationToken cancel = default)
    {
        if (Lsp is null || session is not { } running || fix.Action.Command is not { } command)
        {
            return;
        }
        await running.Server.ExecuteCommandAsync(command, run, cancel);
    }

    /// <summary>A formatter's layout of the whole file as it stands; null where the tool lays none of it out (Biome's
    /// formatter turned off for it, or a kind of file it does not format).</summary>
    public async Task<IReadOnlyList<LspTextEdit>?> FormatAsync(CancellationToken cancel = default) =>
        Lsp is { } lsp && Tool.Formats ? await lsp.FormattedAsync(document.Indentation.Options, document.Conventions, cancel) : null;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        diagnostics.Stop();
        PushLanded();
        Unreached();
        document.Text.Changed -= OnChanged;
        document.Saved -= OnSaved;
        if (Lsp is { } lsp)
        {
            lsp.DiagnosticsPushed -= OnPushed;
            lsp.Dispose();
        }
        Lsp = null;
        if (Web.Tools.Remove(this) && Tool.Lints)
        {
            Web.ShowLint();
        }
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        diagnostics.Moved();
        if (Tool.Lints && Lsp is not null && Pushes)
        {
            AwaitPush();
        }
        Pull(settle);
    }

    private void OnSaved(object? sender, EventArgs e) => Lsp?.Saved();

    private void Pull(TimeSpan delay)
    {
        if (Lsp is not { } lsp || !Tool.Lints || Pushes)
        {
            return;
        }
        diagnostics.Pull(delay, async cancel =>
        {
            if (await diagnostics.PulledAsync(lsp, cancel) is { } pulled && lsp == Lsp)
            {
                Show(pulled);
            }
        });
    }

    private void Show(IReadOnlyList<Diagnostic> reported)
    {
        diagnostics.Take(reported, Marker);
        if (Web.Tools.Contains(this))
        {
            Web.ShowLint();
        }
    }

    /// <summary>Holds the file's checks until its server's push for the text as it stands.</summary>
    private void AwaitPush()
    {
        if (awaitingPush is not { Lapsed: false })
        {
            awaitingPush?.Dispose();
            awaitingPush = document.Checking(Tool.Name);
            BackgroundWork.Begin();
        }
        pushLimit?.Cancel();
        CancellationTokenSource limit = pushLimit = new CancellationTokenSource();
        IdeErrors.Observe(Task.Delay(Waits.Of(PushLimit), limit.Token).ContinueWith(waited =>
        {
            if (!waited.IsCanceled)
            {
                UiThread.Post(() =>
                {
                    if (pushLimit == limit && awaitingPush is { Lapsed: false } held)
                    {
                        pushLimit = null;
                        held.Lapse();
                        BackgroundWork.End();
                    }
                });
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default));
    }

    private void PushLanded()
    {
        pushLimit?.Cancel();
        pushLimit = null;
        if (awaitingPush is { } held)
        {
            awaitingPush = null;
            if (!held.Lapsed)
            {
                BackgroundWork.End();
            }
            held.Dispose();
        }
    }

    /// <summary>A push from the server, on its thread: taken on the window's where it answers the text as it stands (the
    /// version it answers told as it arrived, <see cref="LspDocument.Pushed"/>), which lets the file's checks go.</summary>
    private void OnPushed(object? sender, EventArgs e)
    {
        if (sender is LspDocument lsp)
        {
            UiThread.Post(() =>
            {
                if (lsp == Lsp && !disposed)
                {
                    (IReadOnlyList<Diagnostic> pushed, int version) = lsp.Pushed;
                    if (version == document.Text.Version)
                    {
                        Show(pushed);
                        PushLanded();
                    }
                }
            });
        }
    }

    /// <summary>A problem as "Tool: message (rule)", the rule's name its page's link, at the severity the configuration
    /// gives the rule, never faded as unnecessary (ESLint's server tags no-unused-vars so).</summary>
    private Marker? Marker(Diagnostic diagnostic, TextRange range)
    {
        if (DiagnosticMarks.Kind(diagnostic.Severity) is not { } k)
        {
            return null;
        }
        string? rule = diagnostic.CodeIsNumber ? null : diagnostic.Code;
        string message = rule is null ? $"{Tool.Name}: {diagnostic.Message}" : $"{Tool.Name}: {diagnostic.Message} ({rule})";
        return new Marker(range, k, message) { Link = rule is null ? null : diagnostic.CodePage, LinkText = rule };
    }

    public override string ToString() => $"{Tool.Name} for {document.Name}";
}
