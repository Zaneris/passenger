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
/// the package manager its scripts run with and what said so, its scripts, the Node.js its <c>engines</c> asks for, the
/// package.json of the workspace it is a package of (null for one of none, a workspace's own root among them), and whether
/// its manager runs a script's <c>pre</c> and <c>post</c> hooks with it (<see cref="NodeProjects.RunsHooks"/>).</summary>
public sealed record NodePackage(string Manifest, string Name, NodeManager Manager, string ManagerSource, IReadOnlyList<NodeScript> Scripts,
    string? Engines, string? Root, bool RunsHooks = true)
{
    /// <summary>The package's folder, where its scripts run.</summary>
    public string Folder => Path.GetDirectoryName(Manifest)!;

    /// <summary>The scripts it runs on their own: all of them but the hooks around another of them (<c>prebuild</c> before
    /// <c>build</c>) where its package manager runs those with that one, rather than alone.</summary>
    public IEnumerable<NodeScript> Runnable => RunsHooks ? Scripts.Where(s => !NodeProjects.IsHook(s.Name, Scripts)) : Scripts;

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

    /// <summary>npm's and pnpm's settings file of a project or a workspace.</summary>
    public const string NpmrcName = ".npmrc";

    /// <summary>The settings file of Yarn 2 and later, which Yarn 1 does not read.</summary>
    public const string YarnrcName = ".yarnrc.yml";

    /// <summary>How much of a lockfile is read for the format its header names: a lockfile may run to megabytes.</summary>
    private const int LockfileHead = 4096;

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

    /// <summary>Whether a file's change may change a folder's packages: a package.json, a lockfile, pnpm's workspace file or
    /// a package manager's settings.</summary>
    public static bool Concerns(string path)
    {
        string name = Path.GetFileName(path);
        return name is ManifestName or PnpmWorkspaceName or NpmrcName or YarnrcName || Lockfiles.Any(l => l.Name == name);
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

    /// <summary>Whether a package manager runs a script's <c>pre</c> and <c>post</c> hooks as it runs the script, by its major
    /// version (null where nothing says it) and the setting that turns them on or off (pnpm's <c>enablePrePostScripts</c>,
    /// npm's <c>ignore-scripts</c> turned around; null where none is set): Yarn 1 runs them and Yarn 2 and later never do;
    /// pnpm runs them as its setting says, else as its version's default, off from 6 to 8; npm runs them unless its setting
    /// says not to, Bun always.</summary>
    public static bool RunsHooks(NodeManager manager, int? version, bool? setting) => manager switch
    {
        NodeManager.Yarn => version is 1,
        NodeManager.Pnpm => setting ?? version is not (>= 6 and < 9),
        NodeManager.Npm => setting ?? true,
        _ => true,
    };

    /// <summary>The major version a <c>packageManager</c> field names ("pnpm@9.12.0+sha512…" is 9), null where it names none.</summary>
    public static int? MajorOf(string? packageManager) =>
        (packageManager ?? string.Empty).Split('@', 2) is [_, { } version]
        && int.TryParse(version.Split('.', '+', '-')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int major)
            ? major : null;

    /// <summary>The major version of the package manager that writes a lockfile, as far as its first lines say: Yarn 1's
    /// or a later Yarn's yarn.lock, and the pnpm that writes a pnpm-lock.yaml's <c>lockfileVersion</c> (5 before 5.3, which
    /// pnpm 6 began, 8 before 9.0, else 9); null where they say nothing of it.</summary>
    public static int? LockfileMajor(NodeManager manager, string head)
    {
        if (manager == NodeManager.Yarn)
        {
            return head.Contains("# yarn lockfile v1", StringComparison.Ordinal) ? 1
                : head.Split('\n').Any(line => line.TrimEnd('\r') == "__metadata:") ? 2 : null;
        }
        string? said = manager == NodeManager.Pnpm ? YamlValue(head, "lockfileVersion") : null;
        return decimal.TryParse(said, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out decimal version)
            ? version < 5.3m ? 5 : version < 9 ? 8 : 9 : null;
    }

    /// <summary>A switch an .npmrc sets (<c>key=true</c>), null where it sets none, or sets it to no boolean.</summary>
    public static bool? NpmrcSwitch(string npmrc, string key)
    {
        bool? set = null;
        foreach (string raw in npmrc.Split('\n'))
        {
            string line = raw.Trim();
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length > 0 && line[0] is not (';' or '#') && equals > 0 && line[..equals].Trim() == key)
            {
                set = Switch(line[(equals + 1)..]);
            }
        }
        return set;
    }

    /// <summary>A switch a YAML file sets at its top level (<c>key: false</c>), under any of its names, null where it sets
    /// none, or sets it to no boolean.</summary>
    public static bool? YamlSwitch(string yaml, params string[] keys) =>
        keys.Select(key => YamlValue(yaml, key)).FirstOrDefault(value => value is not null) is { } value ? Switch(value) : null;

    /// <summary>A top-level key's value in a YAML file, unquoted and without its comment; null where it has none.</summary>
    private static string? YamlValue(string yaml, string key)
    {
        foreach (string raw in yaml.Split('\n'))
        {
            string line = Uncommented(raw).TrimEnd();
            if (line.StartsWith(key, StringComparison.Ordinal) && line[key.Length..].TrimStart() is [':', .. string value])
            {
                return Unquoted(value);
            }
        }
        return null;
    }

    private static bool? Switch(string value) => value.Trim().Trim('\'', '"').ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

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
            if (ItemPaths.Matches(pattern.Split('/', StringSplitOptions.RemoveEmptyEntries), names, _ => false))
            {
                taken = !leaves;
            }
        }
        return taken;
    }

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
            string? root = roots.Where(r => PathNames.IsUnder(folder, r.Folder) && PathNames.Relative(folder, r.Folder) is { } below && Covers(r.Globs, below))
                .OrderByDescending(r => r.Folder.Length).Select(r => r.Manifest).FirstOrDefault();
            (NodeManager manager, string source, string? saidAt) = ManagerFor(top, folder, read, saidIn);
            packages.Add(new NodePackage(manifest, made.Name ?? Path.GetFileName(folder), manager, source, made.Scripts, made.Engines, root,
                HooksRun(top, folder, manager, saidAt, read)));
        }
        return packages;
    }

    /// <summary>The package manager of the package in a folder: the nearest folder up from it whose package.json names one
    /// in its <c>packageManager</c> field, or that holds a lockfile or pnpm's workspace file, says it; npm where none does.
    /// Above the folder open only a package's own folder is looked in, so a lockfile of the person's home says nothing.</summary>
    private static (NodeManager Manager, string Source, string? SaidAt) ManagerFor(string top, string folder, Dictionary<string, NodeManifest> read,
        Dictionary<string, (NodeManager, string)?> saidIn)
    {
        foreach (string at in FoldersUp(top, folder))
        {
            if (!saidIn.TryGetValue(at, out (NodeManager, string)? said))
            {
                said = SaidIn(at, read);
                saidIn[at] = said;
            }
            if (said is ({ } manager, { } source))
            {
                return (manager, source, at);
            }
        }
        return (NodeManager.Npm, "no lockfile names another", null);
    }

    /// <summary>A package's folder and those above it a package manager looks in for its settings: within the folder open,
    /// and above it only while a folder holds a package.json.</summary>
    private static IEnumerable<string> FoldersUp(string top, string folder)
    {
        for (string? at = folder; at is not null && (PathNames.Within(at, top) || File.Exists(Path.Combine(at, ManifestName))); at = Path.GetDirectoryName(at))
        {
            yield return at;
        }
    }

    /// <summary>Whether the manager of the package in <paramref name="folder"/> runs its scripts' hooks with them
    /// (<see cref="RunsHooks"/>): its version from the <c>packageManager</c> field or lockfile in the folder that named it,
    /// <paramref name="saidAt"/> (a Yarn there with a .yarnrc.yml being Yarn 2 or later); pnpm's setting from the nearest
    /// pnpm-workspace.yaml up from the package, else the nearest .npmrc setting it (which pnpm 11 and later no longer
    /// read), and npm's <c>ignore-scripts</c> from the nearest .npmrc setting it.</summary>
    private static bool HooksRun(string top, string folder, NodeManager manager, string? saidAt, Dictionary<string, NodeManifest> read)
    {
        if (manager is not (NodeManager.Pnpm or NodeManager.Yarn or NodeManager.Npm))
        {
            return RunsHooks(manager, null, null);
        }
        int? version = saidAt is null || manager == NodeManager.Npm ? null : VersionIn(saidAt, manager, read);
        if (manager == NodeManager.Yarn)
        {
            return RunsHooks(manager, version, null);
        }
        bool? setting = null;
        if (manager == NodeManager.Pnpm)
        {
            string? workspace = FoldersUp(top, folder).Select(at => Path.Combine(at, PnpmWorkspaceName)).FirstOrDefault(File.Exists);
            setting = workspace is not null && ReadFile(workspace) is { } yaml ? YamlSwitch(yaml, "enablePrePostScripts", "enable-pre-post-scripts") : null;
        }
        if (setting is null && (manager == NodeManager.Npm || version is not >= 11))
        {
            string key = manager == NodeManager.Npm ? "ignore-scripts" : "enable-pre-post-scripts";
            setting = FoldersUp(top, folder).Select(at => ReadFile(Path.Combine(at, NpmrcName)) is { } npmrc ? NpmrcSwitch(npmrc, key) : null)
                .FirstOrDefault(set => set is not null);
            setting = manager == NodeManager.Npm ? !setting : setting;
        }
        return RunsHooks(manager, version, setting);
    }

    /// <summary>The major version of the package manager a folder names: by its package.json's <c>packageManager</c> field,
    /// else by its lockfile's first lines, a Yarn's settings file making it Yarn 2 or later.</summary>
    private static int? VersionIn(string folder, NodeManager manager, Dictionary<string, NodeManifest> read)
    {
        string manifest = Path.Combine(folder, ManifestName);
        NodeManifest? made = read.TryGetValue(manifest, out NodeManifest? known) ? known : ReadFile(manifest) is { } text ? ReadManifest(text) : null;
        if (Named(made?.PackageManager) == manager)
        {
            return MajorOf(made!.PackageManager);
        }
        if (manager == NodeManager.Yarn && File.Exists(Path.Combine(folder, YarnrcName)))
        {
            return 2;
        }
        string? lockfile = Lockfiles.Where(l => l.Manager == manager).Select(l => Path.Combine(folder, l.Name)).FirstOrDefault(File.Exists);
        return lockfile is not null && ReadHead(lockfile) is { } head ? LockfileMajor(manager, head) : null;
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

    /// <summary>A file's first few kilobytes as text, null where it can't be read.</summary>
    private static string? ReadHead(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] head = new byte[LockfileHead];
            int length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return System.Text.Encoding.UTF8.GetString(head, 0, length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
