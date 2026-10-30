using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Passenger.Inspector;

/// <summary>What a person sees of an element, as an agent's tools read it: where it is in its window, the text it shows,
/// the values of properties named, and what has the keyboard focus.</summary>
internal static class Shown
{
    /// <summary>An element as a sentence names it: its type, then #name, or the text it shows when it has no name.</summary>
    public static string Called(Visual visual) =>
        Words.Called(Details.Describe(visual), visual is StyledElement { Name.Length: > 0 } ? null : TextOf(visual));

    /// <summary>An element's box in its window's units, its transforms and its ancestors' included; null when it is in no
    /// window.</summary>
    public static Rect? RectIn(Visual visual, TopLevel top) =>
        visual == top ? new Rect(top.ClientSize) : visual.TransformToVisual(top) is { } matrix ? new Rect(visual.Bounds.Size).TransformToAABB(matrix) : null;

    /// <summary>A box as the protocol carries it, to a hundredth of a unit.</summary>
    public static Box Boxed(Rect rect) => Words.Boxed(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>The text an element shows: a text block's or text box's (a password box's masked), a content control's or a
    /// header's when it is text, a window's title; null for anything else.</summary>
    public static string? TextOf(Visual visual)
    {
        string? text = visual switch
        {
            TextBox { PasswordChar: not '\0' } box => Words.Dots(box.Text),
            TextBox box => box.Text ?? string.Empty,
            TextBlock block => block.Text,
            Window window => window.Title,
            HeaderedContentControl { Header: string header } => header,
            HeaderedItemsControl { Header: string header } => header,
            HeaderedSelectingItemsControl { Header: string header } => header,
            ComboBox { SelectionBoxItem: string chosen } => chosen,
            ContentControl { Content: string content } => content,
            ContentControl { Content: IFormattable formattable } => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
        return Words.Shows(text);
    }

    /// <summary>The values of the properties named that an element has: Avalonia's by name, an attached one as Owner.Name,
    /// else a public CLR property.</summary>
    public static Dictionary<string, string>? Values(Visual visual, IReadOnlyList<string> names) => Words.Values(names, name => Value(visual, name));

    private static string? Value(Visual visual, string name)
    {
        int dot = name.LastIndexOf('.');
        AvaloniaProperty? property = dot < 0
            ? AvaloniaPropertyRegistry.Instance.FindRegistered(visual, name)
            : AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(visual.GetType())
                .FirstOrDefault(p => p.Name == name[(dot + 1)..] && Words.TypeName(p.OwnerType) == name[..dot]);
        if (property is not null)
        {
            return Format(visual, name, visual.GetValue(property));
        }
        return dot < 0 ? Words.Read(visual, name, value => Format(visual, name, value)) : null;
    }

    /// <summary>Whether a property of an element, by its name, holds a password's text: the Text, SelectedText or
    /// PreeditText of one whose PasswordChar is set (a password box, and the presenter inside it).</summary>
    public static bool Hides(AvaloniaObject element, string name) =>
        name is "Text" or "SelectedText" or "PreeditText"
            && AvaloniaPropertyRegistry.Instance.FindRegistered(element, "PasswordChar") is { } password && element.GetValue(password) is char c and not '\0';

    /// <summary>A property's value as the inspector gives it (<see cref="Details.Format"/>), a password's text as its box
    /// shows it (<see cref="Hides"/>).</summary>
    public static string Format(AvaloniaObject element, string name, object? value) =>
        Details.Format(value is string text && Hides(element, name) ? Words.Dots(text) : value);

    /// <summary>The element with the keyboard focus, in whichever window.</summary>
    public static IInputElement? Focused() => (AvaloniaLocator.Current.GetService<IKeyboardDevice>() as KeyboardDevice)?.FocusedElement;

    /// <summary>What a scroll viewer at or around an element shows: its offset in its extent.</summary>
    public static string? ScrolledOf(Visual? visual)
    {
        for (Visual? at = visual; at is not null; at = at.GetVisualParent())
        {
            if (at is ScrollViewer viewer)
            {
                return Words.Scrolled(Details.Describe(viewer), viewer.Offset.X, viewer.Offset.Y, viewer.Extent.Width, viewer.Extent.Height, viewer.Viewport.Width, viewer.Viewport.Height);
            }
        }
        return null;
    }
}
