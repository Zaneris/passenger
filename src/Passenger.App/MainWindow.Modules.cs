using Passenger.App.ToolWindows;

namespace Passenger.App;

/// <summary>The Modules window over the open folder's debugger: the chosen session's modules, following the session the
/// Debug window shows as it changes, starts and ends.</summary>
public partial class MainWindow
{
    private ModulesView? modules;

    /// <summary>The Modules tool window, built as it is first shown or asked for.</summary>
    public ModulesView ModulesWindow => modules ?? (ModulesView)Dock[ToolWindowIds.Modules].Content;

    private ModulesView MakeModules()
    {
        modules = new ModulesView();
        modules.Show(session.Debug.Session);
        return modules;
    }

    /// <summary>A session's debugger fills the Modules window with its chosen program's modules while it is the window's.</summary>
    private void BindModules(ProjectSession next)
    {
        void Follow()
        {
            if (session == next)
            {
                modules?.Show(next.Debug.Session);
            }
        }
        next.Debug.StateChanged += (_, _) => Follow();
        next.Debug.TabsChanged += (_, _) => Follow();
        Follow();
    }
}
