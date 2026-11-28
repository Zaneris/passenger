using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Passenger.Core;

namespace Passenger.App.Docking;

/// <summary>How a tool window stands: in its place, in a window kept over the IDE's, in a window of its own the
/// system manages like any other, or as a tab among the editors.</summary>
public enum ToolMode
{
    Docked,
    Floating,
    Windowed,
    Editor,
}

/// <summary>A tool window (Explorer, Debug, Unit Tests): its content, its button on a stripe, and where it docks.
/// The person moves it by dragging its button or its header, or with Move to in its header's menu. One given how to
/// build its view builds it when first shown or asked for, so a window's start makes no view it hides (#233).</summary>
public sealed class ToolWindow([IdentifierOf(typeof(ToolWindowIds))] string id, Words title, string icon, Func<Control> build, ToolAnchor anchor) : Observable
{
    private ToolAnchor anchor = anchor;
    private bool isShown;
    private Words title = title;
    private Control? content;
    private Control? headerTools;

    /// <summary>What in its view last had the keys, which its action gives them back to.</summary>
    private Control? hadKeys;

    /// <summary>The list's row that last had the keys, where <see cref="hadKeys"/> was one.</summary>
    private RowWithKeys? hadRow;

    /// <summary>A tool window whose view is made already (the Explorer's, open at first).</summary>
    public ToolWindow(string id, Words title, string icon, Control content, ToolAnchor anchor) : this(id, title, icon, () => content, anchor)
    {
        this.content = content;
        FollowKeys(content);
    }

    [IdentifierOf(typeof(ToolWindowIds))]
    public string Id { get; } = id;

    /// <summary>Its name on its header and button, a count with it when it has one ("Problems 3"): ToolWindowTitles' words.</summary>
    public Words Title
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
        view.AddHandler(InputElement.GotFocusEvent, (_, e) =>
        {
            hadKeys = e.Source as Control;
            hadRow = RowWithKeys.Of(hadKeys);
            GotKeys?.Invoke(this, EventArgs.Empty);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

    /// <summary>Gives its view the keys, as its action does once it shows: back to what in it had them last, still shown (a
    /// list's row, or its item's row, still chosen: <see cref="ListKeys.Hold(RowWithKeys, NavigationMethod)"/>), else to its
    /// first search box or list (its chosen row, the first where none is), else to the first text that takes them (a
    /// console), else to the view itself until they leave it, so what is typed next never goes to the file. Given them as by
    /// Tab (<paramref name="method"/>), what takes them draws the ring round it; false where nothing in it could.</summary>
    internal bool TakeKeys(NavigationMethod method = NavigationMethod.Unspecified)
    {
        Control view = Content;
        if (TopLevel.GetTopLevel(view) is null)
        {
            return false;
        }
        view.UpdateLayout();
        if (hadRow is { } row ? Shown(row.List) && ListKeys.Hold(row, method) : hadKeys is { } had && Shown(had) && had.Focus(method))
        {
            return true;
        }
        List<Control> shown = [.. view.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled)];
        foreach (Control each in shown)
        {
            switch (each)
            {
                case TextBox { IsReadOnly: false, Focusable: true } search when search.Focus(method):
                    return true;
                case ListBox { ItemCount: > 0 } list when ListKeys.Hold(list, chooseFirst: true, method):
                    return true;
            }
        }
        if (shown.FirstOrDefault(c => c.Focusable && c is not (Button or ComboBox or Thumb)) is { } text && text.Focus(method))
        {
            return true;
        }
        if (view.Focusable)
        {
            return view.Focus(method);
        }
        // Focusable only while it holds them, so a press on its toolbar never takes them from the editor.
        view.Focusable = true;
        view.LostFocus += LetGo;
        if (!view.Focus(method))
        {
            LetGo(view, new RoutedEventArgs());
            return false;
        }
        return true;

        void LetGo(object? sender, RoutedEventArgs e)
        {
            view.LostFocus -= LetGo;
            view.Focusable = false;
        }

        bool Shown(Control control) => view.IsVisualAncestorOf(control) && control.IsEffectivelyVisible && control.IsEffectivelyEnabled;
    }

    /// <summary>Whether its view has been built: what only looks among the views made (which one holds the keys) passes
    /// over one that has not.</summary>
    public bool IsBuilt => content is not null;

    /// <summary>Its view was built, as it was first shown or asked for.</summary>
    public event EventHandler? Built;

    /// <summary>Something in its view took the keys.</summary>
    public event EventHandler? GotKeys;

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
    public Words? ActionTitle { get; init; }

    /// <summary>Where the menus list its action, besides View's Tool Windows.</summary>
    public IReadOnlyList<MenuPlace> Menus { get; init; } = [];

    /// <summary>What its action does in place of showing it with the keys (<see cref="TakeKeys"/>): the terminal's leaves
    /// it again from inside.</summary>
    public Action? Activate { get; init; }

    /// <summary>Whether a show the person asks for (its stripe's button, its tab, a move) gives it the keys as its action
    /// does: the terminal's, shown to be typed into. A layout showing it, or an attached agent's call, never does.</summary>
    public bool TakesKeysAsShown { get; init; }

    /// <summary>What is chosen in it (its rows), for actions that run on them while it has the keys.</summary>
    public Func<object?>? Chosen { get; init; }

    /// <summary>One of the window's actions its header carries as a button before Expand All, with the button's icon and
    /// the action's title for its tip (the Explorer's Select Opened File).</summary>
    public (string Action, string Icon, Words Title)? HeaderAction { get; init; }

    /// <summary>Opens every row of its tree, for Expand All, its button in the header.</summary>
    public Action? ExpandAll { get; init; }

    /// <summary>Closes every row of its tree but the top ones, for Collapse All, its button in the header.</summary>
    public Action? CollapseAll { get; init; }

    /// <summary>Open in the layout the IDE starts with (the Explorer), and so after Restore Default Layout.</summary>
    public bool OpenAtFirst { get; init; }

    /// <summary>A tab among the editors in the layout the IDE starts with, and so after Restore Default Layout, where a
    /// window that needs the room (the Dependency Map) has it; docked at its anchor once the person docks it.</summary>
    public bool InEditorAtFirst { get; init; }

    /// <summary>What it serves, declared as it is made: every folder, or kinds of project the open folder must hold one of
    /// for it to show (<see cref="Unserved"/>).</summary>
    public required ToolWindowServes Serves { get; init; }

    /// <summary>Why it serves nothing in the open folder (<see cref="ToolWindowServes.WhyNot"/>), which keeps it off the
    /// stripes, its place and its tabs, its action greyed saying so, its place in the layout kept; null while it serves the
    /// folder, as a window of every folder always does.</summary>
    public Words? Unserved { get; internal set; }

    /// <summary>The action that shows it, as the dock adds it: an agent's shows it with the keys left where they are.</summary>
    internal IdeAction? ShowAction(DockHost dock) => Action is { } id
        ? new IdeAction(id, ActionTitle ?? Title, ActionGroups.ToolWindows, context =>
        {
            if (context.ByAgent)
            {
                dock.ShowBehind(Id);
            }
            else
            {
                (Activate ?? (() => dock.Activate(Id)))();
            }
        })
        {
            Menus = Menus,
            FromTerminal = true,
            WhyNot = _ => Unserved,
        }
        : null;

    public ToolAnchor Anchor
    {
        get => anchor;
        internal set => Set(ref anchor, value);
    }

    /// <summary>Docked, floating, windowed or an editor tab; its button stays on its anchor's stripe whatever the mode.</summary>
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

    /// <summary>Off its stripe and its place's tabs, closed by its × while the dock takes closed windows off
    /// (<see cref="DockHost.ClosesOffStripe"/>) or unchecked on its stripe's checklist, until it is opened or checked
    /// again.</summary>
    public bool IsClosed { get; internal set; }

    /// <summary>The name a menu gives a place: "Left Top", "Bottom Right".</summary>
    public static Words Name(ToolAnchor anchor) => anchor switch
    {
        ToolAnchor.LeftTop => DockingWords.LeftTop,
        ToolAnchor.LeftBottom => DockingWords.LeftBottom,
        ToolAnchor.RightTop => DockingWords.RightTop,
        ToolAnchor.RightBottom => DockingWords.RightBottom,
        ToolAnchor.BottomLeft => DockingWords.BottomLeft,
        _ => DockingWords.BottomRight,
    };
}
