using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace Passenger.App;

/// <summary>Theme files as the application's resources: the tokens the default theme (Tokens.axaml) gives and the kind of
/// value each takes, read from it, so a token added there is one a theme may give; and a theme file's tokens as a resource
/// dictionary to lay over it, the tokens for both variants at its top and each variant's in its theme dictionary, made as
/// XAML makes the same values. UI thread only.</summary>
public static class ThemeResources
{
    private const string Prefix = "Passenger.";
    private static IReadOnlyDictionary<string, ThemeTokenKind>? kinds;

    /// <summary>Every token the default theme gives, named without <c>Passenger.</c>, and the kind of value it takes.</summary>
    public static IReadOnlyDictionary<string, ThemeTokenKind> Kinds => kinds ??= Read();

    /// <summary>The theme's tokens as a dictionary of resources, a new one at each call.</summary>
    public static ResourceDictionary Dictionary(ThemeFile theme)
    {
        ResourceDictionary laid = Filled(theme.Both);
        if (theme.Dark.Count > 0)
        {
            laid.ThemeDictionaries[ThemeVariant.Dark] = Filled(theme.Dark);
        }
        if (theme.Light.Count > 0)
        {
            laid.ThemeDictionaries[ThemeVariant.Light] = Filled(theme.Light);
        }
        return laid;
    }

    private static ResourceDictionary Filled(IReadOnlyDictionary<string, TokenValue> tokens)
    {
        ResourceDictionary dictionary = [];
        foreach ((string name, TokenValue value) in tokens)
        {
            dictionary.Add(Prefix + name, Resource(Kinds[name], value));
        }
        return dictionary;
    }

    private static object Resource(ThemeTokenKind kind, TokenValue value) => (kind, value) switch
    {
        (_, ColourValue colour) => Brush(colour),
        (_, GradientValue gradient) => Brush(gradient),
        (_, ShadowValue shadow) => BoxShadows.Parse(shadow.Text),
        (ThemeTokenKind.Radius, SidesValue { Numbers: [double every] }) => new CornerRadius(every),
        (ThemeTokenKind.Radius, SidesValue { Numbers: [double topLeft, double topRight, double bottomRight, double bottomLeft] }) =>
            new CornerRadius(topLeft, topRight, bottomRight, bottomLeft),
        (ThemeTokenKind.Thickness, SidesValue { Numbers: [double every] }) => new Thickness(every),
        (ThemeTokenKind.Thickness, SidesValue { Numbers: [double left, double top, double right, double bottom] }) => new Thickness(left, top, right, bottom),
        (_, NumberValue number) => number.Number,
        (_, TextStyleValue style) => style.Words,
        _ => throw new InvalidOperationException($"A {kind} token cannot be {value}: ThemeFiles reads no such value"),
    };

    private static SolidColorBrush Brush(ColourValue colour)
    {
        SolidColorBrush brush = new() { Color = Color.FromUInt32(colour.Argb) };
        if (colour.Opacity != 1)
        {
            brush.Opacity = colour.Opacity;
        }
        return brush;
    }

    private static LinearGradientBrush Brush(GradientValue gradient)
    {
        LinearGradientBrush brush = new()
        {
            StartPoint = new RelativePoint(gradient.StartX, gradient.StartY, RelativeUnit.Relative),
            EndPoint = new RelativePoint(gradient.EndX, gradient.EndY, RelativeUnit.Relative),
        };
        foreach (GradientStopValue stop in gradient.Stops)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromUInt32(stop.Argb), stop.Offset));
        }
        return brush;
    }

    private static Dictionary<string, ThemeTokenKind> Read()
    {
        ResourceDictionary tokens = (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://Passenger/Theme/Tokens.axaml"));
        ResourceDictionary dark = (ResourceDictionary)tokens.ThemeDictionaries[ThemeVariant.Dark];
        Dictionary<string, ThemeTokenKind> read = new(StringComparer.Ordinal);
        foreach (ResourceDictionary holder in (ResourceDictionary[])[tokens, dark])
        {
            foreach (string key in holder.Keys.Cast<string>())
            {
                holder.TryGetValue(key, out object? value);
                read[key[Prefix.Length..]] = value switch
                {
                    SolidColorBrush => ThemeTokenKind.Colour,
                    LinearGradientBrush => ThemeTokenKind.Gradient,
                    BoxShadows => ThemeTokenKind.Shadow,
                    CornerRadius => ThemeTokenKind.Radius,
                    Thickness => ThemeTokenKind.Thickness,
                    double => ThemeTokenKind.Number,
                    string when key.StartsWith(Prefix + "SyntaxStyle.", StringComparison.Ordinal) => ThemeTokenKind.TextStyle,
                    _ => throw new InvalidOperationException($"Tokens.axaml gives {key} as a {value?.GetType().Name ?? "null"}, a kind of value theme files cannot give: teach ThemeFiles and ThemeResources it"),
                };
            }
        }
        return read;
    }
}
