using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Passenger.Inspector.Wpf;

/// <summary>What a person sees of an element, as an agent's tools read it: where it is in its window, the text it shows, the
/// values of properties named, and its window's size and scaling.</summary>
internal static class Shown
{
    /// <summary>The longest text an element's line carries.</summary>
    private const int Longest = 200;

    /// <summary>An element as a sentence names it: its type, then #name, or the text it shows when it has no name.</summary>
    public static string Called(DependencyObject element) =>
        element is FrameworkElement { Name.Length: > 0 } || TextOf(element) is not { Length: > 0 } text
            ? Details.Describe(element)
            : $"{Details.Describe(element)} \"{(text.Length > 40 ? text[..37] + "..." : text)}\"";

    /// <summary>A top level's client area in units: a window's less its frame (its content's own box), a popup root's whole.</summary>
    public static Size ClientSize(Visual top)
    {
        if (top is Window && VisualTreeHelper.GetChildrenCount(top) > 0 && VisualTreeHelper.GetChild(top, 0) is UIElement content)
        {
            return content.RenderSize;
        }
        return top is UIElement element ? element.RenderSize : default;
    }

    /// <summary>Pixels a unit on the top level's screen.</summary>
    public static double Scaling(Visual top) =>
        PresentationSource.FromVisual(top)?.CompositionTarget is { } target ? target.TransformToDevice.M11 : 1;

    /// <summary>An element's box in its top level's units, its transforms and its ancestors' included; null when it is in
    /// none, or not a visual of one (a 3D one).</summary>
    public static Rect? RectIn(DependencyObject element, Visual top)
    {
        if (element == top)
        {
            return new Rect(ClientSize(top));
        }
        if (element is not Visual visual || !top.IsAncestorOf(visual))
        {
            return null;
        }
        try
        {
            Rect box = visual.TransformToAncestor(top).TransformBounds(new Rect(SizeOf(visual)));
            // A box JSON can carry: an empty one (a visual that draws nothing) has infinite sides.
            return box.IsEmpty || !double.IsFinite(box.X) || !double.IsFinite(box.Y) || !double.IsFinite(box.Width) || !double.IsFinite(box.Height) ? null : box;
        }
        catch (InvalidOperationException)
        {
            // Not connected to it after all (a transform that does not invert).
            return null;
        }
    }

    /// <summary>A visual's own size: a UI element's rendered size, else its drawing's bounds (none for one that draws nothing).</summary>
    public static Size SizeOf(Visual visual)
    {
        if (visual is UIElement element)
        {
            return element.RenderSize;
        }
        Rect drawn = VisualTreeHelper.GetDescendantBounds(visual);
        return drawn.IsEmpty ? default : drawn.Size;
    }

    /// <summary>Whether an element clips what it holds to its box.</summary>
    public static bool Clips(DependencyObject element) => element is UIElement { ClipToBounds: true } || element is ScrollContentPresenter;

    /// <summary>A box as the protocol carries it, to a hundredth of a unit.</summary>
    public static Box Boxed(Rect rect) => Words.Boxed(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>The text an element shows: a text block's or text box's (a password box's masked), a content control's or a
    /// header's when it is text, a combo box's choice, a window's title; null for anything else.</summary>
    public static string? TextOf(DependencyObject element)
    {
        string? text = element switch
        {
            PasswordBox box => Words.Dots(box.Password),
            TextBox box => box.Text ?? string.Empty,
            TextBlock block => block.Text,
            AccessText access => access.Text,
            Window window => window.Title,
            HeaderedContentControl { Header: string header } => header,
            HeaderedItemsControl { Header: string header } => header,
            ComboBox { IsEditable: true } box => box.Text,
            ComboBox { SelectionBoxItem: string chosen } => chosen,
            ComboBox { SelectedItem: { } item } => item.ToString(),
            ContentControl { Content: string content } => content,
            ContentControl { Content: IFormattable formattable } => formattable.ToString(null, CultureInfo.InvariantCulture),
            ContentPresenter { Content: string content } when element is FrameworkElement { TemplatedParent: null } => content,
            _ => null,
        };
        return text is { Length: > Longest } ? text[..(Longest - 3)] + "..." : text;
    }

    /// <summary>The values of the properties named that an element has: its dependency properties by name, an attached one
    /// as Owner.Name, else a public CLR property.</summary>
    public static Dictionary<string, string>? Values(DependencyObject element, IReadOnlyList<string> names)
    {
        Dictionary<string, string> values = [];
        foreach (string name in names)
        {
            if (Value(element, name) is { } value)
            {
                values[name] = value;
            }
        }
        return values.Count == 0 ? null : values;
    }

    private static string? Value(DependencyObject element, string name)
    {
        if (Properties.Find(element, name) is { } property)
        {
            return Details.Format(element.GetValue(property));
        }
        if (!name.Contains('.', StringComparison.Ordinal) && element.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { GetMethod: not null } clr
            && clr.GetIndexParameters().Length == 0)
        {
            try
            {
                // A password box's password as the box shows it.
                return Details.Format(element is PasswordBox box && name == nameof(PasswordBox.Password) ? Words.Dots(box.Password) : clr.GetValue(element));
            }
            catch (TargetInvocationException error)
            {
                return $"({error.InnerException?.GetType().Name ?? error.GetType().Name}: {error.InnerException?.Message})";
            }
        }
        return null;
    }

    /// <summary>What a scroll viewer at or around an element shows: its offset in its extent.</summary>
    public static string? ScrolledOf(DependencyObject? element)
    {
        for (DependencyObject? at = element; at is not null; at = Parent(at))
        {
            if (at is ScrollViewer viewer)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"{Details.Describe(viewer)} at {viewer.HorizontalOffset:0.##},{viewer.VerticalOffset:0.##} of {viewer.ExtentWidth:0.##}x{viewer.ExtentHeight:0.##} (viewport {viewer.ViewportWidth:0.##}x{viewer.ViewportHeight:0.##})");
            }
        }
        return null;
    }

    /// <summary>An element's visual parent, else its logical one (a content element's).</summary>
    public static DependencyObject? Parent(DependencyObject element) =>
        (element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : null) ?? LogicalTreeHelper.GetParent(element);

    /// <summary>Whether an element is inside another, or is it.</summary>
    public static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? at = element; at is not null; at = Parent(at))
        {
            if (at == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The popup's root a <see cref="Popup"/> shows its child in, while it is open.</summary>
    public static Visual? RootOf(Popup popup) => popup.Child is { } child ? PresentationSource.FromVisual(child)?.RootVisual : null;
}
