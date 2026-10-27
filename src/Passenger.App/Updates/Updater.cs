using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Passenger.Core;
using Velopack.Locators;

namespace Passenger.App.Updates;

/// <summary>How this copy of Passenger installs a newer release: it downloads the release's package for this system in
/// the background, checks it against the SHA-256 the release gives (and the app's signature on macOS), and installs it
/// as Passenger ends, starting the new version when the person chose Restart to Update. Only a copy that Passenger's
/// installer (Windows) or disk image (macOS) put in place has one.</summary>
public abstract partial class Updater
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
    /// relays (<c>--mcp-attach</c>), which the install ends first (<see cref="EndRelays"/>), each with the folder it works
    /// in, and anything else, which holds the install back until it closes.</summary>
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
                if (OperatingSystem.IsWindows() || Signal(relay.ProcessId, Terminate) != 0)
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

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Signal(int process, int signal);

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
