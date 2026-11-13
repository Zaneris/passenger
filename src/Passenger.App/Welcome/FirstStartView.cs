using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.App.Welcome;

/// <summary>The first start's question, laid over the window before anything else: the theme, chosen from large pictures of
/// Passenger in each (its own and every theme it carries), then dark or light, chosen from pictures of the theme just chosen
/// in each, or the system's by a small choice under them. A click or Enter chooses, put in force at once through
/// <c>change</c>, so the page itself is drawn in it; the arrow keys move between the choices, Back returns to the themes,
/// and Escape or Keep the Defaults ends it keeping what is in force. Every key is the page's while it shows, so nothing of
/// the IDE under it runs.</summary>
public sealed class FirstStartView : UserControl, IOwnKeys
{
    /// <summary>The gap between two tiles.</summary>
    private const double Gap = 24;


    private readonly Func<AppSettings> now;
    private readonly Action<Func<AppSettings, AppSettings>> change;
    private readonly Dictionary<string, Bitmap> pictures = [];
    private readonly TextBlock title = new() { Classes = { StyleClasses.WelcomeTitle } };
    private readonly TextBlock about = new() { Classes = { StyleClasses.WelcomeSubtitle }, TextWrapping = TextWrapping.Wrap };
    private readonly Grid tiles = new();
    private readonly Button system;
    private readonly PathIcon systemChosen = new() { Classes = { StyleClasses.FirstStartCheck } };
    private readonly TextBlock systemWords = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button back = new() { Classes = { StyleClasses.Link, StyleClasses.FirstStartQuiet }, Content = "Back" };
    private readonly Button keep = new() { Classes = { StyleClasses.Link, StyleClasses.FirstStartQuiet }, Content = "Keep the Defaults" };

    public FirstStartView(Func<AppSettings> now, Action<Func<AppSettings, AppSettings>> change)
    {
        this.now = now;
        this.change = change;
        Classes.Add(StyleClasses.FirstStart);
        Focusable = true;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        Looks = [.. Carried()];

        Image mark = new() { Classes = { StyleClasses.WelcomeMark }, Source = new Bitmap(AssetLoader.Open(new Uri("avares://Passenger/Assets/passenger.png"))) };
        RenderOptions.SetBitmapInterpolationMode(mark, BitmapInterpolationMode.HighQuality);
        Grid hero = new() { Classes = { StyleClasses.WelcomeHero }, ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        hero.Children.Add(mark);
        hero.Children.Add(new StackPanel { [Grid.ColumnProperty] = 1, VerticalAlignment = VerticalAlignment.Center, Children = { title, about } });

        systemChosen.Bind(PathIcon.DataProperty, systemChosen.GetResourceObservable(IconKeys.Check));
        system = new Button
        {
            Classes = { StyleClasses.FirstStartSystem },
            Content = new StackPanel { Classes = { StyleClasses.FirstStartCaption }, Children = { systemChosen, systemWords } },
        };
        system.Click += (_, _) => ChooseVariant(ThemeChoice.System);
        back.Click += (_, _) => Show(Screen.Theme);
        keep.Click += (_, _) => End();
        DockPanel foot = new() { Classes = { StyleClasses.FirstStartFoot } };
        DockPanel.SetDock(keep, Dock.Right);
        foot.Children.Add(keep);
        foot.Children.Add(back);

        StackPanel page = new() { Classes = { StyleClasses.FirstStartPage }, Children = { hero, tiles, system, foot } };
        // As tall as the view at least, so the page stands in its middle; on a shorter view it scrolls.
        Panel middle = new() { Children = { page } };
        ScrollViewer scroller = new() { Content = middle };
        scroller.SizeChanged += (_, e) => middle.MinHeight = e.NewSize.Height;
        Content = scroller;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += (_, _) =>
        {
            // The system's dark or light moved under the themes' pictures: the tiles come again, the keys where they were.
            bool held = IsKeyboardFocusWithin;
            Fill();
            if (held)
            {
                FocusChoice();
            }
        };
        Loaded += (_, _) =>
        {
            // Let go of as the page left the window, so read again.
            if (pictures.Count == 0)
            {
                Fill();
            }
            FocusChoice();
        };
        Unloaded += (_, _) =>
        {
            foreach (Bitmap picture in pictures.Values)
            {
                picture.Dispose();
            }
            pictures.Clear();
        };
        Show(Screen.Theme);
    }

    /// <summary>The page's two questions.</summary>
    public enum Screen
    {
        Theme,
        DarkOrLight,
    }

    /// <summary>A theme offered: Passenger's own (<see cref="Theme"/> null) or one it carries, its name and what it is like
    /// in a few words.</summary>
    public sealed record Look(SourcedTheme? Theme, string Name, string Words)
    {
        /// <summary>Its picture in dark or light, among the app's resources.</summary>
        public Uri Picture(bool dark) => new($"avares://Passenger/Assets/FirstStart/{FirstStartPictures.FileOf(Name, dark)}");
    }

    /// <summary>The themes offered, in the order shown: Passenger's own, then each it carries that has its pictures.</summary>
    public IReadOnlyList<Look> Looks { get; }

    /// <summary>The question shown.</summary>
    public Screen Shown { get; private set; }

    /// <summary>The tiles shown, in their order: the themes', or Dark and Light.</summary>
    public IReadOnlyList<Button> Tiles => [.. tiles.Children.OfType<Button>()];

    /// <summary>The small choice under Dark and Light to follow the system's dark or light.</summary>
    public Button SystemChoice => system;

    /// <summary>The way back from dark or light to the themes.</summary>
    public Button Back => back;

    /// <summary>The page ended, by a choice of dark or light, Escape or Keep the Defaults.</summary>
    public event EventHandler? Ended;

    public bool TakesKey(KeyEventArgs e) => true;

    /// <summary>Passenger's own theme and every theme it carries whose pictures it carries too.</summary>
    private static IEnumerable<Look> Carried()
    {
        yield return new Look(null, "Passenger", "Roomy and rounded, in deep blues");
        foreach (ListedTheme listed in BuiltInThemes.Listed(ThemeResources.Kinds).Listed)
        {
            if (!listed.Code && listed.Read.Read is { } theme && listed.Named is { } named)
            {
                Look look = new(named, theme.Name, theme.Description.Split(':')[0].Trim());
                if (AssetLoader.Exists(look.Picture(dark: true)) && AssetLoader.Exists(look.Picture(dark: false)))
                {
                    yield return look;
                }
            }
        }
    }

    /// <summary>Shows a question, its tiles marking what is in force, the keys at that choice.</summary>
    public void Show(Screen screen)
    {
        Shown = screen;
        Fill();
        if (IsLoaded)
        {
            FocusChoice();
        }
    }

    /// <summary>The theme chosen: put in force and kept, then dark or light asked in it.</summary>
    public void ChooseLook(Look look)
    {
        change(settings => settings with { Look = look.Theme });
        Show(Screen.DarkOrLight);
    }

    /// <summary>Dark, light or the system's chosen: put in force and kept, which ends the page.</summary>
    public void ChooseVariant(ThemeChoice choice)
    {
        change(settings => settings with { Theme = choice });
        End();
    }

    /// <summary>The page ends, keeping what is in force.</summary>
    public void End() => Ended?.Invoke(this, EventArgs.Empty);

    /// <summary>The tiles, words and marks of the question shown, in the theme and dark or light in force.</summary>
    private void Fill()
    {
        AppSettings settings = now();
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        tiles.Children.Clear();
        tiles.ColumnDefinitions.Clear();
        if (Shown == Screen.Theme)
        {
            title.Text = "Welcome to Passenger";
            about.Text = $"Choose a theme to begin, then dark or light. {SettingsDialog.PlaceOfPage(SettingsDialog.Appearance)} changes either at any time.";
            foreach (Look look in Looks)
            {
                Add(Tile(look.Name, look.Words, look.Picture(dark), look.Theme == settings.Look, () => ChooseLook(look)));
            }
        }
        else
        {
            Look look = Looks.FirstOrDefault(l => l.Theme == settings.Look) ?? Looks[0];
            title.Text = "Dark or light";
            about.Text = $"{look.Name} comes in both. {SettingsDialog.PlaceOfPage(SettingsDialog.Appearance)} changes it at any time.";
            Add(Tile("Dark", null, look.Picture(dark: true), settings.Theme == ThemeChoice.Dark, () => ChooseVariant(ThemeChoice.Dark)));
            Add(Tile("Light", null, look.Picture(dark: false), settings.Theme == ThemeChoice.Light, () => ChooseVariant(ThemeChoice.Light)));
        }
        system.IsVisible = Shown == Screen.DarkOrLight;
        systemWords.Text = SystemWords();
        systemChosen.IsVisible = settings.Theme == ThemeChoice.System;
        system.Classes.Set(StyleClasses.Chosen, systemChosen.IsVisible);
        back.IsVisible = Shown == Screen.DarkOrLight;
    }

    private void Add(Button tile)
    {
        if (tiles.ColumnDefinitions.Count > 0)
        {
            tiles.ColumnDefinitions.Add(new ColumnDefinition(Gap, GridUnitType.Pixel));
        }
        tiles.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        Grid.SetColumn(tile, tiles.ColumnDefinitions.Count - 1);
        tiles.Children.Add(tile);
    }

    /// <summary>A choice's tile: its picture, its name and a few words, marked when it is the one in force.</summary>
    private Button Tile(string name, string? words, Uri picture, bool chosen, Action choose)
    {
        // Never wider than its pixels keep sharp.
        Image image = new() { Source = Picture(picture), Stretch = Stretch.Uniform, MaxWidth = FirstStartPictures.Widest };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        Border frame = new() { Classes = { StyleClasses.FirstStartPicture }, Child = image };
        PathIcon check = new() { Classes = { StyleClasses.FirstStartCheck }, IsVisible = chosen };
        check.Bind(PathIcon.DataProperty, check.GetResourceObservable(IconKeys.Check));
        TextBlock named = new() { Classes = { StyleClasses.FirstStartName }, Text = name };
        TextBlock said = new() { Classes = { StyleClasses.FirstStartWords }, Text = words, IsVisible = words is not null, TextTrimming = TextTrimming.CharacterEllipsis };
        Button tile = new()
        {
            Classes = { StyleClasses.FirstStartTile },
            Content = new StackPanel
            {
                Children = { frame, new StackPanel { Classes = { StyleClasses.FirstStartCaption }, Children = { check, named } }, said },
            },
        };
        tile.Classes.Set(StyleClasses.Chosen, chosen);
        AutomationProperties.SetName(tile, name);
        tile.Click += (_, _) => choose();
        return tile;
    }

    /// <summary>The small choice's words: to follow the system, saying which it is now where the system says.</summary>
    private static string SystemWords()
    {
        string? shown = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant switch
        {
            Avalonia.Platform.PlatformThemeVariant.Dark => "dark",
            Avalonia.Platform.PlatformThemeVariant.Light => "light",
            _ => null,
        };
        return shown is null ? "Follow the system" : $"Follow the system ({shown} now)";
    }

    /// <summary>A picture the app carries, read once while the page shows.</summary>
    private Bitmap Picture(Uri place)
    {
        string key = place.ToString();
        if (!pictures.TryGetValue(key, out Bitmap? picture))
        {
            using Stream stream = AssetLoader.Open(place);
            picture = new Bitmap(stream);
            pictures[key] = picture;
        }
        return picture;
    }

    /// <summary>The keys at the choice in force, unringed until a key moves them: its tile, or the system's choice.</summary>
    private void FocusChoice()
    {
        Control? chosen = Tiles.FirstOrDefault(t => t.Classes.Contains(StyleClasses.Chosen));
        if (system.IsVisible && system.Classes.Contains(StyleClasses.Chosen))
        {
            chosen = system;
        }
        (chosen ?? Tiles.FirstOrDefault() ?? (Control)this).Focus();
    }

    /// <summary>Left and Right move between the tiles, Down to the system's choice under them and Up back; Escape ends the
    /// page keeping what is in force. Enter and Space are the focused button's own.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        IReadOnlyList<Button> shown = Tiles;
        Control? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        int at = focused is null ? -1 : shown.ToList().FindIndex(t => t == focused || t.IsVisualAncestorOf(focused));
        switch (e.Key)
        {
            case Key.Escape:
                End();
                break;
            case Key.Left or Key.Right when at >= 0:
                shown[Math.Clamp(at + (e.Key == Key.Right ? 1 : -1), 0, shown.Count - 1)].Focus(NavigationMethod.Directional);
                break;
            case Key.Left or Key.Right or Key.Up when at < 0:
                (shown.FirstOrDefault(t => t.Classes.Contains(StyleClasses.Chosen)) ?? shown[0]).Focus(NavigationMethod.Directional);
                break;
            case Key.Down when at >= 0 && system.IsVisible:
                system.Focus(NavigationMethod.Directional);
                break;
            case Key.Down when at < 0 && focused != system:
                shown[0].Focus(NavigationMethod.Directional);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
