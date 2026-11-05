using System.Globalization;
using System.IO.Compression;
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
/// (<see cref="TemplatesOf"/>), the folder a Godot keeps those of its version in (its user data folder's
/// <c>export_templates/4.7.2.stable.mono</c>), and their install from a <c>.tpz</c> Godot's download page offers, a zip
/// whose <c>templates/</c> holds them and the version they are of.</summary>
public static class GodotExports
{
    /// <summary>The file a Godot project's export presets are kept in, beside its project.godot.</summary>
    public const string FileName = "export_presets.cfg";

    /// <summary>The extension of the archive Godot's export templates come in.</summary>
    public const string Archive = ".tpz";

    /// <summary>The file of a template archive and of an installed version's folder naming the version its templates are of.</summary>
    public const string VersionFile = "version.txt";

    /// <summary>The folder inside a template archive that holds its files.</summary>
    private const string Inside = "templates/";

    /// <summary>The files every install takes beside a platform's: the version, and the text server's data a Godot exports with.</summary>
    private static readonly string[] Always = [VersionFile, "icudt_godot.dat"];

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

    /// <summary>The files an install for the presets takes from an archive: each one's templates of both kinds (a Windows
    /// one's console wrappers too), the version and the text server's data; all of the archive's for no preset.</summary>
    public static IReadOnlySet<string>? NeededBy(IReadOnlyList<GodotPreset> presets)
    {
        if (presets.Count == 0)
        {
            return null;
        }
        HashSet<string> needed = new(Always, StringComparer.Ordinal);
        foreach (string template in presets.SelectMany(p => TemplatesOf(p, debug: false).Concat(TemplatesOf(p, debug: true))))
        {
            needed.Add(template);
            if (template.StartsWith("windows_", StringComparison.Ordinal))
            {
                needed.Add(template[..^".exe".Length] + "_console.exe");
            }
        }
        return needed;
    }

    /// <summary>The templates an export of a preset lacks in a Godot's version folder (null where no folder is known), or the
    /// template of its own it names where that is not there; none where it has what it takes.</summary>
    public static IReadOnlyList<string> Missing(GodotPreset preset, bool debug, string? folder, Func<string, bool> exists, string projectFolder)
    {
        if (preset.Custom(debug) is { } custom)
        {
            string path = custom.StartsWith("res://", StringComparison.Ordinal) ? Path.Combine(projectFolder, custom["res://".Length..].Replace('/', Path.DirectorySeparatorChar)) : custom;
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

    /// <summary>The version a template archive's templates are of, as its version file says; null where it has none.</summary>
    public static string? ArchiveVersion(ZipArchive archive) =>
        archive.GetEntry(Inside + VersionFile) is { } entry ? ReadAll(entry).Trim() : null;

    /// <summary>The templates of an archive an install takes (<paramref name="needed"/>, or all for null), by name with
    /// their sizes, in the archive's order; an entry no file of a templates folder can be named after is none of them.</summary>
    public static IReadOnlyList<(string Name, long Size)> Chosen(ZipArchive archive, IReadOnlySet<string>? needed) =>
        [.. archive.Entries.Where(e => e.FullName.StartsWith(Inside, StringComparison.Ordinal) && e.FullName.Length > Inside.Length)
            .Select(e => (Name: e.FullName[Inside.Length..], Size: e.Length)).Where(e => IsFileName(e.Name) && (needed is null || needed.Contains(e.Name)))];

    /// <summary>Whether a name is a template's file in its version's folder on every system, as all of Godot's are: letters,
    /// digits, dots, dashes and underscores, no dot at either end. Windows would read a backslash, colon or end dot of any
    /// other name as leading out of the folder, into a drive or to another file.</summary>
    private static bool IsFileName(string name) => name[0] != '.' && name[^1] != '.' && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>Installs an archive's templates of a Godot's version into its folder, each written whole as it is read
    /// (<see cref="AtomicFile"/>), the version file last so a folder cut short is never taken for one installed; null once
    /// installed, else why not: an archive of another version, or none of those asked.</summary>
    public static string? Install(string archive, string folder, string version, IReadOnlySet<string>? needed, Action<string> stage, CancellationToken cancel)
    {
        using ZipArchive zip = ZipFile.OpenRead(archive);
        string? holds = ArchiveVersion(zip);
        if (holds is null)
        {
            return $"{Path.GetFileName(archive)} holds no {Inside}{VersionFile}: it is not an archive of Godot's export templates";
        }
        if (holds != version)
        {
            return $"{Path.GetFileName(archive)} holds the templates of Godot {holds}, and the Godot found is {version}";
        }
        IReadOnlyList<(string Name, long Size)> chosen = Chosen(zip, needed);
        if (!chosen.Any(c => c.Name != VersionFile))
        {
            return $"{Path.GetFileName(archive)} holds none of the templates asked for ({Plural.Listed([.. needed ?? new HashSet<string>()])})";
        }
        Directory.CreateDirectory(folder);
        int done = 0;
        foreach ((string name, long size) in chosen.OrderBy(c => c.Name == VersionFile ? 1 : 0))
        {
            cancel.ThrowIfCancellationRequested();
            stage($"Installing {name} ({Megabytes(size)}), {++done} of {chosen.Count}");
            ZipArchiveEntry entry = zip.GetEntry(Inside + name)!;
            AtomicFile.Write(Path.Combine(folder, name), stream => Copy(entry, stream, cancel), null, AtomicFile.Temporary,
                static (temporary, target) => File.Move(temporary, target, overwrite: true));
        }
        return null;
    }

    /// <summary>A size as a person reads it, in megabytes.</summary>
    public static string Megabytes(long bytes) => $"{(bytes / (1024.0 * 1024.0)).ToString(bytes < 10 * 1024 * 1024 ? "0.0" : "0", CultureInfo.InvariantCulture)} MB";

    private static void Copy(ZipArchiveEntry entry, Stream into, CancellationToken cancel)
    {
        using Stream from = entry.Open();
        byte[] buffer = new byte[1 << 20];
        int read;
        while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            into.Write(buffer, 0, read);
        }
    }

    private static string ReadAll(ZipArchiveEntry entry)
    {
        using StreamReader reader = new(entry.Open());
        return reader.ReadToEnd();
    }
}
