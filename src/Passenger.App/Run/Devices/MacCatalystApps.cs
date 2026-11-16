using System.Diagnostics;
using System.Runtime.InteropServices;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.App.Run.Devices;

// A Mac Catalyst app's run here: its plan made from its build, and its process found and ended with the run.
public static partial class MacCatalystApps
{
    /// <summary>A built plan's app read from its build, the plan then opening it (<see cref="Opening"/>, through
    /// <see cref="Shell"/>); a plan of no Mac Catalyst app as it is; why not, in words for the person.</summary>
    public static async Task<(LaunchPlan? Plan, string? Problem)> ReadyAsync(LaunchPlan plan, IReadOnlyDictionary<string, string> globals, CancellationToken cancel)
    {
        if (plan.MacCatalyst is not { App: null } launch || plan.Project is not { } project)
        {
            return (plan, null);
        }
        MSBuildAnswer said = await MSBuildQuestions.AskAsync(project, launch.Framework, globals,
            new MSBuildQuestion([MSBuildNames.ComputeRunArguments]) { Properties = [MSBuildNames.AppBundleDir, MSBuildNames.NativeExecutableName] }, cancel);
        string name = Path.GetFileNameWithoutExtension(project);
        if (!said.Succeeded || said.Property(MSBuildNames.AppBundleDir) is not { Length: > 0 } bundle
            || said.Property(MSBuildNames.NativeExecutableName) is not { Length: > 0 } program)
        {
            return (null, $"MSBuild did not say where {name}'s Mac Catalyst app is: {said.Said.FirstLine}");
        }
        string folder = PathNames.Canonical(Path.Combine(Path.GetDirectoryName(project)!, bundle));
        MacCatalystApp app = new(folder, Path.Combine(MacBundles.Programs(folder), program));
        if (!File.Exists(app.Executable))
        {
            return (null, $"Could not run {name}: its Mac Catalyst app {Path.GetFileName(folder)} has not been built");
        }
        IReadOnlyList<string> opening = Opening(app, plan.Unseen, [.. plan.OwnArguments]);
        return (plan with
        {
            Program = Shell,
            Arguments = opening,
            Leading = opening.Count - plan.OwnArguments.Count(),
            MacCatalyst = launch with { App = app },
        }, null);
    }

    /// <summary>How often a start looks for its app among the processes.</summary>
    private static readonly TimeSpan Looking = TimeSpan.FromMilliseconds(100);

    /// <summary>The process of <paramref name="app"/> started since <paramref name="since"/> (the wall clock's, as the system
    /// keeps a process's start), looked for until it is found or its opener has <paramref name="ended"/>, then once more;
    /// null where none is.</summary>
    public static async Task<int?> StartedAsync(MacCatalystApp app, DateTime since, Task ended, CancellationToken cancel = default)
    {
        while (true)
        {
            bool last = ended.IsCompleted;
            if (Find(app.Executable, since) is int found)
            {
                return found;
            }
            if (last)
            {
                return null;
            }
            await Task.WhenAny(ended, Task.Delay(Looking, cancel));
            cancel.ThrowIfCancellationRequested();
        }
    }

    /// <summary>The app's process adopted, so it ends with Passenger as its children do, and ended with its run: once its
    /// opener has <paramref name="ended"/> (the app ended, or Stop ended the opener), whatever of it is left goes. Never throws.</summary>
    public static async Task EndWithAsync(MacCatalystApp app, DateTime since, Task ended)
    {
        if (await StartedAsync(app, since, ended) is not int id)
        {
            return;
        }
        ChildProcesses.Adopt(id, new ProcessStartInfo(app.Executable));
        await ended.Ended();
        // Its id is another's once it has gone: only the app's own program is ended.
        if (Libc.ExecutableOf(id) is { } running && PathNames.Same(running, app.Executable))
        {
            ChildProcesses.KillLeft(id);
        }
    }

    /// <summary>The first process running <paramref name="executable"/> that started at or after <paramref name="since"/>.</summary>
    private static int? Find(string executable, DateTime since)
    {
        int[] ids = new int[4096];
        int count = ListAllProcesses(ids, ids.Length * sizeof(int));
        // A second of leeway: the system keeps a process's start to the microsecond, the caller's moment may round.
        DateTime from = since - TimeSpan.FromSeconds(1);
        for (int i = 0; i < Math.Min(count, ids.Length); i++)
        {
            if (ids[i] > 0 && Libc.ExecutableOf(ids[i]) is { } program && PathNames.Same(program, executable)
                && DotnetProcesses.StartedAt(ids[i]) is { } started && started >= from)
            {
                return ids[i];
            }
        }
        return null;
    }

    /// <summary>proc_listallpids: the ids of every process, as many as fit; their count.</summary>
    [LibraryImport("libc", EntryPoint = "proc_listallpids")]
    private static partial int ListAllProcesses(int[] ids, int size);
}
