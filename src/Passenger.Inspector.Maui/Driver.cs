using Microsoft.Maui.Controls;
using MauiElement = Microsoft.Maui.Controls.Element;

namespace Passenger.Inspector.Maui;

/// <summary>An agent's input to a MAUI app as a person gives it, aimed as every inspector aims (<see cref="Driving{T, TTop}"/>):
/// a click is a finger's tap given to the window's own way in from its platform (on Android, its activity's touch), so the
/// platform's hit testing, MAUI's gestures and a button's Clicked run as they would. Typing, keys and scrolling are refused
/// for now, saying so.</summary>
internal sealed class Driver(Inspector inspector) : Driving<MauiElement, Window>
{
    protected override string Hidden => "it, or an element holding it, has IsVisible false";

    protected override MauiElement? Find(long id) => inspector.Find(id);

    protected override Window? OpenTop(MauiElement element) => element is Window window && inspector.TopLevels().Contains(window) ? window : null;

    protected override Window? TopOf(MauiElement element) => inspector.TopOf(element);

    protected override InputDone Click(Window top, MauiElement? element, InputAction action)
    {
        if (action.Button is { } button && button != "left")
        {
            throw new Refusal($"A finger's tap is a MAUI app's click: there is no {button} button to press");
        }
        Places places = new(top);
        (double x, double y) = At(top, element, action, "tap");
        (float px, float py) = Placing.Pixel(x, y, places.Density);
        for (int tap = 0; tap < Math.Max(1, action.Count ?? 1); tap++)
        {
            if (AndroidViews.Tap(top.Handler?.PlatformView, px, py) is { } why)
            {
                throw new Refusal(why);
            }
        }
        MauiElement? took = Took(element, Hit(top, x, y));
        return new InputDone(inspector.IdOf(top), x, y, took is null ? null : inspector.IdOf(took), took is null ? null : Shown.Called(took),
            took is null ? null : Shown.TextOf(took), Focused(top));
    }

    protected override InputDone Scroll(Window top, MauiElement? element, InputAction action) =>
        throw new Refusal("Scrolling a MAUI app is not given yet: tap what scrolls it, or find the element in app_tree");

    protected override InputDone Type(Window top, MauiElement? element, InputAction action) =>
        throw new Refusal("Typing into a MAUI app is not given yet");

    protected override InputDone Keys(Window top, MauiElement? element, InputAction action) =>
        throw new Refusal("Keys to a MAUI app are not given yet");

    protected override Box Client(Window top) => new Places(top).Client;

    protected override bool IsShown(MauiElement element)
    {
        for (MauiElement? at = element; at is not null; at = at.Parent)
        {
            if (at is VisualElement { IsVisible: false })
            {
                return false;
            }
        }
        return true;
    }

    protected override bool IsEnabled(MauiElement element) => element is not VisualElement { IsEnabled: false };

    protected override Box? BoxIn(MauiElement element, Window top) => new Places(top).Of(element);

    protected override MauiElement? Parent(MauiElement element) => element.Parent;

    protected override bool Clips(MauiElement element) => Inspector.Clips(element);

    protected override MauiElement? ScrollerAround(MauiElement element)
    {
        for (MauiElement? at = element; at is not null; at = at.Parent)
        {
            if (at is ScrollView)
            {
                return at;
            }
        }
        return null;
    }

    /// <summary>What a finger at a point of the window reaches, by where the platform laid each element out: an element
    /// hidden or taking no input passed over with what it holds.</summary>
    protected override MauiElement? Hit(Window top, double x, double y)
    {
        Places places = new(top);
        return Placing.Deepest<MauiElement>(top, Inspector.Children, places.Of, e => e is not VisualElement { IsVisible: false } and not VisualElement { InputTransparent: true }, x, y);
    }

    protected override bool Within(MauiElement element, MauiElement ancestor)
    {
        for (MauiElement? at = element; at is not null; at = at.Parent)
        {
            if (at == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    protected override string Called(MauiElement element) => Shown.Called(element);

    protected override string Named(MauiElement element) => Shown.Called(element);

    /// <summary>The element of a window that has the focus, as a sentence names it; null for none.</summary>
    public static string? Focused(Window top) =>
        ElementTrees.Below<MauiElement>(top, Inspector.Children).FirstOrDefault(e => e is VisualElement { IsFocused: true }) is { } focused ? Shown.Called(focused) : null;
}
