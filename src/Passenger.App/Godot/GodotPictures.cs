using Passenger.Core;

namespace Passenger.App.Godot;

/// <summary>Which file a game's answer to a picture names that Passenger reads and then deletes: the PNG Godot wrote in its
/// temporary folder (macOS's per-user one whatever TMPDIR says, /tmp on Linux, on Windows the environment's TMP or TEMP it
/// was given, else Passenger's own). Whatever answers on the game's debug port may name any path; only a .png directly in
/// that folder, and no link, is taken.</summary>
public static class GodotPictures
{
    /// <summary>The temporary folder a game started with these variables over Passenger's own has, as Godot reads it.</summary>
    public static string TemporaryOf(IReadOnlyDictionary<string, string> environment)
    {
        if (OperatingSystem.IsMacOS())
        {
            return UserTemporary.Folder();
        }
        if (!OperatingSystem.IsWindows())
        {
            return "/tmp";
        }
        return ((string[])["TMP", "TEMP"]).Select(environment.GetValueOrDefault).FirstOrDefault(v => v is { Length: > 0 } && Path.IsPathFullyQualified(v)) ?? Path.GetTempPath();
    }

    /// <summary>Why the file a picture's answer names is not the game's to read and delete, or null where it is: a .png
    /// directly in <paramref name="temporary"/>, no link, there now.</summary>
    public static string? WhyNot(string path, string temporary)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return "which is no PNG";
        }
        string full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) is not { } folder
            || !PathNames.Same(PathNames.Canonical(folder), Path.TrimEndingDirectorySeparator(PathNames.Canonical(temporary))))
        {
            return $"which is not in the temporary folder Godot was given, {temporary}";
        }
        FileInfo file = new(full);
        if (FileLinks.Is(file))
        {
            return "which is a link";
        }
        return file.Exists ? null : "which is not there";
    }
}
