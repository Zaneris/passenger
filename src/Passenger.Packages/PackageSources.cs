using System.Text;
using System.Xml;
using System.Xml.Linq;
using NuGet.Common;
using NuGet.Configuration;
using Passenger.Core;

namespace Passenger.Packages;

/// <summary>Where a nuget.config file stands in NuGet's order: the solution's folder, a folder above it, the user's own,
/// or the machine's, which is never written.</summary>
public enum ConfigPlace
{
    Solution,
    Folder,
    User,
    Machine,
}

/// <summary>A user name and password for a feed, held in memory only and written nowhere but the nuget.config a person
/// saves them to. Its text names the user and never the password, so a log line or a record printed leaks nothing.</summary>
public sealed class FeedCredentials(string username, string password)
{
    public string Username { get; } = username;

    public string Password { get; } = password;

    /// <summary>The value of an HTTP Basic Authorization header.</summary>
    public string Basic() => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));

    public override string ToString() => $"credentials of {Username}";
}

/// <summary>A feed as one nuget.config names it in its packageSources: its key and value as written, whether the file
/// disables it, whether the file holds credentials for it (and whose), and its insecure-connection switch.</summary>
public sealed record ConfigFeed(string Name, string Value, bool DisabledHere, string? Username, bool HasPassword, bool AllowInsecure);

/// <summary>A nuget.config in effect for the folder, as the Sources tab lists it: its feeds, the feeds it disables
/// (its own or another file's), whether it clears the feeds of the files after it, and its globalPackagesFolder and
/// repositoryPath; or why it does not read.</summary>
public sealed record ConfigFile(string Path, ConfigPlace Place, IReadOnlyList<ConfigFeed> Feeds, IReadOnlyList<string> DisabledNames, bool Clears,
    string? GlobalPackagesFolder, string? RepositoryPath, string? Problem)
{
    public bool ReadOnly => Place == ConfigPlace.Machine;

    public bool Disables(string feed) => DisabledNames.Contains(feed, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A feed in effect, as NuGet's own reading of the files gives it: its name, where it is (a local folder made
/// absolute, or a URL), whether it is enabled, the file that names it, and its credentials from packageSourceCredentials
/// or a NuGetPackageSourceCredentials_ variable.</summary>
public sealed record FeedSource(string Name, string Source, bool Enabled, string? DefinedIn, int ProtocolVersion, bool AllowInsecure, bool SkipCertificateCheck, FeedCredentials? Credentials)
{
    public bool IsHttp => Source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The package sources a folder's restore uses, read through NuGet's own configuration, with its
/// packageSourceMapping. Read off the window's thread; a snapshot, read again after any change.</summary>
public sealed class PackageSourceSet
{
    private readonly PackageSourceMapping? mapping;

    private PackageSourceSet(string root, IReadOnlyList<ConfigFile> files, IReadOnlyList<FeedSource> feeds, PackageSourceMapping? mapping,
        IReadOnlyList<(string Feed, IReadOnlyList<string> Patterns)> patterns, string? problem, IReadOnlyList<FeedSource>? audits = null)
    {
        Root = root;
        Files = files;
        Feeds = feeds;
        AuditSources = audits ?? [];
        this.mapping = mapping;
        MappingPatterns = mapping?.IsEnabled == true ? patterns : [];
        Problem = problem;
    }

    /// <summary>Before anything is read: no files and no feeds.</summary>
    public static PackageSourceSet None { get; } = new(string.Empty, [], [], null, [], null);

    /// <summary>The folder the files were looked for from.</summary>
    public string Root { get; }

    /// <summary>The files in effect, NuGet's order: the folder's first, the machine's last.</summary>
    public IReadOnlyList<ConfigFile> Files { get; }

    /// <summary>Every feed in effect, enabled or not, in the order NuGet takes them.</summary>
    public IReadOnlyList<FeedSource> Feeds { get; }

    public IEnumerable<FeedSource> Enabled => Feeds.Where(f => f.Enabled);

    /// <summary>The feeds the files' auditSources name, asked for known vulnerabilities in place of the package sources;
    /// none when no file names any.</summary>
    public IReadOnlyList<FeedSource> AuditSources { get; }

    /// <summary>Where vulnerable packages are learnt of: the audit sources, else every enabled package source.</summary>
    public IReadOnlyList<FeedSource> Audited => AuditSources.Count > 0 ? AuditSources : [.. Enabled];

    /// <summary>Why the files could not be read as a whole, or null.</summary>
    public string? Problem { get; }

    /// <summary>Whether a packageSourceMapping names the feeds each package may come from.</summary>
    public bool Mapped => mapping?.IsEnabled == true;

    /// <summary>The mapping's patterns by feed, as the files give them.</summary>
    public IReadOnlyList<(string Feed, IReadOnlyList<string> Patterns)> MappingPatterns { get; }

    /// <summary>Whether a package may come from a feed: always without a mapping, else when a pattern of that feed's is
    /// the package's longest match, as restore decides.</summary>
    public bool Allows(string feed, string id) =>
        mapping?.IsEnabled != true || mapping.GetConfiguredPackageSources(id).Contains(feed, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the sources for a folder; NuGet makes the user's nuget.config, naming nuget.org, when there is
    /// none.</summary>
    public static PackageSourceSet Load(string root)
    {
        ISettings settings;
        try
        {
            settings = NuGet.Configuration.Settings.LoadDefaultSettings(root);
        }
        catch (Exception failed) when (failed is NuGetConfigurationException or IOException or UnauthorizedAccessException or XmlException)
        {
            return new PackageSourceSet(root, [], [], null, [], failed.Message);
        }
        string user = NuGetEnvironment.GetFolderPath(NuGetFolderPath.UserSettingsDirectory);
        string machine = NuGetEnvironment.GetFolderPath(NuGetFolderPath.MachineWideConfigDirectory);
        List<ConfigFile> files = [];
        foreach (string path in settings.GetConfigFilePaths())
        {
            string file = PathNames.Canonical(path);
            ConfigPlace place = PathNames.IsUnder(file, PathNames.Canonical(machine)) ? ConfigPlace.Machine
                : PathNames.IsUnder(file, PathNames.Canonical(user)) ? ConfigPlace.User
                : PathNames.Same(Path.GetDirectoryName(file)!, PathNames.Canonical(root)) ? ConfigPlace.Solution
                : ConfigPlace.Folder;
            files.Add(Read(file, place));
        }
        List<FeedSource> feeds = [];
        List<FeedSource> audits = [];
        try
        {
            PackageSourceProvider provider = new(settings);
            foreach (PackageSource source in provider.LoadPackageSources())
            {
                feeds.Add(new FeedSource(source.Name, source.Source, source.IsEnabled,
                    files.FirstOrDefault(f => f.Feeds.Any(feed => feed.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase)))?.Path,
                    source.ProtocolVersion, source.AllowInsecureConnections, source.DisableTLSCertificateValidation, Credentials(source)));
            }
            foreach (PackageSource source in provider.LoadAuditSources())
            {
                audits.Add(new FeedSource(source.Name, source.Source, true, null, source.ProtocolVersion, source.AllowInsecureConnections,
                    source.DisableTLSCertificateValidation, Credentials(source)));
            }
        }
        catch (Exception failed) when (failed is NuGetConfigurationException or InvalidOperationException or ArgumentException)
        {
            return new PackageSourceSet(root, files, feeds, null, [], failed.Message);
        }
        PackageSourceMapping? mapping;
        List<(string Feed, IReadOnlyList<string> Patterns)> patterns = [];
        try
        {
            mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
            foreach (PackageSourceMappingSourceItem item in settings.GetSection("packageSourceMapping")?.Items.OfType<PackageSourceMappingSourceItem>() ?? [])
            {
                patterns.Add((item.Key, [.. item.Patterns.Select(p => p.Pattern)]));
            }
        }
        catch (Exception failed) when (failed is NuGetConfigurationException or InvalidOperationException or ArgumentException)
        {
            return new PackageSourceSet(root, files, feeds, null, [], failed.Message);
        }
        return new PackageSourceSet(root, files, feeds, mapping, patterns, null, audits);
    }

    /// <summary>A feed's credentials as NuGet would send them; a password NuGet keeps encrypted, which only Windows can
    /// read, is none elsewhere.</summary>
    private static FeedCredentials? Credentials(PackageSource source)
    {
        if (source.Credentials is not { } given || !given.IsValid())
        {
            return null;
        }
        try
        {
            return new FeedCredentials(given.Username, given.Password);
        }
        catch (Exception unreadable) when (unreadable is NotSupportedException or PlatformNotSupportedException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>One file's own feeds and settings, as written in it.</summary>
    internal static ConfigFile Read(string path, ConfigPlace place)
    {
        XElement root;
        try
        {
            root = XDocument.Load(path).Root ?? throw new XmlException("The file has no configuration element");
        }
        catch (Exception failed) when (failed is XmlException or IOException or UnauthorizedAccessException)
        {
            return new ConfigFile(path, place, [], [], false, null, null, failed.Message);
        }
        HashSet<string> disabled = new(Adds(root.Element("disabledPackageSources"))
            .Where(a => string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase)).Select(a => a.Key), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (string? User, bool Password)> credentials = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement held in root.Element("packageSourceCredentials")?.Elements() ?? [])
        {
            List<(string Key, string Value)> values = [.. Adds(held)];
            credentials[XmlConvert.DecodeName(held.Name.LocalName)] = (values.FirstOrDefault(v => v.Key.Equals("Username", StringComparison.OrdinalIgnoreCase)).Value,
                values.Any(v => v.Key.Equals("Password", StringComparison.OrdinalIgnoreCase) || v.Key.Equals("ClearTextPassword", StringComparison.OrdinalIgnoreCase)));
        }
        XElement? sources = root.Element("packageSources");
        List<ConfigFeed> feeds = [];
        foreach (XElement add in sources?.Elements("add") ?? [])
        {
            if ((string?)add.Attribute("key") is not { } key)
            {
                continue;
            }
            credentials.TryGetValue(key, out (string? User, bool Password) held);
            feeds.Add(new ConfigFeed(key, (string?)add.Attribute("value") ?? string.Empty, disabled.Contains(key), held.User, held.Password,
                string.Equals((string?)add.Attribute("allowInsecureConnections"), "true", StringComparison.OrdinalIgnoreCase)));
        }
        List<(string Key, string Value)> config = [.. Adds(root.Element("config"))];
        string? Setting(string name) => config.LastOrDefault(c => c.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        return new ConfigFile(path, place, feeds, [.. disabled], sources?.Elements("clear").Any() == true, Setting("globalPackagesFolder"), Setting("repositoryPath"), null);
    }

    private static IEnumerable<(string Key, string Value)> Adds(XElement? section) =>
        (section?.Elements("add") ?? []).Select(a => ((string?)a.Attribute("key") ?? string.Empty, (string?)a.Attribute("value") ?? string.Empty));
}
