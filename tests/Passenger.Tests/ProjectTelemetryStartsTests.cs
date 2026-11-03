using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Telemetry;

namespace Passenger.Tests;

/// <summary>A project run that collects its logs and traces does so however it starts: Debug and Profile tell its program
/// of its dashboard through the same plan Run does, and Stop takes the dashboard down with the program. Alone, as it
/// debugs and profiles through the window; against a Linux engine that already holds the dashboard's image.</summary>
[Collection(Alone.Name)]
public sealed class ProjectTelemetryStartsTests
{
    [AvaloniaTheory]
    [InlineData(AppActions.Debug)]
    [InlineData(AppActions.Profile)]
    public void A_project_debugged_or_profiled_sends_its_logs_to_its_dashboard_and_Stop_takes_the_dashboard_down(string start)
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        Assert.SkipWhen(!Ui.AwaitPumped(Waiting.Apart(() => ContainerEngine.Shared.RunAsync(["image", "inspect", ContainerTelemetry.Image],
            TimeSpan.FromSeconds(30).Scaled())), $"docker image inspect {ContainerTelemetry.Image}", TimeSpan.FromSeconds(30) + TestFolders.Spare).Succeeded,
            $"The engine lacks the dashboard's image, which no test pulls: pull {ContainerTelemetry.Image} to run it");
        (string folder, int port) = ProjectTelemetryTests.Shop();
        MainWindow window = ContainerRunsTests.Open(folder);
        ProjectTelemetryTests.Collect(window);
        string? project = null;
        try
        {
            string? refused = Ui.Await(start == AppActions.Debug ? window.DebugAsync() : window.ProfileAsync(), $"{start} to start the project with its dashboard",
                TimeSpan.FromMinutes(5));
            Assert.True(refused is null, $"{refused}{Ui.BuildErrors(window)}");
            RunDashboard dashboard = Assert.Single(window.Session.Dashboards);
            project = dashboard.ProjectName;
            string? answered = null;
            Assert.True(Ui.Pumped(() => (answered = ProjectTelemetryTests.Answer(port)) == "in stock", TimeSpan.FromMinutes(1)), $"web did not answer: {answered}");
            window.ShowToolWindow(ToolWindowIds.Containers);
            ContainersView view = window.ContainersWindow;
            Ui.WaitFor(() => view.Choose(row => row.Key == $"project:{project}"), "the dashboard's project in the Containers window", TimeSpan.FromMinutes(1));
            ToggleButton tab = view.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "containersTelemetryTab");
            Ui.WaitFor(() => tab.IsVisible, "the Telemetry tab of a run that collects telemetry");
            Ui.Click(tab);
            Ui.WaitFor(() => ProjectTelemetryTests.Logged(view.Telemetry) is not null,
                $"web's log of its order at its dashboard under {start} ({view.Telemetry.LogsStatus})", TimeSpan.FromMinutes(1));
            Assert.StartsWith("shop-web", ProjectTelemetryTests.Logged(view.Telemetry)!.Data.GetProperty("resourceName").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            if (project is not null)
            {
                Docker.TakenDown(project, $"Stop to take the dashboard down with the program {start} started");
            }
        }
        Assert.Equal(string.Empty, Docker.Run("network", "ls", "-q", "--filter", $"label=com.docker.compose.project={project}"));
        Ui.WaitFor(() => window.Session.Dashboards.Count == 0, "the session to let go of the dashboard");
        window.Close();
    }
}
