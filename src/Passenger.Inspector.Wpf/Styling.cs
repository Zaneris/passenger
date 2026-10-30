using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Passenger.Inspector.Wpf;

/// <summary>The styles and triggers that could set an element's properties, as WPF applies them: its Style (set on it, or the
/// implicit one for its type) and those it is BasedOn, their triggers, the triggers of its own template and of the template
/// that made it (those naming it), and its default style from the theme with its triggers. A trigger says whether its
/// conditions hold now and, when not, which does not; a setter whether its value is the property's, or what overrides it.</summary>
internal sealed class Styling
{
    /// <summary>FrameworkElement's own default style from the theme, which WPF keeps internal.</summary>
    private static readonly PropertyInfo? ThemeStyleProperty = typeof(FrameworkElement).GetProperty("ThemeStyle", BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly DependencyObject element;
    private readonly List<Style> styles;
    private readonly List<Style> theme;

    public Styling(DependencyObject element)
    {
        this.element = element;
        styles = Chain((element as FrameworkElement)?.Style);
        theme = Chain(element is FrameworkElement ? ThemeStyleProperty?.GetValue(element) as Style : null);
    }

    /// <summary>What gave a property its value from a style or a template, for its row: in words, and the setter's XAML.</summary>
    public sealed record Won(string Detail, SourcePlace? Place);

    /// <summary>A style, BasedOn after it, until a style is seen twice.</summary>
    private static List<Style> Chain(Style? style)
    {
        List<Style> chain = [];
        for (Style? at = style; at is not null && !chain.Contains(at) && chain.Count < 16; at = at.BasedOn)
        {
            chain.Add(at);
        }
        return chain;
    }

    /// <summary>The setter WPF took a property's value from, by the source it says the value has.</summary>
    public Won? Winner(DependencyProperty property, BaseValueSource source)
    {
        switch (source)
        {
            case BaseValueSource.Style or BaseValueSource.ImplicitStyleReference:
                return FirstSetter(styles, property) is { } styled ? new Won($"{Selector(styled.Style)}{Origin(0)}", Details.Place(styled.Setter)) : null;
            case BaseValueSource.StyleTrigger:
                return LastActive(styles, property) is { } triggered ? new Won($"{Condition(triggered.Trigger)} of its Style", Details.Place(triggered.Setter)) : null;
            case BaseValueSource.DefaultStyle:
                return FirstSetter(theme, property) is { } themed
                    ? new Won($"{Words.TypeName(element.GetType())}'s default style (the theme)", Details.Place(themed.Setter))
                    : null;
            case BaseValueSource.DefaultStyleTrigger:
                return LastActive(theme, property) is { } themeTriggered
                    ? new Won($"{Condition(themeTriggered.Trigger)} of {Words.TypeName(element.GetType())}'s default style (the theme)", Details.Place(themeTriggered.Setter))
                    : null;
            case BaseValueSource.TemplateTrigger when element is Control { Template: { } own }:
                return LastActive(own.Triggers, property, null, element) is { } ownTriggered
                    ? new Won($"{Condition(ownTriggered.Trigger)} of its template", Details.Place(ownTriggered.Setter))
                    : null;
            case BaseValueSource.ParentTemplateTrigger when element is FrameworkElement { TemplatedParent: Control { Template: { } made } parent, Name: { Length: > 0 } name }:
                return LastActive(made.Triggers, property, name, parent) is { } partTriggered
                    ? new Won($"{Condition(partTriggered.Trigger)} of {Details.Describe(parent)}'s template", Details.Place(partTriggered.Setter))
                    : null;
            case BaseValueSource.ParentTemplate when element is FrameworkElement { TemplatedParent: { } maker }:
                // Set on this element where the template writes it, which is this element's own place in that XAML.
                return new Won($"{Details.Describe(maker)}'s template", Details.Place(element));
            default:
                return null;
        }
    }

    /// <summary>"from App.xaml" after a style's selector, when WPF recorded where the style is.</summary>
    private string Origin(int index) => index < styles.Count && Details.Place(styles[index]) is { } at ? $" ({Path.GetFileName(at.File)})" : string.Empty;

    private static (Style Style, Setter Setter)? FirstSetter(List<Style> chain, DependencyProperty property)
    {
        foreach (Style style in chain)
        {
            if (style.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == property && s.TargetName is null) is { } setter)
            {
                return (style, setter);
            }
        }
        return null;
    }

    /// <summary>The last trigger whose conditions hold with a setter for the property: WPF lets a later trigger win, and a
    /// style's triggers come after those of the style it is BasedOn.</summary>
    private (TriggerBase Trigger, Setter Setter)? LastActive(List<Style> chain, DependencyProperty property)
    {
        (TriggerBase, Setter)? last = null;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (LastActive(chain[i].Triggers, property, null, element) is { } found)
            {
                last = found;
            }
        }
        return last;
    }

    private static (TriggerBase Trigger, Setter Setter)? LastActive(TriggerCollection triggers, DependencyProperty property, string? target, DependencyObject on)
    {
        (TriggerBase, Setter)? last = null;
        foreach (TriggerBase trigger in triggers)
        {
            if (Setters(trigger).FirstOrDefault(s => s.Property == property && s.TargetName == target) is { } setter && WhyNot(trigger, on) is null)
            {
                last = (trigger, setter);
            }
        }
        return last;
    }

    /// <summary>The element's styles and triggers, each with whether it applies and what became of its setters.</summary>
    public IReadOnlyList<StyleReport> Reports()
    {
        List<StyleReport> reports = [];
        bool set = element is FrameworkElement && DependencyPropertyHelper.GetValueSource(element, FrameworkElement.StyleProperty).BaseValueSource == BaseValueSource.Local;
        for (int i = 0; i < styles.Count; i++)
        {
            string origin = i > 0 ? $"BasedOn by {Selector(styles[i - 1])}" : set ? "Set on it" : $"Implicit, for {Words.TypeName(element.GetType())}";
            reports.Add(StyleReport(styles[i], StyleKinds.Style, origin, BaseValueSource.Style));
        }
        for (int i = styles.Count - 1; i >= 0; i--)
        {
            foreach (TriggerBase trigger in styles[i].Triggers)
            {
                reports.Add(TriggerReport(trigger, StyleKinds.Trigger, $"Of its Style {Selector(styles[i])}", null, element, BaseValueSource.StyleTrigger));
            }
        }
        if (element is Control { Template: { } own })
        {
            foreach (TriggerBase trigger in own.Triggers)
            {
                if (Setters(trigger).Any(s => s.TargetName is null))
                {
                    reports.Add(TriggerReport(trigger, StyleKinds.TemplateTrigger, "Of its template", null, element, BaseValueSource.TemplateTrigger));
                }
            }
        }
        if (element is FrameworkElement { TemplatedParent: Control { Template: { } made } parent, Name: { Length: > 0 } name })
        {
            foreach (TriggerBase trigger in made.Triggers)
            {
                if (Setters(trigger).Any(s => s.TargetName == name))
                {
                    reports.Add(TriggerReport(trigger, StyleKinds.TemplateTrigger, $"Of {Details.Describe(parent)}'s template, setting #{name}", name, parent, BaseValueSource.ParentTemplateTrigger));
                }
            }
        }
        for (int i = 0; i < theme.Count; i++)
        {
            reports.Add(StyleReport(theme[i], StyleKinds.Theme, i == 0 ? "The theme's default style" : $"BasedOn by {Selector(theme[i - 1])}", BaseValueSource.DefaultStyle));
        }
        for (int i = theme.Count - 1; i >= 0; i--)
        {
            foreach (TriggerBase trigger in theme[i].Triggers)
            {
                reports.Add(TriggerReport(trigger, StyleKinds.Trigger, "Of the theme's default style", null, element, BaseValueSource.DefaultStyleTrigger));
            }
        }
        return reports;
    }

    private StyleReport StyleReport(Style style, string kind, string origin, BaseValueSource from)
    {
        List<SetterReport> setters = [];
        List<Style> chain = from == BaseValueSource.DefaultStyle ? theme : styles;
        foreach (Setter setter in style.Setters.OfType<Setter>().Where(s => s.Property is not null && s.TargetName is null))
        {
            setters.Add(SetterOf(setter, from, FirstSetter(chain, setter.Property)?.Setter == setter));
        }
        return new StyleReport(Selector(style), kind, origin, Details.Place(style), true, null, setters);
    }

    private StyleReport TriggerReport(TriggerBase trigger, string kind, string origin, string? target, DependencyObject on, BaseValueSource from)
    {
        string? whyNot = WhyNot(trigger, on);
        List<SetterReport> setters = [];
        foreach (Setter setter in Setters(trigger).Where(s => s.Property is not null && s.TargetName == target))
        {
            if (whyNot is not null)
            {
                setters.Add(new SetterReport(Name(setter.Property), Details.Format(setter.Value), Details.Place(setter), SetterStates.Inactive, null));
                continue;
            }
            bool wins = from switch
            {
                BaseValueSource.StyleTrigger => LastActive(styles, setter.Property)?.Setter == setter,
                BaseValueSource.DefaultStyleTrigger => LastActive(theme, setter.Property)?.Setter == setter,
                BaseValueSource.TemplateTrigger when on is Control { Template: { } own } => LastActive(own.Triggers, setter.Property, null, on)?.Setter == setter,
                BaseValueSource.ParentTemplateTrigger when on is Control { Template: { } made } => LastActive(made.Triggers, setter.Property, target, on)?.Setter == setter,
                _ => false,
            };
            setters.Add(SetterOf(setter, from, wins));
        }
        return new StyleReport(Condition(trigger), kind, origin, Details.Place(trigger), whyNot is null, whyNot, setters);
    }

    /// <summary>A setter's row: it wins when the property's value comes from where the setter is and it is that one, else
    /// what overrides it (an animation or the control itself over any setter), in the words of the property's own row.</summary>
    private SetterReport SetterOf(Setter setter, BaseValueSource from, bool chosen)
    {
        DependencyProperty property = setter.Property;
        ValueSource now;
        try
        {
            now = DependencyPropertyHelper.GetValueSource(element, property);
        }
        catch (ArgumentException)
        {
            now = default;
        }
        if (Details.SourceOf(now) is (ValueSources.Animation or ValueSources.Current) and string over)
        {
            return new SetterReport(Name(property), Details.Format(setter.Value), Details.Place(setter), SetterStates.Overridden, Words.Giver(over));
        }
        bool wins = chosen && (now.BaseValueSource == from || (from == BaseValueSource.Style && now.BaseValueSource == BaseValueSource.ImplicitStyleReference));
        return new SetterReport(Name(property), Details.Format(setter.Value), Details.Place(setter), wins ? SetterStates.Wins : SetterStates.Overridden,
            wins ? null : By(now.BaseValueSource));
    }

    private static string By(BaseValueSource source) => source switch
    {
        BaseValueSource.Local => Words.Giver(ValueSources.Local),
        BaseValueSource.Style or BaseValueSource.ImplicitStyleReference => "another setter of its Style",
        BaseValueSource.StyleTrigger => "a trigger of its Style",
        BaseValueSource.TemplateTrigger => "a trigger of its template",
        BaseValueSource.ParentTemplate => "the template that made it",
        BaseValueSource.ParentTemplateTrigger => "a trigger of the template that made it",
        BaseValueSource.DefaultStyle => "another setter of its default style",
        BaseValueSource.DefaultStyleTrigger => "a trigger of its default style",
        BaseValueSource.Inherited => Words.Giver(ValueSources.Inherited),
        _ => "a later setter",
    };

    private static string Name(DependencyProperty property) => property.Name;

    private static IEnumerable<Setter> Setters(TriggerBase trigger) => trigger switch
    {
        Trigger t => t.Setters.OfType<Setter>(),
        MultiTrigger m => m.Setters.OfType<Setter>(),
        DataTrigger d => d.Setters.OfType<Setter>(),
        MultiDataTrigger md => md.Setters.OfType<Setter>(),
        _ => [],
    };

    /// <summary>A style as the window names it: "Style TargetType=Button".</summary>
    public static string Selector(Style style) => style.TargetType is { } type ? $"Style TargetType={Words.TypeName(type)}" : "Style";

    /// <summary>A trigger as its XAML would read: "Trigger IsMouseOver=True", "MultiTrigger IsEnabled=False, IsChecked=True",
    /// "DataTrigger {Binding State}=Done".</summary>
    public static string Condition(TriggerBase trigger) => trigger switch
    {
        Trigger t => $"Trigger {Of(t.SourceName, t.Property)}={Value(t.Value)}",
        MultiTrigger m => $"MultiTrigger {string.Join(", ", m.Conditions.Select(c => $"{Of(c.SourceName, c.Property)}={Value(c.Value)}"))}",
        DataTrigger d => $"DataTrigger {Bound(d.Binding)}={Value(d.Value)}",
        MultiDataTrigger md => $"MultiDataTrigger {string.Join(", ", md.Conditions.Select(c => $"{Bound(c.Binding)}={Value(c.Value)}"))}",
        EventTrigger e => $"EventTrigger {e.RoutedEvent?.Name}",
        _ => Words.TypeName(trigger.GetType()),
    };

    private static string Of(string? source, DependencyProperty? property) => source is { Length: > 0 } ? $"{source}.{property?.Name}" : property?.Name ?? "?";

    private static string Bound(BindingBase? binding) => binding is Binding b ? Details.Written(b) : "{Binding}";

    private static string Value(object? value) => value switch
    {
        null => "{x:Null}",
        string s => s,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "?",
    };

    /// <summary>Why a trigger does not apply to an element now (the condition that does not hold), or null when it does.
    /// A data trigger's binding is read from the setters it would make: it applies when they are the values its properties have.</summary>
    private static string? WhyNot(TriggerBase trigger, DependencyObject on)
    {
        switch (trigger)
        {
            case Trigger t:
                return Holds(on, t.SourceName, t.Property, t.Value);
            case MultiTrigger m:
                foreach (Condition condition in m.Conditions)
                {
                    if (Holds(on, condition.SourceName, condition.Property, condition.Value) is { } why)
                    {
                        return why;
                    }
                }
                return null;
            case DataTrigger or MultiDataTrigger:
                Setter[] setters = [.. Setters(trigger).Where(s => s.Property is not null && s.TargetName is null)];
                bool shown = setters.Length > 0 && setters.All(s => Same(on.GetValue(s.Property), s.Value) && DependencyPropertyHelper.GetValueSource(on, s.Property).BaseValueSource
                    is BaseValueSource.StyleTrigger or BaseValueSource.TemplateTrigger or BaseValueSource.DefaultStyleTrigger);
                return shown ? null : "Its binding's value is not the one it waits for";
            default:
                return "It applies when its event comes";
        }
    }

    /// <summary>Why a trigger's condition does not hold on an element (or the part it names), or null when it does.</summary>
    private static string? Holds(DependencyObject on, string? sourceName, DependencyProperty? property, object? wanted)
    {
        if (property is null)
        {
            return "It names no property";
        }
        DependencyObject target = on;
        if (sourceName is { Length: > 0 })
        {
            if (on is Control { Template: { } template } control && template.FindName(sourceName, control) is DependencyObject part)
            {
                target = part;
            }
            else
            {
                return $"{sourceName} is not in its template";
            }
        }
        object? value = target.GetValue(property);
        return Same(value, wanted) ? null : $"{Of(sourceName, property)} is {Value(value)}, not {Value(wanted)}";
    }

    private static bool Same(object? value, object? wanted) =>
        Equals(value, wanted) || (value is not null && wanted is string written && string.Equals(Value(value), written, StringComparison.OrdinalIgnoreCase));
}
