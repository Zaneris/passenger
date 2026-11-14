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
public sealed partial class WindowFontTests : IDisposable
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

    /// <summary>Both font lists, the window's in Settings › Appearance and the editor's in Settings › Editor, show each name
    /// in its own face in a row of one height, a list row's, whatever that face's lines need.</summary>
    [AvaloniaFact]
    public void Both_font_lists_show_each_name_in_its_own_face_in_rows_of_one_list_rows_height()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            SettingsDialog dialog = window.ShowSettings();
            foreach (string page in (string[])[SettingsDialog.Appearance, SettingsDialog.EditorPage])
            {
                dialog.Page = page;
                Ui.Settle();
                ComboBox family = SettingsTests.Editor<ComboBox>(dialog, "Font family");
                family.IsDropDownOpen = true;
                Ui.Settle();
                ComboBoxItem[] rows = [.. family.GetRealizedContainers().OfType<ComboBoxItem>()];
                Assert.True(rows.Length > 0, $"The font list of {page} showed no rows");
                Assert.True(rows.All(r => Math.Abs(r.Bounds.Height - ListRow.Height) < 0.01),
                    $"The font list of {page} has rows of other heights than {ListRow.Height}: {string.Join(", ", rows.Select(r => $"{r.Content} {r.Bounds.Height:0.##}"))}");
                family.IsDropDownOpen = false;
                Ui.Settle();
            }
            dialog.Close();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>On macOS the system's own UI font chosen sets the window's words as tight as the system's own apps do: the
    /// Explorer's row as wide as CoreText sets its words in the system font at the row's size, within a pixel, the font's own
    /// tracking at that size its letter spacing; Inter chosen again has none.</summary>
    [AvaloniaFact]
    public void On_macos_the_system_font_sets_the_windows_words_as_wide_as_the_systems_own_apps_within_a_pixel()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The tracking the system's own apps set is macOS's text drawing's");
        File.WriteAllText(Path.Combine(folder, "Element.cs"), "class Element { }\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ApplySettings(window.Settings with { WindowFontFamily = WindowFontNames.SystemUi });
            Ui.Settle();
            TextBlock row = Words(window.ExplorerView.Rows(), "Element.cs");
            Ui.WaitFor(() => Family(Face(row)) == FamilyOf(WindowFonts.Named(WindowFontNames.SystemUi)!), "the Explorer's row drawn in the system font");
            double native = NativeWidth(row.Text!, row.FontSize);
            double drawn = row.TextLayout.WidthIncludingTrailingWhitespace;
            Assert.True(row.LetterSpacing != 0, $"The system font's words at {row.FontSize} px have no tracking, where macOS sets {WindowTracking.At(row.FontSize):0.###} px");
            Assert.True(Math.Abs(drawn - native) <= 1, $"'{row.Text}' at {row.FontSize} px is {drawn:0.##} wide in the window and {native:0.##} as the system's own apps set it");

            window.ApplySettings(window.Settings with { WindowFontFamily = null });
            Ui.Settle();
            row = Words(window.ExplorerView.Rows(), "Element.cs");
            Assert.True(row.LetterSpacing == 0, $"Inter's words kept the system font's tracking, {row.LetterSpacing}");
        }
        finally
        {
            window.ApplySettings(window.Settings with { WindowFontFamily = null });
            window.Close();
        }
    }

    /// <summary>On macOS an installed family with tracking of its own at the size its shaper tracks every size at, PingFang
    /// SC, chosen sets the window's words as wide as the system's own apps set them in it, within a pixel: the shaper's
    /// tracking and the window's letter spacing together come to the font's own at the row's size, never to both.</summary>
    [AvaloniaFact]
    public void On_macos_a_font_its_shaper_tracks_sets_the_windows_words_as_wide_as_the_systems_own_apps_within_a_pixel()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The tracking the system's own apps set is macOS's text drawing's");
        const string Chosen = "PingFang SC";
        Assert.SkipWhen(WindowFonts.Named(Chosen) is null, $"{Chosen} is not installed");
        File.WriteAllText(Path.Combine(folder, "Element.cs"), "class Element { }\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ApplySettings(window.Settings with { WindowFontFamily = Chosen });
            Ui.Settle();
            TextBlock row = Words(window.ExplorerView.Rows(), "Element.cs");
            Ui.WaitFor(() => Family(Face(row)) == FamilyOf(WindowFonts.Named(Chosen)!), $"the Explorer's row drawn in {Chosen}");
            double native = NativeWidth(row.Text!, row.FontSize, Chosen);
            double drawn = row.TextLayout.WidthIncludingTrailingWhitespace;
            Assert.True(Math.Abs(drawn - native) <= 1,
                $"'{row.Text}' at {row.FontSize} px in {Chosen} is {drawn:0.##} wide in the window, its letter spacing {row.LetterSpacing:0.###} px, and {native:0.##} as the system's own apps set it");
        }
        finally
        {
            window.ApplySettings(window.Settings with { WindowFontFamily = null });
            window.Close();
        }
    }

    /// <summary>How wide CoreText sets words at a size in an installed family, else in the system's UI font, as the system's
    /// own apps draw them.</summary>
    private static double NativeWidth(string words, double size, string? family = null)
    {
        IntPtr coreText = System.Runtime.InteropServices.NativeLibrary.Load(CoreText);
        IntPtr coreFoundation = System.Runtime.InteropServices.NativeLibrary.Load(CoreFoundation);
        IntPtr fontKey = System.Runtime.InteropServices.Marshal.ReadIntPtr(System.Runtime.InteropServices.NativeLibrary.GetExport(coreText, "kCTFontAttributeName"));
        IntPtr named = family is null ? IntPtr.Zero : CFStringCreateWithCharacters(IntPtr.Zero, family, family.Length);
        IntPtr font = family is null ? CTFontCreateUIFontForLanguage(SystemUiFont, size, IntPtr.Zero) : CTFontCreateWithName(named, size, IntPtr.Zero);
        IntPtr text = CFStringCreateWithCharacters(IntPtr.Zero, words, words.Length);
        IntPtr attributes = CFDictionaryCreate(IntPtr.Zero, [fontKey], [font], 1, System.Runtime.InteropServices.NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks"),
            System.Runtime.InteropServices.NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks"));
        IntPtr attributed = CFAttributedStringCreate(IntPtr.Zero, text, attributes);
        IntPtr line = CTLineCreateWithAttributedString(attributed);
        try
        {
            return CTLineGetTypographicBounds(line, out _, out _, out _);
        }
        finally
        {
            foreach (IntPtr made in (IntPtr[])[line, attributed, attributes, text, font, named])
            {
                if (made != IntPtr.Zero)
                {
                    CFRelease(made);
                }
            }
        }
    }

    private const string CoreText = "/System/Library/Frameworks/CoreText.framework/CoreText";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>kCTFontUIFontSystem: the font of the system's own UI.</summary>
    private const uint SystemUiFont = 2;

    [System.Runtime.InteropServices.LibraryImport(CoreText)]
    private static partial IntPtr CTFontCreateUIFontForLanguage(uint type, double size, IntPtr language);

    [System.Runtime.InteropServices.LibraryImport(CoreText)]
    private static partial IntPtr CTFontCreateWithName(IntPtr name, double size, IntPtr matrix);

    [System.Runtime.InteropServices.LibraryImport(CoreFoundation, StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    private static partial IntPtr CFStringCreateWithCharacters(IntPtr allocator, string characters, nint length);

    [System.Runtime.InteropServices.LibraryImport(CoreFoundation)]
    private static partial IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [System.Runtime.InteropServices.LibraryImport(CoreFoundation)]
    private static partial IntPtr CFAttributedStringCreate(IntPtr allocator, IntPtr text, IntPtr attributes);

    [System.Runtime.InteropServices.LibraryImport(CoreText)]
    private static partial IntPtr CTLineCreateWithAttributedString(IntPtr text);

    [System.Runtime.InteropServices.LibraryImport(CoreText)]
    private static partial double CTLineGetTypographicBounds(IntPtr line, out double ascent, out double descent, out double leading);

    [System.Runtime.InteropServices.LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr made);

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
