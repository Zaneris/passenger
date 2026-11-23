using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.App.Editors;

namespace Passenger.App;

/// <summary>The icons a theme gives in Passenger's place as the application's resources: each an <see cref="IconPicture"/>
/// under the key of the icon it stands for, in its variant's theme dictionary, laid over Passenger's own (Icons.axaml,
/// FileIconSet.axaml), so every place that finds an icon by its key finds the theme's, and an icon the theme gives none for
/// stays Passenger's. Each SVG is read from beside the theme's file as kept, once however many keys it stands for. UI
/// thread only.</summary>
public static class ThemeIcons
{
    /// <summary>The theme's icons as a dictionary of resources, or null where it gives none.</summary>
    public static ResourceDictionary? Dictionary(ThemeFile theme, Func<string, byte[]?> read)
    {
        if (theme.DarkIcons.Count == 0 && theme.LightIcons.Count == 0)
        {
            return null;
        }
        ResourceDictionary laid = [];
        Dictionary<string, IconPicture?> made = new(StringComparer.Ordinal);
        foreach ((ThemeVariant variant, bool dark) in ((ThemeVariant, bool)[])[(ThemeVariant.Dark, true), (ThemeVariant.Light, false)])
        {
            ResourceDictionary icons = [];
            foreach ((string key, string path) in theme.IconsOf(dark))
            {
                if (!made.TryGetValue(path, out IconPicture? picture))
                {
                    picture = read(path) is { } bytes && ThemeImages.IconWhyNot(path, bytes) is null ? new IconPicture(path, bytes) : null;
                    if (picture is null)
                    {
                        IdeErrors.Write($"Themes: {theme.Name}'s icon {path} is not kept beside it or is no SVG, so Passenger's own shows in its place");
                    }
                    made[path] = picture;
                }
                if (picture is not null)
                {
                    icons[key] = picture;
                }
            }
            laid.ThemeDictionaries[variant] = icons;
        }
        return laid;
    }
}

/// <summary>An icon a theme gives as an SVG, standing where Passenger's geometry of its key stands: its geometry a unit
/// square, so an icon's box is fitted to it whole, and drawn as its SVG fitted into that box (<see cref="Draw"/>), its own
/// colours kept but <c>currentColor</c>, which takes the colour Passenger draws the icon in. Its SVG is read the first
/// time it is drawn in a colour, and nothing it names is read beside it.</summary>
public sealed class IconPicture : StreamGeometry
{
    private readonly byte[] svg;
    private readonly Dictionary<uint, SvgPicture?> drawn = [];

    public IconPicture(string name, byte[] svg)
    {
        Name = name;
        this.svg = svg;
        using StreamGeometryContext square = Open();
        square.BeginFigure(new Point(0, 0), isFilled: true);
        square.LineTo(new Point(1, 0));
        square.LineTo(new Point(1, 1));
        square.LineTo(new Point(0, 1));
        square.EndFigure(isClosed: true);
    }

    /// <summary>Its file's path beside the theme's.</summary>
    public string Name { get; }

    /// <summary>Draws it fitted into a box, centred, its <c>currentColor</c> the brush's colour and every colour as the look
    /// draws it; nothing where its SVG cannot be drawn.</summary>
    public void Draw(DrawingContext context, Rect box, IBrush? brush, IconLook look)
    {
        Color ink = brush is ISolidColorBrush solid ? solid.Color : Colors.Black;
        uint colour = ink.ToUInt32() | 0xFF000000;
        if (!drawn.TryGetValue(colour, out SvgPicture? picture))
        {
            string text = System.Text.Encoding.UTF8.GetString(svg).Replace("currentColor", $"#{colour & 0xFFFFFF:X6}", StringComparison.OrdinalIgnoreCase);
            (picture, string? problem) = SvgPicture.Read(text, null);
            if (problem is not null)
            {
                IdeErrors.Write($"Themes: the icon {Name} is not drawn: {problem}");
            }
            drawn[colour] = picture;
        }
        if (picture is null || picture.Size.Width <= 0 || picture.Size.Height <= 0)
        {
            return;
        }
        double fit = Math.Min(box.Width / picture.Size.Width, box.Height / picture.Size.Height);
        Size size = new(picture.Size.Width * fit, picture.Size.Height * fit);
        Rect into = new(box.X + ((box.Width - size.Width) / 2), box.Y + ((box.Height - size.Height) / 2), size.Width, size.Height);
        picture.Draw(context, into, look, (ink.A / 255.0) * (brush?.Opacity ?? 1));
    }
}
