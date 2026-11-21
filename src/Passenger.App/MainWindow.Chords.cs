using Avalonia.Threading;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>A chord's first key, held: the status bar says it was pressed and what its second key can run, and it is
/// forgotten once the settings' wait for its second ends (<see cref="Passenger.App.Settings.AppSettings.ChordWaitSeconds"/>), on a
/// click, when the focus moves and when the window is left, so a key pressed later is a key of its own rather than the end
/// of a chord begun before.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer chordWait = new() { Interval = TimeSpan.FromSeconds(new Passenger.App.Settings.AppSettings().ChordWaitSeconds) };
    private string? chordMessage;
    private (string? Text, Run.ProblemSeverity? Severity, StatusPlace? Place) beforeChord;

    private void AddChords()
    {
        // A click, a move of the focus and a window left forget it in every window of the IDE's (IdeKeys).
        chordWait.Tick += (_, _) => keys.Reset();
        keys.PendingChanged += (_, _) => ShowChord();
    }

    /// <summary>The chord's message in the status bar while it waits, and the message it covered back once it ends.</summary>
    private void ShowChord()
    {
        chordWait.Stop();
        if (keys.Pending is { } first)
        {
            beforeChord = status.Said;
            chordMessage = ChordMessage(first);
            status.Text = chordMessage;
            chordWait.Start();
            return;
        }
        if (chordMessage is not null && status.Text == chordMessage)
        {
            status.SayAgain(beforeChord);
        }
        chordMessage = null;
    }

    /// <summary>"Prefix key pressed: Comment (C), Uncomment (U)": each action the second key can run, with the first
    /// key it was given.</summary>
    private string ChordMessage(Keystroke first) => "Prefix key pressed: " + string.Join(", ", keys.Keymap.ChordsFrom(first)
        .DistinctBy(chord => chord.Action)
        .Select(chord => $"{actions![chord.Action]?.Title ?? chord.Action} ({KeyWords.Of(chord.Second)})"));
}
