using Passenger.Editor;

namespace Passenger.UnitTests.Editor;

/// <summary>The editor's part of IntelliJ IDEA's keymap: its keys are IntelliJ IDEA's own on this platform ($default's
/// on Windows and Linux, Mac OS X 10.5+'s on macOS), named in Settings as the platform's; and of Rider's Emacs keymap,
/// $default's with Emacs's own lists over them on every system.</summary>
public class IntelliJKeymapTests
{
    private static readonly bool Mac = OperatingSystem.IsMacOS();

    [Fact]
    public void The_editors_keys_are_intellij_ideas_own_on_this_platform() => Cases.All(
        ("An editor action has IntelliJ IDEAs keys on this platform", An_editor_action_has_IntelliJ_IDEAs_keys_on_this_platform),
        ("Settings names IntelliJ IDEAs keymap as the platforms", Settings_names_IntelliJ_IDEAs_keymap_as_the_platforms),
        ("An editor action has Emacss own keys over defaults on every system", An_editor_action_has_Emacss_own_keys_over_defaults_on_every_system));

    private void An_editor_action_has_IntelliJ_IDEAs_keys_on_this_platform() => Cases.Each<(string action, string others, string macOS)>(
    [
        (EditorActions.ToggleLineComment, "control SLASH; control DIVIDE", "meta SLASH; meta DIVIDE"),
        (EditorActions.DeleteLine, "control Y", "meta BACK_SPACE"),
        (EditorActions.Redo, "control shift Z; alt shift BACK_SPACE", "meta shift Z; alt shift BACK_SPACE"),
        (EditorActions.MoveLineUp, "alt shift UP", "alt shift UP"),
        (EditorActions.StartNewLineBefore, "control alt ENTER", "meta alt ENTER"),
        (EditorActions.CloneCaretAbove, "", ""),
    ], row =>
    {
        (string action, string others, string macOS) = row;
        string[] ours = [.. DefaultKeymaps.For(KeymapPreset.IntelliJ).ShortcutsFor(action).Select(s => s.Notation()).Order(StringComparer.Ordinal)];
        string[] theirs = [.. (Mac ? macOS : others).Split("; ", StringSplitOptions.RemoveEmptyEntries).Select(k => Shortcut.Parse(k).Notation()).Order(StringComparer.Ordinal)];
        Assert.True(ours.SequenceEqual(theirs),
            $"In IntelliJ IDEA's keymap {action} runs from [{string.Join("; ", ours)}], where IntelliJ IDEA's own gives it [{string.Join("; ", theirs)}]");
    });

    /// <summary>Emacs's own list in place of $default's, its chords too; a key it takes from another action leaves that one
    /// (Ctrl+/ is Undo's, not Comment's); an action it clears has no keys.</summary>
    private void An_editor_action_has_Emacss_own_keys_over_defaults_on_every_system() => Cases.Each<(string action, string keys)>(
    [
        (EditorActions.Escape, "ESCAPE , ESCAPE; control G"),
        (EditorActions.Undo, "shift control MINUS; control SLASH"),
        (EditorActions.ToggleLineComment, "alt SEMICOLON"),
        (EditorActions.SelectAll, "control X , H"),
        (EditorActions.Duplicate, "meta D"),
        (EditorActions.Delete, "DELETE; control D"),
        (EditorActions.ToggleCase, "control shift U"),
        (EditorActions.ScrollUp, ""),
    ], row =>
    {
        (string action, string keys) = row;
        string[] ours = [.. DefaultKeymaps.For(KeymapPreset.Emacs).ShortcutsFor(action).Select(s => s.Notation()).Order(StringComparer.Ordinal)];
        string[] theirs = [.. keys.Split("; ", StringSplitOptions.RemoveEmptyEntries).Select(k => Shortcut.Parse(k).Notation()).Order(StringComparer.Ordinal)];
        Assert.True(ours.SequenceEqual(theirs), $"In Emacs's keymap {action} runs from [{string.Join("; ", ours)}], where Rider's Emacs gives it [{string.Join("; ", theirs)}]");
        Assert.Equal("Emacs", DefaultKeymaps.NameOf(KeymapPreset.Emacs));
    });

    private void Settings_names_IntelliJ_IDEAs_keymap_as_the_platforms() =>
        Assert.Equal(Mac ? "IntelliJ IDEA (macOS)" : "IntelliJ IDEA", DefaultKeymaps.NameOf(KeymapPreset.IntelliJ));
}
