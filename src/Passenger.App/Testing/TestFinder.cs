using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Testing;

/// <summary>The tests the test projects' code declares, found before any build: each test project's C# files read off
/// the UI thread with Roslyn's parser, an open file from its own parse as it is typed (the gutter's read of it, never a
/// parse of its own), and a file another program changed read again when the folder watcher says so; and each Godot
/// project that carries GUT, its GDScript read for GUT's tests (<see cref="GutScripts"/>), stood for by its project.godot.
/// What it finds is told a project at a time, only when it changed. A UI-thread object.</summary>
public sealed class TestFinder
{
    /// <summary>The test frameworks' own packages: a project referencing one holds tests, adapter or not.</summary>
    private static readonly HashSet<string> FrameworkPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "xunit", "xunit.core", "xunit.v3", "xunit.v3.core", "NUnit", "MSTest", "MSTest.TestFramework", "gdUnit4.api", GoDotTests.Package,
    };

    /// <summary>Closed files' reads by path, with the write time and length they were read at: a file read again only
    /// once it has changed. Filled by the workers that read them.</summary>
    private readonly FileMemo<FileTests> onDisk = new();

    /// <summary>The preprocessor symbols each test project's closed files were last read with, by its path: read with
    /// others, they are read again.</summary>
    private readonly Dictionary<string, CSharpParseOptions> readWith = new(StringComparer.Ordinal);

    /// <summary>Open files' reads, which stand for the file while it is open.</summary>
    private readonly Dictionary<string, FileTests> open = new(StringComparer.Ordinal);

    /// <summary>Each test project's C# files as last listed, and which listing of it was asked for last.</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> listed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> asked = new(StringComparer.Ordinal);

    /// <summary>What was last told of each project: its tests, each file's read they came from, the list of its tests
    /// (<see cref="CodeTests.Tests"/>, whose items a move of lines replaces in place) and where each file's begin in it.</summary>
    private readonly Dictionary<string, ToldTests> told = new(StringComparer.Ordinal);

    /// <summary>The test attributes each test project's files derive from a framework's, by its path, which its files were
    /// last read with.</summary>
    private readonly Dictionary<string, IReadOnlySet<string>> own = new(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> NoAttributes = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Every file each test project's tests were told from, by its path: one no longer among them was deleted or
    /// moved, and a runner's tests of it are gone too.</summary>
    private readonly Dictionary<string, HashSet<string>> toldFrom = new(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> NoMethods = new HashSet<string>(StringComparer.Ordinal);

    private IReadOnlyList<ProjectModel> every = [];

    /// <summary>The folder's reads kept across openings, for the folder <see cref="Folder"/> names; none without a state
    /// folder to keep them in.</summary>
    private TestsCache? cache;
    private int parsed;
    private volatile bool serverLoading;

    /// <summary>How many files are read at once while the language server loads the solution, leaving it the cores.</summary>
    private const int ReadersWhileLoading = 2;

    /// <summary>How many files are read before the readers are counted again by whether the server still loads.</summary>
    private const int ReadsCounted = 64;

    /// <summary>The state folder whose cache keeps each folder's reads across openings (<see cref="TestsCache"/>); none
    /// keeps nothing.</summary>
    public string? StateFolder { get; set; }

    /// <summary>How many closed C# files Roslyn's parser has read for their tests, those the cache had not counted out.</summary>
    public int Parsed => Volatile.Read(ref parsed);

    /// <summary>Whether the language server is still loading the solution, while which files are read
    /// <see cref="ReadersWhileLoading"/> at a time: set from the session's word that projects loaded.</summary>
    public bool ServerLoading
    {
        get => serverLoading;
        set => serverLoading = value;
    }

    /// <summary>The cache's file of the folder open, and how many bytes it holds.</summary>
    public long CacheSize => cache?.Size ?? 0;

    /// <summary>The Godot projects' folders, those carrying GUT among <see cref="Projects"/>.</summary>
    private IReadOnlyList<string> godotFolders = [];
    private int asks;
    private int listings;

    /// <summary>The folder whose projects were last read.</summary>
    public string? Folder { get; private set; }

    /// <summary>The workspace's test projects, in its order.</summary>
    public IReadOnlyList<ProjectModel> Projects { get; private set; } = [];

    /// <summary>Whether a listing of the test projects is under way, before which the tree cannot say there are none.</summary>
    public bool IsLooking => listings > 0;

    /// <summary>Each test project's tests from its code changed, or a listing ended: every test project's, by its path.</summary>
    public event EventHandler<IReadOnlyDictionary<string, CodeTests>>? Changed;

    /// <summary>An open file's tests moved to other lines, nothing else of them changed: its tests as told before, each by
    /// the same test on its line now, which the project's told tests now hold in their place. No <see cref="Changed"/>.</summary>
    public event EventHandler<(string Path, IReadOnlyDictionary<TestItem, TestItem> Moved)>? Moved;

    /// <summary>A test project's files were found to derive other test attributes, or a GUT project's config changed which
    /// scripts hold its tests: open files are read again.</summary>
    public event EventHandler? AttributesChanged;

    /// <summary>A file was read, whose tests' code may have changed (<see cref="CodeOf"/>), told or not.</summary>
    public event EventHandler? CodeRead;

    /// <summary>A test method's code as its file now reads (<see cref="DeclaredTest.Code"/>), an open file's as typed; null
    /// where the file has not been read or declares no such test.</summary>
    public ulong? CodeOf(string path, string fullName)
    {
        UiThread.Check();
        FileTests? read = open.GetValueOrDefault(path) ?? onDisk.Kept(path);
        int dot = fullName.LastIndexOf('.');
        if (read is null || dot < 0)
        {
            return null;
        }
        foreach (DeclaredTest test in read.Tests)
        {
            if (test.Method.Length == fullName.Length - dot - 1 && fullName.AsSpan(dot + 1).SequenceEqual(test.Method)
                && test.Type.Length == dot && fullName.AsSpan(0, dot).SequenceEqual(test.Type))
            {
                return test.Code;
            }
        }
        return null;
    }

    /// <summary>The test attributes the project of a file derives from a framework's, its methods tests by them too; a GUT
    /// project's, the class names and paths of its scripts that are test classes (<see cref="GutScripts.Read"/>).</summary>
    public IReadOnlySet<string> AttributesOf(string path)
    {
        UiThread.Check();
        return Owner(path) is { } project ? own.GetValueOrDefault(project.Path) ?? NoAttributes : NoAttributes;
    }

    /// <summary>Whether a project holds tests: it says it is a test project or references a test adapter, or references
    /// xUnit, NUnit, MSTest, GdUnit4 or GoDotTest itself.</summary>
    public static bool HoldsTests(ProjectModel project) =>
        project.Exists && (project.IsTestProject || project.Packages.Any(p => FrameworkPackages.Contains(p.Name)));

    /// <summary>Reads the workspace's test projects, and its Godot projects that carry GUT (<paramref name="godot"/> their
    /// folders), those read before again only where their files changed.</summary>
    public void Scan(string? workspace, IReadOnlyList<ProjectModel> projects, IReadOnlyList<string>? godot = null)
    {
        UiThread.Check();
        if (workspace != Folder)
        {
            Folder = workspace;
            cache = StateFolder is { } root && workspace is not null ? new TestsCache(TestsCache.PathFor(root, workspace)) : null;
            onDisk.Clear();
            listed.Clear();
            told.Clear();
            readWith.Clear();
            own.Clear();
            toldFrom.Clear();
        }
        every = projects;
        godotFolders = [.. (godot ?? []).Distinct(PathNames.Comparer)];
        Projects = [.. projects.Where(HoldsTests), .. godotFolders.Where(GutScripts.Carried).Select(ProjectModel.OfScriptGame)];
        foreach (ProjectModel project in Projects.Where(p => !p.IsScriptGame))
        {
            CSharpParseOptions symbols = CompilationSymbols.Of(project);
            if (readWith.TryGetValue(project.Path, out CSharpParseOptions? before) && !ReferenceEquals(before, symbols))
            {
                foreach (string file in listed.GetValueOrDefault(project.Path) ?? [])
                {
                    onDisk.Forget(file);
                }
            }
            readWith[project.Path] = symbols;
        }
        HashSet<string> current = [.. Projects.Select(p => p.Path)];
        foreach (string gone in told.Keys.Where(p => !current.Contains(p)).ToList())
        {
            told.Remove(gone);
            listed.Remove(gone);
            own.Remove(gone);
            toldFrom.Remove(gone);
        }
        Read(Projects, timed: true);
    }

    /// <summary>An open file's tests as the gutter read them from its parse.</summary>
    public void OpenRead(string path, FileTests tests)
    {
        UiThread.Check();
        // Kept whatever it says, as its tests' code may have changed where nothing else of them did.
        bool same = open.TryGetValue(path, out FileTests? was) && was.SameAs(tests);
        open[path] = tests;
        CodeRead?.Invoke(this, EventArgs.Empty);
        if (same)
        {
            return;
        }
        if (Owner(path) is not { } project)
        {
            return;
        }
        // Lines put in or taken out above or among its tests: they move where they are told, the project told again nowhere.
        if (told.TryGetValue(project.Path, out ToldTests record) && record.Files.TryGetValue(path, out FileTests? toldRead)
            && toldRead.OnlyMoved(tests) && record.Starts.TryGetValue(path, out int start))
        {
            record.Files[path] = tests;
            Dictionary<TestItem, TestItem> moved = new(ReferenceEqualityComparer.Instance);
            int at = start;
            foreach (DeclaredTest test in tests.Tests)
            {
                if (test.Abstract)
                {
                    continue;
                }
                TestItem item = record.Items[at];
                if (item.Line != test.Line + 1)
                {
                    record.Items[at] = moved[item] = item with { Line = test.Line + 1 };
                }
                at++;
            }
            if (moved.Count > 0)
            {
                Moved?.Invoke(this, (path, moved));
            }
            return;
        }
        Tell([project]);
    }

    /// <summary>A file closed: what is on disk stands for it again, edits not saved gone with it.</summary>
    public void Closed(string path)
    {
        UiThread.Check();
        if (open.Remove(path) && Owner(path) is { } project)
        {
            Read([project], timed: false);
        }
    }

    /// <summary>Files changed on disk: the test projects holding them are listed and read again where they changed, or
    /// when nothing came or went, only the C# files changed among those listed are read again. An open file's own parse
    /// follows it.</summary>
    public void FolderChanged(FolderChanges changes)
    {
        UiThread.Check();
        // GUT come to a Godot project, or gone from one, makes it a test project, or no longer one.
        if (changes.Paths.Any(path => godotFolders.Any(folder => PathNames.IsUnder(path, Path.Combine(folder, "addons")))
            && godotFolders.Any(folder => GutScripts.Carried(folder) != Projects.Any(p => p.IsScriptGame && PathNames.Same(p.Folder, folder)))))
        {
            Scan(Folder, every, godotFolders);
            return;
        }
        // GUT's config changed: which scripts hold its tests, read again, open ones too.
        List<ProjectModel> configured = [.. Projects.Where(p => p.IsScriptGame && changes.Paths.Any(path => PathNames.Same(path, Path.Combine(p.Folder, GutScripts.ConfigFile))))];
        if (configured.Count > 0)
        {
            foreach (string file in configured.SelectMany(p => listed.GetValueOrDefault(p.Path) ?? []))
            {
                onDisk.Forget(file);
            }
            Read(configured, timed: false);
            AttributesChanged?.Invoke(this, EventArgs.Empty);
        }
        List<string> paths = [.. changes.Paths.Where(p => !open.ContainsKey(p))];
        List<ProjectModel> touched = [.. Projects.Where(project => paths.Any(path => PathNames.Within(path, project.Folder) || PathNames.Within(project.Folder, path)))];
        if (touched.Count == 0)
        {
            return;
        }
        if (changes.Structure || touched.Any(project => !listed.ContainsKey(project.Path)))
        {
            Read(touched, timed: false);
            return;
        }
        string[] changed = [.. paths.Where(path => touched.Any(project => listed[project.Path].Contains(path)))];
        if (changed.Length > 0)
        {
            ReadAgain(touched, changed);
        }
    }

    /// <summary>Reads files of projects already listed again on a worker, then tells what changed.</summary>
    private void ReadAgain(IReadOnlyList<ProjectModel> projects, string[] changed)
    {
        IdeErrors.Observe(ReadAsync());

        async Task ReadAsync()
        {
            (string Path, CSharpParseOptions Symbols, IReadOnlySet<string> Own, string? Gut)[] reads =
                [.. changed.Select(path => (path, CompilationSymbols.Of(every, path), AttributesOf(path), GutFolder(path)))];
            TestsCache? kept = cache;
            await BackgroundWork.Run(() =>
            {
                Parallel.ForEach(reads, read => ReadFile(read.Path, read.Symbols, read.Own, read.Gut, kept));
                kept?.Save();
            });
            Tell([.. projects.Where(Projects.Contains)]);
        }
    }

    /// <summary>The test projects a file is one of, by their paths: those it was last listed in (projects sharing a folder
    /// share its files), else the test project it belongs to; none when it is no test project's.</summary>
    public IReadOnlySet<string> ProjectsOf(string path)
    {
        UiThread.Check();
        HashSet<string> holding = new(PathNames.Comparer);
        foreach (ProjectModel project in Projects)
        {
            if (listed.TryGetValue(project.Path, out IReadOnlyList<string>? files) && files.Contains(path, PathNames.Comparer))
            {
                holding.Add(project.Path);
            }
        }
        if (holding.Count == 0 && Owner(path) is { } owner)
        {
            holding.Add(owner.Path);
        }
        return holding;
    }

    /// <summary>Whether a method the tree shows is one of these projects' (<see cref="ProjectsOf"/> a file), or they are
    /// none: a test of the same name in another project is not the file's.</summary>
    public static bool IsOf(TestNode method, IReadOnlySet<string> projects) =>
        projects.Count == 0 || method.ProjectPath is not { } project || projects.Contains(project);

    /// <summary>The test project a file belongs to: of the projects holding it, the nearest, when that one holds tests; a
    /// GDScript file's, the Godot project carrying GUT that holds it.</summary>
    private ProjectModel? Owner(string path) => GutFolder(path) is { } folder ? Projects.First(p => p.IsScriptGame && PathNames.Same(p.Folder, folder))
        : ProjectModel.Owning(every, path) is { } owner && Projects.Contains(owner) ? owner : null;

    /// <summary>The folder of the nearest Godot project carrying GUT that holds a GDScript file GUT's tests may be in; null
    /// for any other file.</summary>
    private string? GutFolder(string path) =>
        Projects.Where(p => p.IsScriptGame && GutScripts.Holds(p.Folder, path)).MaxBy(p => p.Folder.Length)?.Folder;

    /// <summary>Lists the test projects' C# files again and reads those changed on disk since they were read, then tells
    /// what changed: what a run's build took in, ahead of the folder watcher's word of it.</summary>
    public Task CatchUpAsync()
    {
        UiThread.Check();
        return Projects.Count == 0 ? Task.CompletedTask : Listing(Projects, timed: false);
    }

    /// <summary>Lists projects' C# files and reads those changed since they were read (<see cref="Listing"/>).</summary>
    private void Read(IReadOnlyList<ProjectModel> projects, bool timed) => IdeErrors.Observe(Listing(projects, timed));

    /// <summary>Lists projects' C# files and reads those changed since they were read, on a worker, then tells what
    /// changed; a later listing of a project asked for meanwhile wins over this one. It never faults: a failure is
    /// reported.</summary>
    private Task Listing(IReadOnlyList<ProjectModel> projects, bool timed)
    {
        Dictionary<string, int> mine = new(StringComparer.Ordinal);
        foreach (ProjectModel project in projects)
        {
            mine[project.Path] = asked[project.Path] = ++asks;
        }
        IReadOnlyList<ProjectModel> others = every;
        Dictionary<string, IReadOnlySet<string>> owns = projects.ToDictionary(p => p.Path, p => own.GetValueOrDefault(p.Path) ?? NoAttributes, StringComparer.Ordinal);
        TestsCache? kept = cache;
        bool whole = Projects.Where(p => !p.IsScriptGame).All(projects.Contains);
        long began = Stopwatch.GetTimestamp();
        int parsedBefore = Parsed;
        listings++;
        return ReadAsync();

        async Task ReadAsync()
        {
            try
            {
                (Dictionary<string, IReadOnlyList<string>> files, Dictionary<string, IReadOnlySet<string>> readWith) =
                    await BackgroundWork.Run(() => List(projects, others, owns, kept, whole));
                List<ProjectModel> latest = [.. projects.Where(p => asked.GetValueOrDefault(p.Path) == mine[p.Path] && Projects.Contains(p))];
                foreach (ProjectModel project in latest)
                {
                    listed[project.Path] = files[project.Path];
                    // The attributes the cache last read the project with, which its files are told as read with.
                    if (!own.ContainsKey(project.Path) && !ReferenceEquals(readWith[project.Path], NoAttributes))
                    {
                        own[project.Path] = readWith[project.Path];
                    }
                }
                listings--;
                Tell(latest, always: true);
                if (timed && latest.Count > 0)
                {
                    int tests = latest.Sum(p => told.TryGetValue(p.Path, out ToldTests was) ? was.Tests.Tests.Count : 0);
                    IdeErrors.Write($"Unit tests: {tests} found in the code of {Core.Plural.Of(latest.Count, "test project")} in {Stopwatch.GetElapsedTime(began).TotalSeconds:0.00} s, "
                        + $"{Core.Plural.Of(Parsed - parsedBefore, "file")} parsed");
                }
            }
            catch (Exception error)
            {
                listings--;
                IdeErrors.Report(error);
            }
        }
    }

    /// <summary>Each project's C# files, but those of build output, dependencies, hidden folders and projects inside it,
    /// each read once it has changed since it was last read, a project whose attributes are not yet known read with those
    /// the cache last read it with; and the attributes each was read with. Read a few at a time while the language server
    /// loads the solution; the cache written after, a listing of <paramref name="whole"/> the test projects dropping what
    /// it no longer names. A worker's.</summary>
    private (Dictionary<string, IReadOnlyList<string>> Files, Dictionary<string, IReadOnlySet<string>> ReadWith) List(IReadOnlyList<ProjectModel> projects,
        IReadOnlyList<ProjectModel> solution, IReadOnlyDictionary<string, IReadOnlySet<string>> owns, TestsCache? kept, bool whole)
    {
        Dictionary<string, IReadOnlyList<string>> found = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlySet<string>> readWith = new(StringComparer.Ordinal);
        List<(string Path, CSharpParseOptions Symbols, IReadOnlySet<string> Own, string? Gut)> all = [];
        foreach (ProjectModel project in projects)
        {
            bool gut = project.IsScriptGame;
            List<string> files = [.. gut ? GutScripts.Files(project.Folder) : FolderWalk.ProjectFiles(project, solution, ".cs")];
            found[project.Path] = files;
            IReadOnlySet<string> attributes = ReferenceEquals(owns[project.Path], NoAttributes) && !gut ? kept?.AttributesOf(project.Path) ?? NoAttributes : owns[project.Path];
            readWith[project.Path] = attributes;
            CSharpParseOptions symbols = gut ? CSharpParseOptions.Default : CompilationSymbols.Of(project);
            all.AddRange(files.Select(file => (file, symbols, attributes, gut ? project.Folder : null)));
        }
        for (int at = 0; at < all.Count; at += ReadsCounted)
        {
            ParallelOptions readers = new() { MaxDegreeOfParallelism = serverLoading ? ReadersWhileLoading : -1 };
            Parallel.ForEach(all.GetRange(at, Math.Min(ReadsCounted, all.Count - at)), readers, read => ReadFile(read.Path, read.Symbols, read.Own, read.Gut, kept));
        }
        if (kept is not null)
        {
            foreach (ProjectModel project in projects.Where(p => !p.IsScriptGame))
            {
                kept.KeepAttributes(project.Path, readWith[project.Path]);
            }
            kept.Save(whole ? new HashSet<string>(all.Where(r => r.Gut is null).Select(r => r.Path), PathNames.Comparer) : null,
                whole ? new HashSet<string>(projects.Select(p => p.Path), PathNames.Comparer) : null);
        }
        return (found, readWith);
    }

    /// <summary>Reads a closed file's tests with its project's symbols and derived test attributes, or a GDScript file's as
    /// GUT's of the Godot project in <paramref name="gut"/> with the test classes its scripts derive, unless it has not
    /// changed since they were read; one being written is read once the watcher says so again. A worker's.</summary>
    private void ReadFile(string path, CSharpParseOptions symbols, IReadOnlySet<string> attributes, string? gut, TestsCache? kept) =>
        onDisk.Get(path, file => gut is not null ? GutScripts.Read(File.ReadAllText(file), gut, file, attributes, GutScripts.ConfigOf(gut)).Tests : Parse(file, symbols, attributes, kept));

    /// <summary>A closed C# file's tests: those the cache kept where the file and what it is read with are as they were,
    /// else parsed from the bytes its stamp was taken of, and kept. A worker's.</summary>
    private FileTests Parse(string file, CSharpParseOptions symbols, IReadOnlySet<string> attributes, TestsCache? kept)
    {
        byte[] bytes = File.ReadAllBytes(file);
        TestsCache.Stamp stamp = TestsCache.StampOf(file, bytes);
        string with = TestsCache.ReadWith(symbols, attributes);
        if (kept?.Find(file, stamp, with) is { } read)
        {
            return read;
        }
        Interlocked.Increment(ref parsed);
        using StreamReader text = new(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        FileTests tests = TestsAtCaret.Read(text.ReadToEnd(), symbols, attributes);
        kept?.Keep(file, stamp, with, tests);
        return tests;
    }

    /// <summary>Tells the projects' tests as their files now read, open ones from their own parse, for those that changed
    /// (or all of them, once a listing has ended, which may have found none).</summary>
    private void Tell(IReadOnlyList<ProjectModel> projects, bool always = false)
    {
        bool changed = false;
        List<ProjectModel> derived = [];
        foreach (ProjectModel project in projects)
        {
            Dictionary<string, FileTests> files = new(StringComparer.Ordinal);
            foreach (string path in listed.GetValueOrDefault(project.Path) ?? [])
            {
                if (open.TryGetValue(path, out FileTests? typed))
                {
                    files[path] = typed;
                }
                else if (onDisk.Kept(path) is { } read)
                {
                    files[path] = read;
                }
            }
            // A file made in the editor and not yet listed.
            foreach ((string path, FileTests typed) in open)
            {
                if (!files.ContainsKey(path) && Owner(path) == project)
                {
                    files[path] = typed;
                }
            }
            // Files that derive other test attributes than they were read with are read again with them.
            HashSet<string> attributes = [.. files.Values.SelectMany(f => f.Attributes)];
            if (!attributes.SetEquals(own.GetValueOrDefault(project.Path) ?? NoAttributes))
            {
                own[project.Path] = attributes;
                foreach (string path in listed.GetValueOrDefault(project.Path) ?? [])
                {
                    onDisk.Forget(path);
                }
                derived.Add(project);
            }
            if (told.TryGetValue(project.Path, out ToldTests was) && Same(was.Files, files))
            {
                continue;
            }
            List<TestItem> tests = [];
            Dictionary<string, int> starts = new(StringComparer.Ordinal);
            foreach ((string path, FileTests read) in files)
            {
                starts[path] = tests.Count;
                tests.AddRange(read.Tests.Where(t => !t.Abstract).Select(t => new TestItem(project.Path, $"{t.Type}.{t.Method}", t.Method)
                {
                    Path = path,
                    Line = t.Line + 1,
                    Traits = t.Traits,
                    Cases = t.Cases,
                }));
            }
            // A file told from before and gone since declares no method: a runner's tests found there leave with it.
            if (!toldFrom.TryGetValue(project.Path, out HashSet<string>? before))
            {
                toldFrom[project.Path] = before = new(StringComparer.Ordinal);
            }
            before.UnionWith(files.Keys);
            Dictionary<string, IReadOnlySet<string>> methods = before.ToDictionary(f => f, f => files.TryGetValue(f, out FileTests? read) ? read.Methods : NoMethods, StringComparer.Ordinal);
            told[project.Path] = new(new CodeTests(tests, methods), files, tests, starts);
            changed = true;
        }
        if (changed || always)
        {
            Changed?.Invoke(this, Projects.Where(p => told.ContainsKey(p.Path)).ToDictionary(p => p.Path, p => told[p.Path].Tests, StringComparer.Ordinal));
        }
        CodeRead?.Invoke(this, EventArgs.Empty);
        if (derived.Count > 0)
        {
            Read(derived, timed: false);
            AttributesChanged?.Invoke(this, EventArgs.Empty);
        }

        static bool Same(Dictionary<string, FileTests> before, Dictionary<string, FileTests> now) =>
            before.Count == now.Count && now.All(f => before.TryGetValue(f.Key, out FileTests? was) && (ReferenceEquals(was, f.Value) || was.SameAs(f.Value)));
    }

    /// <summary>What was told of a project (<see cref="told"/>).</summary>
    private readonly record struct ToldTests(CodeTests Tests, Dictionary<string, FileTests> Files, List<TestItem> Items, Dictionary<string, int> Starts);
}
