using Avalonia;
using Avalonia.Controls;

namespace Passenger.App.Docking;

/// <summary>The window's frame: a stripe of tool window buttons down each edge (<see cref="Stripe"/>), the side areas
/// left and right of the editor, the bottom area under them, each side split in two halves that show one window each
/// (<see cref="ToolPane"/>). A window moves by a drag (<see cref="DockDrag"/>) or by its header's Move to, floats in a
/// <see cref="FloatWindow"/>, and the layout is captured for the next start.</summary>
public sealed class DockHost : Grid
{
    internal const double StripeWidth = 40;

    /// <summary>The part of the frame's width, and of its height, the editor keeps however large the areas were kept (on
    /// a larger screen, say) or dragged, unless a window is maximised.</summary>
    private const double EditorShare = 0.25;

    private readonly List<ToolWindow> windows = [];
    private readonly Dictionary<ToolAnchor, ToolPane> panes = [];
    private readonly Stripe leftStripe;
    private readonly Stripe rightStripe;
    private readonly Grid leftArea = new() { RowDefinitions = new RowDefinitions("*,0,*") };
    private readonly Grid rightArea = new() { RowDefinitions = new RowDefinitions("*,0,*") };
    private readonly Grid bottomArea = new() { ColumnDefinitions = new ColumnDefinitions("*,0,*") };
    private readonly ContentControl center = new();
    private readonly HairlineDivider leftDivider;
    private readonly HairlineDivider rightDivider;
    private readonly HairlineDivider bottomDivider;
    private readonly Border leftSeam = new() { IsHitTestVisible = false, ZIndex = 1, IsVisible = false };
    private readonly Border rightSeam = new() { IsHitTestVisible = false, ZIndex = 1, IsVisible = false };
    private double leftWidth = DefaultLeft;
    private double rightWidth = DefaultRight;
    private double bottomHeight = DefaultBottom;

    /// <summary>The areas' sizes in the layout the IDE starts with.</summary>
    private const double DefaultLeft = 280;

    private const double DefaultRight = 300;
    private const double DefaultBottom = 280;

    /// <summary>Each window's place as it was added, which Restore Default Layout puts it back in.</summary>
    private readonly Dictionary<string, DockedWindow> firstPlaces = [];

    /// <summary>The sizes the areas were last given, fitted to the frame: a definition no longer its size was dragged.</summary>
    private (double Left, double Right, double Bottom) given;

    private readonly Dictionary<ToolWindow, FloatWindow> floats = [];

    /// <summary>The places a layout applied gave windows this build does not have (a later build's), kept for the next.</summary>
    private Dictionary<string, DockedWindow> others = [];
    private (ToolWindow Window, double Left, double Right, double Bottom)? maximized;
    private List<ToolWindow>? hiddenAll;

    public DockHost()
    {
        ColumnDefinitions = new ColumnDefinitions($"{StripeWidth},0,0,*,0,0,{StripeWidth}");
        RowDefinitions = new RowDefinitions("*,0,0");
        leftStripe = new Stripe(this, ToolAnchor.LeftTop, ToolAnchor.LeftBottom, ToolAnchor.BottomLeft);
        rightStripe = new Stripe(this, ToolAnchor.RightTop, ToolAnchor.RightBottom, ToolAnchor.BottomRight);
        Place(leftStripe, 0, 0, rowSpan: 3);
        Place(rightStripe, 0, 6, rowSpan: 3);
        leftDivider = Divider(GridResizeDirection.Columns);
        rightDivider = Divider(GridResizeDirection.Columns);
        bottomDivider = Divider(GridResizeDirection.Rows);
        Place(leftArea, 0, 1);
        Place(leftDivider, 0, 2);
        Place(center, 0, 3);
        Place(rightDivider, 0, 4);
        Place(rightArea, 0, 5);
        Place(bottomDivider, 1, 1, columnSpan: 5);
        Place(bottomArea, 2, 1, columnSpan: 5);
        // The bottom area meets a side area of the same ground, so a seam marks that edge.
        Place(leftSeam, 1, 1);
        Place(rightSeam, 1, 5);
        foreach (Border seam in (Border[])[leftSeam, rightSeam])
        {
            seam.Bind(Border.BackgroundProperty, seam.GetResourceObservable(TokenKeys.Divider));
        }
        foreach (ToolAnchor anchor in Enum.GetValues<ToolAnchor>())
        {
            ToolPane pane = new(this);
            panes[anchor] = pane;
            (Grid area, int slot) = Area(anchor);
            if (area == bottomArea)
            {
                Grid.SetColumn(pane, slot);
            }
            else
            {
                Grid.SetRow(pane, slot);
            }
            area.Children.Add(pane);
        }
        leftArea.Children.Add(new HairlineDivider(GridResizeDirection.Rows) { [Grid.RowProperty] = 1 });
        rightArea.Children.Add(new HairlineDivider(GridResizeDirection.Rows) { [Grid.RowProperty] = 1 });
        bottomArea.Children.Add(new HairlineDivider(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 });
        DockDrag drag = new(this, leftStripe, rightStripe);
        Drag = drag.Gesture;
        Place(drag.Overlay, 0, 0, rowSpan: 3, columnSpan: 7);
        Layout();
    }

    /// <summary>A window dragged by its stripe button or its header.</summary>
    internal DragGesture<ToolWindow> Drag { get; }

    /// <summary>The left area's width as laid out, or as kept while it is closed: about what a window dropped on a side
    /// would have.</summary>
    internal double LeftAreaWidth => ColumnDefinitions[1].ActualWidth > 0 ? ColumnDefinitions[1].ActualWidth : leftWidth;

    /// <summary>The bottom area's height as kept, open or not.</summary>
    internal double BottomAreaHeight => bottomHeight;

    /// <summary>The editor area, between the side areas and above the bottom one.</summary>
    public Control? Center
    {
        get => center.Content as Control;
        set => center.Content = value;
    }

    public IReadOnlyList<ToolWindow> Windows => windows;

    /// <summary>The window shown last, which Shift+Escape hides.</summary>
    public ToolWindow? Active { get; private set; }

    /// <summary>A window header's options menu while it is open.</summary>
    public MenuFlyout? OpenOptions { get; internal set; }

    /// <summary>A window was moved, shown, hidden or resized: the layout to keep has changed.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>A floating or windowed tool window's own window opened, before it shows: what the IDE puts in each of its
    /// windows (its keys, its popups) goes into it.</summary>
    public event EventHandler<Window>? WindowOpened;

    /// <summary>The window of an id (<see cref="ToolWindowIds"/>); an id no window has is the caller's mistake, and throws.</summary>
    public ToolWindow this[[IdentifierOf(typeof(ToolWindowIds))] string id] => Find(id) ?? throw new ArgumentException($"No tool window has the id '{id}'", nameof(id));

    /// <summary>The window of an id, or null: a kept layout may name one this build has not.</summary>
    private ToolWindow? Find(string id) => windows.FirstOrDefault(w => w.Id == id);

    /// <summary>The window's actions, which each tool window's show action joins as it is added, and whose keymap gives
    /// the stripe buttons' tips.</summary>
    public ActionRegistry? Actions { get; set; }

    public void Add(ToolWindow window)
    {
        window.Order = windows.Count(w => w.Anchor == window.Anchor);
        windows.Add(window);
        firstPlaces[window.Id] = new DockedWindow(window.Anchor, window.Order, window.OpenAtFirst);
        if (window.ShowAction(this) is { } shows)
        {
            Actions?.Add(shows);
        }
        Layout();
        if (window.OpenAtFirst)
        {
            Show(window.Id);
        }
    }

    /// <summary>Restore Default Layout: every window back where it was added, docked, open only if it opens at first,
    /// and the areas at their first sizes, halves shared alike.</summary>
    public void RestoreDefault()
    {
        foreach (Grid area in (Grid[])[leftArea, rightArea])
        {
            area.RowDefinitions[0].Height = area.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
        }
        bottomArea.ColumnDefinitions[0].Width = bottomArea.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        Apply(new DockLayout { Windows = new(firstPlaces), LeftWidth = DefaultLeft, RightWidth = DefaultRight, BottomHeight = DefaultBottom });
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows a window in its place, hiding the one that was there.</summary>
    public void Show([IdentifierOf(typeof(ToolWindowIds))] string id)
    {
        ToolWindow window = this[id];
        hiddenAll = null;
        if (window.Mode == ToolMode.Docked)
        {
            foreach (ToolWindow other in windows.Where(w => w.Anchor == window.Anchor && w != window && w.Mode == ToolMode.Docked))
            {
                other.IsShown = false;
            }
        }
        window.IsShown = true;
        Active = window;
        if (window.Mode != ToolMode.Docked)
        {
            Float(window);
        }
        Layout();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Hide([IdentifierOf(typeof(ToolWindowIds))] string id)
    {
        if (this[id] is { IsShown: true } window)
        {
            Remember();
            if (maximized?.Window == window)
            {
                Restore();
            }
            window.IsShown = false;
            if (floats.Remove(window, out FloatWindow? open))
            {
                window.WindowBounds = open.Placement;
                open.CloseQuietly();
            }
            if (Active == window)
            {
                Active = windows.FirstOrDefault(w => w.IsShown);
            }
            Layout();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Puts a window in its place, over the IDE in a window of its own, or in an independent window (its View
    /// Mode); it stays open if it was.</summary>
    public void SetMode(ToolWindow window, ToolMode mode)
    {
        if (window.Mode == mode)
        {
            return;
        }
        bool shown = window.IsShown;
        if (shown)
        {
            Hide(window.Id);
        }
        window.Mode = mode;
        if (shown)
        {
            Show(window.Id);
        }
        else
        {
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Gives a docked window all of the frame it can have, or gives it back its size (Ctrl+Shift+', a
    /// double-click on its header).</summary>
    public void ToggleMaximize(ToolWindow? window = null)
    {
        if (maximized is not null)
        {
            Restore();
            return;
        }
        if ((window ?? Active) is not { IsShown: true, Mode: ToolMode.Docked } target)
        {
            return;
        }
        Remember();
        maximized = (target, leftWidth, rightWidth, bottomHeight);
        double width = Bounds.Width - (2 * StripeWidth) - 80;
        switch (target.Anchor)
        {
            case ToolAnchor.BottomLeft or ToolAnchor.BottomRight:
                bottomHeight = Bounds.Height - 80;
                break;
            case ToolAnchor.LeftTop or ToolAnchor.LeftBottom:
                leftWidth = width - given.Right;
                break;
            default:
                rightWidth = width - given.Left;
                break;
        }
        Layout();
    }

    public bool IsMaximized => maximized is not null;

    /// <summary>Hides every docked window open, or opens them again (Ctrl+Shift+F12).</summary>
    public void ToggleHideAll()
    {
        if (hiddenAll is { } hidden)
        {
            foreach (ToolWindow window in hidden)
            {
                Show(window.Id);
            }
            hiddenAll = null;
            return;
        }
        List<ToolWindow> open = [.. windows.Where(w => w.IsShown && w.Mode == ToolMode.Docked)];
        foreach (ToolWindow window in open)
        {
            Hide(window.Id);
        }
        hiddenAll = open;
    }

    private void Restore()
    {
        if (maximized is { } was)
        {
            (leftWidth, rightWidth, bottomHeight) = (was.Left, was.Right, was.Bottom);
            maximized = null;
            Layout();
        }
    }

    /// <summary>A floating or windowed window's own window, opened (where it last stood) or brought forward.</summary>
    private void Float(ToolWindow window)
    {
        if (floats.TryGetValue(window, out FloatWindow? open))
        {
            open.Activate();
            return;
        }
        FloatWindow created = new(this, window.WindowBounds, new Size(520, 420)) { Title = window.Title, ShowInTaskbar = window.Mode == ToolMode.Windowed };
        ToolPane pane = new(this) { Floating = created };
        pane.Show(window);
        created.Content = pane;
        created.Dismissed += (_, _) => FloatClosed(window, created);
        // The tool window outlives its window: the pane lets it go, or it holds that window's whole tree.
        created.Closed += (_, _) => pane.Show(null);
        floats[window] = created;
        WindowOpened?.Invoke(this, created);
        if (window.Mode == ToolMode.Floating && TopLevel.GetTopLevel(this) is Window owner)
        {
            created.Show(owner);
        }
        else
        {
            created.Show();
        }
    }

    /// <summary>The person closed a floating window's own window: the tool window is hidden, its bounds kept.</summary>
    private void FloatClosed(ToolWindow window, FloatWindow closed)
    {
        if (floats.Remove(window))
        {
            window.WindowBounds = closed.Placement;
            window.IsShown = false;
            Layout();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Toggle([IdentifierOf(typeof(ToolWindowIds))] string id)
    {
        if (this[id].IsShown)
        {
            Hide(id);
        }
        else
        {
            Show(id);
        }
    }

    /// <summary>Hides the window shown last (Shift+Escape); false when none is open.</summary>
    public bool HideActive()
    {
        if (Active is not { IsShown: true } active)
        {
            return false;
        }
        Hide(active.Id);
        return true;
    }

    public bool IsShown([IdentifierOf(typeof(ToolWindowIds))] string id) => this[id].IsShown;

    /// <summary>Moves a window to another place (at an index among its buttons there), shown if it was.</summary>
    public void Move(ToolWindow window, ToolAnchor anchor, int? index = null)
    {
        bool shown = window.IsShown;
        Remember();
        List<ToolWindow> there = [.. windows.Where(w => w.Anchor == anchor && w != window).OrderBy(w => w.Order)];
        there.Insert(Math.Clamp(index ?? there.Count, 0, there.Count), window);
        window.Anchor = anchor;
        for (int i = 0; i < there.Count; i++)
        {
            there[i].Order = i;
        }
        Renumber();
        window.IsShown = false;
        if (shown)
        {
            Show(window.Id);
        }
        else
        {
            Layout();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The layout as it stands, to keep: while a window is maximised, with the sizes it gives back.</summary>
    public DockLayout Capture()
    {
        Remember();
        (double left, double right, double bottom) = maximized is { } was ? (was.Left, was.Right, was.Bottom) : (leftWidth, rightWidth, bottomHeight);
        Dictionary<string, DockedWindow> placed = windows.ToDictionary(w => w.Id, w => new DockedWindow(w.Anchor, w.Order, w.IsShown)
        {
            Mode = w.Mode,
            Bounds = (floats.TryGetValue(w, out FloatWindow? open) ? open.Placement : w.WindowBounds) is { } at ? [at.X, at.Y, at.Width, at.Height] : null,
        });
        foreach ((string id, DockedWindow other) in others)
        {
            placed.TryAdd(id, other);
        }
        return new DockLayout { Windows = placed, LeftWidth = left, RightWidth = right, BottomHeight = bottom };
    }

    /// <summary>A kept layout over the windows there are; one it does not name stays where it was added. Applied over
    /// another (a folder's over the window's), the windows floating now are docked back first.</summary>
    public void Apply(DockLayout layout)
    {
        foreach ((ToolWindow window, FloatWindow open) in floats.ToList())
        {
            window.WindowBounds = open.Placement;
            open.CloseQuietly();
        }
        floats.Clear();
        Restore();
        hiddenAll = null;
        others = layout.Windows.Where(w => Find(w.Key) is null).ToDictionary();
        foreach (ToolWindow window in windows)
        {
            if (layout.Windows.GetValueOrDefault(window.Id) is { } placed)
            {
                window.Anchor = placed.Anchor;
                window.Order = placed.Order;
                window.IsShown = placed.Shown;
                window.Mode = placed.Mode;
                window.WindowBounds = placed.Bounds is [int x, int y, int width, int height] ? new PixelRect(x, y, width, height) : null;
            }
        }
        // Two shown in one place keep the first.
        foreach (IGrouping<ToolAnchor, ToolWindow> place in windows.Where(w => w.IsShown && w.Mode == ToolMode.Docked).GroupBy(w => w.Anchor))
        {
            foreach (ToolWindow extra in place.OrderBy(w => w.Order).Skip(1))
            {
                extra.IsShown = false;
            }
        }
        Renumber();
        leftWidth = Positive(layout.LeftWidth, leftWidth);
        rightWidth = Positive(layout.RightWidth, rightWidth);
        bottomHeight = Positive(layout.BottomHeight, bottomHeight);
        Active = windows.FirstOrDefault(w => w.IsShown);
        Layout();
        // A floating window kept open opens again once the IDE's own window is there to float over.
        foreach (ToolWindow floating in windows.Where(w => w.IsShown && w.Mode != ToolMode.Docked))
        {
            if (TopLevel.GetTopLevel(this) is null)
            {
                AttachedToVisualTree += (_, _) =>
                {
                    if (floating.IsShown && floating.Mode != ToolMode.Docked)
                    {
                        Float(floating);
                    }
                };
            }
            else
            {
                Float(floating);
            }
        }

        static double Positive(double value, double fallback) => value > 40 ? value : fallback;
    }


    /// <summary>The stripes' tips said again with the keymap in force, which a change of keymap or shortcut has replaced.</summary>
    public void FollowKeys()
    {
        leftStripe.Fill(windows);
        rightStripe.Fill(windows);
    }

    internal void Layout()
    {
        foreach ((ToolAnchor anchor, ToolPane pane) in panes)
        {
            pane.Show(windows.FirstOrDefault(w => w.Anchor == anchor && w.IsShown && w.Mode == ToolMode.Docked));
        }
        Split(leftArea, panes[ToolAnchor.LeftTop], panes[ToolAnchor.LeftBottom], rows: true);
        Split(rightArea, panes[ToolAnchor.RightTop], panes[ToolAnchor.RightBottom], rows: true);
        Split(bottomArea, panes[ToolAnchor.BottomLeft], panes[ToolAnchor.BottomRight], rows: false);
        leftStripe.Fill(windows);
        rightStripe.Fill(windows);
        Fit();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // A size the splitter's keys changed, which no drag's end kept, is kept before the sizes are fitted again.
        Remember();
        Fit();
    }

    /// <summary>Gives the open areas their sizes, fitted to the frame as it is: on one smaller than they were kept on (a
    /// laptop's, after a monitor's), the sides shrink alike, and the bottom, until the editor keeps its share, where a
    /// splitter stops too. Maximised, a window may take all of the frame but its edge.</summary>
    private void Fit()
    {
        bool leftOpen = panes[ToolAnchor.LeftTop].Window is not null || panes[ToolAnchor.LeftBottom].Window is not null;
        bool rightOpen = panes[ToolAnchor.RightTop].Window is not null || panes[ToolAnchor.RightBottom].Window is not null;
        bool bottomOpen = panes[ToolAnchor.BottomLeft].Window is not null || panes[ToolAnchor.BottomRight].Window is not null;
        double share = maximized is null ? EditorShare : 0;
        // Before the first layout there is no frame: the sizes are fitted once there is.
        double across = Math.Max(0, Bounds.Width - (2 * StripeWidth) - 2);
        double down = Math.Max(0, Bounds.Height - 1);
        double left = leftOpen ? leftWidth : 0;
        double right = rightOpen ? rightWidth : 0;
        double bottom = bottomOpen ? bottomHeight : 0;
        if (across > 0 && left + right > across * (1 - share))
        {
            double scale = across * (1 - share) / (left + right);
            (left, right) = (left * scale, right * scale);
        }
        if (down > 0)
        {
            bottom = Math.Min(bottom, down * (1 - share));
        }
        ColumnDefinitions[1].Width = new GridLength(left);
        ColumnDefinitions[2].Width = new GridLength(leftOpen ? 1 : 0);
        ColumnDefinitions[3].MinWidth = across * share;
        ColumnDefinitions[4].Width = new GridLength(rightOpen ? 1 : 0);
        ColumnDefinitions[5].Width = new GridLength(right);
        RowDefinitions[0].MinHeight = down * share;
        RowDefinitions[1].Height = new GridLength(bottomOpen ? 1 : 0);
        RowDefinitions[2].Height = new GridLength(bottom);
        // A closed area's divider, over the editor's edge, would drag an empty area open.
        leftDivider.IsVisible = leftOpen;
        rightDivider.IsVisible = rightOpen;
        bottomDivider.IsVisible = bottomOpen;
        leftSeam.IsVisible = leftOpen && bottomOpen;
        rightSeam.IsVisible = rightOpen && bottomOpen;
        given = (left, right, bottom);
    }

    /// <summary>A side's two halves: both shown share it, as the person last split it while both were; one shown fills
    /// it.</summary>
    private static void Split(Grid area, ToolPane first, ToolPane second, bool rows)
    {
        bool a = first.Window is not null;
        bool b = second.Window is not null;
        GridLength firstHalf = rows ? area.RowDefinitions[0].Height : area.ColumnDefinitions[0].Width;
        GridLength secondHalf = rows ? area.RowDefinitions[2].Height : area.ColumnDefinitions[2].Width;
        bool kept = a && b && firstHalf is { IsStar: true, Value: > 0 } && secondHalf is { IsStar: true, Value: > 0 };
        GridLength Star(bool on, GridLength was) => kept ? was : on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (rows)
        {
            area.RowDefinitions[0].Height = Star(a, firstHalf);
            area.RowDefinitions[1].Height = new GridLength(a && b ? 1 : 0);
            area.RowDefinitions[2].Height = Star(b, secondHalf);
        }
        else
        {
            area.ColumnDefinitions[0].Width = Star(a, firstHalf);
            area.ColumnDefinitions[1].Width = new GridLength(a && b ? 1 : 0);
            area.ColumnDefinitions[2].Width = Star(b, secondHalf);
        }
        area.Children.OfType<HairlineDivider>().Single().IsVisible = a && b;
    }

    /// <summary>Keeps the size the person dragged an area to (its definition no longer the size it was given), kept
    /// while the area is closed. While a window is maximised, the sizes kept are the ones it gives back.</summary>
    private void Remember()
    {
        if (maximized is not null)
        {
            return;
        }
        leftWidth = Dragged(ColumnDefinitions[1].Width, given.Left, leftWidth);
        rightWidth = Dragged(ColumnDefinitions[5].Width, given.Right, rightWidth);
        bottomHeight = Dragged(RowDefinitions[2].Height, given.Bottom, bottomHeight);

        static double Dragged(GridLength now, double given, double kept) => now.IsAbsolute && now.Value > 40 && now.Value != given ? now.Value : kept;
    }

    private void Renumber()
    {
        foreach (IGrouping<ToolAnchor, ToolWindow> place in windows.GroupBy(w => w.Anchor))
        {
            int i = 0;
            foreach (ToolWindow window in place.OrderBy(w => w.Order))
            {
                window.Order = i++;
            }
        }
    }

    private (Grid Area, int Slot) Area(ToolAnchor anchor) => anchor switch
    {
        ToolAnchor.LeftTop => (leftArea, 0),
        ToolAnchor.LeftBottom => (leftArea, 2),
        ToolAnchor.RightTop => (rightArea, 0),
        ToolAnchor.RightBottom => (rightArea, 2),
        ToolAnchor.BottomLeft => (bottomArea, 0),
        _ => (bottomArea, 2),
    };

    private void Place(Control control, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        SetRow(control, row);
        SetColumn(control, column);
        SetRowSpan(control, rowSpan);
        SetColumnSpan(control, columnSpan);
        Children.Add(control);
    }

    /// <summary>An area's divider: a tool window's ground beside the editor's, told apart by shade, so no line.</summary>
    private HairlineDivider Divider(GridResizeDirection direction)
    {
        HairlineDivider divider = new(direction, drawn: false);
        divider.Splitter.DragCompleted += (_, _) =>
        {
            Remember();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        };
        return divider;
    }
}
