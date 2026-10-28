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
            view.ActionRequested += (_, action) => Request(action);
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
        actions!.Add(new IdeAction(AppActions.EndpointsRefresh, "Refresh Endpoints", "Endpoints", _ => Endpoints.Reload(session.Folder))
        {
            WhyNot = NeedsFolder,
            Words = ["Read the .http files again"],
        });
        // A row's Test, Enter and menu, an agent and Find Action test the row chosen; an .http file's icon, its line's request.
        actions.Add(new IdeAction(AppActions.TestEndpoint, "Test Request", "Endpoints", context =>
        {
            if (context.Request is { } sending)
            {
                ShowToolWindow(ToolWindowIds.Endpoints);
                IdeErrors.Observe(Endpoints.TestAsync(sending.Request, null, sending.File));
                return;
            }
            IdeErrors.Observe(Endpoints.TestAsync(Endpoints.Chosen!));
        })
        {
            WhyNot = c => c.Request is null && endpoints?.Chosen is null ? "No request is chosen in the Endpoints window" : null,
            TitleIn = c => c.Request is { Request: var request } ? request.Name.Length > 0 ? $"Send '{request.Name}'" : $"Send {request.Method} {request.Url}" : "Test Request",
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
