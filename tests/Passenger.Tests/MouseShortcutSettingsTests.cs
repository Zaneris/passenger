using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Navigation;
using Passenger.App.Settings;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Mouse shortcuts in Settings' Keymap: Add Mouse Shortcut takes a gesture pressed into its box, says the action
/// a keymap gesture already runs before anything is kept and takes it from that action once Remove is chosen, the gesture
/// then runs its new action in the editor, Find Action and the menus show it beside the keys, and settings.json keeps it
/// under the action in IntelliJ's notation for the next start, where it still runs.</summary>
public sealed class MouseShortcutSettingsTests : IDisposable
{
    private static readonly bool Mac = OperatingSystem.IsMacOS();

    /// <summary>Go to Declaration's gesture in the Visual Studio keymap: Cmd-click on the Mac, Ctrl-click elsewhere.</summary>
    private static readonly string Declaration = Mac ? "meta button1" : "control button1";

    private readonly string folder = TestFolders.Create("mouse-shortcuts-");
    private readonly string settings = TestFolders.Create("mouse-shortcuts-settings-");

    public void Dispose()
    {
        // The keymap in force is the process's: the next test starts on the platform's own.
        AppActions.Choose(KeymapPreset.VisualStudio, new Dictionary<string, string[]>());
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    [AvaloniaFact]
    public void A_mouse_shortcut_added_in_Settings_says_its_conflict_first_runs_its_action_and_is_kept_after_a_restart()
    {
        MouseShortcut gesture = MouseShortcut.Parse(Declaration);
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = Settings(window);
            Assert.Contains(gesture.Display(), dialog.ShortcutsShown(AppActions.GoToDeclaration));
            MouseShortcutDialog mouse = AddMouseShortcut(dialog, AppActions.SplitRight);
            Assert.False(mouse.OkButton.IsEnabled, "Mouse Shortcut's OK could be pressed before any gesture was");
            Ui.Click(mouse.Pad, modifiers: Mac ? RawInputModifiers.Meta : RawInputModifiers.Control);
            Assert.Equal(gesture.Display(), mouse.Shown);
            Assert.True(mouse.Conflicts.Any(c => c.StartsWith(window.Actions[AppActions.GoToDeclaration]!.Title, StringComparison.Ordinal)),
                $"{gesture.Display()}, Go to Declaration's, was said to be assigned to [{string.Join("; ", mouse.Conflicts)}]");
            Ui.Click(mouse.OkButton);
            ConfirmDialog warning = Asked(dialog);
            Assert.True(dialog.ShortcutsShown(AppActions.SplitRight).Count == 0, "Split Right took the gesture before the warning was answered");
            Ui.Click(Button(warning, "Remove"));
            Ui.Settle();
            Assert.Contains(gesture.Display(), dialog.ShortcutsShown(AppActions.SplitRight));
            Assert.DoesNotContain(gesture.Display(), dialog.ShortcutsShown(AppActions.GoToDeclaration));
            Assert.True(dialog.ShowsChanged(AppActions.GoToDeclaration), "Go to Declaration, its gesture taken, is not shown changed");
            Ui.Click(Button(dialog, "OK"));
            Ui.Settle();

            Splits(window);
            Ui.Run(window, AppActions.FindAction);
            Ui.WaitFor(() => window.Popup is ActionPopup, "Find Action");
            ActionPopup actions = (ActionPopup)window.Popup!;
            actions.Query = window.Actions[AppActions.SplitRight]!.Title;
            Ui.Settle();
            string shown = actions.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).FirstOrDefault(t => t.EndsWith(gesture.Display(), StringComparison.Ordinal))
                ?? string.Empty;
            Assert.True(shown.Length > 0, $"Find Action shows no {gesture.Display()} beside Split Right's keys");
            Ui.Press(window, "ESCAPE");

            string file = Path.Combine(settings, "settings.json");
            Ui.WaitFor(() => Read(file)?["shortcuts"]?[AppActions.SplitRight] is JsonArray kept && kept.Any(k => (string?)k == Declaration),
                $"settings.json to keep {Declaration} under Split Right", TimeSpan.FromSeconds(10));
        }
        finally
        {
            window.Close();
        }

        MainWindow again = Open();
        try
        {
            Splits(again);
            Assert.True(again.Actions.Keymap.ActionFor(gesture) == AppActions.SplitRight, $"{gesture.Display()} runs {again.Actions.Keymap.ActionFor(gesture)} after a restart, not Split Right");
        }
        finally
        {
            again.ApplySettings(new AppSettings());
            again.Close();
        }
    }

    /// <summary>The settings.json of a build that kept a person's keys without their gestures: Go to Declaration given a
    /// key of the person's keeps the gesture its keymap gives it.</summary>
    [AvaloniaFact]
    public void A_settings_json_of_keys_alone_keeps_the_keymaps_gestures_for_the_actions_it_names()
    {
        // Version 2, the last before an action's own shortcuts gave its gestures too.
        File.WriteAllText(Path.Combine(settings, "settings.json"),
            $"{{\n  \"version\": 2,\n  \"shortcuts\": {{ \"{AppActions.GoToDeclaration}\": [\"F12\", \"{(Mac ? "meta" : "control")} alt shift F12\"] }}\n}}\n");
        MainWindow window = Open();
        try
        {
            Keymap keymap = window.Actions.Keymap;
            Assert.True(keymap.ActionFor(MouseShortcut.Parse(Declaration)) == AppActions.GoToDeclaration,
                $"{MouseShortcut.Parse(Declaration).Display()} runs {keymap.ActionFor(MouseShortcut.Parse(Declaration))} once the person's own keys of Go to Declaration were read");
            Assert.Contains(Shortcut.Parse($"{(Mac ? "meta" : "control")} alt shift F12"), keymap.ShortcutsFor(AppActions.GoToDeclaration));
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>Go to Declaration's gesture pressed on the editor's text splits it, as Split Right now has the gesture.</summary>
    private static void Splits(MainWindow window)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.Click(editor, editor.PointOf(3) + new Point(1, editor.LineHeight / 2), Mac ? RawInputModifiers.Meta : RawInputModifiers.Control);
        Ui.WaitFor(() => window.EditorArea.Groups.Count == 2, "the gesture to split the editor");
        window.Perform(AppActions.Unsplit);
        Ui.Settle();
    }

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

    /// <summary>Add Mouse Shortcut from an action's Edit Shortcuts: its dialog, shown.</summary>
    private static MouseShortcutDialog AddMouseShortcut(SettingsDialog settings, string action)
    {
        Ui.ChooseFromMenu(settings.KeymapRow(action), "Add Mouse Shortcut");
        Ui.WaitFor(() => settings.OwnedWindows.OfType<MouseShortcutDialog>().Any(w => w.IsVisible), "the Mouse Shortcut dialog");
        return DialogButtonsTests.Check(settings.OwnedWindows.OfType<MouseShortcutDialog>().Single(w => w.IsVisible));
    }

    private static ConfirmDialog Asked(SettingsDialog settings)
    {
        Ui.WaitFor(() => settings.OwnedWindows.OfType<ConfirmDialog>().Any(w => w.IsVisible), "the warning that the gesture is assigned to another action");
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

    private MainWindow Open()
    {
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "one two three\nfour five six\n");
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.OpenFolder(folder);
        window.KeepSettings();
        window.OpenFile(Path.Combine(folder, "notes.txt"));
        Ui.Settle();
        window.EditorArea.CodeEditor.Focus();
        return window;
    }
}
