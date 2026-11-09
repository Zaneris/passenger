using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Passenger.Core;

/// <summary>A peer's messages read as lines of JSON, one to a line: an empty line or one reading as null is passed over,
/// and one that does not read goes to the reader's own rule for it, which may write it down, pass over it, take it as
/// output or throw to end the reading. Framework-free, since it is linked into what Passenger starts that carries none of
/// its assemblies (the project host, WPF's previewer, the inspectors).</summary>
public static partial class JsonLines
{
    /// <summary>The messages read until the reader ends.</summary>
    public static async IAsyncEnumerable<T> ReadAsync<T>(TextReader reader, JsonTypeInfo<T> type, Action<string, JsonException> unread,
        [EnumeratorCancellation] CancellationToken cancel = default) where T : class
    {
        while (await reader.ReadLineAsync(cancel).ConfigureAwait(false) is { } line)
        {
            if (Message(line, type, unread) is { } message)
            {
                yield return message;
            }
        }
    }

    /// <summary>The next message, waited for on the calling thread; null once the reader ends.</summary>
    public static T? Read<T>(TextReader reader, JsonTypeInfo<T> type, Action<string, JsonException> unread) where T : class
    {
        while (reader.ReadLine() is { } line)
        {
            if (Message(line, type, unread) is { } message)
            {
                return message;
            }
        }
        return null;
    }

    private static T? Message<T>(string line, JsonTypeInfo<T> type, Action<string, JsonException> unread) where T : class
    {
        if (line.Length == 0)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(line, type);
        }
        catch (JsonException error)
        {
            unread(line, error);
            return null;
        }
    }
}
