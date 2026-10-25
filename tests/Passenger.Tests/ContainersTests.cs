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

    public const string Image = "busybox:1.37";

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

    private static readonly Lazy<string?> Unusable = new(() =>
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
            ChildResult info = Ask(["info", "--format", "{{.OSType}}"], limit);
            if (!info.Succeeded && Ask(["info", "--format", "{{.Host.OS}}"], limit) is { Succeeded: true } podman)
            {
                // A docker that is Podman's own command words its system there.
                info = podman;
            }
            string os = info.Output.Trim();
            string? why = !info.Succeeded ? $"The container engine is not running here: {ContainerEngine.Said(info)}"
                : os != "linux" ? $"The container engine here runs {os} containers, and these tests run Linux ones" : null;
            if (why is null && !mayPull && Images.Where(i => !Ask(["image", "inspect", i], limit).Succeeded).ToArray() is { Length: > 0 } missing)
            {
                why = $"The engine lacks {string.Join(" and ", missing)}, which no test pulls here ({PullVariable}=never): pull them before the run";
            }
            if (why is null)
            {
                RemoveDeadRuns();
            }
            return why;
        }
        catch (TimeoutException)
        {
            return $"The container engine did not answer docker info within {limit.TotalSeconds:0} s";
        }
    });

    /// <summary>What runs of this machine that ended without their own end (killed, crashed) left: everything with a run's
    /// label whose process is gone, and what such a run had its IDE make, labelled with that run's state and process.</summary>
    private static void RemoveDeadRuns()
    {
        string machine = $"{ContainerLabels.MachineValue}.";
        TimeSpan limit = TimeSpan.FromSeconds(60).Scaled();
        try
        {
            foreach ((string kind, string[] list, string[] remove) in (ReadOnlySpan<(string, string[], string[])>)[
                ("container", ["ps", "-a"], ["rm", "-f"]), ("network", ["network", "ls"], ["network", "rm"]), ("volume", ["volume", "ls"], ["volume", "rm", "-f"])])
            {
                string id = kind == "volume" ? "{{.Name}}" : "{{.ID}}";
                string[] dead = [.. Lines(Ask([.. list, "--filter", $"label={RunKey}", "--format", $"{id}|{{{{.Label \"{RunKey}\"}}}}"], limit).Output)
                    .Select(l => l.Split('|')).Where(l => l.Length == 2 && l[1].StartsWith(machine, StringComparison.Ordinal) && ContainerLabels.IsGone(l[1][machine.Length..]))
                    .Select(l => l[0])
                    .Concat(Lines(Ask([.. list, "--filter", $"label={ContainerLabels.Machine}={ContainerLabels.MachineValue}", "--format",
                            $"{id}|{{{{.Label \"{ContainerLabels.State}\"}}}}|{{{{.Label \"{ContainerLabels.Process}\"}}}}"], limit).Output)
                        .Select(l => l.Split('|')).Where(l => l.Length == 3 && IsDeadRuns(l[1], l[2])).Select(l => l[0]))
                    .Distinct(StringComparer.Ordinal)];
                if (dead.Length > 0)
                {
                    Ask([.. remove, .. dead], limit);
                }
            }
            string[] images = Lines(Ask(["image", "ls", "-a", "-q", "--filter", $"label={RunKey}"], limit).Output);
            if (images.Length > 0)
            {
                string[] dead = [.. Lines(Ask(["image", "inspect", "--format", $"{{{{.Id}}}}|{{{{index .Config.Labels \"{RunKey}\"}}}}", .. images.Distinct(StringComparer.Ordinal)], limit).Output)
                    .Select(l => l.Split('|')).Where(l => l.Length == 2 && l[1].StartsWith(machine, StringComparison.Ordinal) && ContainerLabels.IsGone(l[1][machine.Length..]))
                    .Select(l => l[0])];
                if (dead.Length > 0)
                {
                    Ask(["rmi", "-f", .. dead], limit);
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

    /// <summary>Runs docker to its end off the caller's thread, which may be the window's: its continuations never wait on it.</summary>
    private static ChildResult Ask(string[] words, TimeSpan limit) => Task.Run(() => ContainerEngine.Shared.RunAsync(words, limit)).GetAwaiter().GetResult();

    /// <summary>What docker says to a command, its output or else its errors, whether it succeeded or not.</summary>
    public static string Ask(string[] arguments)
    {
        ChildResult result = Ask(arguments, TimeSpan.FromMinutes(1).Scaled());
        return result.Output.Length > 0 ? result.Output : result.Error;
    }

    /// <summary>What a test's docker build takes so its image lands in the engine: Docker's buildx on a builder of its own
    /// (on Podman's socket, a BuildKit container it starts) keeps what it built in that builder's cache unless told to load it.
    /// Podman's own build has no --load, and lands its images.</summary>
    public static string[] Load =>
        Task.Run(() => ContainerEngine.Shared.StateAsync()).GetAwaiter().GetResult() is { PodmanCli: false, BuildxVersion: not null } ? ["--load"] : [];

    /// <summary>An image's name as Docker words it: Podman names an image built or loaded here in full (docker.io/library/…,
    /// localhost/…).</summary>
    public static string Short(string image) =>
        image.StartsWith("docker.io/library/", StringComparison.Ordinal) ? image["docker.io/library/".Length..]
        : image.StartsWith("localhost/", StringComparison.Ordinal) ? image["localhost/".Length..] : image;

    /// <summary>A container's command as Docker's inspect words it: Podman's puts its own init (Compose's init: true) first.</summary>
    public static string WithoutInit(string command) =>
        command.StartsWith("/run/podman-init -- ", StringComparison.Ordinal) ? command["/run/podman-init -- ".Length..] : command;

    /// <summary>Why no container can run here (no CLI, no engine, Windows containers), or null when one can.</summary>
    public static string? WhyNot => Unusable.Value;

    /// <summary>Runs docker to its end, failing the test with what it said when it fails.</summary>
    public static string Run(params string[] arguments)
    {
        ChildResult result = Ask(arguments, TimeSpan.FromMinutes(3).Scaled());
        Assert.True(result.Succeeded, $"docker {string.Join(' ', arguments)} failed: {ContainerEngine.Said(result)}");
        return result.Output.Trim();
    }

    /// <summary>What docker answers, trimmed, or null while it cannot say (it failed, or took a minute): for a wait's
    /// condition, which looks again rather than failing the test.</summary>
    public static string? Said(params string[] arguments)
    {
        try
        {
            ChildResult result = Ask(arguments, TimeSpan.FromMinutes(1).Scaled());
            return result.Succeeded ? result.Output.Trim() : null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Whether a Compose project has no container left (of those carrying each label given too), as a wait looks:
    /// false while docker cannot say.</summary>
    public static bool NoneLeft(string project, params string[] labels) =>
        Said(["ps", "-aq", "--filter", $"label=com.docker.compose.project={project}", .. labels.SelectMany(l => (string[])["--filter", $"label={l}"])]) is "";

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

    /// <summary>A container of busybox running <paramref name="command"/>, named and labelled as the test's, with more labels.</summary>
    public static string Container(string name, string command, params string[] labels)
    {
        string named = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}{name}";
        List<string> words = ["run", "-d", .. AnyPids, "--name", named, "--label", RunLabel];
        foreach (string label in labels)
        {
            words.AddRange(["--label", label]);
        }
        Run([.. words, Image, "sh", "-c", command]);
        return named;
    }

    /// <summary>A container's state as docker inspect says it (running, exited, paused), or null when there is none.</summary>
    public static string? State(string name)
    {
        ChildResult result = Ask(["inspect", "-f", "{{.State.Status}}", name], TimeSpan.FromSeconds(30).Scaled());
        return result.Succeeded ? result.Output.Trim() : null;
    }

    /// <summary>Whether a volume of that name is there.</summary>
    public static bool VolumeExists(string name) => Ask(["volume", "inspect", name], TimeSpan.FromSeconds(30).Scaled()).Succeeded;

    /// <summary>Removes every container, network, volume and image the run's tests made or had the IDE make (the run's state's label), as
    /// the run ends; quiet where there is no engine.</summary>
    public static void RemoveEverything()
    {
        // A run none of whose tests asked for an engine made nothing on one.
        if (!Unusable.IsValueCreated || Unusable.Value is not null)
        {
            return;
        }
        try
        {
            foreach (string label in (string[])[RunLabel, $"{ContainerLabels.State}={ContainerLabels.StateValue}"])
            {
                ChildResult containers = Ask(["ps", "-aq", "--filter", $"label={label}"], TimeSpan.FromSeconds(30).Scaled());
                string[] ids = containers.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (ids.Length > 0)
                {
                    Ask(["rm", "-f", .. ids], TimeSpan.FromSeconds(60).Scaled());
                }
                ChildResult networks = Ask(["network", "ls", "-q", "--filter", $"label={label}"], TimeSpan.FromSeconds(30).Scaled());
                string[] made = networks.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (made.Length > 0)
                {
                    Ask(["network", "rm", .. made], TimeSpan.FromSeconds(60).Scaled());
                }
                ChildResult volumes = Ask(["volume", "ls", "-q", "--filter", $"label={label}"], TimeSpan.FromSeconds(30).Scaled());
                string[] kept = volumes.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (kept.Length > 0)
                {
                    Ask(["volume", "rm", "-f", .. kept], TimeSpan.FromSeconds(60).Scaled());
                }
                // Untagged ones too: one removed while a container ran is only untagged and stays.
                ChildResult images = Ask(["image", "ls", "-a", "-q", "--filter", $"label={label}"], TimeSpan.FromSeconds(30).Scaled());
                string[] built = [.. images.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];
                if (built.Length > 0)
                {
                    Ask(["rmi", "-f", .. built], TimeSpan.FromSeconds(60).Scaled());
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
            image: busybox:1.37
            command: ["sh", "-c", "mkdir -p /www && echo hello > /www/index.html && echo 'web listening on http://localhost:8080/ at /app/Program.cs:line 12' && exec httpd -f -v -p 8080 -h /www"]
            ports: ["8080"]
            labels: { $LABEL }
            healthcheck: { test: ["CMD", "wget", "-q", "-O", "-", "http://localhost:8080/"], interval: 1s, retries: 30 }
          worker:
            image: busybox:1.37
            command: ["sh", "-c", "i=0; while true; do i=$$((i+1)); echo \"worker tick $$i\"; sleep 1; done"]
            labels: { $LABEL }
        """;

    /// <summary>A Compose project the test brought up: listed with its services, ports and health, the port a link the browser
    /// opens; a container's log streaming into its console with links; and the project's log merging its services, each a
    /// toggle, which Find searches by a regular expression.</summary>
    [AvaloniaFact]
    public void A_compose_project_is_listed_with_its_services_ports_and_health_and_its_logs_stream_with_links_and_merge_its_services()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        (string folder, string project) = Docker.ComposeUp("list", Shop);
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
        Assert.Contains(console.Links.In(line), l => l.Target is WebLink { Address.Port: 8080 });

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
        window.Close();

        static int Ticks(OutputView console) => Text(console).Split('\n').Count(l => l.Contains("worker tick", StringComparison.Ordinal));
    }

    /// <summary>One busy container chosen in the window: its Stats show the engine's numbers while that tab shows, and nothing
    /// streams once it is hidden; its Files tab opens a file of its read-only; and Stop, Start, Restart and Remove act on it,
    /// asking nothing.</summary>
    [AvaloniaFact]
    public void A_containers_stats_show_its_numbers_its_files_open_read_only_and_stop_start_restart_and_remove_act_on_it()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string name = Docker.Container("solo", "trap 'exit 0' TERM; echo 'from inside' > /tmp/note.txt; while true; do :; done");
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

    [AvaloniaFact]
    public void A_folders_end_removes_what_passenger_started_of_a_compose_project_and_leaves_the_persons_own_of_it()
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
        window.Session.Containers.StartedProject(["compose", "-p", project, "-f", compose]);
        string web = $"{project}-web-1";
        string db = $"{project}-db-1";
        Assert.Null(window.CloseSolution());
        Ui.Settle();
        Ui.WaitFor(() => Docker.State(web) is null, $"{web}, which Passenger's labels are on, removed as the folder ended");
        Assert.Equal("running", Docker.State(db));
        window.Close();
    }

    [AvaloniaFact]
    public void Removing_a_volume_asks_first_since_its_data_goes_with_it()
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
    [AvaloniaFact]
    public void Stats_come_though_the_cli_says_much_on_its_errors_and_say_why_the_stream_ended()
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

    [AvaloniaFact]
    public void A_container_can_never_have_its_files_tab_write_outside_passengers_cache_nor_copy_a_huge_file_and_a_file_opened_again_shows_its_new_text()
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
        MainWindow window = Ui.OpenFolder(TestFolders.Create("ctr-escape-window-"));
        ContainersView view = Shown(window);
        Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Container && r.Label == name), $"{name}'s row");
        view.ShowTab("Files");
        Ui.WaitFor(() => view.Files.Entries.Count > 0, "the container's root listed");
        Assert.Equal(["/big", "/note.txt"], view.Files.Entries.Select(f => f.Path).Order(StringComparer.Ordinal));

        // Asked for by its path all the same (as a test or a stale row could), it is refused and nothing is written.
        Task<string?> refused = view.Files.OpenAsync("/" + made);
        Ui.WaitFor(() => refused.IsCompleted, "the made-up path's answer");
        Assert.NotNull(refused.Result);
        Assert.False(File.Exists(escaped), $"A container's made-up file name had Passenger write {escaped}, outside its cache");

        Task<string?> big = view.Files.OpenAsync("/big");
        Ui.WaitFor(() => big.IsCompleted, "the large file's answer");
        Assert.Equal("/big is larger than 16 MB", big.Result);
        string cache = Path.Combine(window.StateFolder, "cache", "containers");
        Assert.Empty(Directory.Exists(cache) ? Directory.EnumerateFiles(cache, "*big*", SearchOption.AllDirectories) : []);

        _ = view.Files.ActivateAsync(view.Files.Entries.First(f => f.Path == "/note.txt"));
        Ui.WaitFor(() => window.EditorArea.Active?.Path.EndsWith("note.txt", StringComparison.Ordinal) == true, "note.txt open in the editor");
        Passenger.App.Workspace.OpenDocument note = window.EditorArea.Active!;
        Assert.Equal("first\n", note.Text.Snapshot.ToString());
        Docker.Run("exec", name, "sh", "-c", "echo second > /note.txt");
        _ = view.Files.ActivateAsync(view.Files.Entries.First(f => f.Path == "/note.txt"));
        Ui.WaitFor(() => note.Text.Snapshot.ToString() == "second\n", $"note.txt's new text in its tab, not {note.Text.Snapshot}");
        Assert.Same(note, window.EditorArea.Active);
        Assert.True(note.IsReadOnly, "A container's file opened again became editable");
        window.Close();
    }

    [AvaloniaFact]
    public void A_memory_snapshot_a_containers_tools_send_that_does_not_hold_together_is_refused_saying_so()
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
            foreach (string said in (string[])["more than the 1073741824 the IDE reads of one", "does not hold together", "does not hold together"])
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

    [AvaloniaFact]
    public void Attaching_in_a_container_never_uses_tools_of_another_version_nor_a_list_of_programs_that_is_none()
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
        ContainerToolsSource source = new([stale], new Passenger.App.Updates.ReleaseFeed(new Uri("http://127.0.0.1:9/repos/Zaneris/passenger/releases/latest"),
            TimeSpan.FromSeconds(5)), TestFolders.Create("ctr-tools-cache-"));
        string bare = Docker.Container("bare", "sleep 600");
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
            Docker.Run("cp", Path.Combine(StandInTools("[{\"name\":\"Spins\",\"commandLine\":\"dotnet /app/Spins.dll\"},{\"id\":\"seven\"},3]"), "."), $"{listing}:{ContainerTools.Copied}");
            string? none = Ui.Await(window.AttachAsync(new DotnetProcess(0, "Spins", "dotnet /app/Spins.dll", "root", true) { Container = listing }), "the attach's answer");
            Assert.True(none?.Contains($"no longer runs in {listing}", StringComparison.Ordinal) == true, $"Attaching to a program the tools named by no id answered: {none}");

            // Tools whose list runs on past what any list takes: refused, never read into the IDE whole.
            string flooding = Docker.Container("flooding", "sleep 600");
            Docker.Run("cp", Path.Combine(StandInTools("[]", "head -c 6000000 /dev/zero | tr '\\0' ' '; echo '[]'"), "."), $"{flooding}:{ContainerTools.Copied}");
            string? flood = Ui.Await(window.AttachAsync(new DotnetProcess(0, "Spins", "dotnet /app/Spins.dll", "root", true) { Container = flooding }), "the attach's answer");
            Assert.True(flood?.Contains("more than the 4 MB a list of programs takes", StringComparison.Ordinal) == true, $"Attaching where the tools' list ran on answered: {flood}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Attaching in a container whose image is for another processor than its engine's says the debugger may not
    /// attach there, the image run under emulation, beside what the program's own note says (a Release build's): the engine's
    /// processor answered by a docker standing in over the real one, the tools a stand-in whose adapter ends at once.</summary>
    [AvaloniaFact]
    public void Attaching_in_an_emulated_container_says_it_is_emulated()
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

    [AvaloniaFact]
    public void Open_shell_runs_a_command_in_the_container_in_a_terminal_tab()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string name = Docker.Container("shell", "sleep 600");
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

    [AvaloniaFact]
    public void The_status_bar_and_problems_say_when_there_is_no_docker_or_its_engine_is_not_running()
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

    [AvaloniaFact]
    public void The_window_follows_an_engine_that_starts_stops_and_starts_again_and_lists_compose_projects_by_their_labels_with_no_compose()
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

    [AvaloniaFact]
    public void A_remote_engines_port_opens_on_its_own_machine()
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

    /// <summary>A container its restart policy starts again after each crash, and one that exited with an error, show
    /// red as failed; one a Stop ended (SIGKILL's 137) and one that exited cleanly show stopped, and each dot's tip
    /// says the engine's words. A container's menu begins with the service actions, greyed with why while no run of the
    /// folder's has it as a .NET service.</summary>
    [AvaloniaFact]
    public void A_container_that_keeps_restarting_or_exited_with_an_error_shows_failed_and_one_stopped_shows_stopped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        (string cli, _) = StandInEngine(TestFolders.Create("ctr-failed-"), up: true, more: [
            """{"ID":"5a5d0c0ffee3","Names":"crashy","Image":"busybox:1.37","State":"restarting","Status":"Restarting (1) 3 seconds ago","Ports":"","Labels":""}""",
            """{"ID":"5a5d0c0ffee4","Names":"broken","Image":"busybox:1.37","State":"exited","Status":"Exited (1) 2 minutes ago","Ports":"","Labels":""}""",
            """{"ID":"5a5d0c0ffee5","Names":"halted","Image":"busybox:1.37","State":"exited","Status":"Exited (137) 1 minute ago","Ports":"","Labels":""}"""]);
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
                Avalonia.Controls.Shapes.Ellipse dot = row.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single();
                Assert.True(window.TryFindResource(colour, window.ActualThemeVariant, out object? expected) && expected is Avalonia.Media.ISolidColorBrush brush
                    && dot.Fill is Avalonia.Media.ISolidColorBrush fill && fill.Color == brush.Color,
                    $"{name}'s dot is {(dot.Fill as Avalonia.Media.ISolidColorBrush)?.Color}, where a container {said} shows {colour}");
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

    [AvaloniaFact]
    public void A_stopped_containers_log_stays_as_it_was_read_while_the_window_reads_the_engine_again()
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
            Ui.WaitFor(() => Text(console).Contains("stopped line 5", StringComparison.Ordinal), "the stopped container's log");
            int changes = 0;
            console.Document.Changed += (_, _) => changes++;
            Ui.Await(view.RefreshAsync().ContinueWith(_ => true, TaskScheduler.Default), "the window's next read of the engine");
            Ui.Await(view.RefreshAsync().ContinueWith(_ => true, TaskScheduler.Default), "the window's read after that");
            Ui.Settle();
            Assert.Equal(0, changes);
            Assert.Equal(5, Text(console).Split('\n').Count(l => l.StartsWith("stopped line", StringComparison.Ordinal)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_log_whose_command_ends_leaving_a_job_on_its_output_says_it_finished_and_ends_the_job()
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

        static bool Alive(int id)
        {
            try
            {
                using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(id);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>A docker of the test's own whose engine is up or down as its <c>state</c> file says: a Linux server at
    /// <paramref name="host"/> with no Compose v2 and no buildx, holding a container of a Compose project "shop"
    /// (service web, port 8080) and a stopped one whose id begins as web's, its events going on while up, its logs and
    /// stats what <paramref name="logs"/> and <paramref name="stats"/> (shell commands) write, and <paramref
    /// name="more"/> listed too (<c>docker ps --format json</c> lines). The CLI and the state file.</summary>
    internal static (string Cli, string State) StandInEngine(string folder, bool up, string host = "unix:///nowhere/docker.sock",
        string logs = "echo 'hello from the stand-in web'", string stats = "exit 1", IReadOnlyList<string>? more = null)
    {
        string state = Path.Combine(folder, "state");
        File.WriteAllText(state, up ? "up" : "down");
        const string Down = "echo 'Cannot connect to the Docker daemon at unix:///nowhere/docker.sock. Is the docker daemon running?' >&2; exit 1";
        string cli = Path.Combine(folder, "docker");
        File.WriteAllText(cli, string.Join('\n', (string[])[
            "#!/bin/sh",
            "here=$(dirname \"$0\")",
            "running=$(cat \"$here/state\")",
            "case \"$1\" in",
            "  version) if [ \"$running\" = up ]; then echo '{\"Client\":{\"Version\":\"29.0.0\",\"Context\":\"stand-in\"},\"Server\":{\"Version\":\"29.0.0\",\"Os\":\"linux\",\"Arch\":\"arm64\"}}'; exit 0; fi",
            $"    echo '{{\"Client\":{{\"Version\":\"29.0.0\",\"Context\":\"stand-in\"}},\"Server\":null}}'; {Down} ;;",
            $"  context) echo '[{{\"Name\":\"stand-in\",\"Endpoints\":{{\"docker\":{{\"Host\":\"{host}\"}}}}}}]'; exit 0 ;;",
            "  compose|buildx) echo \"docker: '$1' is not a docker command.\" >&2; exit 1 ;;",
            "esac",
            $"[ \"$running\" = up ] || {{ {Down}; }}",
            "case \"$1\" in",
            "  ps) echo '{\"ID\":\"5a5d0c0ffee1\",\"Names\":\"shop-web-1\",\"Image\":\"busybox:1.37\",\"State\":\"running\",\"Status\":\"Up 5 minutes\",\"Ports\":\"0.0.0.0:8080->80/tcp\",\"Labels\":\"com.docker.compose.project=shop,com.docker.compose.service=web\"}'",
            "    echo '{\"ID\":\"5a5d0c0ffee2\",\"Names\":\"solo\",\"Image\":\"busybox:1.37\",\"State\":\"exited\",\"Status\":\"Exited (0) 2 minutes ago\",\"Ports\":\"\",\"Labels\":\"\"}'",
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
    [AvaloniaFact]
    public void Find_Action_reaches_each_of_the_Containers_windows_tabs_and_its_logs_Find()
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
