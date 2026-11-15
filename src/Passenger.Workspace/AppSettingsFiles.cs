namespace Passenger.Workspace;

/// <summary>An app's settings files as .NET's host reads them from its folder: appsettings.json for every environment, then
/// appsettings.{environment}.json for the one it runs in, whose keys take the place of the first's.</summary>
public static class AppSettingsFiles
{
    /// <summary>The settings of every environment.</summary>
    public const string Base = "appsettings.json";

    /// <summary>An environment's settings: appsettings.Development.json.</summary>
    public static string Of(string environment) => $"appsettings.{environment}.json";

    /// <summary>The files an app running in any of some environments reads, as its configuration reads them: the base first,
    /// then each environment's.</summary>
    public static IReadOnlyList<string> Read(IEnumerable<string> environments) => [Base, .. environments.Select(Of)];

    /// <summary>Whether a file is one, by its name, letter case aside: appsettings.json or appsettings.{anything}.json.</summary>
    public static bool Is(string path) => Path.GetFileName(path) is var name
        && (name.Equals(Base, StringComparison.OrdinalIgnoreCase) || EnvironmentOf(name) is not null);

    /// <summary>The environment a settings file is of, by its name (Development for appsettings.Development.json); null for
    /// the base or any other file.</summary>
    public static string? EnvironmentOf(string path)
    {
        string name = Path.GetFileName(path);
        const string Start = "appsettings.", End = ".json";
        return name.Length > Start.Length + End.Length && name.StartsWith(Start, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(End, StringComparison.OrdinalIgnoreCase)
            ? name[Start.Length..^End.Length] : null;
    }
}
