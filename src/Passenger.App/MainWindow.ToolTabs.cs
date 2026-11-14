using Passenger.App.Docking;
using Passenger.App.Editors;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Tool windows as tabs among the editors (View Mode Editor Tab): the dock opens and closes each one's tab, a tab
/// closed hides its window, one dragged out of the editors docks where it is let go, and Dock Tool Window in its menu
/// puts it back in its place.</summary>
public partial class MainWindow
{
    /// <summary>The tool windows with a tab open among the editors, each by its tab.</summary>
    private readonly Dictionary<ToolWindow, ToolTab> toolTabs = [];

    private void AddToolTabs()
    {
        Dock.EditorTabs = (ShowToolTab, CloseToolTab);
        Editors.ToolZoneAt = (tool, at) => Dock.ZoneFor(tool.Window, at);
        Editors.ToolTabDropped += (_, dropped) => Dock.DropAt(dropped.Tool.Window, dropped.At);
        Editors.ToolTabDragging += (_, dragging) => Dock.Dragging(dragging);
        actions!.Add(new IdeAction(A.DockToolWindow, "Dock Tool Window", "Tool Windows", c => Dock.SetMode(ToolTabOf(c)!.Window, ToolMode.Docked))
        {
            WhyNot = c => ToolTabOf(c) is null ? "No tool window's tab is chosen" : null,
            TitleIn = c => ToolTabOf(c) is { } tool ? $"Dock {tool.Window.Title} in {ToolWindow.Name(tool.Window.Anchor)}" : "Dock Tool Window",
        });
    }

    /// <summary>The tool window's tab an action acts on: the one whose menu it was chosen from, else the one the active group shows.</summary>
    private ToolTab? ToolTabOf(ActionContext context) => (context.Tab?.File ?? Editors.ActiveGroup.Active)?.Tool;

    /// <summary>Opens a tool window's tab among the editors, or goes to it: brought forward with the keys, else only added
    /// beside the file shown, or shown where the group shows nothing.</summary>
    private void ShowToolTab(ToolWindow window, bool forward)
    {
        if (!toolTabs.TryGetValue(window, out ToolTab? tab))
        {
            tab = new ToolTab(window, closed =>
            {
                if (toolTabs.GetValueOrDefault(closed.Window) == closed)
                {
                    toolTabs.Remove(closed.Window);
                }
                Dock.EditorTabClosed(closed.Window);
            });
            toolTabs[window] = tab;
        }
        if (forward)
        {
            Editors.Activate(tab.Document);
        }
        else if (Editors.HasTab(tab.Document) || Editors.ActiveGroup.Active is not null)
        {
            Editors.ActiveGroup.Add(tab.Document);
        }
        else
        {
            Editors.Activate(tab.Document, focus: false);
        }
    }

    /// <summary>Closes a tool window's tab among the editors, wherever it stands.</summary>
    private void CloseToolTab(ToolWindow window)
    {
        if (toolTabs.Remove(window, out ToolTab? tab))
        {
            Editors.Close(tab.Document);
        }
    }
}
