using System.Reflection;
using System.Runtime.InteropServices;

namespace Passenger.Inspector.Maui;

/// <summary>What the inspector reads of UIKit's own views, iOS's and Mac Catalyst's, where MAUI's cross-platform API says
/// nothing (where a view is in its window) and a finger's tap on them: UIKit's hit testing finds the view at the point, and
/// what takes the tap is given it as a finger's touch gives it, through the head's public UIKit bindings by reflection, as
/// the inspector is built for no head. UIKit has no public way to make a touch, so a control takes a touch's events, a text
/// field the focus a tap gives it, and a view a tap gesture watches is handed back for MAUI's own gesture to take. Every
/// read is null, and a tap refused, on any other head.</summary>
internal static class AppleViews
{
    /// <summary>UIControlEvent's TouchDown and TouchUpInside: a finger pressed on a control, and lifted inside it.</summary>
    private const ulong TouchDown = 1 << 0;
    private const ulong TouchUpInside = 1 << 6;

    private static readonly Type? View = UiKit("UIView");
    private static readonly Type? Control = UiKit("UIControl");
    private static readonly Type? TextField = UiKit("UITextField");
    private static readonly Type? TextView = UiKit("UITextView");
    private static readonly Type? TapGesture = UiKit("UITapGestureRecognizer");
    private static readonly Type? Event = UiKit("UIEvent");
    private static readonly Type? ControlEvent = UiKit("UIControlEvent");
    private static readonly Type? Point = Head("CoreGraphics.CGPoint");
    private static readonly Type? Rect = Head("CoreGraphics.CGRect");

    /// <summary>Whether the app runs on UIKit: on iOS or Mac Catalyst.</summary>
    public static bool Here => View is not null;

    /// <summary>A view's own size in its points, a window's the size of what it shows; null for what is no UIKit view.</summary>
    public static (double Width, double Height)? Size(object? view) =>
        view is not null && View?.IsInstanceOfType(view) == true && Read(view, "Bounds") is { } bounds ? (Number(bounds, "Width"), Number(bounds, "Height")) : null;

    /// <summary>A view's box in its window, in the window's points, as UIKit placed it there: below the bars and insets of
    /// what holds it, less what a scroll view has scrolled. Null for what is no UIKit view, or one in no window now.</summary>
    public static Box? InWindow(object? view)
    {
        MethodInfo? convert = View is null || Rect is null ? null : View.GetMethod("ConvertRectToView", [Rect, View]);
        if (view is null || convert is null || !View!.IsInstanceOfType(view) || Read(view, "Window") is null || Read(view, "Bounds") is not { } bounds)
        {
            return null;
        }
        object placed = convert.Invoke(view, [bounds, null])!;
        return Words.Boxed(Number(placed, "X"), Number(placed, "Y"), Number(placed, "Width"), Number(placed, "Height"));
    }

    /// <summary>A finger's tap at a point of a window (a UIWindow), in its points: the view UIKit's hit test finds there, and the
    /// first of it and what holds it that takes a tap, given it. Why not, where the app is on no UIKit or its bindings lack
    /// what a tap needs; else the view a tap gesture watches that the tap reached, for MAUI's gesture to take, or none.</summary>
    public static (string? Why, object? Watched) Tap(object? window, double x, double y)
    {
        MethodInfo? hitTest = View is null || Point is null || Event is null ? null : View.GetMethod("HitTest", [Point, Event]);
        ConstructorInfo? at = Point?.GetConstructor([typeof(double), typeof(double)]);
        MethodInfo? send = Control is null || ControlEvent is null ? null : Control.GetMethod("SendActionForControlEvents", [ControlEvent]);
        if (hitTest is null || at is null || send is null)
        {
            return ("A tap is given only on Android, iOS and Mac Catalyst so far", null);
        }
        if (window is null || !View!.IsInstanceOfType(window))
        {
            return ("The window has no UIKit window to tap", null);
        }
        for (object? view = hitTest.Invoke(window, [at.Invoke([x, y]), null]); view is not null; view = Read(view, "Superview"))
        {
            if (TextField?.IsInstanceOfType(view) == true || TextView?.IsInstanceOfType(view) == true)
            {
                // A tap on a field gives it the keys, as a finger's does.
                View.GetMethod("BecomeFirstResponder", Type.EmptyTypes)?.Invoke(view, null);
                return (null, null);
            }
            if (Control?.IsInstanceOfType(view) == true)
            {
                if (Read(view, "Enabled") is not false)
                {
                    send.Invoke(view, [Enum.ToObject(ControlEvent!, TouchDown)]);
                    send.Invoke(view, [Enum.ToObject(ControlEvent!, TouchUpInside)]);
                }
                return (null, null);
            }
            if (Read(view, "GestureRecognizers") is Array recognizers && recognizers.Cast<object>().Any(r => TapGesture?.IsInstanceOfType(r) == true && Read(r, "Enabled") is not false))
            {
                return (null, view);
            }
        }
        return (null, null);
    }

    /// <summary>A property of a UIKit object, by name; null where it has none.</summary>
    private static object? Read(object of, string property) => of.GetType().GetProperty(property)?.GetValue(of);

    /// <summary>A number of a CoreGraphics value (a CGRect's X), by name; 0 where it has none.</summary>
    private static double Number(object of, string property) => Read(of, property) is NFloat number ? number : 0;

    /// <summary>A type of UIKit's bindings, as the head the app runs on names its assembly.</summary>
    private static Type? UiKit(string name) => Head($"UIKit.{name}");

    /// <summary>A type of the head's bindings: iOS's, else Mac Catalyst's; null on any other head.</summary>
    private static Type? Head(string name) =>
        Type.GetType($"{name}, Microsoft.iOS", throwOnError: false) ?? Type.GetType($"{name}, Microsoft.MacCatalyst", throwOnError: false);
}
