using System.Globalization;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>An export preset of a Godot project (<c>[preset.N]</c> in export_presets.cfg, its options in
/// <c>[preset.N.options]</c>): its name, the platform it exports to, where it writes the game (from the project's folder),
/// the architecture its options give, the templates of its own it names in place of the installed ones, whether it is the
/// one its platform runs, and the line its name is on (from zero).</summary>
public sealed record GodotPreset(string Name, string Platform, string? ExportPath, string? Architecture, string? CustomRelease, string? CustomDebug, bool Runnable, int Line)
{
    /// <summary>The template of its own it names for an export of the kind, null where it takes the installed ones.</summary>
    public string? Custom(bool debug) => debug ? CustomDebug : CustomRelease;
}

/// <summary>A Godot project's export presets as its export_presets.cfg says them, read as Godot's ConfigFile text through
/// <see cref="GodotFile"/>, and the export templates a Godot exports them with: the files each preset's platform takes
/// (<see cref="TemplatesOf"/>), and the folder a Godot keeps those of its version in (its user data folder's
/// <c>export_templates/4.7.2.stable.mono</c>).</summary>
public static class GodotExports
{
    /// <summary>The file a Godot project's export presets are kept in, beside its project.godot.</summary>
    public const string FileName = "export_presets.cfg";

    /// <summary>The presets of an export_presets.cfg's text in the order it writes them; one with no name or platform passed over.</summary>
    public static IReadOnlyList<GodotPreset> Read(string text)
    {
        GodotFile file = GodotFile.Parse(text);
        List<GodotPreset> presets = [];
        foreach (GodotSection section in file.Sections.Where(s => IsPreset(s.Tag)))
        {
            if (section.Property("name") is not { Value.Text: { Length: > 0 } name } named || section.Property("platform")?.Value.Text is not { Length: > 0 } platform)
            {
                continue;
            }
            GodotSection? options = file.Sections.FirstOrDefault(s => s.Tag == section.Tag + ".options");
            presets.Add(new GodotPreset(name, platform, Text(section, "export_path"), Text(options, "binary_format/architecture"), Text(options, "custom_template/release"),
                Text(options, "custom_template/debug"), section.Property("runnable")?.Value.Word == "true", file.LineOf(named.KeyStart)));
        }
        return presets;

        static string? Text(GodotSection? section, string key) => section?.Property(key)?.Value.Text is { Length: > 0 } text ? text : null;
    }

    /// <summary>A Godot project's presets as its export_presets.cfg on disk says them; none where it has no such file or it
    /// cannot be read.</summary>
    public static IReadOnlyList<GodotPreset> ReadIn(string folder)
    {
        string file = Path.Combine(folder, FileName);
        try
        {
            return File.Exists(file) ? Read(File.ReadAllText(file)) : [];
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Whether a section's tag is a preset's own (<c>preset.0</c>), not its options'.</summary>
    private static bool IsPreset(string tag) =>
        tag.StartsWith("preset.", StringComparison.Ordinal) && tag.Length > "preset.".Length && tag["preset.".Length..].All(char.IsAsciiDigit);

    /// <summary>The template files an export of a preset takes from a Godot's installed templates, by their names in its
    /// version's folder: its platform's, of the kind (debug or release) and architecture asked (x86_64 unless its options
    /// name another); none for a platform Passenger knows no templates of.</summary>
    public static IReadOnlyList<string> TemplatesOf(GodotPreset preset, bool debug)
    {
        string kind = debug ? "debug" : "release";
        string architecture = preset.Architecture ?? "x86_64";
        return preset.Platform switch
        {
            "Linux" or "Linux/X11" or "Linux/BSD" => [$"linux_{kind}.{architecture}"],
            "Windows Desktop" => [$"windows_{kind}_{architecture}.exe"],
            "macOS" => ["macos.zip"],
            "Android" => [$"android_{kind}.apk"],
            "iOS" => ["ios.zip"],
            "visionOS" => ["visionos.zip"],
            "Web" => [$"web_{kind}.zip"],
            _ => [],
        };
    }

    /// <summary>The templates an export of a preset lacks in a Godot's version folder (null where no folder is known), or the
    /// template of its own it names where that is not there; none where it has what it takes.</summary>
    public static IReadOnlyList<string> Missing(GodotPreset preset, bool debug, string? folder, Func<string, bool> exists, string projectFolder)
    {
        if (preset.Custom(debug) is { } custom)
        {
            string path = custom.StartsWith("res://", StringComparison.Ordinal) ? Path.Combine(projectFolder, custom["res://".Length..]) : custom;
            return exists(path) ? [] : [custom];
        }
        return [.. TemplatesOf(preset, debug).Where(t => folder is null || !exists(Path.Combine(folder, t)))];
    }

    /// <summary>The name of a Godot's templates folder, as its answer to <c>--version</c> says the version it is of
    /// ("4.7.2.stable.mono.official.ed1daf0bf" names "4.7.2.stable.mono"): its numbers, its status, and "mono" for a .NET
    /// build; null for a line that is no such answer.</summary>
    public static string? TemplatesVersion(string answer)
    {
        if (GodotExecutables.ReadVersion(answer) is not ({ } numbers, bool dotnet))
        {
            return null;
        }
        string[] words = answer.Trim()[(numbers.Length + 1)..].Split('.');
        return $"{numbers}.{words[0]}{(dotnet ? ".mono" : string.Empty)}";
    }

    /// <summary>The folder a Godot keeps its export templates of a version in: its user data folder's
    /// <c>export_templates/version</c> (<see cref="GodotEditorSettings.DataFolder"/>); null where the variables that place
    /// it are unset.</summary>
    public static string? TemplatesFolder(string executable, string version, bool windows, bool macOS, Func<string, string?> variable, Func<string, bool> exists) =>
        GodotEditorSettings.DataFolder(executable, windows, macOS, variable, exists) is { } data ? Path.Combine(data, "export_templates", version) : null;
}
