using System.Diagnostics;
using System.Globalization;
using Passenger.Core;
using Velopack.Locators;

namespace Passenger.App.Updates;

/// <summary>How this copy of Passenger installs a newer release: it downloads the release's package for this system in
/// the background, checks it against the SHA-256 the release gives (and the app's signature on macOS), and installs it
/// as Passenger ends, starting the new version when the person chose Restart to Update. Only a copy that Passenger's
/// installer (Windows) or disk image (macOS) put in place has one.</summary>
public abstract class Updater
{
    /// <summary>Where this copy is installed, for the log.</summary>
    public abstract string Installed { get; }

    /// <summary>The version made ready to install, or null.</summary>
    public string? Ready { get; protected set; }

    /// <summary>Downloads the release's package for this system and checks it, off the caller's thread, reporting the
    /// percent done. Never throws but for <paramref name="cancel"/>: a refusal or a failure is an answer.</summary>
    public async Task<UpdateOutcome> DownloadAsync(LatestRelease release, Action<int> progress, CancellationToken cancel)
    {
        Ready = null;
        try
        {
            UpdateOutcome outcome = await PrepareAsync(release, progress, cancel).ConfigureAwait(false);
            if (outcome is UpdateOutcome.Ready)
            {
                Ready = release.Version;
            }
            return outcome;
        }
        catch (UpdateRefusedException refused)
        {
            await DiscardAsync().ConfigureAwait(false);
            return new UpdateOutcome.Refused(refused.Message,
                $"Passenger {release.Version} was not installed: {refused.Message}{(refused.Detail is { } detail ? $" ({detail})" : string.Empty)}");
        }
        catch (Exception error) when (error is UpdateFailedException or IOException or UnauthorizedAccessException or TimeoutException
            or System.ComponentModel.Win32Exception)
        {
            await DiscardAsync().ConfigureAwait(false);
            return new UpdateOutcome.Failed(error.Message, $"could not download Passenger {release.Version}: {error.Message}");
        }
    }

    /// <summary>What <see cref="DownloadAsync"/> does for this system: throws <see cref="UpdateRefusedException"/> for a
    /// package that is not what the release says, <see cref="UpdateFailedException"/> for one that would not come.</summary>
    protected abstract Task<UpdateOutcome> PrepareAsync(LatestRelease release, Action<int> progress, CancellationToken cancel);

    /// <summary>Installs the version made ready, as the process ends (its window closed, what it started ended), and
    /// starts it when <paramref name="restart"/>. A failure is logged, and leaves the copy as it was.</summary>
    public abstract void Install(bool restart);

    /// <summary>Drops a download made or under way, as Check for updates turned off asks.</summary>
    public abstract Task DiscardAsync();

    /// <summary>The other processes running from this install, by id: each system's own look.</summary>
    protected abstract IReadOnlyList<int> RunningHere();

    /// <summary>What runs from this install besides this process, whose files an install would change under it: agents'
    /// relays (<c>--mcp-attach</c>), which the install has follow it (<see cref="FollowRelays"/>), each with the folder it
    /// works in, and anything else, which holds the install back until it closes.</summary>
    public IReadOnlyList<OtherCopy> Others() =>
        [.. RunningHere().Select(id => new OtherCopy(id, Mcp.McpProxy.RelayAt(id)))];

    /// <summary>A program in the install's way as a person is told of it: its process id and command line, or its
    /// executable where the system gives no command line.</summary>
    public static async Task<string> NamedAsync(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                ChildResult shown = await ChildProcesses.RunAsync(ChildProcesses.Command("/bin/ps", ["-o", "args=", "-p", processId.ToString(CultureInfo.InvariantCulture)]),
                    limit: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                if (shown.Succeeded && shown.Output.Trim() is { Length: > 0 } command)
                {
                    return $"process {processId}: {command}";
                }
            }
            catch (Exception error) when (error is TimeoutException or System.ComponentModel.Win32Exception)
            {
                // Named by its executable instead.
            }
        }
        try
        {
            using Process running = Process.GetProcessById(processId);
            return $"process {processId}: {running.MainModule?.FileName ?? running.ProcessName}";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"process {processId}, which has ended since";
        }
    }

    /// <summary>The relays ordered to follow the update (<see cref="HoldRelays"/>), until it is in place or given up.</summary>
    private readonly HashSet<int> ordered = [];

    /// <summary>Orders the agents' relays running from this install to follow the update about to be installed, on macOS
    /// and Linux: a call of theirs that finds no window waits for the updated app's from now on, rather than starting the
    /// app being replaced. Taken back by <see cref="LetRelaysGo"/>; nothing on Windows, where the install ends them.</summary>
    public void HoldRelays()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (OtherCopy relay in Others().Where(other => other.Relay))
            {
                Order(relay);
            }
        }
    }

    /// <summary>Takes back each order to follow given (<see cref="HoldRelays"/>): the updated app is in place, or there is
    /// no update after all.</summary>
    public void LetRelaysGo()
    {
        int[] given;
        lock (ordered)
        {
            given = [.. ordered];
            ordered.Clear();
        }
        foreach (int relay in given)
        {
            try
            {
                Mcp.McpProxy.Unordered(relay);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                IdeErrors.Write($"Updates: the agent's relay (process {relay}) was not told the update is in place, and waits up to two minutes for it: {error.Message}");
            }
        }
    }

    private bool Order(OtherCopy relay)
    {
        try
        {
            Mcp.McpProxy.OrderFollowing(relay.ProcessId);
            lock (ordered)
            {
                ordered.Add(relay.ProcessId);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Updates: the agent's relay on {relay.Folder} (process {relay.ProcessId}) cannot be told to follow the update: {error.Message}");
            return false;
        }
    }

    /// <summary>Has the agents' relays running from this install let go of it before it is replaced, each to start again
    /// from the updated app in its own process, as its agent's session: each ordered to follow, then SIGTERM, which it
    /// answers by waiting in a shell, off the app, until <see cref="LetRelaysGo"/>. One that has not let go within
    /// <see cref="RelayEnding"/> is ended (<see cref="EndRelays"/>), as every relay is on Windows, where a process cannot
    /// take another's place.</summary>
    protected void FollowRelays(IReadOnlyList<OtherCopy> others, string version)
    {
        if (OperatingSystem.IsWindows())
        {
            EndRelays(others, version);
            return;
        }
        List<OtherCopy> told = [];
        foreach (OtherCopy relay in others.Where(other => other.Relay))
        {
            if (Order(relay) && Libc.Signal(relay.ProcessId, Terminate) == 0)
            {
                told.Add(relay);
            }
        }
        Stopwatch waited = Stopwatch.StartNew();
        TimeSpan limit = Waits.Of(RelayEnding);
        List<OtherCopy> holding = [];
        foreach (OtherCopy relay in told)
        {
            TimeSpan left = limit - waited.Elapsed;
            if (SpinWait.SpinUntil(() => Mcp.McpProxy.LetGo(relay.ProcessId) || Libc.Signal(relay.ProcessId, 0) != 0, left > TimeSpan.Zero ? left : TimeSpan.Zero)
                && Mcp.McpProxy.LetGo(relay.ProcessId))
            {
                IdeErrors.Write($"Updates: the agent's relay on {relay.Folder} (process {relay.ProcessId}) let go of the app to install Passenger {version}, "
                    + "and starts again from it as its agent's session");
            }
            else if (Libc.Signal(relay.ProcessId, 0) == 0)
            {
                Mcp.McpProxy.Unordered(relay.ProcessId);
                holding.Add(relay);
            }
        }
        if (holding.Count > 0)
        {
            IdeErrors.Write($"Updates: {Plural.Of(holding.Count, "agent's relay", "agents' relays")} did not let go of the app within {limit.TotalSeconds:0} s, and {(holding.Count == 1 ? "is" : "are")} ended");
            EndRelays(holding, version);
        }
    }

    /// <summary>The programs in the install's way, named for the log, as the process ends.</summary>
    protected static string Listed(IEnumerable<OtherCopy> blocking) =>
        string.Join("; ", blocking.Select(other => Synchronously.Run(() => NamedAsync(other.ProcessId))));

    /// <summary>Ends the agents' relays running from this install before it is replaced, each told why first, which it
    /// says on its standard error: SIGTERM, then a kill for one still running after <see cref="RelayEnding"/>; on Windows
    /// a kill at once. Only the relay itself, never its process group, which is its agent's. Each agent reconnects.</summary>
    protected void EndRelays(IReadOnlyList<OtherCopy> others, string version)
    {
        string why = $"Passenger {version} is being installed in {Installed}, which ends this agent's connection to Passenger. "
            + "Reconnect the agent to Passenger to go on (/mcp in Claude Code).";
        List<(OtherCopy Relay, Process Process)> ending = [];
        foreach (OtherCopy relay in others.Where(o => o.Relay))
        {
            Process process;
            try
            {
                process = Process.GetProcessById(relay.ProcessId);
            }
            catch (ArgumentException)
            {
                // Ended meanwhile.
                continue;
            }
            try
            {
                Mcp.McpProxy.TellEnding(relay.ProcessId, why);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                IdeErrors.Write($"Updates: the agent's relay on {relay.Folder} (process {relay.ProcessId}) ends without being told why: {error.Message}");
            }
            try
            {
                if (OperatingSystem.IsWindows() || Libc.Signal(relay.ProcessId, Terminate) != 0)
                {
                    process.Kill();
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Ended meanwhile.
            }
            ending.Add((relay, process));
        }
        TimeSpan limit = Waits.Of(RelayEnding);
        foreach ((OtherCopy relay, Process process) in ending)
        {
            using (process)
            {
                try
                {
                    string how = "it ended";
                    if (!process.WaitForExit(limit))
                    {
                        process.Kill();
                        process.WaitForExit(limit);
                        how = $"killed, still running {limit.TotalSeconds:0} s after it was told to end";
                    }
                    IdeErrors.Write($"Updates: ended the agent's relay on {relay.Folder} (process {relay.ProcessId}) to install Passenger {version}: {how}");
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    IdeErrors.Write($"Updates: the agent's relay on {relay.Folder} (process {relay.ProcessId}) could not be ended: {error.Message}");
                }
            }
        }
    }

    /// <summary>How long a relay told to end has to end itself before it is killed.</summary>
    private static readonly TimeSpan RelayEnding = TimeSpan.FromSeconds(5);

    /// <summary>SIGTERM, as macOS and Linux number it.</summary>
    private const int Terminate = 15;

    /// <summary>The updater of the running copy, or null with why it cannot update itself: not in a disk image's app
    /// bundle or an installer's folder, or where it cannot replace itself.</summary>
    public static Updater? OfThisCopy(string stateFolder, out string why)
    {
        if (OperatingSystem.IsMacOS())
        {
            return DiskImageUpdater.OfThisBundle(Path.Combine(stateFolder, "updates"), out why);
        }
        if (OperatingSystem.IsWindows())
        {
            if (!Installation.Installed || !VelopackLocator.IsCurrentSet || VelopackLocator.Current.CurrentlyInstalledVersion is null)
            {
                why = $"{AppContext.BaseDirectory} is not where Passenger's installer put it";
                return null;
            }
            why = string.Empty;
            return new VelopackUpdater(VelopackLocator.Current);
        }
        why = "Passenger installs updates itself on macOS and Windows";
        return null;
    }
}

/// <summary>Another process running from the install: an agent's relay, with the folder it works in, when
/// <paramref name="Folder"/> is given.</summary>
public sealed record OtherCopy(int ProcessId, string? Folder)
{
    public bool Relay => Folder is not null;
}

/// <summary>What downloading an update came to, with a line for the IDE's log.</summary>
public abstract record UpdateOutcome(string Said)
{
    /// <summary>Downloaded and checked, to install as Passenger ends.</summary>
    public sealed record Ready(string Said) : UpdateOutcome(Said);

    /// <summary>The release carries nothing this copy installs (no package for this system), so it is only announced.</summary>
    public sealed record Unavailable(string Said) : UpdateOutcome(Said);

    /// <summary>Downloaded, but not what the release says it is: removed, and the person told why.</summary>
    public sealed record Refused(string Reason, string Said) : UpdateOutcome(Said);

    /// <summary>Not downloaded: no network, an error, a full disk.</summary>
    public sealed record Failed(string Reason, string Said) : UpdateOutcome(Said);
}
