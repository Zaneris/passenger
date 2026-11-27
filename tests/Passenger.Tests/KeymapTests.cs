using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Navigation;
using Passenger.App.Settings;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Settings' Keymap: an action's Edit Shortcuts has Add Keyboard Shortcut, whose Keyboard Shortcut dialog
/// takes a chord (the first keystroke, Second stroke, the second), lists the actions it is already assigned to as the
/// keys are pressed (a chord clashing with the key that begins it too) and warns before it is kept, Remove taking it
/// from them; the chord then runs its action from the editor and is kept after a restart, Reset Shortcuts giving an
/// action its own back; Find Actions by Shortcut narrows the page to the actions a chord runs; and the keypad's / is a
/// key of its own beside the main keys' /, named as the keypad's, recorded, kept and run apart from it.</summary>
public sealed class KeymapTests : IDisposable
{
    private const string Source = "class One { }\n";
    private const string Warning = "This shortcut is already assigned to other actions. Do you want to remove the other assignments?";

    /// <summary>The key the platform's chords begin with: Cmd on the Mac, Ctrl elsewhere.</summary>
    private static readonly string Cmd = OperatingSystem.IsMacOS() ? "meta" : "control";

    private readonly string folder = TestFolders.Create("keymap-");
    private readonly string settings = TestFolders.Create("keymap-settings-");

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    /// <summary>Keyboard shortcuts, the gate's checks as one flow: a chord recorded shows as written and runs its
    /// action from the editor, one another action has is listed and warned of and Remove takes it from that action, one
    /// begun by another action's key is a conflict and Cancel keeps the keymap, Find Actions by Shortcut keeps a
    /// keystroke's actions then a chord's, a shortcut set is kept after a restart and Reset gives the action its own
    /// back, and the keypad's slash is its own key.</summary>
    [Gate]
    [AvaloniaFact]
    public void A_shortcut_recorded_or_reset_runs_its_action_and_says_its_conflicts()
    {
        Flow.StepApart<KeymapTests>("a chord recorded in the Keyboard Shortcut dialog shows as Rider shows it and runs its action from the editor", t => t.A_chord_recorded_in_the_Keyboard_Shortcut_dialog_shows_as_Rider_shows_it_and_runs_its_action_from_the_editor());
        Flow.StepApart<KeymapTests>("a chord another action has is listed and warned of and Remove takes it from that action", t => t.A_chord_another_action_has_is_listed_and_warned_of_and_Remove_takes_it_from_that_action());
        Flow.StepApart<KeymapTests>("a chord begun by another action's key is a conflict as in Rider and Cancel keeps the keymap", t => t.A_chord_begun_by_another_actions_key_is_a_conflict_as_in_Rider_and_Cancel_keeps_the_keymap());
        Flow.StepApart<KeymapTests>("Find Actions by Shortcut keeps the actions a keystroke starts and then the one a chord runs", t => t.Find_Actions_by_Shortcut_keeps_the_actions_a_keystroke_starts_and_then_the_one_a_chord_runs());
        Flow.StepApart<KeymapTests>("a shortcut set is kept after a restart and Reset Shortcuts gives an action its own back", t => t.A_shortcut_set_is_kept_after_a_restart_and_Reset_Shortcuts_gives_an_action_its_own_back());
        Flow.StepApart<KeymapTests>("a shortcut recorded on the keypad's slash is named as the keypad's kept and run by it alone", t => t.A_shortcut_recorded_on_the_keypads_slash_is_named_as_the_keypads_kept_and_run_by_it_alone());
        Flow.StepApart<KeymapTests>("the comment actions run from the keypad's slash as from the main keys one each shown as its own key", t => t.The_comment_actions_run_from_the_keypads_slash_as_from_the_main_keys_one_each_shown_as_its_own_key());
    }

    private void A_chord_recorded_in_the_Keyboard_Shortcut_dialog_shows_as_Rider_shows_it_and_runs_its_action_from_the_editor()
    {
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = Settings(window, "split right");
            KeyboardShortcutDialog keys = AddKeyboardShortcut(dialog, AppActions.SplitRight, byItsButton: true);
            Ui.Press(keys, $"{Cmd} K");
            Assert.Equal(Keystroke.Parse($"{Cmd} K").Display(), keys.Keys.First.Text);
            Assert.True(keys.OkButton.IsEnabled, "OK stays disabled with a keystroke pressed");
            Ui.Click(keys.Keys.SecondStroke);
            Assert.True(keys.Keys.Second.IsFocused, "Checking Second stroke did not take the keys to the second keystroke's field");
            Ui.Press(keys, $"{Cmd} J");
            Assert.Equal(Keystroke.Parse($"{Cmd} J").Display(), keys.Keys.Second.Text);
            Assert.True(keys.Conflicts.Count == 0, $"A chord no action has is said to be assigned to: {string.Join(", ", keys.Conflicts)}");
            Ui.Click(keys.OkButton);
            Ui.WaitFor(() => !keys.IsVisible, "the Keyboard Shortcut dialog to close");

            string chord = Shortcut.Parse($"{Cmd} K , {Cmd} J").Display();
            Assert.Equal(OperatingSystem.IsMacOS() ? "⌘K, ⌘J" : "Ctrl+K, Ctrl+J", chord);
            Assert.Equal([chord], dialog.ShortcutsShown(AppActions.SplitRight));
            Assert.True(dialog.ShowsChanged(AppActions.SplitRight), "Split Right, given a shortcut, is not shown as changed");
            Assert.Contains($"Remove {chord}", EditShortcuts(dialog, AppActions.SplitRight));
            Ui.Click(Button(dialog, "OK"));

            window.EditorArea.CodeEditor.Focus();
            Ui.Press(window, $"{Cmd} K");
            Assert.Contains("Split Right", window.Status ?? string.Empty);
            Ui.Press(window, $"{Cmd} J");
            Assert.True(window.EditorArea.Groups.Count == 2, $"{chord}, recorded for Split Right, split nothing");
        }
        finally
        {
            // The keymap in force is the process's; the next test starts on the platform's own.
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>A modifier tapped twice in the Keyboard Shortcut dialog is recorded as its double tap, shown as one, and runs
    /// its action from the editor once kept; the keymap's own double Shift opens Go to Everything, which its tip names, and
    /// two capitals typed, Shift held for each, open nothing.</summary>
    internal void A_double_tap_recorded_runs_its_action_a_double_Shift_or_Ctrl_opens_its_popup_but_two_capitals_or_a_Ctrl_held_for_a_shortcut_do_not()
    {
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = Settings(window, "split right");
            KeyboardShortcutDialog keys = AddKeyboardShortcut(dialog, AppActions.SplitRight);
            Ui.Press(keys, "alt , alt");
            string doubleAlt = Shortcut.Parse("alt , alt").Display();
            Assert.True(keys.Keys.First.Text == doubleAlt && keys.OkButton.IsEnabled,
                $"Alt tapped twice in the dialog left \"{keys.Keys.First.Text}\" in its field and OK {(keys.OkButton.IsEnabled ? "enabled" : "disabled")}, where it records {doubleAlt}");
            Ui.Click(keys.OkButton);
            Ui.WaitFor(() => !keys.IsVisible, "the Keyboard Shortcut dialog to close");
            Assert.Equal([doubleAlt], dialog.ShortcutsShown(AppActions.SplitRight));
            Ui.Click(Button(dialog, "OK"));

            window.EditorArea.CodeEditor.Focus();
            Ui.Press(window, "alt , alt");
            Assert.True(window.EditorArea.Groups.Count == 2, $"{doubleAlt}, recorded for Split Right, split nothing");
            window.Perform(AppActions.Unsplit);
            Ui.Settle();

            string doubleShift = Shortcut.Parse("shift , shift").Display();
            Assert.True(window.Actions.Shortcut(AppActions.GoToEverything) == doubleShift,
                $"Go to Everything's tip names {window.Actions.Shortcut(AppActions.GoToEverything)}, where the keymap's first key for it is {doubleShift}");
            window.EditorArea.CodeEditor.Focus();
            Ui.Press(window, "shift , shift");
            Assert.True(window.Popup is SymbolPopup { Title: "Go to Everything" }, $"{doubleShift} in the Visual Studio keymap opened no Go to Everything");
            Ui.Press(window, "ESCAPE");
            window.EditorArea.CodeEditor.Focus();
            Ui.Press(window, "control , control");
            Assert.True(window.Popup is Passenger.App.Run.RunOffersPopup { Title: "Run Anything" },
                $"{Shortcut.Parse("control , control").Display()} in the Visual Studio keymap opened {window.Popup?.GetType().Name ?? "nothing"}, where it opens Run Anything");
            Ui.Press(window, "ESCAPE");
            window.EditorArea.CodeEditor.Focus();
            // Ctrl tapped, then held at once for a shortcut: a tap and a press, never two taps.
            window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.None, null);
            window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.None, null);
            Ui.Press(window, "control LEFT", settle: false);
            window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.None, null);
            Ui.Settle();
            Assert.True(window.Popup is null, $"Ctrl tapped, then held for Ctrl+Left at once, opened {window.Popup?.GetType().Name}, where a modifier held for a shortcut taps nothing");
            window.EditorArea.CodeEditor.Focus();
            foreach ((Key letter, string text) in new[] { (Key.A, "A"), (Key.B, "B") })
            {
                // Typed as a keyboard types a capital: Shift held down for the letter, then let go.
                window.KeyPress(Key.LeftShift, RawInputModifiers.Shift, PhysicalKey.None, null);
                Ui.Type(window, $"shift {letter}", text, settle: false);
                window.KeyRelease(Key.LeftShift, RawInputModifiers.None, PhysicalKey.None, null);
            }
            Ui.Settle();
            Assert.True(window.Popup is null && Text(window).Contains("AB", StringComparison.Ordinal),
                $"Two capitals typed with Shift held for each left {window.Popup?.GetType().Name ?? "no popup"} open and the text \"{Text(window)}\", where they type AB and open nothing");
        }
        finally
        {
            // The keymap in force is the process's; the next test starts on the platform's own.
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    private void A_chord_another_action_has_is_listed_and_warned_of_and_Remove_takes_it_from_that_action()
    {
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = Settings(window, "split right");
            KeyboardShortcutDialog keys = AddKeyboardShortcut(dialog, AppActions.SplitRight);
            Ui.Click(keys.Keys.SecondStroke);
            Assert.True(keys.Keys.First.IsFocused, "Second stroke checked before any keystroke took the keys from the first field");
            Ui.Press(keys, $"{Cmd} K");
            Assert.True(keys.Keys.Second.IsFocused, "The first keystroke, with Second stroke checked, left the keys in its field");
            Ui.Press(keys, $"{Cmd} C");
            Assert.Equal(["Comment in Editor Actions"], keys.Conflicts);
            Ui.Click(keys.OkButton);

            ConfirmDialog warning = Asked(dialog);
            Ui.Click(Button(warning, "Remove"));
            Ui.WaitFor(() => !warning.IsVisible, "the warning to close");
            string chord = Shortcut.Parse($"{Cmd} K , {Cmd} C").Display();
            Assert.Equal([chord], dialog.ShortcutsShown(AppActions.SplitRight));
            Assert.Equal([Shortcut.Parse($"{Cmd} K , C").Display()], dialog.ShortcutsShown(EditorActions.Comment));
            Ui.Click(Button(dialog, "OK"));

            window.EditorArea.CodeEditor.Focus();
            Ui.Press(window, $"{Cmd} K");
            Ui.Press(window, $"{Cmd} C");
            Assert.True(window.EditorArea.Groups.Count == 2, $"{chord}, taken from Comment for Split Right, split nothing");
            Assert.Equal(Source, Text(window));
            Ui.Press(window, $"{Cmd} K");
            Ui.Press(window, "C");
            Assert.StartsWith("//", Text(window), StringComparison.Ordinal);
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    private void A_chord_begun_by_another_actions_key_is_a_conflict_as_in_Rider_and_Cancel_keeps_the_keymap()
    {
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = Settings(window, "split right");
            string completion = Keystroke.Parse("control J").Display();
            const string Listed = "Basic Completion in Main Menu | Code | Code Completion";

            KeyboardShortcutDialog keys = AddKeyboardShortcut(dialog, AppActions.SplitRight);
            Ui.Press(keys, "control J");
            Assert.Equal([Listed], keys.Conflicts);
            Ui.Click(keys.Keys.SecondStroke);
            Ui.Press(keys, "control K");
            Assert.Equal([Listed], keys.Conflicts);
            Ui.Click(keys.OkButton);
            ConfirmDialog warning = Asked(dialog);
            Ui.Click(Button(warning, "Cancel"));
            Ui.WaitFor(() => !warning.IsVisible, "the warning to close");
            Assert.Empty(dialog.ShortcutsShown(AppActions.SplitRight));
            Assert.Contains(completion, dialog.ShortcutsShown(AppActions.Complete));

            // Remove takes Ctrl+J from Basic Completion, which would have run before the chord's second key.
            keys = AddKeyboardShortcut(dialog, AppActions.SplitRight);
            Ui.Press(keys, "control J");
            Ui.Click(keys.Keys.SecondStroke);
            Ui.Press(keys, "control K");
            Ui.Click(keys.OkButton);
            warning = Asked(dialog);
            Ui.Click(Button(warning, "Remove"));
            Ui.WaitFor(() => !warning.IsVisible, "the warning to close");
            Assert.DoesNotContain(completion, dialog.ShortcutsShown(AppActions.Complete));
            Assert.Equal([Keystroke.Parse("control SPACE").Display()], dialog.ShortcutsShown(AppActions.Complete));
            Ui.Click(Button(dialog, "OK"));

            window.EditorArea.CodeEditor.Focus();
            Ui.Press(window, "control J");
            Assert.StartsWith("Prefix key pressed: ", window.Status);
            Ui.Press(window, "control K");
            Assert.True(window.EditorArea.Groups.Count == 2, "Ctrl+J, Ctrl+K, recorded for Split Right, split nothing");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    private void Find_Actions_by_Shortcut_keeps_the_actions_a_keystroke_starts_and_then_the_one_a_chord_runs()
    {
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = Settings(window, null);
            dialog.Page = SettingsDialog.KeymapPage;
            Ui.Settle();
            int all = dialog.Shown.Count;

            OpenFindShortcut(dialog);
            Ui.Press(dialog, "ESCAPE");
            Assert.True(dialog.IsVisible, "Escape in Find Shortcut closed Settings");
            Assert.True(TopLevel.GetTopLevel(dialog.FindShortcut) is null, "Escape left Find Shortcut open");
            Assert.Equal(all, dialog.Shown.Count);

            OpenFindShortcut(dialog);
            Ui.Press(dialog, $"{Cmd} K");
            Keystroke first = Keystroke.Parse($"{Cmd} K");
            string[] starting = [.. window.Actions.All
                .Where(a => window.Actions.Keymap.ShortcutsFor(a.Id).Any(s => s.First == first))
                .Select(a => a.Title).Order(StringComparer.Ordinal)];
            Assert.Contains("Comment", starting);
            Assert.Contains("Toggle Bookmark", starting);
            Assert.Equal(starting, dialog.Shown.Order(StringComparer.Ordinal));

            Ui.Press(dialog, "TAB");
            Assert.True(dialog.FindShortcut.SecondStroke.IsFocused, "Tab in Find Shortcut's field did not go on to Second stroke");
            Ui.Press(dialog, "SPACE");
            Assert.True(dialog.FindShortcut.Second.IsFocused, "Checking Second stroke did not take the keys to its field");
            Ui.Press(dialog, $"{Cmd} C");
            Assert.Equal(["Comment"], dialog.Shown);
            Assert.Equal(Shortcut.Parse($"{Cmd} K , {Cmd} C"), dialog.ShortcutFilter);

            Control comment = dialog.KeymapRow(EditorActions.Comment);
            Ui.Click(comment, Ui.Centre(((Panel)comment).Children[0], comment));
            Assert.True(TopLevel.GetTopLevel(dialog.FindShortcut) is null, "A click outside left Find Shortcut open");
            Assert.Equal(["Comment"], dialog.Shown);
            Ui.Click(dialog.ClearFiltering);
            Assert.Null(dialog.ShortcutFilter);
            Assert.Equal(all, dialog.Shown.Count);
            dialog.Close();
        }
        finally
        {
            window.Close();
        }
    }

    private void A_shortcut_set_is_kept_after_a_restart_and_Reset_Shortcuts_gives_an_action_its_own_back()
    {
        string chord = Shortcut.Parse($"{Cmd} K , {Cmd} C").Display();
        MainWindow window = Open(keeps: true);
        try
        {
            SettingsDialog dialog = Settings(window, "split right");
            KeyboardShortcutDialog keys = AddKeyboardShortcut(dialog, AppActions.SplitRight);
            Ui.Press(keys, $"{Cmd} K");
            Ui.Click(keys.Keys.SecondStroke);
            Ui.Press(keys, $"{Cmd} C");
            Ui.Click(keys.OkButton);
            ConfirmDialog warning = Asked(dialog);
            Ui.Click(Button(warning, "Remove"));
            Ui.WaitFor(() => !warning.IsVisible, "the warning to close");
            Ui.Click(Button(dialog, "OK"));
        }
        finally
        {
            window.Close();
        }

        MainWindow again = Open(keeps: true);
        try
        {
            again.EditorArea.CodeEditor.Focus();
            Ui.Press(again, $"{Cmd} K");
            Ui.Press(again, $"{Cmd} C");
            Assert.True(again.EditorArea.Groups.Count == 2, $"{chord}, given to Split Right before the restart, split nothing after it");
            Assert.Equal(Source, Text(again));
            again.Perform(AppActions.Unsplit);
            Ui.Settle();

            SettingsDialog dialog = Settings(again, "comment");
            Assert.True(dialog.ShowsChanged(EditorActions.Comment), "Comment, which lost a shortcut, is not shown as changed");
            Ui.ChooseFromMenu(dialog.KeymapRow(EditorActions.Comment), "Reset Shortcuts");
            Assert.Equal([Shortcut.Parse($"{Cmd} K , C").Display(), chord], dialog.ShortcutsShown(EditorActions.Comment));
            Assert.False(dialog.ShowsChanged(EditorActions.Comment), "Comment, reset, is still shown as changed");
            Assert.DoesNotContain("Reset Shortcuts", EditShortcuts(dialog, EditorActions.Comment));
            Assert.Empty(dialog.ShortcutsShown(AppActions.SplitRight));
            Ui.Click(Button(dialog, "OK"));

            again.EditorArea.CodeEditor.Focus();
            Ui.Press(again, $"{Cmd} K");
            Ui.Press(again, $"{Cmd} C");
            Assert.True(again.EditorArea.Groups.Count == 1, $"{chord} still splits after Comment's shortcuts were reset");
            Assert.StartsWith("//", Text(again), StringComparison.Ordinal);
        }
        finally
        {
            again.ApplySettings(new AppSettings());
            again.Close();
        }
    }

    private void A_shortcut_recorded_on_the_keypads_slash_is_named_as_the_keypads_kept_and_run_by_it_alone()
    {
        string keypad = OperatingSystem.IsMacOS() ? "⌘⌨/" : "Ctrl+NumPad /";
        MainWindow window = Open(keeps: true);
        try
        {
            SettingsDialog dialog = Settings(window, "split right");
            KeyboardShortcutDialog keys = AddKeyboardShortcut(dialog, AppActions.SplitRight);
            Ui.Press(keys, $"{Cmd} DIVIDE");
            Assert.Equal(keypad, keys.Keys.First.Text);
            // Comment with Line Comment has the keypad's / beside the main keys' /, as Rider's keymaps give it both.
            Assert.StartsWith("Comment with Line Comment in ", Assert.Single(keys.Conflicts), StringComparison.Ordinal);
            Ui.Click(keys.OkButton);
            ConfirmDialog warning = Asked(dialog);
            Ui.Click(Button(warning, "Remove"));
            Ui.WaitFor(() => !warning.IsVisible, "the warning to close");
            Assert.Equal([keypad], dialog.ShortcutsShown(AppActions.SplitRight));
            IReadOnlyList<string> comment = dialog.ShortcutsShown(EditorActions.ToggleLineComment);
            Assert.True(!comment.Contains(keypad) && comment.Contains(Keystroke.Parse($"{Cmd} SLASH").Display()),
                $"Comment with Line Comment shows [{string.Join(", ", comment)}] once the keypad's / was taken from it, not its main keys' / alone");
            Ui.Click(Button(dialog, "OK"));

            // macOS's menu bar would take a keypad key as its character alone, which the main keys' / presses too.
            NativeMenuItem split = Ui.Submenu(Ui.Submenu(window.MenuBar, "Window"), "Editor Tabs").Items.OfType<NativeMenuItem>().Single(i => i.Header == "Split Right");
            Assert.True(split.Gesture is null, $"The menu bar's Split Right was given {split.Gesture} as its key equivalent, which the main keys' / would press too");
        }
        finally
        {
            window.Close();
        }

        // Kept as Rider's DIVIDE, it is the keypad's key again after a restart, never the main keys' /.
        MainWindow again = Open(keeps: true);
        try
        {
            again.EditorArea.CodeEditor.Focus();
            Ui.Press(again, $"{Cmd} SLASH");
            Assert.True(again.EditorArea.Groups.Count == 1, $"The main keys' / ran Split Right, which {keypad} was recorded for");
            Assert.StartsWith("//", Text(again), StringComparison.Ordinal);
            Ui.Press(again, $"{Cmd} DIVIDE");
            Assert.True(again.EditorArea.Groups.Count == 2, $"{keypad}, recorded for Split Right and kept, split nothing after a restart");
            Assert.StartsWith("//", Text(again), StringComparison.Ordinal);
        }
        finally
        {
            again.ApplySettings(new AppSettings());
            again.Close();
        }
    }

    private void The_comment_actions_run_from_the_keypads_slash_as_from_the_main_keys_one_each_shown_as_its_own_key()
    {
        bool mac = OperatingSystem.IsMacOS();
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = Settings(window, null);
            dialog.Page = SettingsDialog.KeymapPage;
            Ui.Settle();
            string[] comment = mac ? ["⌘/", "⌘⌨/", "⌥⌘/", "⌥⌘⌨/", "⌘K, ⌘/"] : ["Ctrl+/", "Ctrl+NumPad /", "Ctrl+Alt+/", "Ctrl+Alt+NumPad /", "Ctrl+K, Ctrl+/"];
            Assert.Equal(comment, dialog.ShortcutsShown(EditorActions.ToggleLineComment));
            // The side button's gesture after the keys, as the page shows an action's mouse shortcuts.
            string[] back = mac ? ["⌘-", "⌘⌨-", "Back Button"] : ["Ctrl+-", "Ctrl+NumPad -", "Back Button"];
            Assert.Equal(back, dialog.ShortcutsShown(AppActions.Back));
            dialog.Close();

            window.EditorArea.CodeEditor.Focus();
            (string Slash, string Keypad)[] lineComment = mac
                ? [("meta SLASH", "meta DIVIDE"), ("meta alt SLASH", "meta alt DIVIDE")]
                : [("control SLASH", "control DIVIDE"), ("control alt SLASH", "control alt DIVIDE")];
            foreach ((string slash, string keypad) in lineComment)
            {
                Ui.Press(window, keypad);
                Assert.True(Text(window).StartsWith("//", StringComparison.Ordinal), $"{Keystroke.Parse(keypad).Display()} did not comment the line");
                Ui.Press(window, slash);
                Assert.True(Text(window) == Source, $"{Keystroke.Parse(slash).Display()} did not take the comment off again");
            }

            Ui.Press(window, mac ? "control meta SLASH" : "control shift SLASH");
            string commented = Text(window);
            Assert.NotEqual(Source, commented);
            Ui.Press(window, $"{Cmd} Z");
            Assert.Equal(Source, Text(window));
            string blockKeypad = mac ? "meta shift DIVIDE" : "control shift DIVIDE";
            Ui.Press(window, blockKeypad);
            Assert.True(Text(window) == commented, $"{Keystroke.Parse(blockKeypad).Display()} left \"{Text(window)}\", not Block Comment's \"{commented}\"");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Settings as its shortcut opens it, with a search typed into it when one is given.</summary>
    private static SettingsDialog Settings(MainWindow window, string? search)
    {
        Ui.Run(window, AppActions.ShowSettings);
        Ui.WaitFor(() => window.OwnedWindows.OfType<SettingsDialog>().Any(), "Settings to open");
        SettingsDialog dialog = window.OwnedWindows.OfType<SettingsDialog>().Single();
        Ui.Settle();
        if (search is not null)
        {
            dialog.KeyTextInput(search);
            Ui.Settle();
            Assert.Contains(SettingsDialog.KeymapPage, dialog.Pages);
            // Chosen in the list of pages the search left, as a click there chooses it.
            dialog.Page = SettingsDialog.KeymapPage;
            Ui.Settle();
        }
        return dialog;
    }

    /// <summary>Add Keyboard Shortcut, from an action's Edit Shortcuts (its row's right-click, or the ⋮ beside its
    /// shortcuts): its dialog, held to the dialogs' look, once the first keystroke's field has the keys.</summary>
    private static KeyboardShortcutDialog AddKeyboardShortcut(SettingsDialog settings, string action, bool byItsButton = false)
    {
        if (byItsButton)
        {
            Control row = settings.KeymapRow(action);
            Button edit = row.GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) as string == "Edit Shortcuts");
            Ui.Click(edit);
            ContextMenu menu = row.ContextMenu!;
            Assert.True(menu.IsOpen, "The ⋮ beside an action's shortcuts opened no Edit Shortcuts menu");
            Ui.Choose(menu, "Add Keyboard Shortcut");
        }
        else
        {
            Ui.ChooseFromMenu(settings.KeymapRow(action), "Add Keyboard Shortcut");
        }
        Ui.WaitFor(() => settings.OwnedWindows.OfType<KeyboardShortcutDialog>().Any(w => w.IsVisible), "the Keyboard Shortcut dialog");
        KeyboardShortcutDialog dialog = settings.OwnedWindows.OfType<KeyboardShortcutDialog>().Single(w => w.IsVisible);
        Assert.Equal("Keyboard Shortcut", dialog.Title);
        Ui.WaitFor(() => dialog.Keys.First.IsFocused, "the first keystroke's field to take the keys");
        return DialogButtonsTests.Check(dialog);
    }

    /// <summary>Find Actions by Shortcut clicked: its popup, once its first field has the keys.</summary>
    private static void OpenFindShortcut(SettingsDialog settings)
    {
        Ui.Click(settings.FindByShortcut);
        Ui.WaitFor(() => settings.FindShortcut.First.IsFocused, "Find Shortcut's field to take the keys");
    }

    /// <summary>The warning given before a shortcut is taken from other actions.</summary>
    private static ConfirmDialog Asked(SettingsDialog settings)
    {
        Ui.WaitFor(() => settings.OwnedWindows.OfType<ConfirmDialog>().Any(w => w.IsVisible), "the warning that the shortcut is assigned to other actions");
        ConfirmDialog warning = settings.OwnedWindows.OfType<ConfirmDialog>().Single(w => w.IsVisible);
        Assert.Equal("Warning", warning.Title);
        Assert.Contains(warning.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Warning);
        return DialogButtonsTests.Check(warning);
    }

    /// <summary>An action's Edit Shortcuts menu, by its items' headers, as a right-click opens it.</summary>
    private static string[] EditShortcuts(SettingsDialog settings, string action)
    {
        Control row = settings.KeymapRow(action);
        row.RaiseEvent(new ContextRequestedEventArgs());
        Ui.Settle();
        ContextMenu menu = row.ContextMenu!;
        string[] items = [.. menu.Items.OfType<MenuItem>().Select(i => i.Header as string ?? string.Empty)];
        menu.Close();
        Ui.Settle();
        return items;
    }

    private static Button Button(Window window, string content) =>
        window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == content);

    private static string Text(MainWindow window) => window.EditorArea.CodeEditor.Document.Snapshot.ToString();

    private MainWindow Open(bool keeps = false)
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), Source);
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.OpenFolder(folder);
        if (keeps)
        {
            window.KeepSettings();
        }
        window.OpenFile(Path.Combine(folder, "One.cs"));
        Ui.Settle();
        return window;
    }
}
