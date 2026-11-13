using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.Editor;
using SkiaSharp;

namespace Passenger.Tests;

/// <summary>A character the code font lacks is drawn in the face the platform's own fallback names for it, in the UI's
/// culture, as the system's own apps draw it, whatever faces the process loaded before: Settings' list of installed fonts
/// loads every family, which would otherwise put the first-named family having the script in its place (#1426). An emoji is
/// drawn whole in a colour face, its modifier and joiner with it (#1551).</summary>
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

    /// <summary>A hand with a skin tone, a face, and a woman and a laptop joined, which the code font lacks, are each drawn as
    /// one glyph of a face with colour glyphs, in the editor and in the terminal: a culture's fallback may name a text face
    /// for the first code point alone (fontconfig's Noto Sans Symbols2 for the hand), which leaves the skin tone a missing
    /// glyph's box beside a monochrome hand (#1551). Skipped where the platform names no colour face for an emoji.</summary>
    [AvaloniaFact]
    public void An_emoji_with_its_skin_tone_or_joiner_is_drawn_as_one_colour_glyph_in_the_editor_and_the_terminal()
    {
        string[] emoji = ["\U0001F44D\U0001F3FD", "\U0001F600", "\U0001F469\u200D\U0001F4BB"];
        EditorRig rig = new($"string s = \"{string.Join(' ', emoji)}\";\n");
        Ui.Settle();
        // Never disposed: Skia gives one object for a typeface, which the system's collection may hold.
        SKTypeface? colour = SKFontManager.Default.MatchCharacter(rig.Editor.FontFamily.Name, SKFontStyle.Normal, [FallbackFonts.EmojiLanguage], 0x1F44D);
        Assert.SkipWhen(colour is null || !colour.GetTableTags().Any(tag => ColourTables.Contains(tag)),
            $"No colour emoji face is installed: the platform names {colour?.FamilyName ?? "none"} for U+1F44D in emoji presentation");
        EachOneColourGlyph(emoji, Drawn(rig.Editor), "the editor");
        rig.Window.Close();

        string folder = TestFolders.Create("emoji-terminal-");
        try
        {
            (MainWindow window, Passenger.Terminal.TerminalSession shell) = TerminalTests.Open(folder, OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh");
            window.KeyTextInput(string.Join(' ', emoji));
            Ui.WaitFor(() => shell.Screen.Lines[shell.Screen.CursorRow].Text().Contains(emoji[^1], StringComparison.Ordinal), "the emoji typed at the prompt");
            Ui.Settle();
            DrawingGroup recorded = new();
            using (DrawingContext context = recorded.Open())
            {
                window.TerminalPanel.View.Render(context);
            }
            List<GlyphRun> runs = [];
            Collect(recorded, runs);
            EachOneColourGlyph(emoji, [.. runs], "the terminal");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }

        static void EachOneColourGlyph(string[] emoji, GlyphRun[] drawn, string where)
        {
            foreach (string one in emoji)
            {
                GlyphRun? run = drawn.FirstOrDefault(r => r.Characters.ToString() == one);
                Assert.True(run is not null, $"In {where} no run drew {one} alone: [{string.Join(", ", drawn.Select(r => $"{r.Characters} in {r.GlyphTypeface.PlatformTypeface.FamilyName}"))}]");
                string face = run.GlyphTypeface.PlatformTypeface.FamilyName;
                Assert.True(ColourTables.Any(tag => run.GlyphTypeface.PlatformTypeface.TryGetTable(OpenTypeTag.Parse(Tag(tag)), out ReadOnlyMemory<byte> table) && table.Length > 0),
                    $"In {where} {one} is drawn in {face}, which has no colour glyphs");
                Assert.True(run.GlyphInfos.Count == 1 && run.GlyphInfos[0].GlyphIndex != 0,
                    $"In {where} {one} is drawn in {face} as glyphs [{string.Join(", ", run.GlyphInfos.Select(g => g.GlyphIndex))}], not one glyph of its own: a part of it is missing or drawn apart");
            }
        }
    }

    /// <summary>The tables a face's colour glyphs are in: Google's bitmaps, layered vectors, Apple's bitmaps and SVG.</summary>
    private static readonly uint[] ColourTables = [Packed("CBDT"), Packed("COLR"), Packed("sbix"), Packed("SVG ")];

    private static uint Packed(string tag) => ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

    private static string Tag(uint tag) => new([(char)(tag >> 24), (char)((tag >> 16) & 0xFF), (char)((tag >> 8) & 0xFF), (char)(tag & 0xFF)]);

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
