using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The engine a test runs containers on: the person's docker, used only where its server runs Linux containers
/// (Docker Desktop, Docker Engine, Colima, OrbStack, Podman), each object a test makes carrying the run's label, which the run's
/// end removes (<see cref="RemoveEverything"/>), and which a later run removes once the run's process is gone, should it
/// end without its own end (<see cref="RemoveDeadRuns"/>). Tiny public images only: busybox.</summary>
internal static class Docker
{
    private const string RunKey = "net.passenger-ide.test-run";

    /// <summary>The run's label, its machine's and process's (its id and start): everything a test makes carries it, the
    /// run's end removes whatever carries it, and a later run what carries a run's of this machine whose process is gone.</summary>
    public static readonly string RunLabel = $"{RunKey}={ContainerLabels.MachineValue}.{ContainerLabels.ProcessValue}";

    public const string Image = "busybox:1.38";

    /// <summary>What every test's <c>docker run</c> takes: Docker's CLI on Podman's socket otherwise makes a container that
    /// holds one process (pids.max 1, Podman 5.7's reading of what the CLI sends; Compose's are Podman's 2048), where no
    /// exec, shell or .NET program could start a thread. Unlimited, as Docker's own default is.</summary>
    public static readonly string[] AnyPids = ["--pids-limit", "-1"];

    /// <summary>Set to <c>never</c> where the images the tests run on are pulled before the run (CI's test.yml): no test
    /// pulls one there, and a missing one skips every container test, saying which. Unset, a test pulls what it needs.</summary>
    [IdentifierHome]
    public const string PullVariable = "PASSENGER_TEST_PULL";

    /// <summary>Every image the container tests run on, Image's and .NET's, as ContainerImages.txt lists them for CI to pull.</summary>
    public static IReadOnlyList<string> Images { get; } =
        [.. File.ReadAllLines(Path.Combine(Debugging.Helpers.Debuggees.Root, "tests", "Passenger.Tests", "ContainerImages.txt")).Select(l => l.Trim()).Where(l => l.Length > 0)];

    /// <summary>Why no container can run here, asked once a run, on the thread pool, the first time a test asks.</summary>
    private static readonly Lazy<Task<string?>> Unusable = new(() => Waiting.Apart(UnusableAsync), LazyThreadSafetyMode.ExecutionAndPublication);

    private static async Task<string?> UnusableAsync()
    {
        if (ContainerEngine.Shared.Cli is null)
        {
            return "No docker command is installed here, so no container can run";
        }
        bool mayPull = Environment.GetEnvironmentVariable(PullVariable) switch
        {
            null or "" => true,
            "never" => false,
            string other => throw new InvalidOperationException($"{PullVariable} is \"{other}\": unset, or never where the run's images were pulled before it"),
        };
        TimeSpan limit = TimeSpan.FromSeconds(30).Scaled();
        try
        {
            ChildResult info = await AskAsync(["info", "--format", "{{.OSType}}"], limit);
            if (!info.Succeeded && await AskAsync(["info", "--format", "{{.Host.OS}}"], limit) is { Succeeded: true } podman)
            {
                // A docker that is Podman's own command words its system there.
                info = podman;
            }
            string os = info.Output.Trim();
            string? why = !info.Succeeded ? $"The container engine is not running here: {ContainerEngine.Said(info)}"
                : os != "linux" ? $"The container engine here runs {os} containers, and these tests run Linux ones" : null;
            if (why is null && !mayPull && await MissingAsync(limit) is { Length: > 0 } missing)
            {
                why = $"The engine lacks {string.Join(" and ", missing)}, which no test pulls here ({PullVariable}=never): pull them before the run";
            }
            if (why is null)
            {
                await RemoveDeadRunsAsync();
            }
            return why;
        }
        catch (TimeoutException)
        {
            return $"The container engine did not answer docker info within {limit.TotalSeconds:0} s";
        }
    }

    /// <summary>The images the run's tests take that the engine lacks.</summary>
    private static async Task<string[]> MissingAsync(TimeSpan limit)
    {
        List<string> missing = [];
        foreach (string image in Images)
        {
            if (!(await AskAsync(["image", "inspect", image], limit)).Succeeded)
            {
                missing.Add(image);
            }
        }
        return [.. missing];
    }

    /// <summary>What runs of this machine that ended without their own end (killed, crashed) left: everything with a run's
    /// label whose process is gone, and what such a run had its IDE make, labelled with that run's state and process.</summary>
    private static async Task RemoveDeadRunsAsync()
    {
        string machine = $"{ContainerLabels.MachineValue}.";
        TimeSpan limit = TimeSpan.FromSeconds(60).Scaled();
        try
        {
            foreach ((string kind, string[] list, string[] remove) in ((string, string[], string[])[])[
                ("container", ["ps", "-a"], ContainerLabels.Removing([])), ("network", ["network", "ls"], ["network", "rm"]), ("volume", ["volume", "ls"], ["volume", "rm", "-f"])])
            {
                string id = kind == "volume" ? "{{.Name}}" : "{{.ID}}";
                string[] dead = [.. Lines((await AskAsync([.. list, "--filter", $"label={RunKey}", "--format", $"{id}|{{{{.Label \"{RunKey}\"}}}}"], limit)).Output)
                    .Select(l => l.Split('|')).Where(l => l.Length == 2 && l[1].StartsWith(machine, StringComparison.Ordinal) && ContainerLabels.IsGone(l[1][machine.Length..]))
                    .Select(l => l[0])
                    .Concat(Lines((await AskAsync([.. list, "--filter", $"label={ContainerLabels.Machine}={ContainerLabels.MachineValue}", "--format",
                            $"{id}|{{{{.Label \"{ContainerLabels.State}\"}}}}|{{{{.Label \"{ContainerLabels.Process}\"}}}}"], limit)).Output)
                        .Select(l => l.Split('|')).Where(l => l.Length == 3 && IsDeadRuns(l[1], l[2])).Select(l => l[0]))
                    .Distinct(StringComparer.Ordinal)];
                if (dead.Length > 0)
                {
                    await AskAsync([.. remove, .. dead], limit);
                }
            }
            string[] images = Lines((await AskAsync(["image", "ls", "-a", "-q", "--filter", $"label={RunKey}"], limit)).Output);
            if (images.Length > 0)
            {
                string[] dead = [.. Lines((await AskAsync(["image", "inspect", "--format", $"{{{{.Id}}}}|{{{{index .Config.Labels \"{RunKey}\"}}}}", .. images.Distinct(StringComparer.Ordinal)], limit)).Output)
                    .Select(l => l.Split('|')).Where(l => l.Length == 2 && l[1].StartsWith(machine, StringComparison.Ordinal) && ContainerLabels.IsGone(l[1][machine.Length..]))
                    .Select(l => l[0])];
                if (dead.Length > 0)
                {
                    await AskAsync(["rmi", "-f", .. dead], limit);
                }
            }
        }
        catch (Exception failed) when (failed is TimeoutException or System.ComponentModel.Win32Exception or IOException)
        {
            Console.Error.WriteLine($"What earlier test runs left could not be removed: {failed.Message}");
        }

        static string[] Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // An IDE's in a test run of this machine whose process is gone: the state it labels with is that run's own.
        static bool IsDeadRuns(string state, string process) =>
            process.Split('-')[0] is var id && ContainerLabels.IsGone(process)
            && state == ContainerLabels.StateValueOf(Path.Combine(Path.GetTempPath(), TestRun.Prefix + id + TestRun.Unindexed, "settings"));
    }

    /// <summary>Docker run to its end from the UI thread (a window test's), on the thread pool, the window's loop running
    /// meanwhile and never waited for; a test with no window, or a fixture, awaits each member's async form.</summary>
    private static ChildResult Ask(string[] words, TimeSpan limit) =>
        Ui.AwaitPumped(Waiting.Apart(() => AskAsync(words, limit)), $"docker {string.Join(' ', words)}", limit + TestFolders.Spare);

    private static Task<ChildResult> AskAsync(string[] words, TimeSpan limit) => ContainerEngine.Shared.RunAsync(words, limit);

    /// <summary>What docker says to a command, its output or else its errors, whether it succeeded or not.</summary>
    public static string Ask(string[] arguments) => Said(Ask(arguments, TimeSpan.FromMinutes(1).Scaled()));

    /// <inheritdoc cref="Ask(string[])"/>
    public static async Task<string> AskAsync(string[] arguments) => Said(await AskAsync(arguments, TimeSpan.FromMinutes(1).Scaled()));

    private static string Said(ChildResult result) => result.Output.Length > 0 ? result.Output : result.Error;

    /// <summary>What a test's docker build takes so its image lands in the engine: Docker's buildx on a builder of its own
    /// (on Podman's socket, a BuildKit container it starts) keeps what it built in that builder's cache unless told to load it.
    /// Podman's own build has no --load, and lands its images.</summary>
    public static async Task<string[]> LoadAsync() =>
        await ContainerEngine.Shared.StateAsync() is { PodmanCli: false, BuildxVersion: not null } ? ["--load"] : [];

    /// <summary>An image's name as Docker words it: Podman names an image built or loaded here in full (docker.io/library/…,
    /// localhost/…).</summary>
    public static string Short(string image) =>
        image.StartsWith("docker.io/library/", StringComparison.Ordinal) ? image["docker.io/library/".Length..]
        : image.StartsWith("localhost/", StringComparison.Ordinal) ? image["localhost/".Length..] : image;

    /// <summary>A container's command as Docker's inspect words it: Podman's puts its own init (Compose's init: true) first.</summary>
    public static string WithoutInit(string command) =>
        command.StartsWith("/run/podman-init -- ", StringComparison.Ordinal) ? command["/run/podman-init -- ".Length..] : command;

    /// <summary>Why no container can run here (no CLI, no engine, Windows containers), or null when one can: asked from the
    /// UI thread, the window's loop running meanwhile.</summary>
    public static string? WhyNot => Ui.AwaitPumped(Unusable.Value, "the container engine to say what it runs", TimeSpan.FromMinutes(5));

    /// <inheritdoc cref="WhyNot"/>
    public static Task<string?> WhyNotAsync() => Unusable.Value;

    /// <summary>Skips a test with no window where no container can run, saying why.</summary>
    public static async Task SkipWithoutEngineAsync()
    {
        string? why = await WhyNotAsync();
        Assert.SkipWhen(why is not null, why ?? string.Empty);
    }

    /// <summary>Runs docker to its end, failing the test with what it said when it fails.</summary>
    public static string Run(params string[] arguments) => Ran(arguments, Ask(arguments, TimeSpan.FromMinutes(3).Scaled()));

    /// <inheritdoc cref="Run"/>
    public static async Task<string> RunAsync(params string[] arguments) => Ran(arguments, await AskAsync(arguments, TimeSpan.FromMinutes(3).Scaled()));

    private static string Ran(string[] arguments, ChildResult result)
    {
        Assert.True(result.Succeeded, $"docker {string.Join(' ', arguments)} failed: {ContainerEngine.Said(result)}");
        return result.Output.Trim();
    }

    /// <summary>What docker answers, trimmed, or null while it cannot say (it failed, or took a minute): for a wait's
    /// condition, which looks again rather than failing the test.</summary>
    public static string? Said(params string[] arguments)
    {
        try
        {
            return Answer(Ask(arguments, TimeSpan.FromMinutes(1).Scaled()));
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <inheritdoc cref="Said"/>
    public static async Task<string?> SaidAsync(params string[] arguments)
    {
        try
        {
            return Answer(await AskAsync(arguments, TimeSpan.FromMinutes(1).Scaled()));
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static string? Answer(ChildResult result) => result.Succeeded ? result.Output.Trim() : null;

    /// <summary>Whether a Compose project has no container left (of those carrying each label given too), as a wait looks:
    /// false while docker cannot say.</summary>
    public static bool NoneLeft(string project, params string[] labels) => Said(Left(project, labels)) is "";

    /// <inheritdoc cref="NoneLeft"/>
    public static async Task<bool> NoneLeftAsync(string project, params string[] labels) => await SaidAsync(Left(project, labels)) is "";

    private static string[] Left(string project, string[] labels) =>
        ["ps", "-aq", "--filter", $"label=com.docker.compose.project={project}", .. labels.SelectMany(l => (string[])["--filter", $"label={l}"])];

    /// <summary>Waits, the window running, until a Compose project has no container left (of those carrying each label given
    /// too), failing with what was to take them down.</summary>
    public static void TakenDown(string project, string what, params string[] labels) =>
        Ui.WaitFor(() => NoneLeft(project, labels), what, TimeSpan.FromMinutes(2));

    /// <summary>The window's Stop of its container run, and the wait until its project has no container left (as
    /// <see cref="TakenDown"/>).</summary>
    public static void Stopped(MainWindow window, string project, string what, params string[] labels)
    {
        window.Perform(AppActions.Stop);
        TakenDown(project, what, labels);
    }

    /// <summary>A folder of the test's own holding a Compose file whose services and network carry the run's label, brought
    /// up and waited for until healthy; its project's name is the test run's and the folder's.</summary>
    public static (string Folder, string Project) ComposeUp(string name, string services)
    {
        string folder = TestFolders.Create($"ctr-{name}-");
        string project = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}{name}";
        string label = RunLabel.Replace("=", ": \"", StringComparison.Ordinal) + "\"";
        File.WriteAllText(Path.Combine(folder, "compose.yaml"), $"name: {project}\nservices:\n{services.Replace("$LABEL", label, StringComparison.Ordinal)}\nnetworks:\n  default:\n    labels: {{ {label} }}\n");
        Run("compose", "-f", Path.Combine(folder, "compose.yaml"), "up", "-d", "--wait");
        return (PathNames.Canonical(folder), project);
    }

    /// <summary>Takes down what <see cref="ComposeUp"/> brought up in a folder, so no later test of the run lists it: its
    /// containers removed with their anonymous volumes (<see cref="Remove"/>), which Compose's down leaves, then its network.</summary>
    public static void ComposeDown(string folder, string project)
    {
        if (Run("ps", "-aq", "--filter", $"label={ComposeLabels.Project}={project}").Split('\n', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } made)
        {
            Remove(made);
        }
        Run("compose", "-f", Path.Combine(folder, "compose.yaml"), "down", "--timeout", "0");
    }

    /// <summary>A container of busybox running <paramref name="command"/>, named and labelled as the test's, with more labels.</summary>
    public static string Container(string name, string command, params string[] labels)
    {
        (string named, string[] words) = Busybox(name, command, labels);
        Run(words);
        return named;
    }

    /// <inheritdoc cref="Container"/>
    public static async Task<string> ContainerAsync(string name, string command, params string[] labels)
    {
        (string named, string[] words) = Busybox(name, command, labels);
        await RunAsync(words);
        return named;
    }

    /// <summary>A container as <see cref="Container"/> makes one, holding an anonymous volume at a path.</summary>
    public static string ContainerHolding(string name, string command, string volume, params string[] labels)
    {
        (string named, string[] words) = Busybox(name, command, labels, ["-v", volume]);
        Run(words);
        return named;
    }

    private static (string Named, string[] Words) Busybox(string name, string command, string[] labels, string[]? more = null)
    {
        string named = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}{name}";
        List<string> words = ["run", "-d", .. AnyPids, "--name", named, "--label", RunLabel];
        foreach (string label in labels)
        {
            words.AddRange(["--label", label]);
        }
        return (named, [.. words, .. more ?? [], Image, "sh", "-c", command]);
    }

    /// <summary>Removes containers a test started, with the anonymous volumes they hold, as Passenger removes its own
    /// (<see cref="ContainerLabels.Removing"/>), as its end does in its finally, whatever came of the test: what docker says
    /// is passed over, the test's own failure being the one to tell, and one left running fails it all the same
    /// (<see cref="LeftBy"/>). From the UI thread, the window's loop running as it waits.</summary>
    public static void Remove(params string[] containers) => Ask(ContainerLabels.Removing(containers));

    /// <inheritdoc cref="Remove"/>
    public static Task RemoveAsync(params string[] containers) => AskAsync(ContainerLabels.Removing(containers));

    /// <summary>The anonymous volumes a container holds (its image's VOLUME, a <c>-v /path</c>), by name.</summary>
    public static string[] AnonymousVolumes(string container) =>
        Run("inspect", "-f", "{{range .Mounts}}{{if eq .Type \"volume\"}}{{.Name}} {{end}}{{end}}", container).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Those of the volumes named the engine still holds.</summary>
    public static string[] Held(IReadOnlyCollection<string> volumes) => volumes.Count == 0 ? []
        : [.. Ask(["volume", "ls", "-q"], TimeSpan.FromSeconds(30).Scaled()).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Intersect(volumes, StringComparer.Ordinal)];

    /// <summary>A container's state as docker inspect says it (running, exited, paused), or null when there is none.</summary>
    public static string? State(string name) => Answer(Ask(["inspect", "-f", "{{.State.Status}}", name], TimeSpan.FromSeconds(30).Scaled()));

    /// <inheritdoc cref="State"/>
    public static async Task<string?> StateAsync(string name) => Answer(await AskAsync(["inspect", "-f", "{{.State.Status}}", name], TimeSpan.FromSeconds(30).Scaled()));

    /// <summary>Whether a volume of that name is there.</summary>
    public static bool VolumeExists(string name) => Ask(["volume", "inspect", name], TimeSpan.FromSeconds(30).Scaled()).Succeeded;

    /// <summary>The engine commands that start a container (run, create, start, restart, unpause, Compose's up), which a test
    /// or the IDE as it ran started, counted (<see cref="Begin"/>, <see cref="LeftBy"/>).</summary>
    private static long starts;

    private static int counting;

    /// <summary>How many containers had been started as each test under way began, by its id.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> StartsAtBegin = new(StringComparer.Ordinal);

    /// <summary>Notes how many containers had been started as a test begins, for <see cref="LeftBy"/> at its end; the first
    /// begins the count.</summary>
    public static void Begin(Xunit.v3.IXunitTest test)
    {
        if (Interlocked.Exchange(ref counting, 1) == 0)
        {
            ChildProcesses.Started += (_, start) =>
            {
                if (start.Start is { } started && Path.GetFileNameWithoutExtension(started.FileName) is "docker" or "podman"
                    && started.ArgumentList.Any(word => word is "run" or "create" or "start" or "restart" or "unpause" or "up"))
                {
                    Interlocked.Increment(ref starts);
                }
            };
        }
        StartsAtBegin[test.UniqueID] = Interlocked.Read(ref starts);
    }

    /// <summary>The run's containers a test left running (those carrying its label, and those its IDE labelled with the run's
    /// state), removed now, each named with its image: looked for only where the test or the IDE started a container as it
    /// ran, and where an engine runs. A test of a class that runs beside others (<see cref="Beside"/>, the debug adapter's)
    /// starts none, and would find theirs.</summary>
    public static IReadOnlyList<string> LeftBy(Xunit.v3.IXunitTest test)
    {
        if (!StartsAtBegin.TryRemove(test.UniqueID, out long before) || Interlocked.Read(ref starts) == before
            || test.TestCase.TestCollection.TestCollectionDisplayName is Beside.Name or Debugging.Helpers.DebugSession.Collection)
        {
            return [];
        }
        Task<IReadOnlyList<string>> removing = Waiting.Apart(RemoveRunningAsync);
        TimeSpan limit = TimeSpan.FromMinutes(2).Scaled();
        // A thread no window ran on (a class run alone, its tests windowless) has no window loop to keep running as it waits.
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess() && Avalonia.Application.Current is not null)
        {
            return Ui.AwaitPumped(removing, "the run's containers left running, removed", limit);
        }
        Waiting.Until(() => removing.IsCompleted, limit, scaled: false);
        return removing.IsCompleted ? removing.GetAwaiter().GetResult()
            : throw new TimeoutException($"The run's containers left running were not removed within {limit.TotalSeconds:0} s");
    }

    /// <summary>Removes the run's containers running now (<see cref="LeftBy"/>), each named with its image; none where no
    /// engine runs.</summary>
    private static async Task<IReadOnlyList<string>> RemoveRunningAsync()
    {
        if (await Unusable.Value is not null)
        {
            return [];
        }
        Dictionary<string, string> running = new(StringComparer.Ordinal);
        foreach (string label in (string[])[RunLabel, $"{ContainerLabels.State}={ContainerLabels.StateValue}"])
        {
            ChildResult listed = await AskAsync(["ps", "--filter", $"label={label}", "--format", "{{.ID}}\t{{.Names}}\t{{.Image}}"], TimeSpan.FromSeconds(30).Scaled());
            foreach (string[] row in listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split('\t')).Where(r => r.Length == 3))
            {
                running[row[0]] = $"{row[1]} ({Short(row[2])})";
            }
        }
        if (running.Count > 0)
        {
            await AskAsync(ContainerLabels.Removing(running.Keys), TimeSpan.FromSeconds(60).Scaled());
        }
        return [.. running.Values.Order(StringComparer.Ordinal)];
    }

    /// <summary>Removes every container, network, volume and image the run's tests made or had the IDE make (the run's state's label), as
    /// the run ends; quiet where there is no engine.</summary>
    public static async Task RemoveEverythingAsync()
    {
        // A run none of whose tests asked for an engine made nothing on one.
        if (!Unusable.IsValueCreated || await Unusable.Value is not null)
        {
            return;
        }
        try
        {
            foreach (string label in (string[])[RunLabel, $"{ContainerLabels.State}={ContainerLabels.StateValue}"])
            {
                ChildResult containers = await AskAsync(["ps", "-aq", "--filter", $"label={label}"], TimeSpan.FromSeconds(30).Scaled());
                string[] ids = containers.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (ids.Length > 0)
                {
                    await AskAsync(ContainerLabels.Removing(ids), TimeSpan.FromSeconds(60).Scaled());
                }
                ChildResult networks = await AskAsync(["network", "ls", "-q", "--filter", $"label={label}"], TimeSpan.FromSeconds(30).Scaled());
                string[] made = networks.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (made.Length > 0)
                {
                    await AskAsync(["network", "rm", .. made], TimeSpan.FromSeconds(60).Scaled());
                }
                ChildResult volumes = await AskAsync(["volume", "ls", "-q", "--filter", $"label={label}"], TimeSpan.FromSeconds(30).Scaled());
                string[] kept = volumes.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (kept.Length > 0)
                {
                    await AskAsync(["volume", "rm", "-f", .. kept], TimeSpan.FromSeconds(60).Scaled());
                }
                // Untagged ones too: one removed while a container ran is only untagged and stays.
                ChildResult images = await AskAsync(["image", "ls", "-a", "-q", "--filter", $"label={label}"], TimeSpan.FromSeconds(30).Scaled());
                string[] built = [.. images.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];
                if (built.Length > 0)
                {
                    await AskAsync(["rmi", "-f", .. built], TimeSpan.FromSeconds(60).Scaled());
                }
            }
        }
        catch (Exception failed) when (failed is TimeoutException or System.ComponentModel.Win32Exception or IOException)
        {
            Console.Error.WriteLine($"The tests' containers could not be removed: {failed.Message}");
        }
    }
}

/// <summary>The Containers window over a real engine, as a person uses it: a Compose project the test brought up listed with
/// its services, ports and health, a container's log streaming with its links, its stats, its files opened read-only, a
/// shell in the terminal, and Start, Stop, Restart and Remove acting; and with no CLI or no engine, the status bar and
/// Problems saying so. Each skips, saying why, where no Linux engine runs.</summary>
public sealed class ContainersTests
{
    /// <summary>A web server answering on a port the engine chooses, healthy once it answers, and a worker that ticks.</summary>
    private const string Shop = """
          web:
            image: busybox:1.38
            command: ["sh", "-c", "mkdir -p /www && echo hello > /www/index.html && echo 'web listening on http://localhost:8080/ at /app/Program.cs:line 12' && exec httpd -f -v -p 8080 -h /www"]
            ports: ["8080"]
            labels: { $LABEL }
            healthcheck: { test: ["CMD", "wget", "-q", "-O", "-", "http://localhost:8080/"], interval: 1s, retries: 30 }
          worker:
            image: busybox:1.38
            command: ["sh", "-c", "i=0; while true; do i=$$((i+1)); echo \"worker tick $$i\"; sleep 1; done"]
            labels: { $LABEL }
        """;

    /// <summary>The Containers window with no engine of its own as one flow, each step one test's check in an instance
    /// of its own: Prune offered until the engine is known not to serve, then saying why, the status bar and Problems
    /// saying there is no docker or its engine is not running, a choice to leave containers running kept on disk beside
    /// another Passenger's, and Find Action reaching each tab and its logs' Find.</summary>
    [AvaloniaFact]
    public void The_Containers_window_says_where_the_engine_stands_and_keeps_the_persons_choices()
    {
        Flow.StepApart<ContainersTests>("prune is offered until the engine is known not to serve and then says why", t => t.Prune_is_offered_until_the_engine_is_known_not_to_serve_and_then_says_why());
        Flow.StepApart<ContainersTests>("the status bar and problems say when there is no docker or its engine is not running", t => t.The_status_bar_and_problems_say_when_there_is_no_docker_or_its_engine_is_not_running());
        Flow.StepApart<ContainersTests>("a choice to leave containers running is on disk once made and another Passengers stays beside it", t => t.A_choice_to_leave_containers_running_is_on_disk_once_made_and_another_Passengers_stays_beside_it());
        Flow.StepApart<ContainersTests>("find Action reaches each of the Containers windows tabs and its logs Find", t => t.Find_Action_reaches_each_of_the_Containers_windows_tabs_and_its_logs_Find());
    }

    /// <summary>The Containers window over a stand-in engine as one flow, every step off Windows alone (the stand-in is
    /// a shell script), each in an instance of its own: Prune asking before the window is quiet, stats coming through
    /// the CLI's errors and saying why they ended, hiding the window ending a service's metrics, metrics looking again
    /// until the program is found, the window following an engine that stops and starts, a restarting or failed
    /// container shown so, and a log whose command leaves a job on its output ending it.</summary>
    [AvaloniaFact]
    public void The_Containers_window_follows_its_engine_and_ends_what_it_reads_once_hidden_or_done()
    {
        Flow.StepApart<ContainersTests>("prune asks its question before the window is quiet however late the engine answers", t => t.Prune_asks_its_question_before_the_window_is_quiet_however_late_the_engine_answers());
        Flow.StepApart<ContainersTests>("stats come though the cli says much on its errors and say why the stream ended", t => t.Stats_come_though_the_cli_says_much_on_its_errors_and_say_why_the_stream_ended());
        Flow.StepApart<ContainersTests>("hiding the Containers window ends a services metrics and a lookup under way starts none", t => t.Hiding_the_Containers_window_ends_a_services_metrics_and_a_lookup_under_way_starts_none());
        Flow.StepApart<ContainersTests>("a services metrics say why its program is not reached and look again until found or its run ends", t => t.A_services_metrics_say_why_its_program_is_not_reached_and_look_again_until_found_or_its_run_ends());
        Flow.StepApart<ContainersTests>("the window follows an engine that starts stops and starts again and lists compose projects by their labels with no compose", t => t.The_window_follows_an_engine_that_starts_stops_and_starts_again_and_lists_compose_projects_by_their_labels_with_no_compose());
        Flow.StepApart<ContainersTests>("a container that keeps restarting or exited with an error shows failed and one stopped shows stopped", t => t.A_container_that_keeps_restarting_or_exited_with_an_error_shows_failed_and_one_stopped_shows_stopped());
        Flow.StepApart<ContainersTests>("a log whose command ends leaving a job on its output says it finished and ends the job", t => t.A_log_whose_command_ends_leaving_a_job_on_its_output_says_it_finished_and_ends_the_job());
    }

    /// <summary>Compose runs over a stand-in engine as one flow, every step off Windows alone (the stand-in is a shell
    /// script), each in an instance of its own: a remote engine's port and browser opening on its own machine, a
    /// Compose project's own name kept from reading its files to its take-down, its browser opening once its service
    /// answers HTTP, Detach and Leave Running letting a run go with the person's say, a quit over a run let go of
    /// asking, and a Passenger nobody sees asking nothing and stopping them.</summary>
    [AvaloniaFact]
    public void A_compose_run_let_go_of_or_on_another_machine_is_followed_asked_about_and_stopped_as_the_person_chooses()
    {
        Flow.StepApart<ContainersTests>("a remote engines port opens on its own machine", t => t.A_remote_engines_port_opens_on_its_own_machine());
        Flow.StepApart<ContainersTests>("a compose run on an engine of another machine opens the browser on that machine", t => t.A_compose_run_on_an_engine_of_another_machine_opens_the_browser_on_that_machine());
        Flow.StepApart<ContainersTests>("a Docker Compose projects own project name is its runs from reading its files to its take down", t => t.A_Docker_Compose_projects_own_project_name_is_its_runs_from_reading_its_files_to_its_take_down());
        Flow.StepApart<ContainersTests>("a runs Compose part comes up before the part after it starts in the Run windows console and Stop takes it down", t => t.A_runs_Compose_part_comes_up_before_the_part_after_it_starts_in_the_Run_windows_console_and_Stop_takes_it_down());
        Flow.StepApart<ContainersTests>("a Docker Compose projects browser opens once its service answers HTTP the status saying meanwhile that it waits", t => t.A_Docker_Compose_projects_browser_opens_once_its_service_answers_HTTP_the_status_saying_meanwhile_that_it_waits());
        Flow.StepApart<ContainersTests>("detach lets go of a Docker Compose projects run and the windows close asks and stops its containers", t => t.Detach_lets_go_of_a_Docker_Compose_projects_run_and_the_windows_close_asks_and_stops_its_containers());
        Flow.StepApart<ContainersTests>("leave Running keeps a runs containers let go of as the solution closes and records them as the persons", t => t.Leave_Running_keeps_a_runs_containers_let_go_of_as_the_solution_closes_and_records_them_as_the_persons());
        Flow.StepApart<ContainersTests>("a quit over a run let go of asks Cancel gives it up and Stop carries it on taking its containers down", t => t.A_quit_over_a_run_let_go_of_asks_Cancel_gives_it_up_and_Stop_carries_it_on_taking_its_containers_down());
        Flow.StepApart<ContainersTests>("a Passenger nobody sees asks nothing and its close stops a runs containers let go of", t => t.A_Passenger_nobody_sees_asks_nothing_and_its_close_stops_a_runs_containers_let_go_of());
    }

    /// <summary>The Containers window over a real engine as one flow, every step needing a Linux container engine, each
    /// in an instance of its own: a compose project listed with its logs, a container's stats, files and actions, a
    /// folder's end leaving the person's own, a volume's removal asked first, a broken snapshot refused, attaching with
    /// the container's own tools, Open Shell, the clean-up passing over what the person left, and a stopped log
    /// kept.</summary>
    [AvaloniaFact]
    public void Containers_are_listed_read_and_acted_on_leaving_the_persons_own_and_asking_before_data_goes()
    {
        Flow.StepApart<ContainersTests>("a compose project is listed with its services ports and health and its logs stream with links and merge its services", t => t.A_compose_project_is_listed_with_its_services_ports_and_health_and_its_logs_stream_with_links_and_merge_its_services());
        Flow.StepApart<ContainersTests>("a containers stats show its numbers its files open read only and stop start restart and remove act on it", t => t.A_containers_stats_show_its_numbers_its_files_open_read_only_and_stop_start_restart_and_remove_act_on_it());
        Flow.StepApart<ContainersTests>("a folders end removes what passenger started of a compose project and leaves the persons own of it", t => t.A_folders_end_removes_what_passenger_started_of_a_compose_project_and_leaves_the_persons_own_of_it());
        Flow.StepApart<ContainersTests>("removing a volume asks first since its data goes with it", t => t.Removing_a_volume_asks_first_since_its_data_goes_with_it());
        Flow.StepApart<ContainersTests>("a memory snapshot a containers tools send that does not hold together is refused saying so", t => t.A_memory_snapshot_a_containers_tools_send_that_does_not_hold_together_is_refused_saying_so());
        Flow.StepApart<ContainersTests>("attaching in a container never uses tools of another version nor a list of programs that is none", t => t.Attaching_in_a_container_never_uses_tools_of_another_version_nor_a_list_of_programs_that_is_none());
        Flow.StepApart<ContainersTests>("open shell runs a command in the container in a terminal tab", t => t.Open_shell_runs_a_command_in_the_container_in_a_terminal_tab());
        Flow.StepApart<ContainersTests>("the next starts clean up passes over what the person chose to leave running just before Passenger ended", t => t.The_next_starts_clean_up_passes_over_what_the_person_chose_to_leave_running_just_before_Passenger_ended());
        Flow.StepApart<ContainersTests>("a stopped containers log stays as it was read while the window reads the engine again", t => t.A_stopped_containers_log_stays_as_it_was_read_while_the_window_reads_the_engine_again());
    }

    /// <summary>A real engine's containers off Windows as one flow, every step needing a Linux container engine and a
    /// path of this machine's, each in an instance of its own: a container's files tab never writing outside
    /// Passenger's cache nor copying a huge file, its copies the person's alone, and attaching in an emulated container
    /// saying it is emulated.</summary>
    [AvaloniaFact]
    public void A_containers_files_stay_in_Passengers_cache_and_an_emulated_one_says_so()
    {
        Flow.StepApart<ContainersTests>("a container can never have its files tab write outside passengers cache nor copy a huge file and its copies are the persons alone", t => t.A_container_can_never_have_its_files_tab_write_outside_passengers_cache_nor_copy_a_huge_file_and_its_copies_are_the_persons_alone());
        Flow.StepApart<ContainersTests>("attaching in an emulated container says it is emulated", t => t.Attaching_in_an_emulated_container_says_it_is_emulated());
    }

    /// <summary>Prune is offered while nothing has asked the engine how it stands, never refused as not running then, and
    /// refused once the engine is known not to serve, in its own words (here: no docker command where it was named).</summary>
    private void Prune_is_offered_until_the_engine_is_known_not_to_serve_and_then_says_why()
    {
        string folder = TestFolders.Create("prune-");
        TestFolders.Write(folder, "README.md", "# Shop\n");
        ContainerEngine engine = new(cli: Path.Combine(folder, "no-docker"), search: false);
        MainWindow window = ContainerRunsTests.Open(folder, engine: engine);
        try
        {
            Assert.Null(engine.Known);
            Assert.Null(window.Actions.WhyNot(AppActions.ContainerPrune, window.Actions.Context()));
            EngineState known = Ui.Await(engine.StateAsync(), "the engine's state, its command missing");
            Assert.False(known.IsReady, $"An engine whose command is missing serves: {known}");
            string? why = window.Actions.WhyNot(AppActions.ContainerPrune, window.Actions.Context());
            Assert.True(why is not null && why == known.Problem,
                $"Prune, the engine known not to serve ({known.Problem}), was refused with \"{why}\" rather than the engine's own words");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Prune asks its question once the engine says it serves, however late, before the window is quiet: what waits
    /// for the window (a screenshot's next step, a test's next key) finds the question asked, never left to come later and
    /// take the keys from whatever comes next. Cancelled, it prunes nothing.</summary>
    private void Prune_asks_its_question_before_the_window_is_quiet_however_late_the_engine_answers()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        string folder = TestFolders.Create("prune-late-");
        TestFolders.Write(folder, "README.md", "# Shop\n");
        string engineFolder = TestFolders.Create("prune-late-engine-");
        (string cli, _) = StandInEngine(engineFolder, up: true, compose: true, late: true);
        MainWindow window = ContainerRunsTests.Open(folder, engine: new ContainerEngine(cli: cli));
        try
        {
            Assert.True(window.Perform(AppActions.ContainerPrune), $"Prune did not run: {window.Status}");
            Ui.Settle();
            ConfirmDialog asked = window.Asked is { IsVisible: true, Title: "Prune" } shown ? shown : throw new Xunit.Sdk.XunitException(
                $"With the window quiet, Prune had not yet asked its question (the status bar says '{window.Status}'), which would come later and take the keys from what was done next");
            Ui.ClickButton(asked, "Cancel");
            Ui.Settle();
            string[] calls = File.ReadAllLines(Path.Combine(engineFolder, "calls"));
            Assert.True(!asked.IsVisible && !calls.Contains("container"),
                $"Cancelled, Prune's question {(asked.IsVisible ? "stayed open" : "closed")} and the engine was asked [{string.Join(", ", calls)}]");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A Compose project the test brought up: listed with its services, ports and health, the port a link the browser
    /// opens; a container's log streaming into its console with links, the address its program names as its own linked where
    /// its container publishes it; and the project's log merging its services, each a toggle, which Find searches by a
    /// regular expression.</summary>
    private void A_compose_project_is_listed_with_its_services_ports_and_health_and_its_logs_stream_with_links_and_merge_its_services()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        (string folder, string project) = Docker.ComposeUp("list", Shop);
        try
        {
            MainWindow window = Ui.OpenFolder(folder);
            ContainersView view = Shown(window);
            Ui.WaitFor(() => view.Rows.Any(r => r.Kind == ContainerRowKind.Project && r.Label == project), $"{project} listed under Compose");

            ContainerRow web = Service(view, project, "web");
            Assert.Equal("healthy", web.Container!.Health);
            Assert.True(web.Container.IsRunning, $"web is {web.Container.State}");
            ContainerPort port = Assert.Single(web.Container.Ports);
            Assert.Equal("8080", port.Inside);
            Assert.Equal(Docker.Image, Docker.Short(Service(view, project, "worker").Container!.Image));
            // Scrolled into view: an engine holding many projects lists web below the rows the list makes.
            Button? link = null;
            Ui.WaitFor(() => view.Choose(r => r.Key == web.Key)
                && (link = view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "containerPort" && Equals(b.Content, port.ToString()))) is not null,
                "web's port link");
            Ui.Click(link!);
            Assert.Contains(new Uri($"http://localhost:{port.HostPort}/"), window.Browsed);

            OutputView console = view.Log.Console;
            Ui.WaitFor(() => Text(console).Contains("web listening on http://localhost:8080/", StringComparison.Ordinal), "web's first line in its log");
            string line = Text(console).Split('\n').First(l => l.Contains("web listening", StringComparison.Ordinal));
            // The address web names as its own is reached here where its container publishes its 8080.
            Uri reached = new($"http://localhost:{port.HostPort}/");
            Assert.True(Ui.LinksIn(console.Links, line).Any(l => l.Target is WebLink { Address: var at } && at == reached),
                $"web's own address in its log does not link where its container publishes its 8080 ({reached}): {string.Join(", ", Ui.LinksIn(console.Links, line).Select(l => l.Target))}");

            Assert.True(view.Choose(r => r.Kind == ContainerRowKind.Project && r.Label == project), "The project's row was not listed");
            Ui.WaitFor(() => Text(console).Contains("worker tick", StringComparison.Ordinal) && Text(console).Contains("web listening", StringComparison.Ordinal),
                "both services' lines in the project's log");
            Assert.Equal(["web", "worker"], view.Log.ServiceToggles.Select(t => (string)t.Content!));
            int ticks = Ticks(console);
            Ui.WaitFor(() => Ticks(console) > ticks, "the worker's next tick, streamed");

            view.Log.OpenFind();
            Ui.Settle();
            view.Log.Find.FindControl<Avalonia.Controls.Primitives.ToggleButton>("UseRegex")!.IsChecked = true;
            view.Log.Find.FindControl<TextBox>("Query")!.Text = @"worker tick \d+$";
            Ui.Settle();
            Assert.Matches(@"^\d+/([2-9]|\d\d+)$", view.Log.Find.CountText);
            // Find Next's keys in the log step its own matches, never the editor's Find.
            view.Log.Find.FindControl<TextBox>("Query")!.Focus();
            Ui.Settle();
            Passenger.Text.Selection matched = console.Editor.Selections.Primary;
            Ui.Press(window, Ui.Shortcut(AppActions.FindNext));
            Assert.True(console.Editor.Selections.Primary != matched, "Find Next's keys in the Containers log left its match where it was");
            Assert.False(window.EditorArea.FindBar.IsVisible, "Find Next's keys in the Containers log went to the editor's Find");
            window.Close();
        }
        finally
        {
            Docker.ComposeDown(folder, project);
        }

        static int Ticks(OutputView console) => Text(console).Split('\n').Count(l => l.Contains("worker tick", StringComparison.Ordinal));
    }

    /// <summary>One busy container chosen in the window: its Stats show the engine's numbers while that tab shows, and nothing
    /// streams once it is hidden; its Files tab opens a file of its read-only; and Stop, Start, Restart and Remove act on it,
    /// asking nothing.</summary>
    private void A_containers_stats_show_its_numbers_its_files_open_read_only_and_stop_start_restart_and_remove_act_on_it()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string name = Docker.Container("solo", "trap 'exit 0' TERM; echo 'from inside' > /tmp/note.txt; while true; do :; done");
        try
        {
            MainWindow window = Ui.OpenFolder(TestFolders.Create("ctr-solo-"));
            ContainersView view = Shown(window);
            Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Container && r.Label == name), $"{name}'s row");

            view.ShowTab("Stats");
            Ui.WaitFor(() => view.Stats.Latest.Count == 1 && view.Stats.Latest[0].Memory.Length > 0 && view.Stats.Latest[0].Cpu > 0, "the engine's numbers for the busy container");
            ContainerStatsSample sample = view.Stats.Latest[0];
            Assert.Equal(name, sample.Name);
            Assert.Contains("/", sample.Memory, StringComparison.Ordinal);
            view.ShowTab("Log");
            Ui.Settle();
            Assert.Empty(view.Stats.Latest);

            view.ShowTab("Files");
            Ui.WaitFor(() => view.Files.Entries.Any(f => f.Path == "/tmp" && f.IsFolder), "the container's root listed");
            _ = view.Files.ActivateAsync(view.Files.Entries.First(f => f.Path == "/tmp"));
            Ui.WaitFor(() => view.Files.Entries.Any(f => f.Path == "/tmp/note.txt"), "/tmp's files listed under it");
            _ = view.Files.ActivateAsync(view.Files.Entries.First(f => f.Path == "/tmp/note.txt"));
            Ui.WaitFor(() => window.EditorArea.Active?.Path.EndsWith("note.txt", StringComparison.Ordinal) == true, "note.txt open in the editor");
            Passenger.App.Workspace.OpenDocument note = window.EditorArea.Active!;
            Assert.True(note.IsReadOnly, "A container's file opened editable");
            Assert.Equal("from inside\n", note.Text.Snapshot.ToString());

            Assert.True(window.Perform(AppActions.ContainerStop), "Stop did not run");
            Ui.WaitFor(() => Docker.State(name) == "exited" && Row(view, name)?.Container?.State == "exited", $"{name} stopped, and shown so");
            Assert.True(window.Perform(AppActions.ContainerStart), "Start did not run");
            Ui.WaitFor(() => Docker.State(name) == "running" && Row(view, name)?.Container?.IsRunning == true, $"{name} started again");
            string started = Docker.Run("inspect", "-f", "{{.State.StartedAt}}", name);
            Assert.True(window.Perform(AppActions.ContainerRestart), "Restart did not run");
            Ui.WaitFor(() => Docker.Said("inspect", "-f", "{{.State.StartedAt}}", name) is { } now && now != started, $"{name} restarted");
            Assert.True(window.Perform(AppActions.ContainerRemove), "Remove did not run");
            Ui.WaitFor(() => Docker.State(name) is null && Row(view, name) is null, $"{name} removed, and gone from the window");
            Assert.Null(window.Asked);
            window.Close();
        }
        finally
        {
            // Its shell spins a core for as long as it runs, which every test after this one would share.
            Docker.Remove(name);
        }
    }

    private void A_folders_end_removes_what_passenger_started_of_a_compose_project_and_leaves_the_persons_own_of_it()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string folder = TestFolders.Create("ctr-project-");
        MainWindow window = Ui.OpenFolder(folder);
        string project = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}ours";
        // web as Passenger's own up makes it, with the folder's labels; db as the person started it, with none of Passenger's.
        string ours = string.Join(", ", window.Session.Containers.Labels.Where(w => w != "--label").Append(Docker.RunLabel).Select(l => l.Split('=', 2)).Select(l => $"\"{l[0]}\": \"{l[1]}\""));
        string run = Docker.RunLabel.Replace("=", ": \"", StringComparison.Ordinal) + "\"";
        string compose = Path.Combine(folder, "compose.yaml");
        File.WriteAllText(compose, $"name: {project}\nservices:\n  web:\n    image: {Docker.Image}\n    command: [\"sleep\", \"600\"]\n    labels: {{ {ours} }}\n"
            + $"  db:\n    image: {Docker.Image}\n    command: [\"sleep\", \"600\"]\n    labels: {{ {run} }}\nnetworks:\n  default:\n    labels: {{ {run} }}\n");
        Docker.Run("compose", "-f", compose, "up", "-d");
        try
        {
            window.Session.Containers.StartedProject(["compose", "-p", project, "-f", compose]);
            string web = $"{project}-web-1";
            string db = $"{project}-db-1";
            Assert.Null(window.CloseSolution());
            Ui.Settle();
            Ui.WaitFor(() => Docker.State(web) is null, $"{web}, which Passenger's labels are on, removed as the folder ended");
            Assert.Equal("running", Docker.State(db));
            window.Close();
        }
        finally
        {
            Docker.Remove($"{project}-web-1", $"{project}-db-1");
        }
    }

    private void Removing_a_volume_asks_first_since_its_data_goes_with_it()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string volume = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}data";
        Docker.Run("volume", "create", "--label", Docker.RunLabel, volume);
        MainWindow window = Ui.OpenFolder(TestFolders.Create("ctr-volume-"));
        ContainersView view = Shown(window);
        Ui.WaitFor(() => view.Rows.Any(r => r.Key == "group:Volumes"), "the Volumes heading");
        ListBox list = view.GetVisualDescendants().OfType<ListBox>().First(l => l.ItemsSource is IEnumerable<RunItem<ContainerRow>>);
        TreeKeysCheck.Press(window, list.ItemsSource!, list.Items.OfType<RunItem<ContainerRow>>().First(i => i.Key == "group:Volumes"), i => i.Data, "the Containers window");
        Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Volume && r.Label == volume), $"{volume}'s row");

        Assert.True(window.Perform(AppActions.ContainerRemove), "Remove did not run");
        ConfirmDialog asked = DialogButtonsTests.Check(Asked(window));
        Assert.Contains(Texts(asked), t => t.StartsWith($"Remove the volume {volume} and the data it holds?", StringComparison.Ordinal));
        Ui.ClickButton(asked, "Cancel");
        Assert.True(Docker.VolumeExists(volume), "The volume was removed though the question was cancelled");

        Assert.True(window.Perform(AppActions.ContainerRemove), "Remove did not run");
        Ui.ClickButton(Asked(window), "Remove");
        Ui.WaitFor(() => !Docker.VolumeExists(volume) && !view.Rows.Any(r => r.Label == volume),
            $"{volume} removed, and gone from the window");
        window.Close();

        static ConfirmDialog Asked(MainWindow window)
        {
            Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(d => d.IsVisible), "Remove's question");
            return window.OwnedWindows.OfType<ConfirmDialog>().Single(d => d.IsVisible);
        }

        static string[] Texts(Window dialog) => [.. dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];
    }

    /// <summary>A container's stats come though its CLI says much on its standard error first, more than a pipe holds unread,
    /// and once the stream ends the pane says why, in the CLI's own words, where the numbers were.</summary>
    private void Stats_come_though_the_cli_says_much_on_its_errors_and_say_why_the_stream_ended()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        string folder = TestFolders.Create("ctr-stats-errors-");
        string end = Path.Combine(folder, "end-stats");
        const string Reading = "{\"Name\":\"shop-web-1\",\"CPUPerc\":\"1.50%\",\"MemPerc\":\"2.00%\",\"MemUsage\":\"10MiB / 1GiB\",\"NetIO\":\"1kB / 2kB\",\"BlockIO\":\"0B / 0B\",\"PIDs\":\"3\"}";
        (string cli, _) = StandInEngine(folder, up: true, stats: "head -c 300000 /dev/zero | tr '\\0' w >&2; echo >&2; "
            + $"while [ ! -f '{end}' ]; do echo '{Reading}'; sleep 0.2; done; echo 'Error response from daemon: the engine went away' >&2; exit 1");
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: cli) };
        window.Show();
        window.OpenFolder(TestFolders.Create("ctr-stats-errors-window-"));
        Ui.Settle();
        try
        {
            ContainersView view = Shown(window);
            Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Service && r.Service == "web"), "web's row");
            view.ShowTab("Stats");
            Ui.WaitFor(() => view.Stats.Latest.Count == 1, $"web's numbers, which come after what the CLI said on its errors (the pane says \"{view.Stats.Said}\")",
                TimeSpan.FromSeconds(20));
            Assert.Equal(1.5, view.Stats.Latest[0].Cpu);
            Assert.Null(view.Stats.Said);
            File.WriteAllText(end, string.Empty);
            Ui.WaitFor(() => view.Stats.Said == "docker stats ended: Error response from daemon: the engine went away",
                $"the pane to say why its stream ended, not \"{view.Stats.Said}\"");
            Assert.Empty(view.Stats.Latest);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A .NET service's metrics are read only while they are shown: hiding the Containers window ends a reading, a
    /// program found after it hid starts none, and shown again the row chosen is read again. The stand-in's program is held
    /// paused, so the pane says what it follows without a session.</summary>
    private void Hiding_the_Containers_window_ends_a_services_metrics_and_a_lookup_under_way_starts_none()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        string folder = TestFolders.Create("ctr-metrics-hidden-");
        (string cli, _) = StandInEngine(folder, up: true);
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: cli) };
        window.Show();
        window.OpenFolder(TestFolders.Create("ctr-metrics-hidden-window-"));
        Ui.Settle();
        try
        {
            ContainersView view = Shown(window);
            Queue<TaskCompletionSource<DotnetLookup?>> lookups = [];
            view.DotnetOf = (_, _) =>
            {
                TaskCompletionSource<DotnetLookup?> lookup = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lookups.Enqueue(lookup);
                return lookup.Task;
            };
            view.Metrics.Paused = (_, _) => true;
            Passenger.Profiler.ProfilePlace place = new(_ => throw new InvalidOperationException("The stand-in's metrics were read"), "a stand-in", path => path);
            string paused = Refusals.MetricsWhilePaused("web");
            Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Service && r.Service == "web"), "web's row");
            view.ShowTab(RunWindows.StatsTab);
            Ui.WaitFor(() => lookups.Count == 1, "web's program to be looked for");
            lookups.Dequeue().SetResult(DotnetLookup.Found(1, place));
            Ui.WaitFor(() => view.Metrics.Said == paused, () => $"web's metrics to be followed, not \"{view.Metrics.Said}\"");
            Assert.True(view.Metrics.IsVisible, "web's metrics had no room under its Stats");

            window.DockHost.Hide(ToolWindowIds.Containers);
            Ui.Settle();
            Assert.True(view.Metrics.Said.Length == 0 && !view.Metrics.IsVisible, $"With the Containers window hidden, web's metrics were still read: \"{view.Metrics.Said}\"");

            window.ShowToolWindow(ToolWindowIds.Containers);
            Ui.WaitFor(() => lookups.Count == 1, "web's program to be looked for again as its Stats show once more");
            TaskCompletionSource<DotnetLookup?> underWay = lookups.Dequeue();
            window.DockHost.Hide(ToolWindowIds.Containers);
            Ui.Settle();
            underWay.SetResult(DotnetLookup.Found(1, place));
            Ui.Settle();
            Assert.True(view.Metrics.Said.Length == 0 && !view.Metrics.IsVisible, $"A lookup that ended with the window hidden began a reading: \"{view.Metrics.Said}\"");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A .NET service chosen before its program can be reached has its metrics say so in their place, looking for it
    /// meanwhile, then why it was not reached, and look again by themselves, nothing else changing, until it is found; and
    /// once its run has ended they let go and look no more. The stand-in's program is held paused, so the pane says what it
    /// follows without a session.</summary>
    private void A_services_metrics_say_why_its_program_is_not_reached_and_look_again_until_found_or_its_run_ends()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        string folder = TestFolders.Create("ctr-metrics-unreached-");
        (string cli, _) = StandInEngine(folder, up: true);
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: cli) };
        window.Show();
        window.OpenFolder(TestFolders.Create("ctr-metrics-unreached-window-"));
        Ui.Settle();
        try
        {
            ContainersView view = Shown(window);
            Queue<TaskCompletionSource<DotnetLookup?>> lookups = [];
            view.DotnetOf = (_, _) =>
            {
                TaskCompletionSource<DotnetLookup?> lookup = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lookups.Enqueue(lookup);
                return lookup.Task;
            };
            view.Metrics.Paused = (_, _) => true;
            Passenger.Profiler.ProfilePlace place = new(_ => throw new InvalidOperationException("The stand-in's metrics were read"), "a stand-in", path => path);
            const string Unreached = "web: its runtime has yet to open its diagnostic port";
            Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Service && r.Service == "web"), "web's row");
            view.ShowTab(RunWindows.StatsTab);
            Ui.WaitFor(() => lookups.Count == 1, "web's program to be looked for");
            Assert.True(view.Metrics.IsVisible && view.Metrics.Said == ContainersView.Looking("web"),
                $"While web's program was looked for, its metrics said nothing of it: \"{view.Metrics.Said}\"");

            lookups.Dequeue().SetResult(DotnetLookup.Missing(Unreached));
            Ui.WaitFor(() => view.Metrics.Said == ContainersView.LookingAgain(Unreached), () => $"web's metrics to say why its program was not reached, not \"{view.Metrics.Said}\"");
            Assert.True(view.Metrics.IsVisible, "web's metrics said why its program was not reached where nobody sees it");
            Ui.WaitFor(() => lookups.Count == 1, "web's program to be looked for again, nothing else having changed");
            lookups.Dequeue().SetResult(DotnetLookup.Found(1, place));
            Ui.WaitFor(() => view.Metrics.Said == Refusals.MetricsWhilePaused("web"), () => $"web's metrics to be followed once its program was found, not \"{view.Metrics.Said}\"");

            Assert.True(view.Choose(r => r.Kind == ContainerRowKind.Project), "the project's row");
            Ui.Settle();
            Assert.True(view.Choose(r => r.Kind == ContainerRowKind.Service && r.Service == "web"), "web's row again");
            Ui.WaitFor(() => lookups.Count == 1, "web's program to be looked for as its row is chosen again");
            lookups.Dequeue().SetResult(DotnetLookup.Missing(Unreached));
            Ui.WaitFor(() => lookups.Count == 1, "web's program to be looked for again");
            // Its run ended: none to look for.
            lookups.Dequeue().SetResult(null);
            Ui.WaitFor(() => view.Metrics.Said.Length == 0 && !view.Metrics.IsVisible, () => $"web's metrics to be let go once its run ended, not \"{view.Metrics.Said}\"");
            Ui.Hold(TimeSpan.FromSeconds(3), "longer than the first wait between lookups");
            Assert.True(lookups.Count == 0, "web's program was looked for again after its run had ended");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The Files tab copies a container's file only into Passenger's cache, never a huge one, and as the person's
    /// alone: the cache's folders 0700 and the copy 0600 whatever the umask, as a secret's file may be one; a file opened
    /// again shows its new text.</summary>
    private void A_container_can_never_have_its_files_tab_write_outside_passengers_cache_nor_copy_a_huge_file_and_its_copies_are_the_persons_alone()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The escape is written as a path of this machine's from its root, which a Windows path is not");
        // Where a made-up name leads from the cache, should its ..s be followed: the test's own folder, never written to.
        string outside = TestFolders.Create("ctr-escape-");
        string escaped = Path.Combine(outside, "escaped.txt");
        string made = "x/" + string.Join('/', Enumerable.Repeat("..", 64)) + outside.TrimStart('/') + "/escaped.txt";
        // The image's ls is its own: it lists names no folder can hold, beside two real files.
        string ls = string.Join(' ', new[] { "#!/bin/sh", "echo note.txt", "echo big", $"echo \"{made}\"", "echo ..", "echo ./", "echo \"a/b\"" }.Select(l => $"'{l}'"));
        string name = Docker.Container("escape",
            $"echo first > /note.txt; dd if=/dev/zero of=/big bs=1048576 count=20 2>/dev/null; mkdir -p '/{outside.TrimStart('/')}'; " +
            $"echo inside > '/{outside.TrimStart('/')}/escaped.txt'; rm /bin/ls; printf '%s\\n' {ls} > /bin/ls; chmod +x /bin/ls; sleep 600");
        try
        {
            MainWindow window = Ui.OpenFolder(TestFolders.Create("ctr-escape-window-"));
            ContainersView view = Shown(window);
            Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Container && r.Label == name), $"{name}'s row");
            view.ShowTab("Files");
            Ui.WaitFor(() => view.Files.Entries.Count > 0, "the container's root listed");
            Assert.Equal(["/big", "/note.txt"], view.Files.Entries.Select(f => f.Path).Order(StringComparer.Ordinal));

            // Asked for by its path all the same (as a test or a stale row could), it is refused and nothing is written.
            Assert.NotNull(Ui.Await(view.Files.OpenAsync("/" + made), "the made-up path's answer"));
            Assert.False(File.Exists(escaped), $"A container's made-up file name had Passenger write {escaped}, outside its cache");

            Assert.Equal("/big is larger than 16 MB", Ui.Await(view.Files.OpenAsync("/big"), "the large file's answer"));
            string cache = Path.Combine(window.StateFolder, "cache", "containers");
            Assert.Empty(Directory.Exists(cache) ? Directory.EnumerateFiles(cache, "*big*", SearchOption.AllDirectories) : []);

            _ = view.Files.ActivateAsync(view.Files.Entries.First(f => f.Path == "/note.txt"));
            Ui.WaitFor(() => window.EditorArea.Active?.Path.EndsWith("note.txt", StringComparison.Ordinal) == true, "note.txt open in the editor");
            Passenger.App.Workspace.OpenDocument note = window.EditorArea.Active!;
            Assert.Equal("first\n", note.Text.Snapshot.ToString());
            Assert.True(PathNames.IsUnder(note.Path, PathNames.Canonical(cache)), $"note.txt's copy {note.Path} is not under the cache {cache}");
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode Folder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                for (string? at = Path.GetDirectoryName(note.Path); at is not null && PathNames.Within(at, PathNames.Canonical(cache)); at = Path.GetDirectoryName(at))
                {
                    Assert.True(File.GetUnixFileMode(at) == Folder, $"The cache's folder {at} is {File.GetUnixFileMode(at)}, so another user may enter it");
                }
                Assert.True(File.GetUnixFileMode(note.Path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                    $"note.txt's copy is {File.GetUnixFileMode(note.Path)}, so another user may read a container's secret the person looked at");
            }
            Docker.Run("exec", name, "sh", "-c", "echo second > /note.txt");
            _ = view.Files.ActivateAsync(view.Files.Entries.First(f => f.Path == "/note.txt"));
            Ui.WaitFor(() => note.Text.Snapshot.ToString() == "second\n", $"note.txt's new text in its tab, not {note.Text.Snapshot}");
            Assert.Same(note, window.EditorArea.Active);
            Assert.True(note.IsReadOnly, "A container's file opened again became editable");
            window.Close();
        }
        finally
        {
            Docker.Remove(name);
        }
    }

    private void A_memory_snapshot_a_containers_tools_send_that_does_not_hold_together_is_refused_saying_so()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        // Stand-in tools of this version: one program, a recording begun at once, and each heap mode started sends a frame.
        string tools = StandInTools("[{\"id\":7,\"name\":\"Spins\",\"commandLine\":\"dotnet /app/Spins.dll\",\"user\":\"root\"}]");
        string frames = TestFolders.Create("ctr-heap-frames-");
        // A frame that says it is 1.5 GB long, and sends nothing of it.
        File.WriteAllBytes(Path.Combine(frames, "1"), Frame((byte)'S', 3L << 29, []));
        // A graph whose one object is of a type it does not have.
        File.WriteAllBytes(Path.Combine(frames, "2"), Snapshot(objectType: 99));
        // A graph that says it has hundreds of millions of types in a few bytes.
        File.WriteAllBytes(Path.Combine(frames, "3"), Snapshot(objectType: 0, types: 1 << 28));
        string name = Docker.Container("heap", "sleep 600");
        Docker.Run("cp", Path.Combine(tools, "."), $"{name}:{ContainerTools.Copied}");
        Docker.Run("cp", Path.Combine(frames, "."), $"{name}:/tmp/frames");
        MainWindow window = Ui.OpenFolder(TestFolders.Create("ctr-heap-window-"));
        try
        {
            DotnetProcess program = new(7, "Spins", "dotnet /app/Spins.dll", "root", true) { Container = name };
            Ui.Launched(window, window.AttachProfilerAsync(program), "the profiler to attach to the stand-in tools' program");
            foreach (string said in (string[])[$"more than the {Numbers.Said(1L << 30)} the IDE reads of one", "does not hold together", "does not hold together"])
            {
                Assert.Null(Ui.Await(window.MemorySnapshotAsync(), "the memory snapshot's answer"));
                Assert.StartsWith("No memory snapshot: ", window.Status, StringComparison.Ordinal);
                Assert.Contains(said, window.Status, StringComparison.Ordinal);
            }

            // Tools that never send the fourth: the snapshot given up (an agent's call) ends them there, saying so.
            using CancellationTokenSource givingUp = new();
            Task<Passenger.Profiler.MemorySnapshot?> never = window.MemorySnapshotAsync(givingUp.Token);
            Ui.PumpUntil(() => Docker.Ask(["exec", name, "ps"]).Contains("passenger-tools heap", StringComparison.Ordinal), "the tools to be asked for a snapshot they never send");
            givingUp.Cancel();
            Assert.Null(Ui.Await(never, "the given-up snapshot's answer"));
            Assert.Contains("was stopped", window.Status, StringComparison.Ordinal);
            Ui.PumpUntil(() => !Docker.Ask(["exec", name, "ps"]).Contains("passenger-tools heap", StringComparison.Ordinal), "the tools there to end with the snapshot given up");
        }
        finally
        {
            window.Close();
            Docker.Remove(name);
        }

        static byte[] Frame(byte kind, long length, byte[] body)
        {
            byte[] frame = new byte[10 + body.Length];
            frame[0] = kind;
            frame[1] = 1;
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(2), length);
            body.CopyTo(frame, 10);
            return frame;
        }

        // The heap wire's version 1, deflated: a snapshot of one object of one type kept by one static.
        static byte[] Snapshot(int objectType, int types = 1)
        {
            using MemoryStream body = new();
            using (System.IO.Compression.DeflateStream deflated = new(body, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            using (BinaryWriter w = new(deflated, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(1);
                w.Write(DateTime.UtcNow.ToBinary());
                w.Write(0L);
                w.Write("dumped");
                w.Write(8);
                w.Write(0L);
                w.Write(0L);
                w.Write7BitEncodedInt(types);
                w.Write("Keepsake");
                w.Write(1UL);
                w.Write7BitEncodedInt(0);
                w.Write(false);
                w.Write7BitEncodedInt(0);
                w.Write7BitEncodedInt(1);
                w.Write((byte)0);
                w.Write("Program.Kept");
                w.Write7BitEncodedInt(1);
                w.Write7BitEncodedInt64(4096);
                foreach ((int value, bool wide) in (ReadOnlySpan<(int, bool)>)[(objectType, false), (24, true), (1, false), (0, false)])
                {
                    w.Write7BitEncodedInt(1);
                    if (wide)
                    {
                        w.Write7BitEncodedInt64(value);
                    }
                    else
                    {
                        w.Write7BitEncodedInt(value);
                    }
                }
                w.Write7BitEncodedInt(0);
                foreach ((int value, bool wide) in (ReadOnlySpan<(int, bool)>)[(0, false), (24, true), (0, false), (1, false)])
                {
                    w.Write7BitEncodedInt(1);
                    if (wide)
                    {
                        w.Write7BitEncodedInt64(value);
                    }
                    else
                    {
                        w.Write7BitEncodedInt(value);
                    }
                }
                w.Write7BitEncodedInt(0);
                w.Write7BitEncodedInt(0);
                w.Write7BitEncodedInt(1);
                w.Write7BitEncodedInt64(24);
                w.Write7BitEncodedInt(0);
            }
            return Frame((byte)'S', body.Length, body.ToArray());
        }
    }

    private void Attaching_in_a_container_never_uses_tools_of_another_version_nor_a_list_of_programs_that_is_none()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        // A copy of the tools for every system that says it is another version than this Passenger's, and no release to download from.
        string stale = TestFolders.Create("ctr-stale-tools-");
        foreach (string rid in (string[])["linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64"])
        {
            string folder = Directory.CreateDirectory(Path.Combine(stale, rid)).FullName;
            File.Copy(Path.Combine(StandInTools("[]"), ContainerTools.Executable), Path.Combine(folder, ContainerTools.Executable));
            File.Copy(typeof(Assert).Assembly.Location, Path.Combine(folder, $"{ContainerTools.Executable}.dll"));
        }
        ContainerToolsSource source = new([stale], new Passenger.App.Updates.ReleaseFeed(new Uri("http://127.0.0.1:9/repos/Zaneris/passenger/releases"),
            TimeSpan.FromSeconds(5)), TestFolders.Create("ctr-tools-cache-"));
        string bare = Docker.Container("bare", "sleep 600");
        List<string> made = [bare];
        MainWindow window = new() { Width = 1200, Height = 800, ContainerToolsFrom = source };
        window.Show();
        window.OpenFolder(TestFolders.Create("ctr-attach-"));
        Ui.Settle();
        try
        {
            string? refused = Ui.Await(window.AttachAsync(new DotnetProcess(0, "Spins", "dotnet /app/Spins.dll", "root", true) { Container = bare }), "the attach's answer");
            Assert.True(refused?.Contains("is version", StringComparison.Ordinal) == true && refused.Contains($"not this Passenger's {Product.Version}", StringComparison.Ordinal),
                $"Attaching with only another version's tools here answered: {refused}");
            Assert.Contains("absent", Docker.Run("exec", bare, "sh", "-c", $"test -e {ContainerTools.Copied} && echo there || echo absent"), StringComparison.Ordinal);

            // Tools of this version whose list of programs names none by an id: no program is taken for one.
            string listing = Docker.Container("listing", "sleep 600");
            made.Add(listing);
            Docker.Run("cp", Path.Combine(StandInTools("[{\"name\":\"Spins\",\"commandLine\":\"dotnet /app/Spins.dll\"},{\"id\":\"seven\"},3]"), "."), $"{listing}:{ContainerTools.Copied}");
            string? none = Ui.Await(window.AttachAsync(new DotnetProcess(0, "Spins", "dotnet /app/Spins.dll", "root", true) { Container = listing }), "the attach's answer");
            Assert.True(none?.Contains($"no longer runs in {listing}", StringComparison.Ordinal) == true, $"Attaching to a program the tools named by no id answered: {none}");

            // Tools whose list runs on past what any list takes: refused, never read into the IDE whole.
            string flooding = Docker.Container("flooding", "sleep 600");
            made.Add(flooding);
            Docker.Run("cp", Path.Combine(StandInTools("[]", "head -c 6000000 /dev/zero | tr '\\0' ' '; echo '[]'"), "."), $"{flooding}:{ContainerTools.Copied}");
            string? flood = Ui.Await(window.AttachAsync(new DotnetProcess(0, "Spins", "dotnet /app/Spins.dll", "root", true) { Container = flooding }), "the attach's answer");
            Assert.True(flood?.Contains("more than the 4 MB a list of programs takes", StringComparison.Ordinal) == true, $"Attaching where the tools' list ran on answered: {flood}");
        }
        finally
        {
            window.Close();
            Docker.Remove([.. made]);
        }
    }

    /// <summary>Attaching in a container whose image is for another processor than its engine's says the debugger may not
    /// attach there, the image run under emulation, beside what the program's own note says (a Release build's): the engine's
    /// processor answered by a docker standing in over the real one, the tools a stand-in whose adapter ends at once.</summary>
    private void Attaching_in_an_emulated_container_says_it_is_emulated()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The docker answering another processor for the engine is a shell script");
        string container = Docker.Container("emulated", "sleep 600");
        Docker.Run("cp", Path.Combine(StandInTools("[{\"id\":7,\"name\":\"Spins\",\"commandLine\":\"dotnet /app/Spins.dll\",\"optimized\":true}]"), "."),
            $"{container}:{ContainerTools.Copied}");
        string image = Docker.Run("image", "inspect", "-f", "{{.Architecture}}", Docker.Image);
        string engine = image == "arm64" ? "amd64" : "arm64";
        string cli = Path.Combine(TestFolders.Create("ctr-emulating-"), "docker");
        File.WriteAllText(cli, $"#!/bin/sh\nif [ \"$1 $2 $3\" = 'version --format {{{{.Server.Arch}}}}' ]; then echo {engine}; exit 0; fi\nexec '{ContainerEngine.Shared.Cli}' \"$@\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: cli) };
        window.Show();
        window.OpenFolder(TestFolders.Create("ctr-attach-"));
        Ui.Settle();
        try
        {
            string? refused = Ui.Await(window.AttachAsync(new DotnetProcess(0, "Spins", "dotnet /app/Spins.dll", "root", true) { Container = container }), "the attach's answer");
            Assert.True(refused?.Contains(ContainerTools.Emulation(image, engine), StringComparison.Ordinal) == true,
                $"Attaching in a container of an {image} image on an {engine} engine did not say it runs under emulation: {refused}");
            Assert.Contains(ContainerTools.ReleaseBuild, refused, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            Docker.Remove(container);
        }
    }

    /// <summary>Stand-in container tools of this version: a script that says the version, lists the programs given (or runs
    /// the command given for its list), begins a recording at once, and in its heap mode sends <c>/tmp/frames/N</c> to the
    /// Nth start, nothing when there is none; the folder holding it.</summary>
    private static string StandInTools(string processes, string? listing = null)
    {
        string tools = TestFolders.Create("ctr-standin-tools-");
        string script = Path.Combine(tools, ContainerTools.Executable);
        File.WriteAllText(script, string.Join('\n',
            "#!/bin/sh",
            "case \"$1\" in",
            $"  version) echo {Product.Version} ;;",
            $"  processes) {listing ?? $"echo '{processes}'"} ;;",
            "  trace) echo recording >&2; cat > /dev/null ;;",
            "  heap) n=$(cat /tmp/heaps 2>/dev/null || echo 0); n=$((n+1)); echo $n > /tmp/heaps; read line; cat /tmp/frames/$n; cat > /dev/null ;;",
            "esac",
            string.Empty));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return tools;
    }

    private void Open_shell_runs_a_command_in_the_container_in_a_terminal_tab()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string name = Docker.Container("shell", "sleep 600");
        try
        {
            MainWindow window = Ui.OpenFolder(TestFolders.Create("ctr-shell-"));
            ContainersView view = Shown(window);
            Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Container && r.Label == name), $"{name}'s row");
            Assert.True(window.Perform(AppActions.ContainerShell), "Open Shell did not run");
            Ui.WaitFor(() => window.TerminalPanel.Tabs.Any(t => t.Name == $"{name} (shell)" && !t.HasEnded), "the shell's terminal tab");
            Terminal.TerminalSession shell = window.TerminalPanel.Tabs.First(t => t.Name == $"{name} (shell)").Session;
            Ui.WaitFor(() => Screen(shell).Contains('#', StringComparison.Ordinal), "the container's shell prompt");
            shell.Write("echo inside-$((6*7)) $(hostname)\r");
            Ui.WaitFor(() => Screen(shell).Contains("inside-42", StringComparison.Ordinal), "the command's answer in the shell");
            shell.Write("exit\r");
            Ui.WaitFor(() => window.TerminalPanel.Tabs.First(t => t.Name == $"{name} (shell)").HasEnded, "the shell to end");
            window.Close();
        }
        finally
        {
            Docker.Remove(name);
        }
    }

    private void The_status_bar_and_problems_say_when_there_is_no_docker_or_its_engine_is_not_running()
    {
        MainWindow none = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: null, search: false) };
        none.Show();
        none.OpenFolder(TestFolders.Create("ctr-none-"));
        Ui.Settle();
        ContainersView view = Shown(none);
        Ui.WaitFor(() => view.HasRead, "the engine's state read");
        Assert.Equal(EngineStatus.NoCli, view.State!.Status);
        Assert.StartsWith("No docker command was found", none.Status, StringComparison.Ordinal);
        Assert.Contains(none.BottomPanel.Problems.ContainerProblems, p => p.Message.StartsWith("No docker command was found", StringComparison.Ordinal));
        Assert.Equal("see it in Problems, Containers", none.StatusPlace?.Name);
        none.Close();

        string folder = TestFolders.Create("ctr-down-");
        MainWindow down = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: StandInCli(folder)) };
        down.Show();
        down.OpenFolder(folder);
        Ui.Settle();
        ContainersView downView = Shown(down);
        Ui.WaitFor(() => downView.HasRead, "the stand-in engine's state read");
        Assert.Equal(EngineStatus.NotRunning, downView.State!.Status);
        Assert.StartsWith("Docker isn't running (context stand-in)", down.Status, StringComparison.Ordinal);
        Assert.Equal(EngineState.DesktopApp is null ? "see it in Problems, Containers" : "start Docker Desktop", down.StatusPlace?.Name);
        Assert.Single(down.BottomPanel.Problems.ContainerProblems);
        down.Close();
    }

    private void The_window_follows_an_engine_that_starts_stops_and_starts_again_and_lists_compose_projects_by_their_labels_with_no_compose()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        (string cli, string state) = StandInEngine(TestFolders.Create("ctr-standin-"), up: false);
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: cli) };
        window.Show();
        window.OpenFolder(TestFolders.Create("ctr-follow-"));
        Ui.Settle();
        try
        {
            ContainersView view = Shown(window);
            Ui.WaitFor(() => view.HasRead && view.State?.Status == EngineStatus.NotRunning, "the stopped engine's state read");

            // Listed by its containers' labels, though the stand-in CLI has no docker compose to list it.
            File.WriteAllText(state, "up");
            Ui.WaitFor(() => view.State?.Status == EngineStatus.NoCompose && view.Rows.Any(r => r.Kind == ContainerRowKind.Service && r.Project?.Name == "shop" && r.Service == "web"),
                $"the started engine's Compose project listed, not [{string.Join(", ", view.Rows.Select(r => r.Label))}] ({view.State?.Status})", TimeSpan.FromSeconds(60));
            Assert.Equal("running(1)", view.Rows.Single(r => r.Kind == ContainerRowKind.Project && r.Label == "shop").Detail);
            Assert.StartsWith("Docker Compose v2 (docker compose) is not installed", window.Status, StringComparison.Ordinal);

            File.WriteAllText(state, "down");
            Ui.WaitFor(() => view.State?.Status == EngineStatus.NotRunning && view.Rows.Count == 0, "the stopped engine's empty window", TimeSpan.FromSeconds(60));
            File.WriteAllText(state, "up");
            Ui.WaitFor(() => view.State?.Status == EngineStatus.NoCompose && view.Rows.Any(r => r.Label == "shop"), "the engine's project again once it started", TimeSpan.FromSeconds(60));
        }
        finally
        {
            window.Close();
        }
    }

    private void A_remote_engines_port_opens_on_its_own_machine()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        (string cli, _) = StandInEngine(TestFolders.Create("ctr-remote-"), up: true, host: "tcp://engine.example:2376");
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: cli) };
        window.Show();
        window.OpenFolder(TestFolders.Create("ctr-remote-window-"));
        Ui.Settle();
        try
        {
            ContainersView view = Shown(window);
            Button? link = null;
            Ui.WaitFor(() => (link = view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "containerPort")) is not null, "web's port link");
            Ui.Click(link!);
            Assert.Contains(new Uri("http://engine.example:8080/"), window.Browsed);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A compose run on an engine of another machine opens the browser at the launched service's port on that
    /// machine, its context's host, as the Containers window links it, never this one's localhost. The engine is a stand-in
    /// at 127.0.0.1, its service's published port a loopback server of the test's.</summary>
    private void A_compose_run_on_an_engine_of_another_machine_opens_the_browser_on_that_machine()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        using StandInHttp web = new(exchange => exchange.ReplyAsync(200, "text/plain", "web"));
        string engineFolder = TestFolders.Create("ctr-remote-run-");
        string cli = StandInComposeEngine(engineFolder, "tcp://127.0.0.1:2376", web.Port);
        string folder = TestFolders.Create("ctr-remote-run-window-");
        string compose = TestFolders.Write(folder, "compose.yaml", $"services:\n  web:\n    image: {Docker.Image}\n    ports: [\"8080:80\"]\n");
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: cli) };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        try
        {
            RunConfiguration remote = new()
            {
                Name = "Remote", Kind = RunKind.Compose, ComposeFiles = [compose], LaunchService = "web", LaunchBrowser = true, BrowserUrl = "{Scheme}://{ServiceHost}:{ServicePort}/",
            };
            window.SetConfigurations([remote], remote.Name);
            Ui.Launched(window, window.RunAsync(), "the stand-in engine's compose run to come up");
            Ui.WaitFor(() => window.Browsed.Any(), "the browser to open at web's published port");
            Assert.Equal([new Uri($"http://127.0.0.1:{web.Port.ToString(CultureInfo.InvariantCulture)}/")], window.Browsed);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            window.Close();
            File.WriteAllText(Path.Combine(engineFolder, "down"), string.Empty);
        }
    }

    /// <summary>A Docker Compose project (.dcproj) naming its Compose project (DockerComposeProjectName) runs under that name
    /// over the one Compose's model of its files gives: every command of its run takes it, from reading its files to its
    /// up, and Stop takes down the containers that carry it. The engine is a stand-in, whose every command is written down.</summary>
    private void A_Docker_Compose_projects_own_project_name_is_its_runs_from_reading_its_files_to_its_take_down()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        using StandInHttp web = new(exchange => exchange.ReplyAsync(200, "text/plain", "web"));
        string engineFolder = TestFolders.Create("ctr-dcname-");
        string cli = StandInComposeEngine(engineFolder, "tcp://127.0.0.1:2376", web.Port);
        string folder = ComposeProject("ctr-dcname-window-", "<DockerComposeProjectName>my-shop</DockerComposeProjectName>");
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: cli) };
        window.Show();
        window.OpenFolder(Path.Combine(folder, "Shop.slnx"));
        Ui.Settle();
        string calls = Path.Combine(engineFolder, "calls");
        try
        {
            Assert.True(window.SelectConfiguration("docker-compose"), $"No configuration of the .dcproj: [{string.Join(", ", window.Configurations.Select(c => c.Name))}]");
            Ui.Launched(window, window.RunAsync(), "the Docker Compose project's run to come up");
            string[] run = [.. File.ReadAllLines(calls).Where(c => c.Contains(" -f ", StringComparison.Ordinal))];
            Assert.Contains(run, c => c.Contains(" config", StringComparison.Ordinal));
            Assert.Contains(run, c => c.Contains(" up ", StringComparison.Ordinal));
            Assert.True(run.All(c => c.StartsWith("compose -p my-shop -f ", StringComparison.Ordinal)),
                $"A command of the run took another project name than the .dcproj's my-shop:\n{string.Join('\n', run.Where(c => !c.StartsWith("compose -p my-shop -f ", StringComparison.Ordinal)))}");
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => File.ReadAllLines(calls).Any(c => c.StartsWith("ps -aq ", StringComparison.Ordinal) && c.Contains($"label={ComposeLabels.Project}=my-shop", StringComparison.Ordinal)),
                "Stop to take down the containers carrying the .dcproj's project name");
        }
        finally
        {
            window.Close();
            File.WriteAllText(Path.Combine(engineFolder, "down"), string.Empty);
        }
    }

    /// <summary>A run of several parts in the folder's passenger-runs.json whose Compose part names the project's compose file:
    /// Run brings its services up as the folder's container run (its .dcproj's project name kept) before the command after it
    /// starts, the Compose part's tab the Run window's own console with its log followed, and Stop takes its containers down
    /// with the rest. Run again, the console's Rerun on the Compose part's tab brings its services up once more, alone, the
    /// command's tab kept as it was: the run's earlier start leaves the console's buttons nothing of its own.</summary>
    private void A_runs_Compose_part_comes_up_before_the_part_after_it_starts_in_the_Run_windows_console_and_Stop_takes_it_down()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        using StandInHttp web = new(exchange => exchange.ReplyAsync(200, "text/plain", "web"));
        string engineFolder = TestFolders.Create("ctr-parts-");
        string cli = StandInComposeEngine(engineFolder, "tcp://127.0.0.1:2376", web.Port);
        string folder = ComposeProject("ctr-parts-window-", "<DockerComposeProjectName>my-shop</DockerComposeProjectName>");
        TestFolders.Write(folder, Passenger.Workspace.RunsFile.FileName, """
            { "passengerRuns": 1, "runs": [ { "name": "Stack", "parts": [
              { "compose": ["docker-compose.yml"], "services": ["web"] },
              { "command": "sh -c \"echo after the services\"", "name": "after" }
            ] } ] }
            """);
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: cli) };
        window.Show();
        window.OpenFolder(Path.Combine(folder, "Shop.slnx"));
        Ui.Settle();
        string calls = Path.Combine(engineFolder, "calls");
        try
        {
            Ui.WaitFor(() => window.SelectConfiguration("Stack"), () => $"the folder's run to be listed ({string.Join(", ", window.Configurations.Select(c => c.Name))})");
            Ui.Launched(window, window.RunAsync(), "the run's Compose part and the command after it to start");
            Ui.WaitFor(() => window.RunParts.Select(p => p.Name).SequenceEqual(["docker-compose: web", "after"])
                    && window.RunParts[1].Runner.Shown().ToString().Contains("after the services", StringComparison.Ordinal),
                () => $"the Compose part and the command after it, each in its tab ({string.Join("; ", window.RunParts.Select(p => $"{p.Name} {p.Said}"))}; {window.Status})");
            PartRun compose = window.RunParts[0];
            Assert.True(compose.IsRunning && compose.Console == window.BottomPanel.Run,
                $"The Compose part's tab is not the Run window's console following its log: {compose.Said}");
            string[] run = [.. File.ReadAllLines(calls).Where(c => c.Contains(" -f ", StringComparison.Ordinal))];
            Assert.True(run.Any(c => c.Contains(" up ", StringComparison.Ordinal) && c.EndsWith("-- web", StringComparison.Ordinal) && c.StartsWith("compose -p my-shop -f ", StringComparison.Ordinal)),
                $"The Compose part did not bring up its service web as its .dcproj's project:\n{string.Join('\n', run)}");
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => File.ReadAllLines(calls).Any(c => c.StartsWith("ps -aq ", StringComparison.Ordinal) && c.Contains($"label={ComposeLabels.Project}=my-shop", StringComparison.Ordinal))
                    && !window.IsRunning,
                "Stop to take down the Compose part's containers with the rest");

            int Ups() => File.ReadAllLines(calls).Count(c => c.Contains(" up ", StringComparison.Ordinal) && c.EndsWith("-- web", StringComparison.Ordinal));
            Ui.Launched(window, window.RunAsync(), "the run's parts to start again");
            Ui.WaitFor(() => window.RunParts.Count == 2 && window.RunParts[0].IsRunning && window.RunParts[1].State == PartState.Ended,
                () => $"the run's parts to start again ({string.Join("; ", window.RunParts.Select(p => $"{p.Name} {p.Said}"))}; {window.Status})");
            PartRun after = window.RunParts[1];
            int up = Ups();
            Assert.True(window.BottomPanel.RunWindow.ShownPart == window.RunParts[0], "The Compose part's tab is not the one the Run window shows");
            Ui.Click(window.BottomPanel.Run.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) is string tip && tip.StartsWith("Rerun", StringComparison.Ordinal) && b.IsEffectivelyVisible));
            Ui.WaitFor(() => Ups() > up && window.RunParts[0].IsRunning && window.Actions.Work.Of(AppActions.RunProgram) is null,
                () => $"the Compose part's Rerun to bring web up again ({string.Join("; ", window.RunParts.Select(p => $"{p.Name} {p.Said}"))}; {window.Status})");
            Assert.True(Ups() == up + 1 && window.RunParts.Count == 2 && window.RunParts[1] == after,
                $"The Run console's Rerun on the Compose part's tab brought web up {Ups() - up} times and the parts are {string.Join("; ", window.RunParts.Select(p => $"{p.Name} {p.Said}"))}, where it was to bring its part up once, alone");
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning, "Stop to take the Compose part down again");
        }
        finally
        {
            window.Close();
            File.WriteAllText(Path.Combine(engineFolder, "down"), string.Empty);
        }
    }

    /// <summary>A Docker Compose project that opens the browser (DockerLaunchBrowser, with no DockerLaunchAction) opens its
    /// service's address (DockerServiceUrl, its {ServiceIPAddress} the host the published port is reached on) only once the
    /// service answers HTTP there, the status adding meanwhile that it waits: the engine takes a connection on a published
    /// port from its container's start, before its program listens, which a port here that takes each request and lets it go
    /// unanswered stands in for; once it answers, the page opens and the status says what the run said alone.</summary>
    private void A_Docker_Compose_projects_browser_opens_once_its_service_answers_HTTP_the_status_saying_meanwhile_that_it_waits()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        int asked = 0;
        int answering = 0;
        using StandInHttp web = new(exchange =>
        {
            Interlocked.Increment(ref asked);
            return Volatile.Read(ref answering) == 1 ? exchange.ReplyAsync(200, "text/plain", "web") : Task.CompletedTask;
        });
        string engineFolder = TestFolders.Create("ctr-dcwait-");
        string cli = StandInComposeEngine(engineFolder, "tcp://127.0.0.1:2376", web.Port);
        string folder = ComposeProject("ctr-dcwait-window-", """
            <DockerLaunchBrowser>true</DockerLaunchBrowser>
            <DockerServiceUrl>{Scheme}://{ServiceIPAddress}:{ServicePort}/swagger</DockerServiceUrl>
            <DockerServiceName>web</DockerServiceName>
            """);
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: cli) };
        window.Show();
        window.OpenFolder(Path.Combine(folder, "Shop.slnx"));
        Ui.Settle();
        Uri page = new($"http://127.0.0.1:{web.Port.ToString(CultureInfo.InvariantCulture)}/swagger");
        try
        {
            Assert.True(window.SelectConfiguration("docker-compose"), $"No configuration of the .dcproj: [{string.Join(", ", window.Configurations.Select(c => c.Name))}]");
            Ui.Launched(window, window.RunAsync(), "the Docker Compose project's run to come up");
            Ui.WaitFor(() => Volatile.Read(ref asked) >= 2, "web asked twice, its requests let go unanswered");
            Assert.True(window.Browsed.Count == 0, $"The browser opened at {string.Join(", ", window.Browsed)} before web answered");
            string waiting = window.Status ?? string.Empty;
            Assert.True(waiting.StartsWith("Running docker-compose", StringComparison.Ordinal) && waiting.EndsWith($"; waiting for web to answer at {page} before opening the browser…", StringComparison.Ordinal),
                $"While web let its requests go, the status said: {waiting}");
            Volatile.Write(ref answering, 1);
            Ui.WaitFor(() => window.Browsed.Count > 0, "the browser to open once web answered");
            Assert.Equal([page], window.Browsed);
            Assert.Equal(waiting[..waiting.LastIndexOf("; waiting for web", StringComparison.Ordinal)], window.Status);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            window.Close();
            File.WriteAllText(Path.Combine(engineFolder, "down"), string.Empty);
        }
    }

    /// <summary>Detach lets go of a Docker Compose project's run, taking nothing down: its log no longer followed, the Run
    /// window and the status saying its containers run on, Stop and Detach then greyed. Closing the window then asks, its
    /// containers still running, whether to stop them or leave them running: Stop takes them down with the folder. The
    /// engine is a stand-in, whose every command is written down, its project's container and network listed while up.</summary>
    private void Detach_lets_go_of_a_Docker_Compose_projects_run_and_the_windows_close_asks_and_stops_its_containers()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        (MainWindow window, string engineFolder) = LetGoOf("ctr-dcdetach-", unseen: false);
        try
        {
            Assert.False(window.IsRunning, "The run was still followed once let go of");
            Assert.Null(window.Builds.Containers);
            Assert.Equal("Nothing is running", window.Actions.WhyNot(AppActions.Stop, window.Actions.Context()));
            Assert.NotNull(window.Actions.WhyNot(AppActions.Detach, window.Actions.Context()));
            Assert.False(TakenDown(engineFolder), "Detach took the run's containers down");

            window.Close();
            ChoiceDialog asked = AskedOfLetGo(window);
            Assert.Contains("Detach let go of docker-compose, and its containers still run", Said(asked), StringComparison.Ordinal);
            Ui.ClickButton(asked, "Stop");
            Ui.WaitFor(() => !window.IsVisible, "the window to close once Stop was chosen");
        }
        finally
        {
            window.Close();
            File.WriteAllText(Path.Combine(engineFolder, "down"), string.Empty);
        }
        Ui.WaitFor(() => TakenDown(engineFolder), "the folder's close to take down the containers of the run let go of");
    }

    /// <summary>Close Solution over a run Detach let go of asks too: Cancel keeps the solution open, and Leave Running closes it
    /// leaving the containers running as the person's, never taken down, recorded on disk by the time the solution has closed
    /// so the next start's clean-up passes them over however soon Passenger quits; the window's close after asks nothing
    /// more.</summary>
    private void Leave_Running_keeps_a_runs_containers_let_go_of_as_the_solution_closes_and_records_them_as_the_persons()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        (MainWindow window, string engineFolder) = LetGoOf("ctr-dcleave-", unseen: false);
        try
        {
            string folder = window.Folder!;
            Assert.NotNull(window.CloseSolution());
            Ui.ClickButton(AskedOfLetGo(window), "Cancel");
            Assert.Equal(folder, window.Folder);
            Assert.NotNull(window.CloseSolution());
            Ui.ClickButton(AskedOfLetGo(window), "Leave Running");
            Ui.WaitFor(() => window.Folder is null, "the solution to close once Leave Running was chosen");
            IReadOnlyList<string> written = LeftOnDisk(TestRun.Settings);
            Assert.True(written.Contains("c0ffee000001") && written.Contains("beef00000001"),
                $"Leave Running's record was not on disk once the solution closed, so a Passenger quitting then leaves the next start to remove the containers: [{string.Join(", ", written)}]");
            Assert.False(TakenDown(engineFolder), "Leave Running took the containers down");
            Assert.Contains("c0ffee000001", ContainerLabels.LeftIn(TestRun.Settings));
            Assert.Contains("beef00000001", ContainerLabels.LeftIn(TestRun.Settings));
            window.Close();
            Ui.WaitFor(() => !window.IsVisible, "the window to close asking nothing more");
            Assert.Empty(window.OwnedWindows.OfType<ChoiceDialog>());
            Assert.False(TakenDown(engineFolder), "The window's close took down the containers left running");
        }
        finally
        {
            window.Close();
            File.WriteAllText(Path.Combine(engineFolder, "down"), string.Empty);
        }
    }

    /// <summary>The system's quit over a run Detach let go of asks too: Cancel gives the quit up, the window open, and Stop
    /// carries it on, the window closed and the containers taken down.</summary>
    private void A_quit_over_a_run_let_go_of_asks_Cancel_gives_it_up_and_Stop_carries_it_on_taking_its_containers_down()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        (MainWindow window, string engineFolder) = LetGoOf("ctr-dcquit-", unseen: false);
        try
        {
            Ui.CloseAsQuit(window);
            ChoiceDialog asked = AskedOfLetGo(window);
            Assert.Contains("as Passenger quits", Said(asked), StringComparison.Ordinal);
            Ui.ClickButton(asked, "Cancel");
            Assert.True(window.IsVisible, "Cancel carried the quit on");
            Assert.False(TakenDown(engineFolder), "Cancel took the containers down");
            Ui.CloseAsQuit(window);
            Ui.ClickButton(AskedOfLetGo(window), "Stop");
            Ui.WaitFor(() => !window.IsVisible, "the quit to carry on once Stop was chosen");
        }
        finally
        {
            window.Close();
            File.WriteAllText(Path.Combine(engineFolder, "down"), string.Empty);
        }
        Ui.WaitFor(() => TakenDown(engineFolder), "the quit to take down the containers of the run let go of");
    }

    /// <summary>A Passenger nobody sees never asks: its window's close takes the containers of a run let go of down, as the
    /// folder's close takes all it started.</summary>
    private void A_Passenger_nobody_sees_asks_nothing_and_its_close_stops_a_runs_containers_let_go_of()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        (MainWindow window, string engineFolder) = LetGoOf("ctr-dcunseen-", unseen: true);
        try
        {
            window.Close();
            Ui.WaitFor(() => !window.IsVisible, "the window to close asking nothing");
            Assert.Empty(window.OwnedWindows.OfType<ChoiceDialog>());
        }
        finally
        {
            window.Close();
            File.WriteAllText(Path.Combine(engineFolder, "down"), string.Empty);
        }
        Ui.WaitFor(() => TakenDown(engineFolder), "the close to take down the containers of the run let go of");
    }

    /// <summary>The next start's clean-up of what a Passenger that is gone left passes over what the person chose to leave
    /// running as theirs, reading the record as it stands on disk the moment the choice is made, as a start after a Passenger
    /// that quit at once does: of two containers such a Passenger labelled, the one recorded as left runs on, the other is
    /// removed with its anonymous volume.</summary>
    private void The_next_starts_clean_up_passes_over_what_the_person_chose_to_leave_running_just_before_Passenger_ended()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        MainWindow window = Ui.OpenFolder(TestFolders.Create("ctr-leftkept-"), languageServer: false);
        string[] labels = [.. window.Session.Containers.Labels.Where(word => word != "--label")];
        ContainerLabels.Flush();
        // The next start's state folder, given this one's record as it stands once the choice is made.
        string next = TestFolders.Create("ctr-leftnext-");
        string state = $"{ContainerLabels.State}={ContainerLabels.StateValueOf(next)}";
        string machine = labels.Single(l => l.StartsWith(ContainerLabels.Machine, StringComparison.Ordinal));
        string gone = $"{ContainerLabels.Process}={ContainerLabels.Of(int.MaxValue - 9, 1)}";
        string kept = Docker.Container("leftkept", "sleep 600", state, machine, gone);
        string removed = Docker.ContainerHolding("leftremoved", "sleep 600", "/data", state, machine, gone);
        string[] volumes = Docker.AnonymousVolumes(removed);
        try
        {
            Ui.Await(ContainerLabels.LeaveToPersonAsync(ContainerEngine.Shared, [Docker.Run("inspect", "-f", "{{.Id}}", kept)[..12]]), "the choice to leave a container running");
            File.Copy(Path.Combine(TestRun.Settings, ContainersFile), Path.Combine(next, ContainersFile));
            IReadOnlyList<string> cleaned = Ui.Await(Waiting.Apart(() => ContainerLabels.RemoveLeftoversAsync(ContainerEngine.Shared, next)), "the clean-up of what a gone Passenger left");
            Assert.True(Docker.State(kept) == "running",
                $"The next start's clean-up removed {kept}, which the person chose to leave running just before Passenger ended: it removed [{string.Join(", ", cleaned)}]");
            Assert.True(Docker.State(removed) is null, $"The clean-up left {removed}, which nobody chose to leave running: it removed [{string.Join(", ", cleaned)}]");
            string[] held = Docker.Held(volumes);
            Assert.True(volumes.Length == 1 && held.Length == 0, $"The clean-up left {removed}'s anonymous volume ({string.Join(", ", held)} of {string.Join(", ", volumes)}), which nothing mounts again");
        }
        finally
        {
            Docker.Remove(kept, removed);
            window.Close();
            TestFolders.Delete(next);
        }
    }

    /// <summary>A choice to leave containers running is on disk once made, never a moment later, so a Passenger that ends at
    /// once leaves it for the next start; and what another Passenger over the same state left running meanwhile stays beside
    /// it, the two records joined rather than one taking the other's place.</summary>
    private void A_choice_to_leave_containers_running_is_on_disk_once_made_and_another_Passengers_stays_beside_it()
    {
        string file = Path.Combine(TestRun.Settings, ContainersFile);
        Ui.Await(ContainerLabels.LeaveToPersonAsync(ContainerEngine.Shared, ["17580a000001"]), "the choice to leave a container running");
        Assert.True(LeftOnDisk(TestRun.Settings).Contains("17580a000001"),
            $"The choice to leave a container running was not on disk once made, so a Passenger ending then has the next start remove it: [{string.Join(", ", LeftOnDisk(TestRun.Settings))}]");

        // Another Passenger over the same state leaves one of its own, writing the record before this one's next choice.
        System.Text.Json.Nodes.JsonObject other = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        other["left"]!.AsArray().Add("17580b000001");
        File.WriteAllText(file, other.ToJsonString());
        Ui.Await(ContainerLabels.LeaveToPersonAsync(ContainerEngine.Shared, ["17580c000001"]), "a second choice to leave a container running");
        IReadOnlyList<string> written = LeftOnDisk(TestRun.Settings);
        Assert.True(written.Contains("17580a000001") && written.Contains("17580b000001") && written.Contains("17580c000001"),
            $"Two Passengers' choices to leave containers running did not both stay in the record, so the next start removes the other's: [{string.Join(", ", written)}]");
    }

    /// <summary>The record of what was left running, in a state folder.</summary>
    private const string ContainersFile = "containers.json";

    /// <summary>What a state folder's record holds as left running, as the disk has it now: nothing where there is no record.</summary>
    private static IReadOnlyList<string> LeftOnDisk(string stateFolder) => File.Exists(Path.Combine(stateFolder, ContainersFile))
        ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(stateFolder, ContainersFile)))?["left"]?.AsArray().Select(id => id!.GetValue<string>()).ToList() ?? []
        : [];

    /// <summary>A window on a .dcproj's solution run on a stand-in engine that lists its container and network while up, the
    /// run let go of by Detach: said in the Run window and the status.</summary>
    private static (MainWindow Window, string EngineFolder) LetGoOf(string prefix, bool unseen)
    {
        string engineFolder = TestFolders.Create(prefix);
        string cli = StandInComposeEngine(engineFolder, "tcp://127.0.0.1:2376", 1);
        File.WriteAllText(Path.Combine(engineFolder, "up"), string.Empty);
        string folder = ComposeProject($"{prefix}window-", "<DockerComposeProjectName>my-shop</DockerComposeProjectName>");
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: cli), Unseen = unseen };
        window.Show();
        window.OpenFolder(Path.Combine(folder, "Shop.slnx"));
        Ui.Settle();
        Assert.True(window.SelectConfiguration("docker-compose"), $"No configuration of the .dcproj: [{string.Join(", ", window.Configurations.Select(c => c.Name))}]");
        Ui.Launched(window, window.RunAsync(), "the Docker Compose project's run to come up");
        Assert.Null(window.Actions.WhyNot(AppActions.Detach, window.Actions.Context()));
        Assert.True(window.Perform(AppActions.Detach), "Detach did not run");
        Ui.WaitFor(() => window.Status?.StartsWith("Detached from docker-compose", StringComparison.Ordinal) == true, () => $"Detach to be said, not \"{window.Status}\"");
        Assert.Equal("Detached from docker-compose: its containers run on until the Containers window stops them or the folder closes", window.Status);
        window.BottomPanel.Run.Flush();
        Assert.Contains("Detached from docker-compose: its containers run on", window.BottomPanel.Run.Document.Snapshot.ToString(), StringComparison.Ordinal);
        return (window, engineFolder);
    }

    /// <summary>Whether the stand-in was told to remove the project's container, as a take-down by its labels does.</summary>
    private static bool TakenDown(string engineFolder) =>
        File.ReadAllLines(Path.Combine(engineFolder, "calls")).Any(c => c == string.Join(' ', ContainerLabels.Removing(["c0ffee000001"])));

    /// <summary>The question leaving the folder asks of runs Detach let go of, held to the dialogs' rules.</summary>
    internal static ChoiceDialog AskedOfLetGo(MainWindow window)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ChoiceDialog>().Any(), "the window to ask whether to stop the containers of the run let go of");
        ChoiceDialog asked = DialogButtonsTests.Check(window.OwnedWindows.OfType<ChoiceDialog>().Single());
        Assert.Equal("Containers Left Running", asked.Title);
        return asked;
    }

    /// <summary>What a question says, its paragraphs joined.</summary>
    private static string Said(Window asked) => string.Join('\n', asked.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).OfType<string>());

    /// <summary>A folder of a solution naming a Docker Compose project (.dcproj) alone, whose docker-compose.yml runs a busybox
    /// web publishing its port 80, the .dcproj's properties those given.</summary>
    private static string ComposeProject(string prefix, string properties)
    {
        string folder = PathNames.Canonical(TestFolders.Create(prefix));
        TestFolders.Write(folder, "docker-compose.yml", $"services:\n  web:\n    image: {Docker.Image}\n    ports: [\"8080:80\"]\n");
        TestFolders.Write(folder, "docker-compose.dcproj", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" Sdk="Microsoft.Docker.Sdk">
              <PropertyGroup Label="Globals">
                <ProjectVersion>2.1</ProjectVersion>
                <DockerTargetOS>Linux</DockerTargetOS>
                {properties}
              </PropertyGroup>
            </Project>

            """);
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"docker-compose.dcproj\" />\n</Solution>\n");
        return folder;
    }

    /// <summary>A docker of the test's own for a compose run: a Linux server at <paramref name="host"/> with Compose v2, whose
    /// model of any compose files is a project "remote" of one service web (busybox, its port 80 published on 8080), which
    /// comes up at once, publishes 80 on <paramref name="port"/>, and logs and follows nothing until a file <c>down</c> is
    /// made beside it, and lists one container's and one network's id (c0ffee000001, beef00000001) to <c>ps -q</c> and
    /// <c>network ls -q</c> while a file <c>up</c> is, else none; every command it is given written to <c>calls</c>. The CLI.</summary>
    private static string StandInComposeEngine(string folder, string host, int port)
    {
        const string Model = """{"name":"remote","services":{"web":{"image":"busybox:1.38","ports":[{"mode":"ingress","target":80,"published":"8080","protocol":"tcp"}],"networks":{"default":null}}},"networks":{"default":{"name":"remote_default"}}}""";
        const string Wait = "while [ ! -f \"$here/down\" ]; do sleep 0.2; done; exit 0";
        string cli = Path.Combine(folder, "docker");
        File.WriteAllText(cli, string.Join('\n', (string[])[
            "#!/bin/sh",
            "here=$(dirname \"$0\")",
            "echo \"$*\" >> \"$here/calls\"",
            "case \"$1\" in",
            "  version) echo '{\"Client\":{\"Version\":\"29.0.0\",\"Context\":\"remote\"},\"Server\":{\"Version\":\"29.0.0\",\"Os\":\"linux\",\"Arch\":\"arm64\"}}'; exit 0 ;;",
            $"  context) echo '[{{\"Name\":\"remote\",\"Endpoints\":{{\"docker\":{{\"Host\":\"{host}\"}}}}}}]'; exit 0 ;;",
            "  info) echo '{\"SecurityOptions\":[\"name=seccomp,profile=builtin\"]}'; exit 0 ;;",
            $"  events) {Wait} ;;",
            "  ps|network) if [ -f \"$here/up\" ]; then case \" $* \" in *\" -q \"*|*\" -aq \"*) if [ \"$1\" = ps ]; then echo c0ffee000001; else echo beef00000001; fi ;; esac; fi; exit 0 ;;",
            "  compose) ;;",
            "  *) exit 0 ;;",
            "esac",
            "for word in \"$@\"; do",
            "  case \"$word\" in",
            "    version) echo '{\"version\":\"2.39.0\"}'; exit 0 ;;",
            $"    config) echo '{Model}'; exit 0 ;;",
            $"    port) echo '0.0.0.0:{port.ToString(CultureInfo.InvariantCulture)}'; exit 0 ;;",
            $"    logs) {Wait} ;;",
            "    up|ps|down|rm|stop|restart) exit 0 ;;",
            "  esac",
            "done",
            "exit 0",
            string.Empty]));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return cli;
    }

    /// <summary>A container its restart policy starts again after each crash, and one that exited with an error, show
    /// red as failed; one a Stop ended (SIGKILL's 137) and one that exited cleanly show stopped, and each dot's tip
    /// says the engine's words. A container's menu begins with the service actions, greyed with why while no run of the
    /// folder's has it as a .NET service.</summary>
    private void A_container_that_keeps_restarting_or_exited_with_an_error_shows_failed_and_one_stopped_shows_stopped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        (string cli, _) = StandInEngine(TestFolders.Create("ctr-failed-"), up: true, more: [
            """{"ID":"5a5d0c0ffee3","Names":"crashy","Image":"busybox:1.38","State":"restarting","Status":"Restarting (1) 3 seconds ago","Ports":"","Labels":""}""",
            """{"ID":"5a5d0c0ffee4","Names":"broken","Image":"busybox:1.38","State":"exited","Status":"Exited (1) 2 minutes ago","Ports":"","Labels":""}""",
            """{"ID":"5a5d0c0ffee5","Names":"halted","Image":"busybox:1.38","State":"exited","Status":"Exited (137) 1 minute ago","Ports":"","Labels":""}"""]);
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: cli) };
        window.Show();
        window.OpenFolder(TestFolders.Create("ctr-failed-window-"));
        Ui.Settle();
        try
        {
            ContainersView view = Shown(window);
            Ui.WaitFor(() => Row(view, "crashy") is not null && Row(view, "halted") is not null, "the stand-in's containers listed");
            foreach ((string name, string colour, string said) in (ReadOnlySpan<(string, string, string)>)[
                ("crashy", TokenKeys.Error, "Restarting (1) 3 seconds ago"), ("broken", TokenKeys.Error, "Exited (1) 2 minutes ago"),
                ("halted", TokenKeys.TextMuted, "Exited (137) 1 minute ago"), ("solo", TokenKeys.TextMuted, "Exited (0) 2 minutes ago")])
            {
                RunRowView row = view.GetVisualDescendants().OfType<RunRowView>().Single(r => r.Shown?.Label == name);
                Ui.Click(row.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == name));
                Assert.Equal(name, view.Chosen?.Label);
                RowIcon dot = row.StateMark;
                Assert.True(dot.ForegroundKey == colour, $"{name}'s mark is {dot.ForegroundKey}, where a container {said} shows {colour}");
                Assert.Equal($"{name}: {said}", ToolTip.GetTip(dot));
            }

            TextBlock crashy = view.GetVisualDescendants().OfType<RunRowView>().Single(r => r.Shown?.Label == "crashy")
                .GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "crashy");
            Ui.Click(crashy);
            Ui.RightClick(crashy);
            ContextMenu menu = view.GetVisualDescendants().OfType<ListBox>().Single().ContextMenu ?? throw new Xunit.Sdk.XunitException("The row has no menu");
            Ui.WaitFor(() => menu.IsOpen, "crashy's menu to open");
            MenuItem[] first = [.. menu.Items.OfType<Control>().TakeWhile(item => item is MenuItem).Cast<MenuItem>()];
            Assert.Equal(["Debug Service", "Attach to Service", "Run Service without Debugger", "Profile Service"], first.Select(item => (string?)item.Header));
            Assert.All(first, item => Assert.True(!item.IsEnabled && ToolTip.GetTip(item) is "No container run is under way: Run, Debug or Profile a container configuration first",
                $"{item.Header} is {(item.IsEnabled ? "enabled" : "greyed")}, saying {ToolTip.GetTip(item)}"));
            menu.Close();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A stopped container's log, read whole (its lines and its command's end), stays as it was read while the window
    /// reads the engine again.</summary>
    private void A_stopped_containers_log_stays_as_it_was_read_while_the_window_reads_the_engine_again()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string name = Docker.Container("stopped", "for i in 1 2 3 4 5; do echo \"stopped line $i\"; done");
        Ui.WaitFor(() => Docker.State(name) == "exited", $"{name} to end");
        MainWindow window = Ui.OpenFolder(TestFolders.Create("ctr-stopped-"));
        try
        {
            ContainersView view = Shown(window);
            Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Container && r.Label == name), $"{name}'s row");
            OutputView console = view.Log.Console;
            // The engine's CLI may go on a moment after its last line, the end then said after it.
            Ui.WaitFor(() => Text(console).Contains("stopped line 5", StringComparison.Ordinal) && Text(console).Contains(LogEnded, StringComparison.Ordinal),
                () => $"the stopped container's log read whole: [{Text(console)}]");
            StaysAsRead(view);
            Assert.Equal(5, Text(console).Split('\n').Count(l => l.StartsWith("stopped line", StringComparison.Ordinal)));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>What the Containers window's console says as a log's command ends.</summary>
    private const string LogEnded = "Process finished with exit code 0.";

    /// <summary>Has the window read the engine again twice, finding its log as it was read, and gives that log.</summary>
    private static string StaysAsRead(ContainersView view)
    {
        OutputView console = view.Log.Console;
        string read = Text(console);
        int changes = 0;
        console.Document.Changed += Count;
        try
        {
            Ui.Await(view.RefreshAsync().ContinueWith(_ => true, TaskScheduler.Default), "the window's next read of the engine");
            Ui.Await(view.RefreshAsync().ContinueWith(_ => true, TaskScheduler.Default), "the window's read after that");
            Ui.Settle();
        }
        finally
        {
            console.Document.Changed -= Count;
        }
        Assert.True(changes == 0, $"The log changed {changes} times as the window read the engine again, from [{read}] to [{Text(console)}]");
        return read;

        void Count(object? sender, Passenger.Text.DocumentChangedEventArgs change) => changes++;
    }

    private void A_log_whose_command_ends_leaving_a_job_on_its_output_says_it_finished_and_ends_the_job()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        string folder = TestFolders.Create("ctr-held-");
        string job = Path.Combine(folder, "job");
        // Its logs end at once, leaving a job of its own on the same output, as a helper a program starts may.
        (string cli, _) = StandInEngine(folder, up: true, logs: $"sleep 600 & echo $! > '{job}'; echo 'the last line before the end'; exit 0");
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: cli) };
        window.Show();
        window.OpenFolder(TestFolders.Create("ctr-held-window-"));
        Ui.Settle();
        try
        {
            ContainersView view = Shown(window);
            Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Container && r.Label == "solo"), "solo's row");
            OutputView console = view.Log.Console;
            Ui.WaitFor(() => Text(console).Contains("Process finished with exit code 0", StringComparison.Ordinal),
                $"the log's command said to have ended, the window quiet, though a job it left held its output: [{Text(console)}]", TimeSpan.FromSeconds(20));
            Assert.Contains("the last line before the end", Text(console), StringComparison.Ordinal);
            int left = int.Parse(File.ReadAllText(job).Trim(), CultureInfo.InvariantCulture);
            Ui.WaitFor(() => !Alive(left), $"the job (process {left}) that held the log's output, ended with the command's group");
        }
        finally
        {
            window.Close();
        }

        static bool Alive(int id) => DotnetProcesses.Runs(id);
    }

    /// <summary>A docker of the test's own, up or down as its <c>state</c> file says (held: down, its events kept until the
    /// state changes), each command's first word written to <c>calls</c>: a Linux server at <paramref name="host"/>, with Compose
    /// v2 and buildx only where <paramref name="compose"/>, holding a Compose project "shop"'s web (port 8080) and a stopped
    /// container whose id begins as web's, its events going on while up, its logs, stats and more <c>docker ps</c> lines as given,
    /// its version said a second late where <paramref name="late"/>. The CLI and the state file.</summary>
    internal static (string Cli, string State) StandInEngine(string folder, bool up, string host = "unix:///nowhere/docker.sock",
        string logs = "echo 'hello from the stand-in web'", string stats = "exit 1", IReadOnlyList<string>? more = null, bool compose = false, bool late = false)
    {
        string state = Path.Combine(folder, "state");
        File.WriteAllText(state, up ? "up" : "down");
        const string Down = "echo 'Cannot connect to the Docker daemon at unix:///nowhere/docker.sock. Is the docker daemon running?' >&2; exit 1";
        string cli = Path.Combine(folder, "docker");
        File.WriteAllText(cli, string.Join('\n', (string[])[
            "#!/bin/sh",
            "here=$(dirname \"$0\")",
            "running=$(cat \"$here/state\")",
            "echo \"$1\" >> \"$here/calls\"",
            "case \"$1\" in",
            (late ? "  version) sleep 1; " : "  version) ") + "if [ \"$running\" = up ]; then echo '{\"Client\":{\"Version\":\"29.0.0\",\"Context\":\"stand-in\"},\"Server\":{\"Version\":\"29.0.0\",\"Os\":\"linux\",\"Arch\":\"arm64\"}}'; exit 0; fi",
            $"    echo '{{\"Client\":{{\"Version\":\"29.0.0\",\"Context\":\"stand-in\"}},\"Server\":null}}'; {Down} ;;",
            $"  context) echo '[{{\"Name\":\"stand-in\",\"Endpoints\":{{\"docker\":{{\"Host\":\"{host}\"}}}}}}]'; exit 0 ;;",
            .. compose
                ? (string[])["  compose) [ \"$2\" = version ] && { echo '{\"version\":\"2.30.0\"}'; exit 0; } ;;", "  buildx) echo 'github.com/docker/buildx v0.19.0 stand-in'; exit 0 ;;"]
                : ["  compose|buildx) echo \"docker: '$1' is not a docker command.\" >&2; exit 1 ;;"],
            "esac",
            "if [ \"$1\" = events ] && [ \"$running\" = held ]; then while [ \"$(cat \"$here/state\")\" = held ]; do sleep 0.2; done; echo events-ended >> \"$here/calls\"; fi",
            $"[ \"$running\" = up ] || {{ {Down}; }}",
            "case \"$1\" in",
            "  ps) echo '{\"ID\":\"5a5d0c0ffee1\",\"Names\":\"shop-web-1\",\"Image\":\"busybox:1.38\",\"State\":\"running\",\"Status\":\"Up 5 minutes\",\"Ports\":\"0.0.0.0:8080->80/tcp\",\"Labels\":\"com.docker.compose.project=shop,com.docker.compose.service=web\"}'",
            "    echo '{\"ID\":\"5a5d0c0ffee2\",\"Names\":\"solo\",\"Image\":\"busybox:1.38\",\"State\":\"exited\",\"Status\":\"Exited (0) 2 minutes ago\",\"Ports\":\"\",\"Labels\":\"\"}'",
            .. (more ?? []).Select(line => $"    echo '{line}'"),
            "    ;;",
            "  images|volume|network) ;;",
            $"  logs) {logs} ;;",
            $"  stats) {stats} ;;",
            "  events) while [ \"$(cat \"$here/state\")\" = up ]; do sleep 0.2; done ;;",
            "  *) exit 1 ;;",
            "esac",
            string.Empty]));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return (cli, state);
    }

    /// <summary>A docker that answers as a CLI does whose engine is not running: its client's version, then an error.</summary>
    internal static string StandInCli(string folder)
    {
        const string Client = """{"Client":{"Version":"29.0.0","Context":"stand-in"},"Server":null}""";
        const string Error = "Cannot connect to the Docker daemon at unix:///nowhere/docker.sock. Is the docker daemon running?";
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(folder, "docker.cmd");
            File.WriteAllText(script, $"@echo off\r\nif \"%1\"==\"version\" (echo {Client.Replace("\"", "\"", StringComparison.Ordinal)}& echo {Error} 1>&2& exit /b 1)\r\nexit /b 1\r\n");
            return script;
        }
        string sh = Path.Combine(folder, "docker");
        File.WriteAllText(sh, $"#!/bin/sh\nif [ \"$1\" = version ]; then echo '{Client}'; echo '{Error}' >&2; exit 1; fi\nexit 1\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    /// <summary>The Containers window's tabs are actions, as the Aspire window's are: Find Action reaches each by its title and
    /// shows the window on that tab, its tab chosen, one the chosen row has not greyed with why; and Find in Container Logs
    /// shows the Log tab with its Find open.</summary>
    private void Find_Action_reaches_each_of_the_Containers_windows_tabs_and_its_logs_Find()
    {
        string folder = TestFolders.Create("ctr-tabs-");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "no containers here\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            foreach (string tab in (string[])[RunWindows.StatsTab, RunWindows.LogTab])
            {
                FindAction(window, $"Show Container {tab}");
                Ui.WaitFor(() => window.DockHost.IsShown(ToolWindowIds.Containers) && window.ContainersWindow.ShownTab == tab, $"the Containers window on its {tab} tab");
                ToggleButton shown = window.ContainersWindow.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == $"containers{tab}Tab");
                Assert.True(shown.IsChecked == true, $"Find Action showed the Containers window's {tab} tab without choosing it");
            }
            Assert.Equal("Nothing chosen in the Containers window has a Details tab", window.Actions.WhyNot(AppActions.ContainersDetails, window.Actions.Context()));
            FindAction(window, "Find in Container Logs");
            Ui.WaitFor(() => window.ContainersWindow.Log.Find.IsVisible, "the log's Find to open");
            Assert.Equal(RunWindows.LogTab, window.ContainersWindow.ShownTab);
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }

        static void FindAction(MainWindow window, string title)
        {
            Ui.Run(window, AppActions.FindAction);
            window.KeyTextInput(title);
            Ui.Settle();
            Passenger.App.Navigation.ActionPopup popup = Assert.IsType<Passenger.App.Navigation.ActionPopup>(window.Popup);
            Assert.Equal(title, popup.Rows[0].Title);
            Ui.Press(window, "ENTER");
        }
    }

    private static ContainersView Shown(MainWindow window)
    {
        window.ShowToolWindow(ToolWindowIds.Containers);
        Ui.Settle();
        return window.ContainersWindow;
    }

    private static ContainerRow Service(ContainersView view, string project, string service)
    {
        ContainerRow? found = null;
        Ui.WaitFor(() => (found = view.Rows.FirstOrDefault(r => r.Kind == ContainerRowKind.Service && r.Project?.Name == project && r.Service == service)) is { Container.Health: "healthy" or null }
            || found?.Service == "worker", $"{project}'s {service} listed");
        return found!;
    }

    private static ContainerRow? Row(ContainersView view, string name) =>
        view.Rows.FirstOrDefault(r => r.Kind == ContainerRowKind.Container && r.Label == name);

    private static string Text(OutputView console)
    {
        console.Flush();
        return console.Document.Snapshot.ToString();
    }

    private static string Screen(Terminal.TerminalSession shell) => string.Join('\n', shell.Screen.Lines.Select(l => l.Text()));
}
