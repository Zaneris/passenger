using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>The window's typeface: the Inter Passenger carries, its hinted faces, until Settings › Appearance chooses
/// another, which then draws every window's and popup's words, the Git log's commits and the Explorer's rows among them.</summary>
public sealed class WindowFontTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("windowfont-"));

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>The Git log's commits and the Explorer's rows are drawn in the carried Inter, whose faces carry their
    /// TrueType hints; the system's own UI font chosen in Settings › Appearance and applied draws them, the dialog's own
    /// words and Find Action's rows in it too, Settings shows it chosen when it opens again, and Inter chosen there draws
    /// them in Inter again.</summary>
    [AvaloniaFact]
    public void The_window_font_chosen_in_settings_draws_the_logs_commits_and_the_explorers_rows_and_inter_comes_back()
    {
        File.WriteAllText(Path.Combine(folder, "Element.cs"), "class Element { }\n");
        File.WriteAllText(Path.Combine(folder, "Explorer.cs"), "class Explorer { }\n");
        TestGit.Init(folder);
        TestGit.Run(folder, "commit", "-q", "--allow-empty", "-m", "Fix the Element tree's Explorer refresh");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false, width: 1300, height: 900);
        try
        {
            window.Perform(AppActions.ShowGit);
            GitLogView log = window.GitLog;
            Ui.Landed(() => log.Rows.Count == 2, "the log's two commits");
            string inter = FamilyOf(WindowFonts.Carried);
            Drawn(window, inter);
            GlyphTypeface face = Face(Words(Log(window), "Fix the Element tree's Explorer refresh"));
            Assert.True(face.PlatformTypeface.TryGetTable(OpenTypeTag.Parse("fpgm"), out ReadOnlyMemory<byte> hints) && hints.Length > 0,
                $"The log's commits are drawn in {face.FamilyName} with no TrueType hints, so their strokes blur at 100% on Windows");

            SettingsDialog dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            ComboBox family = SettingsTests.Editor<ComboBox>(dialog, "Font family");
            Assert.Equal(WindowFontNames.Carried, family.SelectedItem?.ToString());
            SettingsTests.ChooseRow(family, WindowFonts.SystemShown());
            dialog.Apply();
            Ui.Settle();
            Assert.Equal(WindowFontNames.SystemUi, window.Settings.WindowFontFamily);
            string system = FamilyOf(WindowFonts.Named(WindowFontNames.SystemUi)!);
            Assert.True(system != inter, $"The system's own UI font is {system}, the carried font itself");
            TextBlock heading = Words(dialog, "Font family");
            Assert.True(Family(Face(heading)) == system, $"Settings' own words are drawn in {Family(Face(heading))}, not the system font {system}");
            dialog.Close();
            Ui.Settle();
            Drawn(window, system);

            dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            family = SettingsTests.Editor<ComboBox>(dialog, "Font family");
            Assert.Equal(WindowFonts.SystemShown(), family.SelectedItem?.ToString());
            SettingsTests.ChooseRow(family, WindowFontNames.Carried);
            dialog.Ok();
            Ui.Settle();
            Assert.Null(window.Settings.WindowFontFamily);
            Drawn(window, inter);
        }
        finally
        {
            window.ApplySettings(window.Settings with { WindowFontFamily = null });
            window.Close();
        }
    }

    /// <summary>The log's commits, the Explorer's rows and Find Action's rows are drawn in the family named.</summary>
    private static void Drawn(MainWindow window, string family)
    {
        foreach ((string where, TextBlock words) in (IEnumerable<(string, TextBlock)>)[("The Git log's commit", Words(Log(window), "Fix the Element tree's Explorer refresh")),
            ("The Explorer's row", Words(window.ExplorerView.Rows(), "Element.cs"))])
        {
            Assert.True(Family(Face(words)) == family, $"{where} '{words.Text}' is drawn in {Family(Face(words))}, not the window's font {family}");
        }
        Ui.Run(window, AppActions.FindAction);
        Control popup = Assert.IsAssignableFrom<Control>(window.Popup);
        TextBlock row = popup.GetVisualDescendants().OfType<TextBlock>().First(t => !string.IsNullOrEmpty(t.Text) && t.IsEffectivelyVisible && t.TextLayout.TextLines.Count > 0);
        Assert.True(Family(Face(row)) == family, $"Find Action's '{row.Text}' is drawn in {Family(Face(row))}, not the window's font {family}");
        Ui.Press(window, "ESCAPE");
    }

    private static ListBox Log(MainWindow window) =>
        window.GitLog.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IReadOnlyList<GraphRow>);

    private static TextBlock Words(Control within, string text)
    {
        TextBlock? found = null;
        Ui.WaitFor(() => (found = within.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == text && t.IsEffectivelyVisible)) is not null,
            $"'{text}' to be shown");
        return found!;
    }

    /// <summary>The face a TextBlock's first run of words is drawn in.</summary>
    private static GlyphTypeface Face(TextBlock words) =>
        words.TextLayout.TextLines.SelectMany(line => line.TextRuns).OfType<ShapedTextRun>().First().GlyphRun.GlyphTypeface;

    /// <summary>The name of the face a family draws ordinary words in.</summary>
    private static string FamilyOf(FontFamily family)
    {
        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out GlyphTypeface? face), $"No face draws {family}");
        return Family(face);
    }

    /// <summary>A face's family as a family of several weights names it, its own family name where it gives none.</summary>
    private static string Family(GlyphTypeface face) => string.IsNullOrEmpty(face.TypographicFamilyName) ? face.FamilyName : face.TypographicFamilyName;
}
