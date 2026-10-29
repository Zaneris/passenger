using System.Diagnostics;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>The C# files come under one of the folder's projects since the server loaded it, until the server holds each
/// in its project: before, the server has an open one in Miscellaneous Files and the rest in nothing, so what it says of
/// the solution leaves them out. Each is heard by the folder's watcher or found written a moment ago (<see
/// cref="WrittenNow"/>), and let go of once the server holds it, or once a load of its project that read the disk after it
/// came has ended without it (a file the project leaves out). An open one is opened in the server only then. What waits
/// for them waits only for those that can change its answer (<see cref="ReachOfAsync"/>).</summary>
public sealed partial class CSharpService
{
    /// <summary>How long after a write a file may be on disk unheard by the folder's watcher.</summary>
    private static readonly TimeSpan Unheard = WorkspaceEdits.Catching;

    private readonly Dictionary<string, Arrival> arriving = new(PathNames.Comparer);

    /// <summary>The files a load of their project left out, with each one's time then: found written a moment ago again,
    /// unwritten since, one is not waited for again.</summary>
    private readonly Dictionary<string, DateTime> leftOut = new(PathNames.Comparer);

    /// <summary>When the server last asked for each project's design-time builds, by the project.</summary>
    private readonly Dictionary<string, DateTime> given = new(PathNames.Comparer);

    /// <summary>Done at the next load report, and made anew: what waits for a file to be taken in looks again then.</summary>
    private TaskCompletionSource reportHeard = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The wait that opens the files held back in the server once their time is up, cancelled once none is.</summary>
    private CancellationTokenSource? holding;

    /// <summary>A file come under a project (<paramref name="Project"/>), when it was on disk by (heard, or written as found),
    /// and whether a load of the project that read the disk since then has ended, after which the server holds it or never will.</summary>
    private sealed record Arrival(string Project, DateTime Heard)
    {
        public bool Decided { get; set; }
    }

    /// <summary>Whether a C# file come under one of the folder's projects may not be in it in the server yet.</summary>
    public bool Arriving => arriving.Count > 0;

    /// <summary>The folder's loaded projects as MSBuild evaluates them now (the session's), which say what references what;
    /// without a session, those of the workspace it was started on, which may not be evaluated yet.</summary>
    public Func<IReadOnlyList<ProjectModel>>? ProjectsNow { get; init; }

    /// <summary>The C# files of the folder's projects the server may not hold in their projects yet that <paramref
    /// name="counted"/> names by path and project (null: all), with <paramref name="looking"/> those written a moment ago the
    /// watcher may not have heard (an agent's), waited for up to <paramref name="within"/>: none once each is held or left
    /// out, else the rest; none from a server not loaded or without Passenger's extension. At once, on the thread, when none
    /// is waited for or looked for; <paramref name="waiting"/> is told the first waited for, and null once a wait is over.</summary>
    public async Task<IReadOnlyList<(string Path, string Project)>> UnplacedAsync(TimeSpan within, bool looking, Func<string, string, bool>? counted, Action<string?>? waiting,
        CancellationToken cancel)
    {
        UiThread.Check();
        long began = Stopwatch.GetTimestamp();
        if (session is not { IsLoaded: true } || workspace is not { } opened)
        {
            return [];
        }
        IReadOnlyList<ProjectModel> projects = opened.Projects;
        DateTime since = DateTime.UtcNow - Unheard;
        foreach ((string path, string project, DateTime written) in looking ? await BackgroundWork.Run(() => WrittenNow(projects, since), cancel) : [])
        {
            if (leftOut.GetValueOrDefault(path) != written)
            {
                Arrive(path, project, written);
            }
        }
        bool told = false;
        try
        {
            while (true)
            {
                if (session is not { IsLoaded: true } running || !arriving.Any(a => Counts(a.Key, a.Value.Project)))
                {
                    return [];
                }
                Task heard = reportHeard.Task;
                // Every one is asked about, so those held now are let go of whether counted or not.
                List<string> asked = [.. arriving.Keys];
                IReadOnlyList<Roslyn.Extension.TextHeld> texts;
                try
                {
                    texts = await running.HeldTextsAsync(asked, cancel);
                }
                catch (LanguageServerFailed)
                {
                    return [];
                }
                bool letGo = false;
                foreach (Roslyn.Extension.TextHeld text in texts)
                {
                    // One gone, held, or left out by a load of its project since it came is waited for no more.
                    if (arriving.TryGetValue(text.Path, out Arrival? arrival) && (text.Hash is not null || arrival.Decided || !File.Exists(text.Path)))
                    {
                        arriving.Remove(text.Path);
                        letGo = true;
                        if (text.Hash is null && arrival.Decided)
                        {
                            leftOut[text.Path] = Written(text.Path, File.GetLastWriteTimeUtc);
                        }
                    }
                }
                // An open one held back from the server goes to it now that its project has it.
                if (letGo && holding is not null && session == running)
                {
                    ConnectLoaded(running);
                }
                List<(string Path, string Project)> left = [.. asked.Where(p => arriving.TryGetValue(p, out Arrival? a) && Counts(p, a.Project)).Select(p => (p, arriving[p].Project))];
                TimeSpan spent = Stopwatch.GetElapsedTime(began);
                if (left.Count == 0 || spent >= within)
                {
                    return left;
                }
                waiting?.Invoke(left[0].Path);
                told = true;
                // Looked at again as a project's load ends, or after a moment where the server's words are not read.
                await Task.WhenAny(heard, Task.Delay(TimeSpan.FromMilliseconds(250), cancel));
                cancel.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            if (told)
            {
                waiting?.Invoke(null);
            }
        }

        bool Counts(string path, string project) => counted?.Invoke(path, project) != false;
    }

    /// <summary>Waits until the server holds an open file itself in its project, if it came under one since the server
    /// loaded the workspace, for up to <paramref name="within"/>: of one it holds in none it answers from its Miscellaneous
    /// Files, or from nothing. The file when it does not by then, else none.</summary>
    public Task<IReadOnlyList<(string Path, string Project)>> PlacedAsync(string path, TimeSpan within, bool looking, Action<string?>? waiting, CancellationToken cancel) =>
        UnplacedAsync(within, looking, (file, _) => PathNames.Same(file, path), waiting, cancel);

    /// <summary>Waits, for a change at an offset of an open file, until the server holds in their projects the C# files come
    /// under the projects it reaches (<see cref="ReachOfAsync"/>), the file itself first, for up to <paramref name="within"/>
    /// in all: worked out before, the change leaves each it holds in none out (a rename its callers there). The rest when
    /// the time is up, else none.</summary>
    public async Task<IReadOnlyList<(string Path, string Project)>> ReachedAsync(OpenDocument document, Func<int> offset, TimeSpan within, Action<string?>? waiting,
        CancellationToken cancel)
    {
        long began = Stopwatch.GetTimestamp();
        IReadOnlyList<(string Path, string Project)> itself = await PlacedAsync(document.Path, within, looking: false, waiting, cancel);
        if (itself.Count > 0 || !Arriving)
        {
            return itself;
        }
        Func<string, string, bool> reach = await ReachOfAsync(document, offset(), cancel);
        return await UnplacedAsync(within - Stopwatch.GetElapsedTime(began), looking: false, reach, waiting, cancel);
    }

    /// <summary>The C# files come under the projects that can hold the usages of the symbol at an offset of an open file
    /// (<see cref="ReachOfAsync"/>) that the server has not taken into them yet, looked at once: an answer given now says
    /// it went without them.</summary>
    public async Task<IReadOnlyList<(string Path, string Project)>> UntakenAsync(OpenDocument document, int offset, CancellationToken cancel) =>
        Arriving ? await UnplacedAsync(TimeSpan.Zero, looking: false, await ReachOfAsync(document, offset, cancel), null, cancel) : [];

    /// <summary>Which C# files come under the folder's projects can change what the server says of the symbol at an offset
    /// of an open file, by their paths and projects: those of the project that declares it (the file's own where the
    /// server names no declaration in the folder's code) and of every project that references that one, through others too;
    /// every one where no project holds the declaration.</summary>
    public async Task<Func<string, string, bool>> ReachOfAsync(OpenDocument document, int offset, CancellationToken cancel)
    {
        string declared = document.Path;
        if (For(document)?.Lsp is { } lsp)
        {
            try
            {
                if (await lsp.SymbolAtAsync(offset, cancel) is { Path: { } path })
                {
                    declared = PathNames.Canonical(path);
                }
            }
            catch (LanguageServerFailed)
            {
                // A server that cannot say has the file's own project stand for the declaration's.
            }
        }
        return ReachOf(HolderOf(declared) is { } project ? [project] : null, withDependents: true);
    }

    /// <summary>The folder's projects that can use what a C# file declares, by their files: the one holding it and every one
    /// that references it, through others too; none for a file no project of the folder's holds.</summary>
    public IReadOnlyList<string> ProjectsUsing(string path)
    {
        UiThread.Check();
        if (workspace is not { } opened || HolderOf(PathNames.Canonical(path)) is not { } project)
        {
            return [];
        }
        return [project, .. ProjectModel.Dependents(ProjectsNow?.Invoke() ?? opened.Projects, project)];
    }

    /// <summary>Waits, for an edit the server worked out, until it holds in their projects the C# files come under the
    /// projects of the C# files the edit reaches, for up to <paramref name="within"/>: the rest when the time is up, else
    /// none; and whether any was waited for, which the edit went without.</summary>
    public async Task<(IReadOnlyList<(string Path, string Project)> Left, bool Waited)> ReachedAsync(System.Text.Json.JsonElement edit, TimeSpan within, Action<string?>? waiting,
        CancellationToken cancel)
    {
        if (!Arriving)
        {
            return ([], false);
        }
        bool waited = false;
        IReadOnlyList<(string Path, string Project)> left = await UnplacedAsync(within, looking: false, ReachOf(WorkspaceEdits.Paths(edit)), file =>
        {
            waited |= file is not null;
            waiting?.Invoke(file);
        }, cancel);
        return (left, waited);
    }

    /// <summary>Which C# files come under the folder's projects can change an edit the server worked out, by their paths
    /// and projects: those of the projects holding the C# files it reaches; every one where no project holds one of them.</summary>
    private Func<string, string, bool> ReachOf(IEnumerable<string> reached)
    {
        List<string> projects = [];
        foreach (string path in reached.Where(IsCSharp))
        {
            if (HolderOf(PathNames.Canonical(path)) is not { } project)
            {
                return ReachOf(null, withDependents: false);
            }
            projects.Add(project);
        }
        return ReachOf(projects, withDependents: false);
    }

    /// <summary>The projects given, with every one that references one of them where <paramref name="withDependents"/>, as a
    /// test of an arrival's project; every project for none given.</summary>
    private Func<string, string, bool> ReachOf(IReadOnlyList<string>? projects, bool withDependents)
    {
        if (projects is null || workspace is not { } opened)
        {
            return (_, _) => true;
        }
        HashSet<string> reach = new(projects, PathNames.Comparer);
        if (withDependents)
        {
            IReadOnlyList<ProjectModel> now = ProjectsNow?.Invoke() ?? opened.Projects;
            foreach (string project in projects)
            {
                reach.UnionWith(ProjectModel.Dependents(now, project));
            }
        }
        return (_, project) => reach.Contains(project);
    }

    /// <summary>The C# files of the projects written since <paramref name="since"/>, with the project and time of each: a
    /// folder whose own time is older has had none come since, so only its files are looked at whose folder's time is newer.</summary>
    private static List<(string Path, string Project, DateTime Written)> WrittenNow(IReadOnlyList<ProjectModel> projects, DateTime since)
    {
        List<string> tops = [.. projects.Where(p => !p.IsProgram).Select(p => p.Folder).Distinct(PathNames.Comparer).Order(StringComparer.Ordinal)];
        // A project's folder inside another's is walked with it.
        tops = [.. tops.Where(top => !tops.Any(other => other != top && PathNames.IsUnder(top, other)))];
        List<(string, string, DateTime)> found = [];
        foreach (string top in tops)
        {
            foreach ((string folder, IReadOnlyList<string> files) in PathNames.FilesUnder(top, sub => !IgnoredFolders.Is(sub)))
            {
                if (Written(folder, Directory.GetLastWriteTimeUtc) < since)
                {
                    continue;
                }
                foreach (string file in files)
                {
                    if (IsCSharp(file) && Written(file, File.GetLastWriteTimeUtc) is { } written && written >= since && ProjectModel.Owning(projects, file) is { } owner)
                    {
                        found.Add((file, owner.Path, written));
                    }
                }
            }
        }
        return found;
    }

    private static DateTime Written(string path, Func<string, DateTime> time)
    {
        try
        {
            return time(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static bool IsCSharp(string path) => Languages.Language.ForPath(path).Service == Languages.LanguageService.CSharp;

    /// <summary>The C# files the folder's watcher heard come, under a project of the folder's, waited for from now on.</summary>
    private void Arrived(FolderChanges changes) => Came(changes.ComeOrGone);

    /// <summary>Files the IDE itself made, moved or deleted (canonical), which the folder's watcher hears as written: the C#
    /// files among them come under a project of the folder's are waited for from now on.</summary>
    public void FilesCameOrWent(IReadOnlyCollection<string> paths)
    {
        UiThread.Check();
        Came(paths);
    }

    /// <summary>Files come while the server loads the workspace are its load's to read, waited for as it is.</summary>
    private void Came(IEnumerable<string> paths)
    {
        if (session is not { IsLoaded: true } || workspace is null)
        {
            return;
        }
        DateTime now = DateTime.UtcNow;
        foreach (string path in paths)
        {
            if (IsCSharp(path) && HolderOf(path) is { } project)
            {
                leftOut.Remove(path);
                Arrive(path, project, now);
            }
        }
    }

    /// <summary>A file waited for, as first on disk while undecided (a load that read the disk after that read it), and
    /// again once decided only if it came since.</summary>
    private void Arrive(string path, string project, DateTime heard)
    {
        if (arriving.TryGetValue(path, out Arrival? was) && (was.Decided ? heard <= was.Heard : heard >= was.Heard))
        {
            return;
        }
        arriving[path] = new Arrival(project, heard);
    }

    /// <summary>A server asked for a project's design-time builds, from its thread.</summary>
    private void OnProjectGiven(LanguageServer from, ProjectGiven built)
    {
        string project = PathNames.Canonical(built.Project);
        UiThread.Post(() =>
        {
            if (session?.Server == from)
            {
                given[project] = given.GetValueOrDefault(project) > built.Asked ? given[project] : built.Asked;
            }
        });
    }

    /// <summary>A project's load ended, as its report said: each file come under it before its design-time builds were asked
    /// for is in it now or never will be (a server that builds its own projects asks none, so any load counts).</summary>
    private void Decide(string project)
    {
        bool fed = session?.Fed != false;
        DateTime asked = given.GetValueOrDefault(project, DateTime.MinValue);
        foreach (Arrival arrival in arriving.Values)
        {
            if (!arrival.Decided && PathNames.Same(arrival.Project, project) && (!fed || asked > arrival.Heard))
            {
                arrival.Decided = true;
            }
        }
        TaskCompletionSource heard = reportHeard;
        reportHeard = new(TaskCreationOptions.RunContinuationsAsynchronously);
        heard.TrySetResult();
    }

    /// <summary>Whether an open file is held back from the server, which would have it in Miscellaneous Files: it came under
    /// a project whose load since has not ended, and its time to wait is not up.</summary>
    private bool HeldBack(string path, out TimeSpan left)
    {
        left = TimeSpan.Zero;
        if (!arriving.TryGetValue(path, out Arrival? arrival) || arrival.Decided)
        {
            return false;
        }
        left = Patience.Limit - (DateTime.UtcNow - arrival.Heard);
        return left > TimeSpan.Zero;
    }

    /// <summary>Opens the files held back once their time is up, held as the IDE's work until then; let go of at once when
    /// none is held back any more.</summary>
    private void HoldBack(TimeSpan? left)
    {
        if (left is not { } wait)
        {
            holding?.Cancel();
            holding = null;
            return;
        }
        if (holding is not null)
        {
            return;
        }
        CancellationTokenSource cancel = holding = new();
        IdeErrors.Observe(LetInAsync(wait, cancel));
    }

    private async Task LetInAsync(TimeSpan wait, CancellationTokenSource cancel)
    {
        using (BackgroundWork.Scope())
        {
            try
            {
                await Task.Delay(wait, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (holding == cancel)
                {
                    holding = null;
                }
                cancel.Dispose();
            }
        }
        if (session is { } running && !disposed)
        {
            ConnectLoaded(running);
        }
    }

    /// <summary>A new server reads every file as it loads: nothing heard before it is waited for.</summary>
    private void ForgetArrivals()
    {
        arriving.Clear();
        leftOut.Clear();
        given.Clear();
        HoldBack(null);
    }
}
