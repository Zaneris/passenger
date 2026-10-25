using Passenger.App.Aspire;
using Passenger.App.ToolWindows;
using A = Passenger.App.AppActions;

namespace Passenger.App;

public partial class MainWindow
{
    private void AddAspireActions()
    {
        Add(A.AspireDiscover, "Discover Aspire AppHosts", "Aspire", context =>
        {
            ShowToolWindow(ToolWindowIds.Aspire);
            IdeErrors.Observe(WorkingAsync(A.AspireDiscover, "Discovering AppHosts", () => Aspire.RefreshAppsAsync()));
        });
        Add(A.AspireRefresh, "Refresh Aspire Resources", "Aspire", context =>
        {
            ShowToolWindow(ToolWindowIds.Aspire);
            IdeErrors.Observe(WorkingAsync(A.AspireRefresh, "Refreshing resources", () => Aspire.RequestRefreshAsync()));
        },
            _ => Aspire.SelectedApp is null ? "No Aspire AppHost is selected" : null);
        // A resource's Debug, Attach, Run without Debugger and Profile are every run of services' (AddServiceActions).
        Add(A.AspireCommand, "Run Aspire Resource Command…", "Aspire", context =>
        {
            if (Aspire.TakePendingCommand() is { } command)
            {
                IdeErrors.Observe(WorkingAsync(A.AspireCommand, "Running resource command", () => Aspire.ExecuteSelectedCommandAsync(command)));
            }
            else
            {
                ShownAspire().ShowCommandChoices();
            }
        }, NeedsAspireResource);
        Add(A.AspireLogFind, "Find in Aspire Logs", "Aspire", _ => { ShowToolWindow(ToolWindowIds.Aspire); Aspire.ShowPage(RunWindows.LogTab); AspireWindow.OpenFind(); });
        Add(A.AspireShowTrace, "Show Aspire Log Trace", "Aspire", _ => { ShowToolWindow(ToolWindowIds.Aspire); AspireWindow.ShowSelectedTrace(); },
            _ => aspireView?.Telemetry.SelectedLog is { TraceId.Length: > 0 } ? null : "No log with a trace is selected");
        Add(A.AspireShowRelatedLogs, "Show Related Aspire Logs", "Aspire", _ => { ShowToolWindow(ToolWindowIds.Aspire); AspireWindow.ShowRelatedLogs(); },
            _ => aspireView?.Telemetry is { SelectedTrace: not null, SelectedSpan: not null } ? null : "No trace span is selected");
        foreach (string page in AspireModel.Pages)
        {
            // Details is the chosen resource's, beside every resource listed; each found by its tab's former name too.
            string id = AspireModel.PageAction(page);
            Add(id, id == A.AspireResources ? "Show Aspire Resources" : $"Show Aspire {page}", "Aspire", _ =>
            {
                ShowToolWindow(ToolWindowIds.Aspire);
                Aspire.ShowPage(page);
            }, words: id switch
            {
                A.AspireResources => ["Show Aspire Details"],
                A.AspireConsole => ["Show Aspire Console"],
                A.AspireAppHostConsole => ["Show Aspire AppHost console"],
                _ => null,
            });
        }
        foreach ((string id, string title, Passenger.App.Telemetry.TelemetryPage page) in (ReadOnlySpan<(string, string, Passenger.App.Telemetry.TelemetryPage)>)[
            (A.AspireLogs, "Show Aspire Structured logs", Passenger.App.Telemetry.TelemetryPage.Logs), (A.AspireTraces, "Show Aspire Traces", Passenger.App.Telemetry.TelemetryPage.Traces)])
        {
            Add(id, title, "Aspire", _ =>
            {
                ShowToolWindow(ToolWindowIds.Aspire);
                AspireWindow.ShowTelemetry(page);
            });
        }
        Add(A.AspireOpenLink, "Open Aspire Resource Endpoint", "Aspire", _ =>
        {
            if (Aspire.TakePendingLink() is { Scheme: "http" or "https" } link)
            {
                Browse(link);
            }
            else
            {
                ShownAspire().ShowEndpointChoices();
            }
        });
        Add(A.AspireSource, "Open Aspire Resource Source", "Aspire", _ =>
        {
            if (Aspire.SelectedResource?.Source is { } source && Path.IsPathFullyQualified(source))
            {
                OpenLink(new FileLink(source, new Passenger.Text.TextPosition(0, 0)));
            }
        },
            _ => Aspire.SelectedResource?.Source is { } source && Path.IsPathFullyQualified(source) ? null : "No resource source is selected");
        Add(A.AspireContainer, "Show Aspire Resource Container", "Aspire", _ =>
        {
            if (Aspire.SelectedContainerId is { } id)
            {
                Aspire.SelectContainer?.Invoke(id);
            }
        }, _ => Aspire.SelectedContainerId is null ? "No container resource is selected"
            : Aspire.SelectContainer is null ? "Container selection is unavailable" : null);
    }

    private string? NeedsAspireResource(ActionContext _) => Aspire.Selection is null ? "No Aspire resource is selected" : null;

    /// <summary>The Aspire window's view, shown first when it is hidden: a menu of the chosen resource's hangs from its toolbar.</summary>
    private AspireView ShownAspire()
    {
        if (!Dock.IsShown(ToolWindowIds.Aspire))
        {
            ShowToolWindow(ToolWindowIds.Aspire);
        }
        return AspireWindow;
    }
}
