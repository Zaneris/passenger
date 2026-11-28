using System.Text.Json.Serialization;

namespace Passenger.Lsp;

/// <summary>A C# string that holds another language: the language by the name its <c>[StringSyntax]</c> or <c>lang=</c>
/// comment gives it, its text as decoded, and the runs of the C# source each stretch of it came from. A hole of an
/// interpolated string is a character of the text with no source.</summary>
public sealed record EmbeddedText(
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("parts")] EmbeddedTextPart[] Parts);

/// <summary>A run of the decoded text whose characters each take the same width in the C# source: one for a character
/// written as itself, more for one written as an escape.</summary>
public readonly record struct EmbeddedTextPart(
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("width")] int Width)
{
    [JsonIgnore]
    public int End => Offset + Length;

    /// <summary>Where in the C# source the decoded text's character at <paramref name="offset"/> begins.</summary>
    public int SourceOffset(int offset) => Start + (offset - Offset) * Width;
}
