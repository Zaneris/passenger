using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Passenger.Core;

namespace Passenger.App;

/// <summary>The theme in force's background picture for Dark and for Light, each decoded once off the UI thread by Avalonia's
/// own decoder from the bytes kept beside the theme's file (<see cref="KeptThemes.Image"/>), never fetched here, so it draws
/// offline; every <see cref="ThemeBackdrop"/> draws the one its variant shows. UI thread only.</summary>
public static class ThemeBackdrops
{
    private static Shown? dark;
    private static Shown? light;
    private static int laid;

    /// <summary>A variant's picture as decoded, with how the theme draws it.</summary>
    public sealed record Shown(BackgroundValue Value, Bitmap Picture);

    /// <summary>A picture came or went.</summary>
    public static event EventHandler? Changed;

    /// <summary>The picture a variant shows, or null for none (or none decoded yet).</summary>
    public static Shown? Of(ThemeVariant? variant) => variant == ThemeVariant.Light ? light : dark;

    /// <summary>Whether any picture is drawn where it shows.</summary>
    public static bool Any => dark is not null || light is not null;

    /// <summary>Draws a theme's pictures, read through <paramref name="image"/> by their paths beside its file; none for null.</summary>
    public static void Use(ThemeFile? theme, Func<string, byte[]?> image)
    {
        int now = ++laid;
        BackgroundValue? darkValue = theme?.DarkBackground;
        BackgroundValue? lightValue = theme?.LightBackground;
        if (darkValue is null && lightValue is null)
        {
            if (Any)
            {
                (dark, light) = (null, null);
                Changed?.Invoke(null, EventArgs.Empty);
            }
            return;
        }
        if (dark?.Value == darkValue && light?.Value == lightValue)
        {
            return;
        }
        IdeErrors.Observe(DecodeAsync(now, darkValue, lightValue, image));
    }

    private static async Task DecodeAsync(int now, BackgroundValue? darkValue, BackgroundValue? lightValue, Func<string, byte[]?> image)
    {
        (Shown? decodedDark, Shown? decodedLight) = await BackgroundWork.Run(() =>
        {
            Dictionary<string, Bitmap?> pictures = new(StringComparer.Ordinal);
            Shown? Decoded(BackgroundValue? value)
            {
                if (value is null)
                {
                    return null;
                }
                if (!pictures.TryGetValue(value.Image, out Bitmap? picture))
                {
                    pictures[value.Image] = picture = Decode(value.Image, image(value.Image));
                }
                return picture is null ? null : new Shown(value, picture);
            }
            return (Decoded(darkValue), Decoded(lightValue));
        });
        // A theme laid since takes its own pictures.
        if (now != laid)
        {
            return;
        }
        (dark, light) = (decodedDark, decodedLight);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static Bitmap? Decode(string name, byte[]? bytes)
    {
        string? why = bytes is null ? "it is not kept beside the theme" : ThemeImages.WhyNot(name, bytes);
        if (bytes is null || why is not null)
        {
            IdeErrors.Write($"Themes: the background {name} is not drawn: {why}");
            return null;
        }
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            IdeErrors.Write($"Themes: the background {name} could not be decoded: {error.Message}");
            return null;
        }
    }
}
