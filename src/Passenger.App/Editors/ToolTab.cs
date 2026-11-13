using Avalonia.Controls;
using Passenger.App.Docking;
using Passenger.App.Workspace;

namespace Passenger.App.Editors;

/// <summary>A tool window docked among the editors as a tab (<see cref="ToolMode.Editor"/>): its document holds no text
/// (<see cref="OpenDocument.Tool"/>), so it is never one of the open files, and the group showing it shows the window's own
/// view in the editor's place, which stands in one place at a time. Its tab closed for good, <paramref name="closed"/> hears it.</summary>
public sealed class ToolTab
{
    private readonly Action<ToolTab> closed;

    public ToolTab(ToolWindow window, Action<ToolTab> closed)
    {
        Window = window;
        this.closed = closed;
        Document = OpenDocument.OfTool(this);
    }

    public ToolWindow Window { get; }

    /// <summary>The tab's document, which every group showing the window has a tab of.</summary>
    public OpenDocument Document { get; }

    /// <summary>The window's view for a group that shows the tab, taken from where it stood.</summary>
    public Control Make() => new ToolTabView(Window);

    /// <summary>The tab closed: the window is no longer shown.</summary>
    internal void LetGo() => closed(this);
}

/// <summary>A tool window's view in an editor group's place, on the window's own ground, which takes the keys as the
/// window's action gives them.</summary>
internal sealed class ToolTabView : ContentControl
{
    public ToolTabView(ToolWindow window)
    {
        Window = window;
        Control view = window.Content;
        // A view stands in one place at a time, so it leaves the one it stood in first.
        if (view.Parent is ContentControl old)
        {
            old.Content = null;
        }
        Content = view;
        Classes.Add(StyleClasses.ToolTabView);
    }

    public ToolWindow Window { get; }

    /// <summary>Styled as a ContentControl of its own class (Shell.axaml's toolTabView), its ground the theme's.</summary>
    protected override Type StyleKeyOverride => typeof(ContentControl);
}
