using Avalonia;
using Avalonia.Media;
using SkiaSharp;

namespace Passenger.App;

/// <summary>A Linux whose fontconfig lists no font, none being installed or its cache being out of date: Avalonia takes its
/// default font from fontconfig, and with none it could draw no word, so the Inter Passenger carries is made the default,
/// and why is said once, on standard error and in the log, with what lists the fonts again.</summary>
public static class NoSystemFont
{
    /// <summary>What the person is told where fontconfig lists no font.</summary>
    public const string Said = "fontconfig lists no font, so Passenger draws its words in the fonts it carries alone. "
        + "Where fonts are installed, fontconfig's cache is out of date: refresh it with fc-cache -f (sudo fc-cache -f for every user's). "
        + "Otherwise install one, such as your distribution's DejaVu Sans.";

    private static int told;

    /// <summary>The builder given the carried Inter as its default font where fontconfig lists none, saying so.</summary>
    public static AppBuilder Checked(this AppBuilder builder)
    {
        if (!OperatingSystem.IsLinux() || SKFontManager.Default.FontFamilyCount > 0)
        {
            return builder;
        }
        if (Interlocked.Exchange(ref told, 1) == 0)
        {
            IdeErrors.Write($"Fonts: {Said}");
            Console.Error.WriteLine($"passenger: {Said}");
        }
        return builder.With(new FontManagerOptions { DefaultFamilyName = WindowFonts.CarriedName });
    }
}
