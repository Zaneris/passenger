using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>Yarn's Plug'n'Play layout of a folder's packages, read as its specification lays it out: the nearest .pnp.cjs up
/// from a file (in the person's folders, <see cref="FoldersUp"/>), its data inlined in it or beside it in .pnp.data.json, and
/// a package a file asks for placed where the data says, through the asking package's own dependencies, then the top-level
/// package's and the fallback pool where the data falls back. A package may stand in a zip archive or a virtual folder,
/// which a runtime reads only with .pnp.cjs loaded (<see cref="NodeOptions"/>) and Passenger through <see cref="ReadFile"/>.
/// A manifest is read again only once its stamp moves. Any thread.</summary>
public static class YarnPnp
{
    public const string ManifestName = ".pnp.cjs";

    public const string DataName = ".pnp.data.json";

    public const string LoaderName = ".pnp.loader.mjs";

    /// <summary>What .pnp.cjs names its inlined data, a string literal of JSON after it.</summary>
    private const string InlinedState = "RAW_RUNTIME_STATE =";

    private static readonly ConcurrentDictionary<string, (FileStamp? Script, FileStamp? Data, Manifest? Read)> Read = new(PathNames.Comparer);

    /// <summary>The nearest .pnp.cjs from a folder up, in the person's folders; null where none governs it.</summary>
    public static string? ManifestFor(string folder)
    {
        foreach (string at in FoldersUp.From(folder))
        {
            if (FoldersUp.Found(at, ManifestName) is { } manifest && File.Exists(manifest))
            {
                return manifest;
            }
        }
        return null;
    }

    /// <summary>The folder of the package <paramref name="name"/> as a file of a Plug'n'Play project finds it (within an
    /// archive or a virtual folder, as the runtime names it); null where no manifest governs the file, the data does not
    /// read, the file belongs to no package of it, or it does not reach that package.</summary>
    public static string? Resolve(string file, string name)
    {
        if (Path.GetDirectoryName(file) is not { } folder || ManifestFor(folder) is not { } manifest || Of(manifest) is not { } read)
        {
            return null;
        }
        return read.Resolve(file, name);
    }

    /// <summary>NODE_OPTIONS that load a project's Plug'n'Play runtime into Node.js and each program it starts, as Yarn gives
    /// its own scripts: .pnp.cjs required, and its ES module loader where Yarn wrote one, before what the person's
    /// own NODE_OPTIONS (<paramref name="inherited"/>) says.</summary>
    public static string NodeOptions(string manifest, string? inherited)
    {
        string options = $"--require {Quoted(manifest)}";
        string loader = Path.Combine(Path.GetDirectoryName(manifest)!, LoaderName);
        if (File.Exists(loader))
        {
            options += $" --experimental-loader {new Uri(loader).AbsoluteUri}";
        }
        return string.IsNullOrWhiteSpace(inherited) ? options : $"{options} {inherited}";
    }

    /// <summary>A path as NODE_OPTIONS reads one: in double quotes, escaped as a JSON string, where it holds white space.</summary>
    private static string Quoted(string path) => path.Any(char.IsWhiteSpace) ? JsonSerializer.Serialize(path) : path;

    /// <summary>A file's text where it may stand in a package of a Plug'n'Play project: through a virtual folder to the real
    /// one, and from within a zip archive; null where it is not there or does not read.</summary>
    public static string? ReadFile(string path) => InArchive(path, entry =>
    {
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }, File.ReadAllText);

    /// <summary>Whether a file is there where it may stand in a package of a Plug'n'Play project, as <see cref="ReadFile"/>
    /// finds it.</summary>
    public static bool Exists(string path) => InArchive(path, _ => string.Empty, _ => string.Empty) is not null;

    /// <summary>What <paramref name="read"/> makes of a file within a zip archive, or <paramref name="onDisk"/> of one on
    /// disk, through a virtual folder to the real one; null where it is not there or does not read.</summary>
    private static string? InArchive(string path, Func<ZipArchiveEntry, string> read, Func<string, string> onDisk)
    {
        string real = Unvirtual(path);
        try
        {
            if (File.Exists(real))
            {
                return onDisk(real);
            }
            int archive = real.IndexOf(".zip" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            string zip = archive < 0 ? string.Empty : real[..(archive + ".zip".Length)];
            if (archive < 0 || !File.Exists(zip))
            {
                return null;
            }
            using ZipArchive opened = ZipFile.OpenRead(zip);
            return opened.GetEntry(real[(archive + ".zip".Length + 1)..].Replace(Path.DirectorySeparatorChar, '/')) is { } entry ? read(entry) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>A path through a virtual folder (<c>…/__virtual__/hash/n/rest</c>) as the real one it stands for: the
    /// folder before it climbed <c>n</c> times, then the rest.</summary>
    public static string Unvirtual(string path)
    {
        string[] parts = path.Split(Path.DirectorySeparatorChar);
        int at = Array.FindIndex(parts, p => p is "__virtual__" or "$$virtual");
        if (at < 0 || at + 2 >= parts.Length || !int.TryParse(parts[at + 2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int up))
        {
            return path;
        }
        IEnumerable<string> kept = parts.Take(Math.Max(0, at - up));
        return string.Join(Path.DirectorySeparatorChar, [.. kept, .. parts.Skip(at + 3)]);
    }

    /// <summary>The path a package's location in the data names from the manifest's folder: joined on portable paths
    /// (<see cref="Portable"/>), its "." and ".." taken and none above the root, so a location climbing from the folder's
    /// drive to another (a global cache's, a link's) names that drive.</summary>
    internal static string Located(string folder, string location, bool windows)
    {
        List<string> names = [];
        foreach (string name in $"{Portable(folder, windows)}/{location}".Split('/'))
        {
            if (name == "..")
            {
                if (names.Count > 0)
                {
                    names.RemoveAt(names.Count - 1);
                }
            }
            else if (name is not ("" or "."))
            {
                names.Add(name);
            }
        }
        return Native("/" + string.Join('/', names), windows);
    }

    /// <summary>A path as Yarn writes the data's paths (its portable path): '/' between names, and on Windows a drive a name
    /// below the root ("/C:/src/app") and a share's path below "/unc" ("/unc/server/share", "/unc/.dot/" for "\\.\").</summary>
    internal static string Portable(string path, bool windows)
    {
        if (!windows)
        {
            return path;
        }
        string slashed = path.Replace('\\', '/');
        return slashed.Length >= 2 && char.IsAsciiLetter(slashed[0]) && slashed[1] == ':' ? "/" + slashed
            : slashed.StartsWith("//./", StringComparison.Ordinal) ? "/unc/.dot/" + slashed[4..]
            : slashed.StartsWith("//", StringComparison.Ordinal) ? "/unc/" + slashed[2..]
            : slashed;
    }

    /// <summary>The path a portable one (<see cref="Portable"/>) names on this system.</summary>
    internal static string Native(string portable, bool windows)
    {
        if (!windows)
        {
            return portable;
        }
        string path = portable.Length >= 3 && portable[0] == '/' && char.IsAsciiLetter(portable[1]) && portable[2] == ':' ? portable[1..]
            : portable.StartsWith("/unc/.dot/", StringComparison.Ordinal) ? "//./" + portable["/unc/.dot/".Length..]
            : portable.StartsWith("/unc/", StringComparison.Ordinal) ? "//" + portable["/unc/".Length..]
            : portable;
        return path.Replace('/', '\\');
    }

    /// <summary>A manifest's data, read again once its file or its data file changed; null where it does not read.</summary>
    private static Manifest? Of(string manifest)
    {
        string data = Path.Combine(Path.GetDirectoryName(manifest)!, DataName);
        (FileStamp? script, FileStamp? split) = (FileStamp.Of(manifest), FileStamp.Of(data));
        if (Read.TryGetValue(manifest, out (FileStamp? Script, FileStamp? Data, Manifest? Read) kept) && kept.Script == script && kept.Data == split)
        {
            return kept.Read;
        }
        Manifest? made = null;
        try
        {
            string? json = split is not null ? File.ReadAllText(data) : InlinedData(File.ReadAllText(manifest));
            made = json is null ? null : Manifest.Parse(Path.GetDirectoryName(manifest)!, json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // A manifest being written may not read yet: it places nothing until it does.
        }
        Read[manifest] = (script, split, made);
        return made;
    }

    /// <summary>The JSON a .pnp.cjs holds inlined, as the single-quoted string literal after its runtime state's name, its
    /// escapes undone (a line continuation adds nothing); null where it holds none.</summary>
    internal static string? InlinedData(string script)
    {
        int named = script.IndexOf(InlinedState, StringComparison.Ordinal);
        int open = named < 0 ? -1 : script.IndexOf('\'', named + InlinedState.Length);
        if (open < 0)
        {
            return null;
        }
        StringBuilder json = new();
        for (int i = open + 1; i < script.Length; i++)
        {
            char c = script[i];
            if (c == '\'')
            {
                return json.ToString();
            }
            if (c != '\\' || i + 1 >= script.Length)
            {
                json.Append(c);
                continue;
            }
            char next = script[++i];
            if (next == '\r' && i + 1 < script.Length && script[i + 1] == '\n')
            {
                i++;
            }
            else if (next != '\n')
            {
                json.Append(next);
            }
        }
        return null;
    }

    /// <summary>One package of the data: where it stands from the manifest's folder (<c>./</c> or <c>../</c> first, '/'
    /// between folders and after the last), what it depends on by name (a null for a peer left unmet), and whether a path
    /// is never taken to be its.</summary>
    private sealed record Package(string Location, Dictionary<string, Locator?> Dependencies, bool Unfound);

    private readonly record struct Locator(string? Name, string? Reference);

    /// <summary>A manifest's data, read once.</summary>
    private sealed class Manifest
    {
        private readonly string folder;
        private readonly Dictionary<Locator, Package> packages = [];
        private readonly Dictionary<string, Locator?> fallbackPool = new(StringComparer.Ordinal);
        private readonly HashSet<Locator> excluded = [];
        private Regex? ignored;
        private bool fallsBack;

        private Manifest(string folder) => this.folder = folder;

        public static Manifest Parse(string folder, string json)
        {
            Manifest made = new(folder);
            using JsonDocument read = JsonDocument.Parse(json);
            JsonElement root = read.RootElement;
            made.fallsBack = JsonMembers.Bool(root, "enableTopLevelFallback") == true;
            if (JsonMembers.Text(root, "ignorePatternData") is { Length: > 0 } pattern)
            {
                try
                {
                    // Any thread may ask, the window's among them.
                    made.ignored = TextSearch.Written(pattern, RegexOptions.CultureInvariant, TextSearch.AtOnce);
                }
                catch (ArgumentException)
                {
                    // A pattern .NET does not read leaves every path to the data, as most are.
                }
            }
            foreach (JsonElement entry in JsonMembers.Items(root, "fallbackPool"))
            {
                if (entry is { ValueKind: JsonValueKind.Array } && entry.GetArrayLength() == 2 && entry[0].ValueKind == JsonValueKind.String)
                {
                    made.fallbackPool[entry[0].GetString()!] = Dependency(entry[0].GetString()!, entry[1]);
                }
            }
            foreach (JsonElement entry in JsonMembers.Items(root, "fallbackExclusionList"))
            {
                if (entry is { ValueKind: JsonValueKind.Array } && entry.GetArrayLength() == 2 && entry[1].ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement reference in entry[1].EnumerateArray())
                    {
                        made.excluded.Add(new Locator(Text(entry[0]), Text(reference)));
                    }
                }
            }
            foreach (JsonElement named in JsonMembers.Items(root, "packageRegistryData"))
            {
                if (named is not { ValueKind: JsonValueKind.Array } || named.GetArrayLength() != 2 || named[1].ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (JsonElement version in named[1].EnumerateArray())
                {
                    if (version is not { ValueKind: JsonValueKind.Array } || version.GetArrayLength() != 2 || JsonMembers.Text(version[1], "packageLocation") is not { } location)
                    {
                        continue;
                    }
                    Dictionary<string, Locator?> dependencies = new(StringComparer.Ordinal);
                    foreach (JsonElement dependency in JsonMembers.Items(version[1], "packageDependencies"))
                    {
                        if (dependency is { ValueKind: JsonValueKind.Array } && dependency.GetArrayLength() == 2 && dependency[0].ValueKind == JsonValueKind.String)
                        {
                            dependencies[dependency[0].GetString()!] = Dependency(dependency[0].GetString()!, dependency[1]);
                        }
                    }
                    made.packages[new Locator(Text(named[0]), Text(version[0]))] =
                        new Package(location, dependencies, JsonMembers.Bool(version[1], "discardFromLookup") == true);
                }
            }
            return made;
        }

        private static string? Text(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

        /// <summary>A dependency's locator: a reference under its own name, or an alias's name and reference; null for a peer
        /// left unmet.</summary>
        private static Locator? Dependency(string name, JsonElement reference) => reference.ValueKind switch
        {
            JsonValueKind.String => new Locator(name, reference.GetString()),
            JsonValueKind.Array when reference.GetArrayLength() == 2 => new Locator(Text(reference[0]), Text(reference[1])),
            _ => null,
        };

        public string? Resolve(string file, string name)
        {
            if (Owner(file) is not { } owner || !packages.TryGetValue(owner, out Package? asking))
            {
                return null;
            }
            if (!asking.Dependencies.TryGetValue(name, out Locator? found))
            {
                if (!fallsBack || excluded.Contains(owner))
                {
                    return null;
                }
                if (!(packages.TryGetValue(new Locator(null, null), out Package? top) && top.Dependencies.TryGetValue(name, out found))
                    && !fallbackPool.TryGetValue(name, out found))
                {
                    return null;
                }
            }
            return found is { } locator && packages.TryGetValue(locator, out Package? package)
                ? Path.TrimEndingDirectorySeparator(Located(folder, package.Location, OperatingSystem.IsWindows()))
                : null;
        }

        /// <summary>The package a file belongs to: the one whose location is the longest that holds it; null for a path the
        /// data leaves to Node.js's own resolution, or one under no package.</summary>
        private Locator? Owner(string file)
        {
            string relative = Route(file);
            try
            {
                if (ignored?.IsMatch(relative) == true)
                {
                    return null;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Taken as the data's own, as most paths are.
                PassengerLog.Write(TextSearch.RegexStoppedIn($"the ignore pattern of {folder}'s {ManifestName}, matching {relative}"));
            }
            string dotted = relative.StartsWith("../", StringComparison.Ordinal) ? relative : "./" + relative;
            Locator? best = null;
            int length = 0;
            foreach ((Locator locator, Package package) in packages)
            {
                if (!package.Unfound && package.Location.Length > length && dotted.StartsWith(package.Location, StringComparison.Ordinal))
                {
                    (best, length) = (locator, package.Location.Length);
                }
            }
            return best;
        }

        /// <summary>The way from the manifest's folder to a file as the data writes its locations, '/' between names; to a
        /// file on another volume, whose whole path .NET gives, up to the portable root and down that path
        /// (<see cref="Portable"/>).</summary>
        private string Route(string file)
        {
            string relative = Path.GetRelativePath(folder, file);
            if (!Path.IsPathRooted(relative))
            {
                return relative.Replace(Path.DirectorySeparatorChar, '/');
            }
            int names = Portable(folder, OperatingSystem.IsWindows()).Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
            return string.Concat(Enumerable.Repeat("../", names)) + Portable(relative, OperatingSystem.IsWindows()).TrimStart('/');
        }
    }
}
