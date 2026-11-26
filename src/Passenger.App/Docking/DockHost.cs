using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Passenger.Editor;

namespace Passenger.App.Docking;

/// <summary>The window's frame: the side areas left and right of the editor, the bottom area under them (or, in the wide
/// layout, <see cref="IsWide"/>, between them, the sides running the whole height), each side split in two halves that
/// show one window each (<see cref="ToolPane"/>), and the windows named in one of two looks. By stripe
/// icons, a stripe of tool window buttons down each edge (<see cref="Stripe"/>) names and switches every window; by titled
/// tabs, no stripe stands: a place's windows are tabs along its foot, and a hidden one is a tab along the frame's edge
/// (<see cref="EdgeTabs"/>) that opens it over the editors (<see cref="Peek"/>). A window moves by a drag
/// (<see cref="DockDrag"/>) or by its header's Move to, floats in a <see cref="FloatWindow"/>, and the layout is captured
/// for the next start.</summary>
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
    private readonly EdgeTabs leftEdge;
    private readonly EdgeTabs rightEdge;
    private readonly EdgeTabs bottomEdge;

    /// <summary>The window opened over the editors from its edge tab, while one is (<see cref="Peek"/>), and the frame it
    /// stands in there.</summary>
    private ToolWindow? peeked;

    private readonly ToolPane peekPane;
    private readonly Border peekFrame = new() { Classes = { StyleClasses.ToolPeek }, ZIndex = 1, IsVisible = false };

    /// <summary>The edge of the window over the editors toward them, which a drag resizes it by (<see cref="ResizePeek"/>),
    /// and where a drag of it began: the pointer's place and the size it had.</summary>
    private readonly Border peekGrip = new() { Classes = { StyleClasses.PeekGrip } };
    private (Point At, double Size)? gripFrom;
    private readonly Cursor acrossCursor = new(StandardCursorType.SizeWestEast);
    private readonly Cursor downCursor = new(StandardCursorType.SizeNorthSouth);

    /// <summary>The most the window over the editors may be across (or, from the foot, high), as last laid out.</summary>
    private double peekRoom;

    /// <summary>How long the pointer rests on an edge tab before its window opens over the editors, and how long it is
    /// away from that window and its tab before one opened so goes again.</summary>
    internal static readonly TimeSpan HoverRests = TimeSpan.FromMilliseconds(400);

    internal static readonly TimeSpan HoverLeft = TimeSpan.FromMilliseconds(500);

    /// <summary>Whether the window over the editors came by the pointer resting on its tab, without the keys: it goes again
    /// once the pointer has been away from it and its tab for a moment (<see cref="HoverLeft"/>), until a press in it, or on
    /// its tab, keeps it.</summary>
    private bool hovered;

    /// <summary>The edge tab the pointer is on, if any, whose window opens once it rests there.</summary>
    private ToolWindow? hoverTab;

    private readonly Debounce hoverRest;
    private readonly Debounce hoverAway;
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
    private const double DefaultLeft = 300;

    private const double DefaultRight = 300;
    private const double DefaultBottom = 280;

    /// <summary>Each window's place as it was added, which Restore Default Layout puts it back in unless the theme places it.</summary>
    private readonly Dictionary<string, DockedWindow> firstPlaces = [];

    /// <summary>The theme's default layout, over <see cref="firstPlaces"/>; null for Passenger's own.</summary>
    private ThemeLayout? themed;

    /// <summary>The sizes the areas were last given, fitted to the frame: a definition no longer its size was dragged.</summary>
    private (double Left, double Right, double Bottom) given;

    private readonly Dictionary<ToolWindow, FloatWindow> floats = [];

    /// <summary>The places a layout applied gave windows this build does not have (a later build's), kept for the next.</summary>
    private Dictionary<string, DockedWindow> others = [];
    private (ToolWindow Window, double Left, double Right, double Bottom)? maximized;
    private List<ToolWindow>? hiddenAll;

    /// <summary>The windows Hide Side Windows hid, which it shows again (<see cref="ToggleHideSides"/>).</summary>
    private List<ToolWindow>? hiddenSides;

    /// <summary>The IDE's window the frame is in, once it is.</summary>
    private Window? host;

    private readonly DockDrag drag;

    private bool titledTabs;
    private bool closesOffStripe;

    /// <summary>Whether a tool window is being dragged (<see cref="Dragging"/>).</summary>
    private bool dragged;

    /// <summary>When the side areas run the frame's whole height (<see cref="Wide"/>).</summary>
    private WideLayout wideLayout;

    /// <summary>Whether the side areas run the frame's whole height as it stands (<see cref="IsWide"/>).</summary>
    private bool wide;

    /// <summary>The areas' sizes in the layout not standing, kept for when it comes back; while the wide layout does not
    /// stand, null where it never has, so it takes the other's sizes as it first comes.</summary>
    private (double Left, double Right, double Bottom)? apart;

    /// <summary>The window's width over its height past which it takes the wide layout by itself, and under which it goes
    /// back: a 16:9 or 16:10 screen's window stays under 2 whatever bars it is maximised under, and a 21:9 ultrawide
    /// screen's is 2.3 or more, so neither stands near an edge a resize would flip it at, nor does half an ultrawide.</summary>
    public const double WideFrom = 2.2;

    public const double WideUntil = 2;

    public DockHost()
    {
        ColumnDefinitions = new ColumnDefinitions(string.Create(CultureInfo.InvariantCulture, $"{StripeWidth},0,0,*,0,0,{StripeWidth}"));
        RowDefinitions = new RowDefinitions("*,0,0,0");
        leftStripe = new Stripe(this, ToolAnchor.LeftTop, ToolAnchor.LeftBottom, ToolAnchor.BottomLeft);
        rightStripe = new Stripe(this, ToolAnchor.RightTop, ToolAnchor.RightBottom, ToolAnchor.BottomRight);
        Place(leftStripe, 0, 0, rowSpan: 4);
        Place(rightStripe, 0, 6, rowSpan: 4);
        leftEdge = new EdgeTabs(this, ToolAnchor.LeftTop, ToolAnchor.LeftBottom, down: true, "Hidden tool windows, left edge");
        rightEdge = new EdgeTabs(this, ToolAnchor.RightTop, ToolAnchor.RightBottom, down: true, "Hidden tool windows, right edge");
        bottomEdge = new EdgeTabs(this, ToolAnchor.BottomLeft, ToolAnchor.BottomRight, down: false, "Hidden tool windows, bottom edge");
        Place(leftEdge, 0, 0, rowSpan: 4);
        Place(rightEdge, 0, 6, rowSpan: 4);
        Place(bottomEdge, 3, 1, columnSpan: 5);
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
            // A size the splitter's keys changed is kept first, as a resize keeps it.
            pane.LeastChanged += (_, _) =>
            {
                Remember();
                Fit();
            };
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
        peekPane = new ToolPane(this) { Peeking = true };
        peekPane.LeastChanged += (_, _) => Fit();
        peekFrame.Child = new Panel { Children = { peekPane, peekGrip } };
        hoverRest = new Debounce(HoverRests, () =>
        {
            HoverRested();
            return Task.CompletedTask;
        });
        hoverAway = new Debounce(HoverLeft, () =>
        {
            if (peeked is { } over && hovered && !peekFrame.IsPointerOver && hoverTab != over)
            {
                Hide(over.Id);
            }
            return Task.CompletedTask;
        });
        peekFrame.PointerEntered += (_, _) => hoverAway.Stop();
        peekFrame.PointerExited += (_, _) => LeftPeek();
        peekGrip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(peekGrip).Properties.IsLeftButtonPressed && peeked is { } over)
            {
                gripFrom = (e.GetPosition(this), over.Anchor is ToolAnchor.BottomLeft or ToolAnchor.BottomRight ? peekFrame.Bounds.Height : peekFrame.Bounds.Width);
                e.Pointer.Capture(peekGrip);
                e.Handled = true;
            }
        };
        peekGrip.PointerMoved += (_, e) =>
        {
            if (gripFrom is { } from)
            {
                ResizePeek(from, e.GetPosition(this));
            }
        };
        peekGrip.PointerReleased += (_, e) =>
        {
            if (gripFrom is not null)
            {
                gripFrom = null;
                e.Pointer.Capture(null);
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        Place(peekFrame, 0, 1, rowSpan: 3, columnSpan: 5);
        drag = new(this, leftStripe, rightStripe, leftEdge, rightEdge);
        Drag = drag.Gesture;
        Drag.DraggingChanged += (_, e) => Dragging(e.Dragging);
        Place(drag.Overlay, 0, 0, rowSpan: 4, columnSpan: 7);
        Layout();
        AttachedToVisualTree += (_, _) => FollowHost();
        // A press anywhere in the frame but the window over the editors and the edges' tabs puts it away; one in it keeps it.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (peeked is not null && e.Source is Visual pressed && !Within(pressed, peekFrame, leftEdge, rightEdge, bottomEdge))
            {
                Hide(peeked.Id);
            }
            else if (peeked is not null && e.Source is Visual inside && Within(inside, peekFrame))
            {
                hovered = false;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static bool Within(Visual visual, params Visual[] parts) => parts.Any(part => part == visual || part.IsVisualAncestorOf(visual));

    /// <summary>The IDE's window's close: its floating and windowed tool windows go with it, quietly, each kept shown where
    /// it stood by the layout kept as it closed.</summary>
    private void FollowHost()
    {
        if (host is null && TopLevel.GetTopLevel(this) is Window window)
        {
            host = window;
            host.Closed += (_, _) => CloseFloats();
            // The keys gone anywhere in the IDE's window but the window over the editors and the edges' tabs put it away.
            host.AddHandler(GotFocusEvent, (_, e) =>
            {
                if (peeked is not null && e.Source is Visual focused && !Within(focused, peekFrame, leftEdge, rightEdge, bottomEdge))
                {
                    Hide(peeked.Id);
                }
            }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    /// <summary>A window dragged by its stripe button or its header.</summary>
    internal DragGesture<ToolWindow> Drag { get; }

    /// <summary>The left area's width as laid out, or as kept while it is closed: about what a window dropped on a side
    /// would have.</summary>
    internal double LeftAreaWidth => ColumnDefinitions[1].ActualWidth > 0 ? ColumnDefinitions[1].ActualWidth : leftWidth;

    /// <summary>The right area's width as laid out, or as kept while it is closed.</summary>
    internal double RightAreaWidth => ColumnDefinitions[5].ActualWidth > 0 ? ColumnDefinitions[5].ActualWidth : rightWidth;

    /// <summary>The bottom area's height as kept, open or not.</summary>
    internal double BottomAreaHeight => bottomHeight;

    /// <summary>The left stripe's width as laid out, or the left edge's under titled tabs: none while it is folded away.</summary>
    internal double LeftStripeWidth => ColumnDefinitions[0].ActualWidth;

    /// <summary>The right stripe's width as laid out, or the right edge's under titled tabs: none while it is folded away.</summary>
    internal double RightStripeWidth => ColumnDefinitions[6].ActualWidth;

    /// <summary>A tool window dragged (by its stripe icon, its header, its titled tab, its edge tab or its editor tab), or
    /// let go: while it is, every stripe shows under stripe icons, as each is a place to drop it.</summary>
    internal void Dragging(bool dragging)
    {
        if (dragging != dragged)
        {
            dragged = dragging;
            FoldStripes();
            Fit();
        }
    }

    /// <summary>The editor area, between the side areas and above the bottom one.</summary>
    public Control? Center
    {
        get => center.Content as Control;
        set => center.Content = value;
    }

    public IReadOnlyList<ToolWindow> Windows => windows;

    /// <summary>Whether the windows are named by titled tabs, a place's along its foot and a hidden one's along the frame's
    /// edge, no stripe standing; else by stripe icons alone.</summary>
    public bool TitledTabs
    {
        get => titledTabs;
        set
        {
            if (value != titledTabs)
            {
                titledTabs = value;
                Layout();
            }
        }
    }

    /// <summary>Whether a window closed by its × leaves its stripe (or its edge, and its place's tabs) until it is opened
    /// again, rather than keeping its icon or its tab as a hidden window does. It governs only ×: a window off its stripe, however it went, stays
    /// off until it is opened or checked on its stripe's checklist again.</summary>
    public bool ClosesOffStripe
    {
        get => closesOffStripe;
        set
        {
            if (value != closesOffStripe)
            {
                closesOffStripe = value;
                Layout();
            }
        }
    }

    /// <summary>Where a window shown as an editor tab (<see cref="ToolMode.Editor"/>) goes: Show opens its tab among the
    /// editors, given the keys when brought forward, and Close closes it; its tab closed there tells
    /// <see cref="EditorTabClosed"/>.</summary>
    public (Action<ToolWindow, bool> Show, Action<ToolWindow> Close)? EditorTabs { get; set; }

    /// <summary>A window's editor tab closed among the editors: the window is hidden, its icon kept. One closed as the window
    /// stopped serving the folder stays shown in its layout, to come back.</summary>
    public void EditorTabClosed(ToolWindow window)
    {
        if (window is { Mode: ToolMode.Editor, IsShown: true, Unserved: null })
        {
            window.IsShown = false;
            PassActive(window);
            Layout();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The place a window dragged at a point on the screen would dock in, lit in the frame's overlay; null where
    /// it would dock nowhere.</summary>
    public DropZone? ZoneFor(ToolWindow window, PixelPoint at) => drag.ZoneFor(window, this.PointToClient(at));

    /// <summary>Docks a window where it was let go, at a point on the screen, as a drag of its icon docks it.</summary>
    public void DropAt(ToolWindow window, PixelPoint at) => drag.Drop(window, this.PointToClient(at));

    /// <summary>The editor area's place in the frame, where a window dropped goes into an editor tab.</summary>
    internal Rect EditorBounds => center.Bounds;

    /// <summary>The windows the stripes, the places' tabs and the edges name: those serving the open folder but those closed
    /// off them.</summary>
    private List<ToolWindow> OnStripes => [.. Served.Where(w => !w.IsClosed)];

    /// <summary>The windows that serve the open folder (<see cref="ToolWindow.Unserved"/>), which alone the stripes, the places,
    /// their tabs, the edges and the stripes' checklists show, and which alone are shown, peeked or brought.</summary>
    internal IEnumerable<ToolWindow> Served => windows.Where(w => w.Unserved is null);

    /// <summary>Why a window serves nothing in the open folder (<see cref="ToolWindow.Unserved"/>), null where it serves it:
    /// asked of each window as it is added, and of all again at <see cref="Serve"/>.</summary>
    public Func<ToolWindow, string?>? WhyUnserved { get; set; }

    /// <summary>Asks each window again whether it serves the open folder (<see cref="WhyUnserved"/>): one that stops leaves
    /// the stripes, its place, its tabs and its edge, put away from over the editors, its own window or editor tab closed
    /// quietly, shown or not as its layout keeps it; one that serves again comes back where it stood.</summary>
    public void Serve()
    {
        bool changed = false;
        foreach (ToolWindow window in windows)
        {
            string? why = WhyUnserved?.Invoke(window);
            if (why == window.Unserved)
            {
                continue;
            }
            bool served = why is null;
            bool was = window.Unserved is null;
            window.Unserved = why;
            changed |= served != was;
            if (served == was || !window.IsShown)
            {
                continue;
            }
            if (!served)
            {
                if (peeked == window)
                {
                    // Over the editors it is in no kept layout: it goes as Hide puts it away.
                    window.IsShown = false;
                    PeekAt(null);
                }
                if (maximized?.Window == window)
                {
                    Restore();
                }
                if (floats.Remove(window, out FloatWindow? open))
                {
                    window.WindowBounds = open.Placement;
                    open.CloseQuietly();
                }
                if (window.Mode == ToolMode.Editor)
                {
                    EditorTabs?.Close(window);
                }
                if (Active == window)
                {
                    Active = Served.FirstOrDefault(w => w.IsShown);
                }
            }
            else if (window.Mode == ToolMode.Editor)
            {
                EditorTabs?.Show(window, false);
            }
            else if (window.Mode != ToolMode.Docked && TopLevel.GetTopLevel(this) is not null)
            {
                Float(window, forward: false);
            }
        }
        if (changed)
        {
            Layout();
        }
    }

    /// <summary>The window shown last, which Shift+Escape hides.</summary>
    public ToolWindow? Active { get; private set; }

    /// <summary>The window whose view last took the keys, shown or not, which Jump to Last Tool Window gives them again.</summary>
    public ToolWindow? LastWithKeys { get; private set; }

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

    /// <summary>Asks for one of the window's actions where it runs, saying why one cannot: a pane's header buttons'.</summary>
    public Func<string, ActionContext, bool>? Request { get; set; }

    public void Add(ToolWindow window)
    {
        window.GotKeys += (_, _) => LastWithKeys = window;
        window.Order = windows.Count(w => w.Anchor == window.Anchor);
        window.Unserved = WhyUnserved?.Invoke(window);
        windows.Add(window);
        if (window.InEditorAtFirst)
        {
            window.Mode = ToolMode.Editor;
        }
        firstPlaces[window.Id] = new DockedWindow(window.Anchor, window.Order, window.OpenAtFirst) { Mode = window.Mode };
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

    /// <summary>Restore Default Layout: every window back in its default place (<see cref="Default"/>), docked (or among the
    /// editors where it starts there), open only if the default opens it, and the areas at their default sizes, halves
    /// shared alike.</summary>
    public void RestoreDefault()
    {
        foreach (Grid area in (Grid[])[leftArea, rightArea])
        {
            area.RowDefinitions[0].Height = area.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
        }
        bottomArea.ColumnDefinitions[0].Width = bottomArea.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        Apply(Default());
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The default layout: the theme's (<see cref="UseDefault"/>) laid over each window's place as it was added,
    /// the theme's windows first at each place it names, then the rest in their order, those it puts among the editors
    /// as editor tabs, their icons where Passenger puts them; one window docked open in each place, the first in its
    /// order.</summary>
    public DockLayout Default()
    {
        Dictionary<string, DockedWindow> places = new(firstPlaces);
        if (themed is { } theme)
        {
            foreach (ToolAnchor anchor in Enum.GetValues<ToolAnchor>())
            {
                IEnumerable<string> placed = theme.Places.Where(p => p.Anchor == anchor && firstPlaces.ContainsKey(p.Id)).Select(p => p.Id);
                IEnumerable<string> rest = firstPlaces.Where(p => p.Value.Anchor == anchor && !theme.Places.Any(t => t.Id == p.Key)).OrderBy(p => p.Value.Order).Select(p => p.Key);
                int order = 0;
                foreach (string id in placed.Concat(rest))
                {
                    places[id] = new DockedWindow(anchor, order++, theme.Open is { } open ? open.Contains(id) : firstPlaces[id].Shown) { Mode = firstPlaces[id].Mode };
                }
            }
            foreach (string id in theme.InEditor.Where(firstPlaces.ContainsKey))
            {
                places[id] = places[id] with { Mode = ToolMode.Editor };
            }
        }
        foreach (IGrouping<ToolAnchor, KeyValuePair<string, DockedWindow>> place in places.Where(p => p.Value is { Shown: true, Mode: ToolMode.Docked }).GroupBy(p => p.Value.Anchor).ToList())
        {
            foreach (KeyValuePair<string, DockedWindow> extra in place.OrderBy(p => p.Value.Order).Skip(1))
            {
                places[extra.Key] = extra.Value with { Shown = false };
            }
        }
        return new DockLayout
        {
            Windows = places,
            LeftWidth = themed?.LeftWidth ?? DefaultLeft,
            RightWidth = themed?.RightWidth ?? DefaultRight,
            BottomHeight = themed?.BottomHeight ?? DefaultBottom,
        };
    }

    /// <summary>Takes a theme's layout as the default (null for Passenger's own), window by window, a change the layout
    /// kept follows: a window still in the place and the mode the default before gave it goes where the new one puts it,
    /// and one still open or hidden as that default said opens as the new one says; an area still at that default's size
    /// takes the new one's, but a window serving nothing in the folder keeps its place. What the person arranged stays as
    /// they left it, and where nothing was, the whole of the new default is laid as Restore Default Layout lays it.</summary>
    public void UseDefault(ThemeLayout? layout)
    {
        if (ReferenceEquals(layout, themed))
        {
            return;
        }
        DockLayout before = Default();
        bool unarranged = Stands(before);
        themed = layout;
        if (unarranged)
        {
            RestoreDefault();
            return;
        }
        DockLayout now = Capture();
        DockLayout after = Default();
        Dictionary<string, DockedWindow> placed = new(now.Windows);
        HashSet<string> ownOpen = [];
        // Only the windows serving the folder take the new default; the others keep their places for when they serve.
        foreach (ToolWindow window in Served)
        {
            DockedWindow standing = now.Windows[window.Id];
            DockedWindow was = before.Windows[window.Id];
            DockedWindow next = after.Windows[window.Id];
            bool unmoved = (standing.Anchor, standing.Mode) == (was.Anchor, was.Mode);
            bool ownShown = standing.Shown != was.Shown;
            if (ownShown && standing.Shown)
            {
                ownOpen.Add(window.Id);
            }
            placed[window.Id] = standing with
            {
                Anchor = unmoved ? next.Anchor : standing.Anchor,
                Order = unmoved ? next.Order : standing.Order,
                Mode = unmoved ? next.Mode : standing.Mode,
                Shown = !standing.Closed && (ownShown ? standing.Shown : next.Shown),
            };
        }
        // A place the new default and the person both open a window in shows the person's.
        foreach (IGrouping<ToolAnchor, string> place in placed.Where(p => p.Value is { Shown: true, Mode: ToolMode.Docked } && Find(p.Key) is not null)
            .GroupBy(p => p.Value.Anchor, p => p.Key).Where(g => g.Count() > 1).ToList())
        {
            string kept = place.FirstOrDefault(ownOpen.Contains) ?? place.OrderBy(id => Find(id)!.Unserved is null ? 0 : 1).ThenBy(id => placed[id].Order).First();
            foreach (string other in place.Where(id => id != kept))
            {
                placed[other] = placed[other] with { Shown = false };
            }
        }
        Apply(new DockLayout
        {
            Windows = placed,
            LeftWidth = now.LeftWidth == before.LeftWidth ? after.LeftWidth : now.LeftWidth,
            RightWidth = now.RightWidth == before.RightWidth ? after.RightWidth : now.RightWidth,
            BottomHeight = now.BottomHeight == before.BottomHeight ? after.BottomHeight : now.BottomHeight,
            Wide = now.Wide is { } wideNow ? new AreaSizes(
                wideNow.LeftWidth == WideOf(before).LeftWidth ? WideOf(after).LeftWidth : wideNow.LeftWidth,
                wideNow.RightWidth == WideOf(before).RightWidth ? WideOf(after).RightWidth : wideNow.RightWidth,
                wideNow.BottomHeight == WideOf(before).BottomHeight ? WideOf(after).BottomHeight : wideNow.BottomHeight) : null,
        });
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A layout's sizes in the wide layout: its own, else those it keeps for the other.</summary>
    private static AreaSizes WideOf(DockLayout layout) => layout.Wide ?? new AreaSizes(layout.LeftWidth, layout.RightWidth, layout.BottomHeight);

    /// <summary>Whether every window stands docked where a layout puts it, open as it says, the areas at its sizes in both
    /// layouts.</summary>
    private bool Stands(DockLayout layout)
    {
        DockLayout now = Capture();
        return now.LeftWidth == layout.LeftWidth && now.RightWidth == layout.RightWidth && now.BottomHeight == layout.BottomHeight && WideOf(now) == WideOf(layout)
            && windows.All(w => layout.Windows.TryGetValue(w.Id, out DockedWindow? placed) && now.Windows[w.Id] is { } standing
                && (standing.Anchor, standing.Order, standing.Shown, standing.Closed, standing.Mode) == (placed.Anchor, placed.Order, placed.Shown, placed.Closed, placed.Mode));
    }

    /// <summary>Shows a window in its place, hiding the one that was there, with the keys where it takes them as shown
    /// (<see cref="ToolWindow.TakesKeysAsShown"/>); for an attached agent's call, behind (<see cref="ShowBehind"/>).</summary>
    public void Show([IdentifierOf(typeof(ToolWindowIds))] string id) => Show(id, forward: !AgentAsks.Beside);

    /// <summary>Shows a window in its place as <see cref="Show(string)"/> does, but brings no window forward: a floating or
    /// windowed one already open stays where it is, and one opened comes without the keys (an agent's asking).</summary>
    public void ShowBehind([IdentifierOf(typeof(ToolWindowIds))] string id) => Show(id, forward: false);

    /// <summary>Shows a window as <see cref="Show(string)"/> does, but never in the place of one that is to stay in view:
    /// docked where <paramref name="stays"/> is shown, it goes to the other half of that side first.</summary>
    public void ShowBeside([IdentifierOf(typeof(ToolWindowIds))] string id, [IdentifierOf(typeof(ToolWindowIds))] string stays)
    {
        ToolWindow window = this[id];
        if (window.Unserved is null && this[stays] is { IsShown: true, Unserved: null, Mode: ToolMode.Docked } kept && window.Mode == ToolMode.Docked && window.Anchor == kept.Anchor)
        {
            Move(window, OtherHalf(window.Anchor));
        }
        Show(id);
    }

    private static ToolAnchor OtherHalf(ToolAnchor anchor) => anchor switch
    {
        ToolAnchor.LeftTop => ToolAnchor.LeftBottom,
        ToolAnchor.LeftBottom => ToolAnchor.LeftTop,
        ToolAnchor.RightTop => ToolAnchor.RightBottom,
        ToolAnchor.RightBottom => ToolAnchor.RightTop,
        ToolAnchor.BottomLeft => ToolAnchor.BottomRight,
        _ => ToolAnchor.BottomLeft,
    };

    private void Show(string id, bool forward)
    {
        ToolWindow window = this[id];
        // One serving nothing here has nothing to show, and the window in its place stays.
        if (window.Unserved is not null)
        {
            return;
        }
        Shown(window);
        window.IsClosed = false;
        if (peeked == window)
        {
            // Shown in its place, a window over the editors docks there.
            PeekAt(null);
        }
        if (window.Mode == ToolMode.Docked)
        {
            foreach (ToolWindow other in windows.Where(w => w.Anchor == window.Anchor && w != window && w.Mode == ToolMode.Docked))
            {
                other.IsShown = false;
            }
        }
        window.IsShown = true;
        Active = window;
        if (window.Mode == ToolMode.Editor)
        {
            EditorTabs?.Show(window, forward);
        }
        else if (window.Mode != ToolMode.Docked)
        {
            Float(window, forward);
        }
        Layout();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        if (forward && window.TakesKeysAsShown && !AgentAsks.Inputting)
        {
            // A view just put in its place joins the window as the window is laid out.
            UpdateLayout();
            window.TakeKeys();
        }
    }

    /// <summary>Brings a window into view as the person asks for it (<see cref="Bring"/>) and gives it the keys
    /// (<see cref="ToolWindow.TakeKeys"/>); for an attached agent's call, only shows it behind, the keys left where they are.</summary>
    public void Activate([IdentifierOf(typeof(ToolWindowIds))] string id)
    {
        if (AgentAsks.Beside)
        {
            ShowBehind(id);
            return;
        }
        if (this[id].Unserved is not null)
        {
            return;
        }
        Bring(id);
        // A view just put in its place joins the window as the window is laid out.
        UpdateLayout();
        this[id].TakeKeys();
    }

    /// <summary>Shows a window as the person asks for it (its keys, View | Tool Windows, Find Action): under titled tabs, a
    /// docked one whose place stands hidden opens over the editors (<see cref="Peek"/>), its place left hidden; else in its
    /// place, as <see cref="Show"/> does. For an attached agent's call, it is shown behind, never over the editors.</summary>
    public void Bring([IdentifierOf(typeof(ToolWindowIds))] string id)
    {
        if (PeeksAt(this[id]) && !AgentAsks.Beside)
        {
            Peek(id);
        }
        else
        {
            Show(id);
        }
    }

    /// <summary>Whether a window would open over the editors rather than in its place: titled tabs name the windows, it
    /// docks, and no window of its place stands open there.</summary>
    private bool PeeksAt(ToolWindow window) => titledTabs && window is { Mode: ToolMode.Docked, Unserved: null } && panes[window.Anchor].Window is null;

    /// <summary>Opens a window whose place stands hidden over the editors beside its edge, as large as its area, until the
    /// keys or a press go elsewhere in the IDE's window, Escape or Hide puts it away, or its edge tab is clicked again; its
    /// place stays hidden, and a click on its header's Dock, or any show in its place, docks it there. Where it would not
    /// (<see cref="PeeksAt"/>), or for an attached agent's call, which never takes the person's keys, it is shown as
    /// <see cref="Show"/> shows it; for an agent's input into the window, it comes without the keys.</summary>
    public void Peek([IdentifierOf(typeof(ToolWindowIds))] string id)
    {
        ToolWindow window = this[id];
        if (!PeeksAt(window) || AgentAsks.Beside)
        {
            Show(id);
            return;
        }
        PeekOver(window, byHover: false);
    }

    /// <summary>Opens a window over the editors, with the keys unless the pointer resting on its tab opened it
    /// (<see cref="hovered"/>) or an agent's input did.</summary>
    private void PeekOver(ToolWindow window, bool byHover)
    {
        Shown(window);
        window.IsClosed = false;
        foreach (ToolWindow other in windows.Where(w => (w.Anchor == window.Anchor && w.Mode == ToolMode.Docked) || w == peeked).Where(w => w != window))
        {
            other.IsShown = false;
        }
        window.IsShown = true;
        PeekAt(window);
        hovered = byHover;
        Active = window;
        Layout();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        if (!byHover && !AgentAsks.Inputting)
        {
            UpdateLayout();
            window.TakeKeys();
        }
    }

    /// <summary>The window open over the editors, if one is.</summary>
    public ToolWindow? Peeked => peeked;

    /// <summary>A window opened over the editors (true), or put away from there, hidden or docked (false): for the window to
    /// tell a screen reader.</summary>
    public event EventHandler<(ToolWindow Window, bool Over)>? PeekChanged;

    private void PeekAt(ToolWindow? window)
    {
        if (peeked == window)
        {
            return;
        }
        ToolWindow? was = peeked;
        peeked = window;
        hovered = false;
        hoverAway.Stop();
        gripFrom = null;
        if (was is not null)
        {
            PeekChanged?.Invoke(this, (was, false));
        }
        if (window is not null)
        {
            PeekChanged?.Invoke(this, (window, true));
        }
    }

    /// <summary>The pointer onto an edge tab: once it rests there (<see cref="HoverRests"/>), the tab's window opens over the
    /// editors without the keys, in place of one opened so, but never over one a click or the keys opened.</summary>
    internal void EdgeTabEntered(ToolWindow window)
    {
        hoverTab = window;
        if (peeked == window)
        {
            hoverAway.Stop();
        }
        else if (PeeksAt(window) && (peeked is null || hovered))
        {
            hoverRest.Restart();
        }
    }

    /// <summary>The pointer off an edge tab: its window opens no more, and one it opened goes once the pointer stays away.</summary>
    internal void EdgeTabLeft(ToolWindow window)
    {
        if (hoverTab == window)
        {
            hoverTab = null;
        }
        hoverRest.Stop();
        LeftPeek();
    }

    /// <summary>A press on an edge tab: what it does is a click's or a drag's, never the rest's.</summary>
    internal void EdgeTabPressed()
    {
        hoverRest.Stop();
        hoverTab = null;
    }

    private void HoverRested()
    {
        if (hoverTab is { } window && peeked != window && PeeksAt(window) && (peeked is null || hovered))
        {
            PeekOver(window, byHover: true);
        }
    }

    /// <summary>The pointer away from the window over the editors or its tab: one the pointer's rest opened goes once it
    /// stays away.</summary>
    private void LeftPeek()
    {
        if (peeked is not null && hovered)
        {
            hoverAway.Restart();
        }
    }

    /// <summary>The window over the editors resized by a drag of its edge toward them, to <paramref name="at"/> from where
    /// the drag began: between its header's least and the editors' share, kept as its side's width (or the foot's height)
    /// for the next time it opens, over the editors or docked.</summary>
    private void ResizePeek((Point At, double Size) from, Point at)
    {
        if (peeked is not { } window)
        {
            return;
        }
        Vector moved = at - from.At;
        bool side = window.Anchor is not (ToolAnchor.BottomLeft or ToolAnchor.BottomRight);
        double wanted = window.Anchor switch
        {
            ToolAnchor.LeftTop or ToolAnchor.LeftBottom => from.Size + moved.X,
            ToolAnchor.RightTop or ToolAnchor.RightBottom => from.Size - moved.X,
            _ => from.Size - moved.Y,
        };
        double size = Math.Clamp(wanted, side ? peekPane.LeastWidth : ThemeFiles.LeastArea, Math.Max(peekRoom, side ? peekPane.LeastWidth : ThemeFiles.LeastArea));
        switch (window.Anchor)
        {
            case ToolAnchor.LeftTop or ToolAnchor.LeftBottom:
                leftWidth = size;
                break;
            case ToolAnchor.RightTop or ToolAnchor.RightBottom:
                rightWidth = size;
                break;
            default:
                bottomHeight = size;
                break;
        }
        Fit();
    }

    /// <summary>The edge whose tabs name the hidden windows of a side (a left or right place's) or of the foot (a bottom
    /// place's), while it shows; null while it has none, or the stripes name the windows.</summary>
    internal EdgeTabs? EdgeOf(ToolAnchor place) => place switch
    {
        ToolAnchor.LeftTop or ToolAnchor.LeftBottom => leftEdge,
        ToolAnchor.RightTop or ToolAnchor.RightBottom => rightEdge,
        _ => bottomEdge,
    } is { IsVisible: true } edge ? edge : null;

    /// <summary>An edge tab clicked: a docked window opens over the editors, or is put away where it is open there (one the
    /// pointer's rest opened is kept instead, given the keys); one floating, windowed or among the editors opens where it
    /// stands.</summary>
    internal void EdgeTabClicked(ToolWindow window)
    {
        if (peeked == window && hovered)
        {
            hovered = false;
            window.TakeKeys();
        }
        else if (peeked == window)
        {
            Hide(window.Id);
        }
        else if (PeeksAt(window))
        {
            Peek(window.Id);
        }
        else
        {
            Show(window.Id);
        }
    }

    /// <summary>Closes a window: hidden as <see cref="Hide"/> hides it, and, while <see cref="ClosesOffStripe"/>, taken off its
    /// stripe and its place's tabs until it is opened again (its place kept for then). Under titled tabs, one shown in its
    /// place leaves the tab beside it shown there, the next in its order, else the one before, so the place stays open.</summary>
    public void Close([IdentifierOf(typeof(ToolWindowIds))] string id)
    {
        if (!closesOffStripe)
        {
            Hide(id);
            return;
        }
        ToolWindow window = this[id];
        List<ToolWindow> tabbed = titledTabs && window is { IsShown: true, Mode: ToolMode.Docked } && window != peeked
            ? [.. OnStripes.Where(w => w.Anchor == window.Anchor && w.Mode == ToolMode.Docked && w != window).OrderBy(w => w.Order)]
            : [];
        ToolWindow? beside = tabbed.FirstOrDefault(w => w.Order > window.Order) ?? tabbed.LastOrDefault();
        SetOnStripe(id, false);
        if (beside is not null)
        {
            Show(beside.Id);
        }
    }

    /// <summary>A window's stripe icon, as its stripe's checklist sets it whatever <see cref="ClosesOffStripe"/> says: taken
    /// off as a window is closed off it (hidden, its place and its keys kept), or put back, hidden as it was.</summary>
    public void SetOnStripe([IdentifierOf(typeof(ToolWindowIds))] string id, bool on)
    {
        ToolWindow window = this[id];
        if (!on)
        {
            Hide(id);
        }
        if (window.IsClosed == !on)
        {
            return;
        }
        window.IsClosed = !on;
        Layout();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The checklist a stripe opened of its windows at a place, while it is open.</summary>
    public ContextMenu? StripeMenu { get; internal set; }

    /// <summary>The menu a place's titled tabs opened of those with no room in their strip, while it is open.</summary>
    public ContextMenu? HiddenTabsMenu { get; internal set; }

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
            if (window.Mode == ToolMode.Editor)
            {
                EditorTabs?.Close(window);
            }
            PassActive(window);
            Layout();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A window hidden is no longer the one Shift+Escape hides: a window still shown is.</summary>
    private void PassActive(ToolWindow hidden)
    {
        if (Active == hidden)
        {
            Active = Served.FirstOrDefault(w => w.IsShown);
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
        if ((window ?? Active) is not { IsShown: true, Mode: ToolMode.Docked } target || target == peeked)
        {
            return;
        }
        Remember();
        maximized = (target, leftWidth, rightWidth, bottomHeight);
        double width = Bounds.Width - Stripes - 80;
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

    /// <summary>Hides every docked window open, or opens them again in their places (Ctrl+Shift+F12).</summary>
    public void ToggleHideAll() => ToggleHidden(ref hiddenAll, _ => true);

    /// <summary>Hides the docked windows open at the left and right, the editors given their room, or opens them again in
    /// their places; those below the editors stay as they are.</summary>
    public void ToggleHideSides() => ToggleHidden(ref hiddenSides, AtSide);

    private static bool AtSide(ToolWindow window) => window.Anchor is not (ToolAnchor.BottomLeft or ToolAnchor.BottomRight);

    /// <summary>A window shown by the person ends what Hide All Tool Windows hid, and Hide Side Windows' where it is at a side:
    /// the next of either hides again.</summary>
    private void Shown(ToolWindow window)
    {
        hiddenAll = null;
        if (AtSide(window))
        {
            hiddenSides = null;
        }
    }

    /// <summary>Hides the docked windows open that <paramref name="which"/> takes, kept in <paramref name="hidden"/>, or, where
    /// it holds those it hid, opens them again in their places; a window over the editors goes too.</summary>
    private void ToggleHidden(ref List<ToolWindow>? hidden, Func<ToolWindow, bool> which)
    {
        if (hidden is { } was)
        {
            hidden = null;
            foreach (ToolWindow window in was)
            {
                Show(window.Id);
            }
            return;
        }
        if (peeked is { } over && which(over))
        {
            Hide(over.Id);
        }
        List<ToolWindow> open = [.. Served.Where(w => w.IsShown && w.Mode == ToolMode.Docked && which(w))];
        foreach (ToolWindow window in open)
        {
            Hide(window.Id);
        }
        hidden = open;
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

    /// <summary>A floating or windowed window's own window, opened (where it last stood) or, when <paramref name="forward"/>,
    /// brought forward; opened otherwise, it comes without the keys.</summary>
    private void Float(ToolWindow window, bool forward)
    {
        if (floats.TryGetValue(window, out FloatWindow? open))
        {
            if (forward)
            {
                open.Activate();
            }
            return;
        }
        FloatWindow created = new(this, window.WindowBounds, new Size(520, 420)) { Title = window.Title, ShowInTaskbar = window.Mode == ToolMode.Windowed, ShowActivated = forward };
        ToolPane pane = new(this) { Floating = created, Closes = closesOffStripe };
        pane.LeastChanged += (_, _) =>
        {
            // Told after a layout, by which the window may have closed.
            if (created.IsVisible)
            {
                created.HoldAtLeast(new Size(pane.LeastWidth, created.MinHeight));
            }
        };
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

    /// <summary>Every floating or windowed window's own window closed quietly, the tool window left shown, its bounds kept;
    /// one that went with the IDE's window it floated over has closed already.</summary>
    private void CloseFloats()
    {
        foreach ((ToolWindow window, FloatWindow open) in floats.ToList())
        {
            if (open.IsVisible)
            {
                window.WindowBounds = open.Placement;
                open.CloseQuietly();
            }
        }
        floats.Clear();
    }

    /// <summary>A floating window's own window closed by the person: the tool window is hidden, its bounds kept. Closed by
    /// the application's or the system's shutdown, it stays shown where it stood, as the layout kept has it.</summary>
    private void FloatClosed(ToolWindow window, FloatWindow closed)
    {
        if (!floats.Remove(window))
        {
            return;
        }
        window.WindowBounds = closed.Placement;
        if (closed.ShutDown)
        {
            return;
        }
        window.IsShown = false;
        PassActive(window);
        Layout();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>Whether a window shows: open, and serving the open folder.</summary>
    public bool IsShown([IdentifierOf(typeof(ToolWindowIds))] string id) => this[id] is { IsShown: true, Unserved: null };

    /// <summary>The windows shown docked at these places, in their order, each with the place that shows it.</summary>
    internal IEnumerable<(ToolWindow Window, Control Place)> ShownAt(IEnumerable<ToolAnchor> places) =>
        places.Select(anchor => panes[anchor]).Where(pane => pane.Window is not null && pane.IsEffectivelyVisible).Select(pane => (pane.Window!, (Control)pane));

    /// <summary>Moves a window to another place (at an index among its buttons there), docked there, shown if it was.</summary>
    public void Move(ToolWindow window, ToolAnchor anchor, int? index = null)
    {
        // A window among the editors moved to a place docks there.
        if (window.Mode == ToolMode.Editor)
        {
            SetMode(window, ToolMode.Docked);
        }
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
        (double Left, double Right, double Bottom) standing = maximized is { } was ? (was.Left, was.Right, was.Bottom) : (leftWidth, rightWidth, bottomHeight);
        // Each layout's sizes: the one standing's as they stand, the other's as kept apart.
        (double left, double right, double bottom) = wide ? apart!.Value : standing;
        AreaSizes? wideSizes = (wide ? standing : apart) is { } kept ? new AreaSizes(kept.Left, kept.Right, kept.Bottom) : null;
        // A window over the editors is kept as hidden, its place's state.
        Dictionary<string, DockedWindow> placed = windows.ToDictionary(w => w.Id, w => new DockedWindow(w.Anchor, w.Order, w.IsShown && w != peeked)
        {
            Mode = w.Mode,
            Closed = w.IsClosed,
            Bounds = (floats.TryGetValue(w, out FloatWindow? open) ? open.Placement : w.WindowBounds) is { } at ? [at.X, at.Y, at.Width, at.Height] : null,
        });
        foreach ((string id, DockedWindow other) in others)
        {
            placed.TryAdd(id, other);
        }
        return new DockLayout { Windows = placed, LeftWidth = left, RightWidth = right, BottomHeight = bottom, Wide = wideSizes };
    }

    /// <summary>A kept layout over the windows there are; one it does not name stays where it was added. Applied over
    /// another (a folder's over the window's), the windows floating now are docked back first.</summary>
    public void Apply(DockLayout layout)
    {
        CloseFloats();
        foreach (ToolWindow tabbed in windows.Where(w => w is { Mode: ToolMode.Editor, IsShown: true }))
        {
            tabbed.IsShown = false;
            EditorTabs?.Close(tabbed);
        }
        Restore();
        hiddenAll = null;
        hiddenSides = null;
        PeekAt(null);
        others = layout.Windows.Where(w => Find(w.Key) is null).ToDictionary();
        foreach (ToolWindow window in windows)
        {
            if (layout.Windows.GetValueOrDefault(window.Id) is { } placed)
            {
                window.Anchor = placed.Anchor;
                window.Order = placed.Order;
                window.IsShown = placed.Shown;
                window.Mode = placed.Mode;
                window.IsClosed = placed.Closed && !placed.Shown;
                window.WindowBounds = placed.Bounds is [int x, int y, int width, int height] ? new PixelRect(x, y, width, height) : null;
            }
        }
        // Two shown in one place keep the first serving the folder.
        foreach (IGrouping<ToolAnchor, ToolWindow> place in windows.Where(w => w.IsShown && w.Mode == ToolMode.Docked).GroupBy(w => w.Anchor))
        {
            foreach (ToolWindow extra in place.OrderBy(w => w.Unserved is null ? 0 : 1).ThenBy(w => w.Order).Skip(1))
            {
                extra.IsShown = false;
            }
        }
        Renumber();
        (double Left, double Right, double Bottom) was = wide ? apart!.Value : (leftWidth, rightWidth, bottomHeight);
        (double Left, double Right, double Bottom) across = (Positive(layout.LeftWidth, was.Left), Positive(layout.RightWidth, was.Right), Positive(layout.BottomHeight, was.Bottom));
        (double Left, double Right, double Bottom)? wideSizes = layout.Wide is { } kept
            ? (Positive(kept.LeftWidth, across.Left), Positive(kept.RightWidth, across.Right), Positive(kept.BottomHeight, across.Bottom))
            : null;
        (leftWidth, rightWidth, bottomHeight) = wide ? wideSizes ?? across : across;
        apart = wide ? across : wideSizes;
        Active = Served.FirstOrDefault(w => w.IsShown);
        Layout();
        foreach (ToolWindow tabbed in Served.Where(w => w is { Mode: ToolMode.Editor, IsShown: true }))
        {
            EditorTabs?.Show(tabbed, false);
        }
        // A floating window kept open opens again once the IDE's own window is there to float over.
        foreach (ToolWindow floating in Served.Where(w => w.IsShown && w.Mode is ToolMode.Floating or ToolMode.Windowed))
        {
            if (TopLevel.GetTopLevel(this) is null)
            {
                AttachedToVisualTree += (_, _) =>
                {
                    if (floating is { IsShown: true, Unserved: null, Mode: ToolMode.Floating or ToolMode.Windowed })
                    {
                        Float(floating, forward: true);
                    }
                };
            }
            else
            {
                Float(floating, forward: true);
            }
        }

        static double Positive(double value, double fallback) => value > 40 ? value : fallback;
    }


    /// <summary>The stripes' and the edge tabs' tips said again with the keymap in force, which a change of keymap or
    /// shortcut has replaced.</summary>
    public void FollowKeys()
    {
        leftStripe.Fill(OnStripes);
        rightStripe.Fill(OnStripes);
        FillEdges();
    }

    internal void Layout()
    {
        if (peeked is { } over && !(titledTabs && over is { IsShown: true, IsClosed: false, Unserved: null, Mode: ToolMode.Docked }))
        {
            PeekAt(null);
        }
        foreach ((ToolAnchor anchor, ToolPane pane) in panes)
        {
            pane.Show(Served.FirstOrDefault(w => w.Anchor == anchor && w.IsShown && w.Mode == ToolMode.Docked && w != peeked));
            pane.ShowTabs(titledTabs && pane.Window is not null ? [.. OnStripes.Where(w => w.Anchor == anchor && w.Mode == ToolMode.Docked).OrderBy(w => w.Order)] : []);
            pane.Closes = closesOffStripe;
            pane.AutoHides = titledTabs;
        }
        // After the places, so a window docked from over the editors has left them.
        peekPane.Show(peeked);
        peekPane.Closes = closesOffStripe;
        Split(leftArea, panes[ToolAnchor.LeftTop], panes[ToolAnchor.LeftBottom], rows: true);
        Split(rightArea, panes[ToolAnchor.RightTop], panes[ToolAnchor.RightBottom], rows: true);
        Split(bottomArea, panes[ToolAnchor.BottomLeft], panes[ToolAnchor.BottomRight], rows: false);
        leftStripe.Fill(OnStripes);
        rightStripe.Fill(OnStripes);
        FillEdges();
        FoldStripes();
        Fit();
    }

    /// <summary>Under titled tabs, a tab along the frame's edge for each window on it that no place shows and no tab at a
    /// place's foot names: one hidden, or open over the editors; none under stripe icons.</summary>
    private void FillEdges()
    {
        List<ToolWindow> edged = titledTabs
            ? [.. OnStripes.Where(w => (!w.IsShown || w == peeked) && !(w.Mode == ToolMode.Docked && panes[w.Anchor].Window is not null))]
            : [];
        foreach (EdgeTabs edge in (EdgeTabs[])[leftEdge, rightEdge, bottomEdge])
        {
            edge.Fill(edged, peeked);
        }
    }

    /// <summary>Under stripe icons, a stripe with no icon (none docked at its places) folds away, leaving its width to the
    /// areas and the editors, and shows again while a window is dragged, as a place to drop it; one of whose windows the
    /// person took off it stays shown, icon or none, so its checklist can put it back. Under titled tabs no stripe stands,
    /// and each edge shows while it has tabs.</summary>
    private void FoldStripes()
    {
        foreach ((Stripe stripe, EdgeTabs edge, int column) in ((Stripe, EdgeTabs, int)[])[(leftStripe, leftEdge, 0), (rightStripe, rightEdge, 6)])
        {
            bool shown = !titledTabs && (dragged || stripe.HasIcons || Served.Any(w => w.IsClosed && stripe.Lists(w.Anchor)));
            stripe.IsVisible = shown;
            edge.IsVisible = titledTabs && edge.HasTabs;
            ColumnDefinitions[column].Width = new GridLength(shown ? StripeWidth : edge.IsVisible ? EdgeTabs.Breadth : 0);
        }
        bottomEdge.IsVisible = titledTabs && bottomEdge.HasTabs;
        RowDefinitions[3].Height = new GridLength(bottomEdge.IsVisible ? EdgeTabs.Breadth : 0);
    }

    /// <summary>The stripes' or edges' widths, as they are to be laid out.</summary>
    private double Stripes => ColumnDefinitions[0].Width.Value + ColumnDefinitions[6].Width.Value;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // A size the splitter's keys changed, which no drag's end kept, is kept before the sizes are fitted again.
        Remember();
        FollowShape();
        Fit();
    }

    /// <summary>When the side areas run the frame's whole height, the bottom area between them: once the window is wide
    /// for its height (<see cref="WideFrom"/>, back under <see cref="WideUntil"/>), always or never.</summary>
    public WideLayout Wide
    {
        get => wideLayout;
        set
        {
            if (value != wideLayout)
            {
                wideLayout = value;
                FollowShape();
                Fit();
            }
        }
    }

    /// <summary>Whether the side areas run the frame's whole height as it stands, the bottom area between them.</summary>
    public bool IsWide => wide;

    /// <summary>Whether a window of a size takes the wide layout, as <paramref name="layout"/> says, given whether it stands
    /// now: by its shape, past <see cref="WideFrom"/> into it and under <see cref="WideUntil"/> out of it, so a resize at
    /// either edge never flips it to and fro.</summary>
    private static bool Widens(WideLayout layout, bool standing, Size window) => layout switch
    {
        WideLayout.Always => true,
        WideLayout.Never => false,
        _ when window.Width <= 0 || window.Height <= 0 => standing,
        _ => standing ? window.Width / window.Height >= WideUntil : window.Width / window.Height > WideFrom,
    };

    /// <summary>Takes the layout the window's shape and <see cref="Wide"/> ask for: a window maximised gives its sizes back
    /// first, the sizes standing are kept for this layout's return, and the other's come back.</summary>
    private void FollowShape()
    {
        bool next = Widens(wideLayout, wide, host?.ClientSize ?? Bounds.Size);
        if (next == wide)
        {
            return;
        }
        Restore();
        Remember();
        (double Left, double Right, double Bottom) standing = (leftWidth, rightWidth, bottomHeight);
        (leftWidth, rightWidth, bottomHeight) = apart ?? standing;
        apart = standing;
        wide = next;
        PlaceAreas();
    }

    /// <summary>The areas placed for the layout standing: the sides beside the editors, the bottom area and its divider
    /// under all three; or, wide, the sides and their dividers down the whole height, the bottom area under the editors
    /// alone. The seam where the bottom area meets a side's ground runs under that side, or, wide, beside the bottom area.</summary>
    private void PlaceAreas()
    {
        foreach (Control side in (Control[])[leftArea, leftDivider, rightDivider, rightArea])
        {
            SetRowSpan(side, wide ? 3 : 1);
        }
        foreach (Control under in (Control[])[bottomDivider, bottomArea])
        {
            SetColumn(under, wide ? 3 : 1);
            SetColumnSpan(under, wide ? 1 : 5);
        }
        (int row, int left, int right) = wide ? (2, 2, 4) : (1, 1, 5);
        SetRow(leftSeam, row);
        SetColumn(leftSeam, left);
        SetRow(rightSeam, row);
        SetColumn(rightSeam, right);
    }

    /// <summary>Gives the open areas their sizes, fitted to the frame as it is: on one smaller than they were kept on (a
    /// laptop's, after a monitor's), the sides shrink alike, and the bottom, until the editor keeps its share, where a
    /// splitter stops too, and, wide, the room the bottom area's headers take at their least. Maximised, a window may take
    /// all of the frame but its edge.</summary>
    private void Fit()
    {
        bool leftOpen = panes[ToolAnchor.LeftTop].Window is not null || panes[ToolAnchor.LeftBottom].Window is not null;
        bool rightOpen = panes[ToolAnchor.RightTop].Window is not null || panes[ToolAnchor.RightBottom].Window is not null;
        bool bottomOpen = panes[ToolAnchor.BottomLeft].Window is not null || panes[ToolAnchor.BottomRight].Window is not null;
        double share = maximized is null ? EditorShare : 0;
        // Before the first layout there is no frame: the sizes are fitted once there is.
        double across = Math.Max(0, Bounds.Width - Stripes - 2);
        double down = Math.Max(0, Bounds.Height - RowDefinitions[3].Height.Value - 1);
        double left = leftOpen ? leftWidth : 0;
        double right = rightOpen ? rightWidth : 0;
        double bottom = bottomOpen ? bottomHeight : 0;
        double between = Math.Max(across * share, wide && bottomOpen ? Least(ToolAnchor.BottomLeft) + Least(ToolAnchor.BottomRight) + 1 : 0);
        if (across > 0 && left + right > across - between)
        {
            double scale = Math.Max(0, across - between) / (left + right);
            (left, right) = (left * scale, right * scale);
        }
        if (down > 0)
        {
            bottom = Math.Min(bottom, down * (1 - share));
        }
        ColumnDefinitions[1].Width = new GridLength(left);
        ColumnDefinitions[1].MinWidth = Math.Max(Least(ToolAnchor.LeftTop), Least(ToolAnchor.LeftBottom));
        ColumnDefinitions[2].Width = new GridLength(leftOpen ? 1 : 0);
        ColumnDefinitions[3].MinWidth = between;
        ColumnDefinitions[4].Width = new GridLength(rightOpen ? 1 : 0);
        ColumnDefinitions[5].Width = new GridLength(right);
        ColumnDefinitions[5].MinWidth = Math.Max(Least(ToolAnchor.RightTop), Least(ToolAnchor.RightBottom));
        bottomArea.ColumnDefinitions[0].MinWidth = Least(ToolAnchor.BottomLeft);
        bottomArea.ColumnDefinitions[2].MinWidth = Least(ToolAnchor.BottomRight);
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
        PlacePeek(across, down);
    }

    /// <summary>The window over the editors beside its edge, as wide (or, from the foot, as high) as its area is kept,
    /// though never over more than the editors' share leaves nor narrower than its header at its least, its edge toward the
    /// editors drawn; from the foot in the wide layout, between the sides.</summary>
    private void PlacePeek(double across, double down)
    {
        peekFrame.IsVisible = peeked is not null;
        if (peeked is not { } window)
        {
            return;
        }
        double most = 1 - EditorShare;
        double least = peekPane.LeastWidth;
        bool side = window.Anchor is not (ToolAnchor.BottomLeft or ToolAnchor.BottomRight);
        // Wide, the foot's window stands between the sides, as its area does.
        SetColumn(peekFrame, wide && !side ? 3 : 1);
        SetColumnSpan(peekFrame, wide && !side ? 1 : 5);
        peekRoom = side ? across * most : down * most;
        (HorizontalAlignment h, VerticalAlignment v, double width, double height, Thickness edge) = window.Anchor switch
        {
            ToolAnchor.LeftTop or ToolAnchor.LeftBottom => (HorizontalAlignment.Left, VerticalAlignment.Stretch, Math.Max(least, Math.Min(leftWidth, across * most)), double.NaN, Hairlines.After),
            ToolAnchor.RightTop or ToolAnchor.RightBottom => (HorizontalAlignment.Right, VerticalAlignment.Stretch, Math.Max(least, Math.Min(rightWidth, across * most)), double.NaN, Hairlines.Before),
            _ => (HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, Math.Min(bottomHeight, down * most), Hairlines.Above),
        };
        peekFrame.HorizontalAlignment = h;
        peekFrame.VerticalAlignment = v;
        peekFrame.Width = width;
        peekFrame.Height = height;
        peekFrame.BorderThickness = edge;
        peekGrip.HorizontalAlignment = h switch
        {
            HorizontalAlignment.Left => HorizontalAlignment.Right,
            HorizontalAlignment.Right => HorizontalAlignment.Left,
            _ => HorizontalAlignment.Stretch,
        };
        peekGrip.VerticalAlignment = side ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        peekGrip.Width = side ? GripBreadth : double.NaN;
        peekGrip.Height = side ? double.NaN : GripBreadth;
        peekGrip.Cursor = side ? acrossCursor : downCursor;
    }

    /// <summary>How broad the edge a drag resizes the window over the editors by is.</summary>
    private const double GripBreadth = 6;

    /// <summary>The least a place may be while it shows a window: what its header holds at its least.</summary>
    private double Least(ToolAnchor anchor) => panes[anchor] is { Window: not null } pane ? pane.LeastWidth : 0;

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
