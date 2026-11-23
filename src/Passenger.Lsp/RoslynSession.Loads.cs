using Passenger.Core;
using Passenger.Roslyn.Extension;

namespace Passenger.Lsp;

/// <summary>The loads under way of the projects the server was given builds for: each reads the project's C# files and its
/// restore's assets file, and Windows moves no folder with a file under it open, so a folder let go of waits for those
/// reading there (<see cref="LetGo"/>). A load ends as the server's log says the project loaded, in the server's own words
/// (<see cref="LoaderReady"/>), or met a warning or error, or as the server ends; never at its batch's end, which a load
/// of another project held for the same folder keeps from coming.</summary>
public sealed partial class RoslynSession
{
    /// <summary>The loads under way, under <see cref="goingGate"/>.</summary>
    private readonly List<GivenLoad> loads = [];

    /// <summary>The words the server's log says a project loaded in, before its path and after it; null for a server without
    /// Passenger's loader, which is given no builds.</summary>
    private (string Before, string After)? loadedWords;

    /// <summary>The project whose next load's end is held for a test (<see cref="HoldLoadEnd"/>).</summary>
    private (string Project, Task Until, TaskCompletionSource Reached)? endHeld;

    /// <summary>A project's load under way and the folders it reads: the project's own and those its builds write, where
    /// its assets file lies.</summary>
    private sealed class GivenLoad(string project, IReadOnlyList<string> reads)
    {
        public string Project => project;

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether it reads in a folder about to go or move, or around it.</summary>
        public bool Reaches(string folder) => reads.Any(read => PathNames.Within(read, folder) || PathNames.Within(folder, read));
    }

    /// <summary>Takes the server's words for a project loaded, from its answer that it has Passenger's loader.</summary>
    private void Heard(LoaderReady loader)
    {
        const string Place = "{0}";
        int at = loader.Loaded.IndexOf(Place, StringComparison.Ordinal);
        lock (goingGate)
        {
            loadedWords = at < 0 ? null : (loader.Loaded[..at], loader.Loaded[(at + Place.Length)..].TrimEnd());
        }
    }

    /// <summary>A project's load begun from builds given it, before the server has them: one of it before has ended, as the
    /// server loads a project once at a time.</summary>
    private void Loading(string project, string infos)
    {
        string[] reads = [.. Path.GetDirectoryName(project) is { Length: > 0 } folder ? [folder] : Array.Empty<string>(), .. WatchedFiles.WrittenFolders(infos) ?? []];
        lock (goingGate)
        {
            foreach (GivenLoad before in loads.Where(l => PathNames.Same(l.Project, project)).ToList())
            {
                loads.Remove(before);
                before.Ended.TrySetResult();
            }
            loads.Add(new GivenLoad(project, reads));
        }
    }

    /// <summary>The loads a line of the server's log says ended (<see cref="Ends"/>), each ended now or, held for a test,
    /// once let go, under way until then.</summary>
    private void HearLoads(string logged)
    {
        lock (goingGate)
        {
            if (loads.Count == 0)
            {
                return;
            }
        }
        string said = Unprefixed(logged);
        List<GivenLoad> ended;
        lock (goingGate)
        {
            ended = [.. loads.Where(l => Ends(said, l.Project))];
            if (endHeld is { } hold && ended.Any(l => PathNames.Same(l.Project, hold.Project)))
            {
                endHeld = null;
                hold.Reached.TrySetResult();
                UnawaitedWork.Observe(EndAfterAsync(hold.Until, ended));
                return;
            }
        }
        End(ended);
    }

    private async Task EndAfterAsync(Task until, IReadOnlyList<GivenLoad> ended)
    {
        await until.Ended().ConfigureAwait(false);
        End(ended);
    }

    /// <summary>Loads that ended, no longer under way.</summary>
    private void End(IReadOnlyList<GivenLoad> ended)
    {
        lock (goingGate)
        {
            foreach (GivenLoad load in ended)
            {
                loads.Remove(load);
            }
        }
        foreach (GivenLoad load in ended)
        {
            load.Ended.TrySetResult();
        }
    }

    /// <summary>Every load under way ended, as the server has.</summary>
    private void EndLoads()
    {
        List<GivenLoad> ended;
        lock (goingGate)
        {
            ended = [.. loads];
        }
        End(ended);
    }

    /// <summary>Whether a line of the server's log says a project's load ended: loaded, in the server's own words, or with a
    /// warning or error ("Error while loading P: …", the kind's name and its words the same in every language).</summary>
    private bool Ends(string said, string project)
    {
        if (loadedWords is (string before, string after) && said.Length == before.Length + project.Length + after.Length
            && said.StartsWith(before, StringComparison.Ordinal) && said.EndsWith(after, StringComparison.Ordinal)
            && PathNames.Same(said.Substring(before.Length, project.Length), project))
        {
            return true;
        }
        foreach (string kind in (string[])["Error", "Warning"])
        {
            string lead = $"{kind} while loading ";
            if (said.StartsWith(lead, StringComparison.Ordinal) && said.Length > lead.Length + project.Length + 1
                && PathNames.Same(said.Substring(lead.Length, project.Length), project) && said.AsSpan(lead.Length + project.Length).StartsWith(": "))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A line of the server's log without the bracketed names its logger puts first ("[passenger/loadProjects]").</summary>
    private static string Unprefixed(string logged)
    {
        ReadOnlySpan<char> said = logged.AsSpan().Trim();
        while (said.StartsWith("[") && said.IndexOf(']') is int end and > 0)
        {
            said = said[(end + 1)..].TrimStart();
        }
        return said.ToString();
    }

    /// <summary>Holds the end of the next load of <paramref name="project"/> the server's log says ended until
    /// <paramref name="until"/> has, as a busy machine holds a load, for a test: done once one is held.</summary>
    public Task HoldLoadEnd(string project, Task until)
    {
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (goingGate)
        {
            endHeld = (project, until, reached);
        }
        return reached.Task;
    }
}
