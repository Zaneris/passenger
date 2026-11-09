using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Passenger.Core;

/// <summary>A peer's messages read as lines of JSON from the UTF-8 bytes they came in, where they lie: a large message (a
/// picture of a window) costs what it is read as, never a string of its text first.</summary>
public static partial class JsonLines
{
    /// <summary>The messages <paramref name="stream"/>'s lines hold until it ends, as <see cref="ReadAsync{T}(TextReader,
    /// JsonTypeInfo{T}, Action{string, JsonException}, CancellationToken)"/> reads them; a line that does not read is
    /// handed on as text.</summary>
    public static async IAsyncEnumerable<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, Action<string, JsonException> unread,
        [EnumeratorCancellation] CancellationToken cancel = default) where T : class
    {
        using Utf8Lines lines = new(stream);
        while (await lines.NextAsync(cancel).ConfigureAwait(false) is { } line)
        {
            if (Message(line.Span, type, unread) is { } message)
            {
                yield return message;
            }
        }
    }

    private static T? Message<T>(ReadOnlySpan<byte> line, JsonTypeInfo<T> type, Action<string, JsonException> unread) where T : class
    {
        if (line.IsEmpty)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(line, type);
        }
        catch (JsonException error)
        {
            unread(Encoding.UTF8.GetString(line), error);
            return null;
        }
    }
}
