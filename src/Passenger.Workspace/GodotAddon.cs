namespace Passenger.Workspace;

/// <summary>A file of Passenger's addon as an install writes it: its path, its text, and whether it is new to the project.</summary>
public sealed record GodotAddonFile(string Path, string Text, bool Made);

/// <summary>What installing Passenger's addon in a Godot project writes: its files where they differ from Passenger's, in
/// the folder an earlier copy sits in (else <c>addons/passenger</c>), and project.godot's text enabling it (null where it
/// is enabled already); none of either where the copy there is a newer Passenger's (<see cref="Newer"/>).</summary>
public sealed record GodotAddonInstall(string Folder, string Res, IReadOnlyList<GodotAddonFile> Files, string? Project, bool Updated, string? Newer)
{
    /// <summary>Whether the project has the addon as Passenger carries it, enabled.</summary>
    public bool Done => Files.Count == 0 && Project is null;
}

/// <summary>Passenger's addon for Godot's editor in a project: found by its plugin.cfg's name and script, wherever its
/// folder under <c>addons/</c> is, so a copy an earlier Passenger wrote is updated in place and never doubled.</summary>
public static class GodotAddon
{
    /// <summary>The name its plugin.cfg gives it.</summary>
    public const string Name = "Passenger";

    /// <summary>The script its plugin.cfg names.</summary>
    public const string Script = "passenger.gd";

    /// <summary>Its folder under <c>addons/</c> where no copy is there yet.</summary>
    public const string FolderName = "passenger";

    /// <summary>Whether a project's plugin is Passenger's addon.</summary>
    public static bool Is(GodotPlugin plugin) => plugin.Name == Name && plugin.Script == Script;

    /// <summary>The install of the files Passenger carries (each by its name in the addon's folder, its plugin.cfg giving
    /// its version) into a project, whose plugins and project.godot's text are given, a file's text now read by
    /// <paramref name="read"/> (null where there is none).</summary>
    public static GodotAddonInstall Plan(string project, IReadOnlyList<(string Name, string Text)> carried, IReadOnlyList<GodotPlugin> plugins,
        string projectText, Func<string, string?> read)
    {
        GodotPlugin? had = plugins.FirstOrDefault(Is);
        string folder = had is null ? Path.Combine(project, "addons", FolderName) : Path.GetDirectoryName(had.File)!;
        string res = had?.Res ?? $"res://addons/{FolderName}/plugin.cfg";
        string? carriedVersion = carried.Where(c => c.Name == "plugin.cfg").Select(c => VersionIn(c.Text)).FirstOrDefault();
        if (had?.Version is { } version && Number(version) is int there && carriedVersion is not null && Number(carriedVersion) is int ours && there > ours)
        {
            return new GodotAddonInstall(folder, res, [], null, true, version);
        }
        List<GodotAddonFile> files = [];
        foreach ((string name, string text) in carried)
        {
            string path = Path.Combine(folder, name);
            string? now = read(path);
            if (now is null || now.ReplaceLineEndings("\n") != text.ReplaceLineEndings("\n"))
            {
                files.Add(new GodotAddonFile(path, text, now is null));
            }
        }
        List<string> enabled = Enabled(projectText);
        string? written = enabled.Contains(res, StringComparer.Ordinal) ? null : GodotWrites.WithPlugins(projectText, [.. enabled, res]);
        return new GodotAddonInstall(folder, res, files, written, had is not null, null);
    }

    /// <summary>The plugins project.godot's <c>[editor_plugins]</c> enables, by their res:// paths.</summary>
    public static List<string> Enabled(string projectText)
    {
        GodotFile read = GodotFile.Parse(projectText);
        return read.Sections.FirstOrDefault(s => s.Tag == GodotPlugins.Section)?.Property(GodotPlugins.Key) is { } listed
            ? [.. GodotDefinitions.StringsIn(projectText, listed.Value).Select(s => s.Text)] : [];
    }

    /// <summary>The version a plugin.cfg's text gives, or null.</summary>
    public static string? VersionIn(string pluginCfg) =>
        GodotFile.Parse(pluginCfg).Sections.FirstOrDefault(s => s.Tag == "plugin")?.Property("version")?.Value.Text is { Length: > 0 } version ? version : null;

    private static int? Number(string version) => int.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number)
        ? number : null;
}
