using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;

namespace Passenger.Tests;

/// <summary>What a theme sets beyond its tokens, as a person meets it: a theme added from a file with a picture beside it keeps
/// the picture and draws it over the editor at its opacity, and colours a language's code its own way, other languages
/// keeping the theme's.</summary>
public sealed class ThemeLooksTests : IDisposable
{
    private readonly string folder = TestFolders.Create("theme-looks-folder-");
    private readonly string settings = TestFolders.Create("theme-looks-settings-");
    private readonly string themes = TestFolders.Create("theme-looks-file-");

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
        TestFolders.Delete(themes);
    }

    [AvaloniaFact]
    public void A_theme_added_from_a_file_keeps_its_picture_draws_it_over_the_editor_and_colours_its_languages_code_its_own_way()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "public class One { }\n");
        File.WriteAllText(Path.Combine(folder, "one.json"), "{ \"one\": true }\n");
        File.WriteAllBytes(Path.Combine(themes, "glow.png"), TestPictures.Png(64, 48, 0xE0, 0x60, 0xA0));
        string theme = Path.Combine(themes, "Looks.json");
        File.WriteAllText(theme, """
            {"format": 1, "name": "Looks of the test",
             "background": {"image": "glow.png", "opacity": 0.3, "places": ["editor", "welcome"], "fit": "tile"},
             "languages": {"csharp": {"dark": {"Syntax.Keyword": "#FF10A0"}, "light": {"Syntax.Keyword": "#A01060"}}}}
            """);
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        try
        {
            window.OpenFile(Path.Combine(folder, "One.cs"));
            Ui.Settle();
            Color plain = Keyword(window.EditorArea.CodeEditor);
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            Assert.Null(Ui.Await(dialog.ThemePicker.AddFileAsync(theme), "a theme file with a picture added"));
            Ui.Settle();
            Assert.NotNull(KeptThemes.Image(ThemeSource.Added, false, "Looks of the test", "glow.png"));
            ThemeBackdrops.Shown? shown = ThemeBackdrops.Of(window.ActualThemeVariant);
            Assert.True(shown is { Value: { Opacity: 0.3, Fit: BackgroundFit.Tile } }, "The theme's picture is not drawn before Apply");
            Assert.Equal((64, 48), ((int)shown.Picture.Size.Width, (int)shown.Picture.Size.Height));
            ThemeBackdrop over = window.EditorArea.Groups[0].GetVisualDescendants().OfType<ThemeBackdrop>().Single(b => b.Place == BackgroundPlaces.Editor);
            Assert.True(over.Bounds.Width > 0 && !over.IsHitTestVisible, "The editor's picture covers nothing, or takes the pointer");

            Color keyword = window.ActualThemeVariant == ThemeVariant.Dark ? Color.Parse("#FF10A0") : Color.Parse("#A01060");
            Assert.Equal(keyword, Keyword(window.EditorArea.CodeEditor));
            dialog.Apply();
            dialog.Close();
            window.OpenFile(Path.Combine(folder, "one.json"));
            Ui.Settle();
            Assert.Equal(plain, Keyword(window.EditorArea.CodeEditor));
            window.OpenFile(Path.Combine(folder, "One.cs"));
            Ui.Settle();
            Assert.Equal(keyword, Keyword(window.EditorArea.CodeEditor));

            window.ApplySettings(window.Settings with { Look = null });
            Ui.Settle();
            Assert.Null(ThemeBackdrops.Of(window.ActualThemeVariant));
            Assert.Equal(plain, Keyword(window.EditorArea.CodeEditor));
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>The keyword colour an editor finds where it is, its own resources first.</summary>
    private static Color Keyword(Control editor) =>
        editor.TryFindResource(TokenKeys.Syntax_Keyword, editor.ActualThemeVariant, out object? found) && found is ISolidColorBrush brush ? brush.Color : default;
}
