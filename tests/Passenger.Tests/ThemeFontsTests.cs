using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.App.State;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A theme's fonts as a person meets them: the code's family and size and the window's family are the theme's until
/// Settings names its own, which wins, and Reset Font Size goes back to the theme's size; and a size every earlier version
/// kept whether or not the person set it, 13, is read as none set.</summary>
public sealed class ThemeFontsTests : IDisposable
{
    private readonly string folder = TestFolders.Create("theme-fonts-");
    private readonly string settings = TestFolders.Create("theme-fonts-settings-");

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    [AvaloniaFact]
    public void A_themes_fonts_and_code_size_are_drawn_until_Settings_names_its_own_and_Reset_Font_Size_goes_back_to_the_themes()
    {
        IReadOnlyList<string> code = Ui.Await(CodeFonts.InstalledAsync(), "the installed monospaced fonts");
        IReadOnlyList<string> words = Ui.Await(WindowFonts.InstalledAsync(), "the installed fonts");
        Assert.SkipWhen(code.Count < 2 || words.Count == 0, "Fewer than two monospaced fonts, or no other font, are installed beside those Passenger carries");
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile(Path.Combine(folder, "One.cs"));
        Ui.Settle();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            string name = $"Fonts {Guid.NewGuid():N}";
            string json = $$$"""{"format": 1, "name": "{{{name}}}", "fonts": {"code": {"family": "{{{code[0]}}}", "size": 16}, "window": {"family": "{{{words[0]}}}"} } }""";
            Assert.Null(KeptThemes.Keep(ThemeSource.Added, false, name, Encoding.UTF8.GetBytes(json)));
            window.ApplySettings(window.Settings with { Look = new SourcedTheme(ThemeSource.Added.Name, name) });
            Ui.Settle();
            Assert.True((editor.FontFamily.Name, editor.FontSize) == (code[0], 16), $"The theme's code font is {code[0]} at 16, where the editor draws {editor.FontFamily.Name} at {editor.FontSize}");
            Assert.Equal(WindowFonts.Named(words[0])!.Name, WindowFont());

            window.ApplySettings(window.Settings with { EditorFontFamily = code[1], EditorFontSize = 14, WindowFontFamily = WindowFontNames.SystemUi });
            Ui.Settle();
            Assert.True((editor.FontFamily.Name, editor.FontSize) == (code[1], 14), $"Settings named {code[1]} at 14 over the theme's, where the editor draws {editor.FontFamily.Name} at {editor.FontSize}");
            Assert.Equal(WindowFonts.Named(WindowFontNames.SystemUi)!.Name, WindowFont());

            window.Actions.Run(AppActions.ResetFontSize);
            Ui.Settle();
            Assert.Null(window.Settings.EditorFontSize);
            Assert.Equal(16, editor.FontSize);
            window.Actions.Run(AppActions.IncreaseFontSize);
            Ui.Settle();
            Assert.True(window.Settings.EditorFontSize == 17 && editor.FontSize == 17, $"Increase Font Size from the theme's 16 set {window.Settings.EditorFontSize} and drew {editor.FontSize}");

            window.ApplySettings(window.Settings with { Look = null, EditorFontSize = null, EditorFontFamily = null, WindowFontFamily = null });
            Ui.Settle();
            Assert.True((editor.FontFamily.Name, editor.FontSize) == (CodeEditor.CodeFont.Name, AppSettings.OwnCodeSize),
                $"Passenger's own theme, nothing set, drew the code in {editor.FontFamily.Name} at {editor.FontSize}");
            Assert.Equal(WindowFonts.Carried.Name, WindowFont());
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(13, null)]
    [InlineData(15, 15.0)]
    public void A_code_size_kept_before_themes_gave_one_is_the_persons_unless_it_was_the_size_every_version_wrote(double kept, double? read)
    {
        File.WriteAllText(Path.Combine(settings, StateStore.SettingsFile), $$"""{ "version": 4, "editorFontSize": {{kept}} }""");
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.KeepSettings();
        Ui.Settle();
        try
        {
            Assert.Equal(read, window.Settings.EditorFontSize);
            Assert.Equal(read ?? AppSettings.OwnCodeSize, window.EditorArea.CodeEditor.FontSize);
        }
        finally
        {
            window.Close();
        }
    }

    private static string? WindowFont() => Application.Current!.Resources.TryGetValue(WindowFonts.Key, out object? family) ? (family as FontFamily)?.Name : null;
}
