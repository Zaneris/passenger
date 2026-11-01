using System.Text.Json.Nodes;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>IntelliJ IDEA's keymap of the IDE's actions, read as a person's settings choose it and with no window: its
/// keys are IntelliJ IDEA's own on this platform ($default's elsewhere, Mac OS X 10.5+'s on macOS), Search Everywhere's a
/// double Shift, a person's shortcuts over it are kept apart from the other keymaps' and read back as they were written,
/// and a settings.json that names no keymap keeps Visual Studio's. The editor's part is the unit tests'.</summary>
[Collection(Beside.Name)]
public sealed class IntelliJKeymapTests
{
    private static readonly bool Mac = OperatingSystem.IsMacOS();

    /// <summary>The keymap with nothing of the person's over it.</summary>
    private static Keymap Plain => AppActions.Keymap(KeymapPreset.IntelliJ, new Dictionary<string, string[]>());

    [Fact]
    public void IntelliJ_IDEAs_keymap_gives_its_actions_IntelliJ_IDEAs_keys_on_this_platform()
    {
        (string Action, string Name, string[] Others, string[] MacOS)[] keys =
        [
            (AppActions.GoToDeclaration, "Go to Declaration", ["control B"], ["meta B"]),
            // Go to Symbol finds types too, so it takes Go to Class's keys beside its own.
            (AppActions.GoToSymbol, "Go to Class or Symbol", ["control N", "control alt shift N"], ["meta O", "meta alt O"]),
            (AppActions.GoToFile, "Go to File", ["control shift N"], ["meta shift O"]),
            (AppActions.FindUsages, "Find Usages", ["alt F7"], ["alt F7"]),
            (AppActions.FindAction, "Find Action", ["control shift A"], ["meta shift A"]),
            (AppActions.Rename, "Rename", ["shift F6"], ["shift F6"]),
            (AppActions.QuickFix, "Show Context Actions", ["alt ENTER"], ["alt ENTER"]),
            (AppActions.Reformat, "Reformat Code", ["control alt L"], ["meta alt L"]),
            (AppActions.RunProgram, "Run", ["shift F10"], ["control R"]),
            (AppActions.Debug, "Debug", ["shift F9"], ["control D"]),
            (AppActions.StepOver, "Step Over", ["F8"], ["F8"]),
            (AppActions.SaveAll, "Save All", ["control S"], ["meta S"]),
            (AppActions.Save, "Save", [], []),
            (AppActions.GoToEverything, "Search Everywhere", ["shift , shift"], ["shift , shift"]),
        ];
        Keymap keymap = Plain;
        foreach ((string action, string name, string[] others, string[] macOS) in keys)
        {
            string[] ours = [.. keymap.ShortcutsFor(action).Select(s => s.Notation()).Order(StringComparer.Ordinal)];
            string[] theirs = [.. (Mac ? macOS : others).Select(k => Shortcut.Parse(k).Notation()).Order(StringComparer.Ordinal)];
            Assert.True(ours.SequenceEqual(theirs),
                $"In IntelliJ IDEA's keymap {name} runs from [{string.Join("; ", ours)}], where IntelliJ IDEA's own gives it [{string.Join("; ", theirs)}]");
        }
    }

    /// <summary>A shortcut set over IntelliJ IDEA's keymap is kept in its own list, never Visual Studio's or VS Code's, runs
    /// its action there and takes the key from the action that had it; chosen again, Visual Studio's keymap has none of it.</summary>
    [Fact]
    public void A_shortcut_set_over_IntelliJ_IDEAs_keymap_is_its_alone()
    {
        string goToFile = Mac ? "meta shift O" : "control shift N";
        AppSettings settings = new AppSettings { Keymap = KeymapPreset.IntelliJ }
            .WithOwnShortcuts(new Dictionary<string, string[]> { [AppActions.SplitDown] = [goToFile] });
        Assert.Equal([goToFile], settings.IntelliJShortcuts[AppActions.SplitDown]);
        Assert.True(settings.Shortcuts.Count == 0 && settings.VSCodeShortcuts.Count == 0,
            "A shortcut set over IntelliJ IDEA's keymap was kept with Visual Studio's or VS Code's too");
        Assert.Same(settings.IntelliJShortcuts, settings.OwnShortcuts());

        Keymap keymap = AppActions.Keymap(settings);
        Shortcut key = Shortcut.Parse(goToFile);
        Assert.Equal(AppActions.SplitDown, keymap.ActionFor(key));
        Assert.Empty(keymap.ShortcutsFor(AppActions.GoToFile));

        AppSettings visualStudio = settings with { Keymap = KeymapPreset.VisualStudio };
        Assert.Empty(visualStudio.OwnShortcuts());
        Assert.NotEqual(AppActions.SplitDown, AppActions.Keymap(visualStudio).ActionFor(key));
    }

    /// <summary>settings.json's intelliJShortcuts reads back as it was written: a shortcut kept under an action's former id
    /// is the action's now, a null list is passed over, and a settings.json that names no keymap is Visual Studio's.</summary>
    [Fact]
    public void IntelliJ_IDEAs_shortcuts_read_back_from_settings_as_they_were_kept()
    {
        Assert.Equal(KeymapPreset.VisualStudio, new AppSettings().Keymap);
        string former = AppActions.Formerly.First(f => f.Value == AppActions.ServiceDebug).Key;
        JsonObject kept = new()
        {
            ["intelliJShortcuts"] = new JsonObject { [former] = new JsonArray("control alt shift Y"), [AppActions.Stop] = null },
        };
        AppSettings.Renamed(kept);
        JsonObject shortcuts = kept["intelliJShortcuts"]!.AsObject();
        Assert.True(shortcuts.ContainsKey(AppActions.ServiceDebug) && !shortcuts.ContainsKey(former),
            $"intelliJShortcuts kept {former}'s shortcut under its former id, where it is {AppActions.ServiceDebug}'s now");

        AppSettings read = new AppSettings
        {
            Keymap = KeymapPreset.IntelliJ,
            IntelliJShortcuts = new() { [former] = ["control alt shift U"], [AppActions.Stop] = null! },
        }.Clamped();
        Assert.Equal(["control alt shift U"], read.IntelliJShortcuts[AppActions.ServiceDebug]);
        Assert.False(read.IntelliJShortcuts.ContainsKey(AppActions.Stop), "A null list of shortcuts in intelliJShortcuts was read as Stop's");
    }
}
