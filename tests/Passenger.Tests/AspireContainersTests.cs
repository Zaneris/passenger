using System.Globalization;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An AppHost's containers, which its orchestrator (DCP) makes on the container engine with none of Passenger's
/// labels, as a person sees them: under their AppHost in the Containers window, known while the Aspire window is hidden, and
/// gone with the run's Stop but for a Persistent one, the person's to keep. The fixture's starter is given a busybox container
/// of each lifetime (both carrying the run's label, so the run's end removes whatever a failure left); skipped, saying why,
/// without a Linux engine or busybox in it.</summary>
[Collection(Alone.Name)]
public sealed class AspireContainersTests : IClassFixture<AspireApp>
{
    private readonly AspireApp project;

    public AspireContainersTests(AspireApp project)
    {
        this.project = project;
        if (project.WhyNot is null)
        {
            project.Reset();
        }
    }

    /// <summary>The session container's resource name, the run's own so no other run's AppHost makes one of the same.</summary>
    internal static string Session => $"ps{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-session";

    /// <summary>The Persistent container's resource name.</summary>
    internal static string Kept => $"ps{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-kept";

    [AvaloniaFact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public void Stop_takes_down_the_runs_session_container_shown_under_its_AppHost_and_keeps_a_persistent_one()
    {
        Assert.SkipWhen(WhyNot is not null, WhyNot ?? string.Empty);
        WithContainers(Path.GetDirectoryName(project.Host)!, project.Write);
        MainWindow window = project.OpenHost();
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Aspire && PathNames.Same(configuration.Project, project.Host)
                && configuration.LaunchProfile == "http"), "the generated HTTP Aspire configuration");
            Assert.True(window.SelectConfiguration($"Aspire: {AspireApp.Name}.AppHost: http"));
            Assert.False(window.DockHost.IsShown(ToolWindowIds.Aspire), "The Aspire window must be hidden: the run alone follows its containers");
            Ui.Run(window, AppActions.RunProgram);
            string session = Running(Session);
            string kept = Running(Kept);

            window.ShowToolWindow(ToolWindowIds.Containers);
            ContainersView view = window.ContainersWindow;
            Ui.WaitFor(() =>
            {
                if (view.HasRead && !view.Rows.Any(row => row.Kind == ContainerRowKind.AppHost))
                {
                    _ = view.RefreshAsync();
                }
                return view.Rows.Any(row => row.Kind == ContainerRowKind.AppHost && row.Containers.Count == 2);
            }, $"the AppHost's row with its two containers, not [{string.Join(", ", view.Rows.Select(row => $"{row.Kind} {row.Label}"))}]", TimeSpan.FromMinutes(1));
            ContainerRow host = view.Rows.Single(row => row.Kind == ContainerRowKind.AppHost);
            Assert.Equal($"Aspire: {AspireApp.Name}.AppHost: http", host.Label);
            int at = view.Rows.ToList().IndexOf(host);
            Assert.All(view.Rows.Skip(at + 1).Take(2), row => Assert.True(row.Kind == ContainerRowKind.Container && row.Depth == host.Depth + 1,
                $"{row.Label} is not one of the AppHost's containers under it"));
            Assert.True(view.Choose(row => row.Container is { } container && ContainerRow.SameId(session, container.Id)), "The session container's row");
            Ui.Settle();
            view.ShowTab("Details");
            Ui.Settle();
            Assert.Equal("Details", view.ShownTab);

            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => window.AspireRuntime.Apps.Count == 0, "Stop to end the AppHost", TimeSpan.FromMinutes(1));
            // Stop waits for its containers to go: the session one has gone once it has.
            Assert.Null(Docker.State(session));
            Assert.Equal("running", Docker.State(kept));
        }
        finally
        {
            window.Close();
            RemoveMade();
        }
    }

    /// <summary>An AppHost with no Persistent container has its orchestrator put its containers on a session network of
    /// the run's (DCP's <c>aspire-session-network-…</c>, labelled not Persistent), recorded with the session container
    /// as it is made, and Stop takes it down with the container, whatever the orchestrator did of it itself as its
    /// AppHost went.</summary>
    [AvaloniaFact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public void Stop_takes_down_the_runs_session_network_with_its_session_container()
    {
        Assert.SkipWhen(WhyNot is not null, WhyNot ?? string.Empty);
        WithContainers(Path.GetDirectoryName(project.Host)!, project.Write, kept: false);
        MainWindow window = project.OpenHost(languageServer: false);
        string? network = null;
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Aspire && PathNames.Same(configuration.Project, project.Host)
                && configuration.LaunchProfile == "http"), "the generated HTTP Aspire configuration");
            Assert.True(window.SelectConfiguration($"Aspire: {AspireApp.Name}.AppHost: http"));
            Ui.Run(window, AppActions.RunProgram);
            string session = Running(Session);
            string[] networks = (Docker.Said("inspect", "-f", "{{range $name, $_ := .NetworkSettings.Networks}}{{$name}} {{end}}", session) ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            network = Assert.Single(networks);
            Assert.StartsWith("aspire-session-network-", network, StringComparison.Ordinal);
            string record = Path.Combine(window.StateFolder, "aspire-containers", "ownership.json");
            Ui.WaitFor(() => File.Exists(record) && Ui.Shared(record).Contains(session, StringComparison.Ordinal) && Ui.Shared(record).Contains(network, StringComparison.Ordinal),
                "the session container recorded with its network as the orchestrator made them");

            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => window.AspireRuntime.Apps.Count == 0, "Stop to end the AppHost", TimeSpan.FromMinutes(1));
            // Stop waits for its containers and network to go.
            Assert.Null(Docker.State(session));
            Assert.Null(Docker.Said("network", "inspect", network));
        }
        finally
        {
            window.Close();
            RemoveMade();
            if (network is not null)
            {
                Docker.Said("network", "rm", network);
            }
        }
    }

    /// <summary>An AppHost whose orchestrator is told Podman (ASPIRE_CONTAINER_RUNTIME=podman) where Passenger's docker
    /// reaches another engine (Docker's) runs its containers where docker sees none: Passenger finds them on Podman, its
    /// orchestrator's own runtime, records them there, lists them under their AppHost in the Containers window and reads
    /// their Details there, and Stop takes the session container down there, the Persistent one kept. Skipped, saying
    /// why, without Podman beside a docker that reaches another engine, or busybox in Podman's store.</summary>
    [AvaloniaFact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public void An_AppHost_told_Podman_has_its_containers_followed_on_Podman_and_Stop_takes_the_session_one_down_there()
    {
        Assert.SkipWhen(WhyNot is not null, WhyNot ?? string.Empty);
        Assert.SkipWhen(Podman.WhyNot is not null, Podman.WhyNot ?? string.Empty);
        WithContainers(Path.GetDirectoryName(project.Host)!, project.Write);
        TellPodman(Path.GetDirectoryName(project.Host)!, project.Write);
        MainWindow window = project.OpenHost(languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Aspire && PathNames.Same(configuration.Project, project.Host)
                && configuration.LaunchProfile == "http"), "the generated HTTP Aspire configuration");
            Assert.True(window.SelectConfiguration($"Aspire: {AspireApp.Name}.AppHost: http"));
            Ui.Run(window, AppActions.RunProgram);
            string session = Podman.Running(Session);
            string kept = Podman.Running(Kept);
            Assert.Null(Docker.State(session));

            string record = Path.Combine(window.StateFolder, "aspire-containers", "ownership.json");
            Ui.WaitFor(() => File.Exists(record) && Ui.Shared(record).Contains(session, StringComparison.Ordinal), "the session container recorded on Podman");
            using (System.Text.Json.JsonDocument recorded = System.Text.Json.JsonDocument.Parse(Ui.Shared(record)))
            {
                System.Text.Json.JsonElement entry = Assert.Single(recorded.RootElement.GetProperty("entries").EnumerateArray(),
                    entry => entry.GetProperty("id").GetString() == session);
                Assert.True(entry.TryGetProperty("runtime", out System.Text.Json.JsonElement runtime) && runtime.GetString() == "podman",
                    $"The session container was not recorded as on the orchestrator's Podman: {entry}");
            }
            Assert.DoesNotContain(kept, Ui.Shared(record), StringComparison.Ordinal);
            window.ShowToolWindow(ToolWindowIds.Containers);
            ContainersView view = window.ContainersWindow;
            Ui.WaitFor(() => view.Rows.Any(row => row.Kind == ContainerRowKind.AppHost && row.Containers.Count == 2),
                $"the AppHost's row with its two containers on Podman, not [{string.Join(", ", view.Rows.Select(row => $"{row.Kind} {row.Label}"))}]", TimeSpan.FromMinutes(1));
            ContainerRow host = view.Rows.Single(row => row.Kind == ContainerRowKind.AppHost);
            Assert.All(host.Containers, container => Assert.True(container.Engine is { } engine && engine != window.Engine,
                $"{container.Name} is not read from the orchestrator's Podman"));
            Assert.True(view.Choose(row => row.Container is { } container && ContainerRow.SameId(session, container.Id)), "The session container's row");
            Ui.Settle();
            view.ShowTab("Details");
            Ui.WaitFor(() => view.Details.Details?.Id is { } shown && ContainerRow.SameId(shown, session), "the session container's Details, read from Podman");

            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => window.AspireRuntime.Apps.Count == 0, "Stop to end the AppHost", TimeSpan.FromMinutes(1));
            Assert.Null(Podman.State(session));
            Assert.Equal("running", Podman.State(kept));
        }
        finally
        {
            window.Close();
            Podman.RemoveMade();
        }
    }

    /// <summary>Why no AppHost here can make the tests' containers: no Linux engine, or no busybox in it (none is pulled).</summary>
    internal static string? WhyNot => Docker.WhyNot ?? (Docker.Said("image", "inspect", Docker.Image) is null ? NoBusybox : null);

    /// <summary>Skips a test with no window where no AppHost can make the tests' containers (<see cref="WhyNot"/>), saying why.</summary>
    internal static async Task SkipWithoutEngineAsync()
    {
        string? why = await Docker.WhyNotAsync() ?? (await Docker.SaidAsync("image", "inspect", Docker.Image) is null ? NoBusybox : null);
        Assert.SkipWhen(why is not null, why ?? string.Empty);
    }

    private static string NoBusybox => $"The engine lacks {Docker.Image}, which these tests do not pull: docker pull {Docker.Image}";

    /// <summary>The starter's AppHost given a busybox container of each lifetime (the session one alone without
    /// <paramref name="kept"/>), carrying the run's label.</summary>
    internal static void WithContainers(string hostFolder, Func<string, string, string> write, bool kept = true)
    {
        string source = Path.Combine(hostFolder, "AppHost.cs");
        string added = $"""
            builder.AddContainer("{Session}", "busybox", "1.37").WithArgs("sleep", "3600").WithContainerRuntimeArgs("--label", "{Docker.RunLabel}");
            {(kept ? $"builder.AddContainer(\"{Kept}\", \"busybox\", \"1.37\").WithArgs(\"sleep\", \"3600\").WithLifetime(ContainerLifetime.Persistent).WithContainerRuntimeArgs(\"--label\", \"{Docker.RunLabel}\");" : string.Empty)}

            """;
        string text = File.ReadAllText(source);
        Assert.Contains("builder.Build().Run();", text, StringComparison.Ordinal);
        write(source, text.Replace("builder.Build().Run();", added + "builder.Build().Run();", StringComparison.Ordinal));
    }

    /// <summary>The starter's AppHost's HTTP profile telling its orchestrator Podman: ASPIRE_CONTAINER_RUNTIME, and Podman's
    /// store and settings the person's (XDG_DATA_HOME, XDG_CONFIG_HOME), which the profile's own HOME, the run's, would move.</summary>
    private static void TellPodman(string hostFolder, Func<string, string, string> write)
    {
        string settings = Path.Combine(hostFolder, "Properties", "launchSettings.json");
        System.Text.Json.Nodes.JsonObject launch = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        System.Text.Json.Nodes.JsonNode variables = launch["profiles"]!["http"]!["environmentVariables"]!;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        variables[AspireVariables.ContainerRuntime] = "podman";
        variables["XDG_DATA_HOME"] = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(home, ".local", "share");
        variables["XDG_CONFIG_HOME"] = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(home, ".config");
        write(settings, launch.ToJsonString());
    }

    /// <summary>The whole id of the container DCP made for a resource, once it runs, the window's loop running meanwhile.</summary>
    internal static string Running(string resource)
    {
        string? id = null;
        Ui.WaitFor(() => (id = RunningId(resource)) is not null, $"{resource}'s container running", TimeSpan.FromMinutes(3));
        return id!;
    }

    /// <summary>As <see cref="Running"/>, for a test with no window, off the window's thread.</summary>
    internal static async Task<string> RunningAsync(string resource)
    {
        string? id = null;
        Assert.True(await Waiting.UntilAsync(async () => (id = Id(await Docker.SaidAsync(RunningOf(resource)))) is not null, TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken),
            $"{resource}'s container did not run within 3 minutes");
        return id!;
    }

    private static string? RunningId(string resource) => Id(Docker.Said(RunningOf(resource)));

    private static string[] RunningOf(string resource) => ["ps", "-q", "--no-trunc", "--filter", $"label={Docker.RunLabel}", "--filter", $"name={resource}-"];

    private static string? Id(string? said) => said is { Length: 64 } id ? id : null;

    /// <summary>Removes what the tests' AppHosts made: their containers (by the run's label) and the Persistent one's network;
    /// from the UI thread, a test with no window awaiting <see cref="RemoveMadeAsync"/>.</summary>
    internal static void RemoveMade()
    {
        string[] networks = (Docker.Said("ps", "-a", "-q", "--filter", $"label={Docker.RunLabel}", "--filter", $"name={Kept}-") ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(id => (Docker.Said("inspect", "-f", "{{range $name, $_ := .NetworkSettings.Networks}}{{$name}} {{end}}", id) ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct().ToArray();
        foreach (string id in (Docker.Said("ps", "-aq", "--filter", $"label={Docker.RunLabel}") ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(id => Docker.Said("inspect", "-f", "{{.Name}}", id) is { } name && (name.Contains(Session, StringComparison.Ordinal) || name.Contains(Kept, StringComparison.Ordinal))))
        {
            Docker.Said("rm", "-f", id);
        }
        foreach (string network in networks.Where(name => name.StartsWith("aspire-", StringComparison.Ordinal)))
        {
            Docker.Said("network", "rm", network);
        }
    }

    /// <inheritdoc cref="RemoveMade"/>
    internal static async Task RemoveMadeAsync()
    {
        List<string> networks = [];
        foreach (string id in Ids(await Docker.SaidAsync("ps", "-a", "-q", "--filter", $"label={Docker.RunLabel}", "--filter", $"name={Kept}-")))
        {
            networks.AddRange((await Docker.SaidAsync("inspect", "-f", "{{range $name, $_ := .NetworkSettings.Networks}}{{$name}} {{end}}", id) ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        foreach (string id in Ids(await Docker.SaidAsync("ps", "-aq", "--filter", $"label={Docker.RunLabel}")))
        {
            if (await Docker.SaidAsync("inspect", "-f", "{{.Name}}", id) is { } name && (name.Contains(Session, StringComparison.Ordinal) || name.Contains(Kept, StringComparison.Ordinal)))
            {
                await Docker.SaidAsync("rm", "-f", id);
            }
        }
        foreach (string network in networks.Distinct().Where(name => name.StartsWith("aspire-", StringComparison.Ordinal)))
        {
            await Docker.SaidAsync("network", "rm", network);
        }

        static string[] Ids(string? said) => (said ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Podman's own command beside Docker's, the runtime an orchestrator told it runs on: what the tests' AppHosts
    /// made there, by the run's label, and why none can run (no Podman, a docker that reaches Podman itself, no busybox).</summary>
    internal static class Podman
    {
        private static readonly Lazy<ContainerEngine?> Engine = new(() => Programs.OnPath("podman") is { } cli ? new ContainerEngine(cli: cli) : null);

        private static readonly Lazy<string?> Unusable = new(() =>
        {
            if (OperatingSystem.IsWindows() || Engine.Value is null)
            {
                return "Podman is not installed here beside docker, so no orchestrator can be told it";
            }
            if (Said("info", "--format", "{{.Host.OS}}") is not "linux")
            {
                return "Podman does not answer here";
            }
            if (Ui.AwaitPumped(Waiting.Apart(() => ContainerEngine.Shared.StateAsync()), "the container engine's state") is { IsPodman: true })
            {
                return "docker reaches Podman itself here, so an orchestrator told Podman runs its containers where docker sees them";
            }
            return Said("image", "inspect", Docker.Image) is null ? $"Podman lacks {Docker.Image}, which these tests do not pull: podman pull {Docker.Image}" : null;
        });

        /// <summary>Why no orchestrator here can be told Podman, or null when one can.</summary>
        public static string? WhyNot => Unusable.Value;

        /// <summary>What Podman answers, trimmed, or null while it cannot say.</summary>
        public static string? Said(params string[] arguments)
        {
            try
            {
                TimeSpan limit = TimeSpan.FromMinutes(1).Scaled();
                ChildResult result = Ui.AwaitPumped(Waiting.Apart(() => Engine.Value!.RunAsync(arguments, limit)), $"podman {string.Join(' ', arguments)}", limit + TestFolders.Spare);
                return result.Succeeded ? result.Output.Trim() : null;
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        /// <summary>A container's state on Podman (running, exited), or null when it holds none of that id.</summary>
        public static string? State(string id) => Said("inspect", "-f", "{{.State.Status}}", id);

        /// <summary>The whole id of the container an orchestrator made on Podman for a resource, once it runs.</summary>
        public static string Running(string resource)
        {
            string? id = null;
            Ui.WaitFor(() => (id = Said("ps", "-q", "--no-trunc", "--filter", $"label={Docker.RunLabel}", "--filter", $"name={resource}-")) is { Length: 64 },
                $"{resource}'s container running on Podman", TimeSpan.FromMinutes(3));
            return id!;
        }

        /// <summary>Removes what the tests' AppHosts made on Podman: their containers (by the run's label) and their networks.</summary>
        public static void RemoveMade()
        {
            if (Engine.Value is null)
            {
                return;
            }
            string[] ids = (Said("ps", "-aq", "--filter", $"label={Docker.RunLabel}") ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string[] networks = [.. ids.SelectMany(id => (Said("inspect", "-f", "{{range $name, $_ := .NetworkSettings.Networks}}{{$name}} {{end}}", id) ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(name => name.StartsWith("aspire-", StringComparison.Ordinal)).Distinct()];
            if (ids.Length > 0)
            {
                Said(["rm", "-f", "-t", "0", .. ids]);
            }
            foreach (string network in networks)
            {
                Said("network", "rm", network);
            }
        }
    }
}
