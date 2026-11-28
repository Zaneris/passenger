using System.Reflection;

namespace Passenger.Inspector.WinUI;

/// <summary>An agent's input to a WinUI app, never the person's pointer or keyboard: a click is UI Automation's pattern
/// (Invoke, Toggle, SelectionItem, ExpandCollapse) on the nearest element with one where WinUI's hit test lands; typing
/// sets a text box's text, as its Value pattern would; the wheel scrolls the scroll viewer around the element. WinUI takes
/// keys only from Windows' own input, so keys are refused, saying so.</summary>
internal sealed class Driver(Inspector inspector) : Driving<Visual, Visual>
{
    /// <summary>The patterns a click runs, in the order looked for.</summary>
    private static readonly (string Pattern, string Provider, string Method)[] Clicks =
    [
        ("Invoke", "Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider", "Invoke"),
        ("Toggle", "Microsoft.UI.Xaml.Automation.Provider.IToggleProvider", "Toggle"),
        ("SelectionItem", "Microsoft.UI.Xaml.Automation.Provider.ISelectionItemProvider", "Select"),
        ("ExpandCollapse", "Microsoft.UI.Xaml.Automation.Provider.IExpandCollapseProvider", "Expand"),
    ];

    protected override string Hidden => "it or an ancestor is collapsed";

    protected override Visual? Find(long id) => inspector.Find(id);

    protected override Visual? OpenTop(Visual element) => element.Parent is null && inspector.TopLevels().Contains(element) ? element : null;

    protected override Visual? TopOf(Visual element) => Inspector.TopOf(element);

    protected override InputDone Click(Visual top, Visual? element, InputAction action)
    {
        (double x, double y) = At(top, element, action, "click");
        if (action.Button is not (null or "left"))
        {
            throw new Refusal("A WinUI app takes an agent's left click alone, through UI Automation");
        }
        Visual? hit = Hit(top, x, y);
        Visual? took = Took(element, hit) ?? element;
        for (Visual? at = took; at is not null; at = at.Parent)
        {
            if (inspector.ObjectOf(at) is { } shown && Run(shown))
            {
                return Done(top, x, y, at);
            }
        }
        throw new Refusal($"{(took is null ? "Nothing" : Called(took))} at {Words.Spot(x, y)} takes no click an agent can give: no element there or above it can be invoked, toggled, selected or expanded");
    }

    /// <summary>Runs the first click pattern an element's automation peer has; false where it has none.</summary>
    private static bool Run(object shown)
    {
        if (!Projected.Is(shown, "Microsoft.UI.Xaml.UIElement")
            || Projected.CallStatic("Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer", "CreatePeerForElement", shown) is not { } peer)
        {
            return false;
        }
        foreach ((string pattern, string provider, string method) in Clicks)
        {
            if (Projected.Call(peer, "GetPattern", Projected.Enum("Microsoft.UI.Xaml.Automation.Peers.PatternInterface", pattern)) is { } found
                && Projected.Type(provider)?.GetMethod(method, BindingFlags.Public | BindingFlags.Instance, []) is { } call)
            {
                call.Invoke(found, null);
                return true;
            }
        }
        return false;
    }

    protected override InputDone Scroll(Visual top, Visual? element, InputAction action)
    {
        (double x, double y) = At(top, element, action, "scroll");
        Visual? at = Hit(top, x, y) ?? element;
        Visual scroller = (at is null ? null : ScrollerAround(at)) ?? throw new Refusal($"Nothing at {Words.Spot(x, y)} scrolls");
        object viewer = inspector.ObjectOf(scroller) ?? throw new Refusal(Words.ElementGone);
        double right = Projected.Get(viewer, "HorizontalOffset") is double h ? h : 0;
        double down = Projected.Get(viewer, "VerticalOffset") is double v ? v : 0;
        const double Notch = 48;
        Projected.Call(viewer, "ChangeView", right + ((action.DeltaX ?? 0) * Notch), down + ((action.DeltaY ?? 0) * Notch), null, true);
        return Done(top, x, y, scroller);
    }

    protected override InputDone Type(Visual top, Visual? element, InputAction action)
    {
        Visual target = element ?? throw new Refusal("Name the text box to type into");
        object box = inspector.ObjectOf(target) ?? throw new Refusal(Words.ElementGone);
        if (!Projected.Is(box, "Microsoft.UI.Xaml.Controls.TextBox") && !Projected.Is(box, "Microsoft.UI.Xaml.Controls.PasswordBox"))
        {
            throw new Refusal($"{Called(target)} takes no typing: name a TextBox or a PasswordBox");
        }
        string property = Projected.Is(box, "Microsoft.UI.Xaml.Controls.PasswordBox") ? "Password" : "Text";
        string before = action.Replace == true ? string.Empty : Projected.Get(box, property) as string ?? string.Empty;
        Projected.Set(box, property, before + (action.Text ?? string.Empty));
        return Done(top, null, null, target);
    }

    protected override InputDone Keys(Visual top, Visual? element, InputAction action) =>
        throw new Refusal("A WinUI app takes keys only from Windows' own input, which would be the person's: type text into a box, or click what a key would press");

    protected override Box Client(Visual top)
    {
        object? root = inspector.ContentObjectOf(top);
        object? size = root is null ? null : Projected.Get(Projected.Get(root, "XamlRoot") ?? root, "Size");
        return new Box(0, 0, Projected.Number(size, "Width"), Projected.Number(size, "Height"));
    }

    protected override bool IsShown(Visual element)
    {
        for (Visual? at = element; at is not null; at = at.Parent)
        {
            if (inspector.ObjectOf(at) is { } shown && Projected.Get(shown, "Visibility")?.ToString() == "Collapsed")
            {
                return false;
            }
        }
        return true;
    }

    protected override bool IsEnabled(Visual element) => inspector.ObjectOf(element) is not { } shown || Projected.Get(shown, "IsEnabled") is not false;

    protected override Box? BoxIn(Visual element, Visual top) => inspector.ObjectOf(element) is { } shown ? Shown.BoxIn(shown) : null;

    protected override Visual? Parent(Visual element) => element.Parent;

    protected override bool Clips(Visual element) => element.FullType.EndsWith(".ScrollViewer", StringComparison.Ordinal);

    protected override Visual? ScrollerAround(Visual element)
    {
        for (Visual? at = element; at is not null; at = at.Parent)
        {
            if (at.FullType.EndsWith(".ScrollViewer", StringComparison.Ordinal))
            {
                return at;
            }
        }
        return null;
    }

    /// <summary>What WinUI's hit test finds at a point of a window, topmost first.</summary>
    protected override Visual? Hit(Visual top, double x, double y)
    {
        object? root = inspector.ContentObjectOf(top);
        if (root is null || !Projected.Is(root, "Microsoft.UI.Xaml.UIElement"))
        {
            return null;
        }
        object point = Projected.Make("Windows.Foundation.Point", x, y);
        if (Projected.CallStatic("Microsoft.UI.Xaml.Media.VisualTreeHelper", "FindElementsInHostCoordinates", point, root) is not System.Collections.IEnumerable found)
        {
            return null;
        }
        foreach (object element in found)
        {
            if (inspector.VisualOf(element) is { } visual)
            {
                return visual;
            }
        }
        return null;
    }

    protected override bool Within(Visual element, Visual ancestor) => Inspector.Within(element, ancestor);

    protected override string Called(Visual element) =>
        Words.Called(element.ToString(), element.Name is null && inspector.ObjectOf(element) is { } shown ? Shown.TextOf(shown) : null);

    protected override string Named(Visual element) => Called(element);

    private InputDone Done(Visual top, double? x, double? y, Visual target)
    {
        object? shown = inspector.ObjectOf(target);
        return new InputDone(inspector.IdOf(top), x, y, inspector.IdOf(target), target.Name, shown is null ? null : Words.Shows(Shown.TextOf(shown)), null);
    }
}
