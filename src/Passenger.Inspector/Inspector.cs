using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Passenger.Inspector;

/// <summary>The inspector's work on the app's UI thread: the windows and their visual trees, an element looked at closely,
/// Select Element and the outline over the element chosen, a word to Passenger as controls come and go, and for an agent
/// the windows' pictures and input to them (<see cref="Pictures"/>, <see cref="Driver"/>). Elements are named by ids that
/// last as long as the element does.</summary>
internal sealed class Inspector
{
    /// <summary>How long the app's controls may come and go before Passenger is told its tree changed, once.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private readonly Connection connection;
    private readonly List<IDisposable> handlers = [];
    private readonly Picker picker;
    private readonly Driver driver;
    private readonly PropertyBreaks breaks = new();
    private readonly ValueWatch values;
    private readonly XamlHotReload xaml;
    private bool told;

    /// <summary>Whether Passenger has asked for the tree since it was last told of a change, so it is told only once.</summary>
    private bool watched;

    /// <param name="overlays">The renderer debug overlays the launch asked for (<see cref="Wire.OverlaysVariable"/>), or null.</param>
    public Inspector(Connection connection, string? overlays = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        this.connection = connection;
        xaml = new XamlHotReload(result => connection.Send(new Reply { Event = Wire.XamlFailed, Updated = result }));
        picker = new Picker(this, connection);
        driver = new Driver(this);
        values = new ValueWatch(connection);
        handlers.Add(Control.LoadedEvent.AddClassHandler(typeof(Control), (sender, _) => Changed(sender)));
        handlers.Add(Control.UnloadedEvent.AddClassHandler(typeof(Control), (sender, _) => Changed(sender)));
        handlers.Add(Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (sender, _) =>
        {
            Overlays.Opened(sender as TopLevel);
            Changed(sender);
        }));
        handlers.Add(Window.WindowClosedEvent.AddClassHandler(typeof(Window), (sender, _) => Changed(sender)));
        if (overlays is { Length: > 0 })
        {
            Overlays.Want(overlays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), TopLevels());
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
                Wire.Details => Find(request.Element) is { } visual
                    ? new Reply { Id = request.Id, Details = new Details(this).Of(visual) }
                    : Gone(request),
                Wire.Pick => Done(request, () => picker.Choose(request.On == true)),
                Wire.Highlight => Done(request, () => picker.Outline(Find(request.Element))),
                Wire.Capture => Find(request.Element) is { } shown
                    ? new Reply { Id = request.Id, Picture = Pictures.Take(shown, request) }
                    : Gone(request),
                Wire.Input => new Reply { Id = request.Id, Done = driver.Do(request.Input ?? throw new Refusal("No input was given")) },
                Wire.Watch => Done(request, () => Watch(request)),
                Wire.Values => new Reply { Id = request.Id, Values = Values(request) },
                Wire.Overlays => new Reply { Id = request.Id, Windows = SetOverlays(request) },
                Wire.ApplyXaml => new Reply { Id = request.Id, Updated = xaml.Apply(request.Update ?? throw new Refusal("No XAML edits were given")) },
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

    /// <summary>Done once Avalonia has rendered its next frame, whose scene hit testing reads (a popup just opened is in it
    /// only then), or after a moment when nothing renders.</summary>
    public async Task RenderedAsync()
    {
        if (TopLevels().FirstOrDefault() is not { } top)
        {
            return;
        }
        TaskCompletionSource rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        top.RequestAnimationFrame(_ => rendered.TrySetResult());
        await Task.WhenAny(rendered.Task, Task.Delay(FrameLimit));
    }

    /// <summary>How long input waits for a frame before going on without: an app whose windows render nothing.</summary>
    private static readonly TimeSpan FrameLimit = TimeSpan.FromMilliseconds(500);

    /// <summary>Passenger has gone: nothing of the inspector's is left in the app's windows.</summary>
    public void Stop()
    {
        xaml.Stop();
        breaks.Stop();
        values.Stop();
        picker.Choose(false);
        picker.Outline(null);
        foreach (IDisposable handler in handlers)
        {
            handler.Dispose();
        }
    }

    private static Reply Gone(Request request) => new() { Id = request.Id, Error = "That element is no longer in the app" };

    /// <summary>A property breakpoint's watch begun (again, with its hit count) or ended.</summary>
    private void Watch(Request request)
    {
        int breakpoint = request.Breakpoint ?? throw new Refusal("No property breakpoint was named");
        if (request.On == false)
        {
            breaks.Unwatch(breakpoint);
            return;
        }
        Visual element = Find(request.Element) ?? throw new Refusal("That element is no longer in the app");
        breaks.Watch(breakpoint, element, request.Property ?? throw new Refusal("No property was named"), request.HitCondition);
    }

    /// <summary>A XAML file's bindings with their values, the properties read watched from now so the app says when they change.</summary>
    private XamlValues Values(Request request)
    {
        XamlRead read = request.Xaml ?? throw new Refusal("No XAML file was named");
        List<(AvaloniaObject, AvaloniaProperty)> watched = [];
        XamlValues answer = BindingReader.Read(this, read, null, watched);
        values.Watch(read.File, watched, read.More == true);
        return answer;
    }

    private static Reply Done(Request request, Action action)
    {
        action();
        return new Reply { Id = request.Id };
    }

    /// <summary>The overlays asked for set on every window, those opened later too, and the windows as they are now.</summary>
    private List<AppWindow> SetOverlays(Request request)
    {
        Overlays.Want(request.Overlays ?? [], TopLevels());
        return Windows();
    }

    /// <summary>The id Passenger knows a visual by, the same for as long as it lives.</summary>
    public long IdOf(Visual visual) => ElementIds.Of(visual);

    public Visual? Find(long? id) => ElementIds.Find(id);

    /// <summary>Whether a visual is the inspector's own (Select Element's outline), which the tree leaves out.</summary>
    public bool IsOwn(Visual visual) => picker.IsOwn(visual);

    private AppTree Tree(Request request)
    {
        watched = true;
        told = false;
        Look? look = request.Layout == true || request.Properties is { Count: > 0 }
            ? new Look(request.Layout == true, request.Properties ?? [], Shown.Focused())
            : null;
        return new AppTree([.. TopLevels().Select(top => Node(top, top, look, new Rect(top.ClientSize)))]);
    }

    /// <summary>What an agent's tree carries beyond each element's identity: layout and text, and property values.</summary>
    private sealed record Look(bool Layout, IReadOnlyList<string> Properties, IInputElement? Focused);

    /// <summary>The app's top levels as an agent lists them.</summary>
    private List<AppWindow> Windows() => [.. TopLevels().Select(top => new AppWindow(
        IdOf(top),
        TypeName(top.GetType()),
        top.Name,
        (top as Window)?.Title,
        top.ClientSize.Width,
        top.ClientSize.Height,
        top.RenderScaling,
        top is WindowBase { IsActive: true },
        top.IsVisible,
        top is PopupRoot { ParentTopLevel: { } parent } ? IdOf(parent) : null) { Overlays = Overlays.Of(top) })];

    /// <summary>The app's top levels, each once: its desktop lifetime's windows or its single view's, then every other one
    /// Avalonia is drawing (a popup's, or every window of an app with no lifetime, as Passenger's own screenshot has).</summary>
    public IReadOnlyList<TopLevel> TopLevels() => AllTopLevels();

    /// <summary><see cref="TopLevels"/>, for what reads the app without the inspector's own state (an evaluation's read).</summary>
    public static IReadOnlyList<TopLevel> AllTopLevels()
    {
        List<TopLevel> tops = [];
        switch (Application.Current?.ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                tops.AddRange(desktop.Windows);
                break;
            case ISingleViewApplicationLifetime { MainView: { } view } when TopLevel.GetTopLevel(view) is { } single:
                tops.Add(single);
                break;
        }
        foreach (TopLevel top in Drawn())
        {
            if (!tops.Contains(top))
            {
                tops.Add(top);
            }
        }
        return tops;
    }

    /// <summary>The top levels Avalonia's media context draws: every window and popup shown, which is no public list.</summary>
    internal static List<TopLevel> Drawn()
    {
        try
        {
            Type? context = typeof(Visual).Assembly.GetType("Avalonia.Media.MediaContext");
            object? instance = context?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return context?.GetField("_topLevels", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance) is IDictionary drawn
                ? [.. drawn.Keys.OfType<TopLevel>()]
                : [];
        }
        catch (Exception error) when (error is TargetInvocationException or MemberAccessException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>A visual and what it holds; with an agent's look, also where it is and whether what clips it (its window, a
    /// scroll viewer) shows any of it, <paramref name="clip"/> being what its ancestors leave shown.</summary>
    private Element Node(Visual visual, TopLevel top, Look? look, Rect clip)
    {
        Rect? box = look is { Layout: true } ? Shown.RectIn(visual, top) : null;
        Rect inside = box is { } own && visual.ClipToBounds ? clip.Intersect(own) : clip;
        List<Element> children = [];
        foreach (Visual child in visual.GetVisualChildren())
        {
            // An adorner layer holding nothing of the app's: the one the outline of the element chosen made.
            if (!IsOwn(child) && !(child is AdornerLayer layer && layer.Children.All(c => IsOwn(c))))
            {
                children.Add(Node(child, top, look, inside));
            }
        }
        StyledElement? styled = visual as StyledElement;
        Element node = new(
            IdOf(visual),
            TypeName(visual.GetType()),
            styled?.Name,
            styled is null ? [] : [.. styled.Classes.Where(c => !c.StartsWith(':'))],
            styled?.TemplatedParent is not null,
            Details.Place(visual) is not null,
            visual.IsVisible,
            children);
        if (look is null)
        {
            return node;
        }
        if (look.Layout)
        {
            Rect shown = box?.Intersect(clip) ?? default;
            node = node with
            {
                Bounds = box is { } at ? Shown.Boxed(at) : null,
                Text = Shown.TextOf(visual),
                Disabled = visual is InputElement { IsEffectivelyEnabled: false } ? true : null,
                Focused = look.Focused == visual ? true : null,
                OutOfView = box is { Width: > 0, Height: > 0 } && (shown.Width <= 0 || shown.Height <= 0) ? true : null,
            };
        }
        return look.Properties.Count == 0 ? node : node with { Values = Shown.Values(visual, look.Properties) };
    }

    /// <summary>A visual as an agent's answer names it: its type and #name (or the text it shows), and the control whose
    /// template made it.</summary>
    public static string Named(Visual visual) =>
        visual is StyledElement { TemplatedParent: Visual parent } ? $"{Shown.Called(visual)} in {Shown.Called(parent)}'s template" : Shown.Called(visual);

    /// <summary>A type as the tree names it: no namespace, no generic arity.</summary>
    public static string TypeName(Type type)
    {
        string name = type.Name;
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }

    /// <summary>A control came or went: Passenger, if it has the tree, hears once that it changed, a moment after the last.</summary>
    private void Changed(object? sender)
    {
        if ((sender is Visual visual && IsOwn(visual)) || !watched || told)
        {
            return;
        }
        told = true;
        DispatcherTimer.RunOnce(() =>
        {
            watched = false;
            connection.Send(new Reply { Event = Wire.TreeChanged });
        }, Settle);
    }
}

/// <summary>Why the inspector will not do what it was asked (the element is disabled, covered, gone), in words an agent
/// reads as they are.</summary>
internal sealed class Refusal(string message) : Exception(message);

/// <summary>The ids Passenger knows the app's visuals by, each the same for as long as the visual lives: the Live Visual Tree's,
/// an agent's, and the profiler's frames' (<see cref="FrameRecorder"/>), so a control named in one is the one in the others.
/// The app's UI thread's alone.</summary>
internal static class ElementIds
{
    private static readonly ConditionalWeakTable<Visual, object> Ids = new();
    private static readonly Dictionary<long, WeakReference<Visual>> ById = [];
    private static long next;

    public static long Of(Visual visual)
    {
        if (Ids.TryGetValue(visual, out object? known))
        {
            return (long)known;
        }
        long id = ++next;
        Ids.Add(visual, id);
        if (ById.Count > 4096 && ById.Count % 4096 == 0)
        {
            foreach (long dead in ById.Where(e => !e.Value.TryGetTarget(out _)).Select(e => e.Key).ToList())
            {
                ById.Remove(dead);
            }
        }
        ById[id] = new WeakReference<Visual>(visual);
        return id;
    }

    public static Visual? Find(long? id) => id is long known && ById.TryGetValue(known, out WeakReference<Visual>? held) && held.TryGetTarget(out Visual? visual) ? visual : null;
}
