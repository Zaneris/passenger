namespace Passenger.Workspace.GDScript;

/// <summary>Godot's analyzer's errors of a call it makes as it reads the script, every argument a constant (reduce_call):
/// a math utility function given a type it takes none of, in core/variant/variant_utility.cpp's words, GDScript's len(),
/// char() and ord() given what they refuse, in gdscript_utility_functions.cpp's, and from Godot 4.8 a Color made from a
/// code that is no colour (core/math/color.cpp's html_is_valid and find_named_color). Each said where the class is read
/// whole.</summary>
public sealed partial class GDScriptAnalyzer
{
    /// <summary>The built-in types floor(), ceil(), round(), abs(), sign() and snapped() take.</summary>
    private static readonly HashSet<string> Rounded = new(StringComparer.Ordinal)
    {
        "int", "float", "Vector2", "Vector2i", "Vector3", "Vector3i", "Vector4", "Vector4i",
    };

    /// <summary>The built-in types lerp() goes between.</summary>
    private static readonly HashSet<string> Lerped = new(StringComparer.Ordinal)
    {
        "int", "float", "Vector2", "Vector3", "Vector4", "Quaternion", "Basis", "Color", "Transform2D", "Transform3D",
    };

    /// <summary>The built-in types len() gives a length of.</summary>
    private static readonly HashSet<string> Measured = new(StringComparer.Ordinal)
    {
        "String", "StringName", "Dictionary", "Array", "PackedByteArray", "PackedInt32Array", "PackedInt64Array", "PackedFloat32Array",
        "PackedFloat64Array", "PackedStringArray", "PackedVector2Array", "PackedVector3Array", "PackedColorArray", "PackedVector4Array",
    };

    private const string RoundedWords = "Argument \"x\" must be \"int\", \"float\", \"Vector2\", \"Vector2i\", \"Vector3\", \"Vector3i\", \"Vector4\", or \"Vector4i\".";

    /// <summary>A utility function's errors where Godot calls it as it reads the script, each at the argument it names.</summary>
    private void EvaluatedCallErrors(GDScriptCall call, string function, Context context)
    {
        if (!Evaluated(call, context))
        {
            return;
        }
        IReadOnlyList<GDScriptExpression> arguments = call.Arguments;
        switch (function)
        {
            case "floor" or "ceil" or "round" or "abs" or "sign" when arguments.Count == 1 && BuiltinOf(arguments[0], context) is { } x && !Rounded.Contains(x):
                Said(0, RoundedWords);
                break;
            case "snapped" when arguments.Count == 2 && BuiltinOf(arguments[0], context) is { } x && BuiltinOf(arguments[1], context) is { } step:
                if (!Rounded.Contains(x))
                {
                    Said(0, RoundedWords);
                }
                else if (x != step && x is "int" or "float")
                {
                    if (step is not ("int" or "float"))
                    {
                        Said(1, "Argument \"step\" must be \"int\" or \"float\".");
                    }
                }
                else if (x != step)
                {
                    Said(1, $"argument 2 should be \"{x}\" but is \"{context.Analysis.TypeOf(arguments[1])}\".");
                }
                break;
            case "lerp" when arguments.Count == 3 && BuiltinOf(arguments[0], context) is { } from && BuiltinOf(arguments[1], context) is { } to
                && BuiltinOf(arguments[2], context) is "int" or "float":
                if (!Lerped.Contains(from))
                {
                    Said(0, "Argument \"from\" must be \"int\", \"float\", \"Vector2\", \"Vector3\", \"Vector4\", \"Color\", \"Quaternion\", \"Basis\", \"Transform2D\", or \"Transform3D\".");
                }
                else if (from != to)
                {
                    Said(1, $"argument 2 should be \"{from}\" but is \"{context.Analysis.TypeOf(arguments[1])}\".");
                }
                break;
            case "len" when arguments.Count == 1 && BuiltinOf(arguments[0], context) is { } held && !Measured.Contains(held):
                Said(0, $"Value of type '{held}' can't provide a length.");
                break;
            case "char" when arguments.Count == 1 && IntegerOf(arguments[0]) is { } code:
                if (Engine?.Version >= new Version(4, 8))
                {
                    if (code <= 0 || (code & 0xFFFFF800) == 0xD800 || code > 0x10FFFF)
                    {
                        Said(0, $"{code} is not a valid character code.");
                    }
                }
                else if (code < 0 || code > uint.MaxValue)
                {
                    Said(0, "Expected an integer between 0 and 2^32 - 1.");
                }
                break;
            case "ord" when arguments.Count == 1 && arguments[0] is GDScriptLiteral { Kind: GDScriptLiteralKind.String, Value: string text } && text.EnumerateRunes().Count() != 1:
                Said(0, "Expected a string of length 1 (a character).");
                break;
            default:
                break;
        }

        void Said(int at, string words) => Error(context, arguments[at], $"Invalid argument for \"{function}()\" function: {words}");
    }

    /// <summary>From Godot 4.8, a Color made from a constant code that is no colour, said at the code; whether it was.</summary>
    private bool ColorCodeError(GDScriptCall call, Context context)
    {
        if (Engine?.Version < new Version(4, 8) || Engine?.Builtins.GetValueOrDefault("Color") is not { } color || !Evaluated(call, context)
            || call.Arguments is not [{ } first, ..] || BuiltinOf(first, context) is not "String" || ConstantText(first, context) is not { } code)
        {
            return false;
        }
        int digits = code.Length - (code.StartsWith('#') ? 1 : 0);
        bool html = digits is 3 or 4 or 6 or 8 && !code.AsSpan(code.Length - digits).ContainsAnyExcept("0123456789abcdefABCDEF");
        string named = new([.. code.Where(c => c is not (' ' or '-' or '_' or '\'' or '.')).Select(char.ToUpperInvariant)]);
        if (html || color.Constants.Keys.Any(k => k.Replace("_", string.Empty, StringComparison.Ordinal) == named))
        {
            return false;
        }
        Error(context, first, $"Invalid color code \"{code}\".");
        return true;
    }

    /// <summary>Whether Godot calls a call as it reads the script: the class read whole and every argument a constant.</summary>
    private bool Evaluated(GDScriptCall call, Context context) =>
        context.Analysis.Tree is not null && Knows(context.Class) && call.Arguments.All(a => Constancy(a, context) == true);

    /// <summary>A constant argument's built-in type as Godot names it (null's Nil), or null for any other.</summary>
    private static string? BuiltinOf(GDScriptExpression argument, Context context) =>
        context.Analysis.TypeOf(argument) is { Kind: GDScriptTypeKind.Builtin, Meta: false } type ? type.Name : null;
}
