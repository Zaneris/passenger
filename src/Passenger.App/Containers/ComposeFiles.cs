namespace Passenger.App.Containers;

/// <summary>The compose files <c>docker compose</c> reads in a folder when it is given none: the first of its file names the
/// folder has (<see cref="Names"/>, in its order), and with it the first of its override names there (<see
/// cref="OverrideNames"/>), whichever names the file has. Paths as the folder given spells them.</summary>
public static class ComposeFiles
{
    /// <summary>The names a compose file has, as Compose looks for them in a folder, in its order.</summary>
    public static readonly IReadOnlyList<string> Names = ["compose.yaml", "compose.yml", "docker-compose.yml", "docker-compose.yaml"];

    /// <summary>The names an override has, as Compose looks for one beside the file it reads, in its order.</summary>
    public static readonly IReadOnlyList<string> OverrideNames = ["compose.override.yml", "compose.override.yaml", "docker-compose.override.yml", "docker-compose.override.yaml"];

    /// <summary>The file Compose reads in a folder; null where it has none.</summary>
    public static string? In(string folder) => First(folder, Names);

    /// <summary>The override Compose reads with the file it reads in a folder; null where it has none.</summary>
    public static string? OverrideIn(string folder) => First(folder, OverrideNames);

    /// <summary>The files Compose reads in a folder: its file, then its override where there is one; none without a file.</summary>
    public static IReadOnlyList<string> Unasked(string folder) =>
        In(folder) is { } file ? OverrideIn(folder) is { } over ? [file, over] : [file] : [];

    private static string? First(string folder, IReadOnlyList<string> names) => names.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
}
