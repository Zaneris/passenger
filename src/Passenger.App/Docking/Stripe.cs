using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Passenger.Editor;

namespace Passenger.App.Docking;

/// <summary>A stripe of buttons down one edge: the side's upper and lower windows at its top (a gap between them), the
/// bottom area's half at its foot. A button shows or hides its window, and drags it elsewhere. Each window keeps its
/// button from one layout to the next, moved where its window went.</summary>
internal sealed class Stripe : Border
{
    private readonly DockHost host;
    private readonly Dictionary<ToolAnchor, StackPanel> groups;
    private readonly Dictionary<ToolWindow, ToggleButton> buttons = [];
    private readonly Dictionary<ToolWindow, string?> shortcuts = [];
    private readonly ToolAnchor upper;
    private readonly ToolAnchor lower;
    private readonly ToolAnchor bottom;
    private readonly Rectangle gap;

    /// <summary>The keymap the tips' shortcuts were read from: once the person's shortcuts change, they are read again.</summary>
    private Keymap? tipsFrom;

    public Stripe(DockHost host, ToolAnchor upper, ToolAnchor lower, ToolAnchor bottom, Thickness border)
    {
        this.host = host;
        this.upper = upper;
        this.lower = lower;
        this.bottom = bottom;
        groups = new Dictionary<ToolAnchor, StackPanel>
        {
            [upper] = Group(),
            [lower] = Group(),
            [bottom] = Group(),
        };
        // Wide apart and drawn plainly, so the upper and lower windows read as two places, not one list.
        gap = new Rectangle { Height = 1, Width = 22, Margin = new Thickness(0, 12) };
        gap.Bind(Shape.FillProperty, gap.GetResourceObservable("Passenger.StripeSeparator"));
        StackPanel sides = new() { [DockPanel.DockProperty] = Dock.Top, Children = { groups[upper], gap, groups[lower] } };
        groups[bottom].VerticalAlignment = VerticalAlignment.Bottom;
        groups[bottom][DockPanel.DockProperty] = Dock.Bottom;
        Child = new DockPanel { Margin = new Thickness(0, 6), Children = { sides, groups[bottom] } };
        BorderThickness = border;
        this.Bind(BackgroundProperty, this.GetResourceObservable("Passenger.Chrome"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("Passenger.Divider"));
    }

    /// <summary>Puts each group's windows' buttons in their order, pressed as their windows are shown; a group already
    /// in order is left as it is.</summary>
    public void Fill(IReadOnlyList<ToolWindow> windows)
    {
        Keymap keymap = host.Actions?.Keymap ?? AppActions.Keymap();
        bool keysChanged = !ReferenceEquals(keymap, tipsFrom);
        tipsFrom = keymap;
        Dictionary<ToolAnchor, List<ToggleButton>> wanted = groups.Keys.ToDictionary(anchor => anchor, anchor =>
            windows.Where(w => w.Anchor == anchor).OrderBy(w => w.Order).Select(w => Button(w, keymap, keysChanged)).ToList());
        // Every group out of order is emptied first, so a button that moved between two is in neither when it is added.
        foreach ((ToolAnchor anchor, StackPanel group) in groups)
        {
            if (!group.Children.SequenceEqual(wanted[anchor]))
            {
                group.Children.Clear();
            }
        }
        foreach ((ToolAnchor anchor, StackPanel group) in groups)
        {
            if (group.Children.Count == 0)
            {
                group.Children.AddRange(wanted[anchor]);
            }
        }
        gap.IsVisible = groups[upper].Children.Count > 0 && groups[lower].Children.Count > 0;
    }

    /// <summary>The group a point (in the stripe's coordinates) is over: the bottom group's at or below it, the
    /// lower side group's past the upper's end, else the upper's.</summary>
    public ToolAnchor AnchorAt(Point at)
    {
        Rect bottomBounds = Placed(groups[bottom]);
        double sidesEnd = Math.Min(bottomBounds.Top, Bounds.Height - 48) - 12;
        if (at.Y >= sidesEnd)
        {
            return bottom;
        }
        // Past the upper group's buttons is the lower group; a side with no lower buttons splits the stripe, and one with
        // no upper buttons takes a drop above its first lower button's middle into the upper group, as Rider's do.
        double boundary = groups[lower].Children.Count == 0 ? Math.Max(Placed(groups[upper]).Bottom + 6, sidesEnd / 2)
            : groups[upper].Children.Count == 0 && groups[lower].Children[0] is Control first ? Placed(first).Center.Y
            : Placed(groups[upper]).Bottom + 6;
        return at.Y > boundary ? lower : upper;
    }

    /// <summary>Where among a group's buttons a point falls, for a drop to keep its order.</summary>
    public int IndexAt(ToolAnchor anchor, Point at)
    {
        StackPanel group = groups[anchor];
        int index = 0;
        foreach (Control button in group.Children)
        {
            Point centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), this) ?? default;
            if (at.Y > centre.Y)
            {
                index++;
            }
        }
        return index;
    }

    private Rect Placed(Control group) =>
        group.TranslatePoint(default, this) is { } origin ? new Rect(origin, group.Bounds.Size) : group.Bounds;

    /// <summary>A window's button, made the first time, pressed as its window is shown, its tip the window's name as it
    /// is now and the shortcut that shows it.</summary>
    private ToggleButton Button(ToolWindow window, Keymap keymap, bool keysChanged)
    {
        if (!buttons.TryGetValue(window, out ToggleButton? button))
        {
            button = NewButton(window);
            buttons[window] = button;
        }
        if (keysChanged || !shortcuts.ContainsKey(window))
        {
            shortcuts[window] = window.Action is { } action ? keymap.ShortcutsFor(action).FirstOrDefault()?.Display() : null;
        }
        button.IsChecked = host.IsShown(window.Id);
        string? keys = shortcuts[window];
        ToolTip.SetTip(button, keys is null ? window.Title : $"{window.Title} ({keys})");
        return button;
    }

    private ToggleButton NewButton(ToolWindow window)
    {
        PathIcon glyph = new();
        glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable(window.Icon));
        ToggleButton button = new() { Classes = { "stripe" }, Content = glyph, Tag = window.Id };
        button.AddHandler(PointerPressedEvent, (_, e) => host.Drag.Begin(window, e), RoutingStrategies.Tunnel, handledEventsToo: true);
        button.AddHandler(PointerMovedEvent, (_, e) => host.Drag.Continue(e), RoutingStrategies.Tunnel, handledEventsToo: true);
        button.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (host.Drag.End(e))
            {
                // A drag is no click: the button keeps its state, and the move has laid the stripes out again.
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        button.Click += (_, _) =>
        {
            host.Toggle(window.Id);
            button.IsChecked = host.IsShown(window.Id);
        };
        return button;
    }

    private static StackPanel Group() => new() { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
}
