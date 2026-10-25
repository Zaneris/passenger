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
/// boxes in the frame (WPF's), kept on their XAML as it is typed. A UI-thread object.</summary>
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
                host = drawing;
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
                host = started;
                break;
            default:
                Say($"{builder.Dialect.Name} XAML has no preview");
                break;
        }
    }

    /// <summary>The file's text as it stands, sent to the previewer to draw.</summary>
    public void Update()
    {
        UiThread.Check();
        if (!disposed)
        {
            host?.Update();
        }
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

    /// <summary>The previewer started again on the project's newest build, the last frame shown until the new one comes.</summary>
    /// <summary>Whether a previewer runs for the file, rather than the pane saying why none does.</summary>
    public bool Running => host is not null;

    public Task RestartAsync()
    {
        UiThread.Check();
        if (disposed)
        {
            return Task.CompletedTask;
        }
        Stop();
        return StartAsync();
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

        /// <summary>The previewer ended: its last lines written to passenger.log, its last said in the pane.</summary>
        public void Ended()
        {
            string last;
            string all;
            lock (said)
            {
                last = said.LastOrDefault() ?? "it said nothing";
                all = said.Count == 0 ? last : string.Join(" | ", said);
            }
            IdeErrors.Write($"The {previewer} previewer of {Path.GetFileName(preview.Document.Document.Path)} ended: {all}");
            preview.Let();
            preview.Say($"The previewer ended: {last}");
        }
    }
}

/// <summary>A previewer of one framework's XAML, run for one file: told when the file's text changes and when the
/// window's scale does; it puts what it draws on its <see cref="XamlPreview"/>.</summary>
internal interface IPreviewHost : IDisposable
{
    void Update();

    void Rescale(double scaling);
}

/// <summary>An element of the file as the frame draws it: its start tag and its whole markup in the file's text, and its
/// box in the frame, in the frame's units.</summary>
public sealed record PreviewElement(TextRange Tag, TextRange Whole, Rect Box);
