using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>A project as its configuration is given before it runs: its file, its UserSecretsId as MSBuild evaluated it (else
/// as its file sets it, read with the rest), the folders whose compose files may run it (its own up to the folder open) and
/// those of the AppHosts that run it as a service, whose files are read off the UI thread.</summary>
public sealed record ConfigurationProject(string Project, string? UserSecretsId, IReadOnlyList<string> ComposeFolders, IReadOnlyList<string> AppHostFolders)
{
    public string Name => Path.GetFileNameWithoutExtension(Project);

    public string Folder => Path.GetDirectoryName(Project)!;

    /// <summary>Its user secrets' file, there or not, by its id (<see cref="UserSecrets.Of"/>); null without one.</summary>
    public string? SecretsIn(Func<string, string?> text) =>
        UserSecrets.FileOf(UserSecrets.Of(Project, UserSecretsId, text)) is { } file ? PathNames.Canonical(file) : null;

    /// <summary>Its appsettings.json, there or not.</summary>
    public string Settings => Path.Combine(Folder, "appsettings.json");

    public string LaunchSettings => Run.LaunchProfile.SettingsIn(Folder);
}

/// <summary>What gives a configuration key: a settings file of the project's folder, a launch profile's variable, the
/// project's user secrets, a compose file's environment or an AppHost's, or the environment Passenger runs in.</summary>
public enum GivenBy
{
    Settings,
    LaunchProfile,
    UserSecrets,
    Compose,
    AppHost,
    Environment,
}

/// <summary>A configuration key given (sections joined by ':'), the file that gives it and the stretch of its name there
/// (none for the environment's), and its value as written (a string's characters, a JSON value's text).</summary>
public sealed record GivenKey(string Key, GivenBy By, string? File, int Start, int End, string? Value)
{
    /// <summary>Its value as a person may be shown it: a user secret's always as dots, any other masked by Passenger's rule for
    /// secrets (<see cref="Containers.ContainerSecrets.Shown"/>).</summary>
    public string Shown => Value is null ? string.Empty
        : By == GivenBy.UserSecrets && Value.Length > 0 ? Containers.ContainerSecrets.Masked
        : Containers.ContainerSecrets.Shown(Key.Split(':')[^1], Value);
}

/// <summary>Where a project's configuration keys are given before it runs, read as .NET's configuration reads them (keys
/// joined by ':', letter case aside): its appsettings.json and appsettings.{environment}.json for each environment its launch
/// profiles run in (Development where none says), its launch profiles' variables (<c>A__B</c>), its user secrets, the compose
/// files beside it and above it that set <c>A__B</c>, an AppHost that names it, and Passenger's own environment, which a run
/// inherits. What a key nothing gives is warned of against, completion leaves out as given, and Go to Declaration goes to.</summary>
public static partial class ConfigurationFiles
{
    /// <summary>Whether a file is a settings file .NET's host reads: appsettings.json or appsettings.{anything}.json.</summary>
    public static bool IsSettings(string path) =>
        Path.GetFileName(path) is { } name && name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && (name.Length == "appsettings.json".Length || name["appsettings".Length] == '.');

    /// <summary>The user secrets id whose secrets.json a file is, under the secrets' folder (<see cref="UserSecrets.Root"/>);
    /// null for any other file.</summary>
    public static string? SecretsIdOf(string path)
    {
        if (!Path.GetFileName(path).Equals("secrets.json", StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(path) is not { } folder
            || Path.GetDirectoryName(folder) is not { } root)
        {
            return null;
        }
        return PathNames.Same(PathNames.Canonical(root), PathNames.Canonical(UserSecrets.Root)) ? Path.GetFileName(folder) : null;
    }

    /// <summary>The environments a project runs in from its launch profiles (ASPNETCORE_ENVIRONMENT, DOTNET_ENVIRONMENT),
    /// Development for a profile that names none, or where it has none.</summary>
    public static IReadOnlyList<string> Environments(ConfigurationProject project, Func<string, string?> text)
    {
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (Run.LaunchProfile profile in Run.LaunchProfile.Parse(text(project.LaunchSettings)))
        {
            string? named = profile.EnvironmentVariables.FirstOrDefault(variable => variable.Key is "ASPNETCORE_ENVIRONMENT" or "DOTNET_ENVIRONMENT").Value;
            found.Add(string.IsNullOrWhiteSpace(named) ? "Development" : named);
        }
        return found.Count == 0 ? ["Development"] : [.. found];
    }

    /// <summary>Every key the project's sources give, with where.</summary>
    public static IReadOnlyList<GivenKey> Given(ConfigurationProject project, Func<string, string?> text)
    {
        List<GivenKey> given = [];
        foreach (string file in Environments(project, text).Select(e => $"appsettings.{e}.json").Prepend("appsettings.json"))
        {
            string path = Path.Combine(project.Folder, file);
            given.AddRange(Flatten(text(path), path, GivenBy.Settings));
        }
        given.AddRange(LaunchVariables(text(project.LaunchSettings), project.LaunchSettings));
        if (project.SecretsIn(text) is { } secrets)
        {
            given.AddRange(Flatten(text(secrets), secrets, GivenBy.UserSecrets));
        }
        foreach (string compose in project.ComposeFolders.SelectMany(f => FilesIn(f, "*.y*ml")).Where(IsCompose))
        {
            given.AddRange(Variables(text(compose), compose, GivenBy.Compose));
        }
        foreach (string host in project.AppHostFolders.SelectMany(f => FilesIn(f, "*.cs")))
        {
            given.AddRange(Variables(text(host), host, GivenBy.AppHost));
        }
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && name.Contains("__", StringComparison.Ordinal))
            {
                given.Add(new GivenKey(name.Replace("__", ":", StringComparison.Ordinal), GivenBy.Environment, null, 0, 0, variable.Value as string));
            }
        }
        return given;
    }

    /// <summary>A compose file Compose reads unasked or one named for it (compose.override.yaml, docker-compose.prod.yml).</summary>
    private static bool IsCompose(string path) =>
        Path.GetFileName(path) is { } name && (name.StartsWith("compose", StringComparison.OrdinalIgnoreCase) || name.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> FilesIn(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, pattern)] : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Whether the keys given hold one, letter case aside: the key itself with a value, or for a section any key under it.</summary>
    public static bool Gives(IReadOnlyList<GivenKey> given, string key, bool section) =>
        given.Any(g => g.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
            || section && g.Key.Length > key.Length && g.Key[key.Length] == ':' && g.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The keys given that a read's key names, or a section's (any under it), the settings' first.</summary>
    public static IReadOnlyList<GivenKey> Where(IReadOnlyList<GivenKey> given, string key, bool section) =>
        [.. given.Where(g => g.File is not null && (g.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
            || section && g.Key.Length > key.Length && g.Key[key.Length] == ':' && g.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>Whether a key of the code's (<c>*</c> for any item or dictionary key) names a key written in a file, segment by
    /// segment, letter case aside.</summary>
    public static bool Matches(string pattern, IReadOnlyList<string> key)
    {
        string[] wanted = pattern.Split(':');
        return wanted.Length == key.Count && wanted.Zip(key).All(p => p.First == "*" || p.First.Equals(p.Second, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A JSON configuration file's keys as .NET reads them: nested objects' names joined by ':', an array's items by
    /// their index, each with its name's place (an item's, its value's) and its value; none for a file that is not there or
    /// does not read.</summary>
    public static IReadOnlyList<GivenKey> Flatten(string? text, string file, GivenBy by)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        JsonText.Node root;
        try
        {
            root = new JsonText.Reader(text).Document();
        }
        catch (FormatException)
        {
            return [];
        }
        List<GivenKey> keys = [];
        Walk(root, string.Empty, 0, 0);
        return keys;

        void Walk(JsonText.Node node, string prefix, int start, int end)
        {
            switch (node.Kind)
            {
                case JsonText.Kind.Object:
                    foreach (JsonText.Member member in node.Members)
                    {
                        Walk(member.Value, prefix.Length == 0 ? member.Name : $"{prefix}:{member.Name}", member.Start, member.Start + Quoted(text, member.Start));
                    }
                    break;
                case JsonText.Kind.Array:
                    for (int i = 0; i < node.Items.Count; i++)
                    {
                        Walk(node.Items[i], $"{prefix}:{i}", node.Items[i].Start, node.Items[i].End);
                    }
                    break;
                default:
                    string written = text[node.Start..node.End];
                    if (prefix.Length > 0 && written != "null")
                    {
                        keys.Add(new GivenKey(prefix, by, file, start, end, written.StartsWith('"') ? Unquoted(written) : written));
                    }
                    break;
            }
        }
    }

    /// <summary>The launch profiles' variables, each a key with <c>__</c> read as ':'.</summary>
    private static IEnumerable<GivenKey> LaunchVariables(string? text, string file)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }
        JsonText.Node root;
        try
        {
            root = new JsonText.Reader(text).Document();
        }
        catch (FormatException)
        {
            yield break;
        }
        if (root.Member("profiles")?.Value is not { Kind: JsonText.Kind.Object } profiles)
        {
            yield break;
        }
        foreach (JsonText.Member profile in profiles.Members)
        {
            if (profile.Value.Member("environmentVariables")?.Value is not { Kind: JsonText.Kind.Object } variables)
            {
                continue;
            }
            foreach (JsonText.Member variable in variables.Members)
            {
                string written = text[variable.Value.Start..variable.Value.End];
                yield return new GivenKey(variable.Name.Replace("__", ":", StringComparison.Ordinal), GivenBy.LaunchProfile, file, variable.Start,
                    variable.Start + Quoted(text, variable.Start), written.StartsWith('"') ? Unquoted(written) : written);
            }
        }
    }

    /// <summary>The variables a compose file or an AppHost's code names as configuration keys (<c>Payments__ApiKey</c>), each a
    /// key with <c>__</c> read as ':'; their values are the run's, not read here.</summary>
    private static IEnumerable<GivenKey> Variables(string? text, string file, GivenBy by)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }
        foreach (Match variable in Variable().Matches(text))
        {
            Group name = variable.Groups["name"];
            yield return new GivenKey(name.Value.Replace("__", ":", StringComparison.Ordinal), by, file, name.Index, name.Index + name.Length, null);
        }
    }

    [GeneratedRegex(@"(?<![\w.:-])(?<name>[A-Za-z_][\w.-]*__[\w.:-]+?)(?=\s*[:=""])")]
    private static partial Regex Variable();

    /// <summary>How long a quoted name is from its opening quote, both quotes in.</summary>
    private static int Quoted(string text, int start)
    {
        int at = start + 1;
        while (at < text.Length && text[at] != '"')
        {
            at += text[at] == '\\' ? 2 : 1;
        }
        return Math.Min(at + 1, text.Length) - start;
    }

    private static string Unquoted(string written)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(written) ?? string.Empty;
        }
        catch (JsonException)
        {
            return written.Trim('"');
        }
    }
}
