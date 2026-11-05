using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>Godot's safe save: a file written whole into a file of its own beside it, which then takes its place. That
/// file is named after the saved one, then a dash and mkstemp's six letters or digits on macOS and Linux, or digits and
/// <c>.tmp</c> on Windows. Any program may replace a file; only that name says Godot saved it.</summary>
public static class GodotSafeSave
{
    private const string WindowsEnd = ".tmp";

    /// <summary>The file a path of macOS's and Linux's kind is a save of (<c>project.godot-a1B2c3</c> of <c>project.godot</c>);
    /// null for any other path. A person's file may be named so, so a caller asks whether that file is there.</summary>
    public static string? FileOf(string path) =>
        Path.GetFileName(path).Length > 7 && path[^7] == '-' && path[^6..].All(char.IsAsciiLetterOrDigit) ? path[..^7] : null;

    /// <summary>Whether a path is named as Godot's save of <paramref name="file"/> on any system.</summary>
    public static bool Saves(string path, string file) =>
        FileOf(path) is { } saved ? PathNames.Same(saved, file)
        : path.Length > file.Length + WindowsEnd.Length && path.EndsWith(WindowsEnd, StringComparison.Ordinal) && PathNames.Same(path[..file.Length], file)
            && path[file.Length..^WindowsEnd.Length].All(char.IsAsciiDigit);
}
