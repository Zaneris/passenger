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
/// keeping the theme's; and a theme's margins move the editor's text and size a tool window's header and a dialog's footer.</summary>
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

    /// <summary>The toolbar's Debug stands apart from Run and Profile beside it: drawn in a colour of its own, the theme's
    /// Debug token (on the title bar its TitleBar.Debug), in Dark and in Light, where it was drawn as Profile is.</summary>
    [AvaloniaFact]
    public void The_toolbars_debug_is_drawn_in_its_own_colour_apart_from_run_and_profile_in_dark_and_light()
    {
        File.WriteAllText(Path.Combine(folder, "Hello.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"Hello\");\n");
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(folder);
        Button debug = window.FindControl<Button>("DebugButton")!;
        Ui.WaitFor(() => debug.IsEffectivelyEnabled, () => $"Debug to be enabled for Hello (its tip says \"{ToolTip.GetTip(debug)}\")");
        try
        {
            foreach (ThemeChoice variant in (ThemeChoice[])[ThemeChoice.Dark, ThemeChoice.Light])
            {
                window.ApplySettings(window.Settings with { Theme = variant });
                Ui.Settle();
                bool onTitleBar = debug.GetVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains(StyleClasses.TitleBar));
                Color own = window.TryFindResource(onTitleBar ? TokenKeys.TitleBar_Debug : TokenKeys.Debug, window.ActualThemeVariant, out object? token) && token is ISolidColorBrush brush
                    ? brush.Color : throw new Xunit.Sdk.XunitException($"{variant} gives the Debug button no colour");
                (Color drawn, Color run, Color profile) = (Ink(debug), Ink(window.FindControl<Button>("RunButton")!), Ink(window.FindControl<Button>("ProfileButton")!));
                Assert.True(drawn == own && drawn != run && drawn != profile,
                    $"In {variant}, Debug is drawn in {drawn} where its own colour is {own}, Run's {run} and Profile's {profile}");
            }
        }
        finally
        {
            window.Close();
        }

        static Color Ink(Button button) => button.GetVisualDescendants().OfType<PathIcon>().First().Foreground is ISolidColorBrush ink ? ink.Color : default;
    }

    [AvaloniaFact]
    public void A_themes_margins_move_the_editors_text_and_size_the_tool_windows_header_and_a_dialogs_footer()
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

    /// <summary>The keyword colour an editor finds where it is, its own resources first.</summary>
    /// <summary>A card's C# takes the colours a theme gives C# of its own, as the editor showing the file draws them: the
    /// hover card's keyword and method in the theme's C# colours for them, its class in the theme's own class colour, which
    /// the theme leaves C#. A card's served code was coloured again by the language's own highlighter once it knew its
    /// language, which reads the signature as a file of its own and loses what the server said of its names.</summary>
    [AvaloniaFact]
    public void A_hover_cards_code_takes_the_colours_a_theme_gives_its_language_as_the_editor_does()
    {
        File.WriteAllText(Path.Combine(folder, "Looks.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "static class Program\n{\n    static int Twice(int value) => value * 2;\n\n    static void Main() => Twice(2);\n}\n");
        string theme = Path.Combine(themes, "Coded.json");
        File.WriteAllText(theme, """
            {"format": 1, "name": "Coded looks of the test",
             "languages": {"csharp": {"dark": {"Syntax.Keyword": "#FF10A0", "Syntax.Method": "#10C0FF"},
                                      "light": {"Syntax.Keyword": "#A01060", "Syntax.Method": "#0060A0"}}}}
            """);
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        try
        {
            window.OpenFile(Path.Combine(folder, "Program.cs"));
            Ui.Settle();
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            Assert.Null(Ui.Await(dialog.ThemePicker.AddFileAsync(theme), "a theme file with C# colours of its own added"));
            dialog.Apply();
            dialog.Close();
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            Color Drawn(string key) => editor.TryFindResource(key, editor.ActualThemeVariant, out object? found) && found is ISolidColorBrush brush ? brush.Color : default;
            bool dark = window.ActualThemeVariant == ThemeVariant.Dark;
            Assert.Equal(Color.Parse(dark ? "#FF10A0" : "#A01060"), Drawn(TokenKeys.Syntax_Keyword));
            Assert.Equal(Color.Parse(dark ? "#10C0FF" : "#0060A0"), Drawn(TokenKeys.Syntax_Method));

            int at = editor.Document.Snapshot.ToString().IndexOf("=> Twice", StringComparison.Ordinal) + 5;
            window.MouseMove(editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value, Avalonia.Input.RawInputModifiers.None);
            (string Word, string Key)[] words = [("int", TokenKeys.Syntax_Keyword), ("Program", TokenKeys.Syntax_Class), ("Twice", TokenKeys.Syntax_Method)];
            Avalonia.Controls.Documents.Run[] Runs() => [.. window.Assist.HoverCard?.GetVisualDescendants().OfType<ColouredCode>().SelectMany(c => c.Runs) ?? []];
            Ui.WaitFor(() => words.All(w => Runs().Any(r => r.Text == w.Word && r.Foreground is ISolidColorBrush brush && brush.Color == Drawn(w.Key))),
                () => $"the hover card's int, Program and Twice in the colours the editor draws C#'s in, where its runs are "
                    + $"[{string.Join(", ", Runs().Select(r => $"'{r.Text}' {(r.Foreground as ISolidColorBrush)?.Color}"))}]: the card's code would read otherwise than the file's");
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
