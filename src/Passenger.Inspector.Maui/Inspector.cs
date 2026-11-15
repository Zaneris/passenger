using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using MauiElement = Microsoft.Maui.Controls.Element;

namespace Passenger.Inspector.Maui;

/// <summary>The inspector's work on a MAUI app's UI thread: the windows and their visual trees as MAUI's own API gives them
/// (IVisualTreeElement, from each window down), an element looked at closely, Select Element and the outline over the element
/// chosen (<see cref="Picker"/>), a word to Passenger as elements come and go, and for an agent the windows' pictures and a
/// finger's taps (<see cref="Driver"/>). Elements are named by ids that last as long as the element does. A top level is
/// one of the Application's windows.</summary>
internal sealed class Inspector : Inspecting<MauiElement>
{
    private readonly Picker picker;
    private readonly Driver driver;
    private readonly Application app;

    public Inspector(Connection connection) : base(connection)
    {
        app = Application.Current ?? throw new InvalidOperationException("The app has no Application");
        picker = new Picker(this, connection);
        driver = new Driver(this);
        app.DescendantAdded += OnCame;
        app.DescendantRemoved += OnCame;
        VisualDiagnostics.VisualTreeChanged += OnTreeChanged;
    }

    /// <summary>An element came into or went from the app's windows, said by MAUI as it happens, inside the app's own change.</summary>
    private void OnCame(object? sender, ElementEventArgs args) => Changed(args.Element);

    /// <summary>MAUI's own word that its visual tree changed, which it gives where it records its XAML's places.</summary>
    private void OnTreeChanged(object? sender, VisualTreeChangeEventArgs args) => Changed(args.Child);

    protected override void Stopping()
    {
        app.DescendantAdded -= OnCame;
        app.DescendantRemoved -= OnCame;
        VisualDiagnostics.VisualTreeChanged -= OnTreeChanged;
        picker.Choose(false);
        picker.Outline(null);
        picker.Stop();
    }

    protected override ElementDetails DetailsOf(MauiElement element) => new Details(this).Of(element);

    protected override void Pick(bool on) => picker.Choose(on);

    protected override void Outline(MauiElement? element) => picker.Outline(element);

    protected override Picture Capture(MauiElement element, Request request) =>
        throw new InvalidOperationException("A MAUI app's pictures are taken as they are drawn, through CaptureAsync");

    protected override InputDone Do(InputAction input) => driver.Do(input);

    protected override void Watch(int breakpoint, MauiElement element, string property, string? hitCondition) =>
        throw new Refusal("Property breakpoints are an Avalonia or WPF app's: a MAUI app's are not watched yet");

    protected override void Unwatch(int breakpoint)
    {
    }

    protected override XamlValues ValuesOf(Request request) =>
        throw new Refusal("Binding values after a XAML file's lines are read from an Avalonia app's .axaml; a MAUI app's are not");

    protected override XamlValues MadeAt(XamlRead read) => throw new Refusal("A MAUI app's elements are not yet found by their place in a XAML file");

    protected override List<AppWindow> SetOverlays(Request request) => throw new Refusal("Renderer debug overlays are Avalonia's: a MAUI app has none");

    protected override XamlReloaded Reload(XamlChange change) => throw new Refusal("XAML Hot Reload does not reach a MAUI app yet");

    protected override void Later(TimeSpan delay, Action action) => app.Dispatcher.DispatchDelayed(delay, action);

    protected override bool LeftOut(MauiElement element) => false;

    /// <summary>What an input did, read again once the app has run what it set going: what the element it reached shows
    /// now (a button's text its click changed), and what has the focus.</summary>
    public InputDone Read(InputDone done) =>
        done with
        {
            Shows = Find(done.Target) is { } target ? Shown.TextOf(target) : done.Shows,
            Focused = Find(done.Window) is Window window ? Driver.Focused(window) : done.Focused,
        };

    /// <summary>A picture of a window as its platform draws it, or of an element's part of it, as MAUI's own capture takes
    /// them; an area of a window is refused, as MAUI takes none.</summary>
    public async Task<Reply> CaptureAsync(Request request)
    {
        if (Find(request.Element) is not { } element)
        {
            return new Reply { Id = request.Id, Error = Words.ElementGone };
        }
        if (request.Area is not null)
        {
            return new Reply { Id = request.Id, Error = "A MAUI app's pictures are of a window or an element, not an area of one: name the element" };
        }
        Window top = TopOf(element) ?? throw new Refusal($"{Shown.Called(element)} is in no window");
        Places places = new(top);
        Box area = places.Of(element) ?? throw new Refusal($"{Shown.Called(element)} is not shown in its window");
        IScreenshotResult? shot = element == top ? await WindowExtensions.CaptureAsync(top)
            : element is IView view ? await Microsoft.Maui.ViewExtensions.CaptureAsync(view)
            : null;
        if (shot is null)
        {
            return new Reply { Id = request.Id, Error = $"{Shown.Called(element)} drew no picture" };
        }
        using MemoryStream png = new();
        await using (Stream read = await shot.OpenReadAsync(ScreenshotFormat.Png))
        {
            await read.CopyToAsync(png);
        }
        double scale = area.Width > 0 ? shot.Width / area.Width : places.Density;
        return new Reply { Id = request.Id, Picture = new Picture(png.ToArray(), shot.Width, shot.Height, Math.Round(scale, 4), area) };
    }

    protected override AppTree Tree(Request request)
    {
        Look? look = request.Layout == true || request.Properties is { Count: > 0 } ? new Look(request.Layout == true, request.Properties ?? []) : null;
        return new AppTree([.. TopLevels().Select(top => Node(top, look))]);
    }

    /// <summary>What an agent's tree carries beyond each element's identity: layout and text, and property values.</summary>
    private sealed record Look(bool Layout, IReadOnlyList<string> Properties);

    protected override List<AppWindow> Windows()
    {
        List<AppWindow> windows = [];
        foreach (Window top in TopLevels())
        {
            Places places = new(top);
            windows.Add(new AppWindow(IdOf(top), Words.TypeName(top.GetType()), Shown.Name(top), top.Title, places.Client.Width, places.Client.Height,
                Math.Round(places.Density, 4), top.IsActivated, true, null));
        }
        return windows;
    }

    /// <summary>The app's windows whose platform has made them.</summary>
    public IReadOnlyList<Window> TopLevels() => [.. app.Windows.Where(w => w.Handler is not null)];

    /// <summary>The window an element is in, the element itself when it is one.</summary>
    public Window? TopOf(MauiElement element)
    {
        for (MauiElement? at = element; at is not null; at = at.Parent)
        {
            if (at is Window window)
            {
                return window;
            }
        }
        return null;
    }

    /// <summary>A window and what it holds (<see cref="ElementTrees.Made"/>); with an agent's look, also where each is and
    /// whether what clips it (its window, a scroll view) shows any of it.</summary>
    private Element Node(Window top, Look? look)
    {
        Places places = new(top);
        return ElementTrees.Made<MauiElement, Clipped>(top, new Clipped(places.Client, null, places.Client),
            Children,
            (element, holder) =>
            {
                Box? box = look is { Layout: true } ? places.Of(element) : null;
                return new Clipped(holder.Inside, box, box is { } own && Clips(element) ? Overlap(holder.Inside, own) ?? new Box(own.X, own.Y, 0, 0) : holder.Inside);
            },
            (element, where, children) => Node(element, look, where, children));
    }

    /// <summary>Where an element is: what its holders leave shown, its box with an agent's look, and what it leaves shown of
    /// what it holds.</summary>
    private readonly record struct Clipped(Box Clip, Box? Box, Box Inside);

    /// <summary>An element with what it holds made.</summary>
    private Element Node(MauiElement element, Look? look, Clipped where, IReadOnlyList<Element> children)
    {
        Element node = new(IdOf(element), Words.TypeName(element.GetType()), Shown.Name(element),
            element is VisualElement { StyleClass: { Count: > 0 } classes } ? [.. classes] : [],
            false, Shown.Place(element) is not null, element is not VisualElement { IsVisible: false }, children);
        if (look is null)
        {
            return node;
        }
        if (look.Layout)
        {
            Box? box = where.Box;
            node = node with
            {
                Bounds = box,
                Text = Shown.TextOf(element),
                Disabled = element is VisualElement { IsEnabled: false } ? true : null,
                Focused = element is VisualElement { IsFocused: true } ? true : null,
                OutOfView = box is { Width: > 0, Height: > 0 } && Overlap(box, where.Clip) is null ? true : null,
            };
        }
        return look.Properties.Count == 0 ? node : node with { Values = Shown.Values(element, look.Properties) };
    }

    /// <summary>What an element holds in MAUI's visual tree, in its order.</summary>
    public static IReadOnlyList<MauiElement> Children(MauiElement element) => [.. ((IVisualTreeElement)element).GetVisualChildren().OfType<MauiElement>()];

    /// <summary>Whether an element clips what it holds to its box: a scroll view, a layout told to.</summary>
    public static bool Clips(MauiElement element) => element is ScrollView or Layout { IsClippedToBounds: true };

    /// <summary>Where two boxes overlap; null when they share no area.</summary>
    public static Box? Overlap(Box a, Box b)
    {
        double left = Math.Max(a.X, b.X);
        double top = Math.Max(a.Y, b.Y);
        double right = Math.Min(a.X + a.Width, b.X + b.Width);
        double bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right > left && bottom > top ? new Box(left, top, right - left, bottom - top) : null;
    }
}
