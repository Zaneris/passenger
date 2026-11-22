using Passenger.Editor;

namespace Passenger.UnitTests.Editor;

/// <summary>The editor's part of IntelliJ IDEA's keymap: its keys are IntelliJ IDEA's own on this platform ($default's
/// on Windows and Linux, Mac OS X 10.5+'s on macOS), named in Settings as the platform's.</summary>
public class IntelliJKeymapTests
{
    private static readonly bool Mac = OperatingSystem.IsMacOS();

    [Fact]
    public void The_editors_keys_are_intellij_ideas_own_on_this_platform() => Cases.All(
        ("An editor action has IntelliJ IDEAs keys on this platform", An_editor_action_has_IntelliJ_IDEAs_keys_on_this_platform),
        ("Settings names IntelliJ IDEAs keymap as the platforms", Settings_names_IntelliJ_IDEAs_keymap_as_the_platforms));

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

    private void Settings_names_IntelliJ_IDEAs_keymap_as_the_platforms() =>
        Assert.Equal(Mac ? "IntelliJ IDEA (macOS)" : "IntelliJ IDEA", DefaultKeymaps.NameOf(KeymapPreset.IntelliJ));
}
