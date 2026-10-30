using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Passenger.Inspector.Wpf;

/// <summary>The inspector's work on a WPF app's UI thread: the windows and their visual trees, an element looked at
/// closely, Select Element and the outline over the element chosen, a word to Passenger as elements come and go, and
/// for an agent the windows' pictures and input to them (<see cref="Pictures"/>, <see cref="Driver"/>). Elements are
/// named by ids that last as long as the element does. A top level is a window or a popup's root, each the root visual
/// of a presentation source of the app's.</summary>
internal sealed class Inspector : Inspecting<DependencyObject>
{
    /// <summary>The inspector the class handlers below speak for; WPF's class handlers last as long as the app does.</summary>
    private static Inspector? current;

    private readonly Picker picker;
    private readonly Driver driver;
    private readonly PropertyBreaks breaks = new();

    public Inspector(Connection connection) : base(connection)
    {
        picker = new Picker(this, connection);
        driver = new Driver(this);
        bool first = current is null;
        current = this;
        if (first)
        {
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => current?.Changed(sender)), handledEventsToo: true);
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.UnloadedEvent, new RoutedEventHandler((sender, _) => current?.Changed(sender)), handledEventsToo: true);
        }
    }

    /// <summary>Lays out every top level, so input and pictures find what was just made (a popup opened) where it shows.</summary>
    public void Settle()
    {
        foreach (Visual top in TopLevels())
        {
            (top as UIElement)?.UpdateLayout();
        }
    }

    protected override void Stopping()
    {
        breaks.Stop();
        picker.Choose(false);
        picker.Outline(null);
        if (current == this)
        {
            current = null;
        }
    }

    protected override ElementDetails DetailsOf(DependencyObject element) => new Details(this).Of(element);

    protected override void Pick(bool on) => picker.Choose(on);

    protected override void Outline(DependencyObject? element) => picker.Outline(element);

    protected override Picture Capture(DependencyObject element, Request request) => Pictures.Take(this, element, request);

    protected override InputDone Do(InputAction input) => driver.Do(input);

    protected override void Watch(int breakpoint, DependencyObject element, string property, string? hitCondition) => breaks.Watch(breakpoint, element, property, hitCondition);

    protected override void Unwatch(int breakpoint) => breaks.Unwatch(breakpoint);

    protected override XamlValues ValuesOf(Request request) =>
        throw new Refusal("Binding values after a XAML file's lines are read from an Avalonia app's .axaml; a WPF app's are not");

    protected override List<AppWindow> SetOverlays(Request request) => throw new Refusal("Renderer debug overlays are Avalonia's: a WPF app has none");

    protected override void Later(TimeSpan delay, Action action)
    {
        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    protected override bool LeftOut(DependencyObject element) => IsOwn(element);

    /// <summary>Whether an element is the inspector's own (Select Element's outline), which the tree leaves out.</summary>
    public bool IsOwn(DependencyObject element) => picker.IsOwn(element);

    protected override AppTree Tree(Request request)
    {
        Look? look = request.Layout == true || request.Properties is { Count: > 0 }
            ? new Look(request.Layout == true, request.Properties ?? [], Keyboard.FocusedElement)
            : null;
        List<Element> roots = [];
        foreach (Visual top in TopLevels())
        {
            roots.Add(Node(top, top, look, new Rect(Shown.ClientSize(top))));
        }
        return new AppTree(roots);
    }

    /// <summary>What an agent's tree carries beyond each element's identity: layout and text, and property values.</summary>
    private sealed record Look(bool Layout, IReadOnlyList<string> Properties, IInputElement? Focused);

    protected override List<AppWindow> Windows()
    {
        List<AppWindow> windows = [];
        foreach (Visual top in TopLevels())
        {
            Size size = Shown.ClientSize(top);
            windows.Add(new AppWindow(
                IdOf(top),
                Words.TypeName(top.GetType()),
                (top as FrameworkElement)?.Name is { Length: > 0 } name ? name : null,
                (top as Window)?.Title,
                Math.Round(size.Width, 2),
                Math.Round(size.Height, 2),
                Shown.Scaling(top),
                top is Window { IsActive: true },
                top is UIElement { IsVisible: true },
                OwnerOf(top) is { } owner ? IdOf(owner) : null));
        }
        return windows;
    }

    /// <summary>The app's top levels on this thread, each once: its main window, its other windows, then every other root a
    /// presentation source of the app's shows (a popup's, a window shown with no Application).</summary>
    public IReadOnlyList<Visual> TopLevels()
    {
        List<Visual> tops = [];
        if (Application.Current is { } app && app.CheckAccess())
        {
            if (app.MainWindow is { } main && PresentationSource.FromVisual(main) is not null)
            {
                tops.Add(main);
            }
            foreach (Window window in app.Windows)
            {
                if (!tops.Contains(window) && PresentationSource.FromVisual(window) is not null)
                {
                    tops.Add(window);
                }
            }
        }
        int sources = 0;
        foreach (PresentationSource source in PresentationSource.CurrentSources)
        {
            sources++;
            if (source is HwndSource { IsDisposed: false } hwnd && hwnd.CheckAccess() && hwnd.RootVisual is { } root && !tops.Contains(root))
            {
                tops.Add(root);
            }
        }
        if (tops.Count == 0)
        {
            InspectorTrace.Step($"no top levels: Application {Application.Current?.GetType().FullName} (on this thread {Application.Current?.CheckAccess()}), "
                + $"its windows {(Application.Current?.CheckAccess() == true ? Application.Current.Windows.Count : -1)}, presentation sources {sources}");
        }
        return tops;
    }

    /// <summary>The top level an element is in: its presentation source's root.</summary>
    public static Visual? TopOf(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D ? PresentationSource.FromDependencyObject(element)?.RootVisual : null;

    /// <summary>The window a popup's root opened from: the window of the element its popup is in, or placed by (a menu's
    /// popup, a tool tip's), through any popup it is in itself.</summary>
    public Visual? OwnerOf(Visual top)
    {
        if (top is Window || Popup(top) is not { } popup)
        {
            return null;
        }
        DependencyObject anchor = popup.PlacementTarget ?? (DependencyObject)popup;
        for (int i = 0; i < 8 && anchor is not null; i++)
        {
            if (Window.GetWindow(anchor) is { } window)
            {
                return window;
            }
            if (TopOf(anchor) is { } there && there != top)
            {
                if (there is Window placed)
                {
                    return placed;
                }
                if (Popup(there) is { } outer)
                {
                    anchor = outer.PlacementTarget ?? (DependencyObject)outer;
                    continue;
                }
            }
            break;
        }
        return null;
    }

    /// <summary>The popup whose root a top level is: the popup its content belongs to.</summary>
    public static Popup? Popup(Visual top)
    {
        if (top is Window)
        {
            return null;
        }
        Queue<DependencyObject> next = new([top]);
        for (int seen = 0; next.Count > 0 && seen < 16; seen++)
        {
            DependencyObject at = next.Dequeue();
            if (at is FrameworkElement { Parent: Popup popup })
            {
                return popup;
            }
            if (at is FrameworkElement { TemplatedParent: Popup templated })
            {
                return templated;
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(at); i++)
            {
                next.Enqueue(VisualTreeHelper.GetChild(at, i));
            }
        }
        return null;
    }

    /// <summary>An element and what it holds; with an agent's look, also where it is and whether what clips it (its window, a
    /// scroll viewer) shows any of it, <paramref name="clip"/> being what its ancestors leave shown.</summary>
    private Element Node(DependencyObject element, Visual top, Look? look, Rect clip)
    {
        Rect? box = look is { Layout: true } ? Shown.RectIn(element, top) : null;
        Rect inside = box is { } own && Shown.Clips(element) ? Rect.Intersect(clip, own) : clip;
        List<Element> children = [];
        foreach (DependencyObject child in Children(element))
        {
            children.Add(Node(child, top, look, inside));
        }
        FrameworkElement? framed = element as FrameworkElement;
        Element node = new(
            IdOf(element),
            Words.TypeName(element.GetType()),
            framed?.Name is { Length: > 0 } name ? name : null,
            [],
            framed?.TemplatedParent is not null,
            Sources.Place(element) is not null,
            element is not UIElement { Visibility: not Visibility.Visible },
            children);
        if (look is null)
        {
            return node;
        }
        if (look.Layout)
        {
            Rect shown = box is { } at ? Rect.Intersect(at, clip) : Rect.Empty;
            node = node with
            {
                Bounds = box is { } placed ? Shown.Boxed(placed) : null,
                Text = Shown.TextOf(element),
                Disabled = element is UIElement { IsEnabled: false } ? true : null,
                Focused = look.Focused == element ? true : null,
                OutOfView = box is { Width: > 0, Height: > 0 } && (shown.IsEmpty || shown.Width <= 0 || shown.Height <= 0) ? true : null,
            };
        }
        return look.Properties.Count == 0 ? node : node with { Values = Shown.Values(element, look.Properties) };
    }

    /// <summary>An element's visual children, less the inspector's own: an adorner layer that holds the outline alone.</summary>
    public IEnumerable<DependencyObject> Children(DependencyObject element)
    {
        int count = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(element) : 0;
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(element, i);
            if (IsOwn(child) || (child is AdornerLayer layer && VisualTreeHelper.GetChildrenCount(layer) > 0 && Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(layer)).All(c => IsOwn(VisualTreeHelper.GetChild(layer, c)))))
            {
                continue;
            }
            yield return child;
        }
    }

    /// <summary>An element as an agent's answer names it: its type and #name (or the text it shows), and the control whose
    /// template made it.</summary>
    public static string Named(DependencyObject element) =>
        Words.Named(Shown.Called(element), element is FrameworkElement { TemplatedParent: DependencyObject parent } ? Shown.Called(parent) : null);
}
