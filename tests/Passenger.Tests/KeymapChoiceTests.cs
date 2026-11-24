using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Navigation;
using Passenger.App.Settings;
using Passenger.App.Telemetry;
using Passenger.App.ToolWindows;
using Passenger.Editor;
using Passenger.Terminal;

namespace Passenger.Tests;

/// <summary>Settings' Keymap offers Rider's VS Code keymap, IntelliJ IDEA's and its Emacs keymap beside its Visual Studio
/// one, each the platform's: the choice runs its keys in the editor, the IDE and the terminal at once, is kept in settings.json and after
/// a restart, and a settings.json without one keeps Visual Studio's keys and the person's own over them. Shortcuts set
/// over one keymap stay with it, Reset Shortcuts gives back the chosen keymap's own, a shortcut another action has is
/// warned of as ever, no key of any keymap runs two actions, and the keys checked against Rider's keymaps are those they
/// resolve to.</summary>
[AboutMacOS]
public sealed class KeymapChoiceTests : IDisposable
{
    private const string Source = "class One { int item; int Two() => item + item; }\n";

    /// <summary>The key both keymaps' Ctrl keys are Cmd on the Mac.</summary>
    private static readonly string Cmd = OperatingSystem.IsMacOS() ? "meta" : "control";

    private static readonly string VisualStudio = DefaultKeymaps.NameOf(KeymapPreset.VisualStudio);
    private static readonly string VSCode = DefaultKeymaps.NameOf(KeymapPreset.VSCode);
    private static readonly string IntelliJ = DefaultKeymaps.NameOf(KeymapPreset.IntelliJ);
    private static readonly string Emacs = DefaultKeymaps.NameOf(KeymapPreset.Emacs);

    private readonly string folder = TestFolders.Create("keymaps-");
    private readonly string settings = TestFolders.Create("keymaps-settings-");

    public void Dispose()
    {
        // The keymap in force is the process's: whatever a test here left chosen, failing or not, the next starts on the platform's.
        AppActions.Choose(KeymapPreset.VisualStudio, new Dictionary<string, string[]>());
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    /// <summary>A keymap chosen in Settings (VS Code's, IntelliJ IDEA's, Emacs's) runs its keys in the editor and the IDE and is
    /// kept after a restart, shortcuts set over it stay with it until Reset, each keymap's keys are those its source
    /// resolves them to, the menus name them, and the mouse's gestures are the keymap's, one added in Settings saying
    /// its conflict and kept, as a double Shift recorded runs its action.</summary>
    [AvaloniaFact]
    public void A_keymap_chosen_runs_its_keys_everywhere_names_them_and_is_kept()
    {
        Flow.StepApart<KeymapChoiceTests>("VS Code chosen in Settings runs its keys in the editor and the IDE and is kept after a restart", t => t.VS_Code_chosen_in_Settings_runs_its_keys_in_the_editor_and_the_IDE_and_is_kept_after_a_restart());
        Flow.StepApart<KeymapChoiceTests>("IntelliJ IDEA chosen in Settings runs its keys keeps its own shortcut and is kept after a restart", t => t.IntelliJ_IDEA_chosen_in_Settings_runs_its_keys_keeps_its_own_shortcut_and_is_kept_after_a_restart());
        Flow.StepApart<IntelliJKeymapTests>("IntelliJ IDEA's keymap gives its actions IntelliJ IDEA's keys on this platform", t => t.IntelliJ_IDEAs_keymap_gives_its_actions_IntelliJ_IDEAs_keys_on_this_platform());
        Flow.StepApart<KeymapChoiceTests>("Emacs chosen in Settings runs its keys and chords keeps its own shortcut and is kept after a restart", t => t.Emacs_chosen_in_Settings_runs_its_keys_and_chords_keeps_its_own_shortcut_and_is_kept_after_a_restart());
        Flow.StepApart<IntelliJKeymapTests>("IntelliJ IDEA's shortcuts read back from settings as they were kept", t => t.IntelliJ_IDEAs_shortcuts_read_back_from_settings_as_they_were_kept());
        Flow.StepApart<KeymapChoiceTests>("shortcuts set over one keymap stay with it and Reset Shortcuts gives back the chosen keymap's own", t => t.Shortcuts_set_over_one_keymap_stay_with_it_and_Reset_Shortcuts_gives_back_the_chosen_keymaps_own());
        Flow.StepApart<KeymapChoiceTests>("each keymap's keys for Duplicate Parameter Info Toggle Case and Generate are those Rider's keymap resolves them to", t => t.Each_keymaps_keys_for_Duplicate_Parameter_Info_Toggle_Case_and_Generate_are_those_Riders_keymap_resolves_them_to());
        Flow.StepApart<KeymapChoiceTests>("the menus and the tool windows' buttons name the chosen keymap's keys", t => t.The_menus_and_the_tool_windows_buttons_name_the_chosen_keymaps_keys());
        Flow.StepApart<KeymapChoiceTests>("the mouse's caret and column gestures are the keymap's in force", t => t.The_mouses_caret_and_column_gestures_are_the_keymaps_in_force());
        Flow.StepApart<MouseShortcutSettingsTests>("a mouse shortcut added in Settings says its conflict first runs its action and is kept after a restart", t => t.A_mouse_shortcut_added_in_Settings_says_its_conflict_first_runs_its_action_and_is_kept_after_a_restart());
        Flow.StepApart<MouseShortcutSettingsTests>("a settings json of keys alone keeps the keymap's gestures for the actions it names", t => t.A_settings_json_of_keys_alone_keeps_the_keymaps_gestures_for_the_actions_it_names());
        Flow.StepApart<KeymapTests>("a double Shift recorded in the Keyboard Shortcut dialog runs its action from the editor", t => t.A_double_Shift_recorded_in_the_Keyboard_Shortcut_dialog_runs_its_action_from_the_editor());
    }

    /// <summary>Each key reaches what it is meant for where it is pressed: the terminal and a floating terminal keep
    /// the shell's keys, a floating tool window runs the keymap chosen, IntelliJ IDEA's keys act by place (Duplicate,
    /// Generate, View Breakpoints), Copy copies what the mouse chose in the consoles, a chord's first key is forgotten
    /// after a click, a focus move or two seconds, and on Windows AltGr types its character.</summary>
    [AvaloniaFact]
    public void Each_key_reaches_what_it_is_meant_for_in_the_place_it_is_pressed()
    {
        Flow.StepApart<KeymapChoiceTests>("in VS Code's keymap the terminal keeps what is typed and its key goes back to the editor", t => t.In_VS_Codes_keymap_the_terminal_keeps_what_is_typed_and_its_key_goes_back_to_the_editor());
        Flow.StepApart<KeymapChoiceTests>("a floating tool window runs the keymap chosen and its search box keeps what is typed", t => t.A_floating_tool_window_runs_the_keymap_chosen_and_its_search_box_keeps_what_is_typed());
        Flow.StepApart<KeymapChoiceTests>("a floating terminal keeps the shell's keys and lets the IDE's through", t => t.A_floating_terminal_keeps_the_shells_keys_and_lets_the_IDEs_through());
        Flow.StepApart<KeymapChoiceTests>("Duplicate's key duplicates the line in the editor and compares files in the Explorer", t => t.Duplicates_key_duplicates_the_line_in_the_editor_and_compares_files_in_the_Explorer());
        Flow.StepApart<IntelliJPlaceKeysTests>("Generate's key in the Explorer opens New Item and in the editor does not", t => t.Generates_key_in_the_Explorer_opens_New_Item_and_in_the_editor_does_not());
        Flow.StepApart<IntelliJPlaceKeysTests>("View Breakpoints' key on a breakpoint's line edits it and elsewhere lists the breakpoints", t => t.View_Breakpoints_key_on_a_breakpoints_line_edits_it_and_elsewhere_lists_the_breakpoints());
        Flow.StepApart<MouseSelectionTests>("every key a keymap gives Copy copies what the mouse selected in the Run and Debug consoles", t => t.Every_key_a_keymap_gives_Copy_copies_what_the_mouse_selected_in_the_Run_and_Debug_consoles());
        Flow.StepApart<ShortcutTests>("a click after a chord's first key forgets it so the next letter is typed not a comment", t => t.A_click_after_a_chords_first_key_forgets_it_so_the_next_letter_is_typed_not_a_comment());
        Flow.StepApart<ShortcutTests>("a chord's first key is forgotten when the focus moves and after two seconds as in Rider", t => t.A_chords_first_key_is_forgotten_when_the_focus_moves_and_after_two_seconds_as_in_Rider());
        Flow.StepApart<ShortcutTests>("on Windows AltGr types its letter where the layout has one and Ctrl Alt runs its shortcut where it has none", t => t.On_Windows_AltGr_types_its_letter_where_the_layout_has_one_and_Ctrl_Alt_runs_its_shortcut_where_it_has_none());
        Flow.StepApart<ShortcutTests>("on Windows AltGr types its character into the terminal rather than Ctrl and Alt for the shell", t => t.On_Windows_AltGr_types_its_character_into_the_terminal_rather_than_Ctrl_and_Alt_for_the_shell());
    }

    private void VS_Code_chosen_in_Settings_runs_its_keys_in_the_editor_and_the_IDE_and_is_kept_after_a_restart()
    {
        // A person's settings.json from before there was a choice: Split Right given a key of their own.
        string ownKey = $"{Cmd} alt shift J";
        File.WriteAllText(Path.Combine(settings, "settings.json"),
            $"{{\n  \"version\": {AppSettings.Upgrades.Count},\n  \"shortcuts\": {{ \"{AppActions.SplitRight}\": [\"{ownKey}\"] }}\n}}\n");
        MainWindow window = Open(keeps: true);
        try
        {
            Assert.Equal(KeymapPreset.VisualStudio, window.Settings.Keymap);
            Ui.Press(window, ownKey);
            Assert.True(window.EditorArea.Groups.Count == 2, $"{ownKey}, the person's own key for Split Right before keymaps could be chosen, split nothing");
            window.Perform(AppActions.Unsplit);
            Ui.Settle();
            Ui.Press(window, $"{Cmd} shift T");
            Assert.True(window.Popup is GoToFilePopup, "Visual Studio's Go to File key opened no Go to File");
            Ui.Press(window, "ESCAPE");

            SettingsDialog dialog = Settings(window);
            Assert.Equal(VisualStudio, dialog.KeymapChoice.SelectedItem);
            Assert.Equal([Shortcut.Parse($"{Cmd} shift T").Display()], dialog.ShortcutsShown(AppActions.GoToFile));
            Choose(dialog, VSCode);
            Assert.Equal([Shortcut.Parse($"{Cmd} P").Display()], dialog.ShortcutsShown(AppActions.GoToFile));
            Assert.Equal([Shortcut.Parse($"{Cmd} BACK_SLASH").Display()], dialog.ShortcutsShown(AppActions.SplitRight));
            Assert.False(dialog.ShowsChanged(AppActions.SplitRight), "Split Right, the person's in Visual Studio's keymap alone, is shown changed in VS Code's");
            Ui.Click(Button(dialog, "OK"));
            Assert.Equal(KeymapPreset.VSCode, window.Settings.Keymap);

            VSCodeKeysWork(window);

            string file = Path.Combine(settings, "settings.json");
            Ui.WaitFor(() => Read(file) is { } kept && (string?)kept["keymap"] == "VSCode", "the keymap chosen in settings.json", TimeSpan.FromSeconds(10));
            Assert.Equal(ownKey, (string?)Read(file)!["shortcuts"]![AppActions.SplitRight]![0]);
        }
        finally
        {
            window.Close();
        }

        MainWindow again = Open(keeps: true);
        try
        {
            Assert.Equal(KeymapPreset.VSCode, again.Settings.Keymap);
            VSCodeKeysWork(again);
            again.ApplySettings(again.Settings with { Keymap = KeymapPreset.VisualStudio });
            Ui.Settle();
            again.EditorArea.CodeEditor.Focus();
            Ui.Press(again, ownKey);
            Assert.True(again.EditorArea.Groups.Count == 2, $"{ownKey}, Split Right's in Visual Studio's keymap, split nothing once it was chosen again");
        }
        finally
        {
            // The keymap in force is the process's; the next test starts on the platform's own.
            again.ApplySettings(new AppSettings());
            again.Close();
        }
    }

    /// <summary>IntelliJ IDEA's keymap chosen in Settings runs its keys in the editor and the IDE, the menus and Find Action
    /// name them (Search Everywhere's double Shift too), a shortcut set over it is kept with it alone in settings.json, and
    /// after a restart it is still chosen with that shortcut, which Reset Shortcuts takes back to the keymap's own.</summary>
    private void IntelliJ_IDEA_chosen_in_Settings_runs_its_keys_keeps_its_own_shortcut_and_is_kept_after_a_restart()
    {
        string ownKey = $"{Cmd} alt shift Y";
        string goToFile = OperatingSystem.IsMacOS() ? "meta shift O" : "control shift N";
        MainWindow window = Open(keeps: true);
        try
        {
            SettingsDialog dialog = Settings(window);
            Choose(dialog, IntelliJ);
            Assert.Equal([Shortcut.Parse(goToFile).Display()], dialog.ShortcutsShown(AppActions.GoToFile));
            KeyboardShortcutDialog keys = AddKeyboardShortcut(dialog, AppActions.SplitDown);
            Ui.Press(keys, ownKey);
            Assert.Empty(keys.Conflicts);
            Ui.Click(keys.OkButton);
            Ui.WaitFor(() => dialog.ShortcutsShown(AppActions.SplitDown).Count == 1, "Split Down to show the shortcut given it");
            Assert.True(dialog.ShowsChanged(AppActions.SplitDown), "Split Down, given a shortcut over IntelliJ IDEA's keymap, is not shown as changed");
            Ui.Click(Button(dialog, "OK"));
            Assert.Equal(KeymapPreset.IntelliJ, window.Settings.Keymap);
            Assert.True(window.Settings.Shortcuts.Count == 0 && window.Settings.VSCodeShortcuts.Count == 0,
                "A shortcut set over IntelliJ IDEA's keymap was kept over Visual Studio's or VS Code's too");

            IntelliJKeysWork(window, ownKey);
            NativeMenuItem goToFileItem = Ui.Submenu(window.MenuBar, "Navigate").Items.OfType<NativeMenuItem>().First(i => i.Header == window.Actions[AppActions.GoToFile]!.Title);
            Keystroke intelliJs = Keystroke.Parse(goToFile);
            Assert.Equal(new KeyGesture(intelliJs.Key, intelliJs.Modifiers), goToFileItem.Gesture);
            Ui.Press(window, $"{Cmd} shift A");
            ActionPopup findAction = Assert.IsType<ActionPopup>(window.Popup);
            findAction.Query = window.Actions[AppActions.GoToEverything]!.Title;
            Ui.Settle();
            string doubleShift = Shortcut.Parse("shift , shift").Display();
            Assert.True(findAction.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == doubleShift && t.IsEffectivelyVisible),
                $"Find Action shows Search Everywhere without {doubleShift}, its keys in IntelliJ IDEA's keymap");
            Ui.Press(window, "ESCAPE");

            string file = Path.Combine(settings, "settings.json");
            Ui.WaitFor(() => Read(file) is { } kept && (string?)kept["keymap"] == "IntelliJ", "the keymap chosen in settings.json", TimeSpan.FromSeconds(10));
            Assert.Equal(Shortcut.Parse(ownKey).Notation(), (string?)Read(file)!["intelliJShortcuts"]![AppActions.SplitDown]![0]);
        }
        finally
        {
            window.Close();
        }

        MainWindow again = Open(keeps: true);
        try
        {
            Assert.Equal(KeymapPreset.IntelliJ, again.Settings.Keymap);
            IntelliJKeysWork(again, ownKey);
            SettingsDialog dialog = Settings(again);
            Assert.Equal(IntelliJ, dialog.KeymapChoice.SelectedItem);
            Ui.ChooseFromMenu(dialog.KeymapRow(AppActions.SplitDown), "Reset Shortcuts");
            Assert.Empty(dialog.ShortcutsShown(AppActions.SplitDown));
            Ui.Click(Button(dialog, "OK"));
            Assert.Empty(again.Settings.IntelliJShortcuts);
            again.EditorArea.CodeEditor.Focus();
            Ui.Press(again, ownKey);
            Assert.True(again.EditorArea.Groups.Count == 1, $"{ownKey}, taken from Split Down by Reset Shortcuts, still split the editor");
        }
        finally
        {
            // The keymap in force is the process's; the next test starts on the platform's own.
            again.ApplySettings(new AppSettings());
            again.Close();
        }
    }

    /// <summary>Emacs's keymap chosen in Settings runs its keys and chords in the editor and the IDE, Settings names its
    /// chords, a shortcut set over it is kept with it alone in settings.json, and after a restart it is still chosen with
    /// that shortcut, which Visual Studio's keymap chosen again leaves out.</summary>
    private void Emacs_chosen_in_Settings_runs_its_keys_and_chords_keeps_its_own_shortcut_and_is_kept_after_a_restart()
    {
        const string OwnKey = "control alt shift Y";
        MainWindow window = Open(keeps: true);
        try
        {
            SettingsDialog dialog = Settings(window);
            Choose(dialog, Emacs);
            Assert.Equal([Shortcut.Parse("control X , control F").Display()], dialog.ShortcutsShown(AppActions.GoToFile));
            KeyboardShortcutDialog keys = AddKeyboardShortcut(dialog, AppActions.SplitDown);
            Ui.Press(keys, OwnKey);
            Assert.Empty(keys.Conflicts);
            Ui.Click(keys.OkButton);
            // Beside Emacs's own Ctrl+X, 2.
            Ui.WaitFor(() => dialog.ShortcutsShown(AppActions.SplitDown).Count == 2, "Split Down to show the shortcut given it");
            Ui.Click(Button(dialog, "OK"));
            Assert.Equal(KeymapPreset.Emacs, window.Settings.Keymap);
            Assert.True(window.Settings.Shortcuts.Count == 0 && window.Settings.VSCodeShortcuts.Count == 0 && window.Settings.IntelliJShortcuts.Count == 0,
                "A shortcut set over Emacs's keymap was kept over another keymap's too");

            EmacsKeysWork(window, OwnKey);
            string file = Path.Combine(settings, "settings.json");
            Ui.WaitFor(() => Read(file) is { } kept && (string?)kept["keymap"] == "Emacs", "the keymap chosen in settings.json", TimeSpan.FromSeconds(10));
            string[] kept = [.. Read(file)!["emacsShortcuts"]![AppActions.SplitDown]!.AsArray().Select(k => (string?)k ?? string.Empty)];
            Assert.True(kept.SequenceEqual([Shortcut.Parse("control X , 2").Notation(), Shortcut.Parse(OwnKey).Notation()]),
                $"settings.json kept Split Down's shortcuts over Emacs's keymap as [{string.Join("; ", kept)}]");
        }
        finally
        {
            window.Close();
        }

        MainWindow again = Open(keeps: true);
        try
        {
            Assert.Equal(KeymapPreset.Emacs, again.Settings.Keymap);
            EmacsKeysWork(again, OwnKey);
            again.ApplySettings(again.Settings with { Keymap = KeymapPreset.VisualStudio });
            Ui.Settle();
            again.EditorArea.CodeEditor.Focus();
            Ui.Press(again, OwnKey);
            Assert.True(again.EditorArea.Groups.Count == 1, $"{OwnKey}, the person's own key for Split Down over Emacs's keymap alone, split the editor in Visual Studio's");
        }
        finally
        {
            // The keymap in force is the process's; the next test starts on the platform's own.
            again.ApplySettings(new AppSettings());
            again.Close();
        }
    }

    private void Shortcuts_set_over_one_keymap_stay_with_it_and_Reset_Shortcuts_gives_back_the_chosen_keymaps_own()
    {
        MainWindow window = Open();
        try
        {
            string goToFile = window.Actions[AppActions.GoToFile]!.Title;
            SettingsDialog dialog = Settings(window);
            string visualStudios = Shortcut.Parse($"{Cmd} shift T").Display();
            string vsCodes = Shortcut.Parse($"{Cmd} P").Display();

            Choose(dialog, VSCode);
            KeyboardShortcutDialog keys = AddKeyboardShortcut(dialog, AppActions.SplitDown);
            Ui.Press(keys, $"{Cmd} P");
            Assert.Equal([$"{goToFile} in Main Menu | Navigate"], keys.Conflicts);
            Ui.Click(keys.OkButton);
            ConfirmDialog warning = Asked(dialog);
            Ui.Click(Button(warning, "Remove"));
            Ui.WaitFor(() => !warning.IsVisible, "the warning to close");
            Assert.Equal([vsCodes], dialog.ShortcutsShown(AppActions.SplitDown));
            Assert.Empty(dialog.ShortcutsShown(AppActions.GoToFile));
            Assert.True(dialog.ShowsChanged(AppActions.GoToFile), "Go to File, which lost Cmd+P, is not shown as changed");

            Choose(dialog, VisualStudio);
            Assert.Equal([visualStudios], dialog.ShortcutsShown(AppActions.GoToFile));
            Assert.Empty(dialog.ShortcutsShown(AppActions.SplitDown));
            Assert.False(dialog.ShowsChanged(AppActions.GoToFile), "Go to File, changed in VS Code's keymap alone, is shown changed in Visual Studio's");

            Choose(dialog, VSCode);
            Assert.Equal([vsCodes], dialog.ShortcutsShown(AppActions.SplitDown));
            Ui.ChooseFromMenu(dialog.KeymapRow(AppActions.GoToFile), "Reset Shortcuts");
            Assert.Equal([vsCodes], dialog.ShortcutsShown(AppActions.GoToFile));
            Assert.Empty(dialog.ShortcutsShown(AppActions.SplitDown));
            Assert.False(dialog.ShowsChanged(AppActions.GoToFile), "Go to File, reset, is still shown as changed");
            Ui.Click(Button(dialog, "OK"));

            Assert.Empty(window.Settings.Shortcuts);
            window.EditorArea.CodeEditor.Focus();
            Ui.Press(window, $"{Cmd} P");
            Assert.True(window.Popup is GoToFilePopup, $"{vsCodes}, given back to Go to File by Reset Shortcuts, opened no Go to File");
            Ui.Press(window, "ESCAPE");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void No_key_of_any_keymap_is_shown_on_two_actions_or_begins_a_chord_another_action_has()
    {
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = Settings(window);
            foreach (string keymap in SettingsDialog.KeymapChoices.Select(DefaultKeymaps.NameOf))
            {
                Choose(dialog, keymap);
                List<(string Action, string Keys)> shown = [.. window.Actions.All.SelectMany(a => dialog.ShortcutsShown(a.Id).Select(k => (a.Id, k)))];
                Assert.True(shown.Count > 150, $"{keymap} shows only {shown.Count} shortcuts on the Keymap page");
                foreach (IGrouping<string, (string Action, string Keys)> same in shown.GroupBy(s => s.Keys).Where(g => g.Count() > 1))
                {
                    Assert.Fail($"In {keymap}, {same.Key} is shown on {string.Join(" and ", same.Select(s => s.Action))}, where a key runs one action");
                }
                foreach ((string action, string chord) in shown.Where(s => s.Keys.Contains(", ", StringComparison.Ordinal)))
                {
                    (string Action, string Keys) begins = shown.FirstOrDefault(s => !s.Keys.Contains(", ", StringComparison.Ordinal) && chord.StartsWith(s.Keys + ", ", StringComparison.Ordinal));
                    Assert.True(begins.Action is null, $"In {keymap}, {begins.Keys} runs {begins.Action} before {chord}, {action}'s chord, can be finished");
                }
            }
            dialog.Close();
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    private void In_VS_Codes_keymap_the_terminal_keeps_what_is_typed_and_its_key_goes_back_to_the_editor()
    {
        MainWindow window = Open();
        try
        {
            window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VSCode });
            Ui.Settle();
            window.EditorArea.CodeEditor.Focus();

            // VS Code's Ctrl+` (on the Mac too) opens the terminal with the keys.
            Ui.Press(window, "control BACK_QUOTE");
            Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the shell to start");
            Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the keys to go to the terminal");
            Ui.WaitFor(() => window.TerminalPanel.Active!.Screen.CursorColumn > 0, "the shell's prompt");

            // A key the IDE has only in the editor (Select All, Ctrl+A at a line's start on the Mac) and typing are the shell's.
            Ui.Press(window, "control A");
            window.KeyTextInput("echo keymap");
            Ui.WaitFor(() => window.TerminalPanel.Active!.Screen.ScreenText().Contains("echo keymap", StringComparison.Ordinal), "what was typed in the terminal");
            Assert.Equal(Source, Text(window));

            // So is a Ctrl+letter the keymap gives a tool window's action: the shell reads it as a control character.
            Ui.Press(window, "control J");
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Terminal) && window.TerminalPanel.View.IsFocused, "Ctrl+J, pressed in the terminal, hid it rather than going to the shell");
            if (!OperatingSystem.IsWindows())
            {
                window.KeyTextInput("echo one two");
                Ui.Press(window, "control W");
                window.KeyTextInput("three");
                Ui.Press(window, "ENTER");
                Ui.WaitFor(() => window.TerminalPanel.Active!.Screen.ScreenText().Contains("one three", StringComparison.Ordinal), "^W to delete the word before it in the shell");
            }

            Ui.Press(window, "control BACK_QUOTE");
            Assert.False(window.DockHost.IsShown(ToolWindowIds.Terminal), "Ctrl+`, pressed in the terminal, went to the shell rather than closing it");
            Assert.True(window.EditorArea.CodeEditor.IsFocused, "Ctrl+`, pressed in the terminal, left the keys away from the editor");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>A tool window floated off into a window of its own runs the keymap chosen, as the main window does: its
    /// search box keeps what is typed there, keys the IDE's editor has (Backspace, Left) included; Visual Studio's Find
    /// Action opens over it; and once VS Code's keymap is chosen, VS Code's Quick Open does, where it opened nothing before.</summary>
    private void A_floating_tool_window_runs_the_keymap_chosen_and_its_search_box_keeps_what_is_typed()
    {
        MainWindow window = Open();
        try
        {
            Passenger.App.Docking.DockHost dock = window.DockHost;
            dock.Show(ToolWindowIds.Tests);
            dock.SetMode(dock[ToolWindowIds.Tests], Passenger.App.Docking.ToolMode.Floating);
            Ui.Settle();
            Window floating = Assert.Single(window.OwnedWindows);
            TextBox search = window.BottomPanel.Tests.GetVisualDescendants().OfType<TextBox>().Single(b => b.Classes.Contains(StyleClasses.Search));
            Ui.Click(search);
            Assert.True(search.IsKeyboardFocusWithin, "A click on the floating Unit Tests window's search box left the keys elsewhere");
            floating.KeyTextInput("Onex");
            Ui.Press(floating, "BACK_SPACE");
            Ui.Press(floating, "LEFT");
            floating.KeyTextInput("-");
            Assert.Equal("On-e", search.Text);
            Assert.Equal(Source, Text(window));

            Ui.Press(floating, $"{Cmd} shift A");
            Assert.True(window.Popup is ActionPopup popup && floating.GetVisualDescendants().Contains(popup),
                "Visual Studio's Find Action key, pressed in the floating Unit Tests window, opened no Find Action over it");
            Ui.Press(floating, "ESCAPE");
            Assert.True(search.IsKeyboardFocusWithin, "Find Action, closed, left the floating window's keys away from its search box");
            Ui.Press(floating, $"{Cmd} P");
            Assert.False(window.Popup is GoToFilePopup, "VS Code's Quick Open key opened Go to File in Visual Studio's keymap");

            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VSCode });
            Ui.Settle();
            Ui.Press(floating, $"{Cmd} P");
            Assert.True(window.Popup is GoToFilePopup goToFile && floating.GetVisualDescendants().Contains(goToFile),
                "VS Code's Quick Open key, pressed in the floating Unit Tests window once VS Code's keymap was chosen, opened no Go to File over it");
            Ui.Press(floating, "ESCAPE");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>The terminal floated off into a window of its own keeps the shell's keys as it does docked: in VS Code's
    /// keymap a Ctrl+letter the keymap gives a tool window's action (Hide Active Tool Window's Ctrl+J) and what is typed go
    /// to the shell, while the terminal's own key, VS Code's Ctrl+`, is still the IDE's and closes it.</summary>
    private void A_floating_terminal_keeps_the_shells_keys_and_lets_the_IDEs_through()
    {
        MainWindow window = Open();
        try
        {
            window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VSCode });
            Passenger.App.Docking.DockHost dock = window.DockHost;
            dock.SetMode(dock[ToolWindowIds.Terminal], Passenger.App.Docking.ToolMode.Floating);
            Ui.Settle();
            window.EditorArea.CodeEditor.Focus();
            Ui.Press(window, "control BACK_QUOTE");
            Window floating = Assert.Single(window.OwnedWindows);
            Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the shell to start");
            Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the keys to go to the floating terminal");
            Ui.WaitFor(() => window.TerminalPanel.Active!.Screen.CursorColumn > 0, "the shell's prompt");
            long prompted = CursorLine(window);

            Ui.Press(floating, "control J");
            Assert.True(floating.IsVisible && window.TerminalPanel.View.IsFocused, "Ctrl+J, pressed in the floating terminal, hid it rather than going to the shell");
            // sh ends the empty line at Ctrl+J and echoes a command typed before its next prompt ahead of it; cmd's line goes on.
            if (!OperatingSystem.IsWindows())
            {
                Ui.WaitFor(() => CursorLine(window) > prompted && window.TerminalPanel.Active!.Screen.CursorColumn > 0,
                    "the shell's prompt a row lower, the empty line Ctrl+J entered read");
            }
            floating.KeyTextInput("echo floated");
            Ui.Press(floating, "ENTER");
            Ui.WaitFor(() => window.TerminalPanel.Active!.Screen.ScreenText().Contains("\nfloated", StringComparison.Ordinal), "what was typed run in the floating terminal");

            Ui.Press(floating, "control BACK_QUOTE");
            Assert.False(window.DockHost.IsShown(ToolWindowIds.Terminal), "Ctrl+`, pressed in the floating terminal, went to the shell rather than closing it");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>The line of the terminal's output the cursor stands on, counted from the first ever written.</summary>
    private static long CursorLine(MainWindow window)
    {
        TerminalScreen screen = window.TerminalPanel.Active!.Screen;
        return screen.ScreenTop + screen.CursorRow;
    }

    private void The_menus_and_the_tool_windows_buttons_name_the_chosen_keymaps_keys()
    {
        MainWindow window = Open();
        try
        {
            window.ShowToolWindow(ToolWindowIds.Debug);
            Ui.Settle();
            Button breakpoints = window.DebugWindow.GetVisualDescendants().OfType<Button>()
                .Single(b => ToolTip.GetTip(b) is string tip && tip.StartsWith("View Breakpoints and Exceptions", StringComparison.Ordinal));
            Assert.Equal($"View Breakpoints and Exceptions ({Shortcut.Parse($"{Cmd} alt B").Display()})", ToolTip.GetTip(breakpoints));
            // Containers' actions have no keys of either keymap's: Refresh is given one over VS Code's alone.
            string refreshKeys = $"{Cmd} alt shift J";
            window.ApplySettings(window.Settings with { VSCodeShortcuts = new() { [AppActions.ContainersRefresh] = [refreshKeys] } });
            window.ShowToolWindow(ToolWindowIds.Containers);
            Ui.Settle();
            Button refresh = window.ContainersWindow.GetVisualDescendants().OfType<Button>().Single(b => b.Name == AppActions.ContainersRefresh);
            Assert.Equal("Refresh", ToolTip.GetTip(refresh));
            window.ShowToolWindow(ToolWindowIds.Debug);
            Ui.Settle();

            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VSCode });
            Ui.Settle();
            string vsCodes = Shortcut.Parse($"{Cmd} shift F8").Display();
            Assert.Equal($"View Breakpoints and Exceptions ({vsCodes})", ToolTip.GetTip(breakpoints));
            window.ShowToolWindow(ToolWindowIds.Containers);
            Ui.Settle();
            Assert.Equal($"Refresh ({Shortcut.Parse(refreshKeys).Display()})", ToolTip.GetTip(refresh));
            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VisualStudio });
            Ui.Settle();
            Assert.Equal("Refresh", ToolTip.GetTip(refresh));
            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VSCode });
            Ui.Settle();
            NativeMenuItem goToFile = Ui.Submenu(window.MenuBar, "Navigate").Items.OfType<NativeMenuItem>().First(i => i.Header == window.Actions[AppActions.GoToFile]!.Title);
            Keystroke quickOpen = Keystroke.Parse($"{Cmd} P");
            Assert.Equal(new KeyGesture(quickOpen.Key, quickOpen.Modifiers), goToFile.Gesture);
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>Duplicate, Compare Files, Show Diff, Parameter Info and its overloads' steps, Toggle Case, Generate, Clone
    /// Caret, the moves, Context Help (with External Documentation's), Show Usages, Quick Evaluate Expression, Open Solution,
    /// Open File, Run All Tests with Coverage, Toggle Temporary Breakpoint and a SQL console's Query History and Commit have in each keymap the keys and gestures Rider's of that preset here resolves them to
    /// (<see cref="RiderKeymaps"/>), the Mac's Cmd+Up and Down too in Visual Studio's and IntelliJ IDEA's. A key Rider gives
    /// several by place has one action, the rest taking it there (<see cref="AppActions.KeysIn"/>); New is New Item.</summary>
    private void Each_keymaps_keys_for_Duplicate_Parameter_Info_Toggle_Case_and_Generate_are_those_Riders_keymap_resolves_them_to()
    {
        bool mac = OperatingSystem.IsMacOS();
        foreach (KeymapPreset preset in SettingsDialog.KeymapChoices)
        {
            AppActions.Choose(preset, new Dictionary<string, string[]>());
            Keymap keymap = AppActions.Keymap();
            string rider = RiderKeymaps.Of(preset);
            bool visualStudio = preset == KeymapPreset.VisualStudio;
            bool vsCode = preset == KeymapPreset.VSCode;
            // The Mac's Cmd+Up and Down to the text's ends, which Passenger gives the keymaps of the Mac's own.
            bool macEnds = mac && preset is KeymapPreset.VisualStudio or KeymapPreset.IntelliJ;
            (string Action, string Riders, string[] Besides)[] checks =
            [
                (EditorActions.Duplicate, vsCode ? "EditorDuplicateLines" : "EditorDuplicate", []), (AppActions.CompareFiles, "CompareTwoFiles", []),
                (AppActions.ShowDiff, "Diff.ShowDiff", []), (AppActions.ParameterInfo, "ParameterInfo", []), (EditorActions.ToggleCase, "EditorToggleCase", []),
                (AppActions.Generate, "Generate", []), (EditorActions.Up, "EditorUp", []), (EditorActions.Down, "EditorDown", []),
                (EditorActions.Left, "EditorLeft", []), (EditorActions.Right, "EditorRight", []),
                (EditorActions.CloneCaretAbove, visualStudio ? "CloneCaretAboveWithVirtualSpace" : "EditorCloneCaretAbove", []),
                (EditorActions.CloneCaretBelow, visualStudio ? "CloneCaretBelowWithVirtualSpace" : "EditorCloneCaretBelow", []),
                (EditorActions.TextStart, "EditorTextStart", macEnds ? ["meta UP"] : []),
                (EditorActions.TextEnd, "EditorTextEnd", macEnds ? ["meta DOWN"] : []),
                (AppActions.ContextHelp, "ContextHelp", [.. RiderKeymaps.Resolve(rider, "ExternalJavaDoc").Select(s => s.Notation())]),
                (AppActions.ShowUsages, "ShowUsages", []), (AppActions.QuickEvaluate, "QuickEvaluateExpression", []),
                (AppActions.OpenSolution, "RiderOpenSolution", []), (AppActions.OpenFile, "OpenFile", []),
                (AppActions.NextOverload, "MethodOverloadSwitchDown", []), (AppActions.PreviousOverload, "MethodOverloadSwitchUp", []),
                (AppActions.CoverAllTests, RiderKeymaps.CoverAll, []), (AppActions.ToggleTemporaryBreakpoint, "ToggleTemporaryLineBreakpoint", []),
                (AppActions.DatabaseHistory, "Console.History.Browse", []), (AppActions.DatabaseCommit, "Console.Transaction.Commit", []),
            ];
            foreach ((string action, string riders, string[] besides) in checks)
            {
                string[] ours = [.. keymap.ShortcutsFor(AppActions.KeysIn(action)).Select(s => s.Notation()).Order(StringComparer.Ordinal)];
                string[] theirs = [.. RiderKeymaps.ResolveOwnFirst(rider, riders).Concat(besides.Select(Shortcut.Parse)).Select(s => s.Notation()).Distinct().Order(StringComparer.Ordinal)];
                Assert.True(ours.SequenceEqual(theirs),
                    $"In {DefaultKeymaps.NameOf(preset)}'s keymap {action} runs from [{string.Join("; ", ours)}], where Rider's {rider} gives {riders} [{string.Join("; ", theirs)}]");
            }
            foreach ((string action, string riders) in new[] { (AppActions.ShowUsages, "ShowUsages"), (AppActions.QuickEvaluate, "QuickEvaluateExpression") })
            {
                string[] ours = [.. keymap.MouseShortcutsFor(action).Select(g => g.Notation()).Order(StringComparer.Ordinal)];
                string[] theirs = [.. RiderKeymaps.ResolveMouse(rider, riders).Select(g => g.Notation()).Order(StringComparer.Ordinal)];
                Assert.True(ours.SequenceEqual(theirs),
                    $"In {DefaultKeymaps.NameOf(preset)}'s keymap {action} runs from the gestures [{string.Join("; ", ours)}], where Rider's {rider} gives {riders} [{string.Join("; ", theirs)}]");
            }
            foreach (string shared in (string[])[AppActions.CompareFiles, AppActions.ShowDiff])
            {
                Assert.True(keymap.ShortcutsFor(shared).Count == 0,
                    $"In {DefaultKeymaps.NameOf(preset)}'s keymap {shared} has keys of its own, [{string.Join("; ", keymap.ShortcutsFor(shared).Select(s => s.Notation()))}], where it takes another's where it is pressed");
            }
            string[] newItem = [.. keymap.ShortcutsFor(AppActions.NewItem).Select(s => s.Notation()).Order(StringComparer.Ordinal)];
            string[] riderNew = [.. RiderKeymaps.Resolve(rider, "NewElement").Except(RiderKeymaps.Resolve(rider, "Generate")).Select(s => s.Notation()).Order(StringComparer.Ordinal)];
            Assert.True(newItem.SequenceEqual(riderNew),
                $"In {DefaultKeymaps.NameOf(preset)}'s keymap New Item runs from [{string.Join("; ", newItem)}], where Rider's New (NewElement) has [{string.Join("; ", riderNew)}] that Generate does not take");
        }
    }

    /// <summary>Duplicate's key in Visual Studio's keymap (Cmd+D on the Mac, Ctrl+D elsewhere) duplicates the caret's line in
    /// the editor and, pressed in the Explorer on a file, compares it with the file chosen, leaving the editor's text as it
    /// was: Rider gives the key both, told apart by where it is pressed.</summary>
    private void Duplicates_key_duplicates_the_line_in_the_editor_and_compares_files_in_the_Explorer()
    {
        string lines = Path.Combine(folder, "lines.txt");
        string other = Path.Combine(folder, "other.txt");
        File.WriteAllText(lines, "alpha\nbeta\n");
        File.WriteAllText(other, "alpha\ngamma\n");
        const string Duplicated = "alpha\nalpha\nbeta\n";
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(lines);
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.Focus();
            editor.Selections = Passenger.Text.SelectionSet.Single(0);
            Ui.Press(window, $"{Cmd} D");
            Assert.True(Text(window) == Duplicated,
                $"{Cmd} D in the editor left \"{Text(window)}\" (the status saying \"{window.Status}\"), where Duplicate makes the caret's line twice");

            window.PickFiles = (_, _, _) => Task.FromResult<IReadOnlyList<string>>([other]);
            window.ExplorerView.Reveal(Passenger.Core.PathNames.Canonical(lines));
            Ui.Settle();
            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == Passenger.Core.PathNames.Canonical(lines)));
            Ui.Press(window, $"{Cmd} D");
            Passenger.App.Vcs.IDiffHost diff = OpenDiffs.Landed(window, "the diff of lines.txt and other.txt");
            Assert.Equal("lines.txt vs other.txt", OpenDiffs.TitleOf(diff));
            diff.Close();
            Ui.Settle();
            Assert.True(Text(window) == Duplicated, $"{Cmd} D in the Explorer changed the editor's text to \"{Text(window)}\", where it compares files there");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The mouse's caret and column gestures are those of the keymap in force, each keymap's resolved as its keys
    /// are: each adds a caret and takes it away again, makes a column from the caret to a press that its drag goes on with,
    /// drags a column, and drags one beside the carets there are. Visual Studio's Alt-drag, a column there, adds a caret
    /// in VS Code's and drags nothing.</summary>
    private void The_mouses_caret_and_column_gestures_are_the_keymaps_in_force()
    {
        File.WriteAllText(Path.Combine(folder, "columns.txt"), string.Concat(Enumerable.Repeat("abcdefgh\n", 6)));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(Path.Combine(folder, "columns.txt"));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            bool mac = OperatingSystem.IsMacOS();
            RawInputModifiers cmd = mac ? RawInputModifiers.Meta : RawInputModifiers.Control;
            const RawInputModifiers Alt = RawInputModifiers.Alt;
            const RawInputModifiers Shift = RawInputModifiers.Shift;
            Gestures(window, VisualStudio, addCaret: (MouseButton.Left, mac ? Alt | Shift : RawInputModifiers.Control | Alt),
                fromCaret: (mac ? MouseButton.Middle : MouseButton.Left, Alt | Shift),
                columnDrags: mac ? [(MouseButton.Left, Alt), (MouseButton.Middle, RawInputModifiers.None)] : [(MouseButton.Left, Alt)],
                besideCarets: (MouseButton.Left, cmd | Alt | Shift));

            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VSCode });
            Ui.Settle();
            Gestures(window, VSCode, addCaret: (MouseButton.Left, Alt), fromCaret: (MouseButton.Left, Alt | Shift),
                columnDrags: mac ? [] : [(MouseButton.Middle, RawInputModifiers.None)], besideCarets: (MouseButton.Left, cmd | Alt | Shift));
            Place(editor, 0, 6);
            Ui.Drag(window, Spot(window, 1, 1), Spot(window, 3, 3), MouseButton.Left, Alt);
            Assert.True(Selected(editor) == "0:6, 1:1", $"Alt-drag in VS Code's keymap left {Selected(editor)}, where it adds a caret at its press and drags no column");
        }
        finally
        {
            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VisualStudio });
            window.Close();
        }
    }

    /// <summary>A keymap's caret and column gestures, each a button and the modifiers held with it, pressed on six lines of
    /// "abcdefgh"; places are a line and a column from zero.</summary>
    private static void Gestures(MainWindow window, string keymap, (MouseButton Button, RawInputModifiers Held) addCaret, (MouseButton Button, RawInputModifiers Held) fromCaret,
        (MouseButton Button, RawInputModifiers Held)[] columnDrags, (MouseButton Button, RawInputModifiers Held) besideCarets)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        Place(editor, 0, 1);
        Press(window, addCaret, 2, 3);
        Assert.True(Selected(editor) == "0:1, 2:3", $"{addCaret} in {keymap}'s keymap left {Selected(editor)}, where it adds a caret at 2:3 beside 0:1");
        Press(window, addCaret, 2, 3);
        Assert.True(Selected(editor) == "0:1", $"{addCaret} again in {keymap}'s keymap left {Selected(editor)}, where it takes the caret at 2:3 away");

        Place(editor, 0, 1);
        Ui.Drag(window, Spot(window, 2, 4), Spot(window, 3, 5), fromCaret.Button, fromCaret.Held);
        Assert.True(Selected(editor) == "0:1-0:5, 1:1-1:5, 2:1-2:5, 3:1-3:5",
            $"{fromCaret} pressed at 2:4 and dragged to 3:5 in {keymap}'s keymap left {Selected(editor)}, where it makes a column from the caret at 0:1 that the drag goes on with");

        foreach ((MouseButton Button, RawInputModifiers Held) drag in columnDrags)
        {
            Place(editor, 5, 0);
            Ui.Drag(window, Spot(window, 1, 1), Spot(window, 3, 3), drag.Button, drag.Held);
            Assert.True(Selected(editor) == "1:1-1:3, 2:1-2:3, 3:1-3:3", $"{drag} dragged from 1:1 to 3:3 in {keymap}'s keymap left {Selected(editor)}, where it selects that column");
        }

        Place(editor, 0, 6);
        Ui.Drag(window, Spot(window, 2, 1), Spot(window, 3, 2), besideCarets.Button, besideCarets.Held);
        Assert.True(Selected(editor) == "0:6, 2:1-2:2, 3:1-3:2",
            $"{besideCarets} dragged from 2:1 to 3:2 in {keymap}'s keymap left {Selected(editor)}, where it adds that column beside the caret at 0:6");
    }

    /// <summary>A click of a gesture's button with its modifiers held, at a line and column of the editor.</summary>
    private static void Press(MainWindow window, (MouseButton Button, RawInputModifiers Held) gesture, int line, int column)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        Point at = editor.PointOf(editor.Document.Snapshot.GetOffset(new Passenger.Text.TextPosition(line, column))) + new Point(1, editor.LineHeight / 2);
        if (gesture.Button == MouseButton.Middle)
        {
            Ui.MiddleClick(editor, at, gesture.Held);
        }
        else
        {
            Ui.Click(editor, at, gesture.Held);
        }
    }

    /// <summary>Where in the window a line and column of the editor are, inside the character there.</summary>
    private static Point Spot(MainWindow window, int line, int column)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        Point at = editor.PointOf(editor.Document.Snapshot.GetOffset(new Passenger.Text.TextPosition(line, column))) + new Point(1, editor.LineHeight / 2);
        return editor.TranslatePoint(at, window) ?? throw new Xunit.Sdk.XunitException("The editor is not laid out in its window");
    }

    private static void Place(CodeEditor editor, int line, int column) =>
        editor.Selections = Passenger.Text.SelectionSet.Single(editor.Document.Snapshot.GetOffset(new Passenger.Text.TextPosition(line, column)));

    /// <summary>The editor's carets and selections in order, each as line:column from zero, a selection as its two ends.</summary>
    private static string Selected(CodeEditor editor)
    {
        Passenger.Text.TextSnapshot text = editor.Document.Snapshot;
        string At(int offset)
        {
            Passenger.Text.TextPosition place = text.GetPosition(offset);
            return $"{place.Line}:{place.Column}";
        }

        return string.Join(", ", editor.Selections.Items.Select(s => s.IsEmpty ? At(s.Caret) : $"{At(s.Start)}-{At(s.End)}"));
    }

    /// <summary>Representative keys of Rider's VS Code keymap pressed in the window: the editor's (Select Next Occurrence,
    /// Copy Line Down, Delete Line), the IDE's (Quick Open, the Command Palette, Go to Line, Split Editor) and a chord's.</summary>
    private static void VSCodeKeysWork(MainWindow window)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        int item = Source.IndexOf("item", StringComparison.Ordinal);
        editor.Selections = Passenger.Text.SelectionSet.Single(item + 1);
        Ui.Press(window, $"{Cmd} D");
        Ui.Press(window, $"{Cmd} D");
        Assert.True(editor.Selections.Count == 2, $"{Cmd} D twice made {editor.Selections.Count} selections, where VS Code's Add Selection to Next Find Match makes two");
        Ui.Press(window, "ESCAPE");
        editor.Selections = Passenger.Text.SelectionSet.Single(0);
        Ui.Press(window, "alt shift DOWN");
        Assert.Equal(Source + Source, Text(window));
        Ui.Press(window, $"{Cmd} shift K");
        Assert.Equal(Source, Text(window));

        Ui.Press(window, $"{Cmd} P");
        Assert.True(window.Popup is GoToFilePopup, "VS Code's Quick Open key opened no Go to File");
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, $"{Cmd} shift P");
        Assert.True(window.Popup is ActionPopup, "VS Code's Command Palette key opened no Find Action");
        Ui.Press(window, "ESCAPE");
        window.EditorArea.CodeEditor.Focus();
        Ui.Press(window, "control G");
        Assert.True(window.Popup is GoToLinePopup, "VS Code's Go to Line key, Ctrl+G on the Mac too, opened no Go to Line");
        Ui.Press(window, "ESCAPE");
        window.EditorArea.CodeEditor.Focus();
        Ui.Press(window, $"{Cmd} BACK_SLASH");
        Assert.True(window.EditorArea.Groups.Count == 2, "VS Code's Split Editor key split nothing");
        window.Perform(AppActions.UnsplitAll);
        Ui.Settle();
        window.EditorArea.CodeEditor.Focus();
        Ui.Press(window, $"{Cmd} K , {Cmd} W");
        Assert.True(window.EditorArea.Active is null, $"VS Code's Close All Editors chord left {window.EditorArea.Active?.Name} open");
        window.OpenFile("One.cs");
        Ui.Settle();
    }

    /// <summary>Representative keys of IntelliJ IDEA's keymap pressed in the window: the editor's (Delete Line, which Visual
    /// Studio's Ctrl+Y redoes), the IDE's (Go to File, Go to Class's key for Go to Symbol, Search Everywhere's double Shift)
    /// and the person's own key, which splits the editor down.</summary>
    private static void IntelliJKeysWork(MainWindow window, string ownKey)
    {
        bool mac = OperatingSystem.IsMacOS();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        editor.Selections = Passenger.Text.SelectionSet.Single(0);
        Ui.Press(window, mac ? "meta BACK_SPACE" : "control Y");
        Assert.True(Text(window).Length == 0, $"IntelliJ IDEA's Delete Line key left \"{Text(window)}\", where it deletes the caret's line");
        Ui.Press(window, $"{Cmd} Z");
        Assert.Equal(Source, Text(window));

        Ui.Press(window, mac ? "meta shift O" : "control shift N");
        Assert.True(window.Popup is GoToFilePopup, "IntelliJ IDEA's Go to File key opened no Go to File");
        Ui.Press(window, "ESCAPE");
        window.EditorArea.CodeEditor.Focus();
        Ui.Press(window, mac ? "meta O" : "control N");
        Assert.True(window.Popup is SymbolPopup { Title: "Go to Symbol" }, "IntelliJ IDEA's Go to Class key opened no Go to Symbol");
        Ui.Press(window, "ESCAPE");
        window.EditorArea.CodeEditor.Focus();
        Ui.Press(window, "shift , shift");
        Assert.True(window.Popup is SymbolPopup { Title: "Go to Everything" }, "IntelliJ IDEA's double Shift opened no Search Everywhere");
        Ui.Press(window, "ESCAPE");
        window.EditorArea.CodeEditor.Focus();
        Ui.Press(window, ownKey);
        Assert.True(window.EditorArea.Groups.Count == 2, $"{ownKey}, the person's own key for Split Down over IntelliJ IDEA's keymap, split nothing");
        window.Perform(AppActions.UnsplitAll);
        Ui.Settle();
        window.EditorArea.CodeEditor.Focus();
    }

    /// <summary>Representative keys of Rider's Emacs keymap pressed in the window, Ctrl on the Mac too: the editor's (Ctrl+F,
    /// Ctrl+E and Ctrl+A to move, Ctrl+K to cut to the line's end and Ctrl+Y to paste it back, Esc then Ctrl+F a word on,
    /// Ctrl+/ to undo), the IDE's chords (Ctrl+X, 3 to split and Ctrl+X, 1 to unsplit, Ctrl+X, Ctrl+F to Go to File),
    /// Alt+X to Find Action, and the person's own key, which splits the editor down; on the Mac its Cmd keys too.</summary>
    private static void EmacsKeysWork(MainWindow window, string ownKey)
    {
        if (OperatingSystem.IsMacOS())
        {
            // The Mac's own keys, which Rider's Emacs leaves out, as Visual Studio OSX's keymap has them.
            foreach ((string key, string action) in new (string, string)[]
            {
                ("meta C", EditorActions.Copy), ("meta V", EditorActions.Paste), ("meta X", EditorActions.Cut), ("meta Z", EditorActions.Undo),
                ("meta shift Z", EditorActions.Redo), ("meta A", EditorActions.SelectAll), ("meta S", AppActions.Save), ("meta W", AppActions.CloseTab),
                ("meta COMMA", AppActions.ShowSettings),
            })
            {
                Assert.True(AppActions.Keymap().ActionFor(Shortcut.Parse(key)) == action, $"Emacs's {key} on the Mac runs {AppActions.Keymap().ActionFor(Shortcut.Parse(key)) ?? "nothing"}, not {action}");
            }
        }
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        editor.Selections = Passenger.Text.SelectionSet.Single(0);
        Ui.Press(window, "control F");
        Assert.True(editor.Selections.Primary.Caret == 1, $"Emacs's Ctrl+F left the caret at {editor.Selections.Primary.Caret}, where it moves a character on");
        Ui.Press(window, "control E");
        Assert.True(editor.Selections.Primary.Caret == Source.Length - 1, $"Emacs's Ctrl+E left the caret at {editor.Selections.Primary.Caret}, where it goes to the line's end");
        Ui.Press(window, "control A");
        Ui.Press(window, "control K");
        Assert.True(Text(window) == "\n", $"Emacs's Ctrl+K left \"{Text(window)}\", where it cuts to the line's end");
        Ui.Press(window, "control Y");
        Assert.Equal(Source, Text(window));
        Ui.Press(window, "control SLASH");
        Assert.True(Text(window) == "\n", $"Emacs's Ctrl+/ left \"{Text(window)}\", where it undoes the paste");
        Ui.Press(window, "control SLASH");
        Assert.Equal(Source, Text(window));
        editor.Selections = Passenger.Text.SelectionSet.Single(0);
        Ui.Press(window, "ESCAPE , control F");
        Assert.True(editor.Selections.Primary.Caret is > 1 and <= 6 && Text(window) == Source,
            $"Emacs's Esc, Ctrl+F left the caret at {editor.Selections.Primary.Caret}, where it moves a word on");

        Ui.Press(window, "control X , 3");
        Assert.True(window.EditorArea.Groups.Count == 2, "Emacs's Ctrl+X, 3 split nothing");
        Ui.Press(window, "control X , 1");
        Assert.True(window.EditorArea.Groups.Count == 1, "Emacs's Ctrl+X, 1 left the editor split");
        window.EditorArea.CodeEditor.Focus();
        Ui.Press(window, "control X , control F");
        Assert.True(window.Popup is GoToFilePopup, "Emacs's Ctrl+X, Ctrl+F opened no Go to File");
        Ui.Press(window, "ESCAPE");
        window.EditorArea.CodeEditor.Focus();
        Ui.Press(window, "alt X");
        Assert.True(window.Popup is ActionPopup, "Emacs's Alt+X opened no Find Action");
        Ui.Press(window, "ESCAPE");
        window.EditorArea.CodeEditor.Focus();
        Ui.Press(window, ownKey);
        Assert.True(window.EditorArea.Groups.Count == 2, $"{ownKey}, the person's own key for Split Down over Emacs's keymap, split nothing");
        window.Perform(AppActions.UnsplitAll);
        Ui.Settle();
        window.EditorArea.CodeEditor.Focus();
    }

    /// <summary>Settings as its shortcut opens it, on its Keymap page as a click in its list of pages chooses it.</summary>
    private static SettingsDialog Settings(MainWindow window)
    {
        Ui.Run(window, AppActions.ShowSettings);
        Ui.WaitFor(() => window.OwnedWindows.OfType<SettingsDialog>().Any(), "Settings to open");
        SettingsDialog dialog = window.OwnedWindows.OfType<SettingsDialog>().Single();
        Ui.Settle();
        dialog.Page = SettingsDialog.KeymapPage;
        Ui.Settle();
        return dialog;
    }

    /// <summary>A keymap chosen in the drop-down at the Keymap page's top, by a click on it and one on its name.</summary>
    private static void Choose(SettingsDialog dialog, string keymap)
    {
        Ui.Click(dialog.KeymapChoice);
        Ui.WaitFor(() => dialog.KeymapChoice.IsDropDownOpen, "the keymaps' list to open");
        Ui.Settle();
        ComboBoxItem item = dialog.KeymapChoice.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().First().Child!
            .GetVisualDescendants().OfType<ComboBoxItem>().First(i => i.Content as string == keymap);
        Ui.Click(item);
        Ui.WaitFor(() => !dialog.KeymapChoice.IsDropDownOpen, "the keymaps' list to close");
        Assert.Equal(keymap, dialog.KeymapChoice.SelectedItem);
    }

    /// <summary>Add Keyboard Shortcut from an action's Edit Shortcuts: its dialog once its first field has the keys.</summary>
    private static KeyboardShortcutDialog AddKeyboardShortcut(SettingsDialog settings, string action)
    {
        Ui.ChooseFromMenu(settings.KeymapRow(action), "Add Keyboard Shortcut");
        Ui.WaitFor(() => settings.OwnedWindows.OfType<KeyboardShortcutDialog>().Any(w => w.IsVisible), "the Keyboard Shortcut dialog");
        KeyboardShortcutDialog dialog = settings.OwnedWindows.OfType<KeyboardShortcutDialog>().Single(w => w.IsVisible);
        Ui.WaitFor(() => dialog.Keys.First.IsFocused, "the first keystroke's field to take the keys");
        return DialogButtonsTests.Check(dialog);
    }

    private static ConfirmDialog Asked(SettingsDialog settings)
    {
        Ui.WaitFor(() => settings.OwnedWindows.OfType<ConfirmDialog>().Any(w => w.IsVisible), "the warning that the shortcut is assigned to other actions");
        return DialogButtonsTests.Check(settings.OwnedWindows.OfType<ConfirmDialog>().Single(w => w.IsVisible));
    }

    private static Button Button(Window window, string content) =>
        window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == content);

    private static JsonNode? Read(string file)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(file));
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

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
        window.EditorArea.CodeEditor.Focus();
        return window;
    }
}
