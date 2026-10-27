using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Passenger.Dap;

/// <summary>Names many variables share (a collection's elements' type) read as one string: each kept by its bytes' hash in
/// one of a fixed number of places, and handed out again while it holds its place; a name another has taken the place of is
/// read anew. Any thread: a place holds a whole string or none.</summary>
internal sealed class SharedNames : JsonConverter<string>
{
    private const int Places = 512;

    /// <summary>The longest name shared, read on the stack; a longer one is read as it is.</summary>
    private const int Longest = 256;

    private static readonly string?[] Kept = new string?[Places];

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        long length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (reader.TokenType != JsonTokenType.String || length > Longest)
        {
            return reader.GetString();
        }
        Span<byte> unescaped = stackalloc byte[Longest];
        ReadOnlySpan<byte> name = reader.ValueIsEscaped || reader.HasValueSequence ? unescaped[..reader.CopyString(unescaped)] : reader.ValueSpan;
        if (!Ascii.IsValid(name))
        {
            return reader.GetString();
        }
        HashCode hash = default;
        hash.AddBytes(name);
        ref string? place = ref Kept[(int)((uint)hash.ToHashCode() % Places)];
        string? kept = Volatile.Read(ref place);
        if (kept is not null && Ascii.Equals(name, kept))
        {
            return kept;
        }
        string read = Encoding.ASCII.GetString(name);
        Volatile.Write(ref place, read);
        return read;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
