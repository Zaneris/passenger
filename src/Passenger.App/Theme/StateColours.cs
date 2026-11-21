using static Passenger.App.ThemeColours;

namespace Passenger.App;

/// <summary>Which colours the states of tests, changes and signatures take: the theme's own, or a palette told apart
/// without red and green.</summary>
public enum StatePalette
{
    Theme,
    ColourBlindSafe,
}

/// <summary>The colour-blind safe palette, laid over the theme in force: passed, added and a good signature in blue, changed
/// and running in orange, failed, a bad signature and a conflict in vermilion, a new file in reddish purple (Okabe and Ito's
/// hues), blue set against the others along the one axis of colour an eye without red or green cones still sees. Each is
/// moved in lightness until it reads on the grounds the theme in force gives as the theme's own must (a word 4.5:1, a mark
/// 3:1, or 7:1 and 4.5:1 under High Contrast), and a diff's washes until the code reads on them. Tokens are named without
/// <c>Passenger.</c>. Pure.</summary>
public static class StateColours
{
    private const uint Blue = 0xFF0072B2;
    private const uint Orange = 0xFFE69F00;
    private const uint Vermilion = 0xFFD55E00;
    private const uint Purple = 0xFFCC79A7;

    /// <summary>The grounds and words of the theme in force the palette is worked out from.</summary>
    public static readonly IReadOnlyList<string> Read = ["Panel", "RowSelected", "Editor.Background", "Editor.Gutter", "Editor.Text"];

    /// <summary>One variant's tokens, from its grounds and words (<see cref="Read"/>) as the theme in force gives them.</summary>
    public static Dictionary<string, uint> Tokens(IReadOnlyDictionary<string, uint> given, bool highContrast)
    {
        double words = highContrast ? 7.1 : 4.6;
        double marks = highContrast ? 4.6 : 3.1;
        uint panel = given["Panel"];
        uint editor = given["Editor.Background"];
        uint[] files = [panel, given["RowSelected"]];
        uint[] gutter = [given["Editor.Gutter"]];
        bool dark = IsDark(editor);
        bool panelDark = IsDark(panel);
        Dictionary<string, uint> t = new()
        {
            ["Test.Passed"] = Legible(Blue, [panel], marks, panelDark),
            ["Test.Failed"] = Legible(Vermilion, [panel], marks, panelDark),
            ["Test.Running"] = Legible(Orange, [panel], marks, panelDark),
            ["Vcs.Added"] = Legible(Blue, gutter, marks, dark),
            ["Vcs.Modified"] = Legible(Orange, gutter, marks, dark),
            ["Vcs.Conflict"] = Legible(Vermilion, gutter, marks, dark),
            ["Vcs.File.Added"] = Legible(Blue, files, words, panelDark),
            ["Vcs.File.Modified"] = Legible(Orange, files, words, panelDark),
            ["Vcs.File.Unversioned"] = Legible(Purple, files, words, panelDark),
            ["Vcs.File.Conflicted"] = Legible(Vermilion, files, words, panelDark),
        };
        t["Vcs.Signature.Good"] = t["Test.Passed"];
        t["Vcs.Signature.Bad"] = t["Test.Failed"];
        uint code = given["Editor.Text"];
        foreach ((string name, uint hue) in ((string, uint)[])[("Added", Blue), ("Modified", Orange), ("Conflict", Vermilion)])
        {
            // A wash the code reads on, moved away from the code's words where it would not.
            t[$"Diff.{name}"] = Legible(Mix(editor, hue, dark ? 0.15 : 0.14), [code], words, !dark);
            t[$"Diff.{name}Word"] = Legible(Mix(editor, hue, dark ? 0.32 : 0.3), [code], words, !dark);
        }
        return t;
    }
}
