using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App;

/// <summary>What kind of place a theme source is.</summary>
public enum ThemeSourceKind
{
    /// <summary>The themes Passenger carries.</summary>
    BuiltIn,

    /// <summary>Theme files the person added one by one, each kept as it was read.</summary>
    Added,

    /// <summary>A folder on this machine holding <see cref="ThemeFiles.IndexFile"/>.</summary>
    Folder,

    /// <summary>A GitHub repository, <c>owner/name</c>, read through its raw files at its default branch.</summary>
    GitHub,

    /// <summary>An HTTPS address whose files sit under it, <see cref="ThemeFiles.IndexFile"/> among them.</summary>
    Web,
}

/// <summary>A place themes and code colour schemes come from, by the words settings keep it under: a folder's full path,
/// <c>owner/name</c>, or an address ending in <c>/</c>. Passenger's themes repository is listed from the start; a person adds
/// others in Settings.</summary>
public sealed partial record ThemeSource(ThemeSourceKind Kind, string Name)
{
    /// <summary>Passenger's themes repository, the source every Passenger lists.</summary>
    public const string RepositoryName = "Zaneris/passenger-themes";

    /// <summary>The variable naming the place Passenger's themes repository is read from in its own place: a folder, a
    /// repository or an address, as a source is written.</summary>
    [IdentifierHome]
    public const string Variable = "PASSENGER_THEMES";

    /// <summary>Where a GitHub repository's files are read, each under <c>owner/name/HEAD/</c>.</summary>
    public static readonly Uri GitHubRaw = new("https://raw.githubusercontent.com/");

    public static readonly ThemeSource BuiltIn = new(ThemeSourceKind.BuiltIn, "built-in");

    public static readonly ThemeSource Added = new(ThemeSourceKind.Added, "added");

    /// <summary>Passenger's themes repository.</summary>
    public static readonly ThemeSource Repository = new(ThemeSourceKind.GitHub, RepositoryName);

    /// <summary>How a person sees it named.</summary>
    public string Shown => Kind switch
    {
        ThemeSourceKind.BuiltIn => "Passenger",
        ThemeSourceKind.Added => "added from a file",
        _ => Name,
    };

    /// <summary>Where its files are read: a folder, or an address ending in <c>/</c>; none for the built-in and added
    /// themes, which are read where Passenger keeps them.</summary>
    public string? Place => Kind switch
    {
        ThemeSourceKind.Folder => Name,
        ThemeSourceKind.GitHub => new Uri(GitHubRaw, $"{Name}/HEAD/").AbsoluteUri,
        ThemeSourceKind.Web => Name,
        _ => null,
    };

    /// <summary>The source read in this one's place: <see cref="Variable"/>'s for Passenger's themes repository where it
    /// names one, else itself. A value there that names no source is fatal.</summary>
    public ThemeSource Reached => this == Repository && Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } named
        ? Of(named, out string? why) is { Kind: ThemeSourceKind.Folder or ThemeSourceKind.GitHub or ThemeSourceKind.Web } instead
            ? instead
            : throw new FormatException($"{Variable} takes a folder, a GitHub repository (owner/name) or an https address, not '{named}': {why}")
        : this;

    /// <summary>The themes Passenger carried before they moved to its themes repository, which the command line's
    /// <c>--look</c> names and 0.3's settings kept by these names.</summary>
    public static readonly IReadOnlyList<string> FormerlyCarried =
        ["Harbour", "Sorbet", "Nocturne", "Saffron", "Bonbon", "Ledger", "Slate", "Orbit", "Nightjar", "Moss", "Phosphor"];

    /// <summary>The code colour scheme Passenger carried before it moved to its themes repository, by its name there.</summary>
    public const string FormerScheme = "Reef";

    /// <summary>The words settings keep it under.</summary>
    public override string ToString() => Name;

    /// <summary>A theme and a code colour scheme settings chose by name when Passenger carried them (<paramref name="look"/>
    /// and <paramref name="scheme"/> naming where), the same from Passenger's themes repository now; Passenger's own and the
    /// theme's own colours, or a name no Passenger carried, no theme named.</summary>
    public static void MoveFormerChoices(JsonObject settings, string look, string scheme)
    {
        foreach ((string key, string own, Func<string, string?> named) in new (string, string, Func<string, string?>)[]
        {
            (look, "Passenger", name => FormerlyCarried.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))),
            // "Rider" is the name 0.3 kept the same scheme under.
            (scheme, "Theme", name => name.Equals("Rider", StringComparison.OrdinalIgnoreCase) ? FormerScheme : null),
        })
        {
            if (settings[key] is not JsonValue value || !value.TryGetValue(out string? name))
            {
                continue;
            }
            settings.Remove(key);
            if (!name.Equals(own, StringComparison.OrdinalIgnoreCase) && named(name) is { } moved)
            {
                settings[key] = new JsonObject { ["source"] = RepositoryName, ["name"] = moved };
            }
        }
    }

    /// <summary>The source some words name, or null with why not: a full path (or one under <c>~/</c>) for a folder,
    /// <c>owner/name</c> or a github.com address for a repository, an https address (http on this machine) for the rest, its
    /// index's name at its end left out; <c>built-in</c> and <c>added</c> for Passenger's own two.</summary>
    public static ThemeSource? Of(string words, out string? why)
    {
        why = null;
        string text = words.Trim();
        if (text == BuiltIn.Name)
        {
            return BuiltIn;
        }
        if (text == Added.Name)
        {
            return Added;
        }
        if (text.Length == 0)
        {
            why = "it names nothing: give a folder, a GitHub repository (owner/name) or an https address";
            return null;
        }
        if (text.Contains("://", StringComparison.Ordinal))
        {
            return Address(text, out why);
        }
        if (PathNames.Expanded(text) is { } home)
        {
            text = home;
        }
        if (Path.IsPathFullyQualified(text))
        {
            return new ThemeSource(ThemeSourceKind.Folder, Path.TrimEndingDirectorySeparator(Path.GetFullPath(text)));
        }
        if (RepositoryNamed().Match(text) is { Success: true } repository && repository.Groups[2].Value is not ("." or ".."))
        {
            return new ThemeSource(ThemeSourceKind.GitHub, text);
        }
        why = $"'{text}' is no full path to a folder, no GitHub repository (owner/name) and no https address";
        return null;
    }

    private static ThemeSource? Address(string text, out string? why)
    {
        why = null;
        if (WebAddress.Of(text) is not { } address)
        {
            why = $"'{text}' is no http or https address";
            return null;
        }
        if (address.Query.Length > 0 || address.Fragment.Length > 0 || address.UserInfo.Length > 0)
        {
            why = $"{address} carries a query, a fragment or a name and password: a source is the address its files sit under";
            return null;
        }
        if (address.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            string path = address.AbsolutePath.Trim('/');
            path = path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
            if (RepositoryNamed().IsMatch(path))
            {
                return new ThemeSource(ThemeSourceKind.GitHub, path);
            }
            why = $"{address} is no repository's own address on GitHub (https://github.com/owner/name)";
            return null;
        }
        if (!WebAddress.Secure(address))
        {
            why = $"{address} is not https: themes are read from https addresses, and from http on this machine alone";
            return null;
        }
        string place = address.AbsoluteUri;
        if (place.EndsWith("/" + ThemeFiles.IndexFile, StringComparison.Ordinal))
        {
            place = place[..^ThemeFiles.IndexFile.Length];
        }
        return new ThemeSource(ThemeSourceKind.Web, place.EndsWith('/') ? place : place + "/");
    }

    /// <summary>The name of the folder a source's kept themes are under: its own name's last part, as a file's name may hold
    /// it, then a hash of the source, so two sources never share one.</summary>
    public string KeptFolder => $"{Kind.ToString().ToLowerInvariant()}-{Readable(Name.Split('/', '\\', ':').LastOrDefault(p => p.Length > 0) ?? Kind.ToString())}-{Hash($"{Kind} {Name}")}";

    /// <summary>The name of the folder a theme of a name is kept in: the name as a file's name may hold it, then a hash of
    /// it whole, so names alike but for case or a character never share one.</summary>
    public static string KeptName(string name) => $"{Readable(name)}-{Hash(name)}";

    private static string Readable(string text)
    {
        StringBuilder kept = new();
        foreach (char c in text)
        {
            if (kept.Length == 32)
            {
                break;
            }
            kept.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }
        return kept.ToString().Trim('.');
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    // GitHub's rules: an owner of letters, digits and single hyphens, a repository of letters, digits, '.', '-' and '_'.
    [GeneratedRegex(@"^([A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38})/([A-Za-z0-9._-]{1,100})$")]
    private static partial Regex RepositoryNamed();
}

/// <summary>A theme or a code colour scheme as settings name it: its source, by the words <see cref="ThemeSource"/> keeps it
/// under, and its name there.</summary>
public sealed record SourcedTheme(string Source, string Name)
{
    /// <summary>A theme of Passenger's themes repository.</summary>
    public static SourcedTheme Repository(string name) => new(ThemeSource.RepositoryName, name);

    /// <summary>Its source, or null where settings name none Passenger reads.</summary>
    [JsonIgnore]
    public ThemeSource? From => ThemeSource.Of(Source, out _);

    /// <summary>How a person sees it named: the name, then its source.</summary>
    [JsonIgnore]
    public string Shown => From is { } source && source != ThemeSource.BuiltIn ? $"{Name} ({source.Shown})" : Name;
}
