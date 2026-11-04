using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Testing;

/// <summary>GUT's tests as a Godot project's GDScript declares them, found before any run: a script that extends GutTest
/// (GUT's test.gd by its path, or a script of the project's that does, by its class_name or path) is a test class, its
/// <c>func test_…</c> its tests, and each inner class of it that extends one and is named Test… a class of its own, nested
/// in it. A test class is named by the script's path in its project, folders as a namespace ("test.unit.test_math", an
/// inner class after a '+'), so a test is the same test wherever it is found, and a project carries GUT where its
/// addons/gut holds GUT's command line. Where its .gutconfig.json names GUT's folders or scripts, those alone hold tests
/// (<see cref="Config"/>), every script still read for the test classes it derives.</summary>
public static partial class GutScripts
{
    /// <summary>The test framework a GUT test is told as having been found by, which <see cref="GutRunner"/> runs.</summary>
    public const string Framework = "GUT";

    /// <summary>Whether a Godot project carries GUT.</summary>
    public static bool Carried(string folder) => File.Exists(Path.Combine(folder, "addons", "gut", "gut_cmdln.gd"));

    /// <summary>The file GUT's command line reads its options from, in the project's folder.</summary>
    public const string ConfigFile = ".gutconfig.json";

    /// <summary>GUT's choice of the scripts it runs, as its .gutconfig.json gives it: those under its folders (<c>dirs</c>,
    /// their subfolders' only with <c>include_subdirs</c>) whose names begin with its <c>prefix</c> and end with its
    /// <c>suffix</c>, and the scripts its <c>tests</c> name, by res:// path.</summary>
    public sealed record Config(IReadOnlyList<string> Dirs, bool Subdirs, string Prefix, string Suffix, IReadOnlyList<string> Tests)
    {
        /// <summary>A config's choice read from its text; null where it names no folder nor script (GUT's panel then
        /// chooses, from settings of the person's own), or does not read as JSON.</summary>
        public static Config? Read(string text)
        {
            try
            {
                using JsonDocument read = JsonDocument.Parse(text, LenientJson.Document);
                JsonElement root = read.RootElement;
                IReadOnlyList<string> dirs = [.. JsonMembers.Texts(root, "dirs").Where(d => d.Length > 0).Select(d => d.EndsWith('/') ? d : d + "/")];
                IReadOnlyList<string> tests = JsonMembers.Texts(root, "tests");
                return dirs.Count == 0 && tests.Count == 0 ? null
                    : new Config(dirs, JsonMembers.Bool(root, "include_subdirs") ?? false, JsonMembers.Text(root, "prefix") ?? "test_", JsonMembers.Text(root, "suffix") ?? ".gd", tests);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Whether GUT runs a script of the project in <paramref name="folder"/>.</summary>
        public bool Takes(string folder, string path)
        {
            string script = ResourceOf(folder, path);
            if (Tests.Contains(script, StringComparer.Ordinal))
            {
                return true;
            }
            string name = Path.GetFileName(path);
            return name.StartsWith(Prefix, StringComparison.Ordinal) && name.EndsWith(Suffix, StringComparison.Ordinal)
                && Dirs.Any(dir => script.StartsWith(dir, StringComparison.Ordinal) && (Subdirs || !script.AsSpan(dir.Length).Contains('/')));
        }
    }

    /// <summary>Each project's config as last read, by its folder, with the file's time and length it was read at.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime Written, long Length, Config? Config)> Configs = new(PathNames.Comparer);

    /// <summary>A project's GUT config (<see cref="Config.Read"/>), read again only once its file has changed; null for
    /// none. Any thread's.</summary>
    public static Config? ConfigOf(string folder)
    {
        FileInfo file = new(Path.Combine(folder, ConfigFile));
        try
        {
            file.Refresh();
            (DateTime written, long length) = file.Exists ? (file.LastWriteTimeUtc, file.Length) : (default, -1);
            if (Configs.TryGetValue(folder, out (DateTime Written, long Length, Config? Config) known) && known.Written == written && known.Length == length)
            {
                return known.Config;
            }
            Config? config = file.Exists ? Config.Read(File.ReadAllText(file.FullName)) : null;
            Configs[folder] = (written, length, config);
            return config;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether a file is a GDScript file a project's tests may be in: not GUT's own nor another addon's, nor
    /// Godot's build folder's.</summary>
    public static bool Holds(string folder, string path) =>
        Path.GetExtension(path).Equals(".gd", StringComparison.OrdinalIgnoreCase) && PathNames.IsUnder(path, folder)
        && Path.GetRelativePath(folder, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] is not ("addons" or ".godot");

    /// <summary>The project's GDScript files <see cref="Holds"/> takes, folder by folder: none of a hidden folder, nor of one
    /// Godot's .gdignore leaves out. A worker's: it walks the disk.</summary>
    public static IEnumerable<string> Files(string folder) =>
        PathNames.FilesUnder(folder, sub => !Path.GetFileName(sub).StartsWith('.') && Holds(folder, Path.Combine(sub, "x.gd"))
            && !File.Exists(Path.Combine(sub, ".gdignore")))
            .SelectMany(found => found.Files).Where(path => Holds(folder, path));

    /// <summary>The test class a project's script is ("test.unit.test_math" of test/unit/test_math.gd).</summary>
    public static string TypeOf(string folder, string path) =>
        Path.ChangeExtension(Path.GetRelativePath(folder, path), null).Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');

    /// <summary>A script's path as Godot names it in its project ("res://test/test_math.gd").</summary>
    public static string ResourceOf(string folder, string path) => "res://" + Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>The project's GDScript file a test class is in, and its inner class (null for the script's own).</summary>
    public static (string Path, string? Inner) ScriptOf(string folder, string type, string? path)
    {
        int plus = type.IndexOf('+', StringComparison.Ordinal);
        return (path ?? Path.Combine(folder, (plus < 0 ? type : type[..plus]).Replace('.', Path.DirectorySeparatorChar) + ".gd"), plus < 0 ? null : type[(plus + 1)..]);
    }

    /// <summary>What the project's script at <paramref name="path"/> declares for testing (<see cref="FileTests"/>, its
    /// <see cref="FileTests.Attributes"/> its class_name and res:// path where it is a test class, so a script extending it
    /// is one too; no tests where <paramref name="config"/> names other scripts) and where its classes and functions are
    /// (<see cref="FilePlaces"/>), read line by line, lines the editor's from zero; <paramref name="bases"/> the project's
    /// other test classes so known.</summary>
    public static (FileTests Tests, FilePlaces Places) Read(string text, string folder, string path, IReadOnlySet<string>? bases = null, Config? config = null)
    {
        string type = TypeOf(folder, path);
        string resource = ResourceOf(folder, path);
        bool IsGut(string named) => IsGutTest(named, resource, bases);
        string[] lines = text.Split('\n');
        List<(string, int)> types = [];
        List<DeclaredTest> tests = [];
        HashSet<string> methods = new(StringComparer.Ordinal);
        List<DeclaredPlace> typePlaces = [];
        List<DeclaredPlace> methodPlaces = [];
        int extendsLine = -1;
        bool scriptTests = false;
        string? className = null;
        // The inner class being read: its name, the line it is on, whether it extends GutTest and is GUT's.
        (string Name, int Line, bool Tests)? inner = null;
        bool innerHeld = false;
        for (int at = 0; at < lines.Length; at++)
        {
            string line = lines[at].TrimEnd('\r');
            if (line.Trim() is "" || line.TrimStart().StartsWith('#'))
            {
                continue;
            }
            bool top = !char.IsWhiteSpace(line[0]);
            if (top)
            {
                inner = null;
            }
            if (top && (Extends().Match(line) is { Success: true } extends ? extends : ClassName().Match(line)) is { Success: true } head)
            {
                className = head.Groups["name"] is { Success: true } given ? given.Value : className;
                if (head.Groups["base"] is { Success: true } extended)
                {
                    extendsLine = at;
                    scriptTests = IsGut(extended.Value);
                }
                continue;
            }
            if (top && InnerClass().Match(line) is { Success: true } declared)
            {
                string name = declared.Groups["name"].Value;
                bool gut = name.StartsWith("Test", StringComparison.Ordinal) && IsGut(declared.Groups["base"].Value);
                inner = (name, at, gut);
                innerHeld = false;
                typePlaces.Add(new DeclaredPlace($"{type}+{name}", null, at, at, End(lines, at)));
                continue;
            }
            if (!top && inner is { Tests: false } plain && Extends().Match(line.TrimStart()) is { Success: true } innerExtends && plain.Name.StartsWith("Test", StringComparison.Ordinal))
            {
                inner = plain with { Tests = IsGut(innerExtends.Groups["base"].Value) };
                continue;
            }
            if (Function().Match(line) is not { Success: true } function)
            {
                continue;
            }
            string method = function.Groups["name"].Value;
            bool inInner = !top && inner is not null;
            string owner = inInner ? $"{type}+{inner!.Value.Name}" : type;
            int last = End(lines, at);
            methods.Add(method);
            methodPlaces.Add(new DeclaredPlace(owner, method, at, at, last));
            bool test = method.StartsWith("test_", StringComparison.Ordinal) && (inInner ? inner!.Value.Tests : top && scriptTests);
            if (!test)
            {
                continue;
            }
            if (inInner && !innerHeld)
            {
                innerHeld = true;
                types.Add((owner, inner!.Value.Line));
            }
            else if (!inInner && !types.Any(t => t.Item1 == type))
            {
                types.Insert(0, (type, Math.Max(extendsLine, 0)));
            }
            tests.Add(new DeclaredTest(owner, method, at, [], false) { Code = Code(lines, at, last) });
        }
        if (config?.Takes(folder, path) == false)
        {
            types.Clear();
            tests.Clear();
        }
        if (types.Any(t => t.Item1 == type) || typePlaces.Count > 0)
        {
            typePlaces.Insert(0, new DeclaredPlace(type, null, Math.Max(extendsLine, 0), 0, lines.Length - 1));
        }
        HashSet<string> derived = new(StringComparer.Ordinal);
        if (scriptTests)
        {
            derived.Add(resource);
            if (className is not null)
            {
                derived.Add(className);
            }
        }
        return (new FileTests(types, tests, methods) { Attributes = derived }, new FilePlaces(typePlaces, methodPlaces));
    }

    /// <summary>Whether what a script of <paramref name="resource"/> extends is GUT's test class: GutTest by its name, GUT's
    /// test.gd by its path, or one of <paramref name="bases"/> by its class_name or path (a relative one from the script's
    /// folder).</summary>
    private static bool IsGutTest(string named, string resource, IReadOnlySet<string>? bases)
    {
        if (named == "GutTest")
        {
            return true;
        }
        if (named.Length < 2 || named[0] is not ('"' or '\'') || named[^1] != named[0])
        {
            return bases?.Contains(named) == true;
        }
        string script = named[1..^1];
        if (!script.StartsWith("res://", StringComparison.Ordinal))
        {
            List<string> parts = [.. resource["res://".Length..].Split('/')[..^1]];
            foreach (string part in script.Split('/'))
            {
                if (part == ".." && parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                else if (part is not ("." or "" or ".."))
                {
                    parts.Add(part);
                }
            }
            script = "res://" + string.Join('/', parts);
        }
        return script == "res://addons/gut/test.gd" || bases?.Contains(script) == true;
    }

    /// <summary>The last line (from zero) of a function or an inner class beginning at <paramref name="start"/>: the last
    /// before the next that is no deeper than it, blank lines and comments passed over.</summary>
    private static int End(string[] lines, int start)
    {
        int depth = Indent(lines[start]);
        int last = start;
        for (int at = start + 1; at < lines.Length; at++)
        {
            string line = lines[at].TrimEnd('\r');
            if (line.Trim() is "" || line.TrimStart().StartsWith('#'))
            {
                continue;
            }
            if (Indent(line) <= depth)
            {
                break;
            }
            last = at;
        }
        return last;
    }

    private static int Indent(string line)
    {
        int count = 0;
        while (count < line.Length && char.IsWhiteSpace(line[count]))
        {
            count++;
        }
        return count;
    }

    /// <summary>A function's text but its whitespace, hashed as FNV-1a does, so a result is known to be of its code now.</summary>
    private static ulong Code(string[] lines, int first, int last)
    {
        ulong hash = 14695981039346656037;
        for (int at = first; at <= last; at++)
        {
            foreach (char c in lines[at])
            {
                if (!char.IsWhiteSpace(c))
                {
                    hash = (hash ^ c) * 1099511628211;
                }
            }
        }
        return hash;
    }

    [GeneratedRegex(@"^extends\s+(?<base>[A-Za-z_]\w*|""[^""]*""|'[^']*')(?:\s+class_name\s+(?<name>[A-Za-z_]\w*))?")]
    private static partial Regex Extends();

    [GeneratedRegex(@"^class_name\s+(?<name>[A-Za-z_]\w*)(?:\s+extends\s+(?<base>[A-Za-z_]\w*|""[^""]*""|'[^']*'))?")]
    private static partial Regex ClassName();

    [GeneratedRegex(@"^class\s+(?<name>[A-Za-z_]\w*)(?:\s+extends\s+(?<base>[A-Za-z_]\w*|""[^""]*""|'[^']*'))?\s*:")]
    private static partial Regex InnerClass();

    [GeneratedRegex(@"^\s*(?:static\s+)?func\s+(?<name>[A-Za-z_]\w*)\s*\(")]
    private static partial Regex Function();
}
