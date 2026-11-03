using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using ModelContextProtocol.Client;
using Passenger.App;
using Passenger.App.Aspire;
using Passenger.App.Containers;
using Passenger.App.Mcp;
using Passenger.App.Run;
using Passenger.App.Telemetry;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A project run's logs and traces: a configuration that collects them (chosen in Edit Configurations, off until
/// then) has Aspire's dashboard brought up in a container while its program runs, which the program is told to send
/// OpenTelemetry to, its launch profile's own variables kept; the Containers window's Telemetry tab and an agent's
/// <c>telemetry</c> read them, and Stop leaves nothing of the dashboard. Against a Linux engine, only where it already holds
/// the dashboard's image, which no test pulls; with none, the run says why and runs as it would. The app restores from the
/// package cache alone.</summary>
public sealed class ProjectTelemetryTests : McpRig
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    [AvaloniaFact]
    public void A_project_runs_logs_and_traces_show_in_its_dashboards_Telemetry_tab_and_reach_an_agent_and_Stop_leaves_nothing_of_the_dashboard()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        Assert.SkipWhen(!Ui.AwaitPumped(Waiting.Apart(() => ContainerEngine.Shared.RunAsync(["image", "inspect", ContainerTelemetry.Image], TimeSpan.FromSeconds(30).Scaled())),
            $"docker image inspect {ContainerTelemetry.Image}", TimeSpan.FromSeconds(30) + TestFolders.Spare).Succeeded,
            $"The engine lacks the dashboard's image, which no test pulls: pull {ContainerTelemetry.Image} to run it");
        (string folder, int port) = Shop();
        MainWindow window = ContainerRunsTests.Open(folder);
        Collect(window);
        McpClient? client = null;
        string? project = null;
        try
        {
            string? refused = Ui.Await(window.RunAsync(), "the project to run with its dashboard", TimeSpan.FromMinutes(5));
            Assert.True(refused is null, $"{refused}{Ui.BuildErrors(window)}\n{ContainerRunsTests.Run(window)}");
            RunDashboard dashboard = Assert.Single(window.Session.Dashboards);
            project = dashboard.ProjectName;
            string container = ContainerRunsTests.Container(project, ContainerTelemetry.Service);
            Assert.Equal(ContainerLabels.StateValue, Docker.Run("inspect", "-f", "{{index .Config.Labels \"net.passenger-ide.state\"}}", container));
            Assert.Equal(ContainerLabels.FolderValue(folder), Docker.Run("inspect", "-f", "{{index .Config.Labels \"net.passenger-ide.folder\"}}", container));
            // Its API and OTLP endpoints are this machine's loopback's alone.
            foreach (string inside in (string[])["18888", "18889", "18890"])
            {
                Assert.Equal("127.0.0.1", Docker.Run("port", container, inside).Split('\n')[0].Split(':')[0]);
            }
            string? answered = null;
            Assert.True(Ui.Pumped(() => (answered = Answer(port)) == "in stock", TimeSpan.FromMinutes(1)), $"web did not answer: {answered}\n{ContainerRunsTests.Run(window)}");
            string said = ContainerRunsTests.Run(window);
            Assert.Contains($"{Name}'s logs and traces go to Aspire's dashboard ({project})", said, StringComparison.Ordinal);
            Assert.Contains($"{Name} keeps its own {OtelVariables.ServiceName}, as its environment sets", said, StringComparison.Ordinal);

            window.ShowToolWindow(ToolWindowIds.Containers);
            ContainersView view = window.ContainersWindow;
            Ui.WaitFor(() => view.Choose(row => row.Key == $"project:{project}"), "the dashboard's project in the Containers window", TimeSpan.FromMinutes(1));
            ToggleButton tab = view.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "containersTelemetryTab");
            Ui.WaitFor(() => tab.IsVisible, "the Telemetry tab of a run that collects telemetry");
            Ui.Click(tab);
            Ui.WaitFor(() => Logged(view.Telemetry) is not null, $"web's log in the Telemetry tab ({view.Telemetry.LogsStatus})", TimeSpan.FromMinutes(1));
            AspireStructuredLog log = Logged(view.Telemetry)!;
            // Its records go by the name its launch profile gives, kept over Passenger's.
            Assert.StartsWith("shop-web", log.Data.GetProperty("resourceName").GetString(), StringComparison.Ordinal);
            Assert.False(string.IsNullOrEmpty(log.TraceId), "web's log has no trace, though it was written handling a request");
            view.Telemetry.StructuredLogs.ScrollIntoView(view.Telemetry.ItemOf(log)!);
            Ui.Settle();
            Ui.Click(Assert.IsType<ListBoxItem>(view.Telemetry.StructuredLogs.ContainerFromItem(view.Telemetry.ItemOf(log)!)));
            Ui.Click(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == AppActions.ContainersShowTrace && b.IsEffectivelyVisible));
            Ui.WaitFor(() => view.Telemetry.Shown == TelemetryPage.Traces && view.Telemetry.SelectedTrace is { Spans.Count: >= 3 } chosen
                && chosen.TraceId == log.TraceId && view.Telemetry.SelectedSpan?.Span.SpanId == log.SpanId,
                $"the request's trace, with web's request to itself, its log's span chosen ({view.Telemetry.TracesStatus})", TimeSpan.FromMinutes(1));

            client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
            Task<JsonElement> logs = Waiting.Apart(() => Call(client, McpTools.Telemetry, new() { ["limit"] = 50 }));
            Press(Admission(window, "the window to ask whether the agent may work there"), AgentAdmissionDialog.Allow);
            JsonElement read = Await(logs, "the agent's telemetry of the project run, the only run there is");
            Assert.Contains(read.GetProperty("logs").EnumerateArray(), item => item.GetProperty("message").GetString()?.Contains("Passenger order 42", StringComparison.Ordinal) == true);
            JsonElement named = Await(Waiting.Apart(() => Call(client, McpTools.Telemetry, new() { ["resource"] = "shop-web", ["kind"] = "traces", ["traceId"] = log.TraceId })),
                "the agent's telemetry of the request's trace, by the name its records go by");
            Assert.True(Assert.Single(named.GetProperty("traces").EnumerateArray()).GetProperty("spans").GetArrayLength() >= 3, named.ToString());
        }
        finally
        {
            if (client is not null)
            {
                Gone(client);
            }
            window.Perform(AppActions.Stop);
            if (project is not null)
            {
                Docker.TakenDown(project, "Stop to take the run's dashboard down with its program");
            }
        }
        Assert.Equal(string.Empty, Docker.Run("ps", "-aq", "--filter", $"label=com.docker.compose.project={project}"));
        Assert.Equal(string.Empty, Docker.Run("network", "ls", "-q", "--filter", $"label=com.docker.compose.project={project}"));
        Ui.WaitFor(() => window.Session.Dashboards.Count == 0, "the session to let go of the dashboard");
        Ui.WaitFor(() => !window.ContainersWindow.GetVisualDescendants().OfType<ToggleButton>().Any(t => t.Name == "containersTelemetryTab" && t.IsVisible),
            "the Telemetry tab to go with the run");
        window.Close();
    }

    [AvaloniaFact]
    public void A_project_run_that_collects_its_logs_and_traces_with_no_container_engine_says_why_and_runs_as_it_would()
    {
        (string folder, int port) = Shop();
        MainWindow window = ContainerRunsTests.Open(folder, engine: new ContainerEngine(cli: null, search: false));
        Collect(window);
        try
        {
            string? refused = Ui.Await(window.RunAsync(), "the project to run without a dashboard", TimeSpan.FromMinutes(5));
            Assert.True(refused is null, $"{refused}{Ui.BuildErrors(window)}\n{ContainerRunsTests.Run(window)}");
            string? answered = null;
            Assert.True(Ui.Pumped(() => (answered = Answer(port)) == "in stock", TimeSpan.FromMinutes(1)), $"web did not answer: {answered}\n{ContainerRunsTests.Run(window)}");
            string said = ContainerRunsTests.Run(window);
            const string Without = $"{Name} runs without its logs and traces collected: No docker command was found";
            Assert.True(said.Split(Without).Length == 2, $"The run did not say once why its logs and traces are not collected:\n{said}");
            Assert.Empty(window.Session.Dashboards);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning, "Stop to end the project's run");
        }
        window.Close();
    }

    internal const string Name = "Web: http";

    /// <summary>Its configuration's Collect logs and traces turned on in Edit Configurations, off until then, and it chosen.</summary>
    internal static void Collect(MainWindow window)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Name == Name), "the launch profile's configuration");
        // Off until chosen in Edit Configurations, since its image is pulled the first time.
        Assert.False(window.Configurations.Single(c => c.Name == Name).Telemetry, "A project run collects telemetry unasked");
        RunConfigurationsDialog dialog = DialogButtonsTests.Check(window.ShowRunConfigurations());
        Assert.True(dialog.Choose(Name));
        Assert.True(dialog.TelemetryBox.IsEffectivelyVisible, "A project's page offers no choice to collect logs and traces");
        Assert.Equal(RunConfigurationsDialog.TelemetryTip(containers: false), ToolTip.GetTip(dialog.TelemetryBox));
        Ui.Click(dialog.TelemetryBox);
        Ui.Click(dialog.OkButton);
        Assert.True(window.Configurations.Single(c => c.Name == Name).Telemetry, "Edit Configurations' choice to collect logs and traces was not kept");
        Assert.True(window.SelectConfiguration(Name));
    }

    /// <summary>web's log of the order it placed, once the Telemetry tab lists it.</summary>
    internal static AspireStructuredLog? Logged(TelemetryView telemetry) =>
        telemetry.Logs.FirstOrDefault(log => log.ToString().Contains("Passenger order 42", StringComparison.Ordinal));

    internal static string? Answer(int port)
    {
        try
        {
            return Ui.AwaitPumped(Waiting.Apart(() => Http.GetStringAsync(new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"))),
                $"the answer on port {port}", Http.Timeout + TestFolders.Spare);
        }
        catch (Exception unanswered) when (unanswered is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>One ASP.NET Core project that sends OpenTelemetry where it is told (OTLP, as an Aspire service's defaults do),
    /// its launch profile naming its port and the name its records go by, its packages the package cache's (the run's local
    /// feed its only source).</summary>
    internal static (string Folder, int Port) Shop()
    {
        string folder = TestFolders.Create("prj-telemetry-");
        int port = AspireApp.FreePort();
        string version = AvaloniaApp.Metadata("OpenTelemetryVersion");
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"Web/Web.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "Web/Web.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>").With($"""
              <ItemGroup>
                <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="{version}" />
                <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="{version}" />
                <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="{version}" />
                <PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="{version}" />
              </ItemGroup>

            """));
        TestFolders.Write(folder, "Web/Properties/launchSettings.json", $$"""
            {
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "applicationUrl": "http://127.0.0.1:{{port.ToString(CultureInfo.InvariantCulture)}}",
                  "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development", "{{OtelVariables.ServiceName}}": "shop-web" }
                }
              }
            }

            """);
        TestFolders.Write(folder, "Web/Program.cs", """
            using OpenTelemetry;
            using OpenTelemetry.Trace;

            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.Services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
                .WithLogging(configureBuilder: null, configureOptions: logging => logging.IncludeFormattedMessage = true)
                .UseOtlpExporter();
            builder.Services.AddHttpClient();
            WebApplication app = builder.Build();
            app.MapGet("/", async (HttpContext context, ILogger<Program> log, IHttpClientFactory clients) =>
            {
                log.LogInformation("Passenger order {Order} placed", 42);
                return await clients.CreateClient().GetStringAsync($"{context.Request.Scheme}://{context.Request.Host}/stock");
            });
            app.MapGet("/stock", () => "in stock");
            app.Run();

            """);
        AvaloniaApp.UseFeed(folder);
        return (PathNames.Canonical(folder), port);
    }
}
