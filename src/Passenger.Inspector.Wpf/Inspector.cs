using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Passenger.Inspector.Wpf;

/// <summary>The inspector's work on a WPF app's UI thread, as the Avalonia one's: the windows and their visual trees, an
/// element looked at closely, Select Element and the outline over the element chosen, a word to Passenger as elements come and
/// go, and for an agent the windows' pictures and input to them (<see cref="Pictures"/>, <see cref="Driver"/>). Elements are
/// named by ids that last as long as the element does. A top level is a window or a popup's root, each the root visual of a
/// presentation source of the app's.</summary>
internal sealed class Inspector
{
    /// <summary>How long the app's elements may come and go before Passenger is told its tree changed, once.</summary>
    private static readonly TimeSpan Settled = TimeSpan.FromMilliseconds(300);

    /// <summary>The inspector the class handlers below speak for; WPF's class handlers last as long as the app does.</summary>
    private static Inspector? current;

    private readonly Connection connection;
    private readonly Picker picker;
    private readonly Driver driver;
    private readonly PropertyBreaks breaks = new();
    private bool told;
    private bool stopped;

    /// <summary>Whether Passenger has asked for the tree since it was last told of a change, so it is told only once.</summary>
    private bool watched;

    public Inspector(Connection connection)
    {
        this.connection = connection;
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

    public Reply Handle(Request request)
    {
        try
        {
            return request.Method switch
            {
                Wire.Tree => new Reply { Id = request.Id, Tree = Tree(request) },
                Wire.Windows => new Reply { Id = request.Id, Windows = Windows() },
                Wire.Details => Find(request.Element) is { } element
                    ? new Reply { Id = request.Id, Details = new Details(this).Of(element) }
                    : Gone(request),
                Wire.Pick => Done(request, () => picker.Choose(request.On == true)),
                Wire.Highlight => Done(request, () => picker.Outline(Find(request.Element))),
                Wire.Capture => Find(request.Element) is { } shown
                    ? new Reply { Id = request.Id, Picture = Pictures.Take(this, shown, request) }
                    : Gone(request),
                Wire.Input => new Reply { Id = request.Id, Done = driver.Do(request.Input ?? throw new Refusal("No input was given")) },
                Wire.Watch => Done(request, () => Watch(request)),
                Wire.Values => new Reply { Id = request.Id, Error = "Binding values after a XAML file's lines are read from an Avalonia app's .axaml; a WPF app's are not" },
                Wire.Overlays => new Reply { Id = request.Id, Error = "Renderer debug overlays are Avalonia's: a WPF app has none" },
                Wire.ApplyXaml => new Reply { Id = request.Id, Updated = new XamlUpdateResult(false, 0,
                    [.. (request.Update?.Files ?? []).Select(f => new XamlUpdateProblem(f.File, 1, 1, "WPF XAML updates need a restart in this version", true))]) },
                _ => new Reply { Id = request.Id, Error = $"The inspector does not know '{request.Method}'" },
            };
        }
        catch (Refusal refused)
        {
            return new Reply { Id = request.Id, Error = refused.Message };
        }
        catch (Exception error)
        {
            return new Reply { Id = request.Id, Error = $"{error.GetType().Name}: {error.Message}" };
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

    /// <summary>Passenger has gone: nothing of the inspector's is left in the app's windows.</summary>
    public void Stop()
    {
        stopped = true;
        breaks.Stop();
        picker.Choose(false);
        picker.Outline(null);
        if (current == this)
        {
            current = null;
        }
    }

    private static Reply Gone(Request request) => new() { Id = request.Id, Error = "That element is no longer in the app" };

    private static Reply Done(Request request, Action action)
    {
        action();
        return new Reply { Id = request.Id };
    }

    /// <summary>A property breakpoint's watch begun (again, with its hit count) or ended.</summary>
    private void Watch(Request request)
    {
        int breakpoint = request.Breakpoint ?? throw new Refusal("No property breakpoint was named");
        if (request.On == false)
        {
            breaks.Unwatch(breakpoint);
            return;
        }
        DependencyObject element = Find(request.Element) ?? throw new Refusal("That element is no longer in the app");
        breaks.Watch(breakpoint, element, request.Property ?? throw new Refusal("No property was named"), request.HitCondition);
    }

    public long IdOf(DependencyObject element) => ElementIds.Of(element);

    public DependencyObject? Find(long? id) => ElementIds.Find(id);

    /// <summary>Whether an element is the inspector's own (Select Element's outline), which the tree leaves out.</summary>
    public bool IsOwn(DependencyObject element) => picker.IsOwn(element);

    private AppTree Tree(Request request)
    {
        watched = true;
        told = false;
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

    /// <summary>The app's top levels as an agent lists them.</summary>
    public List<AppWindow> Windows()
    {
        List<AppWindow> windows = [];
        foreach (Visual top in TopLevels())
        {
            Size size = Shown.ClientSize(top);
            windows.Add(new AppWindow(
                IdOf(top),
                TypeName(top.GetType()),
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
            Agent.Trace($"no top levels: Application {Application.Current?.GetType().FullName} (on this thread {Application.Current?.CheckAccess()}), "
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
            TypeName(element.GetType()),
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
        element is FrameworkElement { TemplatedParent: DependencyObject parent } ? $"{Shown.Called(element)} in {Shown.Called(parent)}'s template" : Shown.Called(element);

    /// <summary>A type as the tree names it: no namespace, no generic arity.</summary>
    public static string TypeName(Type type)
    {
        string name = type.Name;
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }

    /// <summary>An element came or went: Passenger, if it has the tree, hears once that it changed, a moment after the last.</summary>
    private void Changed(object? sender)
    {
        if (stopped || (sender is DependencyObject element && IsOwn(element)) || !watched || told)
        {
            return;
        }
        told = true;
        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = Settled };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            watched = false;
            if (!stopped)
            {
                connection.Send(new Reply { Event = Wire.TreeChanged });
            }
        };
        timer.Start();
    }
}

/// <summary>Why the inspector will not do what it was asked (the element is disabled, covered, gone), in words an agent
/// reads as they are.</summary>
internal sealed class Refusal(string message) : Exception(message);

/// <summary>The ids Passenger knows the app's elements by, each the same for as long as the element lives: the Live Visual
/// Tree's and an agent's, so an element named in one is the one in the other. The app's UI thread's alone.</summary>
internal static class ElementIds
{
    private static readonly ConditionalWeakTable<DependencyObject, object> Ids = new();
    private static readonly Dictionary<long, WeakReference<DependencyObject>> ById = [];
    private static long next;

    public static long Of(DependencyObject element)
    {
        if (Ids.TryGetValue(element, out object? known))
        {
            return (long)known;
        }
        long id = ++next;
        Ids.Add(element, id);
        if (ById.Count > 4096 && ById.Count % 4096 == 0)
        {
            foreach (long dead in ById.Where(e => !e.Value.TryGetTarget(out _)).Select(e => e.Key).ToList())
            {
                ById.Remove(dead);
            }
        }
        ById[id] = new WeakReference<DependencyObject>(element);
        return id;
    }

    public static DependencyObject? Find(long? id) => id is long known && ById.TryGetValue(known, out WeakReference<DependencyObject>? held) && held.TryGetTarget(out DependencyObject? element) ? element : null;
}
