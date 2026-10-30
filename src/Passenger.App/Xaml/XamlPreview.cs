using Avalonia;
using Avalonia.Media.Imaging;
using Passenger.Core;
using Passenger.Text;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Projects;

namespace Passenger.App.Xaml;

/// <summary>One XAML file's design view, drawn from its project's last build by the previewer of its framework: Avalonia's
/// own (<see cref="AvaloniaPreviewHost"/>) or, on Windows, Passenger's own WPF previewer (<see cref="WpfPreviewHost"/>),
/// each in a process of its own, sent the file's text as it stands. What the pane shows is here, whichever draws it: the
/// last frame, what the preview is doing, the error that kept the XAML from loading, and the file's elements with their
/// boxes in the frame (WPF's), kept on their XAML as it is typed. Its previewer rests while the preview is not shown,
/// the last frame kept, and starts again as it is shown. A UI-thread object.</summary>
public sealed class XamlPreview : IDisposable
{
    /// <summary>How long an update waits for the previewer's answer before what it held is let go.</summary>
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(60);

    private readonly XamlDocument document;
    private IPreviewHost? host;
    private IDisposable? waiting;
    private bool disposed;

    /// <summary>Counts starts, so one overtaken by another (a build finishing while it copied) lets its previewer go.</summary>
    private int starts;

    /// <summary>Whether the last previewer ended by itself, so the next edit starts another.</summary>
    private bool lapsed;

    /// <summary>A previewer that ended before its start handed it over, and what the pane says of its end.</summary>
    private (IPreviewHost Host, string Words)? endedEarly;

    /// <summary>Whether the previewer rests, ended while the preview is not shown, to start as it is shown again.</summary>
    private bool resting;
    private double scaling = 1;

    /// <summary>The elements on the ranges of their XAML and of their tags, which the text carries, and the elements as last
    /// read from both.</summary>
    private readonly AnchoredList<PreviewElement> wholes = new(e => e.Whole, (e, range) => e with { Whole = range });
    private readonly AnchoredList<TextRange> tags = new(r => r, (_, range) => range);
    private (IReadOnlyList<PreviewElement> Wholes, IReadOnlyList<TextRange> Tags, IReadOnlyList<PreviewElement> Read) elements = ([], [], []);

    public XamlPreview(XamlDocument document) => this.document = document;

    /// <summary>The frame, its status, its error or its elements changed; raised on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The last frame drawn, its pixels at 96 DPI whatever it was drawn at, since an image control draws a
    /// bitmap's size in its own units from its pixels.</summary>
    public Bitmap? Frame { get; internal set; }

    /// <summary>The frame's size in the window's units: its pixels over the scale it was drawn at.</summary>
    public Size FrameSize { get; internal set; }

    /// <summary>What the preview is doing, or why it shows nothing.</summary>
    public string Status { get; internal set; } = "Starting the previewer…";

    /// <summary>What the previewer said of the XAML it could not load, with its line; null when the last load went well.</summary>
    public string? Error { get; internal set; }

    /// <summary>The file's elements the frame draws, parents before children, each with its XAML (carried through what was
    /// typed since) and its box in the frame; empty when the previewer gives none (Avalonia's) or the text drawn is gone.</summary>
    public IReadOnlyList<PreviewElement> Elements
    {
        get
        {
            IReadOnlyList<PreviewElement> carried = wholes.Items;
            IReadOnlyList<TextRange> tagged = tags.Items;
            if (!ReferenceEquals(carried, elements.Wholes) || !ReferenceEquals(tagged, elements.Tags))
            {
                elements = (carried, tagged, [.. carried.Select((element, i) => element with { Tag = tagged[i] })]);
            }
            return elements.Read;
        }
        internal set
        {
            wholes.Set(document.Document.Text, value);
            tags.Set(document.Document.Text, [.. value.Select(e => e.Tag)]);
        }
    }

    internal XamlDocument Document => document;

    /// <summary>Starts the previewer of the file's framework on the project's last build, once the project's XAML has been
    /// read; or says why there is none.</summary>
    public async Task StartAsync()
    {
        UiThread.Check();
        int start = ++starts;
        lapsed = false;
        if (await document.Loading.Builder is not { } builder || await document.Loaded is null || disposed)
        {
            Say("Previews are for the XAML of an Avalonia or WPF project");
            return;
        }
        if (start != starts)
        {
            return;
        }
        switch (builder.Dialect)
        {
            case AvaloniaDialect:
                IPreviewHost? drawing = await AvaloniaPreviewHost.StartAsync(this, builder.Current.Inputs);
                if (disposed || start != starts)
                {
                    drawing?.Dispose();
                    return;
                }
                Adopt(drawing);
                break;
            case WpfDialect when !OperatingSystem.IsWindows():
                // WPF draws with Windows' own graphics, which no other system has: said in its place, rather than a pane that never draws.
                Say("WPF XAML previews only on Windows, where WPF draws");
                break;
            case WpfDialect:
                IPreviewHost? started = await WpfPreviewHost.StartAsync(this, builder);
                if (disposed || start != starts)
                {
                    started?.Dispose();
                    return;
                }
                Adopt(started);
                break;
            default:
                Say($"{builder.Dialect.Name} XAML has no preview");
                break;
        }
    }

    /// <summary>The file's text as it stands, sent to the previewer to draw, or to a new one where the last ended.</summary>
    public void Update()
    {
        UiThread.Check();
        if (disposed || resting)
        {
            return;
        }
        if (lapsed)
        {
            IdeErrors.Observe(StartAsync());
            return;
        }
        host?.Update();
    }

    /// <summary>The previewer a start handed over, let go of at once where it has ended already.</summary>
    private void Adopt(IPreviewHost? started)
    {
        host = started;
        (IPreviewHost Host, string Words)? early = endedEarly;
        endedEarly = null;
        if (started is not null && early is { } end && end.Host == started)
        {
            Lapsed(started, end.Words);
        }
    }

    /// <summary>A previewer ended by itself: let go of, the pane saying how, so the next edit or change of the project
    /// starts another.</summary>
    internal void Lapsed(IPreviewHost ended, string words)
    {
        if (disposed)
        {
            return;
        }
        if (host != ended)
        {
            endedEarly = (ended, words);
            return;
        }
        Stop();
        lapsed = true;
        Say(words);
    }

    /// <summary>The scale the pane's window draws at, which the previewer is asked to draw at too.</summary>
    public double Scaling
    {
        get => scaling;
        set
        {
            if (Math.Abs(value - scaling) < 0.01)
            {
                return;
            }
            scaling = value;
            if (!disposed)
            {
                host?.Rescale(value);
            }
        }
    }

    /// <summary>The deepest of the file's elements drawn at a point of the frame, in its units; null for none.</summary>
    public PreviewElement? ElementAt(Point point) => Elements.LastOrDefault(e => e.Box.Contains(point));

    /// <summary>The innermost of the file's drawn elements whose XAML holds an offset; null for none.</summary>
    public PreviewElement? ElementAround(int offset) =>
        Elements.Where(e => e.Whole.Start <= offset && offset < e.Whole.End).OrderBy(e => e.Whole.Length).FirstOrDefault();

    /// <summary>Whether a previewer runs for the file, rather than the pane saying why none does.</summary>
    public bool Running => host is not null;

    /// <summary>The previewer started again on the project's newest build, the last frame shown until the new one comes;
    /// one resting starts as it is shown.</summary>
    public Task RestartAsync()
    {
        UiThread.Check();
        if (disposed || resting)
        {
            return Task.CompletedTask;
        }
        Stop();
        return StartAsync();
    }

    /// <summary>The preview is no longer shown: its previewer ends, a start under way letting its own go, and the last frame
    /// stays for when it is shown again.</summary>
    public void Rest()
    {
        UiThread.Check();
        if (disposed || resting)
        {
            return;
        }
        resting = true;
        lapsed = false;
        starts++;
        Stop();
        Say("The previewer rests while the preview is hidden");
    }

    /// <summary>The preview is shown again: a previewer that rested starts on the project's newest build.</summary>
    public Task WakeAsync()
    {
        UiThread.Check();
        if (disposed || !resting)
        {
            return Task.CompletedTask;
        }
        resting = false;
        Say("Starting the previewer…");
        return StartAsync();
    }

    /// <summary>A build succeeded: a previewer whose build it changed (or with a newer one beside it) starts again on the
    /// newest while the preview is <paramref name="shown"/>, and rests while it is not; one whose build stands goes on, and
    /// a preview with no previewer is started where shown.</summary>
    public async Task BuiltAsync(bool shown)
    {
        UiThread.Check();
        if (disposed || resting)
        {
            return;
        }
        if (host is { } running && await document.Loading.Builder is { } builder)
        {
            ProjectInputs inputs = builder.Current.Inputs;
            PreviewedBuild drawn = running.Build;
            if (await BackgroundWork.Run(() => drawn.Stands(inputs)) || host != running)
            {
                return;
            }
        }
        if (shown)
        {
            await RestartAsync();
        }
        else
        {
            Rest();
        }
    }

    /// <summary>Says what the preview is doing, or why it shows nothing.</summary>
    internal void Say(string status)
    {
        Status = status;
        Tell();
    }

    internal void Tell() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Holds screenshots and tests for the previewer's answer to what was just sent, a while at most.</summary>
    internal void Hold()
    {
        if (waiting is not null)
        {
            return;
        }
        IDisposable held = BackgroundWork.Scope();
        waiting = held;
        IdeErrors.Observe(Task.Delay(Answer).ContinueWith(_ => UiThread.Post(() =>
        {
            if (waiting == held)
            {
                Let();
            }
        }), TaskScheduler.Default));
    }

    internal void Let()
    {
        waiting?.Dispose();
        waiting = null;
    }

    /// <summary>The file's path in its project, as the previewers read it (<c>/Views/MainWindow.axaml</c>).</summary>
    internal string ProjectPath()
    {
        string folder = document.Loading.Folder;
        return "/" + Path.GetRelativePath(folder, document.Document.Path).Replace('\\', '/');
    }

    /// <summary>The project's newest build output that can run: its assembly with its dependencies beside it, and its runtime
    /// configuration when <paramref name="configured"/> (a class library has none), where MSBuild's evaluation says its
    /// build puts it, under its own bin/ or under an artifacts folder above it.</summary>
    public static string? Output(ProjectInputs inputs, bool configured = true)
    {
        string name = inputs.AssemblyName + ".dll";
        List<string> found = [];
        if (inputs.TargetPath is { } evaluated && File.Exists(evaluated))
        {
            found.Add(evaluated);
        }
        string bin = Path.Combine(inputs.Folder, "bin");
        if (Directory.Exists(bin))
        {
            found.AddRange(Directory.EnumerateFiles(bin, name, SearchOption.AllDirectories));
        }
        string project = Path.GetFileNameWithoutExtension(inputs.ProjectPath);
        for (DirectoryInfo? at = new(inputs.Folder); at is not null; at = at.Parent)
        {
            string artifacts = Path.Combine(at.FullName, "artifacts", "bin", project);
            if (Directory.Exists(artifacts))
            {
                found.AddRange(Directory.EnumerateFiles(artifacts, name, SearchOption.AllDirectories));
                break;
            }
        }
        return found.Where(f => (!configured || File.Exists(Path.ChangeExtension(f, ".runtimeconfig.json"))) && File.Exists(Path.ChangeExtension(f, ".deps.json"))
                && !f.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    /// <summary>Where a previewer says the XAML it was sent did not load, " (line N, column M)" from one, as the editor counts
    /// that text's lines; empty with no line. Both previewers read the file as an XML reader does, a lone '\r' ending a line,
    /// so below one theirs run ahead of the editor's (<see cref="Lsp.ServerLines.OfXml"/>).</summary>
    internal static string Where(TextSnapshot sent, int? line, int? column)
    {
        if (line is not int reported || reported < 1)
        {
            return string.Empty;
        }
        TextPosition at = sent.GetPosition(Lsp.ServerLines.OfXml(sent).OffsetOf(reported - 1, Math.Max(0, (column ?? 1) - 1)));
        return column is null ? $" (line {at.Line + 1})" : $" (line {at.Line + 1}, column {at.Column + 1})";
    }

    /// <summary>A target framework's .NET version (net8.0-windows is 8.0); null for one before .NET 5 or none.</summary>
    internal static Version? Framework(string? moniker) =>
        moniker is not null && moniker.StartsWith("net", StringComparison.Ordinal) && Version.TryParse(moniker[3..].Split('-')[0], out Version? version) && version.Major >= 5
            ? version
            : null;

    private void Stop()
    {
        Let();
        host?.Dispose();
        host = null;
    }

    public void Dispose()
    {
        disposed = true;
        Stop();
        wholes.Clear();
        tags.Clear();
    }

    /// <summary>What a previewer's process last said, its last five lines, heard on any thread: once it ends, they go to
    /// passenger.log with its end, and the pane says the last of them.</summary>
    /// <param name="previewer">Whose previewer it is, as the log names it: Avalonia or WPF.</param>
    internal sealed class PreviewerLog(XamlPreview preview, string previewer)
    {
        private readonly Queue<string> said = new();

        /// <summary>A line the previewer wrote, kept for when it ends.</summary>
        public void Heard(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }
            lock (said)
            {
                said.Enqueue(line);
                while (said.Count > 5)
                {
                    said.Dequeue();
                }
            }
        }

        /// <summary>The previewer ended by itself: its exit code and last lines written to passenger.log, the preview let go
        /// of it, and its exit code and last line said in the pane.</summary>
        public void Ended(IPreviewHost host, int exitCode)
        {
            string last;
            string all;
            lock (said)
            {
                last = said.LastOrDefault() ?? "it said nothing";
                all = said.Count == 0 ? last : string.Join(" | ", said);
            }
            string how = ExitCodes.Said(exitCode);
            IdeErrors.Write($"The {previewer} previewer of {Path.GetFileName(preview.Document.Document.Path)} ended with {how}: {all}");
            preview.Lapsed(host, $"The previewer ended with {how}: {last}");
        }
    }
}

/// <summary>A previewer of one framework's XAML, run for one file: told when the file's text changes and when the
/// window's scale does; it puts what it draws on its <see cref="XamlPreview"/>.</summary>
internal interface IPreviewHost : IDisposable
{
    /// <summary>The project's build it draws from, as the build left it.</summary>
    PreviewedBuild Build { get; }

    void Update();

    void Rescale(double scaling);
}

/// <summary>A build a previewer draws from: its assembly, found as <see cref="XamlPreview.Output"/> finds it
/// (<paramref name="Configured"/>), and the newest write among the files beside it, which a build of the project or of one
/// it references changes.</summary>
internal sealed record PreviewedBuild(string Assembly, DateTime Written, bool Configured)
{
    /// <summary>The build at an assembly as it stands; read off the window's thread.</summary>
    public static PreviewedBuild Of(string assembly, bool configured) => new(assembly, Newest(Path.GetDirectoryName(assembly)!), configured);

    /// <summary>Whether this is still the project's newest build, unwritten since; read off the window's thread.</summary>
    public bool Stands(ProjectInputs inputs) =>
        Written != DateTime.MinValue && XamlPreview.Output(inputs, Configured) is { } newest && PathNames.Same(newest, Assembly)
            && Newest(Path.GetDirectoryName(newest)!) == Written;

    /// <summary>The newest write among a folder's files; the least time where it cannot be read, which no build stands as.</summary>
    private static DateTime Newest(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}

/// <summary>An element of the file as the frame draws it: its start tag and its whole markup in the file's text, and its
/// box in the frame, in the frame's units.</summary>
public sealed record PreviewElement(TextRange Tag, TextRange Whole, Rect Box);
