namespace Passenger.Workspace;

/// <summary>An editor plugin of a Godot project (<c>addons/name/plugin.cfg</c>): the res:// path project.godot's
/// <c>[editor_plugins]</c> enables it by, its file, its name, version, author and script as its <c>[plugin]</c> section
/// says them, and whether the project enables it.</summary>
public sealed record GodotPlugin(string Res, string File, string Name, string? Version, string? Author, string? Script, bool Enabled);

/// <summary>The editor plugins under a Godot project's <c>addons/</c>, each folder's plugin.cfg read as Godot's ConfigFile
/// text through <see cref="GodotFile"/>, and those its project.godot enables.</summary>
public static class GodotPlugins
{
    /// <summary>The section and key of project.godot that list the plugins enabled.</summary>
    public const string Section = "editor_plugins";

    public const string Key = "enabled";

    /// <summary>The plugins of a project's folder by their folders' names, each enabled where <paramref name="enabled"/>
    /// lists its res:// path; one whose plugin.cfg cannot be read named by its folder.</summary>
    public static IReadOnlyList<GodotPlugin> Read(string folder, IReadOnlyList<string> enabled)
    {
        string addons = Path.Combine(folder, "addons");
        List<GodotPlugin> plugins = [];
        try
        {
            if (!Directory.Exists(addons))
            {
                return plugins;
            }
            foreach (string own in Directory.EnumerateDirectories(addons).Order(StringComparer.Ordinal))
            {
                string file = Path.Combine(own, "plugin.cfg");
                if (!System.IO.File.Exists(file))
                {
                    continue;
                }
                string res = $"res://addons/{Path.GetFileName(own)}/plugin.cfg";
                GodotSection? plugin = null;
                try
                {
                    plugin = GodotFile.Parse(System.IO.File.ReadAllText(file)).Sections.FirstOrDefault(s => s.Tag == "plugin");
                }
                catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
                {
                    // A plugin.cfg that cannot be read is named by its folder.
                }
                string? Text(string key) => plugin?.Property(key)?.Value.Text is { Length: > 0 } text ? text : null;
                plugins.Add(new GodotPlugin(res, file, Text("name") ?? Path.GetFileName(own), Text("version"), Text("author"), Text("script"), enabled.Contains(res)));
            }
        }
        catch (Exception unlisted) when (unlisted is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed holds no plugin Passenger knows.
        }
        return plugins;
    }
}
