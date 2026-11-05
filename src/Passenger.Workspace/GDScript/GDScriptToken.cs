namespace Passenger.Workspace.GDScript;

/// <summary>A GDScript token's kind, in the order of Godot's own tokenizer (modules/gdscript/gdscript_tokenizer.h).</summary>
public enum GDScriptTokenKind : byte
{
    Empty,
    Annotation,
    Identifier,
    Literal,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    EqualEqual,
    BangEqual,
    And,
    Or,
    Not,
    AmpersandAmpersand,
    PipePipe,
    Bang,
    Ampersand,
    Pipe,
    Tilde,
    Caret,
    LessLess,
    GreaterGreater,
    Plus,
    Minus,
    Star,
    StarStar,
    Slash,
    Percent,
    Equal,
    PlusEqual,
    MinusEqual,
    StarEqual,
    StarStarEqual,
    SlashEqual,
    PercentEqual,
    LessLessEqual,
    GreaterGreaterEqual,
    AmpersandEqual,
    PipeEqual,
    CaretEqual,
    If,
    Elif,
    Else,
    For,
    While,
    Break,
    Continue,
    Pass,
    Return,
    Match,
    When,
    As,
    Assert,
    Await,
    Breakpoint,
    Class,
    ClassName,
    Const,
    Enum,
    Extends,
    Func,
    In,
    Is,
    Namespace,
    Preload,
    Self,
    Signal,
    Static,
    Super,
    Trait,
    Var,
    Void,
    Yield,
    BracketOpen,
    BracketClose,
    BraceOpen,
    BraceClose,
    ParenthesisOpen,
    ParenthesisClose,
    Comma,
    Semicolon,
    Period,
    PeriodPeriod,
    PeriodPeriodPeriod,
    Colon,
    Dollar,
    ForwardArrow,
    Underscore,
    Newline,
    Indent,
    Dedent,
    ConstPi,
    ConstTau,
    ConstInf,
    ConstNan,
    VcsConflictMarker,
    Backtick,
    QuestionMark,
    Error,
    Eof,
}

/// <summary>What a literal token's value is: a string, a <c>&amp;"StringName"</c>, a <c>^"Node/Path"</c>, a number,
/// <c>true</c> or <c>false</c>, or <c>null</c>.</summary>
public enum GDScriptLiteralKind : byte
{
    None,
    String,
    StringName,
    NodePath,
    Int,
    Float,
    Bool,
    Null,
}

/// <summary>One token, where it lies in the text (offsets, its end exclusive) and its value: an identifier's or
/// annotation's name, a literal's value, an error's message.</summary>
public readonly record struct GDScriptToken(GDScriptTokenKind Kind, int Start, int End, object? Value = null, GDScriptLiteralKind Literal = GDScriptLiteralKind.None)
{
    /// <summary>An identifier's, keyword's or annotation's name as written.</summary>
    public string? Name => Value as string;

    /// <summary>Whether it stands for an identifier: the keywords Godot lets name things too (<c>match</c>, <c>when</c>,
    /// <c>PI</c>, <c>TAU</c>, <c>INF</c>, <c>NAN</c>).</summary>
    public bool IsIdentifier => Kind is GDScriptTokenKind.Identifier or GDScriptTokenKind.Match or GDScriptTokenKind.When
        or GDScriptTokenKind.ConstPi or GDScriptTokenKind.ConstInf or GDScriptTokenKind.ConstNan or GDScriptTokenKind.ConstTau;

    /// <summary>Whether it may name a node after <c>$</c> or <c>.</c>: an identifier or almost any keyword.</summary>
    public bool IsNodeName => Kind switch
    {
        GDScriptTokenKind.Identifier or GDScriptTokenKind.And or GDScriptTokenKind.As or GDScriptTokenKind.Assert or GDScriptTokenKind.Await
            or GDScriptTokenKind.Break or GDScriptTokenKind.Breakpoint or GDScriptTokenKind.ClassName or GDScriptTokenKind.Class
            or GDScriptTokenKind.Const or GDScriptTokenKind.ConstPi or GDScriptTokenKind.ConstInf or GDScriptTokenKind.ConstNan
            or GDScriptTokenKind.ConstTau or GDScriptTokenKind.Continue or GDScriptTokenKind.Elif or GDScriptTokenKind.Else
            or GDScriptTokenKind.Enum or GDScriptTokenKind.Extends or GDScriptTokenKind.For or GDScriptTokenKind.Func or GDScriptTokenKind.If
            or GDScriptTokenKind.In or GDScriptTokenKind.Is or GDScriptTokenKind.Match or GDScriptTokenKind.Namespace or GDScriptTokenKind.Not
            or GDScriptTokenKind.Or or GDScriptTokenKind.Pass or GDScriptTokenKind.Preload or GDScriptTokenKind.Return or GDScriptTokenKind.Self
            or GDScriptTokenKind.Signal or GDScriptTokenKind.Static or GDScriptTokenKind.Super or GDScriptTokenKind.Trait
            or GDScriptTokenKind.Underscore or GDScriptTokenKind.Var or GDScriptTokenKind.Void or GDScriptTokenKind.While
            or GDScriptTokenKind.When or GDScriptTokenKind.Yield => true,
        _ => false,
    };

    /// <summary>Whether a binary operator may follow it, so a sign after it is the operator's, not a number's.</summary>
    public bool CanPrecedeBinaryOperator => Kind is GDScriptTokenKind.Identifier or GDScriptTokenKind.Literal or GDScriptTokenKind.Self
        or GDScriptTokenKind.BracketClose or GDScriptTokenKind.BraceClose or GDScriptTokenKind.ParenthesisClose or GDScriptTokenKind.ConstPi
        or GDScriptTokenKind.ConstTau or GDScriptTokenKind.ConstInf or GDScriptTokenKind.ConstNan;

    /// <summary>How Godot's messages name it: "identifier "x"" for an identifier, else its name quoted.</summary>
    public string DebugName => Kind == GDScriptTokenKind.Identifier ? $"identifier \"{Name}\"" : $"\"{KindName(Kind)}\"";

    /// <summary>A kind's name in Godot's messages.</summary>
    public static string KindName(GDScriptTokenKind kind) => Names[(int)kind];

    private static readonly string[] Names =
    [
        "Empty", "Annotation", "Identifier", "Literal", "<", "<=", ">", ">=", "==", "!=", "and", "or", "not", "&&", "||", "!", "&", "|", "~", "^",
        "<<", ">>", "+", "-", "*", "**", "/", "%", "=", "+=", "-=", "*=", "**=", "/=", "%=", "<<=", ">>=", "&=", "|=", "^=", "if", "elif", "else",
        "for", "while", "break", "continue", "pass", "return", "match", "when", "as", "assert", "await", "breakpoint", "class", "class_name",
        "const", "enum", "extends", "func", "in", "is", "namespace", "preload", "self", "signal", "static", "super", "trait", "var", "void",
        "yield", "[", "]", "{", "}", "(", ")", ",", ";", ".", "..", "...", ":", "$", "->", "_", "Newline", "Indent", "Dedent", "PI", "TAU",
        "INF", "NaN", "VCS conflict marker", "`", "?", "Error", "End of file",
    ];
}
