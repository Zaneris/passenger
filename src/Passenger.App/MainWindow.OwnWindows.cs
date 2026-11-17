using Avalonia.Controls;

namespace Passenger.App;

/// <summary>The windows this one opens beside it as the IDE's own (<see cref="IdeKeys"/>): an editor window, a floating or
/// windowed tool window, a diff or merge window, a test's frame. The window's keymap runs in each, editor actions
/// going to its editor with the keys and every other action to this window's registry, a chord forgotten as here, autosave
/// on leaving Passenger from one, and the popups its keys open (Find Action, Go to File) shown over it; an editor window
/// opens the files dropped on it too. A dialog keeps its own keys.</summary>
public partial class MainWindow
{
    private void AddOwnWindows()
    {
        Editors.WindowOpened += (_, opened) =>
        {
            windowKeys.Attach(opened.Window);
            AcceptDrops(opened.Window);
        };
        Dock.WindowOpened += (_, floating) => windowKeys.Attach(floating);
        Bottom.Tests.Frames.ViewerOpened += (_, viewer) => windowKeys.Attach(viewer);
    }

    /// <summary>One of the IDE's windows shown over this one, as its own.</summary>
    private void ShowOwn(Window window)
    {
        windowKeys.Attach(window);
        window.Show(this);
    }
}
