using System.Diagnostics;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>The C# files come under one of the folder's projects since the server loaded it, until the server holds each
/// in its project: before, the server has an open one in Miscellaneous Files and the rest in nothing, so what it says of
/// the solution leaves them out. Each is heard by the folder's watcher or found written a moment ago (<see
/// cref="WrittenNow"/>), and let go of once the server holds it, or once a load of its project that read the disk after it
/// came has ended without it (a file the project leaves out). An open one is opened in the server only then.</summary>
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

    /// <summary>The C# files of the folder's projects the server may not hold in their projects yet, with <paramref
    /// name="looking"/> those written a moment ago the watcher may not have heard (an agent's), waited for up to <paramref
    /// name="within"/>: none once each is held or left out, else the rest, each with its project; none from a server not
    /// loaded or without Passenger's extension. Done at once, on the thread, when none is waited for and none looked for;
    /// <paramref name="waiting"/> is told the first file waited for, and null once a wait is over.</summary>
    public async Task<IReadOnlyList<(string Path, string Project)>> UnplacedAsync(TimeSpan within, bool looking, Action<string?>? waiting, CancellationToken cancel)
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
                if (session is not { IsLoaded: true } running || arriving.Count == 0)
                {
                    return [];
                }
                Task heard = reportHeard.Task;
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
                foreach (Roslyn.Extension.TextHeld text in texts)
                {
                    // One gone, held, or left out by a load of its project since it came is waited for no more.
                    if (arriving.TryGetValue(text.Path, out Arrival? arrival) && (text.Hash is not null || arrival.Decided || !File.Exists(text.Path)))
                    {
                        arriving.Remove(text.Path);
                        if (text.Hash is null && arrival.Decided)
                        {
                            leftOut[text.Path] = Written(text.Path, File.GetLastWriteTimeUtc);
                        }
                    }
                }
                List<(string Path, string Project)> left = [.. asked.Where(arriving.ContainsKey).Select(p => (p, arriving[p].Project))];
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
