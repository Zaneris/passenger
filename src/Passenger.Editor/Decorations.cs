namespace Passenger.Editor;

/// <summary>How a line-end annotation is drawn.</summary>
public enum LineAnnotationLook
{
    /// <summary>Grey italic.</summary>
    Muted,

    /// <summary>In the error colour, italic, on no ground.</summary>
    Error,

    /// <summary>In the error colour, italic, on a tinted ground that sets it apart from the rest.</summary>
    ErrorOnGround,
}

/// <summary>Text drawn after a line's (from zero) code, part of no document: never selected, copied or
/// typed over.</summary>
public readonly record struct LineAnnotation(int Line, string Text, LineAnnotationLook Look);

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
