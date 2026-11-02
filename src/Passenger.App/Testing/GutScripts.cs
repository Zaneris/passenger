using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Testing;

/// <summary>GUT's tests as a Godot project's GDScript declares them, found before any run: a script that extends GutTest
/// (or GUT's test.gd by its path) is a test class, its <c>func test_…</c> its tests, and each inner class of it that
/// extends GutTest and is named Test… a class of its own, nested in it. A test class is named by the script's path in its
/// project, folders as a namespace ("test.unit.test_math", an inner class after a '+'), so a test is the same test
/// wherever it is found, and a project carries GUT where its addons/gut holds GUT's command line.</summary>
public static partial class GutScripts
{
    /// <summary>The test framework a GUT test is told as having been found by, which <see cref="GutRunner"/> runs.</summary>
    public const string Framework = "GUT";

    /// <summary>Whether a Godot project carries GUT.</summary>
    public static bool Carried(string folder) => File.Exists(Path.Combine(folder, "addons", "gut", "gut_cmdln.gd"));

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

    /// <summary>What a script declares for testing (<see cref="FileTests"/>) and where its classes and functions are
    /// (<see cref="FilePlaces"/>), read line by line, lines the editor's from zero; <paramref name="type"/> names the script.</summary>
    public static (FileTests Tests, FilePlaces Places) Read(string text, string type)
    {
        string[] lines = text.Split('\n');
        List<(string, int)> types = [];
        List<DeclaredTest> tests = [];
        HashSet<string> methods = new(StringComparer.Ordinal);
        List<DeclaredPlace> typePlaces = [];
        List<DeclaredPlace> methodPlaces = [];
        int extendsLine = -1;
        bool scriptTests = false;
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
            if (top && Extends().Match(line) is { Success: true } extends)
            {
                extendsLine = at;
                scriptTests = IsGut(extends.Groups["base"].Value);
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
        if (types.Any(t => t.Item1 == type) || typePlaces.Count > 0)
        {
            typePlaces.Insert(0, new DeclaredPlace(type, null, Math.Max(extendsLine, 0), 0, lines.Length - 1));
        }
        return (new FileTests(types, tests, methods), new FilePlaces(typePlaces, methodPlaces));
    }

    /// <summary>Whether a script extends GUT's test class: by its class name, or GUT's test.gd by its path.</summary>
    private static bool IsGut(string named) =>
        named == "GutTest" || named.Trim('"', '\'') is "res://addons/gut/test.gd";

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

    [GeneratedRegex(@"^extends\s+(?<base>[A-Za-z_]\w*|""[^""]*""|'[^']*')")]
    private static partial Regex Extends();

    [GeneratedRegex(@"^class\s+(?<name>[A-Za-z_]\w*)(?:\s+extends\s+(?<base>[A-Za-z_]\w*|""[^""]*""|'[^']*'))?\s*:")]
    private static partial Regex InnerClass();

    [GeneratedRegex(@"^\s*(?:static\s+)?func\s+(?<name>[A-Za-z_]\w*)\s*\(")]
    private static partial Regex Function();
}
