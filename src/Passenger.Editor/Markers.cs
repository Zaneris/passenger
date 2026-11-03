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
    /// <summary>A marker whose message begins with the name its checker gives what it found (CS0168, UNUSED_VARIABLE), where
    /// it gives one.</summary>
    public static Marker Coded(TextRange range, MarkerKind kind, string? code, string said) =>
        code is { Length: > 0 } ? new Marker(range, kind, $"{code}: {said}") { Code = code } : new Marker(range, kind, said);

    /// <summary>The name its checker gives what it found, which its message begins with (<see cref="Coded"/>); null for none.</summary>
    public string? Code { get; init; }

    /// <summary>What it says without its code.</summary>
    public string Said => Code is { } code && Message.StartsWith(code, StringComparison.Ordinal) && Message.AsSpan(code.Length).StartsWith(": ")
        ? Message[(code.Length + 2)..] : Message;

    public Uri? Link { get; init; }

    public string? LinkText { get; init; }

    /// <summary>Where its start goes when text is typed at it while it holds text: <see cref="AnchorGravity.Before"/> takes
    /// the text in, as a squiggle under a word takes what is typed onto that word; <see cref="AnchorGravity.After"/> leaves
    /// the text before it.</summary>
    public AnchorGravity StartGravity { get; init; }
}
