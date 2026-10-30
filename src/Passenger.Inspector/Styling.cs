using Avalonia;
using Avalonia.Controls;
using Avalonia.Diagnostics;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace Passenger.Inspector;

/// <summary>The styles that could apply to a visual, found in the order Avalonia's own styling takes them (its control theme
/// and those it is based on with their nested styles, its templated parent's theme's nested styles, then every style host's
/// Styles from the application down to it), each set against the frames its value store holds: a style Avalonia attached
/// applies while its frame is active; one it did not attach never matched. Which setter gives each property its value is
/// the first active frame holding it in the order the store evaluates them.</summary>
internal sealed class Styling
{
    private readonly Visual visual;
    private readonly List<Candidate> candidates = [];

    /// <summary>The store's frames in the order it evaluates them: the one that wins a property first.</summary>
    private readonly List<IValueFrameDiagnostic> frames = [];

    public Styling(Visual visual)
    {
        this.visual = visual;
        if (visual is not StyledElement element)
        {
            return;
        }
        IReadOnlyList<IValueFrameDiagnostic> applied = element.GetValueStoreDiagnostic().AppliedFrames;
        for (int i = applied.Count - 1; i >= 0; i--)
        {
            if (applied[i].Type != IValueFrameDiagnostic.FrameType.Local)
            {
                frames.Add(applied[i]);
            }
        }
        if (Theme(element) is { } theme)
        {
            FromTheme(theme, StyleKinds.Theme, ThemeOrigin(element, theme));
        }
        if (element.TemplatedParent is StyledElement parent && Theme(parent) is { } parentTheme)
        {
            FromTheme(parentTheme, StyleKinds.TemplatedParentTheme, ThemeOrigin(parent, parentTheme));
        }
        List<IStyleHost> hosts = [];
        for (IStyleHost? host = element; host is not null; host = host.StylingParent)
        {
            hosts.Insert(0, host);
        }
        foreach (IStyleHost host in hosts)
        {
            if (host.IsStylesInitialized)
            {
                string origin = host is Application ? "Application" : host is Visual shown ? Details.Describe(shown) : Words.TypeName(host.GetType());
                foreach (IStyle style in host.Styles)
                {
                    FromStyles(style, $"{origin}.Styles", host);
                }
            }
        }
    }

    /// <summary>A style that could apply, what kind it is, where it comes from, and the host whose Styles hold it.</summary>
    private sealed record Candidate(StyleBase Style, string Kind, string Origin, IStyleHost? Host);

    /// <summary>A control's control theme: its Theme, else the one its style key finds among its resources, as Avalonia's.</summary>
    private static ControlTheme? Theme(StyledElement element) =>
        element.Theme ?? (element.TryFindResource(element.StyleKey, element.ActualThemeVariant, out object? found) ? found as ControlTheme : null);

    private void FromTheme(ControlTheme theme, string kind, string origin)
    {
        if (theme.BasedOn is { } based)
        {
            FromTheme(based, kind, origin);
        }
        // The theme's own setters are for the control it styles, never for its template's parts.
        if (kind == StyleKinds.Theme)
        {
            candidates.Add(new Candidate(theme, StyleKinds.Theme, origin, null));
        }
        foreach (IStyle child in theme.Children)
        {
            Nested(child, kind == StyleKinds.Theme ? StyleKinds.ThemeStyle : kind, origin, null);
        }
    }

    private void FromStyles(IStyle style, string origin, IStyleHost host)
    {
        switch (style)
        {
            case StyleInclude include:
                origin = include.Source?.ToString() ?? origin;
                break;
            case Styles styles when styles.GetType() != typeof(Styles):
                origin = Words.TypeName(styles.GetType());
                break;
        }
        Nested(style, StyleKinds.Style, origin, host);
    }

    private void Nested(IStyle style, string kind, string origin, IStyleHost? host)
    {
        if (style is StyleBase { Setters.Count: > 0 } setting)
        {
            candidates.Add(new Candidate(setting, kind, origin, host));
        }
        foreach (IStyle child in style.Children)
        {
            Nested(child, kind, origin, host);
        }
    }

    /// <summary>Where a control theme is defined: the control's Theme, a theme among the application's styles (FluentTheme),
    /// or a resource dictionary, by its file when Avalonia recorded one.</summary>
    private static string ThemeOrigin(StyledElement element, ControlTheme theme)
    {
        if (element.Theme == theme)
        {
            return Details.Place(theme) is { } place ? $"its Theme, {Path.GetFileName(place.File)}" : "its Theme";
        }
        if (Application.Current is { } app)
        {
            foreach (IStyle style in app.Styles)
            {
                if (style is IResourceProvider provider && provider.TryGetResource(element.StyleKey, element.ActualThemeVariant, out object? found) && found == theme)
                {
                    return style is StyleInclude { Source: { } source } ? source.ToString() : Words.TypeName(style.GetType());
                }
            }
        }
        return Details.Place(theme) is { } defined ? Path.GetFileName(defined.File) : "resources";
    }

    /// <summary>Whether a style is one of a control theme's own, nested in it.</summary>
    private static bool InTheme(StyleBase style)
    {
        for (IStyle? at = style; at is StyleBase based; at = based.Parent)
        {
            if (at is ControlTheme)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A style's selector as Avalonia writes it, a nested one's with its parent's in place of the ^; a control
    /// theme by the type it styles.</summary>
    public static string SelectorText(StyleBase style) => style switch
    {
        ControlTheme theme => theme.TargetType is { } target ? Words.TypeName(target) : "ControlTheme",
        Style { Selector: { } selector } nested => Expanded(selector.ToString(nested), nested.Parent),
        _ => style.ToString() ?? "Style",
    };

    /// <summary>A selector Avalonia wrote with ^ (its own text kept from before it knew its parent), with the parent's.</summary>
    private static string Expanded(string text, IStyle? parent) =>
        !text.Contains('^', StringComparison.Ordinal) || parent is not StyleBase owner ? text : text.Replace("^", SelectorText(owner), StringComparison.Ordinal);

    /// <summary>The frame a style gave the visual, if Avalonia attached it.</summary>
    private IValueFrameDiagnostic? FrameOf(StyleBase style) => frames.FirstOrDefault(f => ReferenceEquals(f.Source, style));

    /// <summary>The active frame that gives a property its value, first in the store's order.</summary>
    private IValueFrameDiagnostic? WinningFrame(AvaloniaProperty property) =>
        frames.FirstOrDefault(f => f.IsActive && f.Values.Any(v => v.Property == property));

    /// <summary>The style that gives a property its value: whether it is a theme's, its selector and origin, and where its
    /// setter (else its selector) is written; null when no style does.</summary>
    public (string Source, string? Detail, SourcePlace? Place)? Winner(AvaloniaProperty property)
    {
        if (WinningFrame(property)?.Source is not StyleBase style)
        {
            return null;
        }
        Candidate? known = candidates.FirstOrDefault(c => c.Style == style);
        string origin = known?.Origin ?? string.Empty;
        return (style is ControlTheme || InTheme(style) ? ValueSources.Theme : ValueSources.Style,
            origin.Length > 0 ? $"{SelectorText(style)} ({origin})" : SelectorText(style),
            SetterPlace(style, property) ?? Details.Place(style));
    }

    private static SourcePlace? SetterPlace(StyleBase style, AvaloniaProperty property) =>
        style.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == property) is { } setter ? Details.Place(setter) : null;

    /// <summary>The styles that apply and those that do not but could, the last Avalonia takes first.</summary>
    public IReadOnlyList<StyleReport> Reports()
    {
        List<StyleReport> reports = [];
        if (visual is not StyledElement element)
        {
            return reports;
        }
        foreach (Candidate candidate in Enumerable.Reverse(candidates))
        {
            IValueFrameDiagnostic? frame = FrameOf(candidate.Style);
            bool applies = frame?.IsActive == true;
            string? why = null;
            if (!applies)
            {
                Explained explained = candidate.Style switch
                {
                    ControlTheme theme => theme.TargetType?.IsAssignableFrom(element.StyleKey) == true
                        ? new Explained(null, true)
                        : new Explained($"it is a {Words.TypeName(element.StyleKey)}, not a {Words.TypeName(theme.TargetType!)}", false),
                    Style style => Selectors.Explain(style, element, candidate.Host),
                    _ => new Explained(null, true),
                };
                if (frame is null && !explained.Relevant)
                {
                    // Another kind of control's style, or another part's: nothing it could ever say of this one.
                    continue;
                }
                why = explained.Why ?? (frame is null
                    ? "Avalonia did not apply it when it styled this control (the style, its name or its place came after)"
                    : "its selector does not match now");
            }
            reports.Add(new StyleReport(SelectorText(candidate.Style), candidate.Kind, candidate.Origin, Details.Place(candidate.Style), applies, why,
                [.. candidate.Style.Setters.OfType<Setter>().Where(s => s.Property is not null).Select(s => Setter(s, candidate.Style, applies, element))]));
        }
        return reports;
    }

    private SetterReport Setter(Setter setter, StyleBase style, bool applies, StyledElement element)
    {
        AvaloniaProperty property = setter.Property!;
        // Foreground, not TextElement.Foreground, on a control that took TextElement's for its own.
        string name = property.IsAttached && !AvaloniaPropertyRegistry.Instance.GetRegistered(element.GetType()).Contains(property)
            ? $"{Words.TypeName(property.OwnerType)}.{property.Name}"
            : property.Name;
        string value = SetterValue(setter.Value);
        SourcePlace? place = Details.Place(setter);
        if (!applies)
        {
            return new SetterReport(name, value, place, SetterStates.Inactive, null);
        }
        if (!property.IsDirect)
        {
            AvaloniaPropertyValue effective = element.GetDiagnostic(property);
            string source = effective.IsOverriddenCurrentValue ? ValueSources.Current : Details.SourceOf(effective.Priority);
            if (source is ValueSources.Current or ValueSources.Local or ValueSources.Animation)
            {
                return new SetterReport(name, value, place, SetterStates.Overridden, Words.Giver(source));
            }
        }
        IValueFrameDiagnostic? winner = WinningFrame(property);
        if (winner is null || ReferenceEquals(winner.Source, style))
        {
            return new SetterReport(name, value, place, SetterStates.Wins, null);
        }
        StyleBase? other = winner.Source as StyleBase;
        string by = other is not null ? SelectorText(other)
            : winner.Type == IValueFrameDiagnostic.FrameType.Template ? Words.Giver(ValueSources.Template) : "another frame";
        return new SetterReport(name, value, place, SetterStates.Overridden, by) { ByPlace = other is null ? null : Details.Place(other) };
    }

    /// <summary>A setter's value as its XAML wrote it: a resource or binding as markup, else the value itself.</summary>
    private static string SetterValue(object? value) => value switch
    {
        DynamicResourceExtension dynamic => $"{{DynamicResource {dynamic.ResourceKey}}}",
        StaticResourceExtension resource => $"{{StaticResource {resource.ResourceKey}}}",
        ITemplate => "(template)",
        _ => Details.Format(value),
    };
}
