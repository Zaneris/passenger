using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Aspire for an agent: <c>run</c>, <c>debug_start</c>, <c>debug_attach</c>, <c>profile</c> and <c>stop</c> on an
/// owned AppHost's resource, <c>telemetry</c> on its logs and traces, and an agent's start given up on or its
/// Passenger's input ended taking down what the start made. Those needing the <c>aspire</c> CLI skip without it.</summary>
[Collection(Alone.Name)]
public sealed class McpAspireTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpAspireTests(RestoredFolders folders)
        : base(folders)
    {
    }

    [Fact]
    public async Task Aspire_tools_are_discoverable_and_refuse_to_control_an_unowned_app()
    {
        await using McpClient client = await Start(["--mcp", Folder]);
        IList<McpClientTool> tools = await Exchange("Listing Aspire tools", TimeSpan.FromMinutes(1),
            cancel => client.ListToolsAsync(cancellationToken: cancel).AsTask());
        foreach (string name in new[] { McpTools.Run, McpTools.DebugStart, McpTools.DebugAttach, McpTools.Profile, McpTools.Stop, McpTools.Telemetry })
        {
            McpClientTool tool = Assert.Single(tools, tool => tool.Name == name);
            Assert.True(tool.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("resource", out _));
        }
        Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("aspire_", StringComparison.Ordinal));

        foreach (string name in new[] { McpTools.Stop, McpTools.DebugAttach, McpTools.Telemetry })
        {
            CallToolResult unowned = await Result(client, name,
                new() { ["resource"] = Path.Combine(Folder, "Sum.csproj") + "::missing" });
            Assert.True(unowned.IsError == true && Text(unowned).Contains("No owned Aspire resource", StringComparison.Ordinal),
                $"{name} accepted an unowned resource: {Text(unowned)}");
        }
        CallToolResult ordinary = await Result(client, McpTools.Run, new() { ["resource"] = "missing" });
        Assert.True(ordinary.IsError == true, $"An ordinary configuration accepted an Aspire target: {Text(ordinary)}");
        Assert.Equal("Stopped", (await Call(client, McpTools.Stop)).GetString());
        JsonElement workspace = await Call(client, McpTools.Workspace);
        Assert.Equal(PathNames.Canonical(Folder), workspace.GetProperty("folder").GetString());
        Assert.Empty(workspace.GetProperty("aspireApps").EnumerateArray());
    }

    /// <summary>An agent's run of an AppHost whose launch profile serves HTTPS, the development certificate not trusted, asks
    /// the person nothing (their answer could keep the call waiting for good): it runs over HTTP, its answer and the status
    /// bar saying so. The AppHost is AspireStandInTests' stand-in, which needs no Aspire, and dev-certs
    /// <see cref="DevCerts"/>' stand-in, which trusts nothing on this machine.</summary>
    [AvaloniaFact]
    public void An_agents_run_of_an_AppHost_that_serves_HTTPS_with_no_trusted_certificate_runs_it_over_HTTP_asking_no_one()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in dev-certs is a shell script");
        string standIn = TestFolders.Create("devcerts-");
        File.WriteAllText(Path.Combine(standIn, "check"), "7");
        using StandInAppHost project = new();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend, "https://localhost:7312");
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, DevCertificates = DevCerts.In(standIn) };
        window.Show();
        window.OpenFolder(project.Folder);
        Ui.Settle();
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration), "the stand-in AppHost's Aspire configuration");
            Task<JsonElement> running = Task.Run(() => Call(client, McpTools.Run, new() { ["configuration"] = StandInAppHost.Configuration, ["waitSeconds"] = 0 }));
            Press(Admission(window, "the window to ask whether the agent may work there"), AgentAdmissionDialog.Allow);
            Press(Question(window, "the question for the agent's run"), "Allow");
            WaitFor(() => running.IsCompleted || window.CertificateQuestion is not null, "the agent's run", TimeSpan.FromMinutes(2));
            Assert.Null(window.CertificateQuestion);
            const string Said = "StandIn.AppHost over HTTP for this run: the development certificate is not trusted, and an agent's call started this run, and asks no one";
            Assert.Contains(Said, running.GetAwaiter().GetResult().GetProperty("status").GetString(), StringComparison.Ordinal);
            Assert.Contains(Said, window.Status, StringComparison.Ordinal);
            Assert.Equal("dev-certs https --check --trust\n", DevCerts.Asked(standIn));
            Assert.Equal("http://localhost:7312", File.ReadAllText(project.UrlsFile).Split('\n')[0]);
        }
        finally
        {
            Gone(client);
            window.Close();
            Await(Task.Run(async () =>
            {
                await backend.DisposeAsync();
                return true;
            }), "the stand-in resource service to stop");
        }
    }

    /// <summary>An agent's workspace says a service its run configuration says not to start is Not started, never
    /// FailedToStart. The AppHost is AspireStandInTests' stand-in, its service's start asked of the IDE as DCP asks it.</summary>
    [AvaloniaFact]
    public void An_agents_workspace_says_a_service_marked_Dont_start_is_Not_started()
    {
        using StandInAppHost project = new();
        project.BuildApi();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend);
        MainWindow window = AgentsWindow(project.Folder, languageServer: false);
        McpClient? client = null;
        try
        {
            WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration), "the stand-in AppHost's Aspire configuration");
            RunConfiguration marked = project.DontStartApi(window.Configurations.First(configuration => configuration.Name == StandInAppHost.Configuration));
            window.SetConfigurations([marked], marked.Name);
            Ui.Run(window, AppActions.RunProgram);
            WaitFor(() => backend.Connected.Task.IsCompleted && window.AspireRuntime.Apps.Count == 1, "the stand-in AppHost's start");
            (System.Net.WebSockets.ClientWebSocket notified, HttpStatusCode answer) = project.AskToStartApi("apiservice");
            using System.Net.WebSockets.ClientWebSocket subscribed = notified;
            Assert.False((int)answer is >= 200 and < 300, "The IDE started a service its configuration says not to start");
            backend.PushResource(project.Refused());
            WaitFor(() => window.Aspire.ResourceSnapshots.Any(resource => resource.Name == "apiservice" && resource.State == "FailedToStart"), "DCP's word of the refused start");
            client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
            Task<JsonElement> asked = Task.Run(() => Call(client, McpTools.Workspace));
            WaitFor(() => asked.IsCompleted, "the agent's workspace");
            JsonElement api = Assert.Single(Assert.Single(asked.GetAwaiter().GetResult().GetProperty("aspireApps").EnumerateArray())
                .GetProperty("resources").EnumerateArray(), resource => resource.GetProperty("name").GetString() == "apiservice");
            Assert.Equal(Passenger.App.Aspire.AspireAppRun.NotStarted, api.GetProperty("state").GetString());
        }
        finally
        {
            if (client is not null)
            {
                Gone(client);
            }
            window.Close();
            Await(Task.Run(async () =>
            {
                await backend.DisposeAsync();
                return true;
            }), "the stand-in resource service to stop");
        }
    }

    [Fact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public async Task An_agent_must_qualify_duplicate_official_starter_resource_names_with_the_owned_AppHost()
    {
        using AspireApp project = new();
        await using McpClient client = await Start(["--mcp", project.Folder]);
        string first = $"Aspire: {AspireApp.Name}.AppHost: http";
        string second = $"Aspire: {AspireApp.CompanionName}.AppHost: http";
        Assert.True(await Waiting.UntilAsync(async () =>
        {
            string?[] names = (await Call(client, McpTools.Workspace)).GetProperty("runConfigurations").EnumerateArray().Select(item => item.GetString()).ToArray();
            return names.Contains(first) && names.Contains(second);
        }, TimeSpan.FromMinutes(2), Cancel), $"Both official AppHosts were not discovered.{Said()}");
        await Call(client, McpTools.Run, new() { ["configuration"] = first, ["resource"] = project.Host + "::apiservice" });
        await Call(client, McpTools.Run, new() { ["configuration"] = second, ["resource"] = project.CompanionHost + "::apiservice" });
        JsonElement[] apps = [];
        Assert.True(await Waiting.UntilAsync(async () =>
        {
            apps = (await Call(client, McpTools.Workspace)).GetProperty("aspireApps").EnumerateArray().ToArray();
            return apps.Length == 2 && apps.All(app => app.GetProperty("resources").EnumerateArray().Count(resource =>
                resource.GetProperty("displayName").GetString() == "apiservice" && resource.GetProperty("state").GetString() == "Running") == 1);
        }, TimeSpan.FromMinutes(1), Cancel), $"Both owned API resources did not reach Running.{Said()}");
        Assert.Equal(2, apps.Length);
        JsonElement[] resources = apps.Select(app => Assert.Single(app.GetProperty("resources").EnumerateArray(), resource => resource.GetProperty("displayName").GetString() == "apiservice"
            && resource.GetProperty("state").GetString() == "Running")).ToArray();
        Assert.Equal(resources[0].GetProperty("displayName").GetString(), resources[1].GetProperty("displayName").GetString());
        Assert.NotEqual(resources[0].GetProperty("target").GetString(), resources[1].GetProperty("target").GetString());
        foreach (string tool in new[] { McpTools.Stop, McpTools.DebugAttach, McpTools.Telemetry })
        {
            CallToolResult refused = await Result(client, tool, new() { ["resource"] = "apiservice" });
            Assert.True(refused.IsError == true && Text(refused).Contains("ambiguous", StringComparison.Ordinal),
                $"{tool} accepted an ambiguous target: {Text(refused)}");
        }
        foreach (JsonElement app in apps)
        {
            JsonElement resource = Assert.Single(app.GetProperty("resources").EnumerateArray(), item => item.GetProperty("displayName").GetString() == "apiservice"
                && item.GetProperty("state").GetString() == "Running");
            string target = resource.GetProperty("target").GetString()!;
            Assert.Equal(app.GetProperty("appHostPath").GetString() + "::" + resource.GetProperty("name").GetString(), target);
            if (resource.GetProperty("name").GetString() != "apiservice")
            {
                CallToolResult refused = await Result(client, McpTools.Telemetry, new() { ["resource"] = app.GetProperty("appHostPath").GetString() + "::apiservice" });
                Assert.True(refused.IsError == true, "A running qualified target accepted the cold declaration alias");
            }
            JsonElement logs = await Call(client, McpTools.Telemetry, new() { ["resource"] = target, ["limit"] = 1 });
            Assert.InRange(logs.GetProperty("logs").GetArrayLength(), 0, 1);
        }
    }

    [AvaloniaFact]
    public void Canceling_an_agents_Aspire_start_ends_the_starting_process_and_its_execution_listener()
    {
        Write("Sum.csproj", TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
        string pidFile = Path.Combine(Folder, "aspire-starting.pid");
        string endpointFile = Path.Combine(Folder, "aspire-starting.endpoint");
        Write("Program.cs", $"File.WriteAllText({JsonSerializer.Serialize(endpointFile)}, Environment.GetEnvironmentVariable(\"DEBUG_SESSION_PORT\"));\nFile.WriteAllText({JsonSerializer.Serialize(pidFile)}, Environment.ProcessId.ToString());\nConsole.WriteLine(\"starting\");\nawait Task.Delay(Timeout.Infinite);\n");
        MainWindow window = AgentsWindow(Folder);
        window.SetConfigurations([new RunConfiguration
        {
            Name = "Pending Aspire",
            Project = PathNames.Canonical(Path.Combine(Folder, "Sum.csproj")),
            Aspire = true,
        }], "Pending Aspire");
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the Aspire agent attaching");
        Process? started = null;
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Aspire), "the explicit Aspire run configuration");
            string configuration = window.Configurations.First(candidate => candidate.Aspire).Name;
            using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            Task<CallToolResult> starting = Task.Run(() => CallGivingUp(client, McpTools.Run,
                new() { ["configuration"] = configuration }, giveUp.Token));
            Window permission = Question(window, "permission for the Aspire startup");
            // Not settled: the start the answer lets go holds background work until it is given up.
            Ui.Click(permission.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Allow")), settle: false);
            Ui.PumpUntil(() => File.Exists(pidFile) || starting.IsCompleted, "the starting AppHost process", TimeSpan.FromMinutes(5));
            Assert.True(File.Exists(pidFile), $"The AppHost process did not start: {(starting.IsCompletedSuccessfully ? Text(starting.Result) : window.Status)}{Said()}");
            int processId = Processes.WrittenId(pidFile, "the starting AppHost PID");
            started = Process.GetProcessById(processId);
            ProcessRunner runner = Assert.Single(window.AspireRuntime.Apps).Runner;
            Ui.PumpUntil(() => runner.Shown().ToString().Contains("starting", StringComparison.Ordinal), "the AppHost's startup console marker");
            int port = new Uri("http://" + File.ReadAllText(endpointFile)).Port;
            Task<string?> reading = Task.Run(async () => (await Call(client, McpTools.Output)).GetString());
            Ui.PumpUntil(() => reading.IsCompleted, "reading the starting AppHost's console");
            Assert.Contains("starting", reading.GetAwaiter().GetResult(), StringComparison.Ordinal);

            giveUp.Cancel();
            Ui.PumpUntil(() => starting.IsCompleted && window.AspireRuntime.Apps.Count == 0 && started.HasExited,
                "canceling the start to roll back the AppHost and listener", TimeSpan.FromMinutes(1));
            Assert.ThrowsAny<OperationCanceledException>(() => starting.GetAwaiter().GetResult());
            Assert.False(Await(Task.Run(async () =>
            {
                using TcpClient socket = new();
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port, Cancel);
                    return true;
                }
                catch (SocketException)
                {
                    return false;
                }
            }), "checking the canceled execution listener"), "The canceled Aspire startup left its DCP listener running");
            Assert.False(window.IsRunning, "The canceled Aspire startup remained a running IDE configuration");
        }
        finally
        {
            Gone(client);
            window.Close();
            started?.Dispose();
        }
    }

    [Fact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public async Task Ending_a_windowless_Passengers_input_stops_the_official_starter_tree_and_Dcp_listener()
    {
        string starter = TestFolders.Create("mcp-aspire-shutdown-");
        DeleteAtEnd(starter);
        string hostPid = Path.Combine(starter, "apphost.pid");
        string apiPid = Path.Combine(starter, "api.pid");
        string webPid = Path.Combine(starter, "web.pid");
        string endpointFile = Path.Combine(starter, "execution-endpoint.txt");
        TestFolders.Prepare(() =>
        {
            AspireApp.Create(starter, AspireApp.Name);
            RecordPid("AppHost", "AppHost.cs", hostPid);
            RecordPid("ApiService", "Program.cs", apiPid);
            RecordPid("Web", "Program.cs", webPid);
            string host = Path.Combine(starter, AspireApp.Name + ".AppHost", "AppHost.cs");
            string text = File.ReadAllText(host);
            File.WriteAllText(host, $"File.WriteAllText({JsonSerializer.Serialize(endpointFile)}, Environment.GetEnvironmentVariable(\"DEBUG_SESSION_PORT\") ?? string.Empty);\n" + text);
            // By its real path, as the AppHost names its projects: two spellings of one project restored at once collide.
            Restore.Run(PathNames.Canonical(starter));
        }, starter);

        await using McpClient client = await Start(["--mcp", starter], environment: new()
        {
            ["DEBUG_SESSION_SERVER_CERTIFICATE"] = "not-a-certificate",
        });
        string configuration = $"Aspire: {AspireApp.Name}.AppHost: http";
        Assert.True(await Waiting.UntilAsync(async () =>
            (await Call(client, McpTools.Workspace)).GetProperty("runConfigurations").EnumerateArray().Any(item => item.GetString() == configuration),
            TimeSpan.FromMinutes(2), Cancel), $"The official starter's HTTP configuration was not discovered.{Said()}");
        // The run answers once the resource is up: no waiting after it.
        string up = (await Call(client, McpTools.Run, new() { ["configuration"] = configuration, ["resource"] = "apiservice" })).GetProperty("status").GetString()!;
        Assert.True(up.StartsWith("apiservice", StringComparison.Ordinal) && up.Contains(" is up\n", StringComparison.Ordinal),
            $"The agent's run answered before its resource was up: {up}");
        Assert.Contains((await Call(client, McpTools.Workspace)).GetProperty("aspireApps")[0].GetProperty("resources").EnumerateArray(),
            resource => resource.GetProperty("displayName").GetString() == "apiservice" && resource.GetProperty("state").GetString() == "Running");
        JsonElement app = default;
        Assert.True(await Waiting.UntilAsync(async () =>
        {
            app = Assert.Single((await Call(client, McpTools.Workspace)).GetProperty("aspireApps").EnumerateArray());
            return new[] { "apiservice", "webfrontend" }.All(name => app.GetProperty("resources").EnumerateArray().Count(resource =>
                resource.GetProperty("displayName").GetString() == name && resource.GetProperty("state").GetString() == "Running") == 1);
        }, TimeSpan.FromMinutes(1), Cancel), $"The official starter services did not reach Running.{Said()}");
        int hostId = await Processes.WrittenIdAsync(hostPid, "the real AppHost PID");
        int apiId = await Processes.WrittenIdAsync(apiPid, "the real API PID");
        int webId = await Processes.WrittenIdAsync(webPid, "the real frontend PID");
        Assert.Equal(hostId, app.GetProperty("processId").GetInt32());
        Assert.All(new[] { hostId, apiId, webId }, id => Assert.True(Processes.IsRunning(id), $"The official starter process {id} never remained alive"));
        string hostProject = PathNames.Canonical(Path.Combine(starter, AspireApp.Name + ".AppHost", AspireApp.Name + ".AppHost.csproj"));
        JsonElement api = Assert.Single(app.GetProperty("resources").EnumerateArray(), resource => resource.GetProperty("displayName").GetString() == "apiservice"
            && resource.GetProperty("state").GetString() == "Running");
        string target = api.GetProperty("target").GetString()!;
        Assert.Equal(hostProject + "::" + api.GetProperty("name").GetString(), target);
        Assert.Contains(app.GetProperty("resources").EnumerateArray(), resource => resource.GetProperty("displayName").GetString() == "webfrontend"
            && resource.GetProperty("state").GetString() == "Running");

        foreach (string missing in new[] { "not-a-resource", hostProject + "::not-a-resource" })
        {
            ModelContextProtocol.Protocol.CallToolResult refused = await Result(client, McpTools.Stop, new() { ["resource"] = missing });
            Assert.True(refused.IsError == true && Text(refused).Contains("No owned Aspire resource", StringComparison.Ordinal));
        }
        JsonElement attached = await Call(client, McpTools.DebugAttach, new() { ["resource"] = target, ["waitSeconds"] = 1 });
        Assert.True(attached.GetProperty("state").GetString() is "running" or "paused", attached.ToString());
        Assert.Equal(apiId, await Processes.WrittenIdAsync(apiPid, "the attached API PID"));
        Assert.True(Processes.IsRunning(hostId) && Processes.IsRunning(webId));

        JsonElement rerun = await Call(client, McpTools.Run, new() { ["resource"] = target });
        Assert.True(rerun.GetProperty("stillRunning").GetBoolean() && rerun.GetProperty("status").GetString()!.Contains(" is up\n", StringComparison.Ordinal),
            $"Run Without Debugger answered before the API was up again: {rerun}");
        await Until(() => !Processes.IsRunning(apiId) && ReadPid(apiPid) != apiId, "Run Without Debugger to restart only the API");
        apiId = await Processes.WrittenIdAsync(apiPid, "the run API PID");
        Assert.True(Processes.IsRunning(hostId) && Processes.IsRunning(webId));
        JsonElement restarted = await Call(client, McpTools.DebugStart, new() { ["resource"] = target, ["waitSeconds"] = 1 });
        Assert.True(restarted.GetProperty("state").GetString() is "running" or "paused", restarted.ToString());
        await Until(() => !Processes.IsRunning(apiId) && ReadPid(apiPid) != apiId, "Debug to restart only the API");
        apiId = await Processes.WrittenIdAsync(apiPid, "the debug API PID");
        Assert.True(Processes.IsRunning(hostId) && Processes.IsRunning(webId));

        JsonElement profile = await Call(client, McpTools.Profile, new() { ["resource"] = target, ["seconds"] = 1 });
        Assert.True(profile.GetProperty("durationMs").GetDouble() > 0, profile.ToString());
        Assert.Equal(apiId, await Processes.WrittenIdAsync(apiPid, "the profiled API PID"));
        Assert.True(Processes.IsRunning(hostId) && Processes.IsRunning(webId));

        api = Assert.Single(Assert.Single((await Call(client, McpTools.Workspace)).GetProperty("aspireApps").EnumerateArray())
            .GetProperty("resources").EnumerateArray(), resource => resource.GetProperty("displayName").GetString() == "apiservice");
        Uri apiUrl = new(Assert.Single(api.GetProperty("urls").EnumerateArray(), url => url.GetProperty("url").GetString()!.StartsWith("http:", StringComparison.Ordinal))
            .GetProperty("url").GetString()!);
        using (HttpClient http = new())
        {
            await http.GetStringAsync(new Uri(apiUrl, "/weatherforecast"), Cancel);
        }
        Assert.True(await Waiting.UntilAsync(async () =>
        {
            JsonElement logs = await Call(client, McpTools.Telemetry, new() { ["resource"] = target, ["limit"] = 1 });
            Assert.InRange(logs.GetProperty("logs").GetArrayLength(), 0, 1);
            return logs.GetProperty("logs").GetArrayLength() == 1;
        }, TimeSpan.FromMinutes(1), Cancel), "Native HTTP telemetry did not return a bounded log snapshot");
        JsonElement traces = await Call(client, McpTools.Telemetry, new() { ["resource"] = target, ["kind"] = "traces", ["limit"] = 1 });
        Assert.InRange(traces.GetProperty("traces").GetArrayLength(), 0, 1);

        await Call(client, McpTools.Stop, new() { ["resource"] = target });
        await Until(() => !Processes.IsRunning(apiId), "resource Stop to end only the API");
        Assert.True(Processes.IsRunning(hostId) && Processes.IsRunning(webId));
        await Call(client, McpTools.Run, new() { ["resource"] = target });
        await Until(() => ReadPid(apiPid) != apiId, "the API to restart before testing EOF cleanup");
        apiId = await Processes.WrittenIdAsync(apiPid, "the restarted API PID");
        await Until(() => File.Exists(endpointFile) && new FileInfo(endpointFile).Length > 0, "the owned DCP endpoint written by the real AppHost");
        string address = File.ReadAllText(endpointFile).Trim();
        Assert.StartsWith("localhost:", address, StringComparison.Ordinal);
        int port = int.Parse(address["localhost:".Length..], CultureInfo.InvariantCulture);
        using (TcpClient listener = new())
        {
            await listener.ConnectAsync(IPAddress.Loopback, port, Cancel);
        }
        Process passenger = LastPassenger();
        int[] owned = [.. Processes.Descendants(passenger.Id), hostId, apiId, webId];
        Assert.Contains(hostId, Processes.Descendants(passenger.Id));
        EndInput(passenger);
        await Until(() => passenger.HasExited && owned.All(id => !Processes.IsRunning(id)),
            "EOF to end windowless Passenger, both services, AppHost, DCP, dashboard and the remaining owned child tree without a dispatcher pump");
        using TcpClient closed = new();
        await Assert.ThrowsAsync<SocketException>(async () => await closed.ConnectAsync(IPAddress.Loopback, port, Cancel));

        void RecordPid(string project, string source, string marker)
        {
            string path = Path.Combine(starter, AspireApp.Name + "." + project, source);
            string text = File.ReadAllText(path);
            string statement = $"File.WriteAllText({JsonSerializer.Serialize(marker)}, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));\n";
            if (text.StartsWith("using ", StringComparison.Ordinal))
            {
                int builder = text.IndexOf("var builder =", StringComparison.Ordinal);
                Assert.True(builder >= 0, $"The official {project} source has no application builder");
                text = text.Insert(builder, statement);
            }
            else
            {
                text = statement + text;
            }
            File.WriteAllText(path, text);
        }

        static int ReadPid(string path) => File.Exists(path) && int.TryParse(File.ReadAllText(path), CultureInfo.InvariantCulture, out int id) ? id : 0;
    }

    /// <summary>An AppHost's session container, which its orchestrator makes with none of Passenger's labels, is known as it
    /// is made though no Aspire window shows it, so when that Passenger ends as a crash ends it (it, its watchdog, its
    /// children and the orchestrator killed at once), the next Passenger of its state removes the container as it starts,
    /// and leaves the Persistent one, which is the person's to keep.</summary>
    [Fact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public async Task An_AppHosts_session_container_a_crashed_Passenger_left_is_removed_as_the_next_one_starts_and_a_persistent_one_stays()
    {
        Assert.SkipWhen(AspireContainersTests.WhyNot is not null, AspireContainersTests.WhyNot ?? string.Empty);
        string starter = TestFolders.Create("mcp-aspire-crash-");
        DeleteAtEnd(starter);
        TestFolders.Prepare(() =>
        {
            AspireApp.Create(starter, AspireApp.Name);
            AspireContainersTests.WithContainers(Path.Combine(starter, AspireApp.Name + ".AppHost"), (path, text) =>
            {
                File.WriteAllText(path, text);
                return path;
            });
            Restore.Run(PathNames.Canonical(starter));
        }, starter);
        try
        {
            McpClient client = await Start(["--mcp", starter], languageServer: false);
            string configuration = $"Aspire: {AspireApp.Name}.AppHost: http";
            Assert.True(await Waiting.UntilAsync(async () =>
                (await Call(client, McpTools.Workspace)).GetProperty("runConfigurations").EnumerateArray().Any(item => item.GetString() == configuration),
                TimeSpan.FromMinutes(2), Cancel), $"The starter's HTTP configuration was not discovered.{Said()}");
            await Call(client, McpTools.Run, new() { ["configuration"] = configuration });
            string session = await AspireContainersTests.RunningAsync(AspireContainersTests.Session);
            string kept = await AspireContainersTests.RunningAsync(AspireContainersTests.Kept);
            // What the next start reads of the containers this one made.
            string record = Path.Combine(OwnState, "aspire-containers", "ownership.json");
            await Until(() => File.Exists(record) && Ui.Shared(record).Contains(session, StringComparison.Ordinal), "the session container recorded as it was made");
            Assert.DoesNotContain(kept, Ui.Shared(record), StringComparison.Ordinal);

            Process passenger = LastPassenger();
            int host = Assert.Single((await Call(client, McpTools.Workspace)).GetProperty("aspireApps").EnumerateArray()).GetProperty("processId").GetInt32();
            int[] orchestrator = [.. Processes.Listed().Where(process => process.Value.Contains("dcp start-apiserver", StringComparison.Ordinal)
                && process.Value.Contains($"--monitor {host.ToString(CultureInfo.InvariantCulture)} ", StringComparison.Ordinal)).Select(process => process.Key)];
            Assert.NotEmpty(orchestrator);
            // The orchestrator first, which would take its containers down as its AppHost goes, and Passenger last.
            int[] killed = [.. orchestrator, .. orchestrator.SelectMany(Processes.Descendants), .. Processes.Descendants(passenger.Id), passenger.Id];
            foreach (int id in killed.Distinct())
            {
                try
                {
                    using Process process = Process.GetProcessById(id);
                    process.Kill();
                }
                catch (Exception gone) when (gone is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
            await Until(() => killed.All(id => !Processes.IsRunning(id)), "Passenger, its children and the orchestrator killed");
            Assert.Equal("running", Docker.State(session));

            await using McpClient next = await Start(["--mcp", starter], languageServer: false);
            await Until(() => Docker.State(session) is null, "the next Passenger to remove the session container the crashed one's AppHost left");
            Assert.Equal("running", Docker.State(kept));
        }
        finally
        {
            AspireContainersTests.RemoveMade();
        }
    }

    /// <summary>On macOS and Linux a Passenger killed alone leaves its watchdog, whose last command removes the AppHost's
    /// session container Passenger recorded, though it carries none of Passenger's labels, and leaves the Persistent one. The
    /// orchestrator is held stopped meanwhile, so the removal is the watchdog's, not the orchestrator's.</summary>
    [Fact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public async Task An_AppHosts_session_container_goes_with_a_Passenger_killed_alone_by_its_watchdogs_last_command_and_a_persistent_one_stays()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no watchdog: the job object ends Passenger's children, and the next start removes its containers");
        Assert.SkipWhen(AspireContainersTests.WhyNot is not null, AspireContainersTests.WhyNot ?? string.Empty);
        string starter = TestFolders.Create("mcp-aspire-watchdog-");
        DeleteAtEnd(starter);
        TestFolders.Prepare(() =>
        {
            AspireApp.Create(starter, AspireApp.Name);
            AspireContainersTests.WithContainers(Path.Combine(starter, AspireApp.Name + ".AppHost"), (path, text) =>
            {
                File.WriteAllText(path, text);
                return path;
            });
            Restore.Run(PathNames.Canonical(starter));
        }, starter);
        int[] held = [];
        try
        {
            McpClient client = await Start(["--mcp", starter], languageServer: false);
            string configuration = $"Aspire: {AspireApp.Name}.AppHost: http";
            Assert.True(await Waiting.UntilAsync(async () =>
                (await Call(client, McpTools.Workspace)).GetProperty("runConfigurations").EnumerateArray().Any(item => item.GetString() == configuration),
                TimeSpan.FromMinutes(2), Cancel), $"The starter's HTTP configuration was not discovered.{Said()}");
            await Call(client, McpTools.Run, new() { ["configuration"] = configuration });
            string session = await AspireContainersTests.RunningAsync(AspireContainersTests.Session);
            string kept = await AspireContainersTests.RunningAsync(AspireContainersTests.Kept);
            string record = Path.Combine(OwnState, "aspire-containers", "ownership.json");
            await Until(() => File.Exists(record) && Ui.Shared(record).Contains(session, StringComparison.Ordinal), "the session container recorded as it was made");

            Process passenger = LastPassenger();
            int host = Assert.Single((await Call(client, McpTools.Workspace)).GetProperty("aspireApps").EnumerateArray()).GetProperty("processId").GetInt32();
            int[] orchestrator = [.. Processes.Listed().Where(process => process.Value.Contains("dcp start-apiserver", StringComparison.Ordinal)
                && process.Value.Contains($"--monitor {host.ToString(CultureInfo.InvariantCulture)} ", StringComparison.Ordinal)).Select(process => process.Key)];
            Assert.NotEmpty(orchestrator);
            held = [.. orchestrator.Concat(orchestrator.SelectMany(Processes.Descendants)).Distinct()];
            foreach (int id in held)
            {
                Processes.Suspend(id, "The orchestrator");
            }
            int[] children = [.. Processes.Descendants(passenger.Id)];
            Assert.Contains(host, children);
            passenger.Kill();
            await Until(() => !Processes.IsRunning(passenger.Id), "Passenger killed alone");
            await Until(() => !Processes.IsRunning(host), "the watchdog to end the AppHost its Passenger started");
            await Until(() => Docker.State(session) is null, "the watchdog's last command to remove the session container the AppHost's orchestrator made");
            Assert.Equal("running", Docker.State(kept));
        }
        finally
        {
            foreach (int id in held)
            {
                Processes.Kill(id);
            }
            AspireContainersTests.RemoveMade();
        }
    }
}
