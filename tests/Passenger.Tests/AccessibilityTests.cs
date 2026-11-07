using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The window as a screen reader's walk meets it: through the automation peers, the one tree Windows' UI
/// Automation, Linux's AT-SPI and macOS's accessibility all read. A walk goes down from a window's peer as a platform's
/// bridge does, finds what it wants by the name a person would hear, and presses it through the peer's own provider, never
/// through the control.</summary>
public sealed class AccessibilityTests : IDisposable
{
    private readonly string folder = TestFolders.Create("accessibility-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_question_is_a_window_by_its_title_saying_its_words_and_pressing_Cancel_by_name_answers_it()
    {
        string notes = Path.Combine(folder, "notes.txt");
        File.WriteAllText(notes, "kept\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Task<bool> deleting = window.DeleteAsync(PathNames.Canonical(notes));
            Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(d => d.IsVisible), "the delete's question");
            ConfirmDialog question = window.OwnedWindows.OfType<ConfirmDialog>().Single(d => d.IsVisible);

            AutomationPeer asked = ControlAutomationPeer.CreatePeerForElement(question);
            Assert.True(asked.GetAutomationControlType() == AutomationControlType.Window && asked.GetName() == "Delete",
                $"A screen reader meets the question as a {asked.GetAutomationControlType()} named \"{asked.GetName()}\", not as the window Delete");
            Assert.True(asked.GetHelpText() == "Move notes.txt to the Trash?", $"The question's window tells a screen reader \"{asked.GetHelpText()}\", not what it asks");
            AutomationPeer[] met = [.. Walk(asked)];
            Assert.True(met.Any(peer => peer.GetAutomationControlType() == AutomationControlType.Text && peer.GetName() == "Move notes.txt to the Trash?"),
                $"A walk of the question never reads what it asks, only: {Heard(met)}");
            Assert.True(Button(asked, "Delete") is not null, $"A walk of the question finds no button named Delete, only: {Heard(met)}");

            AutomationPeer cancel = Button(asked, "Cancel") ?? throw new Xunit.Sdk.XunitException($"A walk of the question finds no button named Cancel, only: {Heard(met)}");
            IInvokeProvider press = cancel.GetProvider<IInvokeProvider>() ?? throw new Xunit.Sdk.XunitException("Cancel can be found but not pressed by what reads the window");
            press.Invoke();

            Assert.False(Ui.Await(deleting, "the question's answer"), "Cancel pressed through accessibility did not answer no");
            Assert.True(File.Exists(notes), "Cancel pressed through accessibility still deleted the file");
            Assert.False(question.IsVisible, "The question stays open after Cancel is pressed through accessibility");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_toolbar_and_stripes_icons_are_named_by_what_they_do_their_keys_left_in_the_tip()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            AutomationPeer top = ControlAutomationPeer.CreatePeerForElement(window);
            foreach (string words in (string[])[.. OperatingSystem.IsMacOS() ? (string[])[] : ["Main menu"], "Build solution", "Run", "Debug", "Profile", "Stop", "Search everywhere", "Settings", "Explorer", "Terminal", "Problems"])
            {
                Assert.True(Button(top, words) is not null, $"The window has no button named {words}, only: {Heard(Walk(top).Where(IsButton))}");
            }
            AutomationPeer run = Assert.Single(Walk(top), peer => peer is ControlAutomationPeer { Owner.Name: "RunButton" });
            Assert.True(run.GetName() == "Run" && run.GetHelpText() == ToolTip.GetTip(((ControlAutomationPeer)run).Owner) as string,
                $"The toolbar's Run is named \"{run.GetName()}\" with \"{run.GetHelpText()}\" as its tip");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_Explorer_row_is_named_by_its_words_never_by_its_items_type()
    {
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "kept\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            AutomationPeer[] rows = [.. Walk(ControlAutomationPeer.CreatePeerForElement(window)).Where(peer => peer.GetAutomationControlType() == AutomationControlType.ListItem)];
            Assert.True(rows.Any(row => row.GetName() == "notes.txt"), $"No row is named notes.txt, only: {Heard(rows)}");
            Assert.False(rows.Any(row => row.GetName().StartsWith("Passenger.", StringComparison.Ordinal)), $"A row is named by its item's type: {Heard(rows)}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_settings_field_is_named_by_its_label_and_the_project_button_by_the_project()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            AutomationPeer project = ControlAutomationPeer.CreatePeerForElement(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ProjectButton"));
            Assert.True(project.GetName() == Path.GetFileName(folder), $"The project's button is named \"{project.GetName()}\", not {Path.GetFileName(folder)}");

            SettingsDialog settings = window.ShowSettings();
            Ui.Settle();
            AutomationPeer[] met = [.. Walk(ControlAutomationPeer.CreatePeerForElement(settings))];
            Assert.True(met.Any(peer => peer.GetAutomationControlType() == AutomationControlType.ComboBox && peer.GetName() == "Theme"),
                $"Settings' Theme box goes unnamed, among: {Heard(met.Where(peer => peer.GetAutomationControlType() == AutomationControlType.ComboBox))}");
            // macOS's menu is the system's, so its page holds no check box to ask of.
            Assert.True(OperatingSystem.IsMacOS() || met.Any(peer => peer.GetAutomationControlType() == AutomationControlType.CheckBox && peer.GetName() == "Show main menu as a menu bar"),
                $"Settings' check box goes unnamed, among: {Heard(met.Where(peer => peer.GetAutomationControlType() == AutomationControlType.CheckBox))}");
            settings.Close();
            Ui.Settle();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_forms_field_is_named_by_its_rows_label()
    {
        TextBox field = new();
        _ = FormRows.Of("Target framework", field);
        string name = ControlAutomationPeer.CreatePeerForElement(field).GetName();
        Assert.True(name == "Target framework", $"A form's field is named \"{name}\", not by its label");
    }

    private static IEnumerable<AutomationPeer> Walk(AutomationPeer peer) => peer.GetChildren().SelectMany(Walk).Prepend(peer);

    private static AutomationPeer? Button(AutomationPeer within, string name) => Walk(within).FirstOrDefault(peer => IsButton(peer) && peer.GetName() == name);

    private static bool IsButton(AutomationPeer peer) => peer.GetAutomationControlType() == AutomationControlType.Button;

    /// <summary>What a walk met that has a name, for a failure to say.</summary>
    private static string Heard(IEnumerable<AutomationPeer> met) =>
        string.Join(", ", met.Where(peer => peer.GetName().Length > 0).Select(peer => $"{peer.GetAutomationControlType()} \"{peer.GetName()}\""));
}
