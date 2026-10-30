using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Core;
using Avalonia.Diagnostics;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Diagnostics;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Passenger.Inspector;

/// <summary>One visual looked at closely: its XAML, its layout, each Avalonia property with its value (a password's text
/// as its box shows it, <see cref="Shown.Hides"/>) and where the value came from, read from Avalonia's own value store
/// (GetDiagnostic for the value and its priority, GetValueStoreDiagnostic for the frames that gave it), and the styles
/// that could apply (<see cref="Styling"/>).</summary>
internal sealed class Details(Inspector inspector)
{
    public ElementDetails Of(Visual visual)
    {
        StyledElement? styled = visual as StyledElement;
        Type type = visual.GetType();
        (SourcePlace? source, string? noSource) = SourceOf(visual);
        Styling styling = new(visual);
        return new ElementDetails(
            inspector.IdOf(visual),
            Words.TypeName(type),
            type.FullName ?? type.Name,
            styled?.Name,
            styled is null ? [] : [.. styled.Classes.Where(c => !c.StartsWith(':'))],
            styled is null ? [] : [.. styled.Classes.Where(c => c.StartsWith(':'))],
            source,
            noSource,
            styled?.TemplatedParent is Visual parent ? Describe(parent) : null,
            Layout(visual),
            Properties(visual, styling),
            styling.Reports());
    }

    /// <summary>Where a visual's XAML is: its own element, or for a root made by its class (a Window, a UserControl) the
    /// file its content came from, at its root (line 0); else why there is none.</summary>
    private static (SourcePlace?, string?) SourceOf(Visual visual)
    {
        if (Place(visual) is { } own)
        {
            return (own, null);
        }
        if (visual is StyledElement { TemplatedParent: Visual parent })
        {
            return (null, $"Made by {Words.TypeName(parent.GetType())}'s template, which was compiled without source information (a theme's, or a Release build's)");
        }
        if (visual is ILogical logical)
        {
            foreach (ILogical child in logical.GetLogicalDescendants().Take(64))
            {
                if (child is StyledElement { TemplatedParent: null } made && Place(made) is { } inside)
                {
                    return (inside with { Line = 0, Column = 0 }, null);
                }
            }
        }
        return (null, "Made in code, or by XAML compiled without source information (a Release build, or AvaloniaXamlCreateSourceInfo off)");
    }

    /// <summary>Where Avalonia's XAML compiler recorded an object was written (under a Debug build), or null.</summary>
    public static SourcePlace? Place(object made)
    {
        try
        {
            return XamlSourceInfo.GetXamlSourceInfo(made) is { SourceUri: { } file } info
                ? new SourcePlace(file.IsFile ? file.LocalPath : file.ToString(), info.LineNumber, info.LinePosition)
                : null;
        }
        catch (Exception error) when (error is MissingMethodException or TypeLoadException)
        {
            // An Avalonia 12 before XAML source information.
            return null;
        }
    }

    /// <summary>A visual as a sentence names it: its type, then #name.</summary>
    public static string Describe(Visual visual) =>
        visual is StyledElement { Name: { Length: > 0 } name } ? $"{Words.TypeName(visual.GetType())}#{name}" : Words.TypeName(visual.GetType());

    private static LayoutInfo Layout(Visual visual)
    {
        Rect bounds = visual.Bounds;
        Box? inWindow = TopLevel.GetTopLevel(visual) is { } top && visual.TranslatePoint(default, top) is { } at
            ? new Box(at.X, at.Y, bounds.Width, bounds.Height)
            : null;
        Layoutable? laid = visual as Layoutable;
        return new LayoutInfo(
            new Box(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            inWindow,
            laid?.DesiredSize.Width ?? 0,
            laid?.DesiredSize.Height ?? 0,
            Edges(laid?.Margin ?? default),
            Named(visual, "BorderThickness") is Thickness border ? Edges(border) : null,
            Named(visual, "Padding") is Thickness padding ? Edges(padding) : null,
            laid?.HorizontalAlignment.ToString() ?? nameof(HorizontalAlignment.Stretch),
            laid?.VerticalAlignment.ToString() ?? nameof(VerticalAlignment.Stretch));
    }

    private static Edges Edges(Thickness t) => new(t.Left, t.Top, t.Right, t.Bottom);

    /// <summary>A property's value by its name, when the visual's type has one by that name.</summary>
    private static object? Named(Visual visual, string name) =>
        AvaloniaPropertyRegistry.Instance.FindRegistered(visual, name) is { } property ? visual.GetValue(property) : null;

    private IReadOnlyList<PropertyValue> Properties(Visual visual, Styling styling)
    {
        List<PropertyValue> rows = [];
        HashSet<AvaloniaProperty> listed = [];
        // Its type's own, and those it took from another (Foreground from TextElement) under their own names.
        foreach (AvaloniaProperty property in AvaloniaPropertyRegistry.Instance.GetRegistered(visual.GetType()))
        {
            if (listed.Add(property))
            {
                rows.Add(Row(visual, property, attached: false, styling));
            }
        }
        foreach (AvaloniaProperty property in AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(visual.GetType()))
        {
            // XamlSourceInfo is Avalonia's record of where the XAML is, which the window shows as the element's source.
            if (property.OwnerType == typeof(XamlSourceInfo) || !listed.Add(property))
            {
                continue;
            }
            PropertyValue row = Row(visual, property, attached: true, styling);
            if (row.Source != ValueSources.Default)
            {
                rows.Add(row);
            }
        }
        rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    private static PropertyValue Row(Visual visual, AvaloniaProperty property, bool attached, Styling styling)
    {
        string name = attached ? $"{Words.TypeName(property.OwnerType)}.{property.Name}" : property.Name;
        string owner = Words.TypeName(property.OwnerType);
        string type = Words.TypeName(property.PropertyType);
        if (property.IsDirect)
        {
            object? direct = visual.GetValue(property);
            string shown = Shown.Format(visual, name, direct);
            if (styling.Winner(property) is { } styled)
            {
                return new PropertyValue(name, owner, attached, type, shown, styled.Source, styled.Detail, styled.Place);
            }
            bool initial = property.GetMetadata(visual.GetType()) is IDirectPropertyMetadata { UnsetValue: var unset } && Equals(direct, unset);
            return new PropertyValue(name, owner, attached, type, shown, initial ? ValueSources.Default : ValueSources.Direct,
                initial ? null : Bound(visual, property, BindingPriority.LocalValue), null);
        }
        AvaloniaPropertyValue diagnostic = visual.GetDiagnostic(property);
        string value = Shown.Format(visual, name, diagnostic.Value);
        if (diagnostic.IsOverriddenCurrentValue)
        {
            return new PropertyValue(name, owner, attached, type, value, ValueSources.Current, "Set by the control itself (SetCurrentValue)", null);
        }
        switch (diagnostic.Priority)
        {
            case BindingPriority.LocalValue:
                return new PropertyValue(name, owner, attached, type, value, ValueSources.Local, Bound(visual, property, diagnostic.Priority), null);
            case BindingPriority.Animation:
                return new PropertyValue(name, owner, attached, type, value, ValueSources.Animation, null, null);
            case BindingPriority.Inherited:
                (string? from, SourcePlace? at) = InheritedFrom(visual, property);
                return new PropertyValue(name, owner, attached, type, value, ValueSources.Inherited, from, at);
            case BindingPriority.Template:
                string? made = visual is StyledElement { TemplatedParent: Visual parent } ? $"{Describe(parent)}'s template" : null;
                string? binding = Bound(visual, property, diagnostic.Priority);
                return new PropertyValue(name, owner, attached, type, value, ValueSources.Template,
                    binding is null ? made : made is null ? binding : $"{binding} in {made}", null);
            case BindingPriority.Style or BindingPriority.StyleTrigger when styling.Winner(property) is { } won:
                return new PropertyValue(name, owner, attached, type, value, won.Source, won.Detail, won.Place);
            case BindingPriority.Style or BindingPriority.StyleTrigger:
                return new PropertyValue(name, owner, attached, type, value, ValueSources.Style, null, null);
            default:
                return new PropertyValue(name, owner, attached, type, value, ValueSources.Default, null, null);
        }
    }

    /// <summary>The binding that gives a property its value at the priority it has, as its XAML would write it, if one does.</summary>
    private static string? Bound(Visual visual, AvaloniaProperty property, BindingPriority priority)
    {
        try
        {
            return BindingOperations.GetBindingExpressionBase(visual, property) is UntypedBindingExpressionBase expression && expression.Priority == priority
                ? expression.Description
                : null;
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The ancestor an inherited value comes from (the nearest that has it other than by inheritance), and its XAML.</summary>
    private static (string?, SourcePlace?) InheritedFrom(Visual visual, AvaloniaProperty property)
    {
        for (StyledElement? at = (visual as IStyleHost)?.StylingParent as StyledElement; at is not null; at = (at as IStyleHost)?.StylingParent as StyledElement)
        {
            AvaloniaPropertyValue there = at.GetDiagnostic(property);
            if (there.Priority is not (BindingPriority.Inherited or BindingPriority.Unset))
            {
                string how = there.Priority switch
                {
                    BindingPriority.LocalValue => "its local value",
                    BindingPriority.Style or BindingPriority.StyleTrigger => "a style",
                    BindingPriority.Template => "its template",
                    BindingPriority.Animation => "an animation",
                    _ => "a value",
                };
                return ($"From {(at is Visual ancestor ? Describe(ancestor) : Words.TypeName(at.GetType()))} ({how})", Place(at));
            }
        }
        return (null, null);
    }

    /// <summary>A value as the window shows it: text quoted, a brush by its colour.</summary>
    public static string Format(object? value) => Words.Value(value, Framed);

    /// <summary>Avalonia's own words for a value, or null for those it has none of.</summary>
    private static string? Framed(object value) => value switch
    {
        ISolidColorBrush brush => brush.Opacity < 1 ? $"{brush.Color} ({brush.Opacity.ToString("0.##", CultureInfo.InvariantCulture)})" : brush.Color.ToString(),
        Thickness t => t.ToString(),
        _ when value == AvaloniaProperty.UnsetValue => "(unset)",
        _ => null,
    };
}
