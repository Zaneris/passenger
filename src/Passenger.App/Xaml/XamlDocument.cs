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

    /// <summary>Who checks the file, as an agent is told while its check is under way.</summary>
    internal const string Checker = "Passenger's XAML";

    private readonly XamlService service;
    private readonly Debounce analysing;
    private (int Version, XamlProject Project, XamlFile File)? last;
    private readonly AnchoredList<Marker> live = new(m => m.Range, (m, range) => m with { Range = range });
    private readonly AnchoredList<BuiltMarker> built = new(b => b.Marker.Range, (b, range) => b with { Marker = b.Marker with { Range = range } });

    /// <summary>What the language server found as the file was last checked: MAUI's source generator's problems, and C#'s
    /// in its expressions (<see cref="GeneratedMarker.Own"/>), which the file's own check leaves to it.</summary>
    private readonly AnchoredList<GeneratedMarker> generated = new(g => g.Marker.Range, (g, range) => g with { Marker = g.Marker with { Range = range } });

    /// <summary>The framework chosen for the file's multi-targeted project, whose inner build's problems alone are shown;
    /// null for a project of one.</summary>
    private string? framework;
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
        analysing = new Debounce(Settle, AnalyseAsync, () => document.Checking(Checker));
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
    public bool IsLoaded => Loading.Loaded is { IsCompletedSuccessfully: true } loaded && loaded.Result is not null;

    /// <summary>The problems last found in the file, each with its fix when it has one.</summary>
    public IReadOnlyList<XamlDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>The version of the text <see cref="Diagnostics"/> were found in; -1 before any check.</summary>
    public int DiagnosedVersion { get; private set; } = -1;

    /// <summary>Raised on the UI thread once a check's problems are the file's (<see cref="Diagnostics"/>).</summary>
    public event EventHandler? Checked;

    /// <summary>The localized keys its last check found a language lacks, where each is read in the text as it stands.</summary>
    internal IReadOnlyList<(TextRange Range, ResourceGap Gap)> Gaps => gaps.Items;

    /// <summary>Whether its last check found it reads localized strings, so a .resx or XLIFF file's change checks it again.</summary>
    internal bool ReadsLocalized { get; private set; }

    /// <summary>Whether its check asks the C# language server (a MAUI file's, whose source generator the server reruns over
    /// it), so the server's load of the projects checks it again.</summary>
    internal bool AsksServer { get; private set; }

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
        // Another framework's XAML in the project (a MAUI app's WinUI head) is not read as the project's.
        if (builder is null || builder.Dialect.LeavesOut(Path.GetRelativePath(builder.Folder, path).Replace('\\', '/')))
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
    internal static XamlFile Read(string path, string text, XamlProject project, string? variant)
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
        using IDisposable checking = Document.Checking(Checker);
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
        DiagnosedVersion = version;
        ReadsLocalized = localizes;
        gaps.Set(Document.Text, [.. found.Where(d => d.Gap is not null).Select(d => (d.Range, d.Gap!))]);
        live.Set(Document.Text, [.. found.Select(d => new Marker(d.Range, d.Severity switch { XamlSeverity.Error => MarkerKind.Error, XamlSeverity.Note => MarkerKind.Info, _ => MarkerKind.Warning }, d.Message)
        {
            StartGravity = AnchorGravity.After,
        })]);
        Show();
        Checked?.Invoke(this, EventArgs.Empty);
        await GeneratorCheckAsync(file, version);
        IReadOnlyList<ColourUse> colours = await BackgroundWork.Run(() => Colours.InXaml(file, file.Variant));
        if (!disposed && Document.Text.Version == version)
        {
            Document.ColourMarks = XamlService.Marks(Document.Text.Snapshot, colours);
        }
    }

    /// <summary>A MAUI file checked by the language server as its text stands (<see cref="Passenger.Lsp.RoslynSession.MauiChecksAsync"/>):
    /// MAUI's source generator run again over it, before any build, its problems placed on the attribute they name, and
    /// each C# expression's compiled as the generator writes it, placed in the expression. Nothing without a server loaded,
    /// until its load checks the file again (<see cref="XamlService.ServerLoaded"/>).</summary>
    private async Task GeneratorCheckAsync(XamlFile file, int version)
    {
        AsksServer = file.Dialect.ReadsExpressions;
        if (!AsksServer || service.Server?.Invoke() is not { IsLoaded: true } server)
        {
            return;
        }
        IReadOnlyList<XamlExpression> expressions = await BackgroundWork.Run(() => XamlExpressions.In(file));
        IReadOnlyList<Roslyn.Extension.MauiCheck>? checks;
        try
        {
            checks = await server.MauiChecksAsync(file.Path, file.Text, [.. expressions.Select(XamlService.Asked)]);
        }
        catch (Passenger.Lsp.LanguageServerFailed)
        {
            return;
        }
        if (checks is null || disposed || Document.Text.Version != version)
        {
            return;
        }
        TextSnapshot text = Document.Text.Snapshot;
        List<GeneratedMarker> found = [];
        HashSet<XmlAttribute> named = [];
        foreach (Roslyn.Extension.MauiCheck check in checks.Where(c => c.At is not null))
        {
            if (check.At!.Line >= text.LineCount)
            {
                continue;
            }
            LineSpan line = text.GetLine(check.At.Line);
            int at = line.Start + Math.Min(check.At.Character, line.Length);
            XmlPlace place = file.Tree.PlaceAt(at);
            TextRange range = place.Attribute is { } attribute ? attribute.HasValue ? attribute.ValueRange : attribute.NameRange : WordAt(text, at, line);
            if (place.Attribute is not null)
            {
                named.Add(place.Attribute);
            }
            found.Add(new GeneratedMarker(Marker.Coded(range, check.Error ? MarkerKind.Error : MarkerKind.Warning, check.Code, check.Message) with
            {
                StartGravity = AnchorGravity.After,
            }, false));
        }
        foreach (Roslyn.Extension.MauiCheck check in checks.Where(c => c.At is null && c.Expression >= 0 && c.Expression < expressions.Count))
        {
            XamlExpression expression = expressions[check.Expression];
            if (named.Contains(expression.Attribute))
            {
                continue;
            }
            int start = expression.ValueStart + check.Start;
            int end = Math.Max(expression.ValueStart + check.End, Math.Min(start + 1, expression.Attribute.ValueEnd));
            found.Add(new GeneratedMarker(Marker.Coded(TextRange.FromBounds(start, end), MarkerKind.Error, check.Code, check.Message) with
            {
                StartGravity = AnchorGravity.After,
            }, true));
        }
        generated.Set(Document.Text, found);
        Show();
    }

    /// <summary>The XAML compiler's problems in this file from the build that just ended, placed on their lines, those of
    /// a multi-targeted project's inner builds shown for the framework chosen (<paramref name="chosen"/>) alone; each goes
    /// once its line is edited, or with the next build.</summary>
    internal void Built(IEnumerable<Problem> problems, string? chosen)
    {
        framework = chosen;
        TextSnapshot text = Document.Text.Snapshot;
        List<BuiltMarker> found = [];
        foreach (Problem problem in problems)
        {
            if (problem.Position.Line >= text.LineCount)
            {
                continue;
            }
            LineSpan line = text.GetLine(problem.Position.Line);
            int start = line.Start + Math.Min(problem.Position.Column, line.Length);
            TextRange range = WordAt(text, start, line);
            found.Add(new BuiltMarker(Marker.Coded(range, problem.Severity == ProblemSeverity.Error ? MarkerKind.Error : MarkerKind.Warning, problem.Code, Plain(problem.Message)) with
            {
                StartGravity = AnchorGravity.After,
            }, problem.Framework));
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
        // Shown again only when a build's problem went, or when what is shown is none of this file's.
        if ((built.Count > 0 && DropEditedLines(e)) || (live.Count + built.Count + generated.Count == 0 && Document.Markers.Count > 0))
        {
            Show();
        }
        analysing.Restart();
    }

    /// <summary>A build's problem on a line edited since is the build's, no longer this text's: one on a line of the text as
    /// it stands that an edit of the change reached; whether there was one.</summary>
    private bool DropEditedLines(DocumentChangedEventArgs e)
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
        return built.Drop(b => edited.Contains(after.GetLineFromOffset(Math.Min(b.Marker.Range.Start, after.Length))));
    }

    /// <summary>The markers shown, where the text has carried them: what the check found, and the build's where the check
    /// found nothing on that line.</summary>
    private void Show()
    {
        TextSnapshot text = Document.Text.Snapshot;
        bool Clear(HashSet<int> lines, Marker marker) => marker.Range.Start > text.Length || !lines.Contains(text.GetLineFromOffset(marker.Range.Start));
        IReadOnlyList<Marker> found = live.Items;
        HashSet<int> lines = [.. found.Where(m => m.Range.Start <= text.Length).Select(m => text.GetLineFromOffset(m.Range.Start))];
        List<Marker> server = [.. generated.Items.Where(g => g.Own || Clear(lines, g.Marker)).Select(g => g.Marker)];
        lines.UnionWith(server.Where(m => m.Range.Start <= text.Length).Select(m => text.GetLineFromOffset(m.Range.Start)));
        Document.Markers = [.. found, .. server, .. built.Items.Where(b => b.Framework is null || framework is null || string.Equals(b.Framework, framework, StringComparison.OrdinalIgnoreCase))
            .Select(b => b.Marker).Where(m => Clear(lines, m))];
    }

    /// <summary>Another framework chosen for the file's project: its build's problems of that framework shown in place of the last's.</summary>
    internal void Choose(string? chosen)
    {
        if (!string.Equals(framework, chosen, StringComparison.OrdinalIgnoreCase))
        {
            framework = chosen;
            Show();
        }
    }

    /// <summary>A marker of a build's problem, with the framework of the inner build that found it (null for a build of one).</summary>
    private sealed record BuiltMarker(Marker Marker, string? Framework);

    /// <summary>A marker of a problem the language server found: C#'s in an expression (<paramref name="Own"/>), shown
    /// always, or MAUI's source generator's, shown where the file's own check found nothing on its line.</summary>
    private sealed record GeneratedMarker(Marker Marker, bool Own);

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
