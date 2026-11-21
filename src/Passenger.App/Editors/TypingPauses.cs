using Passenger.Core;

namespace Passenger.App.Editors;

/// <summary>How long the text and its caret must rest after a key or a move before the window asks again what follows from
/// them (a server's diagnostics, an outline, the uses under the caret, the icons and marks a file's text gives, what a
/// project or scene reads from it, a preview): each its own pause, or all the one <see cref="Variable"/> gives in
/// milliseconds (a test run's, whose every settle after a key waits them out); a value there that is no whole number of
/// milliseconds above 0 is fatal.</summary>
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

    /// <summary>Before the send icons of an .http or .rest file's requests are read again from its text.</summary>
    public static TimeSpan RequestMarks => Of(TimeSpan.FromMilliseconds(300));

    /// <summary>Before a web project's routes are read again from its code.</summary>
    public static TimeSpan Routes => Of(TimeSpan.FromMilliseconds(400));

    /// <summary>Before the Reqnroll and SpecFlow projects' bindings are read again from their code.</summary>
    public static TimeSpan Steps => Of(TimeSpan.FromMilliseconds(400));

    /// <summary>Before a web language server's file has its diagnostics and colours asked for again, or ESLint checks it.</summary>
    public static TimeSpan WebChecks => Of(TimeSpan.FromMilliseconds(400));

    /// <summary>Before a project file is evaluated again with its text as typed.</summary>
    public static TimeSpan ProjectFile => Of(TimeSpan.FromMilliseconds(400));

    /// <summary>Before a XAML file is checked again.</summary>
    public static TimeSpan Xaml => Of(TimeSpan.FromMilliseconds(300));

    /// <summary>Before the Structure window asks for the shown file's outline again.</summary>
    public static TimeSpan Structure => Of(TimeSpan.FromMilliseconds(600));

    /// <summary>Before what a Godot project reads against an open scene, resource or script is read again.</summary>
    public static TimeSpan GodotScenes => Of(TimeSpan.FromMilliseconds(600));

    /// <summary>Before a Markdown, HTML or SVG preview takes the text again.</summary>
    public static TimeSpan Previews => Of(TimeSpan.FromMilliseconds(300));

    /// <summary>Before a file's change bars are worked out again.</summary>
    public static TimeSpan ChangeBars => Of(TimeSpan.FromMilliseconds(120));

    private static TimeSpan Of(TimeSpan own) => EnvironmentValues.Milliseconds(Variable, own);
}
