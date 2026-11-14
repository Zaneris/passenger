using System.IO.Enumeration;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>The program a Node package's scripts are run with and its dependencies installed by.</summary>
public enum NodeManager
{
    Npm,
    Pnpm,
    Yarn,
    Bun,
}

/// <summary>One of a package.json's scripts: its name and the command it runs.</summary>
public sealed record NodeScript(string Name, string Command);

/// <summary>What a package.json says of itself: its name, its scripts in their order, its <c>packageManager</c> field, the
/// Node.js its <c>engines</c> asks for, and the globs of its workspaces' packages (an array, or Yarn's object of them).</summary>
public sealed record NodeManifest(string? Name, IReadOnlyList<NodeScript> Scripts, string? PackageManager, string? Engines, IReadOnlyList<string> Workspaces);

/// <summary>A Node package of the open folder: its package.json (canonical), its name (the manifest's, else its folder's),
/// the package manager its scripts run with and what said so, its scripts, the Node.js its <c>engines</c> asks for, and the
/// package.json of the workspace it is a package of (null for one of none, a workspace's own root among them).</summary>
public sealed record NodePackage(string Manifest, string Name, NodeManager Manager, string ManagerSource, IReadOnlyList<NodeScript> Scripts,
    string? Engines, string? Root)
{
    /// <summary>The package's folder, where its scripts run.</summary>
    public string Folder => Path.GetDirectoryName(Manifest)!;

    /// <summary>Its scripts but the hooks its package manager runs around another of them (<c>prebuild</c> before
    /// <c>build</c>), which run with that one rather than alone.</summary>
    public IEnumerable<NodeScript> Runnable => Scripts.Where(s => !NodeProjects.IsHook(s.Name, Scripts));

    /// <summary>Whether it is the root of a workspace holding other packages.</summary>
    public bool IsWorkspace(IEnumerable<NodePackage> all) => all.Any(p => p.Root is { } root && PathNames.Same(root, Manifest));
}

/// <summary>The Node packages of a folder, each package.json read with the package manager its lockfile or
/// <c>packageManager</c> field names (the nearest folder up from the package that says either, the field first; npm where
/// none does), and the workspaces each root's <c>workspaces</c> or pnpm-workspace.yaml lists. Reads files: a worker's job.</summary>
public static class NodeProjects
{
    public const string ManifestName = "package.json";

    public const string PnpmWorkspaceName = "pnpm-workspace.yaml";

    /// <summary>Each lockfile with the package manager writing it, in the order one is taken where a folder has several.</summary>
    private static readonly (string Name, NodeManager Manager)[] Lockfiles =
    [
        ("pnpm-lock.yaml", NodeManager.Pnpm),
        ("yarn.lock", NodeManager.Yarn),
        ("bun.lock", NodeManager.Bun),
        ("bun.lockb", NodeManager.Bun),
        ("package-lock.json", NodeManager.Npm),
        ("npm-shrinkwrap.json", NodeManager.Npm),
    ];

    /// <summary>Whether a file's change may change a folder's packages: a package.json, a lockfile or pnpm's workspace file.</summary>
    public static bool Concerns(string path)
    {
        string name = Path.GetFileName(path);
        return name == ManifestName || name == PnpmWorkspaceName || Lockfiles.Any(l => l.Name == name);
    }

    /// <summary>The program a package manager is run as.</summary>
    public static string ProgramOf(NodeManager manager) => manager switch
    {
        NodeManager.Pnpm => "pnpm",
        NodeManager.Yarn => "yarn",
        NodeManager.Bun => "bun",
        _ => "npm",
    };

    /// <summary>A package manager's words for running a script with these arguments after it: npm's take a <c>--</c> before
    /// them, which the others pass on to the script themselves.</summary>
    public static IReadOnlyList<string> RunArguments(NodeManager manager, string script, IReadOnlyList<string> arguments) =>
        manager == NodeManager.Npm && arguments.Count > 0 ? ["run", script, "--", .. arguments] : ["run", script, .. arguments];

    /// <summary>Whether a script is a hook its package manager runs around another of these (<c>pre</c> or <c>post</c> and
    /// that one's name).</summary>
    public static bool IsHook(string name, IReadOnlyList<NodeScript> scripts) =>
        (name.StartsWith("pre", StringComparison.Ordinal) || name.StartsWith("post", StringComparison.Ordinal))
        && scripts.Any(s => s.Name.Length > 0 && (name == "pre" + s.Name || name == "post" + s.Name));

    /// <summary>A package.json's text as it reads, null where it is no JSON object.</summary>
    public static NodeManifest? ReadManifest(string json)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(json, LenientJson.Document);
            JsonElement root = read.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            List<NodeScript> scripts = [.. JsonMembers.Properties(root, "scripts")
                .Where(s => s.Value.ValueKind == JsonValueKind.String)
                .Select(s => new NodeScript(s.Name, s.Value.GetString()!))];
            JsonElement? workspaces = JsonMembers.Value(root, "workspaces");
            IReadOnlyList<string> globs = workspaces?.ValueKind == JsonValueKind.Array ? JsonMembers.Texts(root, "workspaces")
                : JsonMembers.Texts(workspaces, "packages");
            return new NodeManifest(JsonMembers.Text(root, "name") is { Length: > 0 } name ? name : null, scripts, JsonMembers.Text(root, "packageManager"),
                JsonMembers.Text(JsonMembers.Object(root, "engines"), "node"), globs);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The package manager a <c>packageManager</c> field names ("pnpm@9.12.0+sha512…"), null for none it names.</summary>
    public static NodeManager? Named(string? packageManager)
    {
        string name = (packageManager ?? string.Empty).Split('@', 2)[0].Trim();
        return name switch
        {
            "npm" => NodeManager.Npm,
            "pnpm" => NodeManager.Pnpm,
            "yarn" => NodeManager.Yarn,
            "bun" => NodeManager.Bun,
            _ => null,
        };
    }

    /// <summary>The package manager whose lockfile is among a folder's file names, and that lockfile's name; null for none.</summary>
    public static (NodeManager Manager, string Lockfile)? OfLockfiles(IEnumerable<string> names)
    {
        HashSet<string> held = new(names, StringComparer.Ordinal);
        return Lockfiles.FirstOrDefault(l => held.Contains(l.Name)) is { Name: not null } found ? (found.Manager, found.Name) : null;
    }

    /// <summary>The globs of pnpm-workspace.yaml's <c>packages</c>, as a block of items or a flow of them, each unquoted.</summary>
    public static IReadOnlyList<string> PnpmWorkspaces(string yaml)
    {
        List<string> globs = [];
        bool listing = false;
        foreach (string raw in yaml.Split('\n'))
        {
            string line = Uncommented(raw).TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }
            if (!char.IsWhiteSpace(line[0]) && !line.StartsWith('-'))
            {
                listing = line.StartsWith("packages:", StringComparison.Ordinal);
                string flow = listing ? line["packages:".Length..].Trim() : string.Empty;
                if (flow.StartsWith('[') && flow.EndsWith(']'))
                {
                    globs.AddRange(flow[1..^1].Split(',').Select(Unquoted).Where(g => g.Length > 0));
                    listing = false;
                }
                continue;
            }
            if (listing && line.TrimStart() is ['-', .. string item])
            {
                globs.Add(Unquoted(item));
            }
        }
        return globs;
    }

    private static string Uncommented(string line)
    {
        int hash = line.IndexOf(" #", StringComparison.Ordinal);
        return line.StartsWith('#') ? string.Empty : hash >= 0 ? line[..hash] : line;
    }

    private static string Unquoted(string text) => text.Trim().Trim('\'', '"').Trim();

    /// <summary>Whether a workspace's globs take in a package by its folder's path from the workspace's ('/' between
    /// folders): one glob takes it, and no glob after it beginning with <c>!</c> leaves it out.</summary>
    public static bool Covers(IReadOnlyList<string> globs, string relative)
    {
        bool taken = false;
        string[] names = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string glob in globs)
        {
            bool leaves = glob.StartsWith('!');
            string pattern = (leaves ? glob[1..] : glob).Trim().TrimStart('.', '/').TrimEnd('/');
            if (Matches(pattern.Split('/', StringSplitOptions.RemoveEmptyEntries), 0, names, 0))
            {
                taken = !leaves;
            }
        }
        return taken;
    }

    private static bool Matches(string[] parts, int part, string[] names, int name) =>
        part == parts.Length ? name == names.Length
        : parts[part] == "**" ? Matches(parts, part + 1, names, name) || (name < names.Length && Matches(parts, part, names, name + 1))
        : name < names.Length && FileSystemName.MatchesSimpleExpression(parts[part], names[name], ignoreCase: false) && Matches(parts, part + 1, names, name + 1);

    /// <summary>The packages of these package.json files (canonical), those that read, under the folder open (<paramref name="top"/>):
    /// each with its workspace and package manager as the files beside it and above it say.</summary>
    public static IReadOnlyList<NodePackage> Read(string top, IEnumerable<string> manifests)
    {
        Dictionary<string, NodeManifest> read = new(PathNames.Comparer);
        foreach (string manifest in manifests)
        {
            if (ReadFile(manifest) is { } text && ReadManifest(text) is { } made)
            {
                read[manifest] = made;
            }
        }
        // Each root and the globs of its workspace's packages: its own field, else pnpm's file beside it.
        List<(string Folder, string Manifest, IReadOnlyList<string> Globs)> roots = [];
        foreach ((string manifest, NodeManifest made) in read)
        {
            string folder = Path.GetDirectoryName(manifest)!;
            IReadOnlyList<string> globs = made.Workspaces.Count > 0 ? made.Workspaces
                : ReadFile(Path.Combine(folder, PnpmWorkspaceName)) is { } yaml ? PnpmWorkspaces(yaml) : [];
            if (globs.Count > 0)
            {
                roots.Add((folder, manifest, globs));
            }
        }
        Dictionary<string, (NodeManager, string)?> saidIn = new(PathNames.Comparer);
        List<NodePackage> packages = [];
        foreach ((string manifest, NodeManifest made) in read.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            string folder = Path.GetDirectoryName(manifest)!;
            string? root = roots.Where(r => PathNames.IsUnder(folder, r.Folder) && Covers(r.Globs, Path.GetRelativePath(r.Folder, folder).Replace('\\', '/')))
                .OrderByDescending(r => r.Folder.Length).Select(r => r.Manifest).FirstOrDefault();
            (NodeManager manager, string source) = ManagerFor(top, folder, read, saidIn);
            packages.Add(new NodePackage(manifest, made.Name ?? Path.GetFileName(folder), manager, source, made.Scripts, made.Engines, root));
        }
        return packages;
    }

    /// <summary>The package manager of the package in a folder: the nearest folder up from it whose package.json names one
    /// in its <c>packageManager</c> field, or that holds a lockfile or pnpm's workspace file, says it; npm where none does.
    /// Above the folder open only a package's own folder is looked in, so a lockfile of the person's home says nothing.</summary>
    private static (NodeManager Manager, string Source) ManagerFor(string top, string folder, Dictionary<string, NodeManifest> read,
        Dictionary<string, (NodeManager, string)?> saidIn)
    {
        for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
        {
            if (!PathNames.Within(at, top) && !File.Exists(Path.Combine(at, ManifestName)))
            {
                break;
            }
            if (!saidIn.TryGetValue(at, out (NodeManager, string)? said))
            {
                said = SaidIn(at, read);
                saidIn[at] = said;
            }
            if (said is { } found)
            {
                return found;
            }
        }
        return (NodeManager.Npm, "no lockfile names another");
    }

    private static (NodeManager, string)? SaidIn(string folder, Dictionary<string, NodeManifest> read)
    {
        string manifest = Path.Combine(folder, ManifestName);
        NodeManifest? made = read.TryGetValue(manifest, out NodeManifest? known) ? known : ReadFile(manifest) is { } text ? ReadManifest(text) : null;
        if (Named(made?.PackageManager) is { } named)
        {
            return (named, $"its packageManager field ({made!.PackageManager!.Split('+')[0]})");
        }
        if (OfLockfiles(Lockfiles.Select(l => l.Name).Where(name => File.Exists(Path.Combine(folder, name)))) is ({ } locked, { } lockfile))
        {
            return (locked, lockfile);
        }
        return File.Exists(Path.Combine(folder, PnpmWorkspaceName)) ? (NodeManager.Pnpm, PnpmWorkspaceName) : null;
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
