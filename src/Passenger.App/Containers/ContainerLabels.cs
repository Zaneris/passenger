using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>The labels on everything Passenger has an engine make (containers, networks): the state folder it keeps state
/// in, by a hash of it, the machine and the process that made it. No child holds a container, so Passenger ends them by
/// these labels: with the folder that started them (<see cref="FolderContainers"/>), on macOS and Linux as the process ends
/// however it ends (the watchdog's last command, <see cref="ChildProcesses.AtIdeEnd"/>), and at the next start, which
/// removes those of this state and machine whose process is gone (<see cref="RemoveLeftoversAsync"/>). What is made for
/// Passenger without its labels is recorded with the same three and ended by the same rules (<see cref="IsLeftover"/>,
/// <see cref="MadeUnlabelled"/>). The watchdog's command is given the CLIs' variables (<see cref="EngineVariables"/>) as
/// the IDE has them.</summary>
public static partial class ContainerLabels
{
    public const string State = "net.passenger-ide.state";
    public const string Process = "net.passenger-ide.process";
    public const string Folder = "net.passenger-ide.folder";
    public const string Machine = "net.passenger-ide.machine";

    /// <summary>The last command the watchdog was given, told again when the engine it reaches or what it removes changes.</summary>
    private static string? toldWatchdog;

    /// <summary>The CLI and context the watchdog's command reaches the engine through: the engine the IDE's commands reach.</summary>
    private static (string Cli, string? Context)? reached;

    /// <summary>The containers made for this process that carry none of its labels, by id, which the watchdog's command
    /// removes with the labelled ones.</summary>
    private static readonly SortedSet<string> Unlabelled = new(StringComparer.Ordinal);

    /// <summary>The networks made for this process that carry none of its labels (an AppHost's session network), by name,
    /// which the watchdog's command removes once the containers on them have gone.</summary>
    private static readonly SortedSet<string> UnlabelledNetworks = new(StringComparer.Ordinal);

    /// <summary>What was made for this process without its labels on another engine than the one the IDE's commands reach
    /// (an AppHost's orchestrator's own runtime: Podman's beside Docker's), by the CLI and words that reach it.</summary>
    private static readonly SortedDictionary<string, MadeOn> Elsewhere = new(StringComparer.Ordinal);

    /// <summary>The containers and networks made on another engine, and the CLI and words that reach it.</summary>
    private sealed record MadeOn(string Cli, IReadOnlyList<string> Arguments)
    {
        public SortedSet<string> Containers { get; } = new(StringComparer.Ordinal);

        public SortedSet<string> Networks { get; } = new(StringComparer.Ordinal);
    }

    private static readonly object Watchdog = new();

    /// <summary>The state folder's <c>containers.json</c>, the process's: whether anything was ever labelled for
    /// it.</summary>
    private static readonly Lazy<global::Passenger.App.State.StateDocument<ContainersState>> Kept =
        new(() => new(Path.Combine(global::Passenger.App.State.StateStore.DefaultRoot, "containers.json")));

    /// <summary>Whether Passenger ever labelled something for this state folder: a start looks for what a crash left only
    /// then, so a person who never used containers here runs no docker command as Passenger starts. Any thread.</summary>
    public static bool EverLabelled => Kept.Value.Peek().Labelled;

    /// <summary>Writes the record of a first labelling now, rather than a moment later: for a test that starts the next
    /// Passenger at once.</summary>
    public static void Flush() => Kept.Value.Flush();

    /// <summary>The state folder's label value: the first 16 hex digits of a SHA-256 of its path.</summary>
    public static string StateValue => StateValueOf(global::Passenger.App.State.StateStore.DefaultRoot);

    /// <summary>This process's label value: its id and start time, so a process given the same id later is another.</summary>
    public static string ProcessValue { get; } = Of(Environment.ProcessId, StartOf(System.Diagnostics.Process.GetCurrentProcess()));

    /// <summary>A process's label value, for a process by its id and start time (Unix seconds).</summary>
    public static string Of(int processId, long started) => string.Create(CultureInfo.InvariantCulture, $"{processId}-{started}");

    /// <summary>A folder's label value: the first 16 hex digits of a SHA-256 of its canonical path.</summary>
    public static string FolderValue(string folder) => Hash(folder);

    /// <summary>A state folder's label value, as the Passenger keeping its state there labels what it makes.</summary>
    public static string StateValueOf(string stateFolder)
    {
        try
        {
            return Hash(PathNames.Canonical(stateFolder));
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return Hash(stateFolder);
        }
    }

    /// <summary>This machine's label value: a hash of its name and its own id (Linux's machine-id, macOS's hardware UUID,
    /// Windows' MachineGuid), so an engine several machines share tells their Passengers' leftovers apart.</summary>
    public static string MachineValue { get; } = Hash($"{Environment.MachineName}\n{MachineId()}");

    /// <summary>The <c>--label</c> words for something made for a folder (or none): the watchdog told to remove what this
    /// process made once it has ended (again whenever the engine it would reach changes), and the state folder told
    /// something was labelled for it. UI thread.</summary>
    public static IReadOnlyList<string> Arguments(ContainerEngine engine, string? folder)
    {
        UiThread.Check();
        TellWatchdog(engine);
        if (!Kept.Value.Value.Labelled)
        {
            Kept.Value.Set(new ContainersState { Labelled = true });
        }
        List<string> words = ["--label", $"{State}={StateValue}", "--label", $"{Machine}={MachineValue}", "--label", $"{Process}={ProcessValue}"];
        if (folder is not null)
        {
            words.AddRange(["--label", $"{Folder}={FolderValue(folder)}"]);
        }
        return words;
    }

    /// <summary>Containers made for this process that carry none of its labels (an Aspire AppHost's, made by its
    /// orchestrator), by id, and the networks made with them (its session network), by name, on the engine the IDE's
    /// commands reach: removed with the labelled ones by the watchdog's last command, told again now, as the process ends
    /// however it ends. Any thread.</summary>
    public static void MadeUnlabelled(ContainerEngine engine, IEnumerable<string> ids, IEnumerable<string>? networks = null)
    {
        lock (Watchdog)
        {
            Unlabelled.UnionWith(ids);
            UnlabelledNetworks.UnionWith(networks ?? []);
        }
        TellWatchdog(engine);
    }

    /// <summary>As <see cref="MadeUnlabelled"/>, on another engine than the one the IDE's commands reach (an AppHost's
    /// orchestrator's own runtime), reached through its CLI with the words given (a context, a host). Any thread.</summary>
    public static void MadeElsewhere(string cli, IReadOnlyList<string> arguments, IEnumerable<string> ids, IEnumerable<string>? networks = null)
    {
        lock (Watchdog)
        {
            string key = $"{cli}\n{string.Join('\n', arguments)}";
            if (!Elsewhere.TryGetValue(key, out MadeOn? made))
            {
                Elsewhere[key] = made = new MadeOn(cli, [.. arguments]);
            }
            made.Containers.UnionWith(ids);
            made.Networks.UnionWith(networks ?? []);
        }
        TellWatchdog(null);
    }

    /// <summary>Containers of <see cref="MadeUnlabelled"/> or <see cref="MadeElsewhere"/> that have gone (removed, or no
    /// longer this process's), and networks made with them, which the watchdog's command no longer names. Any thread.</summary>
    public static void EndedUnlabelled(IEnumerable<string> ids, IEnumerable<string>? networks = null)
    {
        lock (Watchdog)
        {
            string[] gone = [.. ids];
            string[] goneNetworks = [.. networks ?? []];
            Unlabelled.ExceptWith(gone);
            UnlabelledNetworks.ExceptWith(goneNetworks);
            foreach ((string key, MadeOn made) in Elsewhere.ToArray())
            {
                made.Containers.ExceptWith(gone);
                made.Networks.ExceptWith(goneNetworks);
                if (made.Containers.Count + made.Networks.Count == 0)
                {
                    Elsewhere.Remove(key);
                }
            }
        }
        TellWatchdog(null);
    }

    /// <summary>Tells the watchdog the command that removes what this process made, when it changed: through the CLI as the
    /// engine's last state found it (else as it was told before), so finding it never waits on the caller's thread.</summary>
    private static void TellWatchdog(ContainerEngine? engine)
    {
        lock (Watchdog)
        {
            if (engine?.Known is { Cli: { } cli } known)
            {
                reached = (cli, known.Context);
            }
            if (EndCommand() is { } end && end != toldWatchdog)
            {
                toldWatchdog = end;
                ChildProcesses.AtIdeEnd(end);
            }
        }
    }

    /// <summary>The shell command that removes this process's containers and networks, for the watchdog, which runs it with
    /// its own environment: the engine the IDE's commands reach is put in it, the CLIs' variables as the IDE has them and
    /// its context by name; what was made for it without its labels is named, its containers by id and then their networks
    /// by name, on the engine that holds them (that one, or another an orchestrator chose). Null for a value no line can
    /// hold, or with nothing to reach. Under the lock.</summary>
    private static string? EndCommand()
    {
        StringBuilder command = new();
        foreach (string variable in EngineVariables.All)
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                command.Append(CultureInfo.InvariantCulture, $"export {variable}={Quoted(value)}; ");
            }
        }
        int begun = command.Length;
        if (reached is { } through)
        {
            string docker = Quoted(through.Cli);
            if (through.Context is { Length: > 0 } context && ContextName().IsMatch(context))
            {
                docker += $" --context {Quoted(context)}";
            }
            string labels = $"--filter {Quoted($"label={Process}={ProcessValue}")} --filter {Quoted($"label={Machine}={MachineValue}")}";
            command.Append(CultureInfo.InvariantCulture,
                $"{docker} rm -f $({docker} ps -aq {labels}){Named(Unlabelled)}; {docker} network rm $({docker} network ls -q {labels}){Named(UnlabelledNetworks)}");
        }
        foreach (MadeOn made in Elsewhere.Values)
        {
            string other = string.Join(' ', [Quoted(made.Cli), .. made.Arguments.Select(Quoted)]);
            command.Append(command.Length > begun ? "; " : string.Empty).Append(CultureInfo.InvariantCulture, $"{other} rm -f{Named(made.Containers)}");
            if (made.Networks.Count > 0)
            {
                command.Append(CultureInfo.InvariantCulture, $"; {other} network rm{Named(made.Networks)}");
            }
        }
        string said = command.ToString();
        return command.Length == begun || said.Contains('\n', StringComparison.Ordinal) || said.Contains('\r', StringComparison.Ordinal)
            || said.Contains('\0', StringComparison.Ordinal) ? null : said;

        static string Named(IEnumerable<string> words) => string.Concat(words.Select(word => $" {Quoted(word)}"));
    }

    /// <summary>A word in single quotes for sh, a quote in it closed, escaped and opened again.</summary>
    private static string Quoted(string word) => $"'{word.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    /// <summary>A name the CLI takes for a context.</summary>
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.+-]*$")]
    private static partial Regex ContextName();

    /// <summary>This machine's own id, or nothing where none is read.</summary>
    private static string MachineId()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return ((string[])["/etc/machine-id", "/var/lib/dbus/machine-id"]).Where(File.Exists).Select(f => File.ReadAllText(f).Trim()).FirstOrDefault() ?? string.Empty;
            }
            if (OperatingSystem.IsMacOS())
            {
                Span<byte> uuid = stackalloc byte[16];
                TimeSpec wait = default;
                return HostUuid(uuid, ref wait) == 0 ? Convert.ToHexStringLower(uuid) : string.Empty;
            }
            if (OperatingSystem.IsWindows())
            {
                using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                return key?.GetValue("MachineGuid") as string ?? string.Empty;
            }
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or System.Security.SecurityException or DllNotFoundException
            or EntryPointNotFoundException)
        {
        }
        return string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public nint Seconds;
        public nint Nanoseconds;
    }

    /// <summary>macOS's <c>gethostuuid</c>: the machine's hardware UUID, as IOPlatformUUID gives it.</summary>
    [LibraryImport("libc", EntryPoint = "gethostuuid")]
    private static partial int HostUuid(Span<byte> uuid, ref TimeSpec wait);

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private static long StartOf(System.Diagnostics.Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
        }
        catch (Exception unknown) when (unknown is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>Whether the process a label names is gone: none has its id, or the one that has started at another time.</summary>
    public static bool IsGone(string label)
    {
        string[] parts = label.Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int id)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long started))
        {
            return false;
        }
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(id);
            long now = StartOf(process);
            return now != 0 && started != 0 && Math.Abs(now - started) > 2;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>The one rule for what a crash left: made for this state folder (by its value) on this machine by a process
    /// that is gone, never this one. A labelled container's labels and a recorded one's record are both judged by it.</summary>
    public static bool IsLeftover(string? state, string? machine, string? process, string stateValue) =>
        state == stateValue && machine == MachineValue && process is { } made && made != ProcessValue && IsGone(made);

    /// <summary>Whether Passenger ever labelled something for a state folder (its <c>containers.json</c>). Any thread.</summary>
    public static bool EverLabelledIn(string stateFolder) =>
        PathNames.Same(stateFolder, global::Passenger.App.State.StateStore.DefaultRoot) ? EverLabelled
            : new global::Passenger.App.State.StateDocument<ContainersState>(Path.Combine(stateFolder, "containers.json")).Peek().Labelled;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> Looked = new(StringComparer.Ordinal);

    /// <summary>What a crash left for a state folder removed once a process (<see cref="RemoveLeftoversAsync"/>): the
    /// process's start does its own state's, a window keeping its state elsewhere (a test's) its own as it first shows.
    /// Not held, and off the caller's way: an engine that answers slowly or not at all delays nothing. Any thread.</summary>
    public static Task LookForLeftovers(ContainerEngine engine, string stateFolder) =>
        Looked.GetOrAdd(StateValueOf(stateFolder), _ => BackgroundWork.Unheld(async () =>
        {
            try
            {
                await RemoveLeftoversAsync(engine, stateFolder);
            }
            catch (Exception failed)
            {
                IdeErrors.Write($"Containers: what an earlier Passenger left could not be looked for: {failed}");
            }
        }));

    /// <summary>Removes the containers and networks labelled with this state and this machine whose process is gone, and
    /// the containers its record says such a process had made without its labels (an AppHost's session containers): what a
    /// crash left, or a process the watchdog could not clean up after (Windows); another machine's on an engine they share
    /// are never judged by this machine's processes. No docker command runs for a state that never labelled or recorded
    /// anything. Off the UI thread; quiet when there is no engine.</summary>
    public static async Task<IReadOnlyList<string>> RemoveLeftoversAsync(ContainerEngine engine, string? stateFolder = null, CancellationToken cancel = default)
    {
        string folder = stateFolder ?? global::Passenger.App.State.StateStore.DefaultRoot;
        List<string> removed = [.. await Aspire.AspireContainerOwnership.RemoveLeftoversAsync(folder, engine, cancel)];
        // Not held: nothing it does is drawn; finding the CLI may wait on the login shell, never on the caller's thread.
        if (!EverLabelledIn(folder) || await BackgroundWork.Unheld(() => engine.Cli, cancel) is null)
        {
            return removed;
        }
        string stateValue = StateValueOf(folder);
        string filter = $"label={State}={stateValue}";
        string machine = $"label={Machine}={MachineValue}";
        try
        {
            IReadOnlyList<JsonElement> containers = await engine.JsonLinesAsync(["ps", "-a", "--filter", filter, "--filter", machine, "--format", "json"], cancel);
            string[] gone = [.. containers.Select(ContainerInfo.Read).Where(c => IsLeftover(c.Labels.GetValueOrDefault(State), c.Labels.GetValueOrDefault(Machine),
                c.Labels.GetValueOrDefault(Process), stateValue)).Select(c => c.Id)];
            if (gone.Length > 0)
            {
                await engine.RunAsync(["rm", "-f", .. gone], ContainerEngine.AskLimit * 3, cancel);
                removed.AddRange(gone);
            }
            IReadOnlyList<JsonElement> networks = await engine.JsonLinesAsync(["network", "ls", "--filter", filter, "--filter", machine, "--format", "json"], cancel);
            string[] left = [.. networks.Where(n => ContainerInfo.ReadLabels(n) is var labels && IsLeftover(labels.GetValueOrDefault(State),
                    labels.GetValueOrDefault(Machine), labels.GetValueOrDefault(Process), stateValue))
                .Select(ContainerInfo.IdOf).OfType<string>()];
            if (left.Length > 0)
            {
                await engine.RunAsync(["network", "rm", .. left], ContainerEngine.AskLimit, cancel);
                removed.AddRange(left);
            }
            if (removed.Count > 0)
            {
                IdeErrors.Write($"Containers: removed {Plural.Of(removed.Count, "container")} and networks an earlier Passenger left");
            }
        }
        catch (Exception failed) when (failed is ContainerCommandException or TimeoutException or JsonException or System.ComponentModel.Win32Exception)
        {
            // No engine running: what is left is removed at a start that finds one.
        }
        return removed;
    }
}
