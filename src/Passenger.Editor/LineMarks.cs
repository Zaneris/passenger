using Passenger.Text;

namespace Passenger.Editor;

/// <summary>What the gutter shows at a line, and how the line behind it is tinted: a breakpoint (set, bound to code while
/// debugging, failing to bind, or disabled), and the debugger's execution point.</summary>
public enum LineMarkKind
{
    /// <summary>A breakpoint, not yet known to bind or not: a red dot over the line number.</summary>
    Breakpoint,

    /// <summary>A breakpoint the running debugger bound to code: the dot with a tick.</summary>
    BreakpointBound,

    /// <summary>A breakpoint the running debugger could not bind (no code there, no symbols): a ring.</summary>
    BreakpointInvalid,

    /// <summary>A breakpoint turned off: a ring, and no tint on its line.</summary>
    BreakpointDisabled,

    /// <summary>Where the debugger stopped: an arrow beside the number and the line tinted over any other.</summary>
    ExecutionPoint,
}

/// <summary>A mark on a line (from zero) of the document the editor shows. A breakpoint with a condition or hit count
/// shows a question mark on its dot, one that does not stop (a tracepoint) is amber, and a muted one (Mute Breakpoints)
/// is grey. A breakpoint on part of its line (an XAML attribute) has that <see cref="Range"/> tinted rather than the
/// whole line, and its dot only where the line has no breakpoint of its own.</summary>
public readonly record struct LineMark(int Line, LineMarkKind Kind, bool Conditional = false, bool Tracepoint = false, bool Muted = false,
    TextRange? Range = null);

/// <summary>A press on a line's number (from zero), with the keys held and where it was (in the editor's
/// coordinates); <see cref="Context"/> for a right-click, which asks for the breakpoint's properties.</summary>
public readonly record struct GutterClick(int Line, Avalonia.Input.KeyModifiers Modifiers, Avalonia.Point Position, bool Context);

/// <summary>What a line-end annotation says, and so how it is drawn.</summary>
public enum LineAnnotationKind
{
    /// <summary>A value the debugger read, after the code that uses it: grey italic.</summary>
    InlineValue,

    /// <summary>The exception the debugger stopped on, at the line that threw it: in the error colour.</summary>
    Exception,

    /// <summary>A value that could not be read, as a binding that failed: in the error colour, on no ground, as it is no stop's reason.</summary>
    Failed,
}

/// <summary>Text drawn after a line's (from zero) code, part of no document: never selected, copied or
/// typed over.</summary>
public readonly record struct LineAnnotation(int Line, string Text, LineAnnotationKind Kind);

/// <summary>The gutter's columns right of the line numbers, left to right.</summary>
public enum GutterColumn
{
    /// <summary>The execution arrow and the tests' run icons.</summary>
    Marks,

    /// <summary>The change bars.</summary>
    Changes,

    /// <summary>The folds' chevrons, against the code.</summary>
    Folding,
}

/// <summary>A stretch that folds: from <see cref="Start"/>, the end of its first line's code where its placeholder
/// stands while folded, to <see cref="End"/>; folded, the lines after the first are hidden. Its kind says whether it
/// starts folded and which folding actions fold it.</summary>
public readonly record struct FoldRegion(int Start, int End, string Placeholder, bool Folded = false, FoldKind Kind = FoldKind.Code);

/// <summary>What a region folds: which fold as a file opens (Fold by default), and which Collapse to Definitions and
/// the doc comments' actions fold.</summary>
public enum FoldKind : byte
{
    /// <summary>Braces that hold code (a namespace's, a type's, a lambda's), a JSON object or array.</summary>
    Code,

    /// <summary>An XML element.</summary>
    Tag,

    /// <summary>A member's body: a method's, a local function's, an accessor's, a property's accessors. Collapse to
    /// Definitions folds these (and the doc comments), Method bodies folds them by default.</summary>
    Body,

    /// <summary>The usings.</summary>
    Imports,

    /// <summary>The comments a file begins with (its header).</summary>
    FileHeader,

    /// <summary>A documentation comment over several lines.</summary>
    DocComment,

    /// <summary>A block comment over several lines.</summary>
    Comment,

    /// <summary>#region to #endregion.</summary>
    Region,
}

/// <summary>A word drawn inside a line at an offset, part of no document: a parameter's name before its argument or a
/// type after the name it types. Padding leaves a space on that side of its chip; <see cref="Data"/> is what the host
/// knows it by, for its hover card.</summary>
public readonly record struct InlayHint(int Offset, string Label, bool PadLeft = false, bool PadRight = false,
    InlayHintKind Kind = InlayHintKind.Parameter, object? Data = null);

/// <summary>What a hint says, which decides the text it goes with and the side of it a caret at its offset stands.</summary>
public enum InlayHintKind
{
    /// <summary>A parameter's name, which begins its argument: text typed at it goes after it, and so does the caret.</summary>
    Parameter,

    /// <summary>A type, which ends the name it types: text typed at it goes before it, and so does the caret.</summary>
    Type,

    /// <summary>What the text before it comes to (a compose file's <c>${VAR}</c> as Compose resolves it), which ends that
    /// text as a type does; shown whatever Settings say of the other two.</summary>
    Value,
}

/// <summary>A test's mark in the gutter beside a test method or class: how it last ran.</summary>
public enum TestMarkKind
{
    NotRun,
    Passed,
    Failed,
    Running,
}

/// <summary>A test's run icon on a line (from zero), right of the line numbers; a click on it asks the host to run it.</summary>
public readonly record struct TestMark(int Line, TestMarkKind Kind);

/// <summary>A run icon on a line (from zero), right of the line numbers, beside the tests' own: something a run
/// configuration runs (a compose file's service, a Dockerfile's image); a click on it asks the host for its menu.</summary>
public readonly record struct RunMark(int Line);

/// <summary>A colour a line (from zero) writes or names, shown as a swatch right of the line numbers; a click on it asks
/// the host for its picker. <see cref="Data"/> is what the host knows it by.</summary>
public readonly record struct ColourMark(int Line, Avalonia.Media.Color Colour, object? Data = null);
