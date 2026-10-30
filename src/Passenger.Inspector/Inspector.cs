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
internal sealed class Inspector : Inspecting<Visual>
{
    private readonly List<IDisposable> handlers = [];
    private readonly Picker picker;
    private readonly Driver driver;
    private readonly PropertyBreaks breaks = new();
    private readonly ValueWatch values;

    /// <param name="overlays">The renderer debug overlays the launch asked for (<see cref="Wire.OverlaysVariable"/>), or null.</param>
    public Inspector(Connection connection, string? overlays = null) : base(connection)
    {
        Dispatcher.UIThread.VerifyAccess();
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

    protected override void Stopping()
    {
        breaks.Stop();
        values.Stop();
        picker.Choose(false);
        picker.Outline(null);
        foreach (IDisposable handler in handlers)
        {
            handler.Dispose();
        }
    }

    protected override ElementDetails DetailsOf(Visual element) => new Details(this).Of(element);

    protected override void Pick(bool on) => picker.Choose(on);

    protected override void Outline(Visual? element) => picker.Outline(element);

    protected override Picture Capture(Visual element, Request request) => Pictures.Take(element, request);

    protected override InputDone Do(InputAction input) => driver.Do(input);

    protected override void Watch(int breakpoint, Visual element, string property, string? hitCondition) => breaks.Watch(breakpoint, element, property, hitCondition);

    protected override void Unwatch(int breakpoint) => breaks.Unwatch(breakpoint);

    /// <summary>A XAML file's bindings with their values, the properties read watched from now so the app says when they change.</summary>
    protected override XamlValues ValuesOf(Request request)
    {
        XamlRead read = request.Xaml ?? throw new Refusal("No XAML file was named");
        List<(AvaloniaObject, AvaloniaProperty)> watched = [];
        XamlValues answer = BindingReader.Read(this, read, null, watched);
        values.Watch(read.File, watched, read.More == true);
        return answer;
    }

    /// <summary>The overlays asked for set on every window, those opened later too, and the windows as they are now.</summary>
    protected override List<AppWindow> SetOverlays(Request request)
    {
        Overlays.Want(request.Overlays ?? [], TopLevels());
        return Windows();
    }

    protected override void Later(TimeSpan delay, Action action) => DispatcherTimer.RunOnce(action, delay);

    /// <summary>Whether a visual is the inspector's own (Select Element's outline), which the tree leaves out.</summary>
    public bool IsOwn(Visual visual) => picker.IsOwn(visual);

    /// <summary>What the tree leaves out: the inspector's own, and an adorner layer holding nothing of the app's (the one a
    /// window makes for the first outline), whose coming is no change in the app's controls either.</summary>
    protected override bool LeftOut(Visual element) => IsOwn(element) || (element is AdornerLayer layer && layer.Children.All(c => IsOwn(c)));

    protected override AppTree Tree(Request request)
    {
        Look? look = request.Layout == true || request.Properties is { Count: > 0 }
            ? new Look(request.Layout == true, request.Properties ?? [], Shown.Focused())
            : null;
        return new AppTree([.. TopLevels().Select(top => Node(top, top, look, new Rect(top.ClientSize)))]);
    }

    /// <summary>What an agent's tree carries beyond each element's identity: layout and text, and property values.</summary>
    private sealed record Look(bool Layout, IReadOnlyList<string> Properties, IInputElement? Focused);

    protected override List<AppWindow> Windows() => [.. TopLevels().Select(top => new AppWindow(
        IdOf(top),
        Words.TypeName(top.GetType()),
        top.Name,
        (top as Window)?.Title,
        top.ClientSize.Width,
        top.ClientSize.Height,
        top.RenderScaling,
        top is WindowBase { IsActive: true },
        top.IsVisible,
        top is PopupRoot { ParentTopLevel: { } parent } ? IdOf(parent) : null) { Overlays = Overlays.Of(top) })];

    /// <summary>The app's top levels, each once: its desktop lifetime's windows or its single view's, then every other
    /// one Avalonia is drawing (a popup's, or every window of an app with no lifetime).</summary>
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
        foreach (TopLevel top in AvaloniaHooks.Drawn())
        {
            if (!tops.Contains(top))
            {
                tops.Add(top);
            }
        }
        return tops;
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
            if (!LeftOut(child))
            {
                children.Add(Node(child, top, look, inside));
            }
        }
        StyledElement? styled = visual as StyledElement;
        Element node = new(
            IdOf(visual),
            Words.TypeName(visual.GetType()),
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
        Words.Named(Shown.Called(visual), visual is StyledElement { TemplatedParent: Visual parent } ? Shown.Called(parent) : null);
}
