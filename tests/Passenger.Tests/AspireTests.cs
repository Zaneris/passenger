using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Grpc.Core;
using Grpc.Net.Client;
using Passenger.App.Editors;
using Passenger.App;
using Passenger.App.Aspire;
using Passenger.App.Aspire.Protocol;
using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Aspire's official starters run through the window. The class shares one restored pair of them (<see cref="AspireApp"/>),
/// which already holds what its tests ask of it; what reads what a running starter shows shares one run of it, and a test that
/// changes the app (Hot Reload, Rerun, a restart, a debugged or profiled start) runs its own. A window starts no C# language
/// server unless its test reads C# (the gutter's marks, Edit Configurations' services, Hot Reload).</summary>
[Collection(Alone.Name)]
public sealed class AspireTests : IClassFixture<AspireApp>, IDisposable
{
    private readonly AspireApp project;

    public AspireTests(AspireApp project)
    {
        this.project = project;
        if (project.WhyNot is null)
        {
            project.Reset();
        }
    }

    [AvaloniaFact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public void Run_with_Hot_Reload_applies_to_both_quiet_sessions_and_a_rude_edit_restarts_only_its_service()
    {
        MainWindow window = project.OpenHost();
        try
        {
            Choose(window);
            window.ApplySettings(window.Settings with { HotReload = true, RestartOnRudeEdit = true });
            Ui.Run(window, AppActions.RunProgram);
            WaitNativeServices(window);
            Ui.WaitFor(() => window.Debugger.Running.Count == 2, "both quiet native Hot Reload sessions");
            AspireAppRun host = Assert.Single(window.AspireRuntime.Apps);
            int hostPid = host.ProcessId!.Value;
            int apiPid = Service(window, Resource(window, "apiservice")).ProcessId!.Value;
            int webPid = Service(window, Resource(window, "webfrontend")).ProcessId!.Value;
            Assert.Empty(window.Debugger.Tabs);
            Assert.All(host.Services, service => Assert.Null(service.DebugTab));
            Assert.DoesNotContain(Processes.Descendants(hostPid).Select(Processes.Describe),
                command => command.Contains("dotnet watch", StringComparison.OrdinalIgnoreCase)
                    || command.Contains("dotnet-watch", StringComparison.OrdinalIgnoreCase));

            EditSource(window, project.ApiProgram, text => text.Replace("=> 1 + count", "=> 2 + count", StringComparison.Ordinal));
            EditSource(window, project.WebProgram, text => text.Replace("=> 1 + count", "=> 3 + count", StringComparison.Ordinal));
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            using HttpClient http = Client();
            Assert.Equal("2", Ui.Await(http.GetStringAsync(new Uri(HttpUrl(Resource(window, "apiservice")), "/passenger-edit")), "the API's changed method"));
            Assert.Equal("3", Ui.Await(http.GetStringAsync(new Uri(HttpUrl(Resource(window, "webfrontend")), "/passenger-edit")), "the frontend's changed method"));
            Assert.Equal(apiPid, Service(window, Resource(window, "apiservice")).ProcessId);
            Assert.Equal(webPid, Service(window, Resource(window, "webfrontend")).ProcessId);

            string targets = string.Join("; ", window.Debugger.Running.Select(run =>
            {
                HotReloadStateResponse state = Ui.Await(run.Session.HotReloadStateAsync(), $"the modules loaded by {run.Name}");
                return $"{run.Name}: [{string.Join(", ", state.Modules.Where(module => module.Editable).Select(module => $"{module.Name}, mvid={module.Mvid}, path={module.Path}"))}]";
            }));
            EditSource(window, project.ApiProgram, text => text.Replace("static int count", "static long count", StringComparison.Ordinal));
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            FindAction(window, "Show Aspire Resources");
            try
            {
                Ui.WaitFor(() => window.Aspire.ResourceSnapshots.FirstOrDefault(resource => resource.DisplayName == "apiservice") is { } api
                    && window.AspireRuntime.FindService(new(project.Host, api))?.ProcessId is int current && current != apiPid,
                    "only the rude-edited API to restart", TimeSpan.FromMinutes(3));
            }
            catch (TimeoutException error)
            {
                window.AspireRuntime.LastAppConsole?.Flush();
                window.BottomPanel.Build.Flush();
                HotReloadOutcome? outcome = window.Debugger.LastApplied;
                string problems = string.Join("; ", outcome?.Problems.Select(problem =>
                    $"{problem.Code}: project={problem.Project}, path={problem.Path}, position={problem.Position}, {problem.Message}") ?? []);
                string quiet = string.Join("; ", window.Debugger.Running.Select(run => $"{run.Name}: {run.Session.State}"));
                string owned = string.Join("; ", window.AspireRuntime.Apps.Select(app =>
                    $"AppHost pid={app.ProcessId}, running={app.Runner.IsRunning}: " + string.Join("; ", app.Services.Select(service =>
                        $"{service.ResourceName}, plan={service.Plan.Name}, pid={service.ProcessId}, running={service.ProcessId is int servicePid && Processes.IsRunning(servicePid)}"))));
                string resources = string.Join("; ", window.Aspire.ResourceSnapshots.Select(resource =>
                    $"{resource.Name}/{resource.DisplayName}: {resource.State}, commands=[{string.Join(", ", resource.Commands.Select(command => $"{command.Name}: enabled={command.Enabled}"))}]"));
                string build = window.BottomPanel.Build.Document.Snapshot.ToString();
                string hostOutput = window.AspireRuntime.LastAppConsole?.Document.Snapshot.ToString() ?? "No AppHost console";
                string diagnostic = $"Status: {window.Status}\nRestartOnRudeEdit: {window.Settings.RestartOnRudeEdit}\n"
                    + $"Outcome: {outcome?.Result}, {outcome?.Said}; restart=[{string.Join(", ", outcome?.Restart ?? [])}]\n"
                    + $"Problems: {problems}\nFiles: {string.Join("; ", outcome?.Files ?? [])}\nModules: {string.Join("; ", outcome?.Modules ?? [])}\n"
                    + $"Targets before rude edit: {targets}\n"
                    + $"Quiet: {quiet}\nOwned: {owned}\nResources: {resources}\n"
                    + $"Build:\n{build[^Math.Min(build.Length, 4096)..]}\nAppHost:\n{hostOutput[^Math.Min(hostOutput.Length, 8192)..]}";
                diagnostic = Passenger.App.Containers.ContainerSecrets.InValue(diagnostic);
                diagnostic = Regex.Replace(diagnostic, @"\b[0-9a-fA-F]{64}\b", Passenger.App.Containers.ContainerSecrets.Masked);
                diagnostic = Regex.Replace(diagnostic, @"(https?://[^\s?]+)\?[^\s]+", "$1?[redacted]", RegexOptions.IgnoreCase);
                throw new TimeoutException($"{error.Message}\n{diagnostic[..Math.Min(diagnostic.Length, 16384)]}", error);
            }
            Ui.WaitFor(() => !Processes.IsRunning(apiPid), "the old API to exit");
            Assert.Equal(hostPid, Assert.Single(window.AspireRuntime.Apps).ProcessId);
            Assert.Equal(webPid, Service(window, Resource(window, "webfrontend")).ProcessId);
            Assert.True(Processes.IsRunning(hostPid) && Processes.IsRunning(webPid));
            Assert.Empty(window.Debugger.Tabs);
            Assert.Equal(2, window.Debugger.Running.Count);
            Assert.Equal("2", Ui.Await(http.GetStringAsync(new Uri(HttpUrl(Resource(window, "apiservice")), "/passenger-edit")), "the restarted API's method"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public void Rerun_keeps_unchanged_processes_and_restarts_only_the_edited_project()
    {
        MainWindow window = OpenRunning();
        try
        {
            int hostPid = Assert.Single(window.AspireRuntime.Apps).ProcessId!.Value;
            int apiPid = Service(window, Resource(window, "apiservice")).ProcessId!.Value;
            int webPid = Service(window, Resource(window, "webfrontend")).ProcessId!.Value;
            Ui.Run(window, AppActions.Rerun);
            Assert.Equal(hostPid, Assert.Single(window.AspireRuntime.Apps).ProcessId);
            Assert.Equal(apiPid, Service(window, Resource(window, "apiservice")).ProcessId);
            Assert.Equal(webPid, Service(window, Resource(window, "webfrontend")).ProcessId);

            // Written on disk, as another tool would: Rerun reads what changed since the build each service runs began.
            File.WriteAllText(project.ApiProgram, File.ReadAllText(project.ApiProgram).Replace("=> 1 + count", "=> 4 + count", StringComparison.Ordinal));
            Ui.Run(window, AppActions.Rerun);
            Ui.WaitFor(() => window.Aspire.ResourceSnapshots.FirstOrDefault(resource => resource.DisplayName == "apiservice") is { } api
                && window.AspireRuntime.FindService(new(project.Host, api))?.ProcessId is int current && current != apiPid,
                "Rerun to replace the changed API", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => !Processes.IsRunning(apiPid), "Rerun to end the old API");
            Assert.Equal(hostPid, Assert.Single(window.AspireRuntime.Apps).ProcessId);
            Assert.Equal(webPid, Service(window, Resource(window, "webfrontend")).ProcessId);
            using HttpClient http = Client();
            Assert.Equal("4", Ui.Await(http.GetStringAsync(new Uri(HttpUrl(Resource(window, "apiservice")), "/passenger-edit")), "the rebuilt API's method"));

            File.AppendAllText(Path.Combine(Path.GetDirectoryName(project.Host)!, "AppHost.cs"), "\n// Rerun runs a changed AppHost again.\n");
            Ui.Run(window, AppActions.Rerun);
            Ui.WaitFor(() => window.AspireRuntime.Apps is [{ ProcessId: int again }] && again != hostPid
                && window.Aspire.ResourceSnapshots.Any(resource => resource.DisplayName == "apiservice" && resource.State == "Running"),
                "Rerun to run the changed AppHost again", TimeSpan.FromMinutes(3));
            Assert.False(Processes.IsRunning(hostPid), "The AppHost that ran before Rerun still runs");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public void Copied_service_choices_and_a_linked_configuration_survive_reopening_and_single_file_discovery_and_the_linked_pair_stops_together()
    {
        string settings = TestFolders.Create("aspire-configurations-");
        MainWindow window = Keeping(settings);
        try
        {
            Choose(window);
            RunConfiguration generated = window.Configuration!;
            FindAction(window, "Edit Configurations…");
            RunConfigurationsDialog dialog = Assert.IsType<RunConfigurationsDialog>(window.RunConfigurationsDialog);
            Ui.WaitFor(() => ServiceChoice(dialog, "apiservice") is not null && ServiceChoice(dialog, "webfrontend") is not null,
                "both official project resources on the initially selected AppHost");
            Assert.True(ServiceChoice(dialog, "apiservice")!.IsEnabled);
            Assert.True(ServiceChoice(dialog, "webfrontend")!.IsEnabled);
            Assert.Equal(0, ServiceChoice(dialog, "apiservice")!.SelectedIndex);
            Assert.Equal(0, ServiceChoice(dialog, "webfrontend")!.SelectedIndex);
            Ui.Click(dialog.CopyButton);
            Ui.Click(dialog.NameBox);
            Ui.Press(dialog, "control A");
            dialog.KeyTextInput("API debug, frontend run");
            Ui.Settle();
            ComboBox api = ServiceChoice(dialog, "apiservice")!;
            Assert.True(api.IsEnabled);
            Ui.Click(api);
            Ui.Press(dialog, "DOWN");
            Ui.Press(dialog, "ENTER");
            ComboBox web = ServiceChoice(dialog, "webfrontend")!;
            Ui.Click(web);
            Ui.Press(dialog, "END");
            Ui.Press(dialog, "ENTER");
            Ui.Click(web);
            Ui.Press(dialog, "HOME");
            Ui.Press(dialog, "ENTER");
            Ui.Click(dialog.OkButton);
            RunConfiguration saved = Assert.Single(window.Configurations, configuration => configuration.Name == "API debug, frontend run");
            Assert.Equal(ServiceStart.Debug, saved.ServiceActions["apiservice"]);
            Assert.Equal(ServiceStart.Run, saved.ServiceActions["webfrontend"]);
            Assert.True(PathNames.Same(project.ApiProject, saved.ServiceProjects["apiservice"]));
            Assert.True(PathNames.Same(Path.Combine(Path.GetDirectoryName(project.WebProgram)!, $"{AspireApp.Name}.Web.csproj"), saved.ServiceProjects["webfrontend"]));
            // A configuration of the folder's own running both starters, and a single-file AppHost not to replace it.
            RunConfiguration linked = generated with { Name = "Both starters", Generated = false, LinkedAppHosts = [project.CompanionHost] };
            window.SetConfigurations([.. window.Configurations.Where(configuration => !configuration.Generated), linked], linked.Name);
            string single = PathNames.Canonical(project.Write("OtherDiscovered.cs", $"// discovered independently of the project host\n  #:sdk Aspire.AppHost.Sdk@{AspireApp.TemplatesVersion}\nvar builder = DistributedApplication.CreateBuilder(args);\nbuilder.Build().Run();\n"));
            window.Close();
            Ui.Settle();

            window = Keeping(settings);
            Ui.WaitFor(() => window.Configurations.Any(configuration => PathNames.Same(configuration.Project, single)), "asynchronous single-file AppHost discovery");
            Assert.Equal(linked.Name, window.Configuration?.Name);
            RunConfiguration restored = Assert.Single(window.Configurations, configuration => configuration.Name == saved.Name);
            Assert.Equal(saved.ServiceActions, restored.ServiceActions);
            Assert.Equal(saved.ServiceProjects, restored.ServiceProjects);
            Assert.True(window.SelectConfiguration(saved.Name));
            FindAction(window, "Edit Configurations…");
            dialog = Assert.IsType<RunConfigurationsDialog>(window.RunConfigurationsDialog);
            Ui.WaitFor(() => ServiceChoice(dialog, "apiservice")?.SelectedIndex == 1
                && ServiceChoice(dialog, "webfrontend")?.SelectedIndex == 0, "the persisted per-resource choices");
            Ui.Press(dialog, "ESCAPE");

            RunConfiguration discovered = window.Configurations.Single(configuration => PathNames.Same(configuration.Project, single));
            Assert.Contains("requires apphost.cs", window.WhyNotHere(discovered), StringComparison.Ordinal);
            Assert.True(window.SelectConfiguration(discovered.Name));
            Assert.Contains("requires apphost.cs", Ui.Await(window.RunAsync(), "the noncanonical single-file refusal"), StringComparison.Ordinal);
            Assert.Empty(window.AspireRuntime.Apps);
            Assert.True(window.SelectConfiguration(linked.Name));
            Assert.Equal([PathNames.Canonical(project.CompanionHost)], window.Configuration!.LinkedAppHosts);
            Assert.Equal("http", window.Configurations.First(configuration => configuration.Aspire
                && PathNames.Same(configuration.Project, project.CompanionHost)).LaunchProfile);
            Ui.Launched(window, window.RunAsync(), "both linked official starters", TimeSpan.FromMinutes(4));
            Ui.WaitFor(() => window.AspireRuntime.Apps.Count == 2 && window.AspireRuntime.Apps.All(app => app.Services.Count >= 2), "both apps' DCP-owned services");
            Assert.Equal(2, window.AspireWindow.AppSelector.Items.Count);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Both starters (2 apps running)");
            int[] owned = OwnedPids(window);
            Assert.True(owned.Length >= 6, "Two AppHosts and four services must have real PIDs");
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => window.AspireRuntime.Apps.Count == 0 && owned.All(pid => !Processes.IsRunning(pid)), "the linked group Stop to end every owned process");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(settings);
        }
    }

    [AvaloniaFact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public void The_AppHost_gutter_runs_or_debugs_only_the_requested_project_and_refuses_a_declaration_whose_source_changed()
    {
        MainWindow window = project.OpenHost();
        try
        {
            Choose(window);
            window.ApplySettings(window.Settings with { HotReload = false });
            using HttpClient http = Client();
            foreach (bool debugging in new[] { false, true })
            {
                OpenGutter(window, "apiservice");
                Ui.Choose(Assert.IsType<MenuFlyout>(window.RunMarkMenu), debugging ? "Debug 'apiservice'" : "Run 'apiservice'");
                WaitNativeServices(window);
                AspireResourceRun api = Service(window, Resource(window, "apiservice"));
                AspireResourceRun web = Service(window, Resource(window, "webfrontend"));
                Assert.Equal(debugging, api.DebugTab?.IsLive == true);
                Assert.Null(web.DebugTab);
                Assert.Equal(debugging ? 1 : 0, window.Debugger.Tabs.Count);
                Assert.Equal("5", Ui.Await(http.GetStringAsync(new Uri(HttpUrl(Resource(window, "webfrontend")), "/passenger-test")),
                    "the gutter-started application"));
                int[] owned = OwnedPids(window);
                window.Perform(AppActions.Stop);
                Ui.WaitFor(() => window.AspireRuntime.Apps.Count == 0 && owned.All(pid => !Processes.IsRunning(pid)) && Leftovers().Count == 0,
                    "Stop to end the gutter-started AppHost and every program it ran");
            }

            OpenGutter(window, "apiservice");
            string path = window.EditorArea.Active!.Path;
            string source = File.ReadAllText(path);
            File.WriteAllText(path, source + "\n// Changed while the run menu was open.\n");
            Ui.WaitFor(() => window.EditorArea.CodeEditor.Document.Snapshot.ToString() != source,
                "the editor to observe the changed AppHost source");
            Ui.Choose(Assert.IsType<MenuFlyout>(window.RunMarkMenu), "Run 'apiservice'");
            Assert.Empty(window.AspireRuntime.Apps);
            Assert.False(window.IsRunning);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public void Gutter_startup_profile_uses_its_AppHost_not_the_toolbar_and_records_the_first_hot_turn()
    {
        string source = File.ReadAllText(project.ApiProgram);
        Assert.Contains("var builder = WebApplication.CreateBuilder(args);", source, StringComparison.Ordinal);
        // Written on disk, as an editor elsewhere would, and put back before the next test, the shared server kept.
        File.WriteAllText(project.ApiProgram, source.Replace("var builder = WebApplication.CreateBuilder(args);",
            "PassengerEarly.Hot();\nvar builder = WebApplication.CreateBuilder(args);", StringComparison.Ordinal) + """

            static class PassengerEarly
            {
                [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
                public static void Hot()
                {
                    double total = 0;
                    for (int i = 1; i < 100_000_000; i++)
                    {
                        total += Math.Sqrt(i);
                    }
                    Console.WriteLine($"Passenger first turn {total}");
                }
            }
            """);
        MainWindow window = project.OpenHost();
        try
        {
            Choose(window);
            RunConfiguration companion = window.Configurations.First(configuration => PathNames.Same(configuration.Project, project.CompanionHost)
                && configuration.LaunchProfile == "http");
            Assert.True(window.SelectConfiguration(companion.Name));
            OpenGutter(window, "apiservice");
            Ui.Choose(Assert.IsType<MenuFlyout>(window.RunMarkMenu), "Profile 'apiservice'");
            WaitNativeServices(window);
            AspireAppRun app = Assert.Single(window.AspireRuntime.Apps);
            Assert.True(PathNames.Same(project.Host, app.Configuration.Project));
            Assert.Equal(companion.Name, window.Configuration!.Name);
            int apiPid = Service(window, Resource(window, "apiservice")).ProcessId!.Value;
            int webPid = Service(window, Resource(window, "webfrontend")).ProcessId!.Value;
            Ui.WaitFor(() => window.Profiling?.ProcessId == apiPid, "the requested API's startup recording");
            Ui.WaitFor(() => Service(window, Resource(window, "apiservice")).Console.Document.Snapshot.ToString().Contains("Passenger first turn", StringComparison.Ordinal),
                "the profiled API to complete its first hot turn");
            Passenger.App.Profiling.SnapshotRead read = Ui.Await(window.SnapshotAsync(), "the startup profile snapshot", TimeSpan.FromMinutes(2));
            Assert.NotNull(read.Snapshot);
            Assert.Contains(read.Snapshot.HotSpots, spot => spot.Name.StartsWith("PassengerEarly.Hot", StringComparison.Ordinal));
            Assert.True(Processes.IsRunning(apiPid) && Processes.IsRunning(webPid));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>One run of the starter, read as a person reads it: the window's tabs in a small window, a service's health and
    /// address, the merged console and Find in it, a request's structured log and its trace across both services, a span
    /// chosen kept through the next batch, the API's command and the prompts it asks, and Restart of the API alone.</summary>
    [AvaloniaFact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public void The_official_starter_runs_through_the_window_with_its_console_telemetry_commands_and_resource_restart()
    {
        MainWindow window = OpenRunning();
        try
        {
            window.Width = 900;
            window.Height = 650;
            Ui.Settle();
            Avalonia.Controls.Primitives.ToggleButton lastTab = window.AspireWindow.GetVisualDescendants()
                .OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(button => button.Content is TextBlock { Text: "AppHost Log" });
            Point? tabBottom = lastTab.TranslatePoint(new Point(0, lastTab.Bounds.Height), window.AspireWindow);
            Assert.True(lastTab.Bounds.Height > 0 && tabBottom is { } bottom && bottom.Y <= window.AspireWindow.Bounds.Height,
                "The window's tabs must remain reachable in a narrow, short tool window");
            window.Width = 1200;
            window.Height = 800;
            Ui.Settle();
            using HttpClient http = Client();
            AspireResource api = Resource(window, "apiservice");
            AspireResource web = Resource(window, "webfrontend");
            int oldPid = Service(window, api).ProcessId!.Value;
            int webPid = Service(window, web).ProcessId!.Value;
            int[] owned = OwnedPids(window);
            Assert.True(Processes.IsRunning(oldPid) && Processes.IsRunning(webPid), "The API and frontend must be real Passenger-owned processes");

            FindAction(window, "Show Aspire Resources");
            Ui.WaitFor(() => Resource(window, "apiservice").HealthStatus == "Healthy", "the API's native health update");
            Control apiNode = AspireStandInTests.Row(window, "apiservice");
            Assert.Contains(apiNode.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "healthy");
            Uri endpoint = HttpUrl(Resource(window, "apiservice"));
            Button portLink = apiNode.GetVisualDescendants().OfType<Button>().First(button => Equals(ToolTip.GetTip(button), endpoint.AbsoluteUri)
                || Equals(ToolTip.GetTip(button), endpoint.AbsoluteUri.TrimEnd('/')));
            Ui.Click(portLink);
            Assert.Contains(window.Browsed, browsed => browsed == endpoint);

            FindAction(window, "Show Aspire Log");
            Uri request = new(HttpUrl(web), "/passenger-test");
            Assert.Equal("5", Ui.Await(http.GetStringAsync(request), "the frontend's cross-service HTTP response"));
            Ui.WaitFor(() => window.AspireWindow.Console.Document.Snapshot.ToString().Contains("[apiservice]", StringComparison.Ordinal)
                && window.AspireWindow.Console.Document.Snapshot.ToString().Contains("[webfrontend]", StringComparison.Ordinal), "both service prefixes in the merged console");
            Ui.WaitFor(() => window.AspireWindow.Console.Document.Snapshot.ToString().Contains("Passenger request", StringComparison.Ordinal), "the API's live console output");
            FindAction(window, "Find in Aspire Logs");
            window.KeyTextInput("Passenger request");
            Ui.WaitFor(() => window.AspireWindow.Find.IsEffectivelyVisible && window.AspireWindow.Find.CountText.Contains("/", StringComparison.Ordinal)
                && !window.AspireWindow.Find.CountText.StartsWith("0", StringComparison.Ordinal),
                "real Find to select a merged console match");
            Ui.Press(window, "ESCAPE");
            Assert.True(window.IsRunning);

            Roomy(window);
            FindAction(window, "Show Aspire Structured logs");
            RefreshUntil(window, () => window.AspireWindow.Telemetry.Logs
                .Any(log => log.Data.GetRawText().Contains("Passenger request", StringComparison.Ordinal)), "the API's structured request log");
            AspireStructuredLog log = window.AspireWindow.Telemetry.Logs
                .First(item => item.Data.GetRawText().Contains("Passenger request", StringComparison.Ordinal));
            ClickListItem(window, window.AspireWindow.StructuredLogs, log);
            Assert.False(string.IsNullOrWhiteSpace(log.TraceId), "The structured log must retain its trace correlation");
            Assert.False(string.IsNullOrWhiteSpace(log.SpanId), "The structured log must retain its span correlation");
            Assert.Contains(window.AspireWindow.GetVisualDescendants().OfType<Passenger.App.ToolWindows.OutputView>(),
                output => output.IsEffectivelyVisible && output.Document.Snapshot.ToString().Contains("RequestValue", StringComparison.Ordinal)
                    && output.Document.Snapshot.ToString().Contains("42", StringComparison.Ordinal));
            FindAction(window, "Show Aspire Traces");
            RefreshUntil(window, () => window.AspireWindow.Telemetry.Traces.Any(trace =>
                trace.TraceId == log.TraceId && trace.Spans.Select(span => span.Source).Distinct().Count() >= 2), "the API and frontend spans in one trace");
            AspireTrace trace = window.AspireWindow.Telemetry.Traces.First(trace => trace.TraceId == log.TraceId);
            ClickListItem(window, window.AspireWindow.TraceList, trace);
            Assert.True(window.AspireWindow.SpanList.Items.Count >= 2, "The native trace hierarchy must show cross-service spans");

            Passenger.App.Telemetry.TelemetryView.SpanRow child = window.AspireWindow.Telemetry.Spans.First(row => row.Depth > 0);
            ClickListItem(window, window.AspireWindow.SpanList, child);
            string[] before = window.AspireWindow.Telemetry.Traces.Select(item => item.TraceId).ToArray();
            Assert.Equal("5", Ui.Await(http.GetStringAsync(request), "the next cross-service trace"));
            Ui.WaitFor(() => window.AspireWindow.Telemetry.Traces.Any(item => !before.Contains(item.TraceId)),
                "the next native telemetry batch");
            Assert.Equal(trace.TraceId, window.AspireWindow.Telemetry.SelectedTrace?.TraceId);
            Assert.Equal(child.Span.SpanId, window.AspireWindow.Telemetry.SelectedSpan?.Span.SpanId);

            File.Delete(project.InteractionAnswer);
            ChooseResource(window, "apiservice");
            Ui.WaitFor(() => window.Aspire.SelectedResource?.Commands.Any(command => command.DisplayName == "Passenger interaction") == true,
                "the real AppHost's advertised command");
            RunCommand(window, "Passenger interaction");
            AspireInteractionDialog confirmation = Prompt(window, "Restart API?");
            Ui.Click(confirmation.Secondary);
            AspireInteractionDialog form = Prompt(window, "API settings");
            TextBox input = Assert.IsType<TextBox>(form.InputControls["value"]);
            Ui.Click(input);
            Ui.Press(form, "control A");
            form.KeyTextInput("typed while connected");
            Ui.Click(form.Primary);
            Ui.WaitFor(() => form.Problem.Text?.Contains("Choose another value", StringComparison.Ordinal) == true,
                "the AppHost's validation update");
            input = Assert.IsType<TextBox>(form.InputControls["value"]);
            Assert.Equal("typed while connected", input.Text);
            Ui.Click(input);
            Ui.Press(form, "control A");
            form.KeyTextInput("latest value");
            Ui.Click(form.Primary);
            Ui.WaitFor(() => File.Exists(project.InteractionAnswer), "the real AppHost's submitted value");
            Assert.Equal("latest value", File.ReadAllText(project.InteractionAnswer));
            // The form alone: the AppHost may say other things meanwhile, as that the development certificate is not trusted.
            Ui.WaitFor(() => !window.OwnedWindows.OfType<AspireInteractionDialog>().Contains(form), "the completed form to close");
            Ui.WaitFor(() => AppHostSaid(window).Contains("passenger-interaction: Succeeded", StringComparison.Ordinal),
                "the command's result in the AppHost's console");

            AspireResourceCommand restart = Assert.Single(Resource(window, "apiservice").Commands, command => command.Name is "restart" or "resource-restart");
            ChooseResource(window, "apiservice");
            Ui.WaitFor(() => window.Aspire.SelectedResource?.Commands.Any(command => command.Name == restart.Name && command.Enabled) == true,
                $"the API's {restart.DisplayName} command");
            RunCommand(window, restart.DisplayName);
            RefreshUntil(window, () => window.Aspire.ResourceSnapshots.Any(resource => resource.DisplayName == "apiservice"
                && window.AspireRuntime.FindService(new(project.Host, resource))?.ProcessId is int pid && pid != oldPid), "the restarted API's new PID");
            Ui.WaitFor(() => !Processes.IsRunning(oldPid), "the old API process to exit");
            Assert.True(Processes.IsRunning(webPid), "Restarting the API must not stop the frontend");
            Assert.Equal("5", Ui.Await(http.GetStringAsync(new Uri(HttpUrl(Resource(window, "webfrontend")), "/passenger-test")), "HTTP after restart"));
            owned = [.. owned, .. OwnedPids(window)];
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => window.AspireRuntime.Apps.Count == 0 && owned.All(pid => !Processes.IsRunning(pid)), "Stop to end the AppHost and every service");
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"{error.Message}\n{TelemetryDiagnostics(window)}", error);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public void A_discovered_single_file_AppHost_runs_through_the_window_with_its_profiles_number_value_and_one_beside_a_project_is_refused_before_starting_a_process()
    {
        Directory.CreateDirectory(project.PathOf("Loose"));
        string loose = PathNames.Canonical(project.Write("Loose/apphost.cs", $"#:sdk Aspire.AppHost.Sdk@{AspireApp.TemplatesVersion}\nvar builder = DistributedApplication.CreateBuilder(args);\nbuilder.Build().Run();\n"));
        project.Write("Loose/Sibling.csproj", TestProjects.Library());
        string file = PathNames.Canonical(project.Write("apphost.cs", $"#:sdk Aspire.AppHost.Sdk@{AspireApp.TemplatesVersion}\n#:property AspireUseCliBundle=false\nusing Aspire.Hosting;\nusing Microsoft.Extensions.Hosting;\nSystem.Console.WriteLine(\"passenger-profile-port: \" + System.Environment.GetEnvironmentVariable(\"PASSENGER_PROFILE_PORT\"));\nvar builder = DistributedApplication.CreateBuilder(args);\nbuilder.Build().Run();\n"));
        project.Write("apphost.run.json", JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["$schema"] = "https://json.schemastore.org/launchsettings.json",
            ["profiles"] = new Dictionary<string, object>
            {
                ["unused"] = new
                {
                    commandName = "Project",
                    environmentVariables = new Dictionary<string, string>
                    {
                        ["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = "not-an-endpoint",
                    },
                },
                ["selected-http"] = new
                {
                    commandName = "Project",
                    dotnetRunMessages = true,
                    launchBrowser = false,
                    applicationUrl = $"http://localhost:{AspireApp.FreePort()}",
                    environmentVariables = new Dictionary<string, object>
                    {
                        // A number, which dotnet run passes as its JSON.
                        ["PASSENGER_PROFILE_PORT"] = 5000,
                        ["ASPNETCORE_ENVIRONMENT"] = "Development",
                        ["DOTNET_ENVIRONMENT"] = "Development",
                        ["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = $"http://localhost:{AspireApp.FreePort()}",
                        ["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = $"http://localhost:{AspireApp.FreePort()}",
                        ["ASPIRE_DASHBOARD_RESOURCESERVICE_APIKEY"] = "passenger-single-file-profile-key",
                        ["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true",
                        ["ASPIRE_VERSION_CHECK_DISABLED"] = "true",
                        ["ASPIRE_DASHBOARD_DATA_DIRECTORY"] = project.PathOf("single-file-dashboard"),
                        ["HOME"] = AspireCli.Home,
                    },
                },
            },
        }));
        MainWindow window = project.OpenHost(languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => PathNames.Same(configuration.Project, loose))
                && window.Configurations.Any(configuration => PathNames.Same(configuration.Project, file)), "both single-file AppHosts' configurations");

            Assert.True(window.SelectConfiguration(window.Configurations.Single(configuration => PathNames.Same(configuration.Project, loose)).Name));
            string? beside = Ui.Await(window.RunAsync(), "the pinned CLI's sibling-project refusal");
            Assert.Contains("13.x (13.6.0 or newer)", beside, StringComparison.Ordinal);
            Assert.Contains("sibling .csproj", beside, StringComparison.Ordinal);
            Assert.Empty(window.AspireRuntime.Apps);
            Assert.False(window.IsRunning);

            RunConfiguration generated = window.Configurations.Single(configuration => PathNames.Same(configuration.Project, file));
            RunConfiguration configured = generated with
            {
                Name = "Single-file Aspire",
                LaunchProfile = "selected-http",
                Generated = false,
                Environment = new Dictionary<string, string>
                {
                    ["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = $"http://localhost:{AspireApp.FreePort()}",
                    ["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true",
                    ["ASPIRE_VERSION_CHECK_DISABLED"] = "true",
                },
            };
            window.SetConfigurations([configured], configured.Name);
            window.ApplySettings(window.Settings with { HotReload = false });
            string? refused = Ui.Await(window.RunAsync(), "the actual single-file Aspire AppHost", TimeSpan.FromMinutes(3));
            if (refused is not null)
            {
                Assert.Fail($"The single-file AppHost did not launch: {refused}{Ui.BuildErrors(window)}\nAppHost output:\n{Redacted(AppHostSaid(window))}");
            }
            window.BottomPanel.Build.Flush();
            string built = window.BottomPanel.Build.Document.Snapshot.ToString();
            Assert.True(built.Contains($"build {file}", StringComparison.Ordinal) && built.Contains("--output", StringComparison.Ordinal),
                $"The single-file AppHost was not built in the Build window:\n{built}");
            Ui.WaitFor(() => AppHostSaid(window).Contains("passenger-profile-port: 5000", StringComparison.Ordinal),
                () => $"the single-file AppHost given its profile's number value, which it said as:\n{Redacted(AppHostSaid(window))}");
            FindAction(window, "Show Aspire Resources");
            Ui.WaitFor(() => window.Aspire.ResourceSnapshots.Any(resource => resource.DisplayName == "aspire-dashboard" && resource.State == "Running"), "the single-file AppHost's native resource stream");
            AspireAppRun app = Assert.Single(window.AspireRuntime.Apps);
            int[] owned = OwnedPids(window);
            Assert.True(app.ProcessId is > 0 && owned.Length > 0);
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => window.AspireRuntime.Apps.Count == 0 && owned.All(pid => !Processes.IsRunning(pid)), "stopping the single-file AppHost and its CLI");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public void Two_service_breakpoints_keep_both_sessions_alive_and_later_breakpoints_reach_the_background_session()
    {
        MainWindow window = project.OpenHost(languageServer: false);
        int apiLine = BreakAt(window, project.ApiProgram, "logger.LogInformation");
        int webLine = BreakAt(window, project.WebProgram, "logger.LogInformation");
        Choose(window);
        Ui.Launched(window, window.DebugAsync(), "the starter's distributed debug sessions", TimeSpan.FromMinutes(3));
        WaitServices(window);
        AspireResourceRun api = Service(window, Resource(window, "apiservice"));
        AspireResourceRun web = Service(window, Resource(window, "webfrontend"));
        DebugTab apiDebug = Assert.IsType<DebugTab>(api.DebugTab);
        DebugTab webDebug = Assert.IsType<DebugTab>(web.DebugTab);
        Assert.NotSame(apiDebug, webDebug);
        Assert.NotSame(apiDebug.Console, webDebug.Console);
        using HttpClient http = Client();
        Uri apiUrl = HttpUrl(Resource(window, "apiservice"));
        Uri webUrl = HttpUrl(Resource(window, "webfrontend"));
        Task<string> webRequest = http.GetStringAsync(new Uri(webUrl, "/passenger-test"));
        WaitStopped(webDebug, project.WebProgram, webLine);
        Task<string> apiRequest = http.GetStringAsync(new Uri(apiUrl, "/weatherforecast"));
        WaitStopped(apiDebug, project.ApiProgram, apiLine);
        Assert.Same(apiDebug, window.Debugger.Chosen);
        Assert.True(webDebug.IsLive && apiDebug.IsLive, "A second service stop must not replace the first debugger");
        Ui.WaitFor(() => window.BottomPanel.Debug.Tree.Rows.Any(row => row.Name == "requestValue" && row.Value == "42"), "the API request local");
        Ui.Await(Complete(window.Debugger.ChooseAsync(webDebug)), "choosing the paused frontend tab");
        Assert.Same(webDebug, window.Debugger.Chosen);
        Assert.Same(webDebug.Console, window.BottomPanel.Debug.Console);
        Assert.Equal("42", window.BottomPanel.Debug.Tree.Rows.First(row => row.Name == "requestValue").Value);
        Ui.Await(Complete(window.Aspire.RefreshAsync()), "the resource snapshot while a paused sibling is selected");
        Assert.Same(webDebug, window.Debugger.Chosen);

        int later = BreakAt(window, project.ApiProgram, "return forecast;");
        Assert.Same(webDebug, window.Debugger.Chosen);
        Ui.Await(Complete(window.Debugger.ChooseAsync(apiDebug)), "choosing the paused API tab");
        window.Perform(AppActions.Resume);
        Ui.PumpUntil(() => window.Debugger.Stop?.Top?.Line == later + 1, "the breakpoint added after both service sessions started");
        Assert.Same(apiDebug, window.Debugger.Chosen);
        window.Perform(AppActions.Resume);
        Assert.Contains("temperatureC", Ui.AwaitPumped(apiRequest, "the resumed API HTTP request"), StringComparison.Ordinal);
        Ui.Await(Complete(window.Debugger.ChooseAsync(webDebug)), "choosing the frontend to resume its request");
        window.Perform(AppActions.Resume);
        Ui.PumpUntil(() => webDebug.Session?.State != DebugState.Paused, "the frontend to leave its first stop");
        WaitStopped(apiDebug, project.ApiProgram, apiLine);
        Assert.Same(apiDebug, window.Debugger.Chosen);
        window.Perform(AppActions.Resume);
        Ui.PumpUntil(() => window.Debugger.Stop?.Top?.Line == later + 1, "the later breakpoint on the frontend's API request");
        window.Perform(AppActions.Resume);
        Assert.Equal("5", Ui.AwaitPumped(webRequest, "the resumed cross-service HTTP request"));
        Assert.True(apiDebug.IsLive && webDebug.IsLive);
        int[] owned = OwnedPids(window);
        window.Close();
        Ui.WaitFor(() => owned.All(pid => !Processes.IsRunning(pid)), "closing the window to stop both service debuggers and the AppHost");
    }

    [AvaloniaFact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public void Attach_run_without_debugger_and_profile_target_only_the_selected_real_service_and_a_native_tab_takes_its_selection()
    {
        MainWindow window = OpenRunning();
        try
        {
            AspireResource api = Resource(window, "apiservice");
            AspireResource web = Resource(window, "webfrontend");
            int apiPid = Service(window, api).ProcessId!.Value;
            int webPid = Service(window, web).ProcessId!.Value;
            JsonObject changedIdentity = JsonNode.Parse(api.Data.GetRawText())!.AsObject();
            changedIdentity["properties"]!.AsObject()["executable.pid"] = int.MaxValue;
            AspireSelection foreignProcess = new(project.Host, new AspireResource(JsonSerializer.SerializeToElement(changedIdentity)));
            Assert.Null(window.AspireRuntime.FindService(foreignProcess));
            Assert.Throws<InvalidOperationException>(() => window.AspireRuntime.SetDebugMode(foreignProcess, debugging: true));
            ChooseResource(window, "apiservice");
            FindAction(window, "Attach to Service");
            Ui.WaitFor(() => Service(window, api).DebugTab?.IsLive == true, $"Attach to the chosen API ({window.Status})");
            AspireResourceRun service = Service(window, api);
            Assert.Equal(apiPid, service.ProcessId);
            Assert.Null(Service(window, web).DebugTab);
            DebugTab apiTab = Assert.IsType<DebugTab>(service.DebugTab);
            ChooseResource(window, "apiservice");
            Assert.Same(service, window.AspireRuntime.SelectedService);

            LaunchPlan plan = new()
            {
                Name = "Native console",
                Program = Debugging.Helpers.Debuggees.Program("LoopingConsoleApp"),
                Arguments = ["sleep"],
                WorkingDirectory = Path.GetDirectoryName(Debugging.Helpers.Debuggees.Program("LoopingConsoleApp"))!,
            };
            DebugLaunch native = Ui.Await(window.Debugger.StartAdditionalAsync(plan), "the native console beside Aspire");
            Assert.Same(native.Tab, window.Debugger.Chosen);
            Assert.Null(window.AspireRuntime.SelectedService);
            Ui.Await(Complete(window.Aspire.RefreshAsync()), "refreshing Aspire while the native tab is chosen");
            Assert.Null(window.AspireRuntime.SelectedService);
            Assert.Same(native.Tab, window.Debugger.Chosen);
            Ui.Await(Complete(window.Debugger.ChooseAsync(apiTab)), "choosing the Aspire service's native tab again");
            Assert.Same(service, window.AspireRuntime.SelectedService);
            Ui.Await(Complete(window.Debugger.ChooseAsync(native.Tab)), "returning to the ordinary native tab");
            Assert.Null(window.AspireRuntime.SelectedService);
            Assert.True(apiTab.IsLive && native.Tab.IsLive);
            Assert.True(Processes.IsRunning(apiPid));
            Ui.Await(Complete(window.Debugger.ChooseAsync(apiTab)), "choosing the attached API's tab to go on with it");

            ChooseResource(window, "apiservice");
            Button noDebug = window.AspireWindow.Tools[AppActions.ServiceRun];
            Ui.WaitFor(() => noDebug.IsEffectivelyVisible && noDebug.IsEnabled, "the selected resource's Run without debugger action");
            Ui.Click(noDebug);
            RefreshUntil(window, () => window.Aspire.ResourceSnapshots.Any(resource => resource.DisplayName == "apiservice"
                && window.AspireRuntime.FindService(new(project.Host, resource)) is { ProcessId: int pid, DebugTab: null } && pid != apiPid), "the selected API to restart without a debugger");
            Ui.WaitFor(() => !Processes.IsRunning(apiPid), "the attached API process to exit during restart");
            Assert.True(Processes.IsRunning(webPid), "Run without debugger must leave the sibling frontend alive");
            web = Resource(window, "webfrontend");
            ChooseResource(window, "webfrontend");
            FindAction(window, "Profile Service");
            Ui.WaitFor(() => window.Profiling?.ProcessId == webPid, $"Profile to record the chosen frontend ({window.Status})");
            using HttpClient http = Client();
            Assert.Equal("5", Ui.Await(http.GetStringAsync(new Uri(HttpUrl(web), "/passenger-test")), "the profiled frontend's cross-service request"));
            Passenger.App.Profiling.SnapshotRead snapshot = Ui.Await(window.SnapshotAsync(), "the selected frontend's profiling snapshot", TimeSpan.FromMinutes(2));
            Assert.NotNull(snapshot.Snapshot);
            Assert.True(Processes.IsRunning(webPid), "Reading an attached service's snapshot must leave its process alive");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The IDE's execution endpoint and resource service as the running starter's AppHost reaches them, asked what an
    /// orchestrator or another client might ask: each wrong request refused with the starter's services left running, a
    /// foreign instance kept from the starter's, and a create its client gives up on while the window is held starting no
    /// service once the window closes, which ends every program and the endpoint.</summary>
    [AvaloniaFact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public void The_running_windows_Dcp_endpoint_refuses_what_it_must_isolates_foreign_instances_and_starts_nothing_after_close()
    {
        MainWindow window = OpenRunning();
        AspireAppRun app = Assert.Single(window.AspireRuntime.Apps);
        (int Port, string Token) endpoint = AspireApp.ReadConnectionInfo(app.Configuration.Project);
        Uri root = new($"http://127.0.0.1:{endpoint.Port}");
        using HttpClient http = Client();

        using JsonDocument launchSettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(project.Host)!, "Properties", "launchSettings.json")));
        string resourceEndpoint = launchSettings.RootElement.GetProperty("profiles").GetProperty("http")
            .GetProperty("environmentVariables").GetProperty("ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL").GetString()!;
        using GrpcChannel resourceChannel = GrpcChannel.ForAddress(resourceEndpoint);
        DashboardService.DashboardServiceClient resourceClient = new(resourceChannel);
        Task<ApplicationInformationResponse> deniedResource = resourceClient.GetApplicationInformationAsync(new ApplicationInformationRequest(),
            new Metadata { { "x-resource-service-api-key", "wrong-resource-key" } }, cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;
        RpcException denied = Assert.Throws<RpcException>(() =>
        {
            Ui.Await(deniedResource, "rejecting the wrong resource-service key");
        });
        Assert.Equal(StatusCode.Unauthenticated, denied.StatusCode);

        AspireResourceRun service = Service(window, Resource(window, "apiservice"));
        int pid = service.ProcessId!.Value;
        using HttpResponseMessage unauthorized = Send(http, root, HttpMethod.Get, "/info", "not-the-token", service.InstanceId);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using HttpResponseMessage missingInstance = Send(http, root, HttpMethod.Get, "/info", endpoint.Token, null);
        Assert.Equal(HttpStatusCode.BadRequest, missingInstance.StatusCode);
        using HttpResponseMessage info = Send(http, root, HttpMethod.Get, "/info", endpoint.Token, service.InstanceId);
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        Assert.Contains("2024-03-03", Ui.Await(info.Content.ReadAsStringAsync(), "DCP capabilities"), StringComparison.Ordinal);
        using HttpResponseMessage malformed = Send(http, root, HttpMethod.Put, "/run_session?api-version=2024-03-03", endpoint.Token, service.InstanceId, "{");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using HttpResponseMessage invalid = Send(http, root, HttpMethod.Put, "/run_session?api-version=2024-03-03", endpoint.Token, service.InstanceId, "{\"launch_configurations\":[null]}");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using HttpResponseMessage unsupportedFile = Send(http, root, HttpMethod.Put, "/run_session?api-version=2024-03-03", endpoint.Token, service.InstanceId,
            JsonSerializer.Serialize(new
            {
                launch_configurations = new[] { new { type = "project", project_path = project.FileService, mode = "NoDebug" } },
            }));
        Assert.Equal(HttpStatusCode.BadRequest, unsupportedFile.StatusCode);
        string refusal = Ui.Await(unsupportedFile.Content.ReadAsStringAsync(), "the explicit file-service refusal");
        Assert.Contains("UnsupportedProjectType", refusal, StringComparison.Ordinal);
        Assert.Contains("File-based Aspire services are not supported", refusal, StringComparison.Ordinal);
        Assert.False(File.Exists(project.FileServiceRan));
        Assert.True(Processes.IsRunning(pid));

        const string Foreign = "passenger-tests-abcdefghijkl";
        using (ClientWebSocket socket = Notifications(endpoint, Foreign, "a foreign instance's real notification connection"))
        {
            using HttpResponseMessage foreignDelete = Send(http, root, HttpMethod.Delete, $"/run_session/{service.Id}?api-version=2024-03-03", endpoint.Token, Foreign);
            Assert.Equal(HttpStatusCode.NoContent, foreignDelete.StatusCode);
            Assert.True(Processes.IsRunning(pid), "A foreign DCP instance must not delete the starter's API process");
            Assert.Contains("API service is running", Ui.Await(http.GetStringAsync(HttpUrl(Resource(window, "apiservice"))), "the API after foreign deletion"), StringComparison.Ordinal);
            socket.Abort();
        }

        int services = app.Services.Count;
        using (ClientWebSocket notifications = Notifications(endpoint, "passenger-review-abcdefghijkl", "the failed-launch instance's notification subscription"))
        {
            using HttpClient quick = Client();
            quick.Timeout = TimeSpan.FromSeconds(15).Scaled();
            using HttpResponseMessage failed = Send(quick, root, HttpMethod.Put, "/run_session?api-version=2024-03-03", endpoint.Token, "passenger-review-abcdefghijkl",
                JsonSerializer.Serialize(new
                {
                    launch_configurations = new[] { new { type = "project", project_path = project.ApiProject, mode = "NoDebug", launch_profile = "missing-folder" } },
                }));
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Contains("SessionOperationFailed", Ui.Await(failed.Content.ReadAsStringAsync(), "the launch failure"), StringComparison.Ordinal);
            Assert.Equal(services, app.Services.Count);
            Assert.All(app.Services, running => Assert.True(Processes.IsRunning(running.ProcessId!.Value)));
            notifications.Abort();
        }

        const string Flooding = "passenger-flood-abcdefghijkl";
        int[] siblings = [.. app.Services.Select(running => running.ProcessId).OfType<int>().Where(Processes.IsRunning)];
        Assert.True(siblings.Length >= 2, "The official starter's API and frontend must be running before queue pressure");
        using (ClientWebSocket unread = Notifications(endpoint, Flooding, "the deliberately unread DCP notification socket"))
        {
            Uri address = new($"http://127.0.0.1:{AspireApp.FreePort()}");
            using HttpResponseMessage created = Send(http, root, HttpMethod.Put, "/run_session?api-version=2024-03-03", endpoint.Token, Flooding,
                JsonSerializer.Serialize(new
                {
                    launch_configurations = new[] { new { type = "project", project_path = project.ApiProject, mode = "NoDebug", disable_launch_profile = true } },
                    env = new[] { new { name = "ASPNETCORE_URLS", value = address.ToString() }, new { name = "DOTNET_ENVIRONMENT", value = "Development" } },
                }));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.NotNull(created.Headers.Location);
            AspireResourceRun flooding = Assert.Single(app.Services, running => running.InstanceId == Flooding);
            int floodingPid = flooding.ProcessId!.Value;
            Ui.WaitFor(() => flooding.Console.Document.Snapshot.ToString().Contains("Now listening on", StringComparison.Ordinal), "the additional real API listener");
            Assert.Equal("flood completed", Ui.Await(http.GetStringAsync(new Uri(address, "/passenger-flood")), "the API to write beyond the unread notification queue"));
            Assert.True(Processes.IsRunning(floodingPid), "Dropping console notifications must not terminate their service");
            Assert.Contains("API service is running", Ui.Await(http.GetStringAsync(address), "the service after console saturation"), StringComparison.Ordinal);
            unread.Abort();
            Ui.WaitFor(() => !Processes.IsRunning(floodingPid), "disconnecting the foreign instance to stop only its additional service");
            Assert.All(siblings, id => Assert.True(Processes.IsRunning(id), "Disconnecting the foreign DCP instance must leave the starter's services alive"));
        }

        const string Instance = "passenger-cancel-abcdefghijkl";
        string marker = project.PathOf("canceled-dcp-request.pid");
        using ClientWebSocket held = Notifications(endpoint, Instance, "the request-cancellation instance's notification connection");
        HashSet<int> lateProcesses = [];
        window.AspireRuntime.Changed += (_, _) => lateProcesses.UnionWith(app.Services.Where(running =>
            running.InstanceId == Instance && running.ResourceName == "canceled-at-boundary").Select(running => running.ProcessId).OfType<int>());
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30).Scaled());
        Uri serviceAddress = new($"http://127.0.0.1:{AspireApp.FreePort()}");
        string canceledBody = JsonSerializer.Serialize(new
        {
            launch_configurations = new[] { new { type = "project", project_path = project.ApiProject, mode = "NoDebug", disable_launch_profile = true } },
            env = new[]
            {
                new { name = "OTEL_SERVICE_NAME", value = "canceled-at-boundary" },
                new { name = "ASPNETCORE_URLS", value = serviceAddress.ToString() },
                new { name = "DOTNET_ENVIRONMENT", value = "Development" },
                new { name = "PASSENGER_CANCELED_DCP_PID", value = marker },
            },
        });
        Task disconnect = Waiting.Apart(async () =>
        {
            using TcpClient abandoned = new();
            await abandoned.ConnectAsync(IPAddress.Loopback, endpoint.Port, limit.Token);
            byte[] payload = Encoding.UTF8.GetBytes(canceledBody);
            byte[] headers = Encoding.ASCII.GetBytes($"PUT /run_session?api-version=2024-03-03 HTTP/1.1\r\nHost: 127.0.0.1:{endpoint.Port}\r\nAuthorization: Bearer {endpoint.Token}\r\nMicrosoft-Developer-DCP-Instance-ID: {Instance}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\n\r\n");
            await abandoned.GetStream().WriteAsync(headers, limit.Token);
            await abandoned.GetStream().WriteAsync(payload, limit.Token);
            using HttpClient control = Client();
            using HttpRequestMessage probe = new(HttpMethod.Get, new Uri(root, "/info"));
            probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
            probe.Headers.Add("Microsoft-Developer-DCP-Instance-ID", Instance);
            using HttpResponseMessage answered = await control.SendAsync(probe, limit.Token);
            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
            abandoned.Client.LingerState = new LingerOption(true, 0);
        }, limit.Token);
        // Wait without pumping: no posted service launch may run before the peer has disconnected.
        Ui.Idle(disconnect, "the real HTTP request to send and disconnect while the UI was held", TimeSpan.FromSeconds(30));
        int[] owned = OwnedPids(window);
        // Close before releasing the UI, so socket-abort scheduling cannot race a valid late launch.
        window.Close();
        Ui.WaitFor(() => owned.All(id => !Processes.IsRunning(id)), "closing with the disconnected request to end every owned process");
        Assert.Empty(lateProcesses);
        Assert.False(File.Exists(marker), "A canceled service launch escaped into window shutdown");
        Task<HttpResponseMessage> closed = http.GetAsync(new Uri(root, "/info"));
        Assert.Throws<HttpRequestException>(() => Ui.AwaitPumped(closed, "the request-owning endpoint to stop listening"));
    }

    [AvaloniaFact(Skip = AspireApp.AppHostSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsUnavailable))]
    public void Canceling_a_window_start_ends_the_pending_AppHost_and_its_owned_endpoint()
    {
        MainWindow window = project.OpenHost(languageServer: false);
        Choose(window);
        using CancellationTokenSource cancel = new();
        HashSet<int> observed = [];
        window.AspireRuntime.Changed += (_, _) => observed.UnionWith(OwnedPids(window));
        Task<string?> starting = window.Builds.RunAsync(cancellation: cancel.Token);
        Ui.PumpUntil(() => window.AspireRuntime.Apps.FirstOrDefault() is { Runner.IsRunning: true }
            && File.Exists(AspireApp.ConnectionInfoPath(project.Host)), "the real AppHost process to record its inherited connection before startup completes", TimeSpan.FromMinutes(3));
        Assert.Single(window.AspireRuntime.Apps);
        Assert.False(starting.IsCompleted, "Cancellation must happen during startup, not after a successful launch");
        int port = AspireApp.ReadConnectionInfo(project.Host).Port;
        cancel.Cancel();
        Assert.NotNull(Ui.AwaitPumped(starting, "the canceled window launch to finish"));
        Ui.WaitFor(() => window.AspireRuntime.Apps.Count == 0 && observed.All(id => !Processes.IsRunning(id)), "cancellation to clean up the AppHost and every observed service");
        using HttpClient http = Client();
        Task<HttpResponseMessage> request = http.GetAsync($"http://127.0.0.1:{port}/info");
        Assert.Throws<HttpRequestException>(() => Ui.AwaitPumped(request, "the canceled start's listener to refuse HTTP"));
        window.Close();
    }

    private static ComboBox? ServiceChoice(RunConfigurationsDialog dialog, string resource) =>
        dialog.AspireTable.Rows.SingleOrDefault(row => row.Service == resource)?.Action;

    private static void EditSource(MainWindow window, string path, Func<string, string> change)
    {
        Assert.Null(window.OpenFile(path));
        Ui.Settle();
        OpenDocument? document = window.EditorArea.Active;
        Assert.NotNull(document);
        Assert.True(PathNames.Same(path, document.Path), $"Requested {path}, but the active document is {document.Path}");
        Assert.Same(document.Text, window.EditorArea.CodeEditor.Document);
        string before = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
        string after = change(before);
        Assert.NotEqual(before, after);
        Ui.Click(window.EditorArea.CodeEditor);
        Assert.Same(document, window.EditorArea.Active);
        Assert.Same(document.Text, window.EditorArea.CodeEditor.Document);
        Ui.Run(window, Passenger.Editor.EditorActions.SelectAll);
        Ui.Await(Complete(window.Clipboard!.SetTextAsync(after)), "the edited source on the clipboard");
        Assert.Same(document, window.EditorArea.Active);
        Assert.Same(document.Text, window.EditorArea.CodeEditor.Document);
        Ui.Run(window, Passenger.Editor.EditorActions.Paste);
        string expected = LineEndings.In(after, window.EditorArea.CodeEditor.Document.LineEnding);
        Ui.WaitFor(() => window.EditorArea.CodeEditor.Document.Snapshot.ToString() == expected, "the multiline source paste");
        Assert.Same(document, window.EditorArea.Active);
        Assert.True(PathNames.Same(path, document.Path));
        Assert.Same(document.Text, window.EditorArea.CodeEditor.Document);
        Assert.Equal(expected, window.EditorArea.CodeEditor.Document.Snapshot.ToString());
    }

    /// <summary>Chooses a resource in the Aspire window's Resources page by a click on its name, as a person does.</summary>
    private static void ChooseResource(MainWindow window, string name)
    {
        FindAction(window, "Show Aspire Resources");
        Ui.WaitFor(() => window.Aspire.Rows.Any(row => row.Resource.DisplayName == name), $"{name}'s row in the Aspire window");
        Ui.Click(AspireStandInTests.Words(AspireStandInTests.Row(window, name), name));
        Assert.Equal(name, window.Aspire.SelectedResource?.DisplayName);
    }

    /// <summary>Runs the chosen resource's command as a person does: the toolbar's commands button, then its item.</summary>
    private static void RunCommand(MainWindow window, string title)
    {
        Ui.Click(window.AspireWindow.Tools[AppActions.AspireCommand]);
        Ui.Choose(window.GetVisualDescendants().OfType<ContextMenu>().FirstOrDefault(menu => menu.IsOpen), title);
    }

    /// <summary>The AppHost's prompt of that title, shown over the window; one that does not come names what the window said
    /// and shows instead.</summary>
    private static AspireInteractionDialog Prompt(MainWindow window, string title)
    {
        try
        {
            return AspireStandInTests.Interaction(window, title);
        }
        catch (TimeoutException error)
        {
            string shown = string.Join("; ", window.OwnedWindows.Select(owned => $"{owned.GetType().Name} '{owned.Title}'{(owned.IsVisible ? "" : " (hidden)")}"));
            throw new TimeoutException($"{error.Message}\nStatus bar: {window.Status}\nAspire window: {window.Aspire.Status}\nOver the window: {shown}", error);
        }
    }

    private static void FindAction(MainWindow window, string title)
    {
        Ui.Run(window, AppActions.FindAction);
        window.KeyTextInput(title);
        Ui.Settle();
        Passenger.App.Navigation.ActionPopup popup = Assert.IsType<Passenger.App.Navigation.ActionPopup>(window.Popup);
        Assert.Equal(title, popup.Rows[0].Title);
        Ui.Press(window, "ENTER");
    }

    private void WaitNativeServices(MainWindow window)
    {
        FindAction(window, "Show Aspire Resources");
        Ui.WaitFor(() => new[] { "apiservice", "webfrontend" }.All(name => window.Aspire.ResourceSnapshots.Any(resource =>
            resource.DisplayName == name && resource.State == "Running"
                && window.AspireRuntime.FindService(new(project.Host, resource))?.ProcessId is > 0)),
            "the native API and frontend resource stream", TimeSpan.FromMinutes(3));
    }

    private static void OpenGutter(MainWindow window, string resource)
    {
        Passenger.App.Workspace.OpenDocument document = window.EditorArea.Active!;
        int line = Array.FindIndex(document.Text.Snapshot.ToString().Split('\n'), text => text.Contains($"\"{resource}\"", StringComparison.Ordinal)
            && text.Contains("AddProject", StringComparison.Ordinal));
        Assert.True(line >= 0, $"No AddProject declaration for {resource}");
        Ui.WaitFor(() => document.RunMarks.Any(mark => mark.Line == line), $"the {resource} run gutter mark");
        Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
        editor.ScrollTo(new Vector(0, Math.Max(0, line * editor.LineHeight - editor.Bounds.Height / 3)));
        Ui.WaitFor(() => editor.IsEffectivelyVisible && editor.RunMarks.Any(mark => mark.Line == line)
            && editor.PointOf(document.Text.Snapshot.GetLineStart(line)).Y >= 0
            && editor.PointOf(document.Text.Snapshot.GetLineStart(line)).Y + editor.LineHeight < editor.Bounds.Height,
            $"the visible {resource} run gutter mark");
        double x = Enumerable.Range(0, 400).First(x => editor.IsOnGutterColumn(editor.Gutter!.Marks, new Point(x, 0))) + 3;
        Ui.Click(editor, new Point(x, editor.PointOf(document.Text.Snapshot.GetLineStart(line)).Y + editor.LineHeight / 2));
        Assert.NotNull(window.RunMarkMenu);
    }

    /// <summary>A record's row clicked, as a person chooses it: its item is kept through the snapshots streamed meanwhile
    /// (RunRows.Put), so the click lands on it.</summary>
    private static void ClickListItem(MainWindow window, ListBox list, object record)
    {
        object item = window.AspireWindow.Telemetry.ItemOf(record) ?? throw new InvalidOperationException($"{record} is not listed");
        list.ScrollIntoView(item);
        Ui.Settle();
        Ui.Click(Assert.IsType<ListBoxItem>(list.ContainerFromItem(item)));
        Assert.Same(item, list.SelectedItem);
    }

    /// <summary>The starter run from the toolbar in a window with no C# language server, which nothing here reads, once no
    /// program of an earlier test's is left.</summary>
    private MainWindow OpenRunning()
    {
        // An AppHost an earlier test left running holds the ports this one's would listen on, and its services would answer.
        Assert.True(Waiting.Until(() => Leftovers().Count == 0, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)),
            $"An earlier test left its AppHost's programs running:\n{string.Join("\n", Leftovers().Values)}");
        MainWindow window = project.OpenHost(languageServer: false);
        Choose(window);
        Ui.Run(window, AppActions.RunProgram);
        WaitNativeServices(window);
        return window;
    }

    private void Choose(MainWindow window)
    {
        Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Aspire && PathNames.Same(configuration.Project, project.Host)
            && configuration.LaunchProfile == "http"), "the generated HTTP Aspire configuration");
        Assert.True(window.SelectConfiguration($"Aspire: {AspireApp.Name}.AppHost: http"));
    }

    private void WaitServices(MainWindow window)
    {
        window.ShowToolWindow(ToolWindowIds.Aspire);
        RefreshUntil(window, () => new[] { "apiservice", "webfrontend" }.All(name =>
            window.Aspire.ResourceSnapshots.Any(resource => resource.DisplayName == name && resource.State == "Running"
                && window.AspireRuntime.FindService(new(project.Host, resource))?.ProcessId is > 0)), "the API and frontend's real DCP-owned processes");
    }

    private static void RefreshUntil(MainWindow window, Func<bool> ready, string what)
    {
        Task refresh = window.Aspire.RefreshAsync();
        try
        {
            Ui.PumpUntil(() =>
            {
                Task done = refresh;
                if (!done.IsCompleted)
                {
                    return false;
                }
                done.GetAwaiter().GetResult();
                if (ready())
                {
                    return true;
                }
                refresh = window.Aspire.RefreshAsync();
                return false;
            }, what, TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException error)
        {
            string resources = string.Join("; ", window.Aspire.ResourceSnapshots.Select(resource =>
            {
                JsonElement process = default;
                string pid = resource.Data.TryGetProperty("properties", out JsonElement properties)
                    && properties.TryGetProperty("executable.pid", out process) ? process.ToString() : "unknown";
                return $"{resource.Name}: {resource.State}, {resource.Source}, pid={pid}";
            }));
            string services = string.Join("; ", window.AspireRuntime.Apps.SelectMany(app => app.Services)
                .Select(service => $"{service.ResourceName}: {service.ProcessId}, {service.Project}"));
            throw new TimeoutException($"{error.Message}\nView: {window.Aspire.Status}\nResources: {resources}\nServices: {services}", error);
        }
        Ui.Settle();
    }

    /// <summary>What a failed wait names of the window: the Aspire window's visible words about telemetry, and what the
    /// AppHost's console said, its secrets masked.</summary>
    private static string TelemetryDiagnostics(MainWindow window)
    {
        string labels = string.Join("\n", window.AspireWindow.GetVisualDescendants().OfType<TextBlock>()
            .Where(label => label.IsEffectivelyVisible).Select(label => label.Text)
            .Where(text => text is not null && (text.Contains("telemetry", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Live ·", StringComparison.Ordinal))));
        string diagnostic = Redacted($"Visible Aspire labels:\n{labels}\nAppHost output:\n{AppHostSaid(window)}");
        return diagnostic.Length > 16384 ? diagnostic[..2048] + "\n[output truncated]\n" + diagnostic[^14336..] : diagnostic;
    }

    /// <summary>The AppHost's console as the Aspire window shows it.</summary>
    private static string AppHostSaid(MainWindow window)
    {
        window.AspireRuntime.LastAppConsole?.Flush();
        return window.AspireRuntime.LastAppConsole?.Document.Snapshot.ToString() ?? "No AppHost output was retained";
    }

    private static string Redacted(string text)
    {
        text = Passenger.App.Containers.ContainerSecrets.InValue(text);
        text = Regex.Replace(text, @"\b[0-9a-fA-F]{64}\b", Passenger.App.Containers.ContainerSecrets.Masked);
        return Regex.Replace(text, @"(https?://[^?\s]+)\?[^\s]+", "$1?[redacted]", RegexOptions.IgnoreCase);
    }

    private MainWindow Keeping(string settings)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, Autosaves = false, WarmServer = project.Server };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(project.Folder);
        Ui.Settle();
        return window;
    }

    private static int BreakAt(MainWindow window, string path, string statement)
    {
        int line = Array.FindIndex(File.ReadAllLines(path), text => text.Contains(statement, StringComparison.Ordinal));
        Assert.True(line >= 0, $"The starter has no {statement} in {path}");
        window.OpenFile(path);
        Ui.Settle();
        window.EditorArea.CodeEditor.GoTo(new TextPosition(line, 0));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        return line;
    }

    private static void WaitStopped(DebugTab tab, string path, int line)
    {
        Ui.WaitFor(() => tab.Session is { State: DebugState.Paused, StoppedThread: not null }, $"the request breakpoint in {path}:{line + 1}");
        DebugSession session = Assert.IsType<DebugSession>(tab.Session);
        IReadOnlyList<DapStackFrame> frames = Ui.Await(session.StackTraceAsync(session.StoppedThread!.Value), "the paused service's own stack");
        DapStackFrame top = Assert.IsType<DapStackFrame>(frames.FirstOrDefault());
        Assert.Equal(line + 1, top.Line);
        Assert.True(PathNames.Same(path, top.Source!.Path!), "The service stop must show its own source file");
    }

    private static AspireResource Resource(MainWindow window, string name) => Assert.Single(window.Aspire.ResourceSnapshots, resource => resource.DisplayName == name);
    private AspireResourceRun Service(MainWindow window, AspireResource resource) => Assert.IsType<AspireResourceRun>(window.AspireRuntime.FindService(new(project.Host, resource)));
    /// <summary>Each program of the fixture's AppHosts now running: a service or AppHost by its folder, the orchestrator by the
    /// run's Aspire home its notification socket is in.</summary>
    private Dictionary<int, string> Leftovers()
    {
        string folder = PathNames.Canonical(project.Folder);
        return Processes.Listed().Where(process => process.Value.Contains(AspireCli.Home, StringComparison.Ordinal)
            || process.Value.Contains(folder, StringComparison.Ordinal) && (process.Value.Contains("dotnet run", StringComparison.Ordinal)
                || process.Value.Contains("/bin/Debug/", StringComparison.Ordinal)))
            .ToDictionary(process => process.Key, process => $"{process.Key}: {process.Value}");
    }

    /// <summary>After each test, its windows closed: one that left its AppHost's programs running fails, naming them, and they
    /// are ended, so the next test's AppHost has its ports and the class goes on.</summary>
    public void Dispose()
    {
        if (project.WhyNot is not null || Waiting.Until(() => Leftovers().Count == 0, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)))
        {
            return;
        }
        Dictionary<int, string> left = Leftovers();
        foreach (int id in left.Keys)
        {
            try
            {
                using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(id);
                process.Kill();
            }
            catch (Exception ended) when (ended is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone meanwhile.
            }
        }
        Assert.Fail($"The test's AppHost's programs outlived its window, and were ended:\n{string.Join("\n", left.Values)}");
    }

    /// <summary>The Aspire window given the room its lists need, as a person makes it: a resource chosen in it, and the window
    /// maximized (Maximize Tool Window), since its lists share a short bottom panel with their details.</summary>
    private static void Roomy(MainWindow window)
    {
        ChooseResource(window, "apiservice");
        Ui.Run(window, AppActions.MaximizeToolWindow);
        Ui.WaitFor(() => window.AspireWindow.Bounds.Height > 400, "the Aspire window maximized");
    }

    /// <summary>A notification connection of a DCP instance to the IDE's execution endpoint, open until disposed or aborted.</summary>
    private static ClientWebSocket Notifications((int Port, string Token) endpoint, string instance, string what)
    {
        ClientWebSocket socket = new();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + endpoint.Token);
        socket.Options.SetRequestHeader("Microsoft-Developer-DCP-Instance-ID", instance);
        Ui.Await(Complete(socket.ConnectAsync(new Uri($"ws://127.0.0.1:{endpoint.Port}/run_session/notify?api-version=2024-03-03"),
            TestContext.Current.CancellationToken)), what);
        return socket;
    }

    private static int[] OwnedPids(MainWindow window) => [.. window.AspireRuntime.Apps.SelectMany(app =>
        new[] { app.ProcessId, app.Runner.ProcessId }.Concat(app.Services.Select(service => service.ProcessId))).OfType<int>().Distinct()];
    private static Uri HttpUrl(AspireResource resource) => resource.Data.GetProperty("urls").EnumerateArray()
        .Select(url => new Uri(url.GetProperty("url").GetString()!)).First(url => url.Scheme == "http");
    private static HttpClient Client() => new() { Timeout = TimeSpan.FromMinutes(2).Scaled() };
    private static async Task<bool> Complete(Task task) { await task; return true; }

    private static HttpResponseMessage Send(HttpClient http, Uri root, HttpMethod method, string path, string token, string? instance, string? body = null)
    {
        using HttpRequestMessage request = new(method, new Uri(root, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (instance is not null)
        {
            request.Headers.Add("Microsoft-Developer-DCP-Instance-ID", instance);
        }
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        return Ui.Await(http.SendAsync(request), "the running AppHost's DCP HTTP response");
    }
}
