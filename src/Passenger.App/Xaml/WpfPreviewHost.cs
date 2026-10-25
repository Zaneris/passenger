using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Media.Imaging;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Text;
using Passenger.WpfPreview;
using Passenger.Xaml.Projects;
using Passenger.Xaml.Syntax;

namespace Passenger.App.Xaml;

/// <summary>A WPF file's design view, drawn by Passenger's own previewer (src/Passenger.WpfPreview, under
/// <c>wpfpreview/</c> on Windows) run through ChildProcesses by the machine's dotnet on the project's runtime
/// configuration (the previewer's own, rolling forward, for a class library or a project before .NET 8) and
/// dependencies, from a copy of its last build (the state's <c>previews/</c>, so a build never meets its assemblies
/// locked). Text goes as JSON lines with App.xaml's; a frame comes back as a PNG with its elements' boxes. A UI-thread
/// object.</summary>
internal sealed class WpfPreviewHost : IPreviewHost
{
    private readonly XamlPreview preview;
    private readonly XamlProjectBuilder builder;
    private readonly string assembly;
    private readonly string shadow;
    private readonly Channel<Sent> outgoing = Channel.CreateUnbounded<Sent>(new UnboundedChannelOptions { SingleReader = true });
    /// <summary>The texts sent and not yet answered, by sequence: what an answer's boxes are placed on, read by the reader.</summary>
    private readonly ConcurrentDictionary<int, TextSnapshot> unanswered = new();
    private readonly XamlPreview.PreviewerLog log;
    private Process? process;
    private Stopwatch? starting;
    private int sequence;
    private bool disposed;

    private WpfPreviewHost(XamlPreview preview, XamlProjectBuilder builder, string assembly, string shadow)
    {
        this.preview = preview;
        log = new XamlPreview.PreviewerLog(preview, "WPF");
        this.builder = builder;
        this.assembly = assembly;
        this.shadow = shadow;
    }

    /// <summary>Where the app carries the previewer.</summary>
    public static string Previewer => Path.Combine(AppContext.BaseDirectory, "wpfpreview", "Passenger.WpfPreview.dll");

    /// <summary>Where the builds previewed are copied, a folder per preview named for the IDE's process.</summary>
    private static string Copies => Path.Combine(StateStore.DefaultRoot, "previews");

    /// <summary>Starts the previewer on a copy of the project's last build; null, having said why, when there is none to
    /// start.</summary>
    public static async Task<WpfPreviewHost?> StartAsync(XamlPreview preview, XamlProjectBuilder builder)
    {
        ProjectInputs inputs = builder.Current.Inputs;
        if (XamlPreview.Output(inputs, configured: false) is not { } built)
        {
            preview.Say($"Build {Path.GetFileNameWithoutExtension(inputs.ProjectPath)} to preview its XAML: the previewer draws its last build");
            return null;
        }
        if (!File.Exists(Previewer))
        {
            preview.Say("WPF's previewer is missing from Passenger's folder (wpfpreview/)");
            return null;
        }
        preview.Say($"Copying {Path.GetFileName(built)}'s build for the previewer…");
        string shadow = Path.Combine(Copies, $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            await BackgroundWork.Run(() => Copy(Path.GetDirectoryName(built)!, shadow));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Forget(shadow);
            preview.Say($"The previewer could not copy the build: {error.Message}");
            return null;
        }
        WpfPreviewHost started = new(preview, builder, Path.Combine(shadow, Path.GetFileName(built)), shadow);
        try
        {
            await started.RunAsync(inputs);
            return started;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            started.Dispose();
            preview.Say($"The previewer could not start: {error.Message}");
            return null;
        }
    }

    /// <summary>The previewer started on the copy, its fork off the window's thread, held as background work until the
    /// frame it draws.</summary>
    private async Task RunAsync(ProjectInputs inputs)
    {
        string name = Path.GetFileNameWithoutExtension(assembly);
        string configuration = Path.Combine(shadow, name + ".runtimeconfig.json");
        // A class library has no runtime configuration, and one older than .NET 8 runs no previewer built for 8.
        bool own = !File.Exists(configuration) || XamlPreview.Framework(inputs.TargetFramework) is not { Major: >= 8 };
        if (own)
        {
            configuration = Path.ChangeExtension(Previewer, ".runtimeconfig.json");
        }
        List<string> arguments = ["exec", "--runtimeconfig", configuration];
        string dependencies = Path.Combine(shadow, name + ".deps.json");
        if (File.Exists(dependencies))
        {
            arguments.AddRange(["--depsfile", dependencies]);
        }
        // A class library's build leaves its packages where restore put them, which its dependencies name as a cache does.
        foreach (string cache in inputs.PackageFolders.Values.Select(f => Path.GetDirectoryName(Path.GetDirectoryName(f))).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            arguments.AddRange(["--additionalprobingpath", cache]);
        }
        arguments.AddRange([Previewer, "--assembly", assembly]);
        ProcessStartInfo start = ChildProcesses.DotnetCommand(arguments, shadow);
        start.Environment[RuntimeVariables.XamlDiagnosticsSourceInfo] = "1";
        if (own)
        {
            // Its own configuration rolls forward to the newest runtime, which a project on a preview of .NET needs.
            start.Environment[RuntimeVariables.RollForwardToPrerelease] = "1";
        }
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process started;
        using (BackgroundWork.Scope())
        {
            started = await ChildProcesses.StartAsync(start);
        }
        process = started;
        started.ErrorDataReceived += (_, e) => log.Heard(e.Data);
        started.Exited += (_, _) => UiThread.Post(() => Ended(started));
        started.BeginErrorReadLine();
        starting = Stopwatch.StartNew();
        // Long-lived readers of the previewer's streams: each answer holds the window's work itself (UiThread.Post).
        IdeErrors.Observe(Task.Run(() => WriteAsync(started)));
        IdeErrors.Observe(Task.Run(() => ReadAsync(started)));
        preview.Say($"Starting the previewer on {Path.GetFileName(assembly)}…");
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
        TextSnapshot text = preview.Document.Document.Text.Snapshot;
        unanswered[sequence] = text;
        string? application = builder.Current.Application?.Path;
        preview.Hold();
        outgoing.Writer.TryWrite(new Sent(sequence, preview.Document.Document.Path, text, application,
            application is null ? null : preview.Document.Service.OpenText(application)?.ToString(), preview.Scaling));
    }

    /// <summary>The window's scale changed: the file drawn again at it.</summary>
    public void Rescale(double scaling) => Update();

    /// <summary>Writes each request off the UI thread: App.xaml read from disk when it is not open, and the text, which may
    /// be long, sent in order.</summary>
    private async Task WriteAsync(Process to)
    {
        try
        {
            await foreach (Sent request in outgoing.Reader.ReadAllAsync())
            {
                WpfPreviewSource? application = null;
                if (request.Application is { } path)
                {
                    string? text = request.ApplicationText;
                    if (text is null && File.Exists(path))
                    {
                        text = await File.ReadAllTextAsync(path);
                    }
                    application = text is null ? null : new WpfPreviewSource(path, text, BaseUri(path));
                }
                string line = JsonSerializer.Serialize(new WpfPreviewRequest
                {
                    Sequence = request.Sequence,
                    Path = request.Path,
                    Xaml = request.Text.ToString(),
                    BaseUri = BaseUri(request.Path),
                    Application = application,
                    Scale = request.Scale,
                }, WpfPreviewJson.Options);
                await to.StandardInput.WriteLineAsync(line);
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
            await foreach (WpfPreviewReply reply in JsonLines.ReadAsync(from.StandardOutput, WpfPreviewJson.Reply, (line, _) => log.Heard(line)))
            {
                TextSnapshot? sent = unanswered.GetValueOrDefault(reply.Sequence);
                Bitmap? frame = null;
                IReadOnlyList<PreviewElement> elements = [];
                if (reply is { Kind: WpfPreviewKinds.Frame, Png: { } png })
                {
                    using MemoryStream bytes = new(png);
                    frame = new Bitmap(bytes);
                    elements = sent is null ? [] : Place(sent, reply.Elements ?? []);
                }
                UiThread.Post(() => Received(reply, frame, sent, elements));
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The previewer ended, which Ended says.
        }
    }

    private void Received(WpfPreviewReply reply, Bitmap? frame, TextSnapshot? sent, IReadOnlyList<PreviewElement> elements)
    {
        if (disposed)
        {
            return;
        }
        foreach (int answered in unanswered.Keys.Where(k => k <= reply.Sequence))
        {
            unanswered.TryRemove(answered, out _);
        }
        switch (reply.Kind)
        {
            case WpfPreviewKinds.Frame when frame is not null:
                if (starting is { } clock)
                {
                    starting = null;
                    IdeErrors.Write($"Preview of {Path.GetFileName(preview.Document.Document.Path)}: first frame {frame.PixelSize.Width}x{frame.PixelSize.Height} after {clock.ElapsedMilliseconds} ms");
                }
                preview.Frame = frame;
                preview.FrameSize = new Size(frame.PixelSize.Width / reply.Scale, frame.PixelSize.Height / reply.Scale);
                // Boxes placed on a text since typed over would land on the wrong elements: the next frame brings them.
                preview.Elements = sent == preview.Document.Document.Text.Snapshot ? elements : [];
                preview.Error = null;
                preview.Status = reply.Notes is { Count: > 0 } notes
                    ? $"Live. {string.Join("; ", notes.Select(n => n.Message + XamlPreview.Where(sent ?? preview.Document.Document.Text.Snapshot, n.Line, null)))}"
                    : "Live";
                break;
            case WpfPreviewKinds.Error:
                preview.Error = $"{reply.Message}{XamlPreview.Where(sent ?? preview.Document.Document.Text.Snapshot, reply.Line, reply.Column)}";
                preview.Status = "The XAML did not load";
                break;
            default:
                preview.Frame = null;
                preview.Elements = [];
                preview.Error = null;
                preview.Status = reply.Message ?? "Nothing to draw";
                break;
        }
        if (reply.Sequence >= sequence)
        {
            preview.Let();
        }
        preview.Tell();
    }

    /// <summary>The previewer's boxes on the elements of the text it drew: a box's line and column are its element's name
    /// (or its '&lt;'), as an XML reader places it, a lone '\r' ending a line (<see cref="Lsp.ServerLines.OfXml"/>).</summary>
    private static List<PreviewElement> Place(TextSnapshot text, IReadOnlyList<WpfPreviewBox> boxes)
    {
        XmlTree tree = XmlTree.Parse(text.ToString());
        Lsp.ServerLines lines = Lsp.ServerLines.OfXml(text);
        List<PreviewElement> placed = [];
        foreach (WpfPreviewBox box in boxes)
        {
            if (box.Line < 1 || box.Line > lines.LineCount)
            {
                continue;
            }
            int offset = lines.OffsetOf(box.Line - 1, Math.Max(0, box.Column - 1));
            XmlElement? element = tree.Elements.Where(e => e.Start <= offset && offset <= e.NameEnd).MaxBy(e => e.Start);
            if (element is not null)
            {
                placed.Add(new PreviewElement(TextRange.FromBounds(element.Start, element.StartTagEnd), TextRange.FromBounds(element.Start, element.End),
                    new Rect(box.X, box.Y, Math.Max(0, box.Width), Math.Max(0, box.Height))));
            }
        }
        return placed;
    }

    /// <summary>A file's pack URI in the project's assembly, which relative URIs in it resolve against as in the build.</summary>
    private string BaseUri(string path)
    {
        ProjectInputs inputs = builder.Current.Inputs;
        string relative = Path.GetRelativePath(inputs.Folder, path).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal))
        {
            relative = Path.GetFileName(path);
        }
        return $"pack://application:,,,/{Uri.EscapeDataString(inputs.AssemblyName)};component/{string.Join('/', relative.Split('/').Select(Uri.EscapeDataString))}";
    }

    private void Ended(Process ended)
    {
        if (!disposed && ended == process)
        {
            log.Ended();
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
            if (dash > 0 && int.TryParse(name[..dash], out int owner) && owner != Environment.ProcessId && !Running(owner))
            {
                Delete(folder);
            }
        }
    }

    private static bool Running(int id)
    {
        try
        {
            using Process found = Process.GetProcessById(id);
            return !found.HasExited;
        }
        catch (Exception missing) when (missing is ArgumentException or InvalidOperationException)
        {
            return false;
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
    private static void Forget(string folder, Process? ending = null) => IdeErrors.Observe(Task.Run(async () =>
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
        Forget(shadow, running);
    }

    /// <summary>A request as the UI thread made it, for the writer to finish: App.xaml's text when it is open.</summary>
    private sealed record Sent(int Sequence, string Path, TextSnapshot Text, string? Application, string? ApplicationText, double Scale);
}
