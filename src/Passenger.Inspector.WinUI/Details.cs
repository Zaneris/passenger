namespace Passenger.Inspector.WinUI;

/// <summary>A WinUI element looked at closely, as WinUI's diagnostics read it: each property set on it with its value and
/// what gave it (its local value, a style and where the style is, its default style, a value it inherits), where its XAML
/// made it, and its layout read from the element itself.</summary>
internal static class Details
{
    public static ElementDetails Of(Inspector inspector, Visual visual)
    {
        (List<PropertyRead> values, List<ValueGiver> givers) = inspector.Diagnostics.ValuesChain(visual.Handle);
        List<PropertyValue> properties = [];
        foreach (PropertyRead read in values.Where(v => !v.Overridden).OrderBy(v => v.Name, StringComparer.Ordinal))
        {
            ValueGiver? giver = read.Giver >= 0 && read.Giver < givers.Count ? givers[read.Giver] : null;
            properties.Add(new PropertyValue(read.Name, Short(read.DeclaringType), false, Short(read.Type), Words.CutValue(read.Value ?? "(null)"), SourceOf(read.Source),
                DetailOf(read.Source, giver), giver is { File: { } file, Line: > 0 } ? inspector.PlaceOf(new Visual(0) { File = file, Line = giver.Line, Column = giver.Column }) : null));
        }
        object? shown = inspector.ObjectOf(visual);
        SourcePlace? source = inspector.PlaceOf(visual);
        return new ElementDetails(inspector.IdOf(visual), visual.Type, visual.FullType, visual.Name, [], [], source,
            source is null ? visual.FromTheme ? "WinUI's theme made it, in a control's template" : "The app's build recorded no XAML for it" : null,
            null, Layout(shown), properties, []);
    }

    /// <summary>A type's name without its namespace.</summary>
    private static string Short(string type) => type[(type.LastIndexOf('.') + 1)..];

    /// <summary>What gave a value, in the window's words (<see cref="ValueSources"/>).</summary>
    private static string SourceOf(int source) => source switch
    {
        Sources.Local => ValueSources.Local,
        Sources.Style => ValueSources.Style,
        Sources.BuiltInStyle => ValueSources.Theme,
        Sources.Inherited => ValueSources.Inherited,
        Sources.ParentTemplate => ValueSources.Template,
        Sources.Animation or Sources.VisualState => ValueSources.Animation,
        _ => ValueSources.Default,
    };

    /// <summary>What the giver of a value is, where the row says more than its source: a style's target and key, a visual
    /// state's.</summary>
    private static string? DetailOf(int source, ValueGiver? giver) => giver is null || source is Sources.Local or Sources.Default ? null
        : giver.Name is { Length: > 0 } key ? $"{(source == Sources.VisualState ? "Visual state" : "Style")} {key} for {Short(giver.Type)}"
        : source == Sources.Style ? $"Style for {Short(giver.Type)}"
        : null;

    /// <summary>Where an element is laid out, read from the element: its size, margin, padding and alignment.</summary>
    private static LayoutInfo Layout(object? shown)
    {
        if (shown is null || !Projected.Is(shown, "Microsoft.UI.Xaml.FrameworkElement"))
        {
            return new LayoutInfo(new Box(0, 0, 0, 0), null, 0, 0, new Edges(0, 0, 0, 0), null, null, string.Empty, string.Empty);
        }
        double width = Projected.Get(shown, "ActualWidth") is double w ? w : 0;
        double height = Projected.Get(shown, "ActualHeight") is double h ? h : 0;
        object? desired = Projected.Get(shown, "DesiredSize");
        return new LayoutInfo(new Box(0, 0, Math.Round(width, 2), Math.Round(height, 2)), Shown.BoxIn(shown), Number(desired, "Width"), Number(desired, "Height"),
            Edges(Projected.Get(shown, "Margin")) ?? new Edges(0, 0, 0, 0), Edges(Projected.Get(shown, "BorderThickness")), Edges(Projected.Get(shown, "Padding")),
            Projected.Get(shown, "HorizontalAlignment")?.ToString() ?? string.Empty, Projected.Get(shown, "VerticalAlignment")?.ToString() ?? string.Empty);
    }

    private static double Number(object? value, string name) => Math.Round(Projected.Number(value, name), 2);

    /// <summary>A Thickness's edges; null for an element without that property.</summary>
    private static Edges? Edges(object? thickness) =>
        thickness is null ? null : new Edges(Number(thickness, "Left"), Number(thickness, "Top"), Number(thickness, "Right"), Number(thickness, "Bottom"));
}
