using System.Text.RegularExpressions;

namespace Passenger.Workspace;

/// <summary>What a member of a GDScript's class is.</summary>
public enum GodotScriptKind
{
    Function,
    Signal,
    Variable,
    Constant,
}

/// <summary>A member a GDScript declares outside its inner classes: what it is, its name, and where its name starts in the
/// file's text.</summary>
public sealed record GodotScriptDeclared(GodotScriptKind Kind, string Name, int Start);

/// <summary>A GDScript file's class as its own lines declare it, the way Godot reads a script's top level: what it extends as
/// written (a quoted path, or a class by its name), its <c>class_name</c>, and its functions (<c>static</c> ones too),
/// signals, variables (not <c>static</c> ones) and constants (a named <c>enum</c> among them), each on a line that starts
/// with its word, annotations before it or not; an inner class's, indented, are its own. Pure.</summary>
public sealed partial class GodotScriptDeclarations
{
    private GodotScriptDeclarations(string? extends, bool extendsPath, string? className, IReadOnlyList<GodotScriptDeclared> members)
    {
        Extends = extends;
        ExtendsPath = extendsPath;
        ClassName = className;
        Members = members;
    }

    /// <summary>What its <c>extends</c> names: a path (<see cref="ExtendsPath"/>, unquoted), else a class by its name; null where
    /// it extends nothing written, so RefCounted.</summary>
    public string? Extends { get; }

    public bool ExtendsPath { get; }

    /// <summary>The name <c>class_name</c> gives its class across the project; null for none.</summary>
    public string? ClassName { get; }

    /// <summary>Its members in the order written.</summary>
    public IReadOnlyList<GodotScriptDeclared> Members { get; }

    /// <summary>Whether a file is a GDScript.</summary>
    public static bool IsScript(string path) => path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase);

    /// <summary>The member of a kind it declares by a name; null for none.</summary>
    public GodotScriptDeclared? Member(GodotScriptKind kind, string name) => Members.FirstOrDefault(m => m.Kind == kind && m.Name == name);

    public static GodotScriptDeclarations Read(string text)
    {
        List<GodotScriptDeclared> members = [];
        string? extends = null;
        bool path = false;
        string? named = null;
        for (int start = 0; start < text.Length;)
        {
            int end = text.IndexOf('\n', start);
            end = end < 0 ? text.Length : end;
            // Only a line that starts at its first column, outside the inner classes' indented bodies, is the class's own.
            if (end > start && text[start] is not (' ' or '\t' or '#' or '\r' or '\n'))
            {
                string line = text[start..end].TrimEnd('\r');
                if (Declaration().Match(line) is { Success: true } declared)
                {
                    Group name = declared.Groups["name"];
                    GodotScriptKind? kind = declared.Groups["word"].Value switch
                    {
                        "func" => GodotScriptKind.Function,
                        "signal" => GodotScriptKind.Signal,
                        "var" when !declared.Groups["static"].Success => GodotScriptKind.Variable,
                        "const" or "enum" => GodotScriptKind.Constant,
                        _ => null,
                    };
                    if (kind is { } of)
                    {
                        members.Add(new GodotScriptDeclared(of, name.Value, start + name.Index));
                    }
                }
                else if (ClassNamed().Match(line) is { Success: true } classed)
                {
                    named ??= classed.Groups["name"].Value;
                    if (classed.Groups["extends"].Success && extends is null)
                    {
                        (extends, path) = Extended(classed.Groups["extends"].Value);
                    }
                }
                else if (extends is null && Extended().Match(line) is { Success: true } extending)
                {
                    (extends, path) = Extended(extending.Groups["extends"].Value);
                }
            }
            start = end + 1;
        }
        return new GodotScriptDeclarations(extends, path, named, members);
    }

    /// <summary>What an <c>extends</c> names, unquoted, and whether it is a path.</summary>
    private static (string? Extends, bool Path) Extended(string written)
    {
        string extends = written.Trim();
        return extends.Length > 1 && extends[0] is '"' or '\'' && extends[^1] == extends[0] ? (extends[1..^1], true) : (extends.Length > 0 ? extends : null, false);
    }

    [GeneratedRegex(@"^(?:@[A-Za-z_]\w*(?:\([^)]*\))?\s+)*(?<static>static\s+)?(?<word>func|signal|var|const|enum)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"^(?:@[A-Za-z_]\w*(?:\([^)]*\))?\s+)*class_name\s+(?<name>[A-Za-z_]\w*)(?:\s+extends\s+(?<extends>""[^""]*""|'[^']*'|[A-Za-z_][\w.]*))?")]
    private static partial Regex ClassNamed();

    [GeneratedRegex(@"^(?:@[A-Za-z_]\w*(?:\([^)]*\))?\s+)*extends\s+(?<extends>""[^""]*""|'[^']*'|[A-Za-z_][\w.]*)")]
    private static partial Regex Extended();
}

/// <summary>A GDScript's class with the scripts it extends in its project, itself first (<see cref="GodotScenes.ChainOf"/>):
/// each script's res:// path and declarations, whether they are all its class has beside an engine class's (each script it
/// extends read, the last extending a class no script of the project names), and that engine class (RefCounted where
/// nothing is written). A node with no script has none, its engine class its own.</summary>
public sealed record GodotScriptChain(IReadOnlyList<(string Script, GodotScriptDeclarations Declared)> Scripts, bool Whole, string? Engine)
{
    /// <summary>The class of a node with no script: its engine class's alone, known where the scene says it.</summary>
    public static GodotScriptChain Of(string? engine) => new([], engine is not null, engine);

    /// <summary>The script and declaration of the member of a kind by a name its class has, the nearest first; null for none.</summary>
    public (string Script, GodotScriptDeclared Member)? Find(GodotScriptKind kind, string name)
    {
        foreach ((string script, GodotScriptDeclarations declared) in Scripts)
        {
            if (declared.Member(kind, name) is { } member)
            {
                return (script, member);
            }
        }
        return null;
    }

    /// <summary>Every name of a kind its scripts declare, each once, the nearest first.</summary>
    public IReadOnlyList<string> Names(GodotScriptKind kind) =>
        [.. Scripts.SelectMany(s => s.Declared.Members).Where(m => m.Kind == kind).Select(m => m.Name).Distinct(StringComparer.Ordinal)];

    /// <summary>Whether its class is a script's that is, or extends, another.</summary>
    public bool Runs(string script) => Scripts.Any(s => s.Script == script);
}
