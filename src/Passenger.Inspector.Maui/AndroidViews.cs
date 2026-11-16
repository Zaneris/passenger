using System.Reflection;

namespace Passenger.Inspector.Maui;

/// <summary>What the inspector reads of Android's own views where MAUI's cross-platform API says nothing (where a view is in
/// its window, an activity's window and size) and the touch a person's finger gives an activity, through Mono.Android's
/// public bindings by reflection, as the inspector is built for no head. Every read is null, and a touch refused, on any
/// other head.</summary>
internal static class AndroidViews
{
    /// <summary>MotionEvent's ACTION_DOWN and ACTION_UP.</summary>
    private const int Down = 0;
    private const int Up = 1;

    /// <summary>InputDevice's SOURCE_TOUCHSCREEN, what a finger's touch comes from.</summary>
    private const int Touchscreen = 0x1002;

    private static readonly Type? View = Type.GetType("Android.Views.View, Mono.Android", throwOnError: false);
    private static readonly Type? Motion = Type.GetType("Android.Views.MotionEvent, Mono.Android", throwOnError: false);
    private static readonly Type? Clock = Type.GetType("Android.OS.SystemClock, Mono.Android", throwOnError: false);

    /// <summary>Whether the app runs on Android.</summary>
    public static bool Here => View is not null;

    /// <summary>A view's top left in its window, in pixels; null for what is no Android view, or one in no window now.</summary>
    public static (int X, int Y)? InWindow(object? view) => Located(view, "GetLocationInWindow");

    /// <summary>A view's top left on its screen, in pixels; null for what is no Android view.</summary>
    public static (int X, int Y)? OnScreen(object? view) => Located(view, "GetLocationOnScreen");

    /// <summary>A view's laid out size in pixels; null for what is no Android view, or one not laid out.</summary>
    public static (int Width, int Height)? Size(object? view) =>
        view is not null && View?.IsInstanceOfType(view) == true && View.GetProperty("Width")?.GetValue(view) is int width
            && View.GetProperty("Height")?.GetValue(view) is int height && (width > 0 || height > 0)
            ? (width, height)
            : null;

    /// <summary>The root view of an activity's window, whose size is the window's; null for what is no activity.</summary>
    public static object? Decor(object? activity) =>
        activity?.GetType().GetProperty("Window")?.GetValue(activity) is { } window ? window.GetType().GetProperty("DecorView")?.GetValue(window) : null;

    /// <summary>A finger's tap at a point of an activity's window, in pixels, given as its window takes a person's: pressed and
    /// lifted there. Why not, where the app is on no Android or its bindings lack what a touch needs; else null.</summary>
    public static string? Tap(object? activity, float x, float y)
    {
        MethodInfo? obtain = Motion?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Obtain" && m.GetParameters() is
            [{ ParameterType: var downTime }, { ParameterType: var eventTime }, { ParameterType.IsEnum: true }, { ParameterType: var px }, { ParameterType: var py }, { ParameterType.IsEnum: true }]
            && downTime == typeof(long) && eventTime == typeof(long) && px == typeof(float) && py == typeof(float));
        MethodInfo? dispatch = activity is null || Motion is null ? null : activity.GetType().GetMethod("DispatchTouchEvent", [Motion]);
        MethodInfo? uptime = Clock?.GetMethod("UptimeMillis", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
        if (obtain is null || dispatch is null || uptime is null)
        {
            return activity is null ? "The window has no activity to touch" : "A tap is given only on Android, iOS and Mac Catalyst so far";
        }
        ParameterInfo[] parameters = obtain.GetParameters();
        long down = (long)uptime.Invoke(null, null)!;
        foreach (int action in (int[])[Down, Up])
        {
            object touch = obtain.Invoke(null, [down, action == Down ? down : (long)uptime.Invoke(null, null)!, Enum.ToObject(parameters[2].ParameterType, action), x, y,
                Enum.ToObject(parameters[5].ParameterType, 0)])!;
            if (Motion!.GetProperty("Source") is { CanWrite: true } source)
            {
                source.SetValue(touch, Enum.ToObject(source.PropertyType, Touchscreen));
            }
            dispatch.Invoke(activity, [touch]);
            Motion.GetMethod("Recycle", Type.EmptyTypes)?.Invoke(touch, null);
        }
        return null;
    }

    /// <summary>Where a view is, by a method of View's that fills two numbers; null for what is no Android view, or one in no
    /// window now.</summary>
    private static (int X, int Y)? Located(object? view, string method)
    {
        if (view is null || View?.IsInstanceOfType(view) != true || View.GetMethod(method, [typeof(int[])]) is not { } locate
            || View.GetProperty("IsAttachedToWindow")?.GetValue(view) is false)
        {
            return null;
        }
        int[] at = new int[2];
        locate.Invoke(view, [at]);
        return (at[0], at[1]);
    }
}
