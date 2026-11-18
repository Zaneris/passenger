using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Settings' Confirm before closing, chosen in System Settings: Never quits and closes unasked; When quitting asks
/// before Exit, the system's Quit and the last window's close, once however many windows are open; Every window asks before
/// any window closes too. Cancel keeps Passenger open, Don't ask again sets Never, a close the person agreed to asks
/// nothing more, and an attached agent's input never closes a window that would ask, nor answers the question.</summary>
public sealed class ConfirmClosingTests : ProjectWindowsFolders
{
    private static readonly string QuitTitle = $"{MainWindow.QuitWord} Passenger";

    private const string CloseTitle = "Close Window";

    /// <summary>Exit and the system's Quit ask before Passenger quits where Confirm before closing says When quitting or
    /// Every window: once, however many windows are open and however often it is asked meanwhile (the system's Quit closing
    /// each window in turn, the first's question holding the rest), Cancel keeping every window open, and agreed, every
    /// window closes, the setting kept as it was. Under Never, Exit quits unasked.</summary>
    [AvaloniaTheory]
    [InlineData(ConfirmClosing.Never)]
    [InlineData(ConfirmClosing.WhenQuitting)]
    [InlineData(ConfirmClosing.EveryWindow)]
    public void Exit_and_the_systems_Quit_ask_once_before_quitting_as_Confirm_before_closing_says(ConfirmClosing choice)
    {
        (MainWindow first, MainWindow second) = TwoWindows(choice);
        try
        {
            if (choice == ConfirmClosing.Never)
            {
                Assert.True(first.Perform(AppActions.Exit));
                Ui.Settle();
                Assert.Empty(Questions());
                Assert.False(first.IsVisible || second.IsVisible, "Exit under Never left a window open");
                return;
            }

            // As the application menu's Quit: the system closes each window in the order they opened.
            foreach (MainWindow window in PassengerApp.Windows.ToList())
            {
                Ui.CloseAsQuit(window);
            }
            ConfirmDialog asked = Asked(QuitTitle);
            Assert.True(first.IsVisible && second.IsVisible, "The system's Quit closed a window while it asked whether to quit");
            Ui.ClickButton(asked, "Cancel");
            Assert.True(first.IsVisible && second.IsVisible && Questions().Length == 0, "Cancel on the system's Quit's question did not keep every window open");

            Assert.True(first.Perform(AppActions.Exit));
            asked = Asked(QuitTitle);
            Assert.True(second.Perform(AppActions.Exit));
            Ui.Settle();
            Assert.Single(Questions());
            Ui.ClickButton(asked, "Cancel");
            Assert.True(first.IsVisible && second.IsVisible && Questions().Length == 0, "Cancel on Exit's question did not keep every window open");

            Assert.True(first.Perform(AppActions.Exit));
            Ui.ClickButton(Asked(QuitTitle), MainWindow.QuitWord);
            Ui.WaitFor(() => !first.IsVisible && !second.IsVisible, "every window to close once the person agreed to quit");
            Assert.Empty(PassengerApp.Windows);
            Ui.WaitFor(() => (string?)ReadNow(SettingsFile)?["confirmBeforeClosing"] == choice.ToString(),
                () => $"settings.json to keep {choice}, Don't ask again left unticked: {ReadNow(SettingsFile)?.ToJsonString()}");
        }
        finally
        {
            CloseEvery();
        }
    }

    /// <summary>A window's close asks first where Confirm before closing says Every window, and the last window's close,
    /// which quits, where it says When quitting or Every window: Cancel keeps the window open, and Don't ask again, ticked
    /// as the person agrees, sets Never in settings.json. Under Never neither asks, and under When quitting a window that is
    /// not the last closes unasked.</summary>
    [AvaloniaTheory]
    [InlineData(ConfirmClosing.Never)]
    [InlineData(ConfirmClosing.WhenQuitting)]
    [InlineData(ConfirmClosing.EveryWindow)]
    public void A_windows_close_asks_first_as_Confirm_before_closing_says_and_Dont_ask_again_sets_Never(ConfirmClosing choice)
    {
        (MainWindow first, MainWindow second) = TwoWindows(choice);
        try
        {
            second.Close();
            Ui.Settle();
            if (choice == ConfirmClosing.EveryWindow)
            {
                ConfirmDialog asked = Asked(CloseTitle);
                Assert.Contains($"The window on {Path.GetFileName(beta)} closes", Said(asked), StringComparison.Ordinal);
                Ui.ClickButton(asked, "Cancel");
                Assert.True(second.IsVisible, "Cancel on the window's question closed it");
                second.Close();
                Ui.ClickButton(Asked(CloseTitle), MainWindow.CloseWord);
                Ui.WaitFor(() => !second.IsVisible, "the window to close once the person agreed");
            }
            else
            {
                Assert.True(!second.IsVisible && Questions().Length == 0, $"Under {choice} a window that is not the last did not close unasked");
            }

            first.Close();
            Ui.Settle();
            if (choice == ConfirmClosing.Never)
            {
                Assert.True(!first.IsVisible && Questions().Length == 0, "Under Never the last window did not close unasked");
                return;
            }
            ConfirmDialog quit = Asked(QuitTitle);
            Ui.ClickButton(quit, "Cancel");
            Assert.True(first.IsVisible, "Cancel on the last window's question quit Passenger");
            first.Close();
            quit = Asked(QuitTitle);
            NotAgain(quit).IsChecked = true;
            Ui.ClickButton(quit, MainWindow.QuitWord);
            Ui.WaitFor(() => !first.IsVisible, "the last window to close once the person agreed to quit");
            Ui.WaitFor(() => (string?)ReadNow(SettingsFile)?["confirmBeforeClosing"] == nameof(ConfirmClosing.Never),
                () => $"settings.json to say Never after Don't ask again: {ReadNow(SettingsFile)?.ToJsonString()}");
        }
        finally
        {
            CloseEvery();
        }
    }

    /// <summary>A close the person agreed to asks nothing more of Confirm before closing, whatever else it asks: a window
    /// holding a read-only file's changes, closed under Every window and agreed to, asks about the changes, and Close
    /// Without Saving closes it at once, where asking again would make the person agree twice.</summary>
    [AvaloniaFact]
    public void A_close_agreed_to_asks_no_more_after_the_question_of_changes_it_could_not_save()
    {
        string locked = Path.Combine(beta, "Locked.cs");
        File.WriteAllText(locked, "class Locked { }\n");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        try
        {
            (MainWindow _, MainWindow second) = TwoWindows(ConfirmClosing.EveryWindow);
            second.Confirm = (_, _, _) => Task.FromResult(false);
            second.OpenFile("Locked.cs");
            Ui.Settle();
            second.EditorArea.CodeEditor.Selections = SelectionSet.Single(0);
            second.KeyTextInput("public ");
            Ui.Settle();
            Assert.True(second.EditorArea.Active is { IsModified: true, Name: "Locked.cs" }, "The read-only file was not left changed and unsaved");

            second.Close();
            Ui.ClickButton(Asked(CloseTitle), MainWindow.CloseWord);
            Ui.WaitFor(() => second.OwnedWindows.OfType<ChoiceDialog>().Any(), "the window to ask about the read-only file's changes");
            Ui.ClickButton(DialogButtonsTests.Check(second.OwnedWindows.OfType<ChoiceDialog>().Single()), "Close Without Saving");
            Ui.Settle();
            Assert.True(!second.IsVisible && Questions().Length == 0, "The window asked again whether to close after the person agreed and answered for its changes");
            Assert.Equal("class Locked { }\n", File.ReadAllText(locked));
        }
        finally
        {
            File.SetAttributes(locked, FileAttributes.Normal);
            CloseEvery();
        }
    }

    /// <summary>An attached agent's input never meets the question: under Every window its close of a window is refused,
    /// saying the person closes it, and no question is asked; and its click on the question the person's own close or Exit
    /// asked is refused too, the question left asking.</summary>
    [AvaloniaFact]
    public void An_agents_input_never_closes_a_window_that_would_ask_nor_answers_the_question()
    {
        (MainWindow first, MainWindow second) = TwoWindows(ConfirmClosing.EveryWindow);
        try
        {
            Assert.Equal(Refusals.CloseIsThePersons, AgentAsks.Input(second.Close));
            Ui.Settle();
            Assert.True(second.IsVisible && Questions().Length == 0, "An agent's input closed a window Every window asks about, or met its question");
            Assert.Equal(Refusals.CloseIsThePersons, second.Status);

            second.Close();
            ConfirmDialog asked = Asked(CloseTitle);
            Assert.Equal(Refusals.CloseIsThePersons, AgentAsks.Input(() => Ui.ClickButton(asked, MainWindow.CloseWord)));
            Ui.Settle();
            Assert.True(asked.IsVisible && asked.Answered is null && second.IsVisible, "An agent's click answered the person's question");
            Ui.ClickButton(asked, "Cancel");

            Assert.True(first.Perform(AppActions.Exit));
            ConfirmDialog quit = Asked(QuitTitle);
            Assert.Equal(Refusals.QuitIsThePersons, AgentAsks.Input(() => Ui.ClickButton(quit, MainWindow.QuitWord)));
            Ui.Settle();
            Assert.True(quit.IsVisible && quit.Answered is null && first.IsVisible && second.IsVisible, "An agent's click answered the person's question whether to quit");
            Ui.ClickButton(quit, "Cancel");
        }
        finally
        {
            CloseEvery();
        }
    }

    private string SettingsFile => Path.Combine(settings, "settings.json");

    /// <summary>A window a person sees on alpha and one on beta in front of it, Confirm before closing chosen in the first's
    /// Settings as a person chooses it, which the second follows.</summary>
    private (MainWindow First, MainWindow Second) TwoWindows(ConfirmClosing choice)
    {
        MainWindow first = Started(LaunchOptions.Empty with { Folder = alpha }, languageServer: false, seen: true);
        MainWindow second = InNewWindow(first, beta);
        SettingsDialog dialog = first.ShowSettings();
        Ui.Settle();
        dialog.Query = "confirm before closing";
        Ui.Settle();
        Assert.Equal(SettingsDialog.SystemPage, dialog.Page);
        ComboBox box = SettingsTests.Editor<ComboBox>(dialog, "Confirm before closing");
        Assert.Equal(["Never", "When quitting", "Every window"], box.Items.Select(i => i?.ToString()));
        Assert.Equal(0, box.SelectedIndex);
        box.SelectedIndex = (int)choice;
        dialog.Ok();
        Ui.Settle();
        Assert.Equal((choice, choice), (first.Settings.ConfirmBeforeClosing, second.Settings.ConfirmBeforeClosing));
        return (first, second);
    }

    /// <summary>The questions Confirm before closing asks, over whichever window.</summary>
    private static ConfirmDialog[] Questions() =>
        [.. PassengerApp.Windows.SelectMany(w => w.OwnedWindows.OfType<ConfirmDialog>()).Where(d => d.IsVisible)];

    /// <summary>The one question asked, held to the dialogs' rules.</summary>
    private static ConfirmDialog Asked(string title)
    {
        Ui.WaitFor(() => Questions().Length > 0, $"the question {title}");
        ConfirmDialog asked = DialogButtonsTests.Check(Assert.Single(Questions()));
        Assert.Equal(title, asked.Title);
        return asked;
    }

    private static CheckBox NotAgain(Window asked) =>
        asked.GetVisualDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "Don't ask again");

    /// <summary>What a question says, its paragraphs joined.</summary>
    private static string Said(Window asked) => string.Join('\n', asked.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).OfType<string>());

    /// <summary>Every window left open closed unasked, as a failed test leaves them.</summary>
    private static void CloseEvery()
    {
        foreach (MainWindow window in PassengerApp.Windows.ToList())
        {
            window.ApplySettings(window.Settings with { ConfirmBeforeClosing = ConfirmClosing.Never });
            window.Close();
        }
        Ui.Settle();
    }
}
