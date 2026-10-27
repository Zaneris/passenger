using System.Text.Json;
using Passenger.Core;

namespace Passenger.Host;

/// <summary>The host's replies, each a line of JSON (<see cref="HostReply"/>) written whole under one lock: its value
/// serialized straight into one pooled buffer after the id, and the line written out only once it is complete, so a value
/// that does not serialize sends nothing of itself.</summary>
public sealed class Replies(Stream output)
{
    private readonly Lock gate = new();
    private readonly PooledBytes line = new();
    private Utf8JsonWriter? writer;

    public void Partial(int id, object value) => Write(id, HostReply.Partial, value, null);

    public void Result(int id, object value) => Write(id, HostReply.Result, value, null);

    public void Error(int id, string? error) => Write(id, HostReply.Error, null, error);

    private void Write(int id, ReadOnlySpan<byte> name, object? value, string? error)
    {
        lock (gate)
        {
            line.Clear();
            Utf8JsonWriter json = writer ??= new Utf8JsonWriter(line, new JsonWriterOptions { Encoder = HostJson.Options.Encoder });
            json.Reset(line);
            json.WriteStartObject();
            json.WriteNumber(HostReply.Id, id);
            if (value is not null)
            {
                json.WritePropertyName(name);
                JsonSerializer.Serialize(json, value, value.GetType(), HostJson.Options);
            }
            else if (error is not null)
            {
                json.WriteString(name, error);
            }
            json.WriteEndObject();
            json.Flush();
            line.GetSpan(1)[0] = (byte)'\n';
            line.Advance(1);
            output.Write(line.Written);
            output.Flush();
        }
    }
}
