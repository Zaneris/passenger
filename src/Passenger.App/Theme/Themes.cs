using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Passenger.App;

/// <summary>A theme or code colour scheme the command line names: Passenger's own (<see cref="Theme"/> null) or another.</summary>
public sealed record NamedTheme(SourcedTheme? Theme);

/// <summary>Puts a theme and a code colour scheme in force for the process, each named by its source and name: Passenger's
/// own theme is Tokens.axaml (App.axaml's), under every other; another's tokens, made from its file as kept
/// (<see cref="KeptThemes"/>, or the built-in source's), laid over it in the application's resources, then the scheme's over
/// either, so the last laid wins. One named but not kept yet is fetched from its source, Passenger's own drawn meanwhile and
/// said so (<see cref="Said"/>), and laid once kept; one that cannot be fetched is said, never dropped from the settings.
/// Fluent's palette takes the theme's accent. UI thread only.</summary>
public static class Themes
{
    private static readonly List<IResourceProvider> laid = [];
    private static readonly Dictionary<(bool Code, SourcedTheme Theme), Task> fetching = [];
    private static readonly Dictionary<(bool Code, SourcedTheme Theme), string> unfetched = [];
    private static readonly Dictionary<(bool Code, SourcedTheme Theme), ThemeFile> read = [];
    private static Application? applying;
    private static bool applied;
    private static ThemeVariant chosenVariant = ThemeVariant.Default;
    private static ThemeShows shows;

    /// <summary>The theme in force, or wanted while it is fetched; null for Passenger's own.</summary>
    public static SourcedTheme? Look { get; private set; }

    /// <summary>The code's colours in force, or wanted while they are fetched; null for the theme's own.</summary>
    public static SourcedTheme? Scheme { get; private set; }

    /// <summary>What the window says of the themes: one being fetched, or one that could not be and why; null for nothing.</summary>
    public static string? Said { get; private set; }

    /// <summary>Whether <see cref="Said"/> is a problem rather than work under way.</summary>
    public static bool SaidProblem { get; private set; }

    /// <summary>What the window says of the themes changed.</summary>
    public static event EventHandler? SaidChanged;

    /// <summary>The fonts the theme in force gives, which a font Settings names wins over.</summary>
    public static ThemeFonts Fonts { get; private set; } = ThemeFonts.None;

    /// <summary>The theme in force gave other fonts.</summary>
    public static event EventHandler? FontsChanged;

    /// <summary>The variants the theme in force shows in: one alone where it is a VS Code theme's, whatever the setting.</summary>
    public static ThemeShows Shows => shows;

    /// <summary>Puts the dark or light the setting chooses in force, unless the theme in force shows in one alone.</summary>
    public static void UseVariant(Application app, ThemeVariant chosen)
    {
        chosenVariant = chosen;
        app.RequestedThemeVariant = shows switch
        {
            ThemeShows.Dark => ThemeVariant.Dark,
            ThemeShows.Light => ThemeVariant.Light,
            _ => chosen,
        };
    }

    /// <summary>The default layout the theme in force gives, or null for Passenger's own.</summary>
    public static ThemeLayout? Layout { get; private set; }

    /// <summary>The theme in force gave another default layout.</summary>
    public static event EventHandler? LayoutChanged;

    /// <summary>Puts a theme and a scheme in force over those before, each fetched first where it is not kept.</summary>
    public static void Apply(Application app, SourcedTheme? look, SourcedTheme? scheme)
    {
        if (applied && look == Look && scheme == Scheme && ReferenceEquals(app, applying))
        {
            return;
        }
        applying = app;
        applied = true;
        Look = look;
        Scheme = scheme;
        LayWanted();
    }

    /// <summary>Lays the wanted theme and scheme again, as a refresh kept newer copies of them.</summary>
    public static void Reload()
    {
        read.Clear();
        unfetched.Clear();
        if (applied)
        {
            LayWanted();
        }
    }

    /// <summary>Lays a theme and a scheme as read, for a check of their tokens alone; the next <see cref="Apply"/> lays what
    /// it names again.</summary>
    public static void Wear(Application app, ThemeFile? theme, ThemeFile? scheme)
    {
        Lay(app, theme, scheme, _ => null);
        applied = false;
    }

    /// <summary>A theme or scheme as kept, read once; null where it is not kept here (fetched in its place) or is refused,
    /// with why in <see cref="unfetched"/>.</summary>
    private static ThemeFile? Kept(bool code, SourcedTheme theme)
    {
        if (read.TryGetValue((code, theme), out ThemeFile? known))
        {
            return known;
        }
        if (theme.From is not { } source)
        {
            unfetched[(code, theme)] = $"{theme.Name} comes from {theme.Source}, which names no folder, repository or address";
            return null;
        }
        byte[]? bytes = source == ThemeSource.BuiltIn ? BuiltInThemes.Bytes(code, theme.Name) : KeptThemes.Bytes(source, code, theme.Name);
        if (bytes is null)
        {
            return null;
        }
        string file = KeptThemes.PathOf(source, code, theme.Name);
        ThemeRead<ThemeFile> got = code ? ThemeFiles.ReadCodeColours(file, bytes, ThemeResources.Kinds) : ThemeFiles.ReadTheme(file, bytes, ThemeResources.Kinds);
        if (got.Read is not { } whole || whole.Name != theme.Name)
        {
            unfetched[(code, theme)] = got.Refused ?? $"{file} holds {got.Read?.Name}, not {theme.Name}";
            return null;
        }
        if (whole.Base is { } wanted)
        {
            // A base not kept beside the theme is fetched in its place.
            if (VsCodeBases.Kept(source, code, theme.Name, wanted, out string? why) is not { } under)
            {
                if (why is not null)
                {
                    unfetched[(code, theme)] = $"{theme.Name}'s VS Code theme {wanted.Theme} ({wanted.Id}) is refused: {why}";
                }
                return null;
            }
            whole = ThemeFiles.Over(code ? VsCodeColours.ToScheme(under) : VsCodeColours.ToTheme(under), whole);
        }
        read[(code, theme)] = whole;
        return whole;
    }

    private static void LayWanted()
    {
        if (applying is not { } app)
        {
            return;
        }
        ThemeFile? theme = Look is { } look ? Kept(false, look) ?? Fetched(false, look) : null;
        ThemeFile? scheme = Scheme is { } colours ? Kept(true, colours) ?? Fetched(true, colours) : null;
        Func<string, byte[]?> images = theme is null || Look is not { From: { } source } shown ? _ => null
            : source == ThemeSource.BuiltIn ? image => BuiltInThemes.Image(false, shown.Name, image)
            : image => KeptThemes.Image(source, false, shown.Name, image);
        Lay(app, theme, scheme, images);
        Say();
    }

    /// <summary>Starts fetching one not kept, unless under way or refused already; null, for Passenger's own meanwhile.</summary>
    private static ThemeFile? Fetched(bool code, SourcedTheme theme)
    {
        if (fetching.ContainsKey((code, theme)) || unfetched.ContainsKey((code, theme)))
        {
            return null;
        }
        Task fetch = FetchAsync(code, theme);
        if (!fetch.IsCompleted)
        {
            fetching[(code, theme)] = fetch;
        }
        IdeErrors.Observe(fetch);
        return null;
    }

    private static async Task FetchAsync(bool code, SourcedTheme theme)
    {
        IReadOnlyDictionary<string, ThemeTokenKind> kinds = ThemeResources.Kinds;
        ThemeSource source = theme.From!;
        string? why;
        try
        {
            byte[]? kept = source == ThemeSource.BuiltIn ? BuiltInThemes.Bytes(code, theme.Name) : KeptThemes.Bytes(source, code, theme.Name);
            ThemeFile? file = kept is null ? null : (code ? ThemeFiles.ReadCodeColours(theme.Name, kept, kinds) : ThemeFiles.ReadTheme(theme.Name, kept, kinds)).Read;
            why = file?.Base is { Id: not null }
                ? await Passenger.Core.BackgroundWork.Run(() => VsCodeBases.KeepAsync(source, code, theme.Name, file, CancellationToken.None))
                : source.Kind is ThemeSourceKind.BuiltIn or ThemeSourceKind.Added or ThemeSourceKind.OpenVsx
                ? $"{theme.Name} is no longer kept: {(source.Kind == ThemeSourceKind.OpenVsx ? "choose it again from VS Code themes" : "add its file again")} in {Settings.SettingsDialog.PlaceOfPage(Settings.SettingsDialog.Appearance)}"
                : await Passenger.Core.BackgroundWork.Run(() => FetchOnceAsync(source, code, theme.Name, kinds));
        }
        finally
        {
            fetching.Remove((code, theme));
        }
        if (why is not null)
        {
            unfetched[(code, theme)] = why;
            IdeErrors.Write($"Themes: {why}");
        }
        if (theme == (code ? Scheme : Look))
        {
            LayWanted();
        }
    }

    /// <summary>A theme of a name fetched from its source and kept; why not where it cannot be. Off the UI thread.</summary>
    public static async Task<string?> FetchOnceAsync(ThemeSource source, bool code, string name, IReadOnlyDictionary<string, ThemeTokenKind> kinds)
    {
        ThemeSource reached;
        try
        {
            reached = source.Reached;
        }
        catch (FormatException error)
        {
            return error.Message;
        }
        (byte[]? bytes, string? why) = await ThemeSourceReads.BytesAsync(reached, ThemeFiles.IndexFile, ThemeFiles.MostBytes, CancellationToken.None);
        if (bytes is null)
        {
            return $"{name} could not be fetched from {source.Shown}: {why}";
        }
        ThemeRead<ThemeIndex> index = ThemeFiles.ReadIndex($"{source.Shown}'s {ThemeFiles.IndexFile}", bytes);
        if (index.Read is not { } listed)
        {
            return $"{name} could not be fetched from {source.Shown}: {index.Refused}";
        }
        // The file named as the theme is tried first, as a source's files usually are.
        IReadOnlyList<string> paths = code ? listed.CodeColours : listed.Themes;
        foreach (string path in paths.OrderBy(p => Path.GetFileNameWithoutExtension(p) == name ? 0 : 1))
        {
            ListedTheme file = await ThemeSourceReads.FileAsync(source, reached, path, code, kinds, CancellationToken.None);
            if (file.Read.Read is { } theme && theme.Name == name && file.Bytes is { } whole)
            {
                (Dictionary<string, byte[]> images, string? unseen) = await ThemeSourceReads.ImagesAsync(file, reached, CancellationToken.None);
                return unseen ?? await VsCodeBases.KeepAsync(source, code, name, theme, CancellationToken.None) ?? KeptThemes.Keep(source, code, name, whole, images);
            }
        }
        return $"{source.Shown} lists no {(code ? "code colour scheme" : "theme")} named {name} that reads whole";
    }

    private static void Say()
    {
        string? said = null;
        bool problem = false;
        foreach ((bool code, SourcedTheme? theme) in ((bool, SourcedTheme?)[])[(false, Look), (true, Scheme)])
        {
            if (theme is null || read.ContainsKey((code, theme)))
            {
                continue;
            }
            string own = code ? "the theme's own colours for the code" : "Passenger's own theme";
            if (unfetched.TryGetValue((code, theme), out string? why))
            {
                said = $"{why}. {own} shows in its place until it can be";
                problem = true;
            }
            else if (fetching.ContainsKey((code, theme)))
            {
                said ??= $"{theme.Name} is being fetched from {theme.From?.Shown ?? theme.Source}, chosen before; {own} shows until it comes";
            }
        }
        if (said == Said && problem == SaidProblem)
        {
            return;
        }
        Said = said;
        SaidProblem = problem;
        SaidChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void Lay(Application app, ThemeFile? theme, ThemeFile? scheme, Func<string, byte[]?> images)
    {
        // Made before those in force go; the scheme after the theme, so it wins.
        ResourceDictionary[] next = [.. ((ThemeFile?[])[theme, scheme]).OfType<ThemeFile>().Select(ThemeResources.Dictionary)];
        IList<IResourceProvider> merged = app.Resources.MergedDictionaries;
        foreach (IResourceProvider was in laid)
        {
            merged.Remove(was);
        }
        laid.Clear();
        foreach (ResourceDictionary tokens in next)
        {
            laid.Add(tokens);
            merged.Add(tokens);
        }
        TakeAccentFromTokens(app);
        if ((theme?.Shows ?? ThemeShows.Both) != shows)
        {
            shows = theme?.Shows ?? ThemeShows.Both;
            UseVariant(app, chosenVariant);
        }
        // Depths cycle through as many brace colours as the theme and the scheme give, from three to six.
        Passenger.Languages.CSharp.BraceLevels.InForce = Passenger.Languages.CSharp.BraceLevels.Given(level =>
            ((ThemeFile?[])[theme, scheme]).Any(file => file is not null && Gives(file, $"Syntax.Brace{level}")));
        ThemeLanguages.Use(theme, scheme);
        ThemeBackdrops.Use(theme, images);
        ThemeFonts fonts = theme?.Fonts ?? ThemeFonts.None;
        if (fonts != Fonts)
        {
            Fonts = fonts;
            FontsChanged?.Invoke(null, EventArgs.Empty);
        }
        if (!ReferenceEquals(theme?.Layout, Layout))
        {
            Layout = theme?.Layout;
            LayoutChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    private static bool Gives(ThemeFile file, string token) => file.Both.ContainsKey(token) || file.Dark.ContainsKey(token) || file.Light.ContainsKey(token);

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
