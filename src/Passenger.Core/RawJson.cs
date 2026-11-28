using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Passenger.Core;

/// <summary>A JSON value carried whole as its UTF-8 bytes: written into a message as the value it is, and read out of one
/// as its bytes, never as a string inside the message, nor as a document of its parse.</summary>
[JsonConverter(typeof(RawJsonConverter))]
public sealed class RawJson(byte[] utf8)
{
    public static RawJson EmptyArray { get; } = new("[]"u8.ToArray());

    /// <summary>A value's bytes copied out of the document it lies in.</summary>
    public static RawJson Of(JsonElement value) => new(JsonMarshal.GetRawUtf8Value(value).ToArray());

    public ReadOnlyMemory<byte> Utf8 { get; } = utf8;

    public override string ToString() => Encoding.UTF8.GetString(Utf8.Span);
}

public sealed class RawJsonConverter : JsonConverter<RawJson>
{
    /// <summary>The value's bytes copied out of the message, its document's room pooled and given back.</summary>
    public override RawJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument value = JsonDocument.ParseValue(ref reader);
        return RawJson.Of(value.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options) => writer.WriteRawValue(value.Utf8.Span, skipInputValidation: true);
}
