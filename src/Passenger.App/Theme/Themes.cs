using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Passenger.App;

/// <summary>Passenger's built-in themes, each a Dark and a Light that follow the dark-or-light setting: Passenger's own
/// (Tokens.axaml, App.axaml's), the default; Harbour (Harbour.axaml); the rest written by tools/themes.py, each to the
/// file of its name.</summary>
public enum Look
{
    Passenger,
    Harbour,
    Sorbet,
    Nocturne,
    Saffron,
    Bonbon,
    Ledger,
    Slate,
    Orbit,
    Nightjar,
    Moss,
    Phosphor,
}

/// <summary>Which colours the code is drawn in: the theme's own scheme, or Rider's (Dark or Light as the theme is).</summary>
public enum ColourScheme
{
    Theme,
    Rider,
}

/// <summary>Puts a theme and a colour scheme in force for the process: another theme's tokens (Theme/NAME.axaml) laid over Passenger's
/// in the application's resources, then Rider's scheme (RiderScheme.axaml) over either, so the last laid wins. A theme is
/// tokens alone, its colours and the few shapes it draws differently, which the shared styles (Shell.axaml) and Fluent's
/// own keys (FluentKeys) read; Fluent's palette takes the theme's accent. UI thread only.</summary>
public static class Themes
{
    private static readonly Uri Root = new("avares://Passenger/");
    private static readonly List<ResourceInclude> laid = [];
    private static bool applied;

    /// <summary>The theme in force.</summary>
    public static Look Look { get; private set; }

    /// <summary>The code's colours in force.</summary>
    public static ColourScheme Scheme { get; private set; }

    /// <summary>Puts a theme or a scheme in force over the one before; null leaves that one as it is.</summary>
    public static void Apply(Application app, Look? look, ColourScheme? scheme)
    {
        Look nextLook = look ?? Look;
        ColourScheme nextScheme = scheme ?? Scheme;
        if (applied && nextLook == Look && nextScheme == Scheme)
        {
            return;
        }
        applied = true;
        IList<IResourceProvider> merged = app.Resources.MergedDictionaries;
        foreach (ResourceInclude was in laid)
        {
            merged.Remove(was);
        }
        laid.Clear();
        // Rider's scheme after the theme, so its colours win under either.
        foreach (string? source in (string?[])[nextLook == Look.Passenger ? null : $"Theme/{nextLook}.axaml", nextScheme == ColourScheme.Rider ? "Theme/RiderScheme.axaml" : null])
        {
            if (source is not null)
            {
                ResourceInclude include = new(Root) { Source = new Uri(Root, source) };
                laid.Add(include);
                merged.Add(include);
            }
        }
        Look = nextLook;
        Scheme = nextScheme;
        TakeAccentFromTokens(app);
    }

    /// <summary>Fluent's own accented parts (a ticked box, a chosen radio button, a focused drop-down) in the theme's
    /// Passenger.Accent: Fluent takes the system's accent otherwise (the user's on macOS, #0078D4 headless).</summary>
    public static void TakeAccentFromTokens(Application app)
    {
        FluentTheme fluent = app.Styles.OfType<FluentTheme>().Single();
        foreach (ThemeVariant variant in (ThemeVariant[])[ThemeVariant.Dark, ThemeVariant.Light])
        {
            if (app.TryGetResource(TokenKeys.Accent, variant, out object? found) && found is ISolidColorBrush accent)
            {
                fluent.Palettes[variant] = new ColorPaletteResources { Accent = accent.Color };
            }
        }
    }
}
