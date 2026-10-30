using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Passenger.Inspector.Wpf;

/// <summary>One element looked at closely: its XAML, its layout, each dependency property with its value and where the value
/// came from, as WPF's own DependencyPropertyHelper.GetValueSource says (local, a style, its default style, a template, a
/// trigger, inheritance, the default), with the style's or template's XAML where WPF recorded it, and the styles and triggers
/// that could set its properties (<see cref="Styling"/>).</summary>
internal sealed class Details(Inspector inspector)
{
    public ElementDetails Of(DependencyObject element)
    {
        Type type = element.GetType();
        (SourcePlace? source, string? noSource) = SourceOf(inspector, element);
        Styling styling = new(element);
        FrameworkElement? framed = element as FrameworkElement;
        return new ElementDetails(
            inspector.IdOf(element),
            Words.TypeName(type),
            type.FullName ?? type.Name,
            framed?.Name is { Length: > 0 } name ? name : null,
            [],
            States(element),
            source,
            noSource,
            framed?.TemplatedParent is { } parent ? Describe(parent) : null,
            Layout(element),
            Properties(element, styling),
            styling.Reports());
    }

    /// <summary>Where an element's XAML is: its own element, or for a root made by its class (a Window, a UserControl) whose
    /// own place WPF did not record, the file its content came from, at its root (line 0); else why there is none.</summary>
    private static (SourcePlace?, string?) SourceOf(Inspector inspector, DependencyObject element)
    {
        if (Sources.Place(element) is { } own)
        {
            return (own, null);
        }
        if (element is FrameworkElement { TemplatedParent: { } parent })
        {
            return (null, $"Made by {Words.TypeName(parent.GetType())}'s template, which was compiled without line information (a theme's, or a Release build's)");
        }
        Queue<DependencyObject> next = new(inspector.Children(element));
        for (int seen = 0; next.Count > 0 && seen < 64; seen++)
        {
            DependencyObject child = next.Dequeue();
            if (child is FrameworkElement { TemplatedParent: null } && Sources.Place(child) is { } inside)
            {
                return (inside with { Line = 0, Column = 0 }, null);
            }
            foreach (DependencyObject below in inspector.Children(child))
            {
                next.Enqueue(below);
            }
        }
        return (null, "Made in code, or by XAML compiled without line information (a Release build, or XamlDebuggingInformation off)");
    }

    /// <summary>What WPF's states say of an element as Avalonia's pseudo-classes would: :pointerover, :pressed, :focus, :disabled, ...</summary>
    private static List<string> States(DependencyObject element)
    {
        List<string> states = [];
        if (element is UIElement ui)
        {
            Add(ui.IsMouseOver, ":pointerover");
            Add(ui.IsKeyboardFocused, ":focus");
            Add(ui.IsKeyboardFocusWithin && !ui.IsKeyboardFocused, ":focus-within");
            Add(!ui.IsEnabled, ":disabled");
        }
        Add(element is System.Windows.Controls.Primitives.ButtonBase { IsPressed: true }, ":pressed");
        Add(element is System.Windows.Controls.Primitives.ToggleButton { IsChecked: true }, ":checked");
        Add(element is ListBoxItem { IsSelected: true } or TabItem { IsSelected: true } or TreeViewItem { IsSelected: true }, ":selected");
        return states;

        void Add(bool on, string state)
        {
            if (on)
            {
                states.Add(state);
            }
        }
    }

    /// <summary>Where WPF recorded an object was made in XAML, or null.</summary>
    public static SourcePlace? Place(object made) => Sources.Place(made);

    /// <summary>An element as a sentence names it: its type, then #name.</summary>
    public static string Describe(DependencyObject element) => Words.Describe(element.GetType(), (element as FrameworkElement)?.Name);

    private static LayoutInfo Layout(DependencyObject element)
    {
        Visual? visual = element as Visual;
        Size size = visual is null ? default : Shown.SizeOf(visual);
        Vector offset = visual is null ? default : VisualTreeHelper.GetOffset(visual);
        Box? inWindow = Inspector.TopOf(element) is { } top && Shown.RectIn(element, top) is { } at ? new Box(at.X, at.Y, at.Width, at.Height) : null;
        FrameworkElement? framed = element as FrameworkElement;
        Size desired = (element as UIElement)?.DesiredSize ?? default;
        return new LayoutInfo(
            new Box(offset.X, offset.Y, size.Width, size.Height),
            inWindow,
            desired.Width,
            desired.Height,
            Edges(framed?.Margin ?? default),
            element switch
            {
                Border border => Edges(border.BorderThickness),
                Control control => Edges(control.BorderThickness),
                _ => null,
            },
            element switch
            {
                Border border => Edges(border.Padding),
                Control control => Edges(control.Padding),
                TextBlock text => Edges(text.Padding),
                _ => null,
            },
            framed?.HorizontalAlignment.ToString() ?? nameof(HorizontalAlignment.Stretch),
            framed?.VerticalAlignment.ToString() ?? nameof(VerticalAlignment.Stretch));
    }

    private static Edges Edges(Thickness t) => new(t.Left, t.Top, t.Right, t.Bottom);

    private static IReadOnlyList<PropertyValue> Properties(DependencyObject element, Styling styling)
    {
        List<PropertyValue> rows = [];
        foreach ((DependencyProperty property, bool attached) in Wpf.Properties.Of(element))
        {
            rows.Add(Row(element, property, attached, styling));
        }
        rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    /// <summary>A property's row: its value, and its source in the window's words with what WPF said ("Style trigger",
    /// "ParentTemplate") and the XAML that set it, when WPF recorded where.</summary>
    private static PropertyValue Row(DependencyObject element, DependencyProperty property, bool attached, Styling styling)
    {
        string name = Wpf.Properties.NameOf(property, attached);
        string owner = Words.TypeName(property.OwnerType);
        string type = Words.TypeName(property.PropertyType);
        string value;
        ValueSource source;
        try
        {
            value = Format(element.GetValue(property));
            source = DependencyPropertyHelper.GetValueSource(element, property);
        }
        catch (Exception error)
        {
            // A property whose getter fails here (some read their element's state) is shown with why, not the row's end.
            InspectorTrace.Step($"{owner}.{name} could not be read: {error}");
            return new PropertyValue(name, owner, attached, type, $"({error.GetType().Name}: {error.Message})", ValueSources.Default, null, null);
        }
        if (source.IsAnimated)
        {
            return new PropertyValue(name, owner, attached, type, value, ValueSources.Animation, null, null);
        }
        if (source.IsCurrent)
        {
            return new PropertyValue(name, owner, attached, type, value, ValueSources.Current, Words.SetItself, null);
        }
        string? coerced = source.IsCoerced ? "coerced" : null;
        switch (source.BaseValueSource)
        {
            case BaseValueSource.Local:
                return new PropertyValue(name, owner, attached, type, value, ValueSources.Local, Join(source.IsExpression ? Bound(element, property) : null, coerced), null);
            case BaseValueSource.Inherited:
                (string? from, SourcePlace? at) = InheritedFrom(element, property);
                return new PropertyValue(name, owner, attached, type, value, ValueSources.Inherited, from, at);
            case BaseValueSource.Style or BaseValueSource.StyleTrigger or BaseValueSource.ImplicitStyleReference
                or BaseValueSource.DefaultStyle or BaseValueSource.DefaultStyleTrigger
                or BaseValueSource.TemplateTrigger or BaseValueSource.ParentTemplate or BaseValueSource.ParentTemplateTrigger:
                Styling.Won? won = styling.Winner(property, source.BaseValueSource);
                return new PropertyValue(name, owner, attached, type, value, SourceOf(source.BaseValueSource),
                    Join(won?.Detail ?? Said(element, source.BaseValueSource), source.IsExpression ? Bound(element, property) : null, coerced), won?.Place);
            case BaseValueSource.Default:
                return new PropertyValue(name, owner, attached, type, value, ValueSources.Default, coerced, null);
            default:
                return new PropertyValue(name, owner, attached, type, value, ValueSources.Default, source.BaseValueSource.ToString(), null);
        }
    }

    /// <summary>The source (<see cref="ValueSources"/>) WPF says a value has, as the inspector's shared words name it
    /// (<see cref="Words.Giver"/>): an animation's or SetCurrentValue's over whatever gave its base value.</summary>
    public static string SourceOf(ValueSource source) => source switch
    {
        { IsAnimated: true } => ValueSources.Animation,
        { IsCurrent: true } => ValueSources.Current,
        _ => SourceOf(source.BaseValueSource),
    };

    /// <summary>The source (<see cref="ValueSources"/>) a WPF base value source gives a value.</summary>
    public static string SourceOf(BaseValueSource source) => source switch
    {
        BaseValueSource.Local => ValueSources.Local,
        BaseValueSource.Style or BaseValueSource.StyleTrigger or BaseValueSource.ImplicitStyleReference => ValueSources.Style,
        BaseValueSource.DefaultStyle or BaseValueSource.DefaultStyleTrigger => ValueSources.Theme,
        BaseValueSource.TemplateTrigger or BaseValueSource.ParentTemplate or BaseValueSource.ParentTemplateTrigger => ValueSources.Template,
        BaseValueSource.Inherited => ValueSources.Inherited,
        _ => ValueSources.Default,
    };

    /// <summary>A source WPF names with no setter to point at, in the window's words.</summary>
    public static string Said(DependencyObject element, BaseValueSource source)
    {
        string? parent = element is FrameworkElement { TemplatedParent: { } templated } ? Describe(templated) : null;
        return source switch
        {
            BaseValueSource.Style => "its Style",
            BaseValueSource.StyleTrigger => "a trigger of its Style",
            BaseValueSource.ImplicitStyleReference => $"the implicit Style for {Words.TypeName(element.GetType())}",
            BaseValueSource.DefaultStyle => $"{Words.TypeName(element.GetType())}'s default style (the theme)",
            BaseValueSource.DefaultStyleTrigger => $"a trigger of {Words.TypeName(element.GetType())}'s default style (the theme)",
            BaseValueSource.TemplateTrigger => "a trigger of its template",
            BaseValueSource.ParentTemplate => parent is null ? "its parent's template" : $"{parent}'s template",
            BaseValueSource.ParentTemplateTrigger => parent is null ? "a trigger of its parent's template" : $"a trigger of {parent}'s template",
            _ => source.ToString(),
        };
    }

    private static string? Join(params string?[] parts) => parts.Where(p => p is { Length: > 0 }).ToList() is { Count: > 0 } said ? string.Join(", ", said) : null;

    /// <summary>The binding that gives a property its value, as its XAML would write it, if one does.</summary>
    public static string? Bound(DependencyObject element, DependencyProperty property)
    {
        BindingExpressionBase? expression = BindingOperations.GetBindingExpressionBase(element, property);
        return expression?.ParentBindingBase switch
        {
            Binding binding => Written(binding),
            MultiBinding multi => $"{{MultiBinding {string.Join(", ", multi.Bindings.OfType<Binding>().Select(Written))}}}",
            PriorityBinding => "{PriorityBinding}",
            _ => null,
        };
    }

    /// <summary>A binding as its XAML writes it: {Binding Path, ElementName=..., RelativeSource=..., Mode=...}.</summary>
    public static string Written(Binding binding)
    {
        List<string> parts = [];
        if (binding.Path?.Path is { Length: > 0 } path)
        {
            parts.Add(path);
        }
        if (binding.XPath is { Length: > 0 } xpath)
        {
            parts.Add($"XPath={xpath}");
        }
        if (binding.ElementName is { Length: > 0 } named)
        {
            parts.Add($"ElementName={named}");
        }
        if (binding.RelativeSource is { } relative)
        {
            parts.Add(relative.Mode switch
            {
                RelativeSourceMode.TemplatedParent => "RelativeSource={RelativeSource TemplatedParent}",
                RelativeSourceMode.Self => "RelativeSource={RelativeSource Self}",
                RelativeSourceMode.FindAncestor => $"RelativeSource={{RelativeSource AncestorType={Words.TypeName(relative.AncestorType ?? typeof(object))}}}",
                _ => $"RelativeSource={{RelativeSource {relative.Mode}}}",
            });
        }
        if (binding.Source is { } source)
        {
            parts.Add($"Source={Words.TypeName(source.GetType())}");
        }
        if (binding.Mode is not BindingMode.Default)
        {
            parts.Add($"Mode={binding.Mode}");
        }
        return parts.Count == 0 ? "{Binding}" : $"{{Binding {string.Join(", ", parts)}}}";
    }

    /// <summary>The ancestor an inherited value comes from (the nearest that has it other than by inheritance), and its XAML.</summary>
    private static (string?, SourcePlace?) InheritedFrom(DependencyObject element, DependencyProperty property)
    {
        for (DependencyObject? at = InheritanceParent(element); at is not null; at = InheritanceParent(at))
        {
            BaseValueSource there;
            try
            {
                there = DependencyPropertyHelper.GetValueSource(at, property).BaseValueSource;
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (there is not (BaseValueSource.Inherited or BaseValueSource.Default or BaseValueSource.Unknown))
            {
                return (Words.InheritedFrom(Describe(at), SourceOf(there)), Sources.Place(at));
            }
        }
        return (null, null);
    }

    /// <summary>Where WPF inherits from: the logical parent, else the visual one.</summary>
    private static DependencyObject? InheritanceParent(DependencyObject element) =>
        LogicalTreeHelper.GetParent(element) ?? (element is Visual ? VisualTreeHelper.GetParent(element) : null);

    /// <summary>A value as the window shows it: text quoted, a brush by its colour, a binding as XAML writes it.</summary>
    public static string Format(object? value) => Words.Value(value, Framed);

    /// <summary>WPF's own words for a value, or null for those it has none of.</summary>
    private static string? Framed(object value) => value switch
    {
        SolidColorBrush brush => brush.Opacity < 1 ? $"{brush.Color} ({brush.Opacity.ToString("0.##", CultureInfo.InvariantCulture)})" : brush.Color.ToString(CultureInfo.InvariantCulture),
        Thickness t => string.Create(CultureInfo.InvariantCulture, $"{t.Left},{t.Top},{t.Right},{t.Bottom}"),
        _ when value == DependencyProperty.UnsetValue => "(unset)",
        BindingExpressionBase { ParentBindingBase: Binding binding } => Written(binding),
        Binding binding => Written(binding),
        DynamicResourceExtension dynamic => $"{{DynamicResource {dynamic.ResourceKey}}}",
        StaticResourceExtension resource => $"{{StaticResource {resource.ResourceKey}}}",
        // A setter's {DynamicResource} is WPF's own expression by the time the style is sealed.
        _ when value.GetType().Name == "ResourceReferenceExpression"
            && value.GetType().GetProperty("ResourceKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(value) is { } key
            => $"{{DynamicResource {key}}}",
        _ => null,
    };
}
