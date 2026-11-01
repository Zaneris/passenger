using System.Text.RegularExpressions;

namespace Passenger.App.Godot;

/// <summary>What Passenger reads of Godot's editor for GDScript's language server: the port its Editor Settings serve it
/// on, and a warning's name out of a diagnostic's message, as Godot's server writes one.</summary>
public static partial class GodotServerWords
{
    /// <summary>The port Godot's editor serves GDScript on unless its Editor Settings say another.</summary>
    public const int DefaultPort = 6005;

    /// <summary>The language server's port an Editor Settings file gives (<c>network/language_server/remote_port</c>), else
    /// <see cref="DefaultPort"/>.</summary>
    public static int PortIn(string settings) =>
        RemotePort().Match(settings) is { Success: true } set && int.TryParse(set.Groups["port"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture, out int port)
            ? port : DefaultPort;

    [GeneratedRegex(@"^network/language_server/remote_port\s*=\s*(?<port>\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex RemotePort();

    /// <summary>A GDScript warning's name and what it says, as Godot's server writes one ("(UNUSED_PARAMETER): The
    /// parameter…"); nulls for any other message.</summary>
    public static (string? Code, string? Said) Warning(string message)
    {
        int close = message.IndexOf("): ", StringComparison.Ordinal);
        return message.StartsWith('(') && close > 1 && !message.AsSpan(1, close - 1).ContainsAnyExcept(WarningName)
            ? (message[1..close], message[(close + 3)..])
            : (null, null);
    }

    /// <summary>The characters of a GDScript warning's name.</summary>
    private static readonly System.Buffers.SearchValues<char> WarningName = System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789");
}
