using Passenger.App.Aspire;
using Passenger.App.ToolWindows;
using Passenger.Core;
using A = Passenger.App.AppActions;

namespace Passenger.App;

public partial class MainWindow
{
    private void AddAspireActions()
    {
        Add(A.AspireDiscover, ActionTitles.AspireDiscover, ActionGroups.Aspire, context =>
        {
            ShowToolWindow(ToolWindowIds.Aspire);
            IdeErrors.Observe(WorkingAsync(A.AspireDiscover, "Discovering AppHosts", async () =>
            {
                await Aspire.RefreshAppsAsync();
                return (true, Aspire.Status);
            }));
        }, NeedsAspire);
        Add(A.AspireRefresh, ActionTitles.AspireRefresh, ActionGroups.Aspire, context =>
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
        Add(A.AspireCommand, ActionTitles.AspireCommand, ActionGroups.Aspire, context =>
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
        Add(A.AspireLogFind, ActionTitles.AspireLogFind, ActionGroups.Aspire, _ => { ShowToolWindow(ToolWindowIds.Aspire); Aspire.ShowPage(RunWindows.LogTab); AspireWindow.OpenFind(); }, NeedsAspire);
        Add(A.AspireShowTrace, ActionTitles.AspireShowTrace, ActionGroups.Aspire, _ => { ShowToolWindow(ToolWindowIds.Aspire); AspireWindow.ShowSelectedTrace(); },
            c => NeedsAspire(c) ?? (aspireView?.Telemetry.SelectedLog is { TraceId.Length: > 0 } ? null : "No log with a trace is selected"));
        Add(A.AspireShowRelatedLogs, ActionTitles.AspireShowRelatedLogs, ActionGroups.Aspire, _ => { ShowToolWindow(ToolWindowIds.Aspire); AspireWindow.ShowRelatedLogs(); },
            c => NeedsAspire(c) ?? (aspireView?.Telemetry is { SelectedTrace: not null, SelectedSpan: not null } ? null : "No trace span is selected"));
        foreach (string page in AspireModel.Pages)
        {
            // Details is the chosen resource's, beside every resource listed; each found by its tab's former name too, Stats by what it charts.
            string id = AspireModel.PageAction(page);
            Words title = id switch
            {
                A.AspireResources => ActionTitles.AspireResources,
                A.AspireConsole => ActionTitles.AspireConsole,
                A.AspireStats => ActionTitles.AspireStats,
                A.AspireGraph => ActionTitles.AspireGraph,
                A.AspireTelemetry => ActionTitles.AspireTelemetry,
                A.AspireAppHostConsole => ActionTitles.AspireAppHostConsole,
                _ => throw new InvalidOperationException($"No title for the Aspire window's {page} page"),
            };
            Add(id, title, ActionGroups.Aspire, _ =>
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
        foreach ((string id, Words title, Passenger.App.Telemetry.TelemetryPage page) in (ReadOnlySpan<(string, Words, Passenger.App.Telemetry.TelemetryPage)>)[
            (A.AspireLogs, ActionTitles.AspireLogs, Passenger.App.Telemetry.TelemetryPage.Logs), (A.AspireTraces, ActionTitles.AspireTraces, Passenger.App.Telemetry.TelemetryPage.Traces)])
        {
            Add(id, title, ActionGroups.Aspire, _ =>
            {
                ShowToolWindow(ToolWindowIds.Aspire);
                AspireWindow.ShowTelemetry(page);
            }, NeedsAspire);
        }
        // The graph as drawn, for a document: Mermaid's flowchart, which renders where Markdown does.
        Add(A.AspireGraphMermaid, ActionTitles.AspireGraphMermaid, ActionGroups.Aspire, _ =>
        {
            if (aspireView?.Graph.Mermaid() is { } flowchart)
            {
                IdeErrors.Observe(CopyAsync(flowchart, $"Copied the graph of {Plural.Of(aspireView.Graph.Nodes.Count, "resource")} as a Mermaid flowchart"));
            }
        }, c => NeedsAspire(c) ?? (aspireView?.Graph.Nodes.Count is > 0 ? null : "The Aspire graph shows no resources"), words: ["Export Aspire Graph as Mermaid"]);
        Add(A.AspireOpenLink, ActionTitles.AspireOpenLink, ActionGroups.Aspire, _ =>
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
        Add(A.AspireSource, ActionTitles.AspireSource, ActionGroups.Aspire, _ =>
        {
            if (Aspire.SelectedResource?.Source is { } source && Path.IsPathFullyQualified(source))
            {
                GoToAspireSource(new FileLink(source, new Passenger.Text.TextPosition(0, 0)));
            }
        },
            c => NeedsAspire(c) ?? (Aspire.SelectedResource?.Source is { } source && Path.IsPathFullyQualified(source) ? null : "No resource source is selected"));
        Add(A.AspireContainer, ActionTitles.AspireContainer, ActionGroups.Aspire, _ =>
        {
            if (Aspire.SelectedContainerId is { } id)
            {
                Aspire.SelectContainer?.Invoke(id);
            }
        }, c => NeedsAspire(c) ?? (Aspire.SelectedContainerId is null ? "No container resource is selected"
            : Aspire.SelectContainer is null ? "Container selection is unavailable" : null));
    }

    private Words? NeedsAspireResource(ActionContext context) => NeedsAspire(context) ?? (Aspire.Selection is null ? "No Aspire resource is selected" : null);

    /// <summary>Why an action of the Aspire window cannot run: the window serves nothing in the open folder.</summary>
    private Words? NeedsAspire(ActionContext _) => Unserved(ToolWindowIds.Aspire);

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
