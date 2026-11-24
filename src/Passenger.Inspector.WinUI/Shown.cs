namespace Passenger.Inspector.WinUI;

/// <summary>What a WinUI app shows of an element, read through the app's own WinUI by reflection on the UI thread: its box
/// in its window, the text it shows, whether it is enabled and shown, and its window as an agent lists it.</summary>
internal static class Shown
{
    /// <summary>An element of the tree with where it is in its window, its text, and whether it is disabled, as an agent's
    /// look asks for them.</summary>
    public static Element Laid(Inspector inspector, Visual visual, Element made)
    {
        if (inspector.ObjectOf(visual) is not { } shown)
        {
            return made;
        }
        return made with
        {
            Bounds = BoxIn(shown),
            Text = Words.Shows(TextOf(shown)),
            Disabled = Projected.Get(shown, "IsEnabled") is false ? true : null,
        };
    }

    /// <summary>The text an element shows: a TextBlock's or TextBox's, a password's masked, a string content's.</summary>
    public static string? TextOf(object shown)
    {
        if (Projected.Is(shown, "Microsoft.UI.Xaml.Controls.PasswordBox"))
        {
            return Words.Dots(Projected.Get(shown, "Password") as string);
        }
        if (Projected.Is(shown, "Microsoft.UI.Xaml.Controls.TextBlock") || Projected.Is(shown, "Microsoft.UI.Xaml.Controls.TextBox"))
        {
            return Projected.Get(shown, "Text") as string;
        }
        return Projected.Is(shown, "Microsoft.UI.Xaml.Controls.ContentControl") && Projected.Get(shown, "Content") is string content ? content : null;
    }

    /// <summary>An element's box in its window's units, its transforms included; null for one in no window or not laid out.</summary>
    public static Box? BoxIn(object shown)
    {
        if (!Projected.Is(shown, "Microsoft.UI.Xaml.UIElement") || Projected.Call(shown, "TransformToVisual", [null]) is not { } transform)
        {
            return null;
        }
        double width = Projected.Get(shown, "ActualWidth") is double w ? w : 0;
        double height = Projected.Get(shown, "ActualHeight") is double h ? h : 0;
        object rect = Projected.Make("Windows.Foundation.Rect", 0d, 0d, width, height);
        object placed = Projected.Call(transform, "TransformBounds", rect)!;
        return Words.Boxed(Projected.Number(placed, "X"), Projected.Number(placed, "Y"), Projected.Number(placed, "Width"), Projected.Number(placed, "Height"));
    }

    /// <summary>A top level as an agent lists it: its window's title, size, scaling and whether it is in front and shown.</summary>
    public static Func<Visual, AppWindow> Window(Inspector inspector) => top =>
    {
        object? root = inspector.ContentObjectOf(top);
        object? xamlRoot = root is null ? null : Projected.Get(root, "XamlRoot");
        object? size = xamlRoot is null ? null : Projected.Get(xamlRoot, "Size");
        double scaling = xamlRoot is not null && Projected.Get(xamlRoot, "RasterizationScale") is double scale ? scale : 1;
        nint window = WindowOf(xamlRoot);
        bool isWindow = Inspector.IsWindow(top);
        return new AppWindow(inspector.IdOf(top), isWindow ? "Window" : "Popup", top.Name, window != 0 ? Inspector.TitleOf(window) : null,
            Math.Round(Projected.Number(size, "Width"), 2), Math.Round(Projected.Number(size, "Height"), 2), scaling,
            window != 0 && Inspector.GetForegroundWindow() == window, window == 0 || Inspector.IsWindowVisible(window), null);
    };

    /// <summary>The window a XamlRoot is shown in, as Windows names it; zero for none known.</summary>
    public static nint WindowOf(object? xamlRoot)
    {
        if (xamlRoot is null || Projected.Get(xamlRoot, "ContentIslandEnvironment") is not { } environment || Projected.Get(environment, "AppWindowId") is not { } id)
        {
            return 0;
        }
        if (Projected.Type("Microsoft.UI.Win32Interop") is not null && Projected.CallStatic("Microsoft.UI.Win32Interop", "GetWindowFromWindowId", id) is nint window)
        {
            return window;
        }
        object? value = id.GetType().GetProperty("Value")?.GetValue(id) ?? id.GetType().GetField("Value")?.GetValue(id);
        return value is ulong raw ? (nint)raw : 0;
    }
}
