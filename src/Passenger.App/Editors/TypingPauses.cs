using Passenger.Core;

namespace Passenger.App.Editors;

/// <summary>How long the text and its caret must rest after a key or a move before the window asks again what follows from
/// them (the C# server's diagnostics, the path's outline, the uses under the caret, the run icons): each its own pause,
/// or all the one <see cref="Variable"/> gives in milliseconds (a test run's, whose every settle after a key waits them
/// out); a value there that is no whole number of milliseconds above 0 is fatal.</summary>
public static class TypingPauses
{
    [IdentifierHome]
    public const string Variable = "PASSENGER_TYPING_PAUSE_MS";

    /// <summary>Before a C# file's diagnostics are pulled again, and the other C# files shown.</summary>
    public static TimeSpan Diagnostics => Of(TimeSpan.FromMilliseconds(600));

    /// <summary>Before the path in the status bar asks for the file's outline again.</summary>
    public static TimeSpan Outline => Of(TimeSpan.FromMilliseconds(400));

    /// <summary>Before the uses of what the caret rests on are asked for, or a word chosen is looked for.</summary>
    public static TimeSpan Uses => Of(TimeSpan.FromMilliseconds(300));

    /// <summary>Before the run icons of a file with an entry point, a run file's or an AppHost's are read again.</summary>
    public static TimeSpan RunMarks => Of(TimeSpan.FromMilliseconds(300));

    private static TimeSpan Of(TimeSpan own) => EnvironmentValues.Milliseconds(Variable, own);
}
