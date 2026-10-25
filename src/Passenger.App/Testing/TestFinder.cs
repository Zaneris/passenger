using System.Diagnostics;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Testing;

/// <summary>The tests the test projects' code declares, found before any build: each test project's C# files read off
/// the UI thread with Roslyn's parser, an open file from its own parse as it is typed (the gutter's read of it, never a
/// parse of its own), and a file another program changed read again when the folder watcher says so. What it finds is
/// told a project at a time, only when it changed. A UI-thread object.</summary>
public sealed class TestFinder
{
    /// <summary>The test frameworks' own packages: a project referencing one holds tests, adapter or not.</summary>
    private static readonly HashSet<string> FrameworkPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "xunit", "xunit.core", "xunit.v3", "xunit.v3.core", "NUnit", "MSTest", "MSTest.TestFramework",
    };

    /// <summary>Closed files' reads by path, with the write time and length they were read at: a file read again only
    /// once it has changed. Filled by the workers that read them.</summary>
    private readonly FileMemo<FileTests> onDisk = new();

    /// <summary>Open files' reads, which stand for the file while it is open.</summary>
    private readonly Dictionary<string, FileTests> open = new(StringComparer.Ordinal);

    /// <summary>Each test project's C# files as last listed, and which listing of it was asked for last.</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> listed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> asked = new(StringComparer.Ordinal);

    /// <summary>What was last told of each project: its tests, each file's read they came from, the list of its tests
    /// (<see cref="CodeTests.Tests"/>, whose items a move of lines replaces in place) and where each file's begin in it.</summary>
    private readonly Dictionary<string, ToldTests> told = new(StringComparer.Ordinal);

    private IReadOnlyList<ProjectModel> every = [];
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

    /// <summary>Whether a project holds tests: it says it is a test project or references a test adapter, or references
    /// xUnit, NUnit or MSTest itself.</summary>
    public static bool HoldsTests(ProjectModel project) =>
        project.Exists && (project.IsTestProject || project.Packages.Any(p => FrameworkPackages.Contains(p.Name)));

    /// <summary>Reads the workspace's test projects, those read before again only where their files changed.</summary>
    public void Scan(string? workspace, IReadOnlyList<ProjectModel> projects)
    {
        UiThread.Check();
        if (workspace != Folder)
        {
            Folder = workspace;
            onDisk.Clear();
            listed.Clear();
            told.Clear();
        }
        every = projects;
        Projects = [.. projects.Where(HoldsTests)];
        HashSet<string> current = [.. Projects.Select(p => p.Path)];
        foreach (string gone in told.Keys.Where(p => !current.Contains(p)).ToList())
        {
            told.Remove(gone);
            listed.Remove(gone);
        }
        Read(Projects, timed: true);
    }

    /// <summary>An open file's tests as the gutter read them from its parse.</summary>
    public void OpenRead(string path, FileTests tests)
    {
        UiThread.Check();
        if (open.TryGetValue(path, out FileTests? was) && was.SameAs(tests))
        {
            return;
        }
        open[path] = tests;
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

    /// <summary>Files changed on disk: the test projects holding them are listed and read again where they changed. An
    /// open file's own parse follows it.</summary>
    public void FolderChanged(FolderChanges changes)
    {
        UiThread.Check();
        List<string> paths = [.. changes.Paths.Where(p => !open.ContainsKey(p))];
        List<ProjectModel> touched = [.. Projects.Where(project => paths.Any(path => PathNames.Within(path, project.Folder) || PathNames.Within(project.Folder, path)))];
        if (touched.Count > 0)
        {
            Read(touched, timed: false);
        }
    }

    /// <summary>The test project a file belongs to: of the projects holding it, the nearest, when that one holds tests.</summary>
    private ProjectModel? Owner(string path) => ProjectModel.Owning(every, path) is { } owner && Projects.Contains(owner) ? owner : null;

    /// <summary>Lists projects' C# files and reads those changed since they were read, on a worker, then tells what
    /// changed; a later listing of a project asked for meanwhile wins over this one.</summary>
    private void Read(IReadOnlyList<ProjectModel> projects, bool timed)
    {
        Dictionary<string, int> mine = new(StringComparer.Ordinal);
        foreach (ProjectModel project in projects)
        {
            mine[project.Path] = asked[project.Path] = ++asks;
        }
        IReadOnlyList<ProjectModel> others = every;
        long began = Stopwatch.GetTimestamp();
        listings++;
        IdeErrors.Observe(ReadAsync());

        async Task ReadAsync()
        {
            try
            {
                Dictionary<string, IReadOnlyList<string>> files = await BackgroundWork.Run(() => List(projects, others));
                List<ProjectModel> latest = [.. projects.Where(p => asked.GetValueOrDefault(p.Path) == mine[p.Path] && Projects.Contains(p))];
                foreach (ProjectModel project in latest)
                {
                    listed[project.Path] = files[project.Path];
                }
                listings--;
                Tell(latest, always: true);
                if (timed && latest.Count > 0)
                {
                    int tests = latest.Sum(p => told.TryGetValue(p.Path, out ToldTests was) ? was.Tests.Tests.Count : 0);
                    IdeErrors.Write($"Unit tests: {tests} found in the code of {Core.Plural.Of(latest.Count, "test project")} in {Stopwatch.GetElapsedTime(began).TotalSeconds:0.00} s");
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
    /// each read once it has changed since it was last read. A worker's.</summary>
    private Dictionary<string, IReadOnlyList<string>> List(IReadOnlyList<ProjectModel> projects, IReadOnlyList<ProjectModel> solution)
    {
        Dictionary<string, IReadOnlyList<string>> found = new(StringComparer.Ordinal);
        List<string> all = [];
        foreach (ProjectModel project in projects)
        {
            List<string> files = [.. FolderWalk.ProjectFiles(project, solution, ".cs")];
            found[project.Path] = files;
            all.AddRange(files);
        }
        Parallel.ForEach(all, ReadFile);
        return found;
    }

    /// <summary>Reads a closed file's tests unless it has not changed since they were read; one being written is read once
    /// the watcher says so again. A worker's.</summary>
    private void ReadFile(string path) => onDisk.Get(path, file => TestsAtCaret.Read(File.ReadAllText(file)));

    /// <summary>Tells the projects' tests as their files now read, open ones from their own parse, for those that changed
    /// (or all of them, once a listing has ended, which may have found none).</summary>
    private void Tell(IReadOnlyList<ProjectModel> projects, bool always = false)
    {
        bool changed = false;
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
                if (!files.ContainsKey(path) && ProjectModel.Owning(every, path) == project)
                {
                    files[path] = typed;
                }
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
                }));
            }
            told[project.Path] = new(new CodeTests(tests, files.ToDictionary(f => f.Key, f => f.Value.Methods, StringComparer.Ordinal)), files, tests, starts);
            changed = true;
        }
        if (changed || always)
        {
            Changed?.Invoke(this, Projects.Where(p => told.ContainsKey(p.Path)).ToDictionary(p => p.Path, p => told[p.Path].Tests, StringComparer.Ordinal));
        }

        static bool Same(Dictionary<string, FileTests> before, Dictionary<string, FileTests> now) =>
            before.Count == now.Count && now.All(f => before.TryGetValue(f.Key, out FileTests? was) && (ReferenceEquals(was, f.Value) || was.SameAs(f.Value)));
    }

    /// <summary>What was told of a project (<see cref="told"/>).</summary>
    private readonly record struct ToldTests(CodeTests Tests, Dictionary<string, FileTests> Files, List<TestItem> Items, Dictionary<string, int> Starts);
}
