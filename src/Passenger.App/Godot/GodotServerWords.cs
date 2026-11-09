using System.Text.RegularExpressions;
using Passenger.Workspace;

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

    /// <summary>The port the person's Godot editor serves GDScript on (<see cref="PortIn"/>), by the Editor Settings the Godot
    /// at <paramref name="executable"/> of a version reads (<see cref="GodotEditorSettings"/>: beside a self-contained Godot,
    /// else in the user folders the variables place, its version's file or the newest older one's); null where its version
    /// is unknown or no such file is there, so its editor has never run there.</summary>
    public static int? EditorPort(string executable, string? version, Func<string, string?> variable, Func<string, bool> exists, Func<string, string> read)
    {
        if (GodotEditorSettings.Folder(executable, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), variable, exists) is not { } folder
            || GodotEditorSettings.Files(folder, version, exists) is not { Read: { } settings })
        {
            return null;
        }
        try
        {
            return PortIn(read(settings));
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^network/language_server/remote_port\s*=\s*(?<port>\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex RemotePort();

    /// <summary>A GDScript warning's name and what it says, as Godot's server writes one: before its message ("(UNUSED_PARAMETER):
    /// The parameter…"), or as the diagnostic's code beside its message; nulls for any other message.</summary>
    public static (string? Code, string? Said) Warning(string message, string? code = null)
    {
        int close = message.IndexOf("): ", StringComparison.Ordinal);
        if (message.StartsWith('(') && close > 1 && !message.AsSpan(1, close - 1).ContainsAnyExcept(WarningName))
        {
            return (message[1..close], message[(close + 3)..]);
        }
        return code is { Length: > 0 } && !code.AsSpan().ContainsAnyExcept(WarningName) ? (code, message) : (null, null);
    }

    /// <summary>The characters of a GDScript warning's name.</summary>
    private static readonly System.Buffers.SearchValues<char> WarningName = System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789");
}
