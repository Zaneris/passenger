using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace Passenger.App;

/// <summary>An icon look as Avalonia draws it: read from a control's resources, and laid on its colours and brushes.</summary>
public readonly partial record struct IconLook
{
    private static readonly string SaturationKey = ThemeResources.KeyOf(SaturationToken);
    private static readonly string BrightnessKey = ThemeResources.KeyOf(BrightnessToken);
    private static readonly string TintKey = ThemeResources.KeyOf(TintToken);

    /// <summary>Each brush drawn with a look, the last look and colour it was drawn with and what that made, so a row drawn
    /// again makes no brush.</summary>
    private static readonly ConditionalWeakTable<ISolidColorBrush, Adjusted> Made = [];

    /// <summary>The look a control's icons take in a variant, as the resources it finds give it.</summary>
    public static IconLook Of(IResourceHost host, ThemeVariant? variant)
    {
        double saturation = host.TryFindResource(SaturationKey, variant, out object? given) && given is double s ? s : 1;
        double brightness = host.TryFindResource(BrightnessKey, variant, out given) && given is double b ? b : 1;
        uint tint = host.TryFindResource(TintKey, variant, out given) && given is ISolidColorBrush wash
            ? ((uint)Math.Round(wash.Color.A * Math.Clamp(wash.Opacity, 0, 1)) << 24) | (wash.Color.ToUInt32() & 0xFFFFFF) : 0;
        return new IconLook(saturation, brightness, tint);
    }

    /// <summary>A colour as the look draws it.</summary>
    public Color Apply(Color colour) => Changes ? Color.FromUInt32(Apply(colour.ToUInt32())) : colour;

    /// <summary>A brush as the look draws it: a plain colour's own brush made once for each colour and look, any other brush
    /// as it is.</summary>
    public IBrush? Apply(IBrush? brush)
    {
        if (!Changes || brush is not ISolidColorBrush solid)
        {
            return brush;
        }
        if (Made.TryGetValue(solid, out Adjusted? known) && known.Look == this && known.Colour == solid.Color && known.Opacity == solid.Opacity)
        {
            return known.Brush;
        }
        ImmutableSolidColorBrush drawn = new(Apply(solid.Color), solid.Opacity);
        Made.AddOrUpdate(solid, new Adjusted(this, solid.Color, solid.Opacity, drawn));
        return drawn;
    }

    private sealed record Adjusted(IconLook Look, Color Colour, double Opacity, IBrush Brush);
}
