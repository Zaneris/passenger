using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>What a theme sets beyond its tokens, as a person meets it: a theme added from a file with a picture beside it keeps
/// the picture and draws it over the editor at its opacity, and colours a language's code its own way, other languages
/// keeping the theme's; a theme's margins move the editor's text and size a tool window's header and a dialog's footer; and a
/// theme over a carried theme shows the base with only its changes.</summary>
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

    /// <summary>A theme added from a file keeps its picture over the editor and colours its languages' code its own
    /// way, and its margins move the editor's text and size the headers and footers, as one flow, each step the check
    /// one test made in an instance of its own.</summary>
    [AvaloniaFact]
    public void A_themes_picture_language_colours_and_margins_are_drawn_where_it_gives_them()
    {
        Flow.StepApart<ThemeLooksTests>("a theme added from a file keeps its picture draws it over the editor and colours its languages code its own way", t => t.A_theme_added_from_a_file_keeps_its_picture_draws_it_over_the_editor_and_colours_its_languages_code_its_own_way());
        Flow.StepApart<ThemeLooksTests>("a themes margins move the editors text and size the tool windows header and a dialogs footer", t => t.A_themes_margins_move_the_editors_text_and_size_the_tool_windows_header_and_a_dialogs_footer());
        Flow.StepApart<ThemeLooksTests>("a theme over a carried theme shows the base with only its changes", t => t.A_theme_over_a_carried_theme_shows_the_base_with_only_its_changes());
    }

    private void A_theme_over_a_carried_theme_shows_the_base_with_only_its_changes()
    {
        ThemeFile cubicle = CarriedThemes.Theme("Cubicle")!;
        string name = $"Over Cubicle {Guid.NewGuid():N}";
        string json = $$$"""{"format": 1, "name": "{{{name}}}", "base": "Cubicle", "tokens": {"Accent": "#C0306A", "ControlRadius": 9}}""";
        Assert.Null(KeptThemes.Keep(ThemeSource.Added, false, name, System.Text.Encoding.UTF8.GetBytes(json)));
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        Ui.Settle();
        try
        {
            window.ApplySettings(window.Settings with { Look = new SourcedTheme(ThemeSource.Added.Name, name) });
            Ui.Settle();
            Assert.True(Themes.Said is null, $"The theme over Cubicle was not laid: {Themes.Said}");
            foreach (ThemeVariant variant in (ThemeVariant[])[ThemeVariant.Dark, ThemeVariant.Light])
            {
                Assert.True(Drawn(window, TokenKeys.Accent, variant) is ISolidColorBrush { Color: var accent } && accent == Color.Parse("#C0306A"), $"The theme's own accent is not drawn under {variant}");
                Dictionary<string, TokenValue> carried = new(cubicle.Both);
                foreach ((string token, TokenValue value) in variant == ThemeVariant.Dark ? cubicle.Dark : cubicle.Light)
                {
                    carried[token] = value;
                }
                Assert.True(Drawn(window, TokenKeys.Frame, variant) is ISolidColorBrush { Color: var frame } && frame.ToUInt32() == ((ColourValue)carried["Frame"]).Argb,
                    $"Cubicle's frame is not drawn under {variant} over Cubicle");
            }
            Assert.True(Drawn(window, TokenKeys.ControlRadius, ThemeVariant.Dark) is CornerRadius { TopLeft: 9 }, "The theme's own radius is not drawn over Cubicle's");
            Assert.True(Drawn(window, TokenKeys.TabRadius, ThemeVariant.Dark) is CornerRadius { TopLeft: 0 }, "Cubicle's square tabs are lost under a theme over it");
            Assert.True(Themes.Layout is { } layout && layout.RightWidth == cubicle.Layout!.RightWidth && layout.ToolWindows == cubicle.Layout.ToolWindows
                && layout.Places.SequenceEqual(cubicle.Layout.Places), "Cubicle's default layout is not the default under a theme over it");
            Assert.Equal(cubicle.Fonts.CodeSize, Themes.Fonts.CodeSize);
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }

        static object? Drawn(Control control, string key, ThemeVariant variant) => control.TryFindResource(key, variant, out object? found) ? found : null;
    }

    private void A_theme_added_from_a_file_keeps_its_picture_draws_it_over_the_editor_and_colours_its_languages_code_its_own_way()
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

    private void A_themes_margins_move_the_editors_text_and_size_the_tool_windows_header_and_a_dialogs_footer()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "public class One { }\n");
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile(Path.Combine(folder, "One.cs"));
        Ui.Settle();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            double text = editor.PointOf(0).X;
            Border header = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains(StyleClasses.ToolHeader) && b.IsEffectivelyVisible);
            TextBlock title = header.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains(StyleClasses.ToolTitle));
            Assert.True(header.Bounds.Height == 38 && title.Margin == new Thickness(14, 0, 0, 0), $"Passenger's own tool window header is {header.Bounds.Height} high, its title at {title.Margin}");

            string name = $"Margins {Guid.NewGuid():N}";
            string json = $$$"""{"format": 1, "name": "{{{name}}}", "tokens": {"EditorTextPadding": 30, "ToolHeaderHeight": 50, "ToolTitleMargin": [20, 0, 0, 0], "DialogFooterPadding": [24, 14, 24, 14]} }""";
            Assert.Null(KeptThemes.Keep(ThemeSource.Added, false, name, System.Text.Encoding.UTF8.GetBytes(json)));
            window.ApplySettings(window.Settings with { Look = new SourcedTheme(ThemeSource.Added.Name, name) });
            Ui.Settle();
            Assert.True(editor.PointOf(0).X == text + 24, $"The theme's 30 between gutter and text put the text at {editor.PointOf(0).X}, where Passenger's own 6 put it at {text}");
            Assert.True(header.Bounds.Height == 50 && title.Margin == new Thickness(20, 0, 0, 0), $"The theme's tool window header is {header.Bounds.Height} high, its title at {title.Margin}");
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            Border footer = dialog.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains(StyleClasses.FooterBar));
            Assert.Equal(new Thickness(24, 14, 24, 14), footer.Padding);
            dialog.Close();
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    private static Color Keyword(Control editor) =>
        editor.TryFindResource(TokenKeys.Syntax_Keyword, editor.ActualThemeVariant, out object? found) && found is ISolidColorBrush brush ? brush.Color : default;
}
