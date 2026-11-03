using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Passenger.App.Godot;

/// <summary>What Passenger's addon and the window say to each other before anything else, and what tells a game of Godot's
/// Play: the listing the addon reads, the secret it says first and the one it is answered with, and a game's command line.</summary>
public static class GodotEditorWire
{
    /// <summary>The longest first line read before it is refused.</summary>
    private const int LongestSecret = 256;

    /// <summary>The listing's text: the folders it answers for, its port, the secret an editor says and the one it is answered
    /// with, and the process listening, by which a listing left by a Passenger gone is told.</summary>
    public static string ListingText(IReadOnlyList<string> folders, int port, string token, string reply, int process) => new JsonObject
    {
        ["folders"] = new JsonArray([.. folders.Distinct(StringComparer.Ordinal).Select(f => (JsonNode?)JsonValue.Create(f))]),
        ["port"] = port,
        ["token"] = token,
        ["reply"] = reply,
        ["process"] = process,
    }.ToJsonString();

    /// <summary>Whether a connection's first line is the secret, read a byte at a time so nothing after it is read before it
    /// is checked; once it is, the reply is said. A line longer than any secret, or none within the limit, is refused.</summary>
    public static async Task<bool> GreetedAsync(Stream stream, byte[] tokenHash, string reply, TimeSpan limit, CancellationToken cancel)
    {
        using CancellationTokenSource within = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        within.CancelAfter(limit);
        byte[] line = new byte[LongestSecret];
        byte[] one = new byte[1];
        int length = 0;
        try
        {
            while (true)
            {
                if (await stream.ReadAsync(one, within.Token) == 0)
                {
                    return false;
                }
                if (one[0] == (byte)'\n')
                {
                    break;
                }
                if (length == line.Length)
                {
                    return false;
                }
                line[length++] = one[0];
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return false;
        }
        int said = length > 0 && line[length - 1] == (byte)'\r' ? length - 1 : length;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(line.AsSpan(0, said)), tokenHash))
        {
            return false;
        }
        await stream.WriteAsync(Encoding.UTF8.GetBytes(reply + "\n"), cancel);
        return true;
    }

    /// <summary>Whether a command line is of a game Godot's editor of this process started: it names the editor after
    /// <c>--editor-pid</c>.</summary>
    public static bool IsGameOf(string commandLine, int editor)
    {
        string[] words = commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < words.Length; i++)
        {
            if (words[i] == "--editor-pid" && words[i + 1].Trim('"') == editor.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                return true;
            }
        }
        return false;
    }
}
