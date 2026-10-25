using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Designer;
using Avalonia.Remote.Protocol.Viewport;
using RemoteFormat = Avalonia.Remote.Protocol.Viewport.PixelFormat;
using Passenger.Core;
using Passenger.Text;
using Passenger.Xaml.Projects;

namespace Passenger.App.Xaml;

/// <summary>An Avalonia file's design view, drawn by the project's Avalonia.Designer.HostApp (in its Avalonia package,
/// MIT) run through ChildProcesses on the project's last build: its frames come over Avalonia.Remote.Protocol to a port
/// of Passenger's on loopback, and the file's text as it stands goes to it as it is typed. A UI-thread object: the
/// protocol's messages cross to it through <see cref="UiThread"/>.</summary>
internal sealed class AvaloniaPreviewHost : IPreviewHost
{
    private readonly XamlPreview preview;
    private readonly string output;
    private readonly XamlPreview.PreviewerLog log;
    private IDisposable? listening;
    private IAvaloniaRemoteTransportConnection? connection;
    private Process? host;
    private bool disposed;
    private Stopwatch? starting;
    private bool empty;
    private double drawnAt;

    /// <summary>The texts sent and not yet answered, in order, as the host answers them: what an answer's line is read in.</summary>
    private readonly Queue<TextSnapshot> unanswered = new();

    private AvaloniaPreviewHost(XamlPreview preview, string output)
    {
        this.preview = preview;
        log = new XamlPreview.PreviewerLog(preview, "Avalonia");
        this.output = output;
    }

    /// <summary>Starts the host on the project's last build; null, having said why, when there is none to start.</summary>
    public static async Task<AvaloniaPreviewHost?> StartAsync(XamlPreview preview, ProjectInputs inputs)
    {
        if (XamlPreview.Output(inputs) is not { } built)
        {
            preview.Say($"Build {Path.GetFileNameWithoutExtension(inputs.ProjectPath)} to preview its XAML: the previewer draws its last build");
            return null;
        }
        if (HostApp(inputs) is not { } designer)
        {
            string why = NoHostApp(inputs);
            IdeLog.Write(IdeLog.Ide, $"Preview of {Path.GetFileName(preview.Document.Document.Path)}: {why}");
            preview.Say(why);
            return null;
        }
        AvaloniaPreviewHost started = new(preview, built);
        try
        {
            await started.RunAsync(designer);
            return started;
        }
        catch (Exception error) when (error is SocketException or IOException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            started.Dispose();
            preview.Say($"The previewer could not start: {error.Message}");
            return null;
        }
    }

    /// <summary>The host started on the build, its fork off the window's thread, held as background work until the
    /// frame it draws.</summary>
    private async Task RunAsync(string designer)
    {
        int port = FreePort();
        listening = new BsonTcpTransport().Listen(IPAddress.Loopback, port, UiThread.Marshal<IAvaloniaRemoteTransportConnection>(Connected));
        string folder = Path.GetDirectoryName(output)!;
        string name = Path.GetFileNameWithoutExtension(output);
        ProcessStartInfo start = ChildProcesses.DotnetCommand(["exec", "--runtimeconfig", Path.Combine(folder, name + ".runtimeconfig.json"),
            "--depsfile", Path.Combine(folder, name + ".deps.json"), designer, "--transport", $"tcp-bson://127.0.0.1:{port}/", "--method", "avalonia-remote",
            "--session-id", Guid.NewGuid().ToString("N"), output], folder);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process started;
        using (BackgroundWork.Scope())
        {
            started = await ChildProcesses.StartAsync(start);
        }
        host = started;
        started.OutputDataReceived += (_, e) => log.Heard(e.Data);
        started.ErrorDataReceived += (_, e) => log.Heard(e.Data);
        started.Exited += (_, _) => UiThread.Post(() => Ended(started));
        started.BeginOutputReadLine();
        started.BeginErrorReadLine();
        starting = Stopwatch.StartNew();
        preview.Say($"Starting the previewer on {Path.GetFileName(output)}…");
        preview.Hold();
    }

    public void Update()
    {
        if (connection is not { } open || disposed)
        {
            return;
        }
        preview.Hold();
        TextSnapshot text = preview.Document.Document.Text.Snapshot;
        unanswered.Enqueue(text);
        IdeErrors.Observe(open.Send(new UpdateXamlMessage { Xaml = text.ToString(), AssemblyPath = output, XamlFileProjectPath = preview.ProjectPath() }));
    }

    public void Rescale(double scaling)
    {
        if (connection is { } open && !disposed)
        {
            IdeErrors.Observe(open.Send(new ClientRenderInfoMessage { DpiX = 96 * scaling, DpiY = 96 * scaling }));
        }
    }

    private void Connected(IAvaloniaRemoteTransportConnection opened)
    {
        if (disposed)
        {
            opened.Dispose();
            return;
        }
        connection = opened;
        opened.OnMessage += (_, message) => UiThread.Post(() => Received(opened, message));
        opened.OnException += (_, error) => UiThread.Post(() => preview.Say($"The previewer's connection failed: {error.Message}"));
        opened.Start();
        double dpi = 96 * preview.Scaling;
        IdeErrors.Observe(opened.Send(new ClientSupportedPixelFormatsMessage { Formats = [RemoteFormat.Bgra8888, RemoteFormat.Rgba8888] }));
        IdeErrors.Observe(opened.Send(new ClientRenderInfoMessage { DpiX = dpi, DpiY = dpi }));
        Update();
    }

    private void Received(IAvaloniaRemoteTransportConnection from, object message)
    {
        if (disposed || from != connection)
        {
            return;
        }
        switch (message)
        {
            case RequestViewportResizeMessage resize:
                // Never answered: an answer lays out a root sized to its content again, which asks again.
                empty = resize.Width < 1 || resize.Height < 1;
                break;
            case UpdateXamlResultMessage result:
                TextSnapshot answered = unanswered.TryDequeue(out TextSnapshot? sent) ? sent : preview.Document.Document.Text.Snapshot;
                preview.Error = result.Error is { Length: > 0 } ? Describe(result, answered) : null;
                if (preview.Error is not null)
                {
                    preview.Let();
                }
                else if (preview.Frame is null || Math.Abs(drawnAt - (96 * preview.Scaling)) > 0.5)
                {
                    // The host's window takes its scale from what arrives once it exists; a reload keeps the one it had.
                    double scale = 96 * preview.Scaling;
                    IdeErrors.Observe(from.Send(new ClientRenderInfoMessage { DpiX = scale, DpiY = scale }));
                }
                preview.Status = preview.Error is null ? "Live" : "The XAML did not load";
                if (preview.Error is null && empty)
                {
                    preview.Frame = null;
                    preview.Status = "Nothing to draw: the root is 0 × 0 (hidden, or sized to content with none and no d:DesignWidth and d:DesignHeight)";
                    preview.Let();
                }
                preview.Tell();
                break;
            case FrameMessage frame:
                Draw(frame);
                if (starting is { } clock)
                {
                    starting = null;
                    IdeErrors.Write($"Preview of {Path.GetFileName(preview.Document.Document.Path)}: first frame {frame.Width}x{frame.Height} after {clock.ElapsedMilliseconds} ms");
                }
                IdeErrors.Observe(from.Send(new FrameReceivedMessage { SequenceId = frame.SequenceId }));
                preview.Status = "Live";
                preview.Tell();
                preview.Let();
                break;
        }
    }

    /// <summary>A frame's pixels in a bitmap the pane shows, row by row as its stride lays them out.</summary>
    private void Draw(FrameMessage frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Data is null)
        {
            return;
        }
        Avalonia.Platform.PixelFormat format = frame.Format == RemoteFormat.Rgba8888 ? Avalonia.Platform.PixelFormat.Rgba8888 : Avalonia.Platform.PixelFormat.Bgra8888;
        WriteableBitmap bitmap = new(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), format, AlphaFormat.Premul);
        using (ILockedFramebuffer target = bitmap.Lock())
        {
            int row = frame.Width * 4;
            for (int y = 0; y < frame.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(frame.Data, y * frame.Stride, target.Address + (y * target.RowBytes), row);
            }
        }
        preview.Frame = bitmap;
        drawnAt = frame.DpiX;
        preview.FrameSize = new Size(frame.Width * 96 / frame.DpiX, frame.Height * 96 / frame.DpiY);
    }

    /// <summary>What the host said of a XAML it could not load, with the line it names in the text it was sent, as the editor
    /// counts that text's lines (<see cref="XamlPreview.Where"/>), in place of the compiler's its message repeats.</summary>
    private static string Describe(UpdateXamlResultMessage result, TextSnapshot sent)
    {
        if (result.Exception is { } details)
        {
            string message = details.LineNumber is null ? details.Message : XamlDocument.Plain(details.Message);
            return $"{details.ExceptionType}: {message}{XamlPreview.Where(sent, details.LineNumber, details.LinePosition)}";
        }
        return result.Error.Split('\n')[0].Trim();
    }

    private void Ended(Process ended)
    {
        if (!disposed && ended == host)
        {
            log.Ended();
        }
    }

    /// <summary>Avalonia's previewer host in the project's Avalonia package: the newest of its tools/ frameworks that is not
    /// newer than the project's.</summary>
    private static string? HostApp(ProjectInputs inputs)
    {
        if (!inputs.PackageFolders.TryGetValue("Avalonia", out string? package) || !Directory.Exists(Path.Combine(package, "tools")))
        {
            return null;
        }
        Version target = XamlPreview.Framework(inputs.TargetFramework) ?? new Version(99, 0);
        return Directory.EnumerateDirectories(Path.Combine(package, "tools"))
            .Select(d => (Folder: d, Version: ToolsFramework(Path.GetFileName(d))))
            .Where(d => d.Version is not null && d.Version <= target)
            .OrderByDescending(d => d.Version)
            .Select(d => Path.Combine(d.Folder, "designer", "Avalonia.Designer.HostApp.dll"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Why the project has no previewer host, naming where it was looked for.</summary>
    private static string NoHostApp(ProjectInputs inputs)
    {
        string project = Path.GetFileNameWithoutExtension(inputs.ProjectPath);
        if (!inputs.PackageFolders.TryGetValue("Avalonia", out string? package))
        {
            return inputs.AssetsFile is { } assets
                ? $"{project}'s restore ({PathNames.Shown(assets, null)}) names no Avalonia package in the package cache: restore {project}, then build it"
                : $"{project} has not been restored (no project.assets.json): build it to preview its XAML";
        }
        string tools = Path.Combine(package, "tools");
        string[] frameworks = Directory.Exists(tools) ? [.. Directory.EnumerateDirectories(tools).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)] : [];
        return $"No previewer for {inputs.TargetFramework ?? project} in {PathNames.Shown(tools, null)} (it has {(frameworks.Length == 0 ? "none" : string.Join(", ", frameworks))}): designer/Avalonia.Designer.HostApp.dll is looked for in each";
    }

    /// <summary>A tools/ folder's framework: net5.0 and later as themselves, and netstandard or netcoreapp (Avalonia 11's
    /// host, which the project's runtime runs) below them all; null for .NET Framework's.</summary>
    private static Version? ToolsFramework(string folder) =>
        XamlPreview.Framework(folder)
        ?? (folder.StartsWith("netstandard", StringComparison.Ordinal) || folder.StartsWith("netcoreapp", StringComparison.Ordinal) ? new Version(1, 0) : null);

    private static int FreePort()
    {
        TcpListener free = new(IPAddress.Loopback, 0);
        free.Start();
        int port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        return port;
    }

    public void Dispose()
    {
        disposed = true;
        connection?.Dispose();
        connection = null;
        listening?.Dispose();
        listening = null;
        if (host is { } running)
        {
            host = null;
            ChildProcesses.Kill(running);
            running.Dispose();
        }
    }
}
