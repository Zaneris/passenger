using System.Text.Json.Serialization;

namespace Passenger.Lsp;

/// <summary>Decoded SQL and the stretches of the C# source that produced it. Interpolation holes have no source here.</summary>
public sealed record EmbeddedSql(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("parts")] EmbeddedSqlPart[] Parts);

/// <summary>A run in the decoded SQL whose UTF-16 characters each occupy the same width in the C# source.</summary>
public readonly record struct EmbeddedSqlPart(
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("width")] int Width)
{
    [JsonIgnore]
    public int End => Offset + Length;

    public int SourceOffset(int offset) => Start + (offset - Offset) * Width;
}
