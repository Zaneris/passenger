namespace Passenger.App.Containers;

/// <summary>The compose files <c>docker compose</c> reads in a folder when it is given none: the first of its file names the
/// folder has (<see cref="Names"/>, in its order), and with it the first of its override names there (<see
/// cref="OverrideNames"/>), whichever names the file has. Paths as the folder given spells them. And which files are compose
/// files at all, wherever they are (<see cref="IsCompose"/>), with the globs a YAML server takes them by.</summary>
public static class ComposeFiles
{
    /// <summary>The names a compose file's name starts with.</summary>
    private static readonly string[] Stems = ["compose", "docker-compose"];

    private static readonly string[] Extensions = [".yaml", ".yml"];

    /// <summary>What may follow a stem before more of the name: compose.override.yaml, docker-compose.prod.yml, compose-dev.yml.</summary>
    private const string Joins = ".-_";

    /// <summary>Whether a file is a compose file: YAML (.yaml, .yml) named compose or docker-compose, alone or followed by a
    /// '.', '-' or '_' and more, in any letter case; composer.yml is none.</summary>
    public static bool IsCompose(string path)
    {
        ReadOnlySpan<char> name = Path.GetFileName(path.AsSpan());
        foreach (string extension in Extensions)
        {
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            ReadOnlySpan<char> stem = name[..^extension.Length];
            foreach (string start in Stems)
            {
                if (stem.StartsWith(start, StringComparison.OrdinalIgnoreCase) && (stem.Length == start.Length || Joins.Contains(stem[start.Length])))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>The globs that take <see cref="IsCompose"/>'s files, each matched anywhere in the folder, their letters in
    /// either case: a YAML server's schema association, whose globs tell letter case apart.</summary>
    public static readonly IReadOnlyList<string> SchemaGlobs =
        [.. from stem in Stems from after in (string[])["", .. Joins.Select(j => $"{j}*")] from extension in Extensions select AnyCase(stem) + after + AnyCase(extension)];

    /// <summary>The prefix network_mode names a service by (<c>service:db</c>), whose network the service shares.</summary>
    public const string ServicePrefix = "service:";

    private static string AnyCase(string text) =>
        string.Concat(text.Select(c => char.IsLetter(c) ? $"[{char.ToLowerInvariant(c)}{char.ToUpperInvariant(c)}]" : c.ToString()));

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
