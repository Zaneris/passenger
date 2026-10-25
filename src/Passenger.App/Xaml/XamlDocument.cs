using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Features;
using Passenger.Xaml.Localization;
using Passenger.Xaml.Projects;
using Passenger.Xaml.Syntax;

namespace Passenger.App.Xaml;

/// <summary>One open XAML file's share of its project's XAML: its text read (off the UI thread, once per version) against
/// the project as it stands, for completion and the rest as it is typed, and its problems marked once typing pauses,
/// with the XAML compiler's from the last build on their lines until those lines change, and its localized strings a language
/// lacks (<see cref="XamlLocalization"/>), the .resx and XLIFF files read as the editor has them. The project's load may still be
/// under way as the file opens; what is asked meanwhile waits for it, and a file of a project whose XAML Passenger does
/// not read (neither Avalonia's nor WPF's) answers nothing.</summary>
public sealed class XamlDocument : IDisposable
{
    /// <summary>How long typing pauses before the file is checked again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private readonly XamlService service;
    private readonly Debounce analysing;
    private (int Version, XamlProject Project, XamlFile File)? last;
    private readonly AnchoredList<Marker> live = new(m => m.Range, (m, range) => m with { Range = range });
    private readonly AnchoredList<Marker> built = new(m => m.Range, (m, range) => m with { Range = range });
    private readonly AnchoredList<(TextRange Range, ResourceGap Gap)> gaps = new(g => g.Range, (g, range) => (range, g.Gap));
    private bool disposed;

    /// <summary>Whether a check is due that has not begun (the text, its project or a .resx it reads changed), and the one
    /// under way, which an agent's call waits for.</summary>
    private bool due = true;
    private Task checking = Task.CompletedTask;

    internal XamlDocument(XamlService service, OpenDocument document, XamlLoading loading)
    {
        this.service = service;
        Document = document;
        Loading = loading;
        analysing = new Debounce(Settle, AnalyseAsync, document.Checking);
        document.Text.Changed += OnChanged;
        IdeErrors.Observe(AnalyseAsync());
    }

    public OpenDocument Document { get; }

    /// <summary>The session's XAML, which knows the other open files.</summary>
    internal XamlService Service => service;

    internal XamlLoading Loading { get; private set; }

    /// <summary>The project's first read; its answer null for a project whose XAML Passenger does not read.</summary>
    public Task<XamlProject?> Loaded => Loading.Loaded;

    /// <summary>Whether the project's types have been read and are XAML Passenger reads.</summary>
    public bool IsLoaded => Loading.Loaded.IsCompletedSuccessfully && Loading.Loaded.Result is not null;

    /// <summary>The problems last found in the file, each with its fix when it has one.</summary>
    public IReadOnlyList<XamlDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>The localized keys its last check found a language lacks, where each is read in the text as it stands.</summary>
    internal IReadOnlyList<(TextRange Range, ResourceGap Gap)> Gaps => gaps.Items;

    /// <summary>Whether its last check found it reads localized strings, so a .resx or XLIFF file's change checks it again.</summary>
    internal bool ReadsLocalized { get; private set; }

    /// <summary>The file checked again as it stands, as typing pausing checks it (a language's file it reads was written).</summary>
    internal void Recheck()
    {
        due = true;
        analysing.Restart();
    }

    /// <summary>The file's check as it stands: run now where one is due, else the one under way waited for, so what it found
    /// (an agent's fixes on it) is the text's.</summary>
    internal Task CheckedAsync()
    {
        if (due && !disposed)
        {
            analysing.Stop();
            return AnalyseAsync();
        }
        return checking;
    }

    /// <summary>The file read as it stands, against the project as it stands: the last read when neither changed since;
    /// null for a project whose XAML Passenger does not read. A read of the project begun anew while the last was awaited
    /// (its restore landed) is awaited in turn, never its builder taken before it has loaded.</summary>
    public async Task<XamlFile?> FileAsync()
    {
        TextSnapshot text = Document.Text.Snapshot;
        int version = Document.Text.Version;
        string path = Document.Path;
        string? variant = Avalonia.Application.Current?.ActualThemeVariant.Key?.ToString();
        XamlLoading loading;
        XamlProjectBuilder? builder;
        do
        {
            loading = Loading;
            builder = await loading.Loaded is null ? null : await loading.Builder;
        }
        while (loading != Loading && !disposed);
        if (builder is null)
        {
            return null;
        }
        XamlProject project = builder.Current;
        if (last is { } kept && kept.Version == version && kept.Project == project && kept.File.Variant == variant)
        {
            return kept.File;
        }
        XamlFile file = await BackgroundWork.Run(() => Read(path, text.ToString(), project, variant));
        if (Document.Text.Version == version)
        {
            last = (version, project, file);
        }
        return file;
    }

    /// <summary>A parse made a file of the project, the project's summary of it taken from this text.</summary>
    private static XamlFile Read(string path, string text, XamlProject project, string? variant)
    {
        XmlTree tree = XmlTree.Parse(text);
        return new XamlFile(path, tree, project.With(XamlFileSummary.Of(path, tree, project.ResolveSource))) { Variant = variant };
    }

    /// <summary>What completion offers at a caret, for the text as it stands.</summary>
    public async Task<XamlCompletionList?> CompleteAsync(int caret, CancellationToken cancel)
    {
        if (!Loading.Loaded.IsCompleted)
        {
            service.Say($"Reading the types {Path.GetFileNameWithoutExtension(Loading.ProjectPath)}'s XAML can use…");
        }
        XamlFile? file = await FileAsync();
        cancel.ThrowIfCancellationRequested();
        return file is null ? null : await BackgroundWork.Run(() => XamlCompletion.At(file, caret), cancel);
    }

    /// <summary>The file checked as it stands, its markers replaced by what is found, unless it changed meanwhile; a check
    /// that fails is reported, as nothing awaits it (a Debounce's work).</summary>
    private Task AnalyseAsync() => checking = CheckedOrReportedAsync();

    private async Task CheckedOrReportedAsync()
    {
        try
        {
            due = false;
            await CheckAsync();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            IdeErrors.Report(error);
        }
    }

    private async Task CheckAsync()
    {
        using IDisposable held = BackgroundWork.Scope();
        using IDisposable checking = Document.Checking();
        int version = Document.Text.Version;
        XamlFile? file = await FileAsync();
        if (file is null || disposed)
        {
            return;
        }
        if (await Loading.Builder is { } builder && await BackgroundWork.Run(() => builder.XamlTyped(file.Path, file.Tree)) is not null && !disposed)
        {
            service.Summarised(this);
        }
        Func<string, TextSnapshot?> open = service.OpenSnapshots();
        (IReadOnlyList<XamlDiagnostic> found, bool localizes) = await BackgroundWork.Run(() =>
        {
            IReadOnlyList<LocalizedStatic> reads = XamlLocalization.Reads(file);
            return ((IReadOnlyList<XamlDiagnostic>)[.. XamlDiagnostics.Of(file), .. XamlLocalization.Findings(reads, open)], reads.Count > 0);
        });
        if (disposed || Document.Text.Version != version)
        {
            return;
        }
        Diagnostics = found;
        ReadsLocalized = localizes;
        gaps.Set(Document.Text, [.. found.Where(d => d.Gap is not null).Select(d => (d.Range, d.Gap!))]);
        live.Set(Document.Text, [.. found.Select(d => new Marker(d.Range, d.Severity == XamlSeverity.Error ? MarkerKind.Error : MarkerKind.Warning, d.Message))]);
        Show();
        IReadOnlyList<ColourUse> colours = await BackgroundWork.Run(() => Colours.InXaml(file, file.Variant));
        if (!disposed && Document.Text.Version == version)
        {
            Document.ColourMarks = XamlService.Marks(Document.Text.Snapshot, colours);
        }
    }

    /// <summary>The XAML compiler's problems in this file from the build that just ended, placed on their lines; each
    /// goes once its line is edited, or with the next build.</summary>
    internal void Built(IEnumerable<Problem> problems)
    {
        TextSnapshot text = Document.Text.Snapshot;
        List<Marker> found = [];
        foreach (Problem problem in problems)
        {
            if (problem.Position.Line >= text.LineCount)
            {
                continue;
            }
            LineSpan line = text.GetLine(problem.Position.Line);
            int start = line.Start + Math.Min(problem.Position.Column, line.Length);
            TextRange range = WordAt(text, start, line);
            string code = problem.Code is { Length: > 0 } c ? $"{c}: " : string.Empty;
            found.Add(new Marker(range, problem.Severity == ProblemSeverity.Error ? MarkerKind.Error : MarkerKind.Warning, code + Plain(problem.Message)));
        }
        built.Set(Document.Text, found);
        Show();
    }

    /// <summary>The name a compiler's place points at (its tag's or attribute's), or the rest of its line.</summary>
    private static TextRange WordAt(TextSnapshot text, int start, LineSpan line)
    {
        int at = start;
        if (at < line.End && text[at] == '<')
        {
            at++;
        }
        int end = at;
        while (end < line.End && MarkupNames.IsNameChar(text[end]))
        {
            end++;
        }
        return end > at ? TextRange.FromBounds(at, end) : TextRange.FromBounds(start, Math.Max(start, line.End));
    }

    /// <summary>A XAML compiler's message without the place it repeats at its end (Avalonia's "Line 8, position 6.", WPF's
    /// MarkupCompile's "Line 8 Position 6.").</summary>
    internal static string Plain(string message)
    {
        int at = message.LastIndexOf(" Line ", StringComparison.Ordinal);
        return at > 0 && message.IndexOf(" position ", at, StringComparison.OrdinalIgnoreCase) > at ? message[..at].TrimEnd() : message;
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        due = true;
        if (built.Count > 0)
        {
            DropEditedLines(e);
        }
        if (live.Count + built.Count > 0 || Document.Markers.Count > 0)
        {
            Show();
        }
        analysing.Restart();
    }

    /// <summary>A build's problem on a line edited since is the build's, no longer this text's: one on a line of the text as
    /// it stands that an edit of the change reached.</summary>
    private void DropEditedLines(DocumentChangedEventArgs e)
    {
        TextSnapshot after = e.After;
        HashSet<int> edited = [];
        int shift = 0;
        foreach (TextEdit edit in e.Edits)
        {
            int start = Math.Min(edit.Start + shift, after.Length);
            int last = after.GetLineFromOffset(Math.Min(start + edit.Text.Length, after.Length));
            for (int line = after.GetLineFromOffset(start); line <= last; line++)
            {
                edited.Add(line);
            }
            shift += edit.Text.Length - edit.Length;
        }
        built.Drop(m => edited.Contains(after.GetLineFromOffset(Math.Min(m.Range.Start, after.Length))));
    }

    /// <summary>The markers shown, where the text has carried them: what the check found, and the build's where the check
    /// found nothing on that line.</summary>
    private void Show()
    {
        TextSnapshot text = Document.Text.Snapshot;
        IReadOnlyList<Marker> found = live.Items;
        HashSet<int> lines = [.. found.Where(m => m.Range.Start <= text.Length).Select(m => text.GetLineFromOffset(m.Range.Start))];
        Document.Markers = [.. found, .. built.Items.Where(m => m.Range.Start > text.Length || !lines.Contains(text.GetLineFromOffset(m.Range.Start)))];
    }

    /// <summary>The project was read again (its C# changed, another XAML file did): the file is checked against it.</summary>
    internal void ProjectChanged()
    {
        last = null;
        Recheck();
    }

    /// <summary>The project is being loaded again (its project file or restore changed).</summary>
    internal void Follow(XamlLoading reloading)
    {
        Loading = reloading;
        last = null;
        Recheck();
    }

    public void Dispose()
    {
        disposed = true;
        analysing.Stop();
        Document.Text.Changed -= OnChanged;
        live.Clear();
        built.Clear();
        gaps.Clear();
        last = null;
    }
}
