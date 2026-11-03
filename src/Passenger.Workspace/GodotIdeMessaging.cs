using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>What Godot's .NET editor answers names for, by the numbers its code completion reads: its request's own list
/// stops at ThemeStyles = 9, but the number reaches the engine's list as sent, whose 9 is font sizes and 10 styleboxes.</summary>
public enum GodotIdeKind
{
    InputActions = 0,
    NodePaths = 1,
    ResourcePaths = 2,
    ScenePaths = 3,
    ShaderParams = 4,
    Signals = 5,
    ThemeColors = 6,
    ThemeConstants = 7,
    ThemeFonts = 8,
    ThemeFontSizes = 9,
    ThemeStyles = 10,
}

/// <summary>A message of Godot's IDE messaging protocol: a request or a response, its id (the request's name), its status
/// and its JSON body.</summary>
public sealed record GodotIdeMessage(bool Request, string Id, string Status, string Body);

/// <summary>Godot's IDE messaging protocol, as its .NET editor serves it (GodotTools.IdeMessaging, the editor's
/// MessagingServer): a meta file in the project names the loopback port; each side writes its handshake line and checks the
/// other's; a message is its kind, id, status, the count of its body's lines, the body, and one line more. Pure.</summary>
public static class GodotIdeMessaging
{
    /// <summary>The protocol's version Passenger speaks: a server of another major, or of a later minor, refuses it.</summary>
    public const string Version = "1.1.2";

    /// <summary>The name a client gives itself in its handshake.</summary>
    public const string Identity = "Passenger";

    /// <summary>The status of a message answered as asked.</summary>
    public const string Ok = "Ok";

    /// <summary>The status of an answer to a request its peer does not serve.</summary>
    public const string NotSupported = "RequestNotSupported";

    /// <summary>The id of a request for the names a script's string may take.</summary>
    public const string CodeCompletion = "CodeCompletion";

    /// <summary>The id of a request that the editor play its main scene, as its Play does, building first as Play builds.</summary>
    public const string Play = "Play";

    /// <summary>The id of a request that the editor play its main scene, its body saying whether its own build comes first. Its
    /// debugger fields name a Mono debugger agent the editor puts in its environment for Godot 3's games, which no Godot 4
    /// game reads.</summary>
    public const string DebugPlay = "DebugPlay";

    /// <summary>The id of a request that the editor stop what it plays, as its Stop does.</summary>
    public const string StopPlay = "StopPlay";

    /// <summary>The body of a request to play without the editor building first, once Passenger has built the game itself.</summary>
    public static string PlayBuilt => JsonSerializer.Serialize(new Dictionary<string, object> { ["DebuggerHost"] = string.Empty, ["DebuggerPort"] = 0, ["BuildBeforePlaying"] = false });

    /// <summary>The body of a request that carries nothing.</summary>
    public const string NoBody = "{}";

    private static readonly Regex ServerHandshake = new(@"^GodotIdeServer,Version=([0-9]+)\.([0-9]+)\.([0-9]+),([_a-zA-Z][_a-zA-Z0-9]{0,63})$", RegexOptions.CultureInvariant);

    /// <summary>The meta file a project's .NET editor writes its port and executable into while it runs.</summary>
    public static string MetaPath(string project) => Path.Combine(project, ".godot", "mono", "metadata", "ide_messaging_meta.txt");

    /// <summary>The port and executable a meta file's text names; null for a text that names no port.</summary>
    public static (int Port, string Executable)? MetaOf(string text)
    {
        string[] lines = text.Split('\n');
        return lines.Length > 0 && int.TryParse(lines[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65535
            ? (port, lines.Length > 1 ? lines[1].Trim() : string.Empty) : null;
    }

    /// <summary>The handshake line a client writes.</summary>
    public static string ClientHandshake => $"GodotIdeClient,Version={Version},{Identity}";

    /// <summary>Whether a line is the handshake of a server this client may talk to: Godot's, of the same major version and a
    /// minor no later than the client's.</summary>
    public static bool IsServerHandshake(string line)
    {
        string[] own = Version.Split('.');
        return ServerHandshake.Match(line) is { Success: true } match && match.Groups[1].Value == own[0]
            && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
            && minor <= int.Parse(own[1], CultureInfo.InvariantCulture);
    }

    /// <summary>A message as the protocol writes it, line by line.</summary>
    public static string Written(GodotIdeMessage message)
    {
        int lines = message.Body.Count(c => c == '\n') + 1;
        return $"{(message.Request ? "Request" : "Response")}\n{message.Id}\n{message.Status}\n{lines.ToString(CultureInfo.InvariantCulture)}\n{message.Body}\n\n";
    }

    /// <summary>The body of a request for the names of a kind a script's strings may take, the script by its res:// path.</summary>
    public static string CompletionAsked(GodotIdeKind kind, string script) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["Kind"] = (int)kind, ["ScriptFile"] = script });

    /// <summary>The names an answer to <see cref="CompletionAsked"/> gives, each without the quotes the editor writes around
    /// it; null for a body that is no such answer.</summary>
    public static IReadOnlyList<string>? CompletionAnswered(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return [.. JsonMembers.Texts(document.RootElement, "Suggestions", anyCase: true)
                .Select(s => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A peer's messages read from its lines as they come, as the protocol frames them; a line out of place starts
    /// over at the next.</summary>
    public sealed class Reader
    {
        private string? kind;
        private string? id;
        private string? status;
        private int? pending;
        private readonly StringBuilder body = new();

        /// <summary>The message a line ends, or null while one is still being read (or the line was out of place).</summary>
        public GodotIdeMessage? Read(string line)
        {
            if (kind is null)
            {
                kind = line is "Request" or "Response" ? line : null;
            }
            else if (id is null)
            {
                id = line;
            }
            else if (status is null)
            {
                status = line;
            }
            else if (pending is null)
            {
                if (!int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                {
                    Clear();
                    return null;
                }
                pending = count;
            }
            else if (pending > 0)
            {
                body.Append(line).Append('\n');
                pending--;
            }
            else
            {
                GodotIdeMessage message = new(kind == "Request", id, status, body.ToString().TrimEnd('\n'));
                Clear();
                return message;
            }
            return null;
        }

        private void Clear()
        {
            kind = id = status = null;
            pending = null;
            body.Clear();
        }
    }
}
