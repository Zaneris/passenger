using System.Globalization;
using System.Text;

namespace Passenger.App.Godot;

/// <summary>A running game's value as a scene writes it (<see cref="Scene"/>: <c>Vector2(100, 50)</c>, <c>2.0</c>,
/// <c>Color(1, 0, 0, 1)</c>) and as C# writes it for a member's default (<see cref="CSharp"/>: <c>new Vector2(100f, 50f)</c>,
/// <c>2f</c>), each number in its shortest form: a single's as a single, so 0.3 sent as one reads 0.3.</summary>
public static class GodotText
{
    /// <summary>A number in its shortest form, a single's as a single.</summary>
    public static string Number(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }
        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "inf_neg";
        }
        return (double)(float)value == value
            ? ((float)value).ToString("R", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>A value as a scene's property writes it, or null for one a scene cannot hold as text (an object).</summary>
    public static string? Scene(object? value) => value switch
    {
        null => "null",
        bool flag => flag ? "true" : "false",
        long whole => whole.ToString(CultureInfo.InvariantCulture),
        double real => Real(real),
        string text => Quoted(text),
        GodotName name => "&" + Quoted(name.Name),
        GodotNodePath path => $"NodePath({Quoted(path.ToString())})",
        GodotMath math => $"{SceneName(math.Type)}({string.Join(", ", math.Values.Select(v => math.Type is GodotType.Vector2I or GodotType.Vector3I or GodotType.Vector4I or GodotType.Rect2I ? ((long)v).ToString(CultureInfo.InvariantCulture) : Number(v)))})",
        _ => null,
    };

    /// <summary>A scalar real as a scene writes it: a whole one with ".0", so it reads back as a real.</summary>
    private static string Real(double value)
    {
        string written = Number(value);
        return written.Contains('.', StringComparison.Ordinal) || written.Contains('E', StringComparison.OrdinalIgnoreCase) || written.StartsWith("inf", StringComparison.Ordinal)
            || written == "nan" ? written : written + ".0";
    }

    /// <summary>A string as a scene quotes it: its backslashes and quotes escaped, its lines as they are.</summary>
    public static string Quoted(string text)
    {
        StringBuilder written = new("\"");
        foreach (char c in text)
        {
            if (c is '\\' or '"')
            {
                written.Append('\\');
            }
            written.Append(c);
        }
        return written.Append('"').ToString();
    }

    /// <summary>A Variant type's name as a scene writes a value of it.</summary>
    private static string SceneName(GodotType type) => type switch
    {
        GodotType.Vector2I => "Vector2i",
        GodotType.Vector3I => "Vector3i",
        GodotType.Vector4I => "Vector4i",
        GodotType.Rect2I => "Rect2i",
        GodotType.Aabb => "AABB",
        _ => type.ToString(),
    };

    /// <summary>A value as C# writes a member's default, for the member's declared type (<paramref name="declared"/>: "float",
    /// "double", an enum's name), or null for one C# is not written for here. An enum's value is its name among the
    /// choices given.</summary>
    public static string? CSharp(object? value, string declared, IReadOnlyList<GodotChoice>? choices = null)
    {
        string type = declared.Trim();
        return value switch
        {
            bool flag => flag ? "true" : "false",
            long whole when choices?.FirstOrDefault(c => c.Value is long v && v == whole) is { } choice && !IsInteger(type) => $"{type}.{choice.Name}",
            long whole => whole.ToString(CultureInfo.InvariantCulture),
            double real when type is "double" or "System.Double" or "Double" => Literal(real, "d"),
            double real => Literal(real, "f"),
            string text => Escaped(text),
            GodotName name => Escaped(name.Name),
            GodotNodePath path => $"new NodePath({Escaped(path.ToString())})",
            GodotMath { Type: GodotType.Vector2I or GodotType.Vector3I or GodotType.Vector4I or GodotType.Rect2I } math =>
                $"new {CSharpName(math.Type)}({string.Join(", ", math.Values.Select(v => ((long)v).ToString(CultureInfo.InvariantCulture)))})",
            GodotMath { Type: GodotType.Vector2 or GodotType.Vector3 or GodotType.Vector4 or GodotType.Color or GodotType.Rect2 or GodotType.Quaternion or GodotType.Plane } math =>
                $"new {CSharpName(math.Type)}({string.Join(", ", math.Values.Select(v => Literal(v, "f")))})",
            _ => null,
        };
    }

    private static bool IsInteger(string type) => type is "int" or "long" or "short" or "byte" or "uint" or "ulong" or "ushort" or "sbyte"
        or "Int32" or "Int64" or "System.Int32" or "System.Int64";

    private static string CSharpName(GodotType type) => type switch
    {
        GodotType.Vector2I => "Vector2I",
        GodotType.Vector3I => "Vector3I",
        GodotType.Vector4I => "Vector4I",
        GodotType.Rect2I => "Rect2I",
        _ => type.ToString(),
    };

    /// <summary>A real as a C# literal of a suffix's type: "2f", "0.5f", "1.5d" as "1.5".</summary>
    private static string Literal(double value, string suffix)
    {
        if (double.IsNaN(value))
        {
            return suffix == "f" ? "float.NaN" : "double.NaN";
        }
        if (double.IsInfinity(value))
        {
            return (suffix == "f" ? "float." : "double.") + (value > 0 ? "PositiveInfinity" : "NegativeInfinity");
        }
        string written = suffix == "f" ? ((float)value).ToString("R", CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture);
        return suffix == "f" ? written + "f" : written.Contains('.', StringComparison.Ordinal) || written.Contains('E', StringComparison.OrdinalIgnoreCase) ? written : written + ".0";
    }

    /// <summary>A string as a C# literal, its escapes written.</summary>
    private static string Escaped(string text)
    {
        StringBuilder written = new("\"");
        foreach (char c in text)
        {
            written.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }
        return written.Append('"').ToString();
    }
}
