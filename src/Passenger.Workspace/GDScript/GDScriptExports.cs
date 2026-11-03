using System.Globalization;

namespace Passenger.Workspace.GDScript;

/// <summary>A script's exported member as Godot's inspector takes it: its name, Variant type (by its number), hint and hint
/// string, and its default as Godot's variant text writes it, where the script writes it as a constant.</summary>
public sealed record GodotScriptExport(string Name, int Type, long Hint, string HintString, string? Default);

/// <summary>A GDScript's exported variables as Godot's inspector takes them (<c>@export</c>, <c>@export_enum</c>,
/// <c>@export_range</c>, the colour's without alpha): the type it is written or its value is, an enum of the script's by
/// its names and values, and its default where its value is a literal, an enum's member or a built-in type's call of
/// literals. Pure.</summary>
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

    /// <summary>The exports of a script's text, in the order it writes them.</summary>
    public static IReadOnlyList<GodotScriptExport> Of(string text)
    {
        GDScriptTree tree = GDScriptTree.Parse(text);
        Dictionary<string, List<(string Name, long Value)>> enums = new(StringComparer.Ordinal);
        foreach (GDScriptEnum declared in tree.Members.Select(m => m.Node).OfType<GDScriptEnum>().Where(e => e.Identifier is not null))
        {
            long next = 0;
            List<(string, long)> values = [];
            foreach ((GDScriptIdentifier name, GDScriptExpression? value) in declared.Values)
            {
                next = value is GDScriptLiteral { Value: long given } ? given : next;
                values.Add((name.Name, next++));
            }
            enums[declared.Identifier!.Name] = values;
        }
        List<GodotScriptExport> exports = [];
        foreach (GDScriptMember member in tree.Members)
        {
            if (member.Node is not GDScriptVariable { Identifier: { } name } variable
                || variable.Annotations.FirstOrDefault(a => a.Name.StartsWith("@export", StringComparison.Ordinal) && a.Name is not ("@export_category" or "@export_group" or "@export_subgroup")) is not { } export)
            {
                continue;
            }
            string? typed = variable.Type is { Chain.Count: > 0 } type ? string.Join('.', type.Chain.Select(c => c.Name)) : TypeOf(variable.Initializer, enums);
            int variant = typed is null ? 0 : enums.ContainsKey(typed) ? 2 : Variants.GetValueOrDefault(typed);
            (long hint, string words) = export.Name switch
            {
                "@export_enum" => (Enum, string.Join(',', export.Arguments.OfType<GDScriptLiteral>().Select(a => a.Value as string).OfType<string>())),
                "@export_range" => (Range, string.Join(',', export.Arguments.Select(a => Written(text, member.Offset, a)))),
                "@export_color_no_alpha" => (ColorNoAlpha, string.Empty),
                _ when typed is not null && enums.TryGetValue(typed, out List<(string Name, long Value)>? values) =>
                    (Enum, string.Join(',', values.Select(v => $"{v.Name}:{v.Value.ToString(CultureInfo.InvariantCulture)}"))),
                _ => (0, string.Empty),
            };
            if (export.Name == "@export_enum" && variant == 0)
            {
                variant = 2;
            }
            exports.Add(new GodotScriptExport(name.Name, variant, hint, words, Default(text, member.Offset, variable.Initializer, enums)));
        }
        return exports;
    }

    /// <summary>The type a value without one written is: a literal's, an enum member's, a built-in type's call's.</summary>
    private static string? TypeOf(GDScriptExpression? value, Dictionary<string, List<(string Name, long Value)>> enums) => value switch
    {
        GDScriptLiteral { Value: bool } => "bool",
        GDScriptLiteral { Value: long } => "int",
        GDScriptLiteral { Value: double } => "float",
        GDScriptLiteral { Value: string } => "String",
        GDScriptSubscript { IsAttribute: true, Base: GDScriptIdentifier { Name: { } named } } when enums.ContainsKey(named) => named,
        GDScriptCall { Callee: GDScriptIdentifier { Name: { } called } } when Variants.ContainsKey(called) => called,
        _ => null,
    };

    /// <summary>A value as Godot's variant text writes it where the script writes it as a constant: a literal as written, an
    /// enum member by its value, a built-in type's call of literals as written; else null.</summary>
    private static string? Default(string text, int offset, GDScriptExpression? value, Dictionary<string, List<(string Name, long Value)>> enums) => value switch
    {
        GDScriptLiteral { Value: bool or long or double or string } => Written(text, offset, value),
        GDScriptSubscript { IsAttribute: true, Base: GDScriptIdentifier { Name: { } named }, Attribute.Name: { } member } when enums.TryGetValue(named, out List<(string Name, long Value)>? values)
            && values.FirstOrDefault(v => v.Name == member) is { Name: not null } found => found.Value.ToString(CultureInfo.InvariantCulture),
        GDScriptCall { Callee: GDScriptIdentifier { Name: { } called } } call when Variants.ContainsKey(called) && call.Arguments.All(a => a is GDScriptLiteral or GDScriptUnary)
            => Written(text, offset, value),
        _ => null,
    };

    private static string Written(string text, int offset, GDScriptNode node) => text[(offset + node.Start)..(offset + node.End)];
}
