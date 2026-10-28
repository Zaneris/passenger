using Passenger.Text;

namespace Passenger.Editor;

/// <summary>How a marked range is drawn: a wavy underline in the severity's colour, or for code the compiler calls
/// unnecessary (an unused using, a dead assignment) the text faded instead.</summary>
public enum MarkerKind
{
    Error,
    Warning,
    Info,
    Unnecessary,
}

/// <summary>A range the language service has something to say about, and what it says; <see cref="Link"/> is a page that
/// explains it (an ESLint rule's), which the words <see cref="LinkText"/> of the message stand for.</summary>
public readonly record struct Marker(TextRange Range, MarkerKind Kind, string Message)
{
    public Uri? Link { get; init; }

    public string? LinkText { get; init; }

    /// <summary>Where its start goes when text is typed at it while it holds text: <see cref="AnchorGravity.Before"/> takes
    /// the text in, as a squiggle under a word takes what is typed onto that word; <see cref="AnchorGravity.After"/> leaves
    /// the text before it.</summary>
    public AnchorGravity StartGravity { get; init; }
}
