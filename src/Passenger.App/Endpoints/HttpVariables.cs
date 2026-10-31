using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App.Endpoints;

/// <summary>The environments of an .http file: <c>http-client.env.json</c> beside it or in a folder over it up to the
/// open folder, its <c>http-client.private.env.json</c> laid over it, each an object of environments
/// (<c>{"dev": {"host": "localhost:5000"}}</c>), and the <c>$shared</c> one that every environment starts from.</summary>
public static partial class HttpVariables
{
    public const string PublicFile = "http-client.env.json";
    public const string PrivateFile = "http-client.private.env.json";
    private const string Shared = "$shared";

    /// <summary>Whether a change to this file changes what the Endpoints window shows or sends.</summary>
    public static bool Matters(string path) =>
        HttpFile.Is(path) || Path.GetFileName(path) is PublicFile or PrivateFile;

    /// <summary>The environments named in the folder's env files, in the order first written.</summary>
    public static IReadOnlyList<string> Environments(IEnumerable<string> envFiles)
    {
        List<string> names = [];
        foreach (string file in envFiles)
        {
            foreach (string name in Read(file).Keys)
            {
                if (name != Shared && !names.Contains(name))
                {
                    names.Add(name);
                }
            }
        }
        return names;
    }

    /// <summary>The env files that hold for an .http file: the nearest folder's from it up to the root that has any,
    /// the public first and the private over it.</summary>
    public static IReadOnlyList<string> FilesFor(string httpFile, string? root)
    {
        for (string? folder = Path.GetDirectoryName(httpFile); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            string[] found = [.. new[] { PublicFile, PrivateFile }.Select(n => Path.Combine(folder, n)).Where(File.Exists)];
            if (found.Length > 0)
            {
                return found;
            }
            if (root is null || Core.PathNames.Same(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(root)))
            {
                break;
            }
        }
        return [];
    }

    /// <summary>Every variable a request of this file sees in an environment: the environment's ($shared first), then
    /// the file's own (<c>@name = value</c>), which win.</summary>
    public static Dictionary<string, string> For(HttpFile file, string? environment, string? root)
    {
        Dictionary<string, string> variables = new(StringComparer.Ordinal);
        foreach (string envFile in FilesFor(file.Path, root))
        {
            Dictionary<string, Dictionary<string, string>> environments = Read(envFile);
            foreach (string name in new[] { Shared, environment }.OfType<string>())
            {
                if (environments.TryGetValue(name, out Dictionary<string, string>? values))
                {
                    foreach ((string key, string value) in values)
                    {
                        variables[key] = value;
                    }
                }
            }
        }
        foreach ((string key, string value) in file.Variables)
        {
            variables[key] = value;
        }
        return variables;
    }

    private static Dictionary<string, Dictionary<string, string>> Read(string envFile)
    {
        Dictionary<string, Dictionary<string, string>> environments = new(StringComparer.Ordinal);
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(envFile), LenientJson.Document);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return environments;
            }
            foreach (JsonProperty environment in document.RootElement.EnumerateObject())
            {
                if (environment.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                Dictionary<string, string> values = new(StringComparer.Ordinal);
                foreach (JsonProperty variable in environment.Value.EnumerateObject())
                {
                    values[variable.Name] = variable.Value.ValueKind == JsonValueKind.String ? variable.Value.GetString()! : variable.Value.GetRawText();
                }
                environments[environment.Name] = values;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // A file being written, or one half-typed: its environments come back when it reads.
        }
        return environments;
    }

    /// <summary>The most a request's variables may fill in, in characters, so a file whose variables name each other
    /// over and over (each twice, ten deep) cannot hold the UI thread up as it is sent.</summary>
    public const int MostFilledIn = 1024 * 1024;

    /// <summary>One request's filling in: the names no value was found for, the person's environment variables it read
    /// (those not set among them), and how much is left to fill in.</summary>
    public sealed class Filling
    {
        public HashSet<string> Unresolved { get; } = new(StringComparer.Ordinal);

        /// <summary>The environment variables read, through the file's variables and the env files' values alike.</summary>
        public SortedSet<string> Environment { get; } = new(StringComparer.Ordinal);

        /// <summary>Those of <see cref="Environment"/> that are not set, each filled in empty.</summary>
        public HashSet<string> Unset { get; } = new(StringComparer.Ordinal);

        public int Left { get; set; } = MostFilledIn;

        /// <summary>Whether the variables came to more than <see cref="MostFilledIn"/>, the rest left as written.</summary>
        public bool Overflowed => Left < 0;
    }

    /// <summary>Text with its <c>{{name}}</c> replaced, variables that name others followed, and the dynamic ones made anew
    /// each time ($uuid, $timestamp, $isoTimestamp, $randomInt, $processEnv NAME); the names it could not resolve are added
    /// to the filling's <see cref="Filling.Unresolved"/> and left as written.</summary>
    public static string Resolve(string text, IReadOnlyDictionary<string, string> variables, Filling filling, int depth = 0) =>
        Reference().Replace(text, match =>
        {
            if (filling.Overflowed)
            {
                return match.Value;
            }
            string name = match.Groups[1].Value.Trim();
            string? filled = Dynamic(name, filling);
            if (filled is null && depth < 10 && variables.TryGetValue(name, out string? value))
            {
                filled = Resolve(value, variables, filling, depth + 1);
            }
            if (filled is null)
            {
                filling.Unresolved.Add(name);
                return match.Value;
            }
            // Every reference costs at least one, so a thousand empty ones cost as much as a thousand characters.
            filling.Left -= Math.Max(1, filled.Length);
            return filling.Overflowed ? match.Value : filled;
        });

    private static string? Dynamic(string name, Filling filling)
    {
        if (name.StartsWith("$processEnv ", StringComparison.Ordinal) || name.StartsWith("$env.", StringComparison.Ordinal))
        {
            string variable = (name.StartsWith("$env.", StringComparison.Ordinal) ? name[5..] : name[12..]).Trim();
            filling.Environment.Add(variable);
            if (Environment.GetEnvironmentVariable(variable) is { } set)
            {
                return set;
            }
            filling.Unset.Add(variable);
            return string.Empty;
        }
        return name switch
        {
            "$uuid" or "$guid" or "$random.uuid" => Guid.NewGuid().ToString(),
            "$timestamp" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "$isoTimestamp" => DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            "$randomInt" => Random.Shared.Next(0, 1000).ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    [GeneratedRegex(@"\{\{(.+?)\}\}")]
    private static partial Regex Reference();
}
