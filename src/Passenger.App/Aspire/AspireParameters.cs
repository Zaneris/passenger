using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.App.Workspace;
using Passenger.Workspace;

namespace Passenger.App.Aspire;

/// <summary>An AppHost as its parameters' values are read: its project file (or single file) and its UserSecretsId as
/// MSBuild evaluated it, none for one without.</summary>
public sealed record AspireAppHostSettings(string AppHost, string? UserSecretsId)
{
    public string Folder => Path.GetDirectoryName(AppHost)!;

    /// <summary>Its user secrets' file, there or not, by its id (<see cref="UserSecrets.Of"/>); null without one.</summary>
    public string? SecretsIn(Func<string, string?> text) => UserSecrets.FileOf(UserSecrets.Of(AppHost, UserSecretsId, text));

    /// <summary>Its appsettings.json, there or not.</summary>
    public string Settings => Path.Combine(Folder, "appsettings.json");
}

/// <summary>An AppHost's parameters' values where Passenger can know them before it runs: <c>Parameters:NAME</c> in its
/// folder's appsettings*.json and in its user secrets (an environment variable or the command line only a run has), and the
/// change that sets one: in the user secrets for a secret, else in appsettings.json, every other character of the file
/// kept. A value is never read back out of either, only whether it is there.</summary>
public static class AspireParameters
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Whether the AppHost's configuration gives a parameter a value, read from <paramref name="text"/> (an open
    /// file's text as it stands, else the disk's).</summary>
    public static bool HasValue(AspireAppHostSettings host, string name, Func<string, string?> text)
    {
        IEnumerable<string> settings;
        try
        {
            settings = Directory.Exists(host.Folder) ? Directory.EnumerateFiles(host.Folder, "appsettings*.json").ToList() : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            settings = [];
        }
        return settings.Append(host.SecretsIn(text)).OfType<string>().Any(file => Holds(text(file), name));
    }

    /// <summary>Whether a configuration file's text gives Parameters:NAME a value, its keys read as .NET's configuration
    /// reads them (nested objects joined by ':', letter case aside), the name also with its dashes as underscores.</summary>
    private static bool Holds(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        HashSet<string> wanted = new(StringComparer.OrdinalIgnoreCase) { $"Parameters:{name}", $"Parameters:{name.Replace('-', '_')}" };
        try
        {
            using JsonDocument read = JsonDocument.Parse(text, Lenient);
            return Keys(read.RootElement, string.Empty).Any(wanted.Contains);
        }
        catch (JsonException)
        {
            return false;
        }

        static IEnumerable<string> Keys(JsonElement element, string prefix)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    foreach (string key in Keys(property.Value, prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}"))
                    {
                        yield return key;
                    }
                }
            }
            else if (element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Array or JsonValueKind.Undefined) && prefix.Length > 0)
            {
                yield return prefix;
            }
        }
    }

    /// <summary>A configuration file's text with a parameter's value set (a new file's whole text when it has none), or why
    /// it cannot be: where the file has its key already (with no value, null), there, flat or in its Parameters object,
    /// so the configuration never reads the key twice; else in user secrets for a secret (as <c>dotnet user-secrets set</c>
    /// writes them, beside a Parameters object when the file has one), else in appsettings.json's Parameters object.</summary>
    public static (string? Text, string? Problem) Setting(string path, string? was, string name, bool secret, string value)
    {
        if (string.IsNullOrWhiteSpace(was))
        {
            JsonObject made = secret ? new JsonObject { [$"Parameters:{name}"] = value } : new JsonObject { ["Parameters"] = new JsonObject { [name] = value } };
            return (made.ToJsonString(new JsonSerializerOptions(JsonText.Written) { WriteIndented = true }) + "\n", null);
        }
        (string? changed, string? problem) = KeyIn(was, name) switch
        {
            ({ } flat, null) => JsonText.SetInRoot(was, flat, null, value),
            ({ } parameters, { } entry) => JsonText.SetInRoot(was, parameters, entry, value),
            _ => !secret || NestsParameters(was) ? JsonText.SetInRoot(was, "Parameters", name, value) : JsonText.SetInRoot(was, $"Parameters:{name}", null, value),
        };
        return changed is null ? (null, $"{Path.GetFileName(path)} was not changed: {problem}") : (changed, null);
    }

    /// <summary>Where a configuration file's root has a parameter's key, as written: a flat <c>Parameters:NAME</c> member
    /// (its entry null), or the Parameters object's member and its NAME entry, letter case aside, the name also with its
    /// dashes as underscores, the last of each that .NET's configuration reads; nulls where it has none.</summary>
    private static (string? Member, string? Entry) KeyIn(string text, string name)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { name, name.Replace('-', '_') };
        try
        {
            using JsonDocument read = JsonDocument.Parse(text, Lenient);
            if (read.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }
            (string? Member, string? Entry) found = (null, null);
            foreach (JsonProperty member in read.RootElement.EnumerateObject())
            {
                if (member.Name.Split(':', 2) is [{ } section, { } key] && section.Equals("Parameters", StringComparison.OrdinalIgnoreCase) && names.Contains(key))
                {
                    found = (member.Name, null);
                }
                else if (member.Name.Equals("Parameters", StringComparison.OrdinalIgnoreCase) && member.Value.ValueKind == JsonValueKind.Object
                    && member.Value.EnumerateObject().Select(entry => entry.Name).LastOrDefault(names.Contains) is { } entry)
                {
                    found = (member.Name, entry);
                }
            }
            return found;
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Whether a configuration file keeps its parameters in a Parameters object rather than as flat keys.</summary>
    private static bool NestsParameters(string text)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(text, Lenient);
            return read.RootElement.ValueKind == JsonValueKind.Object && read.RootElement.EnumerateObject()
                .Any(p => p.Name.Equals("Parameters", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
