using Passenger.App.Endpoints;
using Passenger.App.Workspace;
using Passenger.Text;

namespace Passenger.App;

/// <summary>The window's Endpoints: its tool window over the open folder's .http and .rest files, read again as one of
/// them or an environment file changes on disk, and the Interactive Test dialog its rows open.</summary>
public partial class MainWindow
{
    private EndpointsView? endpoints;

    /// <summary>The Endpoints tool window, built as it is first shown or asked for.</summary>
    public EndpointsView Endpoints => endpoints ?? (EndpointsView)Dock[ToolWindowIds.Endpoints].Content;

    /// <summary>The Interactive Test dialog open last.</summary>
    public InteractiveTestDialog? InteractiveTest { get; private set; }

    private void AddEndpoints()
    {
        // Built as it is first shown or asked for (a route's request sent), over the session's files and routes then.
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Endpoints, "Endpoints", IconKeys.Endpoints, () =>
        {
            EndpointsView view = new(new EndpointsParts(work, () => session.EndpointGrants, text => status.Text = text));
            endpoints = view;
            view.GoToRequested += (_, request) => NavigateTo(request.File, new TextPosition(request.Line, 0));
            view.InteractiveRequested += (_, request) => OpenInteractiveTest(request);
            FollowRoutes(view);
            view.Reload(session.Folder);
            view.ShowRoutes(session.Routes.All);
            return view;
        }, Docking.ToolAnchor.RightTop)
        {
            Action = AppActions.ShowEndpoints,
            Menus = [new MenuPlace("Tools", 0, 3)],
            Chosen = () => endpoints?.Chosen,
        });
        // The row's Test button, Enter and its menu test it as this does; an agent or Find Action tests the row chosen.
        actions!.Add(new IdeAction(AppActions.TestEndpoint, "Test Request", "Endpoints", context => IdeErrors.Observe(Endpoints.TestAsync(Endpoints.Chosen!)))
        {
            WhyNot = _ => endpoints?.Chosen is null ? "No request is chosen in the Endpoints window" : null,
            Words = ["HTTP Client", "Send Request"],
        });
    }

    /// <summary>A session's requests in the Endpoints window, while it is the window's; the last one's test stopped.</summary>
    private void BindEndpoints(ProjectSession next)
    {
        endpoints?.Stop();
        endpoints?.Reload(next.Folder);
        ForgetRoutes(next);
        BindRequests(next);
    }

    private void EndpointFilesChanged(FolderChanges changes)
    {
        if (changes.Paths.Any(HttpVariables.Matters))
        {
            endpoints?.Reload(session.Folder);
        }
        RouteFilesChanged(changes);
    }

    /// <summary>Opens a request in the Interactive Test dialog, its variables those of the environment chosen as it sends.</summary>
    public InteractiveTestDialog OpenInteractiveTest(EndpointRequest request)
    {
        InteractiveTestDialog dialog = new(request, endpoints!);
        InteractiveTest = dialog;
        IdeErrors.Observe(dialog.ShowDialog(this));
        return dialog;
    }
}
