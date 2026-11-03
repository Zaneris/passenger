namespace Passenger.App.Godot;

/// <summary>Passenger's addon for Godot's editor as Passenger carries it (<c>Godot/Addon</c>): each file by its name in the
/// addon's folder, with its text.</summary>
public static class GodotAddonFiles
{
    private static readonly Lazy<IReadOnlyList<(string Name, string Text)>> Read = new(() => [.. ((string[])["plugin.cfg", Passenger.Workspace.GodotAddon.Script, "scenes.gd"]).Select(name =>
    {
        using Stream stream = typeof(GodotAddonFiles).Assembly.GetManifestResourceStream($"Godot/Addon/{name}")
            ?? throw new InvalidOperationException($"Passenger carries no Godot/Addon/{name}");
        using StreamReader reader = new(stream);
        return (name, reader.ReadToEnd());
    })]);

    /// <summary>The files, as written into a project.</summary>
    public static IReadOnlyList<(string Name, string Text)> Carried => Read.Value;
}
