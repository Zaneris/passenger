namespace Passenger.Workspace;

/// <summary>How a project writes its C#, as a script template for it writes it: the namespace its files take (none where
/// the project names none), file-scoped or in braces, braces on a line of their own or at the line's end, and its indent.</summary>
public sealed record GodotScriptStyle(string? Namespace, bool FileScoped, bool BracesOnOwnLine, bool Tabs, int IndentSize);

/// <summary>The C# script templates Passenger writes for Godot's Attach Script dialog, which reads a project's templates
/// from its script_templates folder, one folder a base class: a partial class (as Godot's generators need) of the class the
/// dialog names, deriving from the type it names, in the project's namespace and style. Godot fills the dialog's names in
/// (<c>_CLASS_</c>, <c>_BASE_</c>, <c>_BINDINGS_NAMESPACE_</c>) and makes each run of tabs its own indent, so a project
/// indented with spaces says none is to be made (<c>meta-space-indent: 0</c>). Pure.</summary>
public static class GodotScriptTemplates
{
    /// <summary>The project's folder Godot reads its own script templates from, by default.</summary>
    public const string Folder = "script_templates";

    /// <summary>Each template's file in its base class's folder.</summary>
    public const string FileName = "Passenger.cs";

    /// <summary>The name Godot's dialog lists a template by, which says it is Passenger's.</summary>
    public const string Name = "Passenger (this project's style)";

    /// <summary>The base classes a template is offered for, Node's applying to every node without one of its own.</summary>
    public static IReadOnlyList<string> Bases { get; } = ["Node", "Node2D", "Node3D", "Control", "Resource"];

    /// <summary>Where a base class's template goes in a Godot project's folder.</summary>
    public static string PathOf(string project, string baseClass) => Path.Combine(project, Folder, baseClass, FileName);

    /// <summary>The folder of a Godot project's templates, which the game's build leaves out, as they are no code of its own:
    /// as its C# project names it, from that project's folder (<paramref name="project"/>) to the Godot project's.</summary>
    public static string Excluded(string project, string godot) =>
        Path.GetRelativePath(project, godot) is var relative && relative == "." ? Folder : $"{relative.Replace(Path.DirectorySeparatorChar, '/')}/{Folder}";

    /// <summary>Whether a template's text is one Passenger wrote, which it keeps up to date.</summary>
    public static bool IsPassengers(string text) => text.Contains($"// meta-name: {Name}", StringComparison.Ordinal);

    /// <summary>The style a project's files are written in: its .editorconfig's words for a C# file of it (<paramref name="setting"/>
    /// by name), its root namespace, and whether its C# takes a file-scoped namespace (C# 10 and later).</summary>
    public static GodotScriptStyle StyleOf(Func<string, string?> setting, string? rootNamespace, bool fileScopedTaken)
    {
        string? declared = Word(setting("csharp_style_namespace_declarations"));
        string? braces = Word(setting("csharp_new_line_before_open_brace"));
        bool tabs = Word(setting("indent_style")) == "tab";
        int size = int.TryParse(Word(setting("indent_size")), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int given) && given > 0
            ? given : 4;
        return new GodotScriptStyle(rootNamespace is { Length: > 0 } ? rootNamespace : null, fileScopedTaken && declared != "block_scoped", braces != "none", tabs, size);

        // A value's word before any severity (file_scoped:warning).
        static string? Word(string? value) => value?.Split(':')[0].Trim().ToLowerInvariant();
    }

    /// <summary>A base class's template in a style: Godot's lifecycle methods for a node, an empty class for a resource.</summary>
    public static string Text(string baseClass, GodotScriptStyle style)
    {
        string unit = style.Tabs ? "\t" : new string(' ', style.IndentSize);
        List<string> lines =
        [
            $"// meta-name: {Name}",
            $"// meta-description: A {baseClass} script in the project's namespace and style, written by Passenger",
            .. style.Tabs ? Array.Empty<string>() : ["// meta-space-indent: 0"],
            "using _BINDINGS_NAMESPACE_;",
            string.Empty,
        ];
        int depth = 0;
        if (style.Namespace is { } space && style.FileScoped)
        {
            lines.AddRange([$"namespace {space};", string.Empty]);
        }
        else if (style.Namespace is { } blocked)
        {
            Open($"namespace {blocked}");
        }
        Open("public partial class _CLASS_ : _BASE_");
        if (baseClass != "Resource")
        {
            Open("public override void _Ready()");
            Close();
            lines.Add(string.Empty);
            Open("public override void _Process(double delta)");
            Close();
        }
        while (depth > 0)
        {
            Close();
        }
        return string.Join('\n', lines) + "\n";

        void Open(string head)
        {
            string at = string.Concat(Enumerable.Repeat(unit, depth));
            if (style.BracesOnOwnLine)
            {
                lines.AddRange([at + head, at + "{"]);
            }
            else
            {
                lines.Add($"{at}{head} {{");
            }
            depth++;
        }

        void Close()
        {
            depth--;
            lines.Add(string.Concat(Enumerable.Repeat(unit, depth)) + "}");
        }
    }
}
