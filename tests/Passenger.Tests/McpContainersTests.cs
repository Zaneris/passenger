using System.Text.Json;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Mcp;
using Passenger.App.State;

namespace Passenger.Tests;

/// <summary>An agent's container tools on a real engine: the engine's containers listed, a log read, a container stopped
/// and started, a command run in one only once the person allows it, and what a crashed Passenger left removed as the
/// next one starts. Each skips, saying why, where no Linux engine runs.</summary>
public sealed class McpContainersTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpContainersTests(RestoredFolders folders)
        : base(folders)
    {
    }

    [Fact]
    public async Task An_agent_lists_the_engines_containers_reads_a_log_and_stops_and_starts_one()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string name = Docker.Container("agent", "echo agent-hello; trap 'exit 0' TERM; while true; do sleep 1; done");
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);

        JsonElement listed = await Call(client, McpTools.Containers);
        Assert.Equal("Ready", listed.GetProperty("status").GetString());
        JsonElement mine = listed.GetProperty("containers").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        Assert.Equal("running", mine.GetProperty("state").GetString());
        Assert.Equal(Docker.Image, Docker.Short(mine.GetProperty("image").GetString()!));

        Assert.Contains("agent-hello", Text(await Call(client, McpTools.Containers, new() { ["container"] = name })), StringComparison.Ordinal);

        Assert.Equal($"{name}: stop done", Text(await Call(client, McpTools.ContainerAction, new() { ["container"] = name, ["action"] = "stop" })));
        Assert.Equal("exited", Docker.State(name));
        Assert.Equal($"{name}: start done", Text(await Call(client, McpTools.ContainerAction, new() { ["container"] = name, ["action"] = "start" })));
        Assert.Equal("running", Docker.State(name));

        CallToolResult unknown = await Result(client, McpTools.ContainerAction, new() { ["container"] = $"{name}-not", ["action"] = "stop" });
        Assert.True(unknown.IsError == true && Text(unknown).Contains("containers lists them", StringComparison.Ordinal), $"A stop of no such container answered: {Text(unknown)}");
    }

    [Fact]
    public async Task An_agents_run_brings_a_compose_configuration_up_with_its_services_state_and_stop_takes_it_down()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string compose = Plain("mcp-compose-");
        string project = $"ptest{Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}agentrun";
        File.WriteAllText(Path.Combine(compose, "compose.yaml"), $"name: {project}\nservices:\n  ticker:\n    image: {Docker.Image}\n"
            + "    command: [\"sh\", \"-c\", \"echo ticker-up; trap 'exit 0' TERM; while true; do sleep 1; done\"]\n");
        await using McpClient client = await Start(["--mcp", compose], languageServer: false);

        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = "Compose: compose.yaml" });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"The run did not go on: {ran}");
        string status = ran.GetProperty("status").GetString()!;
        Assert.Contains($"Compose project {project}", status, StringComparison.Ordinal);
        Assert.Contains("ticker: running", status, StringComparison.Ordinal);
        Assert.Equal("running", Docker.Run("compose", "-p", project, "ps", "--format", "{{.State}}"));
        Assert.True(await Waiting.UntilAsync(async () => Text(await Call(client, McpTools.Output)).Contains("ticker-up", StringComparison.Ordinal), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken),
            "output never had the service's log");

        Assert.Equal("Stopped", Text(await Call(client, McpTools.Stop)));
        Assert.Equal(string.Empty, Docker.Run("ps", "-aq", "--filter", $"label=com.docker.compose.project={project}"));
    }

    [AvaloniaFact]
    public void An_agents_command_in_a_container_waits_for_the_person_to_allow_it()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string name = Docker.Container("exec", "sleep 600");
        string notes = Plain("mcp-ctr-");
        MainWindow window = Ui.OpenFolder(notes);
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> running = Task.Run(() => Call(client, McpTools.ContainerExec, new() { ["container"] = name, ["command"] = "echo $((6*7)) > /tmp/answer; cat /tmp/answer" }));
            Press(Admission(window, "the window to ask whether the agent may work there"), AgentAdmissionDialog.Allow);
            ConfirmDialog asked = Question(window, "the question for the agent's command in the container");
            Assert.Contains("Allow container_exec?", Texts(asked));
            Assert.False(Docker.Run("exec", name, "sh", "-c", "test -e /tmp/answer && echo there || echo absent") == "there", "The command ran before the person allowed it");
            Press(asked, "Allow");
            JsonElement ran = Await(running, "the command in the container");
            Assert.Equal(0, ran.GetProperty("exitCode").GetInt32());
            Assert.Equal("42\n", ran.GetProperty("output").GetString());
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    /// <summary>An agent's run of a configuration whose service serves HTTPS, the development certificate not trusted, asks the
    /// person nothing (their answer could keep the call waiting for good): it runs the service over HTTP, its answer and the
    /// status bar saying so. dev-certs is ContainerRunsTests' stand-in, which trusts nothing on this machine.</summary>
    [AvaloniaFact]
    public void An_agents_run_of_a_service_that_serves_HTTPS_with_no_trusted_certificate_runs_it_over_HTTP_asking_no_one()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in dev-certs is a shell script");
        string standIn = TestFolders.Create("devcerts-");
        File.WriteAllText(Path.Combine(standIn, "check"), "7");
        (string shop, string project) = ContainerRunsTests.Shop("agentcerts");
        File.WriteAllText(Path.Combine(shop, "compose.yaml"), File.ReadAllText(Path.Combine(shop, "compose.yaml"))
            .Replace("    ports: [\"8080\"]\n    depends_on", "    ports: [\"8081\"]\n    environment: { ASPNETCORE_HTTPS_PORTS: \"8081\", ASPNETCORE_HTTP_PORTS: \"\" }\n    depends_on", StringComparison.Ordinal));
        MainWindow window = ContainerRunsTests.Open(shop, DevCerts.In(standIn));
        global::Passenger.App.Run.RunConfiguration web = window.Configurations.First(c => c.Kind == global::Passenger.App.Run.RunKind.Compose).Copied() with
        {
            Name = "Web over HTTPS",
            Services = ["web"],
            Generated = false,
        };
        window.SetConfigurations([web], web.Name);
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> running = Task.Run(() => Call(client, McpTools.Run, new() { ["configuration"] = web.Name }));
            Press(Admission(window, "the window to ask whether the agent may work there"), AgentAdmissionDialog.Allow);
            Press(Question(window, "the question for the agent's run"), "Allow");
            WaitFor(() => running.IsCompleted || window.CertificateQuestion is not null, "the agent's run", TimeSpan.FromMinutes(5));
            Assert.Null(window.CertificateQuestion);
            const string Said = "web over HTTP for this run: the development certificate is not trusted, and an agent's call started this run, and asks no one";
            Assert.Contains(Said, running.GetAwaiter().GetResult().GetProperty("status").GetString(), StringComparison.Ordinal);
            Assert.Contains(Said, window.Status, StringComparison.Ordinal);
            Assert.Equal("dev-certs https --check --trust\n", File.ReadAllText(Path.Combine(standIn, "asked")));
        }
        finally
        {
            Gone(client);
            Docker.Stopped(window, project, "Stop to take web down");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_agent_lists_an_engine_without_compose_by_its_containers_labels_and_reads_a_log()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        (string cli, _) = ContainersTests.StandInEngine(TestFolders.Create("ctr-standin-"), up: true);
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: cli) };
        window.Show();
        window.OpenFolder(Plain("mcp-nocompose-"));
        Ui.Settle();
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Task<JsonElement> listing = Task.Run(() => Call(client, McpTools.Containers));
            Press(Admission(window, "the window to ask whether the agent may work there"), AgentAdmissionDialog.Allow);
            JsonElement listed = Await(listing, "the engine's containers, though its CLI has no compose to list projects");
            Assert.Equal("NoCompose", listed.GetProperty("status").GetString());
            JsonElement project = Assert.Single(listed.GetProperty("projects").EnumerateArray());
            Assert.Equal(("shop", "running(1)"), (project.GetProperty("name").GetString(), project.GetProperty("status").GetString()));
            Assert.Equal(["shop-web-1", "solo"], listed.GetProperty("containers").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
            Assert.Contains("hello from the stand-in web", Text(Await(Task.Run(() => Call(client, McpTools.Containers, new() { ["container"] = "shop-web-1" })), "the container's log")),
                StringComparison.Ordinal);
            // The start both containers' ids share names neither: an action is never taken on the one listed first.
            CallToolResult either = Await(Task.Run(() => Result(client, McpTools.Containers, new() { ["container"] = "5a5d0c0ffee" })), "the log of an id's start two share");
            Assert.True(either.IsError == true && Text(either).Contains("begins the ids of 2 containers (shop-web-1, solo)", StringComparison.Ordinal),
                $"An id's start two containers share answered: {Text(either)}");
            // A project's log is Compose's to read, so the engine's refusal is the agent's answer.
            CallToolResult merged = Await(Task.Run(() => Result(client, McpTools.Containers, new() { ["container"] = "shop" })), "the project's log");
            Assert.True(merged.IsError == true && Text(merged).Contains("'compose' is not a docker command", StringComparison.Ordinal)
                && !Text(merged).Contains(PassengerTools.Faulted, StringComparison.Ordinal), $"The project's log answered: {Text(merged)}");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Containers_a_passenger_of_this_machine_that_is_gone_left_are_removed_as_the_next_one_starts_and_another_machines_stay()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        MainWindow window = Ui.OpenFolder(Plain("mcp-left-"));
        // The labels a folder's containers carry, which also record that this state labelled something: a start then looks.
        string[] labels = [.. window.Session.Containers.Labels.Where(word => word != "--label")];
        ContainerLabels.Flush();
        string state = labels.Single(l => l.StartsWith(ContainerLabels.State, StringComparison.Ordinal));
        string machine = labels.Single(l => l.StartsWith(ContainerLabels.Machine, StringComparison.Ordinal));
        // A crashed Passenger's of this state, one of this living process, and one another machine's on a shared engine.
        string gone = $"{ContainerLabels.Process}={ContainerLabels.Of(int.MaxValue - 7, 1)}";
        string left = Docker.Container("left", "sleep 600", state, machine, gone);
        string living = Docker.Container("living", "sleep 600", labels);
        string elsewhere = Docker.Container("elsewhere", "sleep 600", state, $"{ContainerLabels.Machine}=0123456789abcdef", gone);
        McpClient client = Await(Task.Run(() => Start(["--mcp", Folder], environment: new() { [StateStore.Variable] = TestRun.Settings }, languageServer: false)), "Passenger starting");
        try
        {
            Ui.WaitFor(() => Docker.State(left) is null, $"{left}, labelled with this state and a process that is gone, removed as Passenger started",
                TimeSpan.FromSeconds(60));
            Assert.Equal("running", Docker.State(living));
            Assert.Equal("running", Docker.State(elsewhere));
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }
}
