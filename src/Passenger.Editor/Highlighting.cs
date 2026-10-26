using Passenger.Text;

namespace Passenger.Editor;

/// <summary>What a stretch of code is, for colour. Each kind has a brush in the theme,
/// <c>Passenger.Syntax.{Kind}</c>; a kind with none is drawn in the plain text colour.</summary>
public enum TokenKind
{
    Text,
    Keyword,
    ControlKeyword,
    Comment,
    DocComment,
    String,
    StringEscape,
    Number,
    Operator,
    Punctuation,
    Preprocessor,
    Identifier,
    Namespace,
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
    TypeParameter,
    Method,
    Property,
    Event,
    Field,
    Constant,
    EnumMember,
    Local,
    Parameter,
    Label,
    Excluded,

    /// <summary>What a program wrote to standard error, in a console.</summary>
    ErrorOutput,

    /// <summary>What a person typed into a console for the program's input, in italics.</summary>
    UserInput,

    /// <summary>A link in a console (a file's location, a web address), in the link colour.</summary>
    Link,

    /// <summary>A console's line naming an error as MSBuild and the compilers write one, in the error colour.</summary>
    BuildError,

    /// <summary>A console's line naming a warning as MSBuild and the compilers write one, in the warning colour.</summary>
    BuildWarning,

    /// <summary>A bracket at a nesting depth, the three colours taking turns (rainbow braces).</summary>
    Brace1,
    Brace2,
    Brace3,

    /// <summary>An XML or HTML tag: its brackets and name.</summary>
    Tag,
    AttributeName,
    AttributeValue,

    /// <summary>A character or entity reference: <c>&amp;amp;</c>.</summary>
    Entity,

    /// <summary>A JSON object's property name.</summary>
    JsonKey,

    /// <summary>A YAML mapping's key.</summary>
    YamlKey,

    /// <summary>A Markdown heading's text, drawn bold.</summary>
    Heading,

    /// <summary>Markdown's strong emphasis, in the text's colour, bold.</summary>
    Bold,

    /// <summary>Markdown's emphasis, in the text's colour, italic.</summary>
    Italic,

    /// <summary>Markdown's code: a span, a fenced or indented block.</summary>
    Code,

    /// <summary>A Markdown block quote.</summary>
    Quote,

    /// <summary>Where a Markdown link goes: its URL or reference.</summary>
    LinkDestination,
}

/// <summary>Where the pointer rested: the offset under it and the point, in the editor's coordinates, and the inlay
/// hint it rests on, if any, which the card describes rather than the code.</summary>
public sealed class HoverEventArgs(int offset, Avalonia.Point point, InlayHint? hint = null) : EventArgs
{
    public int Offset { get; } = offset;
    public Avalonia.Point Point { get; } = point;
    public InlayHint? Hint { get; } = hint;
}

/// <summary>A coloured stretch of a document, in its offsets.</summary>
public readonly record struct SyntaxSpan(int Start, int Length, TokenKind Kind)
{
    public int End => Start + Length;
}

/// <summary>Colours a document. The editor asks for the spans over a range each time it draws;
/// <see cref="Changed"/> asks it to draw again when new colours are ready.</summary>
public interface IHighlighter
{
    event EventHandler? Changed;

    /// <summary>The spans overlapping [start, start + length) of <paramref name="text"/>, sorted.
    /// A highlighter still working on an older version answers with its best mapping of what it had.</summary>
    IReadOnlyList<SyntaxSpan> GetSpans(TextSnapshot text, int start, int length);
}

/// <summary>A highlighter that knows its language's brackets, which the editor highlights in pairs at the caret and Move
/// Caret to Matching Brace goes between: only it can tell a bracket of the code from one in a string or a comparison.</summary>
public interface IBraceMatcher
{
    /// <summary>The bracket covering the character at <paramref name="offset"/> and the one it pairs with; null when that
    /// is no bracket of the code, one with no pair, or <paramref name="text"/> is not the text this matcher reads. A pair
    /// counted for across lines is looked for near the bracket, as every caret move asks for it, unless
    /// <paramref name="far"/> (Move Caret to Matching Brace, asked once).</summary>
    BracePair? PairAt(TextSnapshot text, int offset, bool far = false);

    /// <summary>The name of the XML or HTML tag a caret is in or at the end of, and the name of the tag it pairs with
    /// (a start tag's end tag, an end tag's start tag); null outside a tag's name, for a tag with no pair, or in a
    /// language without tags. The pair is looked for near the caret, as every caret move asks for it, unless
    /// <paramref name="far"/> (Move Caret to Matching Brace, asked once).</summary>
    BracePair? TagPairAt(TextSnapshot text, int caret, bool far = false) => null;
}

/// <summary>A bracket and the one it pairs with, each as its range in the text.</summary>
public readonly record struct BracePair(TextRange Touched, TextRange Matching);

/// <summary>What folds in a document's text, found off the UI thread as it changes: the regions of the newest text it
/// read, in order of where they start, none folded, and <see cref="FoldsChanged"/> raised on the UI thread for each.</summary>
public interface IFoldFinder
{
    IReadOnlyList<FoldRegion> Folds { get; }

    event EventHandler? FoldsChanged;
}

/// <summary>A use of the symbol at the caret, as the language service highlights it: a write (an assignment, a
/// declaration with its value) apart from a read.</summary>
public readonly record struct UsageHighlight(TextRange Range, bool IsWrite);
