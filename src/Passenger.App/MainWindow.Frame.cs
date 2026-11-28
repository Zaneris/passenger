using Avalonia.Controls;
using Passenger.App.Settings;
using Passenger.Core;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The window's own frame: Zoom In, Zoom Out and Reset Zoom, which step the whole window through Settings' Display
/// scale and keep it there; Full Screen; Hide Side Windows; Jump to Last Tool Window, back to the tool window that last had
/// the keys; and Close Tool Window Tab, the tab shown of the tool window with the keys closed as its × closes it. Each runs
/// from the terminal too.</summary>
public partial class MainWindow
{
    /// <summary>The window's state before Full Screen, which it goes back to.</summary>
    private WindowState beforeFullScreen = WindowState.Normal;

    private void AddFrameActions()
    {
        Frame(A.ZoomIn, ActionTitles.ZoomIn, _ => ApplySettings(Declared.DisplayScale.Stepped(settings, up: true)!),
            _ => Declared.DisplayScale.Stepped(settings, up: true) is null ? Refusals.ZoomedAsFarAsItGoes(up: true) : null, ("View/Appearance", 1, 0));
        Frame(A.ZoomOut, ActionTitles.ZoomOut, _ => ApplySettings(Declared.DisplayScale.Stepped(settings, up: false)!),
            _ => Declared.DisplayScale.Stepped(settings, up: false) is null ? Refusals.ZoomedAsFarAsItGoes(up: false) : null, ("View/Appearance", 1, 1));
        Frame(A.ResetZoom, ActionTitles.ResetZoom, _ => ApplySettings(Declared.DisplayScale.Unset(settings)), null, ("View/Appearance", 1, 2));
        actions!.Add(new IdeAction(A.FullScreen, ActionTitles.FullScreen, "IDE", _ => ToggleFullScreen())
        {
            IsChecked = _ => WindowState == WindowState.FullScreen,
            Menus = [new MenuPlace("View/Appearance", 0, 0)],
            FromTerminal = true,
        });
        Frame(A.HideSideWindows, ActionTitles.HideSideWindows, _ => Dock.ToggleHideSides(), null, ("View", 3, 3));
        Frame(A.LastToolWindow, ActionTitles.LastToolWindow, _ => Dock.Activate(Dock.LastWithKeys!.Id),
            _ => Dock.LastWithKeys is null ? Refusals.NoToolWindowHadKeys : null, ("View", 3, 4));
        Frame(A.CloseToolTab, ActionTitles.CloseToolTab, context =>
        {
            if (!TabsWithKeys(context)!.CloseTab())
            {
                Refuse(Refusals.ToolTabStays);
            }
        }, context => TabsWithKeys(context) is null ? Refusals.NoToolTabs : null, ("View", 3, 5));
    }

    /// <summary>One of the frame's actions, which the terminal's keys run too.</summary>
    private void Frame(string id, Words title, Action<ActionContext> run, Func<ActionContext, Words?>? whyNot, (string Path, int Group, int Order) menu) =>
        actions!.Add(new IdeAction(id, title, "IDE", run)
        {
            WhyNot = whyNot ?? (_ => null),
            Menus = [new MenuPlace(menu.Path, menu.Group, menu.Order)],
            FromTerminal = true,
        });

    /// <summary>Full Screen, or back to the state the window had before it.</summary>
    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = beforeFullScreen;
            return;
        }
        beforeFullScreen = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        WindowState = WindowState.FullScreen;
    }
}
