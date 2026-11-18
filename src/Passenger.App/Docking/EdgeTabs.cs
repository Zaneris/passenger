using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App.Docking;

/// <summary>Under titled tabs, one edge of the frame (a side's, or the foot's) where each window of its places that stands
/// hidden is a tab of its name: the pointer resting on it opens a docked one over the editors beside the edge without the
/// keys, a click opens it with them (<see cref="DockHost.Peek"/>) and closes it again, opens one that floats or stands
/// among the editors there, and a drag moves it as its titled tab's does. A side's tabs run down it, the upper place's before the lower's; the foot's run across, the left place's from its
/// start and the right's to its end. Each takes the keys as a tab of the window's regions, Enter or Space opening it, and
/// is a button of its name to a screen reader, as a stripe icon is.</summary>
internal sealed class EdgeTabs : Border
{
    /// <summary>How broad an edge with tabs stands.</summary>
    public const double Breadth = 32;

    private readonly DockHost host;
    private readonly ToolAnchor first;
    private readonly ToolAnchor second;
    private readonly bool down;
    private readonly StackPanel firstTabs;
    private readonly StackPanel secondTabs;
    private readonly Dictionary<ToolWindow, Border> tabs = [];

    public EdgeTabs(DockHost host, ToolAnchor first, ToolAnchor second, bool down, string said)
    {
        this.host = host;
        Said = said;
        this.first = first;
        this.second = second;
        this.down = down;
        Orientation orientation = down ? Orientation.Vertical : Orientation.Horizontal;
        // Down a side, tabs as narrow as a stripe's buttons stand apart as those do.
        firstTabs = new StackPanel { Orientation = orientation, Spacing = down ? Gaps.Tight : 0 };
        secondTabs = new StackPanel { Orientation = orientation, Spacing = down ? Gaps.Tight : 0 };
        Panel groups = down
            ? new StackPanel { Spacing = Gaps.Wide, Children = { firstTabs, secondTabs } }
            : new DockPanel { Children = { new Panel { [DockPanel.DockProperty] = Dock.Right, Children = { secondTabs } }, firstTabs } };
        Child = new Panel { Children = { new ThemeBackdrop(BackgroundPlaces.Frame), groups } };
        Classes.Add(StyleClasses.EdgeTabs);
        Classes.Set(StyleClasses.EdgeTabsDown, down);
        IsVisible = false;
    }

    /// <summary>Styled as a Border of its own class (Shell.axaml's edgeTabs), its look the theme's.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>What the edge is told as, entered as one of the window's regions.</summary>
    public string Said { get; }

    /// <summary>Gives the keys, as by Tab, to the edge's first tab; false with none.</summary>
    public bool TakeKeys() => firstTabs.Children.Concat(secondTabs.Children).FirstOrDefault() is { } tab && tab.Focus(NavigationMethod.Tab);

    /// <summary>Whether any window has a tab here.</summary>
    public bool HasTabs => firstTabs.Children.Count + secondTabs.Children.Count > 0;

    /// <summary>The windows whose tabs stand here, in their order.</summary>
    public IReadOnlyList<ToolWindow> Named => [.. firstTabs.Children.Concat(secondTabs.Children).Select(tab => tabs.First(t => t.Value == tab).Key)];

    /// <summary>A tab for each of these windows docked at its places, in their order, the one open over the editors marked
    /// chosen; a group already in order is left as it is.</summary>
    public void Fill(IEnumerable<ToolWindow> windows, ToolWindow? peeked)
    {
        Keymap keymap = host.Actions?.Keymap ?? AppActions.Keymap();
        List<ToolWindow> here = [.. windows.Where(w => w.Anchor == first || w.Anchor == second).OrderBy(w => w.Order)];
        foreach ((StackPanel group, ToolAnchor anchor) in ((StackPanel, ToolAnchor)[])[(firstTabs, first), (secondTabs, second)])
        {
            Border[] wanted = [.. here.Where(w => w.Anchor == anchor).Select(TabOf)];
            if (!group.Children.SequenceEqual(wanted))
            {
                group.Children.Clear();
            }
        }
        foreach ((StackPanel group, ToolAnchor anchor) in ((StackPanel, ToolAnchor)[])[(firstTabs, first), (secondTabs, second)])
        {
            if (group.Children.Count == 0)
            {
                group.Children.AddRange(here.Where(w => w.Anchor == anchor).Select(TabOf));
            }
            group.IsVisible = group.Children.Count > 0;
        }
        foreach ((ToolWindow window, Border tab) in tabs)
        {
            tab.Classes.Set(StyleClasses.Active, window == peeked);
            string? keys = window.Action is { } action ? keymap.ShortcutsFor(action).FirstOrDefault()?.Display() : null;
            ToolTip.SetTip(tab, keys is null ? window.Title : $"{window.Title} ({keys})");
        }
    }

    /// <summary>The place a window dropped on this edge at a point (in its coordinates) docks in: the half of it the point is in.</summary>
    public ToolAnchor AnchorAt(Point at) => (down ? at.Y < Bounds.Height / 2 : at.X < Bounds.Width / 2) ? first : second;

    /// <summary>A window's tab, made the first time: its icon and its name, the name turned to run down a side.</summary>
    private Border TabOf(ToolWindow window)
    {
        if (tabs.TryGetValue(window, out Border? made))
        {
            return made;
        }
        RowIcon icon = new() { Width = 14, Height = 14, DataKey = window.Icon };
        TextBlock name = new() { Classes = { StyleClasses.TabName }, Text = window.Title, VerticalAlignment = VerticalAlignment.Center };
        Control content = down
            ? new StackPanel { Spacing = Gaps.Small, HorizontalAlignment = HorizontalAlignment.Center, Children = { icon, new LayoutTransformControl { LayoutTransform = new RotateTransform(90), Child = name } } }
            : new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gaps.Small, VerticalAlignment = VerticalAlignment.Center, Children = { icon, name } };
        Border tab = new() { Classes = { StyleClasses.Tab }, Tag = window.Id, Focusable = true };
        ChosenMark.Hold(tab, content);
        AutomationProperties.SetName(tab, window.Title);
        AutomationProperties.SetControlTypeOverride(tab, AutomationControlType.Button);
        tab.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None)
            {
                host.EdgeTabClicked(window);
                e.Handled = true;
            }
        };
        // Tool windows live as long as the dock, as their tabs do.
        window.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolWindow.Title))
            {
                name.Text = window.Title;
                AutomationProperties.SetName(tab, window.Title);
            }
        };
        tab.PointerEntered += (_, _) => host.EdgeTabEntered(window);
        tab.PointerExited += (_, _) => host.EdgeTabLeft(window);
        tab.PointerPressed += (_, e) =>
        {
            host.EdgeTabPressed();
            if (e.GetCurrentPoint(tab).Properties.IsLeftButtonPressed)
            {
                host.Drag.Begin(window, e, tab);
                e.Handled = true;
            }
        };
        tab.PointerMoved += (_, e) => host.Drag.Continue(e);
        tab.PointerReleased += (_, e) =>
        {
            // A drag is no click; a click is a left press let go over its tab.
            if (!host.Drag.End(e) && e.InitialPressMouseButton == MouseButton.Left && new Rect(tab.Bounds.Size).Contains(e.GetPosition(tab)))
            {
                host.EdgeTabClicked(window);
            }
        };
        tabs[window] = tab;
        return tab;
    }
}
