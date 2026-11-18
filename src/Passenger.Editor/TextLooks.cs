namespace Passenger.Editor;

/// <summary>How a look colours its text or ground: a theme token's colour, a colour of its own, or neither (the text's own
/// colour, no ground).</summary>
public readonly record struct LookColour
{
    /// <summary>The theme token whose colour it is, or null.</summary>
    public string? Token { get; private init; }

    /// <summary>A colour of its own as 0xAARRGGBB, where it names no token; 0 for neither.</summary>
    public uint Argb { get; private init; }

    public bool IsNone => Token is null && Argb == 0;

    public static LookColour Of(string token) => new() { Token = token };

    /// <summary>A colour of its own, from its red, green and blue (0xRRGGBB).</summary>
    public static LookColour Of(int rgb) => new() { Argb = 0xFF000000 | (uint)(rgb & 0xFFFFFF) };
}

/// <summary>How a look sets its text beside its colours.</summary>
[Flags]
public enum LookStyle : byte
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strike = 8,
}

/// <summary>A look a highlighter gives a stretch beyond the theme's kinds (a program's own colours in a console): its text's
/// colour, drawn at <paramref name="Opacity"/>, its ground and how its text is set.</summary>
public readonly record struct TextLook(LookColour Fore, LookColour Back, LookStyle Style, double Opacity = 1);

/// <summary>The looks highlighters give stretches beyond the theme's kinds, each the token kind of its own that a span names
/// it by, from <see cref="First"/> on, kept for the process: an editor asks for a kind's look as it draws it, and keeps the
/// font and brushes it made for it until the theme changes. At most <see cref="Most"/> are kept; a look past them has no
/// kind, and is drawn as plain text. Any thread.</summary>
public static class TextLooks
{
    /// <summary>The first look's kind: no kind of the theme's reaches it.</summary>
    public const TokenKind First = (TokenKind)0x10000;

    /// <summary>The most looks kept.</summary>
    public const int Most = 4096;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<TextLook, TokenKind> Kinds = [];
    private static readonly List<TextLook> Looks = [];

    /// <summary>The kind a look is named by, made the first time it is asked for; null once <see cref="Most"/> are kept.</summary>
    public static TokenKind? KindOf(TextLook look)
    {
        lock (Gate)
        {
            if (Kinds.TryGetValue(look, out TokenKind kind))
            {
                return kind;
            }
            if (Looks.Count == Most)
            {
                return null;
            }
            kind = First + Looks.Count;
            Looks.Add(look);
            Kinds[look] = kind;
            return kind;
        }
    }

    /// <summary>The look a kind names, where it names one.</summary>
    public static bool TryGet(TokenKind kind, out TextLook look)
    {
        int index = kind - First;
        lock (Gate)
        {
            if (index >= 0 && index < Looks.Count)
            {
                look = Looks[index];
                return true;
            }
        }
        look = default;
        return false;
    }
}
