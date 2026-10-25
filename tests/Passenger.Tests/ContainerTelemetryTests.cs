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

/// <summary>A container run's logs and traces: a configuration that collects them runs Aspire's dashboard beside its
/// services (chosen in Edit Configurations, off until then), which its .NET service sends OpenTelemetry to; the
/// Containers window's Telemetry tab and an agent's <c>telemetry</c> read them, and Stop leaves nothing of the
/// dashboard. Against a Linux engine, only where it already holds the dashboard's and the runtime's images, which no
/// test pulls; the app restores from the package cache alone.</summary>
public sealed class ContainerTelemetryTests : McpRig
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    [AvaloniaFact]
    public void A_compose_runs_service_logs_and_traces_show_in_its_Telemetry_tab_and_reach_an_agent_and_Stop_leaves_nothing_of_the_dashboard()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string[] missing = [.. ((string[])[ContainerTelemetry.Image, "mcr.microsoft.com/dotnet/aspnet:10.0"])
            .Where(image => !Task.Run(() => ContainerEngine.Shared.RunAsync(["image", "inspect", image], TimeSpan.FromSeconds(30).Scaled())).GetAwaiter().GetResult().Succeeded)];
        Assert.SkipWhen(missing.Length > 0, $"The engine lacks images the run uses, which no test pulls: pull {string.Join(" and ", missing)} to run it");
        (string folder, string project) = Shop();
        MainWindow window = ContainerRunsTests.Open(folder);
        const string Name = "Compose: compose.yaml";
        // Off until chosen in Edit Configurations, since its image is pulled the first time.
        Assert.False(window.Configurations.Single(c => c.Name == Name).Telemetry, "A container run collects telemetry unasked");
        RunConfigurationsDialog dialog = DialogButtonsTests.Check(window.ShowRunConfigurations());
        Assert.True(dialog.Choose(Name));
        Assert.True(dialog.TelemetryBox.IsVisible, "The Compose page offers no choice to collect logs and traces");
        Ui.Click(dialog.TelemetryBox);
        Ui.Click(dialog.OkButton);
        Assert.True(window.Configurations.Single(c => c.Name == Name).Telemetry, "Edit Configurations' choice to collect logs and traces was not kept");
        Assert.True(window.SelectConfiguration(Name));
        McpClient? client = null;
        try
        {
            string? refused = Ui.Await(window.RunAsync(), "the Compose application to come up with its dashboard", TimeSpan.FromMinutes(5));
            Assert.True(refused is null, $"{refused}{Ui.BuildErrors(window)}\n{ContainerRunsTests.Run(window)}");
            string dashboard = ContainerRunsTests.Container(project, ContainerTelemetry.Service);
            Assert.Equal(ContainerLabels.StateValue, Docker.Run("inspect", "-f", "{{index .Config.Labels \"net.passenger-ide.state\"}}", dashboard));
            // Its OTLP endpoint is the run's network's alone: only the API, on this machine's loopback, is published.
            Assert.Equal("127.0.0.1", Docker.Run("compose", "-p", project, "port", ContainerTelemetry.Service, "18888").Split(':')[0]);
            Assert.Equal(string.Empty, Docker.Said("compose", "-p", project, "port", ContainerTelemetry.Service, "18889") ?? string.Empty);

            int port = ContainerRunsTests.Published(project, "web");
            string? answered = null;
            Assert.True(Ui.Pumped(() => (answered = Answer(port)) == "in stock", TimeSpan.FromMinutes(1)), $"web did not answer: {answered}\n{ContainerRunsTests.Run(window)}");

            Ui.WaitFor(() => ContainerRunsTests.Run(window).Contains("Now listening on", StringComparison.Ordinal), "web's log in the Run window", TimeSpan.FromMinutes(1));
            Assert.DoesNotContain("Login to the dashboard", ContainerRunsTests.Run(window), StringComparison.Ordinal);

            window.ShowToolWindow(ToolWindowIds.Containers);
            ContainersView view = window.ContainersWindow;
            Ui.WaitFor(() => view.Choose(row => row.Key == $"service:{project}/web"), "web's row in the Containers window", TimeSpan.FromMinutes(1));
            ToggleButton tab = view.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "containersTelemetryTab");
            Ui.WaitFor(() => tab.IsVisible, "the Telemetry tab of a run that collects telemetry");
            Ui.Click(tab);
            Ui.WaitFor(() => Logged(view.Telemetry) is not null, $"web's log in the Telemetry tab ({view.Telemetry.LogsStatus})", TimeSpan.FromMinutes(1));
            AspireStructuredLog log = Logged(view.Telemetry)!;
            Assert.StartsWith("web", log.Data.GetProperty("resourceName").GetString(), StringComparison.Ordinal);
            Assert.False(string.IsNullOrEmpty(log.TraceId), "web's log has no trace, though it was written handling a request");
            view.Telemetry.StructuredLogs.ScrollIntoView(view.Telemetry.ItemOf(log)!);
            Ui.Settle();
            Ui.Click(Assert.IsType<ListBoxItem>(view.Telemetry.StructuredLogs.ContainerFromItem(view.Telemetry.ItemOf(log)!)));
            Ui.Click(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == AppActions.ContainersShowTrace && b.IsEffectivelyVisible));
            Ui.WaitFor(() => view.Telemetry.Shown == TelemetryPage.Traces && view.Telemetry.SelectedTrace is { Spans.Count: >= 3 } chosen
                && chosen.TraceId == log.TraceId && view.Telemetry.SelectedSpan?.Span.SpanId == log.SpanId,
                $"the request's trace, with web's request to itself, its log's span chosen ({view.Telemetry.TracesStatus})", TimeSpan.FromMinutes(1));

            client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
            Task<JsonElement> logs = Task.Run(() => Call(client, McpTools.Telemetry, new() { ["resource"] = "web", ["limit"] = 50 }));
            Press(Admission(window, "the window to ask whether the agent may work there"), AgentAdmissionDialog.Allow);
            JsonElement read = Await(logs, "the agent's telemetry of web's logs");
            Assert.Contains(read.GetProperty("logs").EnumerateArray(), item => item.GetProperty("message").GetString()?.Contains("Passenger order 42", StringComparison.Ordinal) == true);
            JsonElement traced = Await(Task.Run(() => Call(client, McpTools.Telemetry, new() { ["kind"] = "traces", ["traceId"] = log.TraceId })), "the agent's telemetry of the request's trace");
            Assert.True(Assert.Single(traced.GetProperty("traces").EnumerateArray()).GetProperty("spans").GetArrayLength() >= 3, traced.ToString());
        }
        finally
        {
            if (client is not null)
            {
                Gone(client);
            }
            Docker.Stopped(window, project, "Stop to take the run's containers down, its dashboard's with them");
        }
        Assert.Equal(string.Empty, Docker.Run("ps", "-aq", "--filter", $"label=com.docker.compose.project={project}"));
        Assert.Equal(string.Empty, Docker.Run("network", "ls", "-q", "--filter", $"label=com.docker.compose.project={project}"));
        Ui.WaitFor(() => !window.ContainersWindow.GetVisualDescendants().OfType<ToggleButton>().Any(t => t.Name == "containersTelemetryTab" && t.IsVisible),
            "the Telemetry tab to go with the run");
        window.Close();
    }

    /// <summary>web's log of the order it placed, once the Telemetry tab lists it.</summary>
    private static AspireStructuredLog? Logged(TelemetryView telemetry) =>
        telemetry.Logs.FirstOrDefault(log => log.ToString().Contains("Passenger order 42", StringComparison.Ordinal));

    private static string? Answer(int port)
    {
        try
        {
            return Task.Run(() => Http.GetStringAsync(new Uri($"http://localhost:{port.ToString(CultureInfo.InvariantCulture)}/"))).GetAwaiter().GetResult();
        }
        catch (Exception unanswered) when (unanswered is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>A compose app of one ASP.NET Core service that sends OpenTelemetry where it is told (OTLP, as an Aspire
    /// service's defaults do), its packages the package cache's (the run's local feed its only source); its Compose project
    /// named for the test run.</summary>
    private static (string Folder, string Project) Shop()
    {
        string folder = TestFolders.Create("ctr-telemetry-");
        string project = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}telemetry";
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
            app.MapGet("/", async (ILogger<Program> log, IHttpClientFactory clients) =>
            {
                log.LogInformation("Passenger order {Order} placed", 42);
                return await clients.CreateClient().GetStringAsync("http://localhost:8080/stock");
            });
            app.MapGet("/stock", () => "in stock");
            app.Run();

            """);
        TestFolders.Write(folder, "Web/Dockerfile", """
            FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
            USER $APP_UID
            WORKDIR /app
            EXPOSE 8080

            FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
            WORKDIR /src
            COPY . .
            RUN dotnet publish "Web/Web.csproj" -c Release -o /app/publish

            FROM base AS final
            WORKDIR /app
            COPY --from=build /app/publish .
            ENTRYPOINT ["dotnet", "Web.dll"]

            """);
        TestFolders.Write(folder, "compose.yaml", $$"""
            name: {{project}}
            services:
              web:
                build: { context: ., dockerfile: Web/Dockerfile }
                ports: ["8080"]

            """);
        AvaloniaApp.UseFeed(folder);
        return (PathNames.Canonical(folder), project);
    }
}
