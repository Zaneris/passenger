using Avalonia.Controls;
using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Confirm before closing (Settings, System Settings): as chosen, the person is asked before Passenger quits (Exit,
/// the system's Quit, the last window's close), once however many windows the quit closes, or before any window closes;
/// Cancel keeps it open, and Don't ask again sets the setting to Never. Nothing is asked where the close asks a question
/// of its own that confirms it (programs Hot Reload changed, <see cref="CloseHeldAsync"/>), in a quit already under way,
/// at the system's shutdown, or in a Passenger nobody sees or an agent's own. Only the person answers it: an attached
/// agent's input never closes a window that would ask (<see cref="CloseRefused"/>), nor reaches the question.</summary>
public partial class MainWindow
{
    /// <summary>What the person's quit is called here, the question's title and answer: Quit on macOS, Exit elsewhere.</summary>
    public static readonly string QuitWord = OperatingSystem.IsMacOS() ? "Quit" : "Exit";

    /// <summary>The quit's action as named here: as the application menu names it on macOS (Quit Passenger), as File's
    /// item elsewhere (Exit).</summary>
    public static readonly string QuitTitle = OperatingSystem.IsMacOS() ? $"{QuitWord} Passenger" : QuitWord;

    /// <summary>The answer of the question before a window closes.</summary>
    public const string CloseWord = "Close";

    /// <summary>Whether the window closes as the person answered a question of its close, which asks nothing more.</summary>
    private bool closeAnswered;

    private bool askingClose;

    /// <summary>Whether the person is asked before Passenger quits from this window.</summary>
    internal bool ConfirmsQuit => Confirms(ConfirmClosing.WhenQuitting, ConfirmClosing.EveryWindow);

    private bool Confirms(params ConfirmClosing[] asking) => !Unseen && !AgentsOwn && asking.Contains(settings.ConfirmBeforeClosing);

    /// <summary>Whether this window is the last of the person's open, whose close quits Passenger.</summary>
    private bool IsLastWindow => !PassengerApp.Windows.Any(w => w != this && w.IsVisible);

    /// <summary>Whether closing the window asks whether to stop programs Hot Reload changed that its close would stop
    /// (<see cref="CloseHeldAsync"/>), which confirms the close: nobody is at an agent's own Passenger to ask.</summary>
    internal bool AsksOfHeld => !AgentsOwn && HeldByLeaving().Count > 0;

    /// <summary>Whether a close of this window by an attached agent's input is refused (<see cref="RefusedToAgentsInput"/>):
    /// the person's last window, whose close quits Passenger, or any window Confirm before closing asks about.</summary>
    private bool CloseRefused() =>
        !AgentsOwn && (IsLastWindow ? RefusedToAgentsInput(Refusals.QuitIsThePersons.Shown)
            : Confirms(ConfirmClosing.EveryWindow) && RefusedToAgentsInput(Refusals.CloseIsThePersons.Shown));

    /// <summary>The question Confirm before closing asks before this close goes on, or null where it asks none: the system's
    /// Quit asks once for every window (<see cref="PassengerApp.QuitAskedAsync"/>), the last window's close as a quit, any
    /// other's as Every window says.</summary>
    private Task? ConfirmFirst(WindowClosingEventArgs e)
    {
        if (closeAnswered || PassengerApp.Quitting || e.CloseReason == WindowCloseReason.OSShutdown)
        {
            return null;
        }
        if (e.CloseReason == WindowCloseReason.ApplicationShutdown)
        {
            // The system's Quit, never Passenger's own shutdown of its lifetime.
            return !e.IsProgrammatic && ConfirmsQuit ? PassengerApp.QuitAskedAsync(this) : null;
        }
        bool last = IsLastWindow;
        return !AsksOfHeld && (last ? ConfirmsQuit : Confirms(ConfirmClosing.EveryWindow)) ? CloseConfirmedAsync(quits: last) : null;
    }

    /// <summary>The window was closed where Confirm before closing asks: it closes once the person agrees, else stays open.</summary>
    private async Task CloseConfirmedAsync(bool quits)
    {
        if (askingClose)
        {
            return;
        }
        askingClose = true;
        bool agreed;
        try
        {
            agreed = await ConfirmedAsync(quits);
        }
        finally
        {
            askingClose = false;
        }
        if (agreed)
        {
            CloseAsAnswered();
        }
    }

    /// <summary>Confirm before closing's question of a quit, or of this window's close, over this window: whether the person
    /// agreed. Don't ask again, ticked as they agree, sets the setting to Never.</summary>
    internal async Task<bool> ConfirmedAsync(bool quits)
    {
        ConfirmDialog asked = quits
            ? new ConfirmDialog($"{QuitWord} Passenger", "Passenger's windows close, and the programs, builds and terminals they run end with them.",
                QuitWord, offersNotAgain: true) { PersonsAlone = Refusals.QuitIsThePersons.Shown }
            : new ConfirmDialog("Close Window", Folder is null ? "This window closes."
                : $"The window on {SolutionTitle} closes, and the programs, builds and terminals it runs end with it.",
                CloseWord, offersNotAgain: true) { PersonsAlone = Refusals.CloseIsThePersons.Shown };
        bool agreed = await asked.ShowDialog<bool>(this);
        if (agreed && asked.NotAgain)
        {
            ApplySettings(settings with { ConfirmBeforeClosing = ConfirmClosing.Never });
        }
        return agreed;
    }

    /// <summary>Closes the window as the person answered a question of its close, asking nothing more of Confirm before
    /// closing.</summary>
    private void CloseAsAnswered()
    {
        closeAnswered = true;
        try
        {
            Close();
        }
        finally
        {
            closeAnswered = false;
        }
    }
}
