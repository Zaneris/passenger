namespace Passenger.Workspace.GDScript;

/// <summary>What a project makes of a GDScript warning: passed over, a warning, or an error.</summary>
public enum GDScriptWarnLevel
{
    Ignore,
    Warn,
    Error,
}

/// <summary>One of Godot's findings in a script, where Passenger's reading gives it: its range in the text, Godot's name for
/// it where it is a warning (<c>UNUSED_VARIABLE</c>; null for an error), and its words as Godot's own; or, a note, Passenger's
/// word of its own reading, which says nothing is wrong with the script.</summary>
public readonly record struct GDScriptFinding(int Start, int End, string? Code, string Message, bool IsNote = false)
{
    /// <summary>Whether it is an error: one of Godot's, or a warning the project treats as one.</summary>
    public bool IsError => !IsNote && (Code is null || Message.EndsWith(GDScriptWarnings.TreatedAsError, StringComparison.Ordinal));
}

/// <summary>Godot's GDScript warnings (modules/gdscript/gdscript_warning.h, MIT): each one's name, in Godot's order, and the
/// level a project gives it unless its <c>debug/gdscript/warnings/*</c> settings say another.</summary>
public static class GDScriptWarnings
{
    /// <summary>What a warning a project treats as an error says after its words.</summary>
    public const string TreatedAsError = " (Warning treated as error.)";

    private static readonly (string Name, GDScriptWarnLevel Level)[] All =
    [
        ("UNASSIGNED_VARIABLE", GDScriptWarnLevel.Warn),
        ("UNASSIGNED_VARIABLE_OP_ASSIGN", GDScriptWarnLevel.Warn),
        ("UNUSED_VARIABLE", GDScriptWarnLevel.Warn),
        ("UNUSED_LOCAL_CONSTANT", GDScriptWarnLevel.Warn),
        ("UNUSED_PRIVATE_CLASS_VARIABLE", GDScriptWarnLevel.Warn),
        ("UNUSED_PARAMETER", GDScriptWarnLevel.Warn),
        ("UNUSED_SIGNAL", GDScriptWarnLevel.Warn),
        ("SHADOWED_VARIABLE", GDScriptWarnLevel.Warn),
        ("SHADOWED_VARIABLE_BASE_CLASS", GDScriptWarnLevel.Warn),
        ("SHADOWED_GLOBAL_IDENTIFIER", GDScriptWarnLevel.Warn),
        ("UNREACHABLE_CODE", GDScriptWarnLevel.Warn),
        ("UNREACHABLE_PATTERN", GDScriptWarnLevel.Warn),
        ("STANDALONE_EXPRESSION", GDScriptWarnLevel.Warn),
        ("STANDALONE_TERNARY", GDScriptWarnLevel.Warn),
        ("INCOMPATIBLE_TERNARY", GDScriptWarnLevel.Warn),
        ("UNTYPED_DECLARATION", GDScriptWarnLevel.Ignore),
        ("INFERRED_DECLARATION", GDScriptWarnLevel.Ignore),
        ("UNSAFE_PROPERTY_ACCESS", GDScriptWarnLevel.Ignore),
        ("UNSAFE_METHOD_ACCESS", GDScriptWarnLevel.Ignore),
        ("UNSAFE_CAST", GDScriptWarnLevel.Ignore),
        ("UNSAFE_CALL_ARGUMENT", GDScriptWarnLevel.Ignore),
        ("UNSAFE_VOID_RETURN", GDScriptWarnLevel.Warn),
        ("RETURN_VALUE_DISCARDED", GDScriptWarnLevel.Ignore),
        ("STATIC_CALLED_ON_INSTANCE", GDScriptWarnLevel.Warn),
        ("MISSING_TOOL", GDScriptWarnLevel.Warn),
        ("REDUNDANT_STATIC_UNLOAD", GDScriptWarnLevel.Warn),
        ("REDUNDANT_AWAIT", GDScriptWarnLevel.Warn),
        ("MISSING_AWAIT", GDScriptWarnLevel.Ignore),
        ("ASSERT_ALWAYS_TRUE", GDScriptWarnLevel.Warn),
        ("ASSERT_ALWAYS_FALSE", GDScriptWarnLevel.Warn),
        ("INTEGER_DIVISION", GDScriptWarnLevel.Warn),
        ("NARROWING_CONVERSION", GDScriptWarnLevel.Warn),
        ("INT_AS_ENUM_WITHOUT_CAST", GDScriptWarnLevel.Warn),
        ("INT_AS_ENUM_WITHOUT_MATCH", GDScriptWarnLevel.Warn),
        ("ENUM_VARIABLE_WITHOUT_DEFAULT", GDScriptWarnLevel.Warn),
        ("EMPTY_FILE", GDScriptWarnLevel.Warn),
        ("DEPRECATED_KEYWORD", GDScriptWarnLevel.Warn),
        ("CONFUSABLE_IDENTIFIER", GDScriptWarnLevel.Warn),
        ("CONFUSABLE_LOCAL_DECLARATION", GDScriptWarnLevel.Warn),
        ("CONFUSABLE_LOCAL_USAGE", GDScriptWarnLevel.Warn),
        ("CONFUSABLE_CAPTURE_REASSIGNMENT", GDScriptWarnLevel.Warn),
        ("CONFUSABLE_TEMPORARY_MODIFICATION", GDScriptWarnLevel.Warn),
        ("INFERENCE_ON_VARIANT", GDScriptWarnLevel.Error),
        ("NATIVE_METHOD_OVERRIDE", GDScriptWarnLevel.Error),
        ("GET_NODE_DEFAULT_WITHOUT_ONREADY", GDScriptWarnLevel.Error),
        ("ONREADY_WITH_EXPORT", GDScriptWarnLevel.Error),
        ("ONREADY_WITH_CAST", GDScriptWarnLevel.Warn),
        ("PROPERTY_USED_AS_FUNCTION", GDScriptWarnLevel.Warn),
        ("CONSTANT_USED_AS_FUNCTION", GDScriptWarnLevel.Warn),
        ("FUNCTION_USED_AS_PROPERTY", GDScriptWarnLevel.Warn),
    ];

    private static readonly Dictionary<string, GDScriptWarnLevel> Defaults = All.ToDictionary(w => w.Name, w => w.Level, StringComparer.Ordinal);

    /// <summary>Every warning's name, in Godot's order.</summary>
    public static IEnumerable<string> Names => All.Select(w => w.Name);

    /// <summary>Whether Godot has a warning of a name (its upper-case form).</summary>
    public static bool Exists(string name) => Defaults.ContainsKey(name);

    /// <summary>The level a warning has where nothing sets it.</summary>
    public static GDScriptWarnLevel DefaultLevel(string name) => Defaults.GetValueOrDefault(name, GDScriptWarnLevel.Ignore);
}

/// <summary>A project's GDScript warning settings, as its project.godot sets them (Godot's update_project_settings): whether
/// warnings are given at all (<c>debug/gdscript/warnings/enable</c>), each warning's level, and the folders whose scripts
/// give none (<c>directory_rules</c>, or the older <c>exclude_addons</c>; <c>res://addons</c> excluded unless said).</summary>
public sealed record GDScriptWarningSettings(bool Enabled, IReadOnlyDictionary<string, GDScriptWarnLevel> Levels, IReadOnlyList<(string Folder, bool Excluded)> Rules)
{
    private const string Prefix = "debug/gdscript/warnings/";

    /// <summary>Godot's own: every warning at its default level, the addons' scripts giving none.</summary>
    public static GDScriptWarningSettings Default { get; } = new(true, new Dictionary<string, GDScriptWarnLevel>(StringComparer.Ordinal), [("res://addons/", true)]);

    /// <summary>Every warning given, as Godot's own test runner sets them.</summary>
    public static GDScriptWarningSettings Every { get; } = new(true, GDScriptWarnings.Names.ToDictionary(n => n, _ => GDScriptWarnLevel.Warn, StringComparer.Ordinal), []);

    /// <summary>The settings a project.godot's text gives; a value Godot would not read leaves that setting as Godot's own.</summary>
    public static GDScriptWarningSettings Of(string? project)
    {
        if (project is null)
        {
            return Default;
        }
        Dictionary<string, string> values = GodotProject.Values(project);
        bool enabled = !values.TryGetValue(Prefix + "enable", out string? enable) || GodotProject.Truth(enable) != false;
        Dictionary<string, GDScriptWarnLevel> levels = new(StringComparer.Ordinal);
        foreach (string name in GDScriptWarnings.Names)
        {
            if (values.TryGetValue(Prefix + name.ToLowerInvariant(), out string? set) && Level(set) is { } level)
            {
                levels[name] = level;
            }
        }
        Dictionary<string, bool> rules = new(StringComparer.Ordinal) { ["res://addons/"] = true };
        if (values.TryGetValue(Prefix + "directory_rules", out string? written))
        {
            rules = RulesOf(written);
        }
        if (values.TryGetValue(Prefix + "exclude_addons", out string? addons) && GodotProject.Truth(addons) is { } exclude)
        {
            rules["res://addons/"] = exclude;
        }
        // The deepest folder's rule decides, as Godot sorts them.
        return new GDScriptWarningSettings(enabled, levels, [.. rules.OrderByDescending(r => r.Key.Count(c => c == '/')).Select(r => (r.Key, r.Value))]);
    }

    /// <summary>A warning's level for a script.</summary>
    public GDScriptWarnLevel LevelOf(string code) => Levels.TryGetValue(code, out GDScriptWarnLevel level) ? level : GDScriptWarnings.DefaultLevel(code);

    /// <summary>Whether a script (its res:// path) gives no warnings: warnings off, or its folder's rule excludes it.</summary>
    public bool Silences(string script)
    {
        if (!Enabled)
        {
            return true;
        }
        foreach ((string folder, bool excluded) in Rules)
        {
            if (script.StartsWith(folder, StringComparison.Ordinal))
            {
                return excluded;
            }
        }
        return false;
    }

    private static GDScriptWarnLevel? Level(string value) => value.Trim() switch
    {
        "0" or "false" => GDScriptWarnLevel.Ignore,
        "1" or "true" => GDScriptWarnLevel.Warn,
        "2" => GDScriptWarnLevel.Error,
        _ => null,
    };

    /// <summary>A <c>directory_rules</c> dictionary's folders (each ending in '/') and whether each excludes: Godot's
    /// <c>{ "res://addons": 1 }</c>, 1 excluding and 0 including.</summary>
    private static Dictionary<string, bool> RulesOf(string written)
    {
        Dictionary<string, bool> rules = new(StringComparer.Ordinal);
        foreach (GodotDefinitions.Entry rule in GodotDefinitions.DictionaryOf(written, GodotFile.ValueAt(written, 0, written.Length)))
        {
            if (rule.Key.StartsWith(GodotResPaths.Scheme, StringComparison.Ordinal) && rule.Value.Word is "0" or "1")
            {
                rules[rule.Key.EndsWith('/') ? rule.Key : rule.Key + "/"] = rule.Value.Word == "1";
            }
        }
        return rules;
    }
}
