using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Passenger.Core;

/// <summary>Names many messages share (a collection's elements' type, the file, type and method of a theory's rows) read
/// as one string: each kept by its bytes' hash in one of a fixed number of pairs of places, the pair's name read last in
/// its first, and handed out again while it holds either; a name its pair lacks is read anew, the one it displaces from
/// the first place moving to the second. The hash is seeded anew in each process, so in some runs two names read in turn
/// meet in one pair, where both are kept. Any thread: a place holds a whole string or none.</summary>
public sealed class SharedNames : JsonConverter<string>
{
    private const int Pairs = 512;

    /// <summary>The longest name shared, read on the stack; a longer one is read as it is.</summary>
    private const int Longest = 256;

    private static readonly string?[] Kept = new string?[Pairs * 2];

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Read(ref reader);

    /// <summary>The string or property name the reader is on, the one kept for its bytes when there is one.</summary>
    public static string? Read(ref Utf8JsonReader reader)
    {
        long length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.PropertyName) || length > Longest)
        {
            return reader.GetString();
        }
        Span<byte> unescaped = stackalloc byte[Longest];
        ReadOnlySpan<byte> name = reader.ValueIsEscaped || reader.HasValueSequence ? unescaped[..reader.CopyString(unescaped)] : reader.ValueSpan;
        if (!Ascii.IsValid(name))
        {
            return reader.GetString();
        }
        int pair = PairOf(name) * 2;
        ref string? first = ref Kept[pair];
        ref string? second = ref Kept[pair + 1];
        string? last = Volatile.Read(ref first);
        if (last is not null && Ascii.Equals(name, last))
        {
            return last;
        }
        string? before = Volatile.Read(ref second);
        string read = before is not null && Ascii.Equals(name, before) ? before : Encoding.ASCII.GetString(name);
        Volatile.Write(ref second, last);
        Volatile.Write(ref first, read);
        return read;
    }

    /// <summary>Which pair of places keeps a name's bytes, in this process.</summary>
    internal static int PairOf(ReadOnlySpan<byte> name)
    {
        HashCode hash = default;
        hash.AddBytes(name);
        return (int)((uint)hash.ToHashCode() % Pairs);
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
