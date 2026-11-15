using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MauiElement = Microsoft.Maui.Controls.Element;

namespace Passenger.Inspector.Maui;

/// <summary>A MAUI element looked at closely: what it is, its style classes and visual states, where it is laid out, and
/// every bindable property it has with its value, set on it or its default. MAUI keeps no record the app can read of which
/// style or binding gave a value, so a value set is said set, nothing more.</summary>
internal sealed class Details(Inspector inspector)
{
    /// <summary>Why the details name no place in the app's XAML.</summary>
    private const string NoSource = "MAUI recorded no place in the app's XAML for it: its Debug build records one for each element its XAML makes";

    public ElementDetails Of(MauiElement element)
    {
        Type type = element.GetType();
        SourcePlace? place = Shown.Place(element);
        Box? inWindow = inspector.TopOf(element) is { } top ? new Places(top).Of(element) : null;
        IView? view = element as IView;
        Microsoft.Maui.Graphics.Rect frame = view?.Frame ?? default;
        LayoutInfo layout = new(
            Words.Boxed(frame.X, frame.Y, frame.Width, frame.Height),
            inWindow,
            view?.DesiredSize.Width ?? 0,
            view?.DesiredSize.Height ?? 0,
            view is null ? new Edges(0, 0, 0, 0) : Edged(view.Margin),
            null,
            element is IPadding padded ? Edged(padded.Padding) : null,
            view?.HorizontalLayoutAlignment.ToString() ?? "Fill",
            view?.VerticalLayoutAlignment.ToString() ?? "Fill");
        return new ElementDetails(
            inspector.IdOf(element),
            Words.TypeName(type),
            type.FullName ?? type.Name,
            Shown.Name(element),
            element is VisualElement { StyleClass: { } classes } ? [.. classes] : [],
            States(element),
            place,
            place is null ? NoSource : null,
            null,
            layout,
            Properties(element),
            []);
    }

    /// <summary>The visual state each of an element's state groups is in ("Normal", "Focused").</summary>
    private static List<string> States(MauiElement element) =>
        element is VisualElement visual
            ? [.. VisualStateManager.GetVisualStateGroups(visual).Select(g => g.CurrentState?.Name).OfType<string>()]
            : [];

    /// <summary>Every bindable property of the element's type and its bases, with its value: set on it, or its default.</summary>
    private static List<PropertyValue> Properties(MauiElement element)
    {
        List<PropertyValue> values = [];
        foreach (BindableProperty property in Shown.Properties(element.GetType()))
        {
            string value;
            try
            {
                value = Shown.Value(element.GetValue(property));
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException or NullReferenceException)
            {
                // The app's own code (a property's coercion, a converter) said why it has no value now.
                value = $"({error.GetType().Name}: {error.Message})";
            }
            values.Add(new PropertyValue(property.PropertyName, Words.TypeName(property.DeclaringType), false, Words.TypeName(property.ReturnType), value,
                element.IsSet(property) ? ValueSources.Local : ValueSources.Default, null, null));
        }
        return values;
    }

    private static Edges Edged(Thickness edges) => new(edges.Left, edges.Top, edges.Right, edges.Bottom);
}
