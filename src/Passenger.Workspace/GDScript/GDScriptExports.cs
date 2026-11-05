using System.Globalization;

namespace Passenger.Workspace.GDScript;

/// <summary>A script's exported member as Godot's inspector takes it: its name, Variant type (by its number), hint and hint
/// string, its default as Godot's variant text writes it, where the script writes it as a constant, and the file of the
/// script it is inherited from, none for the script's own.</summary>
public sealed record GodotScriptExport(string Name, int Type, long Hint, string HintString, string? Default, string? Inherited = null);

/// <summary>A GDScript's exported variables as Godot's inspector takes them (<c>@export</c>, <c>@export_enum</c>,
/// <c>@export_range</c>, the colour's without alpha): the type it is written or its value is, an enum by its names and values
/// as Godot folds them, or as a plain integer where only Godot can, and its default where its value is a literal, an enum's
/// member or a built-in type's call of literals. A script's own come first, then each script's it extends. Pure.</summary>
public static class GDScriptExports
{
    private const long Range = 1;
    private const long Enum = 2;
    private const long ColorNoAlpha = 21;

    /// <summary>Variant types by the name GDScript writes them.</summary>
    private static readonly Dictionary<string, int> Variants = new(StringComparer.Ordinal)
    {
        ["bool"] = 1, ["int"] = 2, ["float"] = 3, ["String"] = 4, ["Vector2"] = 5, ["Vector2i"] = 6, ["Rect2"] = 7, ["Rect2i"] = 8,
        ["Vector3"] = 9, ["Vector3i"] = 10, ["Transform2D"] = 11, ["Vector4"] = 12, ["Vector4i"] = 13, ["Plane"] = 14, ["Quaternion"] = 15,
        ["AABB"] = 16, ["Basis"] = 17, ["Transform3D"] = 18, ["Projection"] = 19, ["Color"] = 20, ["StringName"] = 21, ["NodePath"] = 22,
        ["Dictionary"] = 27, ["Array"] = 28,
    };

    /// <summary>The exports of a script's text alone, in the order it writes them.</summary>
    public static IReadOnlyList<GodotScriptExport> Of(string text)
    {
        const string Alone = "res://script.gd";
        GDScriptTree tree = GDScriptTree.Parse(text);
        return Of(tree, Alone, new GDScriptProject(null, path => path == Alone ? tree : null, new Dictionary<string, string>(), new Dictionary<string, string>()));
    }

    /// <summary>The exports of a project's script, its own in the order it writes them, then those of each script it extends
    /// in turn, as Godot's inspector lists them.</summary>
    public static IReadOnlyList<GodotScriptExport> Of(GDScriptTree tree, string path, GDScriptProject project)
    {
        GDScriptAnalyzer analyzer = new(project);
        List<GodotScriptExport> exports = [];
        int depth = 0;
        for (GDScriptType? at = GDScriptAnalyzer.ScriptClass(tree, path); at is { Kind: GDScriptTypeKind.Class, Script: { } script } && depth++ < 64; at = analyzer.BaseOf(at))
        {
            if (project.Script(script) is { } read)
            {
                exports.AddRange(Own(at, read.Text, script == path ? null : Path.GetFileName(script), analyzer));
            }
        }
        return exports;
    }

    /// <summary>The exports a class declares itself, in the order it writes them.</summary>
    private static IEnumerable<GodotScriptExport> Own(GDScriptType owner, string text, string? inherited, GDScriptAnalyzer analyzer)
    {
        foreach ((GDScriptNode node, int offset) in analyzer.OwnMembers(owner))
        {
            if (node is not GDScriptVariable { Identifier: { } name } variable
                || variable.Annotations.FirstOrDefault(a => a.Name.StartsWith("@export", StringComparison.Ordinal) && a.Name is not ("@export_category" or "@export_group" or "@export_subgroup")) is not { } export)
            {
                continue;
            }
            GDScriptType? written = variable.Type is { Chain.Count: > 0 } type ? analyzer.TypeWritten(type, owner, offset) : null;
            GDScriptType? enumerated = written is { Kind: GDScriptTypeKind.Enum } ? written : EnumOf(variable.Initializer, owner, analyzer);
            string? typed = variable.Type is { Chain.Count: > 0 } chain ? string.Join('.', chain.Chain.Select(c => c.Name)) : TypeOf(variable.Initializer);
            int variant = enumerated is not null ? 2 : typed is null ? 0 : Variants.GetValueOrDefault(typed);
            (long hint, string words) = export.Name switch
            {
                "@export_enum" => (Enum, string.Join(',', export.Arguments.OfType<GDScriptLiteral>().Select(a => a.Value as string).OfType<string>())),
                "@export_range" => (Range, string.Join(',', export.Arguments.Select(a => Written(text, offset, a)))),
                "@export_color_no_alpha" => (ColorNoAlpha, string.Empty),
                _ when enumerated is { ValuesPartial: false, Values: { } values } =>
                    (Enum, string.Join(',', values.Select(v => $"{v.Key}:{v.Value.ToString(CultureInfo.InvariantCulture)}"))),
                _ => (0, string.Empty),
            };
            if (export.Name == "@export_enum" && variant == 0)
            {
                variant = 2;
            }
            yield return new GodotScriptExport(name.Name, variant, hint, words, Default(text, offset, variable.Initializer, owner, analyzer), inherited);
        }
    }

    /// <summary>The enum a value is a member of, written as <c>Enum.MEMBER</c>; null for any other.</summary>
    private static GDScriptType? EnumOf(GDScriptExpression? value, GDScriptType owner, GDScriptAnalyzer analyzer) =>
        value is GDScriptSubscript { IsAttribute: true, Base: GDScriptIdentifier { Name: { } named } } ? analyzer.EnumNamed(named, owner) : null;

    /// <summary>The type a value without one written is, where that is no enum's: a literal's, a built-in type's call's.</summary>
    private static string? TypeOf(GDScriptExpression? value) => value switch
    {
        GDScriptLiteral { Value: bool } => "bool",
        GDScriptLiteral { Value: long } => "int",
        GDScriptLiteral { Value: double } => "float",
        GDScriptLiteral { Value: string } => "String",
        GDScriptCall { Callee: GDScriptIdentifier { Name: { } called } } when Variants.ContainsKey(called) => called,
        _ => null,
    };

    /// <summary>A value as Godot's variant text writes it where the script writes it as a constant: a literal as written, an
    /// enum member by its value where that is known, a built-in type's call of literals as written; else null.</summary>
    private static string? Default(string text, int offset, GDScriptExpression? value, GDScriptType owner, GDScriptAnalyzer analyzer) => value switch
    {
        GDScriptLiteral { Value: bool or long or double or string } => Written(text, offset, value),
        GDScriptSubscript { IsAttribute: true, Attribute.Name: { } member } when EnumOf(value, owner, analyzer) is { ValuesPartial: false, Values: { } values } =>
            values.TryGetValue(member, out long found) ? found.ToString(CultureInfo.InvariantCulture) : null,
        GDScriptCall { Callee: GDScriptIdentifier { Name: { } called } } call when Variants.ContainsKey(called) && call.Arguments.All(a => a is GDScriptLiteral or GDScriptUnary)
            => Written(text, offset, value),
        _ => null,
    };

    private static string Written(string text, int offset, GDScriptNode node) => text[(offset + node.Start)..(offset + node.End)];
}
