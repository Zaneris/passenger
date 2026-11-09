using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.Editor;
using SkiaSharp;

namespace Passenger.Tests;

/// <summary>A character the code font lacks is drawn in the face the platform's own fallback names for it, in the UI's
/// culture, as the system's own apps draw it, whatever faces the process loaded before: Settings' list of installed fonts
/// loads every family, which would otherwise put the first-named family having the script in its place (#1426).</summary>
public sealed class FontFallbackTests
{
    /// <summary>Arabic, Hebrew, Devanagari and Japanese in strings, typed once Settings' list of installed monospaced fonts
    /// has loaded every family, are each drawn in the face the platform names for the character.</summary>
    [AvaloniaFact]
    public void Text_the_code_font_lacks_is_drawn_in_the_platforms_own_fallback_after_settings_loaded_every_font()
    {
        Ui.Await(CodeFonts.InstalledAsync(), "the installed monospaced fonts, which loads every family");
        string[] samples = ["الحساب", "שלום", "नमस्ते", "日本語"];
        EditorRig rig = new(string.Join('\n', samples.Select((sample, i) => $"string s{i} = \"{sample}\";")) + "\n");
        Ui.Settle();
        string family = rig.Editor.FontFamily.Name;
        GlyphRun[] drawn = Drawn(rig.Editor);
        int compared = 0;
        foreach (string sample in samples)
        {
            GlyphRun[] runs = [.. drawn.Where(run => run.Characters.Span.IndexOfAny(sample.AsSpan()) >= 0)];
            Assert.True(runs.Length > 0, $"No row drew '{sample}', so what face it takes goes unchecked");
            foreach (GlyphRun run in runs)
            {
                Rune first = Rune.GetRuneAt(run.Characters.ToString(), 0);
                // A script no installed font has is left to Avalonia's own search: nothing here says which face is right.
                if (PlatformFace(first.Value, family) is not { } platform)
                {
                    continue;
                }
                string face = run.GlyphTypeface.PlatformTypeface.FamilyName;
                Assert.True(face == platform,
                    $"'{run.Characters}' of '{sample}' is drawn in {face}, where the platform's fallback names {platform} for U+{first.Value:X4} in {CultureInfo.CurrentUICulture.Name}: the code font's fallback hangs on which fonts the process loaded");
                compared++;
            }
        }
        Assert.True(compared > 0, $"The platform names no face for any of {string.Join(", ", samples)}, so no run's face was checked");
    }

    /// <summary>The family the platform's own fallback names for a character asked by a family, in the UI's culture; null for none.</summary>
    private static string? PlatformFace(int codepoint, string family) =>
        SKFontManager.Default.MatchCharacter(family, SKFontStyle.Normal, [CultureInfo.CurrentUICulture.Name], codepoint)?.FamilyName;

    /// <summary>The glyph runs the editor's rows draw (its own visuals, no control's), each recorded as it draws.</summary>
    private static GlyphRun[] Drawn(CodeEditor editor)
    {
        List<GlyphRun> runs = [];
        foreach (Visual visual in editor.GetVisualDescendants().Where(visual => visual is not Control))
        {
            DrawingGroup recorded = new();
            using (DrawingContext context = recorded.Open())
            {
                visual.Render(context);
            }
            Collect(recorded, runs);
        }
        return [.. runs];
    }

    private static void Collect(Drawing drawing, List<GlyphRun> runs)
    {
        switch (drawing)
        {
            case GlyphRunDrawing { GlyphRun: { } run }:
                runs.Add(run);
                break;
            case DrawingGroup group:
                foreach (Drawing child in group.Children)
                {
                    Collect(child, runs);
                }
                break;
        }
    }
}
