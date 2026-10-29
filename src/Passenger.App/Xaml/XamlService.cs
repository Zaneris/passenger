using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Xaml;
using Passenger.Xaml.Features;
using Passenger.Xaml.Projects;

namespace Passenger.App.Xaml;

/// <summary>XAML for a folder's session: each project with XAML Passenger reads (an Avalonia or WPF project) loaded the first time one of
/// its files opens, wholly off the UI thread (its project file and restore read, its assemblies and C#), and kept current
/// as its C# and XAML change (typed in an open file, or written on disk); every open XAML file given a
/// <see cref="XamlDocument"/>. A UI-thread object: the loads and reads run on the thread pool, each project's builder
/// taking its own lock.</summary>
public sealed class XamlService : IDisposable
{
    /// <summary>How long typing in a C# file pauses before the XAML that names its types reads it again.</summary>
    private static readonly TimeSpan SourceSettle = TimeSpan.FromMilliseconds(500);

    /// <summary>The folder of the <c>dotnet</c> that builds, whose packs hold the framework's reference assemblies; found
    /// once, off the UI thread, as finding it may wait for the login shell's environment.</summary>
    private static readonly Lazy<string?> DotnetRoot = new(() =>
    {
        try
        {
            return Path.GetDirectoryName(Dotnet.Host());
        }
        catch (FileNotFoundException)
        {
            // No SDK: the framework's reference assemblies are not read, the packages' still are.
            return null;
        }
    });

    private readonly Dictionary<string, XamlLoading> projects = new(PathNames.Comparer);
    private readonly Dictionary<OpenDocument, XamlDocument> documents = [];
    private readonly Dictionary<OpenDocument, TypedSource> sources = [];
    private readonly List<SourcesPending> pending = [];
    private bool disposed;

    /// <summary>The next text of an open C# file held by a test (<see cref="HoldNextText"/>): what it waits for, and done
    /// once it waits.</summary>
    private (Task Until, TaskCompletionSource Holding)? textHeld;

    /// <summary>A project's types or XAML files were read again; raised on the UI thread.</summary>
    public event EventHandler<XamlProject>? ProjectChanged;

    /// <summary>Something to say in the status bar (a project's XAML still loading).</summary>
    public event EventHandler<string>? Message;

    /// <summary>Where a library's own files come from (a theme's XAML for go to declaration): its Source Link unless
    /// set.</summary>
    public ILibraryFiles LibraryFiles { get; set; } = new SourceLink();

    /// <summary>What MSBuild's evaluation says of a project, by its path, asked from any thread: where its assets file,
    /// packages and build are, which a project's XAML and preview take before what its folders suggest.</summary>
    public Func<string, ProjectEvaluation?>? Evaluations { get; set; }

    /// <summary>The projects whose evaluation moved their restore or build are read again, so their XAML and preview take
    /// what it now names.</summary>
    public void EvaluationsChanged(IReadOnlyCollection<string> projectPaths)
    {
        UiThread.Check();
        foreach (XamlLoading loading in projects.Values.Where(l => projectPaths.Contains(l.ProjectPath, PathNames.Comparer)).ToList())
        {
            Reload(loading);
        }
    }

    /// <summary>The open files' texts as the editor has them now, each a snapshot any thread reads, for the .resx and XLIFF
    /// files a XAML file's localized strings are read from off the UI thread; none open without it.</summary>
    public Func<Func<string, TextSnapshot?>>? Snapshots { get; set; }

    /// <summary>The open files' texts as the editor has them now (<see cref="Snapshots"/>), taken on the UI thread.</summary>
    internal Func<string, TextSnapshot?> OpenSnapshots()
    {
        UiThread.Check();
        return Snapshots?.Invoke() ?? (_ => null);
    }

    /// <summary>A .resx or XLIFF file changed: each open XAML file that reads localized strings is checked again; no other.</summary>
    public void LocalizationChanged()
    {
        UiThread.Check();
        foreach (XamlDocument document in documents.Values.Where(d => d.ReadsLocalized))
        {
            document.Recheck();
        }
    }

    /// <summary>Whether a path is a XAML file Passenger reads, by its name.</summary>
    public static bool IsXaml(string path) => Languages.Language.ForPath(path) == Languages.Language.Xaml;

    /// <summary>An open file's XAML, null for a file that is not XAML of a C# project.</summary>
    public XamlDocument? For(OpenDocument document) => documents.GetValueOrDefault(document);

    public IReadOnlyCollection<XamlDocument> Documents => documents.Values;

    /// <summary>An open XAML file's text as it stands, by its path; null when it is not open.</summary>
    public TextSnapshot? OpenText(string path)
    {
        UiThread.Check();
        return documents.Keys.FirstOrDefault(d => PathNames.Same(d.Path, path))?.Text.Snapshot;
    }

    /// <summary>Gives an open XAML file of a C# project its share of the project's XAML, and follows an open C# file as it
    /// is typed, so the XAML naming its types sees them.</summary>
    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        if (disposed || documents.ContainsKey(document) || sources.ContainsKey(document))
        {
            return;
        }
        if (IsXaml(document.Path) && !PathNames.Within(document.Path, LibrarySources.Folder))
        {
            if (ProjectOf(document.Path) is { } loading)
            {
                XamlDocument xaml = new(this, document, loading);
                documents[document] = xaml;
                foreach (SourcesPending change in pending)
                {
                    change.Hold(xaml);
                }
            }
            return;
        }
        if (document.Language == Languages.Language.CSharp)
        {
            // Only a file a XAML project may read waits for a pause: each wait holds background work half a second.
            Debounce wait = new(SourceSettle, () => SourceTypedAsync(document), () => Pending(Reading(document.Path)));
            EventHandler<DocumentChangedEventArgs> handler = (_, _) =>
            {
                if (Reading(document.Path).Any())
                {
                    wait.Restart();
                }
            };
            document.Text.Changed += handler;
            document.Parsed += OnParsed;
            sources[document] = new TypedSource(handler, wait);
        }
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.Remove(document, out XamlDocument? xaml))
        {
            xaml.Dispose();
        }
        if (sources.Remove(document, out TypedSource? source))
        {
            document.Text.Changed -= source.Handler;
            document.Parsed -= OnParsed;
            source.Wait.Stop();
        }
    }

    /// <summary>Files changed on disk: a project's C# or XAML read again where it changed, and a project file or restore
    /// (its assets) loading the whole project again.</summary>
    public void FilesChanged(IEnumerable<string> paths)
    {
        UiThread.Check();
        foreach (IGrouping<XamlLoading, string> changed in paths.SelectMany(p => OwnersOf(p).Select(owner => (Path: p, Owner: owner)))
            .GroupBy(p => p.Owner, p => p.Path))
        {
            XamlLoading loading = changed.Key;
            List<string> files = [.. changed];
            if (files.Any(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) == "project.assets.json"))
            {
                Reload(loading);
                continue;
            }
            HashSet<string> open = [.. documents.Keys.Select(d => d.Path)];
            Dictionary<string, (TypedSource Source, TextSnapshot Text, int Version)> typed = new(StringComparer.Ordinal);
            foreach ((OpenDocument document, TypedSource source) in sources)
            {
                typed[document.Path] = (source, document.Text.Snapshot, document.Text.Version);
            }
            IdeErrors.Observe(UpdateAsync(loading, builder =>
            {
                XamlProject? made = null;
                foreach (string file in files)
                {
                    if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        // An open file's types are its editor's text, which the disk's may be behind; a file deleted goes.
                        made = (typed.TryGetValue(file, out (TypedSource Source, TextSnapshot Text, int Version) editor) && File.Exists(file)
                            ? editor.Source.Give(builder, file, editor.Text, editor.Version)
                            : builder.SourceChanged(file, null)) ?? made;
                    }
                    else if (builder.Owns(file) && !open.Contains(file))
                    {
                        made = builder.XamlChanged(file, null);
                    }
                }
                return made;
            }));
        }
    }

    /// <summary>An open XAML file's resources, includes or names changed as it was typed: the project's other open files
    /// are checked again against them.</summary>
    internal void Summarised(XamlDocument changed)
    {
        foreach (XamlDocument document in documents.Values.Where(d => d != changed && d.Loading.ProjectPath == changed.Loading.ProjectPath))
        {
            document.ProjectChanged();
        }
    }

    /// <summary>A build ended: each open XAML file takes the XAML compiler's problems in it, in place of the last build's.</summary>
    public void Built(IReadOnlyList<Run.Problem> problems)
    {
        UiThread.Check();
        foreach (XamlDocument document in documents.Values)
        {
            document.Built(problems.Where(p => p.Path is { } path && PathNames.Same(path, document.Document.Path)));
        }
    }

    private void OnParsed(object? sender, Languages.CSharp.ParsedText parsed)
    {
        if (sender is OpenDocument document)
        {
            BackgroundWork.Track(ShowColoursAsync(document, parsed));
        }
    }

    /// <summary>A C# file parsed: its colours (Color.Parse, FromRgb and the rest) found off the UI thread, and its
    /// swatches shown when the text is still the one parsed.</summary>
    private async Task ShowColoursAsync(OpenDocument document, Languages.CSharp.ParsedText parsed)
    {
        try
        {
            IReadOnlyList<Passenger.Xaml.Features.ColourUse> colours = await BackgroundWork.Run(() => Passenger.Xaml.Features.Colours.InCSharp(parsed.Root));
            if (!disposed && document.Text.Snapshot == parsed.Text && (colours.Count > 0 || document.ColourMarks.Count > 0))
            {
                document.ColourMarks = Marks(parsed.Text, colours);
            }
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
    }

    /// <summary>The gutter's swatches for colours found in a text: a line's first.</summary>
    internal static IReadOnlyList<ColourMark> Marks(TextSnapshot text, IReadOnlyList<Passenger.Xaml.Features.ColourUse> colours) =>
        [.. colours.Where(c => c.Offset <= text.Length).Select(c => new ColourMark(text.GetLineFromOffset(c.Offset),
            Avalonia.Media.Color.FromArgb(c.Colour.A, c.Colour.R, c.Colour.G, c.Colour.B), c))];

    /// <summary>The load of the project a XAML file is in, begun if it has not been; null for a file in no C# project.
    /// Whether the project's XAML is one Passenger reads is learned off the UI thread, as the load begins.</summary>
    private XamlLoading? ProjectOf(string path) => ProjectFile(path) is { } project ? LoadingOf(project) : null;

    /// <summary>The load of a project by its file, begun if it has not been.</summary>
    private XamlLoading LoadingOf(string project)
    {
        if (projects.TryGetValue(project, out XamlLoading? loading))
        {
            return loading;
        }
        Dictionary<string, TextSnapshot> texts = OpenTexts();
        Task<XamlProjectBuilder?> builder = BuilderAsync(project, Evaluations);
        loading = new XamlLoading(project, builder, LoadAsync(builder, texts));
        projects[project] = loading;
        IdeErrors.Observe(AnnounceAsync(loading));
        return loading;
    }

    /// <summary>A project's builder, made on the thread pool once the window has laid out and drawn what it had to (the
    /// file whose opening asked for it): the read that follows keeps every processor and the collector busy for a while,
    /// which that first frame would otherwise wait behind. None for a session ended meanwhile.</summary>
    private async Task<XamlProjectBuilder?> BuilderAsync(string project, Func<string, ProjectEvaluation?>? evaluations)
    {
        using IDisposable held = BackgroundWork.Scope();
        await Dispatcher.Yield(DispatcherPriority.Background);
        return disposed ? null : await BackgroundWork.Run(() => XamlProjectBuilder.For(project, DotnetRoot.Value, CSharpParsing.Default, IdeErrors.Report, evaluations));
    }

    private static async Task<XamlProject?> LoadAsync(Task<XamlProjectBuilder?> made, Dictionary<string, TextSnapshot> texts)
    {
        if (await made is not { } builder)
        {
            return null;
        }
        return await BackgroundWork.Run(() => builder.Load(p => texts.TryGetValue(p, out TextSnapshot? text) ? text.ToString() : null));
    }

    /// <summary>The IDE's log learns once a project's XAML has loaded, and of a load that failed.</summary>
    private async Task AnnounceAsync(XamlLoading loading)
    {
        try
        {
            if (await loading.Loaded is { } project && !disposed)
            {
                IdeErrors.Write($"XAML for {Path.GetFileName(loading.ProjectPath)} read: {project.Types.All.Count()} types, {project.Files.Count} XAML files");
                ProjectChanged?.Invoke(this, project);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            IdeErrors.Report(error);
        }
    }

    /// <summary>The project already loading or loaded that a file belongs to.</summary>
    private XamlLoading? Loaded(string path) =>
        projects.Values.Where(l => PathNames.Within(path, l.Folder)).OrderByDescending(l => l.Folder.Length).FirstOrDefault();

    /// <summary>The projects loaded or loading that a file changed on disk may change: for C#, each that may read it; for
    /// anything else, the project it is in, or whose restore it is.</summary>
    private IEnumerable<XamlLoading> OwnersOf(string path)
    {
        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return Reading(path);
        }
        return (RestoredFor(path) ?? Loaded(path)) is { } owner ? [owner] : [];
    }

    /// <summary>The projects whose XAML may read a C# file: each read whose folder or a referenced project's holds it (an
    /// app's XAML binding to App.Core's view models), and each still loading, whose references are not known yet; none
    /// whose XAML Passenger does not read. Each builder says off the UI thread whether it compiles the file.</summary>
    private IEnumerable<XamlLoading> Reading(string path) =>
        projects.Values.Where(l => !l.Loaded.IsCompleted || (l.Loaded.IsCompletedSuccessfully && l.Loaded.Result is { } project
            && (PathNames.Within(path, l.Folder) || project.Referenced.Any(r => PathNames.Within(path, r.Folder)))));

    /// <summary>The project a restore's assets under an artifacts folder are of (UseArtifactsOutput's
    /// artifacts/obj/Project/project.assets.json), under no project's folder: the project of that name below the folder
    /// holding artifacts.</summary>
    private XamlLoading? RestoredFor(string path)
    {
        if (Path.GetFileName(path) != "project.assets.json" || Path.GetDirectoryName(path) is not { } named || Path.GetDirectoryName(named) is not { } obj
            || Path.GetFileName(obj) != "obj" || Path.GetDirectoryName(obj) is not { } artifacts || Path.GetFileName(artifacts) != "artifacts"
            || Path.GetDirectoryName(artifacts) is not { } root)
        {
            return null;
        }
        string name = Path.GetFileName(named);
        return projects.Values.Where(l => Path.GetFileNameWithoutExtension(l.ProjectPath) == name && PathNames.Within(l.ProjectPath, root))
            .OrderByDescending(l => l.Folder.Length).FirstOrDefault();
    }

    /// <summary>A project's load begun again, its builder made again: what it references now says whether its XAML is one
    /// Passenger reads and in which dialect (Avalonia added through NuGet, or restored at last for a fresh clone).</summary>
    private void Reload(XamlLoading loading)
    {
        Dictionary<string, TextSnapshot> texts = OpenTexts();
        Task<XamlProjectBuilder?> builder = BuilderAsync(loading.ProjectPath, Evaluations);
        XamlLoading again = loading with { Builder = builder, Loaded = LoadAsync(builder, texts) };
        projects[loading.ProjectPath] = again;
        foreach (XamlDocument document in documents.Values.Where(d => d.Loading.ProjectPath == loading.ProjectPath))
        {
            document.Follow(again);
        }
        IdeErrors.Observe(AnnounceAsync(again));
    }

    private Dictionary<string, TextSnapshot> OpenTexts()
    {
        Dictionary<string, TextSnapshot> texts = new(StringComparer.Ordinal);
        foreach (OpenDocument document in documents.Keys.Concat(sources.Keys))
        {
            texts[document.Path] = document.Text.Snapshot;
        }
        return texts;
    }

    /// <summary>A C# file typed in: its types read again from its text as it stands, by every project that reads it, which
    /// takes it only when it took no newer text of the file meanwhile (<see cref="TypedSource.Give"/>).</summary>
    private async Task SourceTypedAsync(OpenDocument document)
    {
        if (!sources.TryGetValue(document, out TypedSource? source))
        {
            return;
        }
        string path = document.Path;
        TextSnapshot text = document.Text.Snapshot;
        int version = document.Text.Version;
        if (textHeld is { } held)
        {
            textHeld = null;
            held.Holding.SetResult();
            await held.Until;
        }
        await Task.WhenAll(Reading(path).ToList().Select(loading => UpdateAsync(loading, builder => source.Give(builder, path, text, version))));
    }

    /// <summary>Holds the next text of an open C# file, taken as its typing pauses, from the projects that read it until
    /// <paramref name="until"/> ends, for a test to land a file's texts out of order. Done once it is held.</summary>
    public Task HoldNextText(Task until)
    {
        UiThread.Check();
        TaskCompletionSource holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        textHeld = (until, holding);
        return holding.Task;
    }

    /// <summary>A change made to a loaded project off the UI thread; its open XAML files read against the result.</summary>
    private async Task UpdateAsync(XamlLoading loading, Func<XamlProjectBuilder, XamlProject?> change)
    {
        using SourcesPending checking = Pending([loading]);
        try
        {
            if (await loading.Loaded is null || await loading.Builder is not { } builder)
            {
                return;
            }
            XamlProject? made = await BackgroundWork.Run(() => change(builder));
            if (made is null || disposed)
            {
                return;
            }
            foreach (XamlDocument document in documents.Values.Where(d => d.Loading.ProjectPath == loading.ProjectPath))
            {
                document.ProjectChanged();
            }
            ProjectChanged?.Invoke(this, made);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            IdeErrors.Report(error);
        }
    }

    /// <summary>Where the XAML of the projects given uses a symbol of their C# (<see cref="CodeSymbol"/>): each project's
    /// XAML read first where no open file has had it read and its folders hold XAML, and the C# typed in that it has yet to
    /// read taken in. By file, its text as the editor has it, else as on disk, with the ranges of the uses in it, read off
    /// the UI thread.</summary>
    public async Task<IReadOnlyList<(string Path, TextSnapshot Text, IReadOnlyList<TextRange> Uses)>> UsesAsync(IEnumerable<string> projectFiles, CodeSymbol symbol)
    {
        UiThread.Check();
        if (disposed)
        {
            return [];
        }
        List<string> given = [.. projectFiles.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Distinct(PathNames.Comparer)];
        List<string> unread = [.. given.Where(p => !projects.ContainsKey(p))];
        HashSet<string> holding = await BackgroundWork.Run(() => unread.Where(XamlProjectBuilder.HoldsXaml).ToHashSet(PathNames.Comparer));
        if (disposed)
        {
            return [];
        }
        List<XamlLoading> loadings = [.. given.Where(p => projects.ContainsKey(p) || holding.Contains(p)).Select(LoadingOf)];
        await TypedNowAsync();
        List<XamlProject> read = [];
        foreach (XamlLoading loading in loadings)
        {
            if (await loading.Builder is not { } builder)
            {
                continue;
            }
            if (!loading.Loaded.IsCompleted)
            {
                Say($"Reading the types {Path.GetFileNameWithoutExtension(loading.ProjectPath)}'s XAML can use…");
            }
            if (await loading.Loaded is not null)
            {
                read.Add(builder.Current);
            }
        }
        Func<string, TextSnapshot?> open = OpenSnapshots();
        return read.Count == 0 ? [] : await BackgroundWork.Run(() => Uses(read, symbol, open));
    }

    private static List<(string Path, TextSnapshot Text, IReadOnlyList<TextRange> Uses)> Uses(IReadOnlyList<XamlProject> read, CodeSymbol symbol, Func<string, TextSnapshot?> open)
    {
        List<(string, TextSnapshot, IReadOnlyList<TextRange>)> found = [];
        HashSet<string> seen = new(PathNames.Comparer);
        foreach (XamlProject project in read)
        {
            IReadOnlyList<string> names = symbol.NamesIn(project.Types);
            foreach (string path in project.Files.Keys.Order(StringComparer.Ordinal))
            {
                if (!seen.Add(path) || (open(path) ?? EditorArea.Read(path)) is not { } text)
                {
                    continue;
                }
                string written = text.ToString();
                if (names.Any(n => written.Contains(n, StringComparison.OrdinalIgnoreCase))
                    && XamlNavigation.Uses(XamlDocument.Read(path, written, project, null), names, symbol.Is) is { Count: > 0 } uses)
                {
                    found.Add((path, text, uses));
                }
            }
        }
        return found;
    }

    /// <summary>The C# typed in that the projects reading it are still to read once typing pauses, read by them now.</summary>
    private Task TypedNowAsync()
    {
        List<OpenDocument> typed = [.. sources.Where(s => s.Value.Wait.IsWaiting).Select(s => s.Key)];
        foreach (OpenDocument document in typed)
        {
            sources[document].Wait.Stop();
        }
        return Task.WhenAll(typed.Select(SourceTypedAsync));
    }

    /// <summary>The XAML file whose <c>x:Class</c> is a class, in the project of a C# file of it: an open one's text first,
    /// else the project's files on disk, read off the UI thread by the walk a project's files take (<see cref="FolderWalk"/>);
    /// null for none.</summary>
    public async Task<string?> ClassFileAsync(string className, string near)
    {
        UiThread.Check();
        string declared = $"Class=\"{className}\"";
        foreach (XamlDocument document in documents.Values)
        {
            if (document.Document.Text.Snapshot.ToString().Contains(declared, StringComparison.Ordinal))
            {
                return document.Document.Path;
            }
        }
        return await BackgroundWork.Run(() => ProjectFile(near) is { } project
            ? FolderWalk.Files(Path.GetDirectoryName(project)!, built: true).Where(IsXaml).FirstOrDefault(file => Declares(file, declared))
            : null);
    }

    private static bool Declares(string file, string declared)
    {
        try
        {
            return File.ReadAllText(file).Contains(declared, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Gone, or not readable: no XAML to go to.
            return false;
        }
    }

    /// <summary>A file's project: the nearest folder at or above it with a C# project file.</summary>
    private static string? ProjectFile(string path)
    {
        for (DirectoryInfo? at = new FileInfo(path).Directory; at is not null; at = at.Parent)
        {
            try
            {
                string? found = at.EnumerateFiles("*.csproj").Select(f => f.FullName).Order(StringComparer.Ordinal).FirstOrDefault();
                if (found is not null)
                {
                    return found;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Says something in the status bar.</summary>
    internal void Say(string message) => Message?.Invoke(this, message);

    /// <summary>Lets go of the open files and every project read, as the folder's session ends.</summary>
    public void Dispose()
    {
        disposed = true;
        foreach (OpenDocument document in documents.Keys.Concat(sources.Keys).ToList())
        {
            Detach(document);
        }
        projects.Clear();
    }

    /// <summary>Holds the checks of the open XAML files of the projects a change of their C# reaches, those opened meanwhile
    /// too, until it ends: the XAML is read against the new code (<see cref="XamlDocument.ProjectChanged"/>) before an agent's
    /// diagnostics answer.</summary>
    private SourcesPending Pending(IEnumerable<XamlLoading> reading)
    {
        SourcesPending change = new(this, [.. reading.Select(l => l.ProjectPath)]);
        pending.Add(change);
        foreach (XamlDocument document in documents.Values)
        {
            change.Hold(document);
        }
        return change;
    }

    /// <summary>A change of C# on its way to the XAML of some projects, holding their open files' checks (<see cref="Pending"/>).</summary>
    private sealed class SourcesPending(XamlService service, HashSet<string> projects) : IDisposable
    {
        private readonly List<IDisposable> held = [];

        public void Hold(XamlDocument document)
        {
            if (projects.Contains(document.Loading.ProjectPath))
            {
                held.Add(document.Document.Checking(XamlDocument.Checker));
            }
        }

        public void Dispose()
        {
            if (service.pending.Remove(this))
            {
                foreach (IDisposable hold in held)
                {
                    hold.Dispose();
                }
            }
        }
    }

    /// <summary>An open C# file a XAML project may read, followed as it is typed: its texts given to each project's builder
    /// one at a time, by the document's version, so an older one never lands after a newer one.</summary>
    private sealed class TypedSource(EventHandler<DocumentChangedEventArgs> handler, Debounce wait)
    {
        private readonly object gate = new();

        /// <summary>The version of the file's text each builder took last.</summary>
        private readonly ConditionalWeakTable<XamlProjectBuilder, StrongBox<int>> taken = [];

        public EventHandler<DocumentChangedEventArgs> Handler { get; } = handler;

        public Debounce Wait { get; } = wait;

        /// <summary>Gives a builder the file's text of a version, off the UI thread, unless it took that version or a newer one
        /// already: the project it then makes, or null when it makes none.</summary>
        public XamlProject? Give(XamlProjectBuilder builder, string path, TextSnapshot text, int version)
        {
            lock (gate)
            {
                if (taken.TryGetValue(builder, out StrongBox<int>? last) && last.Value >= version)
                {
                    return null;
                }
                XamlProject? made = builder.SourceChanged(path, text.ToString());
                taken.AddOrUpdate(builder, new StrongBox<int>(version));
                return made;
            }
        }
    }
}

/// <summary>One project's XAML being read: its project file and folder at once, its builder and its first read once the
/// thread pool has made them (null for a project whose XAML Passenger does not read).</summary>
public sealed record XamlLoading(string ProjectPath, Task<XamlProjectBuilder?> Builder, Task<XamlProject?> Loaded)
{
    public string Folder => Path.GetDirectoryName(ProjectPath)!;
}
