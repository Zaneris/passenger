using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Passenger.App.Docking;

/// <summary>Where a tool window docks: each side's upper or lower half, the bottom's left or right half. A place shows
/// one of its windows at a time.</summary>
public enum ToolAnchor
{
    LeftTop,
    LeftBottom,
    RightTop,
    RightBottom,
    BottomLeft,
    BottomRight,
}

/// <summary>How a tool window stands: in its place, in a window kept over the IDE's, or in a window of its own the
/// system manages like any other.</summary>
public enum ToolMode
{
    Docked,
    Floating,
    Windowed,
}

/// <summary>A tool window (Explorer, Debug, Unit Tests): its content, its button on a stripe, and where it docks.
/// The person moves it by dragging its button or its header, or with Move to in its header's menu. One given how to
/// build its view builds it when first shown or asked for, so a window's start makes no view it hides (#233).</summary>
public sealed class ToolWindow([IdentifierOf(typeof(ToolWindowIds))] string id, string title, string icon, Func<Control> build, ToolAnchor anchor) : Observable
{
    private ToolAnchor anchor = anchor;
    private bool isShown;
    private string title = title;
    private Control? content;
    private Control? headerTools;

    /// <summary>What in its view last had the keys, which its action gives them back to.</summary>
    private Control? hadKeys;

    /// <summary>A tool window whose view is made already (the Explorer's, open at first).</summary>
    public ToolWindow(string id, string title, string icon, Control content, ToolAnchor anchor) : this(id, title, icon, () => content, anchor)
    {
        this.content = content;
        FollowKeys(content);
    }

    [IdentifierOf(typeof(ToolWindowIds))]
    public string Id { get; } = id;

    /// <summary>Its name on its header and button, a count with it when it has one ("Problems 3").</summary>
    public string Title
    {
        get => title;
        set => Set(ref title, value);
    }

    /// <summary>The theme's icon resource for its stripe button.</summary>
    public string Icon { get; } = icon;

    /// <summary>Its view, built the first time it is asked for: as it is shown, or by what reads or acts on it.</summary>
    public Control Content
    {
        get
        {
            if (content is null)
            {
                content = build();
                FollowKeys(content);
                Built?.Invoke(this, EventArgs.Empty);
            }
            return content;
        }
    }

    private void FollowKeys(Control view) =>
        view.AddHandler(InputElement.GotFocusEvent, (_, e) => hadKeys = e.Source as Control, RoutingStrategies.Bubble, handledEventsToo: true);

    /// <summary>Gives its view the keys, as its action does once it shows: back to what in it had them last, still shown,
    /// else to the first of its search boxes and lists (a list's chosen row, the first chosen where none is), else to the
    /// first text it shows that takes them (a console), else to the view itself until they leave it, so what is typed next
    /// never goes to the file.</summary>
    internal void TakeKeys()
    {
        Control view = Content;
        if (TopLevel.GetTopLevel(view) is null)
        {
            return;
        }
        view.UpdateLayout();
        if (hadKeys is { } had && view.IsVisualAncestorOf(had) && had.IsEffectivelyVisible && had.IsEffectivelyEnabled
            && (ItemsControl.ItemsControlFromItemContainer(had) is ListBox rows ? ListKeys.Hold(rows) : had.Focus()))
        {
            return;
        }
        List<Control> shown = [.. view.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled)];
        foreach (Control each in shown)
        {
            switch (each)
            {
                case TextBox { IsReadOnly: false, Focusable: true } search when search.Focus():
                    return;
                case ListBox { ItemCount: > 0 } list when ListKeys.Hold(list, chooseFirst: true):
                    return;
            }
        }
        if (shown.FirstOrDefault(c => c.Focusable && c is not (Button or ComboBox or Thumb)) is { } text && text.Focus())
        {
            return;
        }
        if (view.Focusable)
        {
            view.Focus();
            return;
        }
        // Focusable only while it holds them, so a press on its toolbar never takes them from the editor.
        view.Focusable = true;
        view.LostFocus += LetGo;
        if (!view.Focus())
        {
            LetGo(view, new RoutedEventArgs());
        }

        void LetGo(object? sender, RoutedEventArgs e)
        {
            view.LostFocus -= LetGo;
            view.Focusable = false;
        }
    }

    /// <summary>Whether its view has been built: what only looks among the views made (which one holds the keys) passes
    /// over one that has not.</summary>
    public bool IsBuilt => content is not null;

    /// <summary>Its view was built, as it was first shown or asked for.</summary>
    public event EventHandler? Built;

    /// <summary>Controls of its own for its header, after its title (the Explorer's view switch).</summary>
    public Control? HeaderTools
    {
        get => headerTools ??= Tools?.Invoke();
        init => headerTools = value;
    }

    /// <summary>The header's controls of a view built when first shown, asked for once its <see cref="Content"/> is.</summary>
    public Func<Control?>? Tools { get; init; }

    /// <summary>Menu items of its own atop its header's options (the Explorer's Appearance), made as the menu
    /// opens.</summary>
    public Func<IReadOnlyList<Control>>? Options { get; init; }

    /// <summary>The id of the action that shows it, if there is one (its shortcut is the button's tip): the dock adds it
    /// to the window's actions, to run from anywhere, the terminal too.</summary>
    public string? Action { get; init; }

    /// <summary>Its action's name in Find Action, where the window's own would be another action's ("Run Tool Window").</summary>
    public string? ActionTitle { get; init; }

    /// <summary>Where the menus list its action, besides View's Tool Windows.</summary>
    public IReadOnlyList<MenuPlace> Menus { get; init; } = [];

    /// <summary>What its action does in place of showing it with the keys (<see cref="TakeKeys"/>): the terminal's leaves
    /// it again from inside.</summary>
    public Action? Activate { get; init; }

    /// <summary>What is chosen in it (its rows), for actions that run on them while it has the keys.</summary>
    public Func<object?>? Chosen { get; init; }

    /// <summary>Opens every row of its tree, for Expand All, its button in the header.</summary>
    public Action? ExpandAll { get; init; }

    /// <summary>Closes every row of its tree but the top ones, for Collapse All, its button in the header.</summary>
    public Action? CollapseAll { get; init; }

    /// <summary>Open in the layout the IDE starts with (the Explorer), and so after Restore Default Layout.</summary>
    public bool OpenAtFirst { get; init; }

    /// <summary>The action that shows it, as the dock adds it.</summary>
    internal IdeAction? ShowAction(DockHost dock) => Action is { } id
        ? new IdeAction(id, ActionTitle ?? Title, "Tool Windows", _ => (Activate ?? (() => dock.Activate(Id)))()) { Menus = Menus, FromTerminal = true }
        : null;

    public ToolAnchor Anchor
    {
        get => anchor;
        internal set => Set(ref anchor, value);
    }

    /// <summary>Docked, floating or windowed; its button stays on its anchor's stripe whatever the mode.</summary>
    public ToolMode Mode { get; internal set; }

    /// <summary>Where its own window last stood, floating or windowed, in screen pixels.</summary>
    public PixelRect? WindowBounds { get; internal set; }

    /// <summary>Its place among its anchor's buttons, from the top or the left.</summary>
    public int Order { get; internal set; }

    /// <summary>Open in its place: what its stripe button shows as pressed.</summary>
    public bool IsShown
    {
        get => isShown;
        internal set => Set(ref isShown, value);
    }

    /// <summary>The name a menu gives a place: "Left Top", "Bottom Right".</summary>
    public static string Name(ToolAnchor anchor) => anchor switch
    {
        ToolAnchor.LeftTop => "Left Top",
        ToolAnchor.LeftBottom => "Left Bottom",
        ToolAnchor.RightTop => "Right Top",
        ToolAnchor.RightBottom => "Right Bottom",
        ToolAnchor.BottomLeft => "Bottom Left",
        _ => "Bottom Right",
    };
}
