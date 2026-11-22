using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Navigation;

namespace Passenger.Tests;

/// <summary>The Switcher on Ctrl+Tab, by real keys with Ctrl held down and let go: a quick press goes back to the tab used
/// last and a second comes back; held, its popup lists the tabs by use, the one before chosen and told a screen reader,
/// each Tab moving on and Shift+Tab back, Ctrl let go opening the one chosen and Escape changing nothing; its order follows
/// a tab clicked and a file closed; asked for by its action it waits for Enter, and a tool window chosen is gone to; an
/// agent's input switches without opening it.</summary>
public sealed class SwitcherTests : IDisposable
{
    private static readonly string[] Three = ["One.txt", "Two.txt", "Three.txt"];

    private readonly string folder = TestFolders.Create("switcher-");

    public SwitcherTests()
    {
        foreach (string file in Three)
        {
            File.WriteAllText(Path.Combine(folder, file), $"{Path.GetFileNameWithoutExtension(file)}\n");
        }
    }

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>The switcher: a quick Ctrl+Tab goes back to the tab used last and a second comes back, held its popup
    /// lists the tabs by use, Tab and Shift+Tab move, letting Ctrl go opens the one chosen and Escape changes nothing,
    /// and an agent's input switches as a quick press, never opening the popup.</summary>
    [AvaloniaFact]
    public void The_switcher_goes_back_to_the_tab_used_last_or_the_one_chosen_and_an_agent_never_opens_it()
    {
        Flow.StepApart<SwitcherTests>("a quick Ctrl Tab goes back to the tab used last and a second comes back without the popup", t => t.A_quick_Ctrl_Tab_goes_back_to_the_tab_used_last_and_a_second_comes_back_without_the_popup());
        Flow.StepApart<SwitcherTests>("held its popup lists the tabs by use Tab moves on Shift Tab back Ctrl let go opens the one chosen and Escape changes nothing", t => t.Held_its_popup_lists_the_tabs_by_use_Tab_moves_on_Shift_Tab_back_Ctrl_let_go_opens_the_one_chosen_and_Escape_changes_nothing());
        Flow.StepApart<SwitcherTests>("an agent's input switches as a quick press never opening the popup", t => t.An_agents_input_switches_as_a_quick_press_never_opening_the_popup());
    }

    private void A_quick_Ctrl_Tab_goes_back_to_the_tab_used_last_and_a_second_comes_back_without_the_popup()
    {
        MainWindow window = Opened();
        try
        {
            int shown = window.Popups.Shown;
            QuickSwitch(window);
            Assert.True(window.EditorArea.Active?.Name == "Two.txt", $"A quick Ctrl+Tab from Three.txt went to {window.EditorArea.Active?.Name}, not Two.txt, opened before it");
            QuickSwitch(window);
            Assert.True(window.EditorArea.Active?.Name == "Three.txt", $"A second quick Ctrl+Tab went to {window.EditorArea.Active?.Name}, not back to Three.txt");
            Assert.True(window.EditorArea.CodeEditor.IsFocused, "The file switched to has not the keys");
            Assert.False(window.IsPopupOpen, "A quick Ctrl+Tab left the Switcher's popup open");
            Assert.True(window.Popups.Shown == shown, "A quick Ctrl+Tab, let go at once, showed the Switcher's popup, flashing it over the editor");
        }
        finally
        {
            window.Close();
        }
    }

    private void Held_its_popup_lists_the_tabs_by_use_Tab_moves_on_Shift_Tab_back_Ctrl_let_go_opens_the_one_chosen_and_Escape_changes_nothing()
    {
        MainWindow window = Opened();
        try
        {
            List<(string Words, AutomationLiveSetting Live)> heard = AccessibilityTests.Listen(window);
            Hold(window);
            Ui.Press(window, "control TAB");
            SwitcherPopup popup = Shown(window);
            string[] files = [.. popup.Rows.Where(r => r.Document is not null).Select(r => r.Name)];
            Assert.True(files.SequenceEqual(["Three.txt", "Two.txt", "One.txt"]), $"The Switcher lists the tabs as [{string.Join(", ", files)}], not the latest used first");
            Assert.True(popup.Rows.Skip(files.Length).Any() && popup.Rows.Skip(files.Length).All(r => r.Window is not null),
                "The Switcher lists no tool windows after the tabs");
            Assert.True(popup.Selected?.Name == "Two.txt", $"Held, the Switcher chose {popup.Selected?.Name}, not Two.txt, the tab used before");
            Assert.True(heard.Any(h => h.Words.StartsWith($"Switcher, Two.txt", StringComparison.Ordinal) && h.Words.EndsWith($"item 2 of {popup.Rows.Count}", StringComparison.Ordinal)),
                $"Opening, the Switcher told a screen reader {AccessibilityTests.Said(heard)}, not its name and its choice in the list");
            Ui.Press(window, "control TAB");
            Assert.True(popup.Selected?.Name == "One.txt", $"Tab with Ctrl held moved the choice to {popup.Selected?.Name}, not One.txt");
            Assert.True(heard.Any(h => h.Words == $"One.txt, item 3 of {popup.Rows.Count}"), $"The choice moved was told a screen reader as {AccessibilityTests.Said(heard)}");
            Ui.Press(window, "control shift TAB");
            Assert.True(popup.Selected?.Name == "Two.txt", $"Shift+Tab with Ctrl held moved the choice to {popup.Selected?.Name}, not back to Two.txt");
            Ui.Press(window, "control TAB");
            Assert.True(window.EditorArea.Active?.Name == "Three.txt", "Moving the Switcher's choice switched the file before Ctrl was let go");
            LetGo(window);
            Assert.False(window.IsPopupOpen, "Ctrl let go left the Switcher open");
            Assert.True(window.EditorArea.Active?.Name == "One.txt", $"Ctrl let go after Tab twice opened {window.EditorArea.Active?.Name}, not One.txt, the third");
            Assert.True(window.EditorArea.CodeEditor.IsFocused, "The file the Switcher opened has not the keys");

            Hold(window);
            Ui.Press(window, "control TAB");
            Ui.Press(window, "control TAB");
            Assert.True(Shown(window).Selected?.Name == "Two.txt", $"Tab twice from One.txt chose {Shown(window).Selected?.Name}, not Two.txt, the third by use");
            Ui.Press(window, "control ESCAPE");
            Assert.False(window.IsPopupOpen, "Escape left the Switcher open");
            LetGo(window);
            Assert.True(window.EditorArea.Active?.Name == "One.txt", $"Escape, then Ctrl let go, switched to {window.EditorArea.Active?.Name}, where Escape changes nothing");
            Assert.True(window.EditorArea.CodeEditor.IsFocused, "Escape from the Switcher left the editor without the keys");
        }
        finally
        {
            window.Close();
        }
    }

    private void An_agents_input_switches_as_a_quick_press_never_opening_the_popup()
    {
        MainWindow window = Opened();
        try
        {
            int shown = window.Popups.Shown;
            Assert.Null(AgentAsks.Input(() => Ui.Press(window, "control TAB", settle: false)));
            Ui.Settle();
            Assert.True(window.EditorArea.Active?.Name == "Two.txt", $"An agent's Ctrl+Tab went to {window.EditorArea.Active?.Name}, not Two.txt, as a quick press does");
            Assert.True(window.Popups.Shown == shown && !window.IsPopupOpen, "An agent's Ctrl+Tab opened the Switcher's popup, which waits for a modifier only the person holds");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The window on the folder with One.txt, Two.txt and Three.txt opened in that order, Three.txt shown.</summary>
    private MainWindow Opened()
    {
        MainWindow window = Ui.OpenFolder(folder, width: 1400, languageServer: false);
        foreach (string file in Three)
        {
            Assert.Null(window.OpenFile(file));
        }
        Ui.Settle();
        return window;
    }

    /// <summary>Ctrl+Tab pressed and let go at once, Ctrl with it, as a quick press is, before the IDE runs anything between.</summary>
    private static void QuickSwitch(MainWindow window)
    {
        Hold(window);
        Ui.Press(window, "control TAB", settle: false);
        LetGo(window);
    }

    private static void Hold(MainWindow window) => window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);

    private static void LetGo(MainWindow window)
    {
        window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        Ui.Settle();
    }

    private static SwitcherPopup Shown(MainWindow window)
    {
        Ui.WaitFor(() => window.Popup is SwitcherPopup, "the Switcher's popup, Ctrl held");
        return (SwitcherPopup)window.Popup!;
    }

    private static Control Tab(EditorGroup group, string name) =>
        group.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == name && t.Classes.Contains(StyleClasses.TabName)).FindAncestorOfType<Border>()!;
}
