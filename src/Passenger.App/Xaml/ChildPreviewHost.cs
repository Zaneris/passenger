using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Media.Imaging;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Text;
using Passenger.Xaml.Projects;

namespace Passenger.App.Xaml;

/// <summary>A previewer of Passenger's own (WPF's, MAUI's) in a process of its own, run through ChildProcesses on a copy of
/// the project's last build (the state's <c>previews/</c>, so a build never meets its assemblies locked). Each request
/// goes as a JSON line with App.xaml's text, open or on disk; each answer comes back as one, a frame as a PNG with its
/// elements' boxes, the error that kept the XAML from loading, or why there is nothing to draw. What a previewer sends
/// and how it reads its answers is its own; the rest is here. A UI-thread object.</summary>
internal abstract class ChildPreviewHost : IPreviewHost
{
    private readonly Channel<Sent> outgoing = Channel.CreateUnbounded<Sent>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>The texts sent and not yet answered, by sequence: what an answer's boxes are placed on, read by the reader.</summary>
    private readonly ConcurrentDictionary<int, TextSnapshot> unanswered = new();
    private Process? process;
    private Stopwatch? starting;
    private int sequence;
    private bool disposed;

    /// <param name="previewer">Whose previewer it is, as the log names it.</param>
    protected ChildPreviewHost(XamlPreview preview, XamlProjectBuilder builder, PreviewedBuild build, string assembly, string shadow, string previewer)
    {
        Preview = preview;
        Builder = builder;
        Build = build;
        Assembly = assembly;
        Shadow = shadow;
        Log = new XamlPreview.PreviewerLog(preview, previewer);
    }

    public PreviewedBuild Build { get; }

    protected XamlPreview Preview { get; }

    protected XamlProjectBuilder Builder { get; }

    /// <summary>The project's assembly in the copy.</summary>
    protected string Assembly { get; }

    /// <summary>The copy's folder.</summary>
    protected string Shadow { get; }

    protected XamlPreview.PreviewerLog Log { get; }

    /// <summary>Where the builds previewed are copied, a folder per preview named for the IDE's process.</summary>
    private static string Copies => Path.Combine(StateStore.DefaultRoot, "previews");

    /// <summary>A build's folder copied for a previewer to load from, and the build as it stood; null, having said why, when
    /// it could not be copied.</summary>
    protected static async Task<(PreviewedBuild Build, string Shadow)?> CopyAsync(XamlPreview preview, string built)
    {
        preview.Say($"Copying {Path.GetFileName(built)}'s build for the previewer…");
        string shadow = Path.Combine(Copies, $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            PreviewedBuild copied = await BackgroundWork.Run(() =>
            {
                PreviewedBuild build = PreviewedBuild.Of(built, configured: false);
                Copy(Path.GetDirectoryName(built)!, shadow);
                return build;
            });
            return (copied, shadow);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Forget(shadow);
            preview.Say($"The previewer could not copy the build: {error.Message}");
            return null;
        }
    }

    /// <summary>The previewer started, its fork off the window's thread, held as background work until the frame it draws;
    /// the pane says <paramref name="said"/> meanwhile.</summary>
    protected async Task StartAsync(ProcessStartInfo start, string said)
    {
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process started;
        using (BackgroundWork.Scope())
        {
            started = await ChildProcesses.StartAsync(start);
        }
        process = started;
        Task errors = ChildProcesses.Hear(started, errors: Log.Heard);
        IdeErrors.Observe(errors);
        starting = Stopwatch.StartNew();
        // Long-lived readers of the previewer's streams: each answer holds the window's work itself (UiThread.Post).
        IdeErrors.Observe(BackgroundWork.Unheld(() => WriteAsync(started)));
        Task output = BackgroundWork.Unheld(() => ReadAsync(started));
        IdeErrors.Observe(output);
        AvaloniaPreviewHost.HearEnd(started, [errors, output], () => Ended(started));
        Preview.Say(said);
        Update();
    }

    /// <summary>Sends the file's text as it stands, with App.xaml's as it stands when it is open.</summary>
    public void Update()
    {
        if (disposed || process is null)
        {
            return;
        }
        sequence++;
        TextSnapshot text = Preview.Document.Document.Text.Snapshot;
        unanswered[sequence] = text;
        string? application = Builder.Current.Application?.Path;
        Preview.Hold();
        outgoing.Writer.TryWrite(new Sent(application, application is null ? null : Preview.Document.Service.OpenText(application)?.ToString(),
            Request(sequence, text)));
    }

    /// <summary>The window's scale changed: the file drawn again at it.</summary>
    public void Rescale(double scaling) => Update();

    /// <summary>A request of the file's text as it stands, made on the window's thread with what else it is drawn as; the
    /// line it returns is written off it, given App.xaml's path and text where the project has one.</summary>
    protected abstract Func<(string Path, string Xaml)?, string> Request(int sequence, TextSnapshot text);

    /// <summary>The previewer's answers, read from its output's UTF-8 bytes; a line that is not the protocol's goes to
    /// <paramref name="unread"/>.</summary>
    protected abstract IAsyncEnumerable<PreviewAnswer> AnswersAsync(Stream output, Action<string> unread);

    /// <summary>Writes each request off the UI thread: App.xaml read from disk when it is not open, and the text, which may
    /// be long, sent in order.</summary>
    private async Task WriteAsync(Process to)
    {
        try
        {
            await foreach (Sent request in outgoing.Reader.ReadAllAsync())
            {
                (string Path, string Xaml)? application = null;
                if (request.Application is { } path)
                {
                    string? text = request.ApplicationText;
                    if (text is null && File.Exists(path))
                    {
                        text = await File.ReadAllTextAsync(path);
                    }
                    application = text is null ? null : (path, text);
                }
                await to.StandardInput.WriteLineAsync(request.Line(application));
                await to.StandardInput.FlushAsync();
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The previewer ended, which Ended says.
        }
    }

    /// <summary>Reads the previewer's answers off the UI thread, each frame decoded and its boxes placed on the XAML it
    /// answers before it is shown.</summary>
    private async Task ReadAsync(Process from)
    {
        try
        {
            // A line that is not the protocol's is the previewer's own output.
            await foreach (PreviewAnswer answer in AnswersAsync(ChildProcesses.OutputStream(from), Log.Heard))
            {
                TextSnapshot? sent = unanswered.GetValueOrDefault(answer.Sequence);
                Bitmap? frame = null;
                IReadOnlyList<PreviewElement> elements = [];
                if (answer is { Kind: PreviewAnswerKind.Frame, Png: { } png })
                {
                    using MemoryStream bytes = new(png);
                    frame = new Bitmap(bytes);
                    elements = sent is null ? [] : XamlPreview.Placed(sent, answer.Boxes);
                }
                UiThread.Post(() => Received(answer, frame, sent, elements));
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The previewer ended, which Ended says.
        }
    }

    private void Received(PreviewAnswer answer, Bitmap? frame, TextSnapshot? sent, IReadOnlyList<PreviewElement> elements)
    {
        if (disposed)
        {
            return;
        }
        foreach (int answered in unanswered.Keys.Where(k => k <= answer.Sequence))
        {
            unanswered.TryRemove(answered, out _);
        }
        switch (answer.Kind)
        {
            case PreviewAnswerKind.Frame when frame is not null:
                if (starting is { } clock)
                {
                    starting = null;
                    IdeErrors.Write($"Preview of {Path.GetFileName(Preview.Document.Document.Path)}: first frame {frame.PixelSize.Width}x{frame.PixelSize.Height} after {clock.ElapsedMilliseconds} ms");
                }
                Preview.Frame = frame;
                Preview.FrameSize = new Size(frame.PixelSize.Width / answer.Scale, frame.PixelSize.Height / answer.Scale);
                // Boxes placed on a text since typed over would land on the wrong elements: the next frame brings them.
                Preview.Elements = sent == Preview.Document.Document.Text.Snapshot ? elements : [];
                Preview.Error = null;
                Preview.Status = answer.Notes is { Count: > 0 } notes
                    ? $"Live. {string.Join("; ", notes.Select(n => n.Message + XamlPreview.Where(sent ?? Preview.Document.Document.Text.Snapshot, n.Line, null)))}"
                    : "Live";
                break;
            case PreviewAnswerKind.Error:
                Preview.Error = $"{answer.Message}{XamlPreview.Where(sent ?? Preview.Document.Document.Text.Snapshot, answer.Line, answer.Column)}";
                Preview.Status = "The XAML did not load";
                break;
            default:
                Preview.Frame = null;
                Preview.Elements = [];
                Preview.Error = null;
                Preview.Status = answer.Message ?? "Nothing to draw";
                break;
        }
        if (answer.Sequence >= sequence)
        {
            Preview.Let();
        }
        Preview.Tell();
    }

    private void Ended(Process ended)
    {
        if (!disposed && ended == process)
        {
            Log.Ended(this, ended.ExitCode);
        }
    }

    /// <summary>A build's folder copied, its subfolders too (satellite assemblies, runtimes/), for the previewer to load from.</summary>
    private static void Copy(string from, string to)
    {
        SweepCopies();
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Copies left by an IDE that is no longer running (one that crashed, or could not delete them as it ended).</summary>
    private static void SweepCopies()
    {
        if (!Directory.Exists(Copies))
        {
            return;
        }
        foreach (string folder in Directory.EnumerateDirectories(Copies))
        {
            string name = Path.GetFileName(folder);
            int dash = name.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0 && int.TryParse(name[..dash], out int owner) && owner != Environment.ProcessId && !DotnetProcesses.Runs(owner))
            {
                Delete(folder);
            }
        }
    }

    private static void Delete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Still held by a previewer as it ends: the next sweep takes it.
        }
    }

    /// <summary>A copy let go once its previewer has let go of its files, off the UI thread; nothing drawn waits for it.</summary>
    private static void Forget(string folder, Process? ending = null) => IdeErrors.Observe(BackgroundWork.Unheld(async () =>
    {
        try
        {
            if (ending is not null)
            {
                await ending.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (TimeoutException)
        {
            // Deleted as far as it lets go; the rest is swept later.
        }
        finally
        {
            ending?.Dispose();
        }
        for (int attempt = 0; attempt < 5 && Directory.Exists(folder); attempt++)
        {
            Delete(folder);
            if (Directory.Exists(folder))
            {
                await Task.Delay(200);
            }
        }
    }));

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        outgoing.Writer.TryComplete();
        Process? running = process;
        process = null;
        if (running is not null)
        {
            ChildProcesses.Kill(running);
        }
        Forget(Shadow, running);
    }

    /// <summary>A request as the UI thread made it, for the writer to finish: App.xaml's path, and its text when it is open.</summary>
    private sealed record Sent(string? Application, string? ApplicationText, Func<(string Path, string Xaml)?, string> Line);
}

/// <summary>What a previewer answered, whichever it is: its kind, the request it answers, a frame's PNG, scale and boxes
/// (each by its element's line and column from 1 as XML readers count, in the frame's units), an error's message and
/// place, and what a frame was drawn without, each with the line of the element it is about where it has one.</summary>
internal sealed record PreviewAnswer(PreviewAnswerKind Kind, int Sequence)
{
    public byte[]? Png { get; init; }

    public double Scale { get; init; } = 1;

    public IReadOnlyList<(int Line, int Column, Rect Box)> Boxes { get; init; } = [];

    public string? Message { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }

    public IReadOnlyList<(string Message, int? Line)> Notes { get; init; } = [];
}

internal enum PreviewAnswerKind
{
    Frame,
    Error,
    Nothing,
}
