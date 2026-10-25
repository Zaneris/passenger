using Avalonia;
using Avalonia.Controls;

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

    /// <summary>A tool window whose view is made already (the Explorer's, open at first).</summary>
    public ToolWindow(string id, string title, string icon, Control content, ToolAnchor anchor) : this(id, title, icon, () => content, anchor)
    {
        this.content = content;
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
                Built?.Invoke(this, EventArgs.Empty);
            }
            return content;
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

    /// <summary>What its action does in place of showing it (the Explorer's gives its rows the keys, the terminal's
    /// leaves it again from inside).</summary>
    public Action? Activate { get; init; }

    /// <summary>What is chosen in it (its rows), for actions that run on them while it has the keys.</summary>
    public Func<object?>? Chosen { get; init; }

    /// <summary>Open in the layout the IDE starts with (the Explorer), and so after Restore Default Layout.</summary>
    public bool OpenAtFirst { get; init; }

    /// <summary>The action that shows it, as the dock adds it.</summary>
    internal IdeAction? ShowAction(DockHost dock) => Action is { } id
        ? new IdeAction(id, ActionTitle ?? Title, "Tool Windows", _ => (Activate ?? (() => dock.Show(Id)))()) { Menus = Menus, FromTerminal = true }
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
