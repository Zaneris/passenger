using Passenger.App.Aspire;
using Passenger.App.ToolWindows;
using Passenger.Core;
using A = Passenger.App.AppActions;

namespace Passenger.App;

public partial class MainWindow
{
    private void AddAspireActions()
    {
        Add(A.AspireDiscover, "Discover Aspire AppHosts", "Aspire", context =>
        {
            ShowToolWindow(ToolWindowIds.Aspire);
            IdeErrors.Observe(WorkingAsync(A.AspireDiscover, "Discovering AppHosts", async () =>
            {
                await Aspire.RefreshAppsAsync();
                return (true, Aspire.Status);
            }));
        }, NeedsAspire);
        Add(A.AspireRefresh, "Refresh Aspire Resources", "Aspire", context =>
        {
            ShowToolWindow(ToolWindowIds.Aspire);
            IdeErrors.Observe(WorkingAsync(A.AspireRefresh, "Refreshing resources", async () =>
            {
                await Aspire.RequestRefreshAsync();
                return (true, Aspire.Status);
            }));
        },
            c => NeedsAspire(c) ?? (Aspire.SelectedApp is null ? "No Aspire AppHost is selected" : null));
        // A resource's Debug, Attach, Run without Debugger and Profile are every run of services' (AddServiceActions).
        Add(A.AspireCommand, "Run Aspire Resource Command…", "Aspire", context =>
        {
            if (Aspire.TakePendingCommand() is { } command)
            {
                IdeErrors.Observe(WorkingAsync(A.AspireCommand, "Running resource command", async () =>
                {
                    await Aspire.ExecuteSelectedCommandAsync(command);
                    return (true, Aspire.Status);
                }));
            }
            else
            {
                ShownAspire().ShowCommandChoices();
            }
        }, NeedsAspireResource);
        Add(A.AspireLogFind, "Find in Aspire Logs", "Aspire", _ => { ShowToolWindow(ToolWindowIds.Aspire); Aspire.ShowPage(RunWindows.LogTab); AspireWindow.OpenFind(); }, NeedsAspire);
        Add(A.AspireShowTrace, "Show Aspire Log Trace", "Aspire", _ => { ShowToolWindow(ToolWindowIds.Aspire); AspireWindow.ShowSelectedTrace(); },
            c => NeedsAspire(c) ?? (aspireView?.Telemetry.SelectedLog is { TraceId.Length: > 0 } ? null : "No log with a trace is selected"));
        Add(A.AspireShowRelatedLogs, "Show Related Aspire Logs", "Aspire", _ => { ShowToolWindow(ToolWindowIds.Aspire); AspireWindow.ShowRelatedLogs(); },
            c => NeedsAspire(c) ?? (aspireView?.Telemetry is { SelectedTrace: not null, SelectedSpan: not null } ? null : "No trace span is selected"));
        foreach (string page in AspireModel.Pages)
        {
            // Details is the chosen resource's, beside every resource listed; each found by its tab's former name too, Stats by what it charts.
            string id = AspireModel.PageAction(page);
            Add(id, id == A.AspireResources ? "Show Aspire Resources" : $"Show Aspire {page}", "Aspire", _ =>
            {
                ShowToolWindow(ToolWindowIds.Aspire);
                Aspire.ShowPage(page);
            }, NeedsAspire, words: id switch
            {
                A.AspireResources => ["Show Aspire Details"],
                A.AspireConsole => ["Show Aspire Console"],
                A.AspireAppHostConsole => ["Show Aspire AppHost console"],
                A.AspireStats => ["Show Aspire Metrics"],
                A.AspireGraph => ["Show Aspire Resource Graph", "Aspire Dependencies"],
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
            }, NeedsAspire);
        }
        // The graph as drawn, for a document: Mermaid's flowchart, which renders where Markdown does.
        Add(A.AspireGraphMermaid, "Copy Aspire Graph as Mermaid", "Aspire", _ =>
        {
            if (aspireView?.Graph.Mermaid() is { } flowchart)
            {
                IdeErrors.Observe(CopyAsync(flowchart, $"Copied the graph of {Plural.Of(aspireView.Graph.Nodes.Count, "resource")} as a Mermaid flowchart"));
            }
        }, c => NeedsAspire(c) ?? (aspireView?.Graph.Nodes.Count is > 0 ? null : "The Aspire graph shows no resources"), words: ["Export Aspire Graph as Mermaid"]);
        Add(A.AspireOpenLink, "Open Aspire Resource Endpoint", "Aspire", _ =>
        {
            if (Aspire.TakePendingLink() is { Scheme: "http" or "https" } link)
            {
                BrowsePage(link);
            }
            else
            {
                ShownAspire().ShowEndpointChoices();
            }
        }, NeedsAspire);
        Add(A.AspireSource, "Open Aspire Resource Source", "Aspire", _ =>
        {
            if (Aspire.SelectedResource?.Source is { } source && Path.IsPathFullyQualified(source))
            {
                GoToAspireSource(new FileLink(source, new Passenger.Text.TextPosition(0, 0)));
            }
        },
            c => NeedsAspire(c) ?? (Aspire.SelectedResource?.Source is { } source && Path.IsPathFullyQualified(source) ? null : "No resource source is selected"));
        Add(A.AspireContainer, "Show Aspire Resource Container", "Aspire", _ =>
        {
            if (Aspire.SelectedContainerId is { } id)
            {
                Aspire.SelectContainer?.Invoke(id);
            }
        }, c => NeedsAspire(c) ?? (Aspire.SelectedContainerId is null ? "No container resource is selected"
            : Aspire.SelectContainer is null ? "Container selection is unavailable" : null));
    }

    private string? NeedsAspireResource(ActionContext context) => NeedsAspire(context) ?? (Aspire.Selection is null ? "No Aspire resource is selected" : null);

    /// <summary>Why an action of the Aspire window cannot run: the window serves nothing in the open folder.</summary>
    private string? NeedsAspire(ActionContext _) => Unserved(ToolWindowIds.Aspire);

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
