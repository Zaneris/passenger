using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.Web;

/// <summary>One open JavaScript, TypeScript, HTML, CSS, JSON, Dockerfile or YAML file's share of its language server:
/// kept in step, marked with the diagnostics the server pulls or pushes (moved with the text between them), and, where
/// the server finds colours (CSS's), a swatch in the gutter beside each line that writes one. Of one server only: a
/// server started again gives the file a new one, so what asks the server asks <see cref="WebLanguages.For"/> each
/// time.</summary>
public sealed class WebDocument : IServerDocument, IDisposable
{
    /// <summary>How long typing must pause before diagnostics and colours are asked for again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    private readonly OpenDocument document;
    private readonly ServerDiagnostics diagnostics;
    private LanguageSession? session;

    /// <summary>The file's checks held while its server starts: until it is connected, or cannot start.</summary>
    private IDisposable? waiting;

    /// <summary>The file's checks held, for the Dockerfile and YAML servers, which push a file's diagnostics after each
    /// change of it, from the change until that push lands or <see cref="PushLimit"/> passes: what an agent's diagnostics
    /// and a screenshot wait for.</summary>
    private IDisposable? awaitingPush;
    private CancellationTokenSource? pushLimit;

    private static readonly TimeSpan PushLimit = TimeSpan.FromSeconds(5);

    /// <summary>The file's markers from the container engine's checks (BuildKit's of a Dockerfile, Compose's of a compose
    /// file), which the editor shows with its own server's and ESLint's.</summary>
    private readonly AnchoredList<Marker> engine = new(m => m.Range, (m, range) => m with { Range = range });

    internal WebDocument(OpenDocument document, WebServerKind kind, string languageId)
    {
        this.document = document;
        diagnostics = new ServerDiagnostics(document, holdsChecks: true);
        Kind = kind;
        LanguageId = languageId;
        // What a server before this one marked moves with the text until this one says otherwise.
        diagnostics.Adopt(document.Markers);
        document.Text.Changed += OnChanged;
        document.Saved += OnSaved;
        waiting = document.Checking();
    }

    /// <summary>The server cannot start (no Node.js, too old, missing): the file waits for no checks of its.</summary>
    internal void Unreached()
    {
        waiting?.Dispose();
        waiting = null;
    }

    public WebServerKind Kind { get; }

    /// <summary>The language the server is told the file is in (<c>typescriptreact</c>, <c>scss</c>, <c>jsonc</c>).</summary>
    public string LanguageId { get; }

    public LspDocument? Lsp { get; private set; }

    /// <summary>The server's characters after which completion opens unasked, none until it has started.</summary>
    public IReadOnlyList<string> CompletionTriggers => session?.Server.CompletionTriggers ?? [];

    /// <summary>Whether the server answers a request (its capability, as <c>renameProvider</c>): the JSON server has no
    /// definitions or renames, which Passenger then gives itself.</summary>
    public bool Provides(string capability) => session?.Server.Provides(capability) == true;

    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics.Placed;

    /// <summary>ESLint's share of the file, while ESLint checks it.</summary>
    public EslintDocument? Lint { get; set; }

    /// <summary>ESLint's problems changed (<see cref="EslintDocument.Markers"/>): shown with the server's own.</summary>
    public void ShowLint() => document.Markers = Shown();

    /// <summary>The container engine's checks of the file as it stands, shown with the server's own
    /// (<see cref="Containers.EngineChecks"/>).</summary>
    public void ShowEngine(IReadOnlyList<Marker> markers)
    {
        engine.Set(document.Text, markers);
        document.Markers = Shown();
    }

    /// <summary>Every source's markers where the text has carried them, but an engine's on a line the server already marks
    /// as gravely: the engine's checks and the server's often find the same problem, so the line is marked once.</summary>
    private IReadOnlyList<Marker> Shown()
    {
        IReadOnlyList<Marker> own = diagnostics.Markers;
        IReadOnlyList<Marker> lint = Lint?.Markers ?? [];
        if (engine.Count == 0)
        {
            return [.. own, .. lint];
        }
        TextSnapshot text = document.Text.Snapshot;
        int Line(Marker m) => text.GetLineFromOffset(Math.Min(m.Range.Start, text.Length));
        HashSet<(int, MarkerKind)> marked = [.. own.Select(m => (Line(m), m.Kind))];
        return [.. own, .. lint, .. engine.Items.Where(m => !marked.Contains((Line(m), m.Kind)))];
    }

    internal void Connect(LanguageSession started)
    {
        if (Lsp is not null)
        {
            return;
        }
        session = started;
        Lsp = started.Open(document.Path, document.Text, LanguageId);
        Lsp.DiagnosticsPushed += OnPushed;
        AwaitPush();
        Refresh();
        Unreached();
    }

    /// <summary>Holds the file's checks until its server's push for the text as it stands (<see cref="awaitingPush"/>).</summary>
    private void AwaitPush()
    {
        if (Lsp is null || Kind is not (WebServerKind.Dockerfile or WebServerKind.Yaml))
        {
            return;
        }
        if (awaitingPush is null)
        {
            awaitingPush = document.Checking();
            BackgroundWork.Begin();
        }
        pushLimit?.Cancel();
        CancellationTokenSource limit = pushLimit = new CancellationTokenSource();
        IdeErrors.Observe(Task.Delay(PushLimit, limit.Token).ContinueWith(waited =>
        {
            if (!waited.IsCanceled)
            {
                UiThread.Post(() =>
                {
                    if (pushLimit == limit)
                    {
                        PushLanded();
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
            held.Dispose();
            BackgroundWork.End();
        }
    }

    /// <summary>The diagnostics and colours asked for again (the server's refresh, a server started again).</summary>
    public void Refresh() => Pull(TimeSpan.Zero);

    public void Dispose()
    {
        diagnostics.Stop();
        engine.Clear();
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
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        diagnostics.Moved();
        // What it shows is every marker of its own server's and ESLint's, so with none shown only the engine's can move.
        if (document.Markers.Count + engine.Count > 0)
        {
            document.Markers = Shown();
        }
        AwaitPush();
        Pull(Settle);
    }

    private void OnSaved(object? sender, EventArgs e) => Lsp?.Saved();

    /// <summary>A push from the server, on its thread: taken on the window's, if it answers the text as it stands (the
    /// version it answers told as it arrived, <see cref="LspDocument.Pushed"/>), which lets the file's checks go; one for
    /// a text typed over leaves them held for the next.</summary>
    private void OnPushed(object? sender, EventArgs e)
    {
        if (sender is LspDocument lsp)
        {
            UiThread.Post(() =>
            {
                if (lsp == Lsp)
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

    private void Pull(TimeSpan delay)
    {
        // A server that pushes its diagnostics and finds no colours (TypeScript's) has nothing to be asked.
        if (Lsp is not { } lsp || !(Provides("diagnosticProvider") || Provides("colorProvider")))
        {
            return;
        }
        diagnostics.Pull(delay, cancel => PullAsync(lsp, cancel));
    }

    /// <summary>The file's diagnostics where its server is asked for them (the rest push theirs), then its colours where it
    /// finds them, each for the text as it stands.</summary>
    private async Task PullAsync(LspDocument lsp, CancellationToken cancel)
    {
        if (session?.Server.Provides("diagnosticProvider") == true && await diagnostics.PulledAsync(lsp, cancel) is { } pulled)
        {
            Show(pulled);
        }
        if (session?.Server.Provides("colorProvider") == true)
        {
            (IReadOnlyList<ColorInformation> colours, int coloured) = await lsp.ColoursAsync(cancel);
            if (!cancel.IsCancellationRequested && coloured == document.Text.Version)
            {
                document.ColourMarks = Swatches(lsp, colours);
            }
        }
    }

    private void Show(IReadOnlyList<Diagnostic> reported)
    {
        // The server's own codes stay with the diagnostics, which its quick fixes are asked for by.
        diagnostics.Take(reported, (d, range) => DiagnosticMarks.Marker(Named(d), range));
        document.Markers = Shown();
    }

    /// <summary>A diagnostic with its code before its message: TypeScript's as TS2322 (from its own server, or the HTML
    /// server's script), none for the rest, whose codes are lint rules' names or the JSON server's numbers.</summary>
    private static Diagnostic Named(Diagnostic diagnostic)
    {
        string? code = diagnostic.Code is { ValueKind: System.Text.Json.JsonValueKind.Number } number && diagnostic.Source is "typescript" or "ts" or "js"
            ? $"TS{number}" : null;
        return new Diagnostic
        {
            Range = diagnostic.Range,
            Severity = diagnostic.Severity,
            Code = code is null ? null : System.Text.Json.JsonSerializer.SerializeToElement(code),
            CodeDescription = diagnostic.CodeDescription,
            Source = diagnostic.Source,
            Message = diagnostic.Message,
            Tags = diagnostic.Tags,
            Data = diagnostic.Data,
        };
    }

    /// <summary>A swatch in the gutter for each line that writes a colour, the first on the line.</summary>
    private static IReadOnlyList<ColourMark> Swatches(LspDocument lsp, IReadOnlyList<ColorInformation> colours) =>
        [.. colours
            .Select(c => (Line: lsp.Text.Snapshot.GetLineFromOffset(Math.Min(lsp.OffsetOf(c.Range.Start), lsp.Text.Snapshot.Length)), c.Color))
            .DistinctBy(c => c.Line)
            .Select(c => new ColourMark(c.Line, Avalonia.Media.Color.FromArgb(Byte(c.Color.Alpha), Byte(c.Color.Red), Byte(c.Color.Green), Byte(c.Color.Blue))))];

    private static byte Byte(double component) => (byte)Math.Clamp(Math.Round(component * 255), 0, 255);
}
