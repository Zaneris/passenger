using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Passenger.Workspace;

/// <summary>What a project's Godot holds a value's text to beyond its words, each left to Godot where null: its version,
/// whether a <c>res://</c> file is there, and whether its class database makes an object of a class.</summary>
public sealed record GodotVariantBounds(Version? Engine, Func<string, bool>? IsFile, Func<string, bool>? IsClass);

/// <summary>Whether text is one value a scene's line can hold, read token by token as Godot's own variant parser reads it:
/// numbers, strings, names, a colour's hex, arrays, dictionaries, and each constructor with what it takes, the old names
/// Godot still reads among them. Nothing may follow the value, as a scene's reader takes what does for the next property's
/// name, swallowing lines after it. A value a project's Godot would not take though it reads (a class it lacks, a file not
/// there, words newer than it) is refused where <see cref="GodotVariantBounds"/> say so.</summary>
public static class GodotVariantText
{
    private const int Deepest = 512;

    /// <summary>The first Godots whose reader takes an empty Signal or Callable; a packed array of Vector4s or bytes as base64;
    /// and a typed Dictionary.</summary>
    private static readonly Version Callables = new(4, 1);
    private static readonly Version Vector4Arrays = new(4, 3);
    private static readonly Version TypedDictionaries = new(4, 4);

    /// <summary>The constructors of a fixed count of numbers, by the names Godot reads.</summary>
    private static readonly Dictionary<string, int> Counted = new(StringComparer.Ordinal)
    {
        ["Vector2"] = 2, ["Vector2i"] = 2, ["Vector3"] = 3, ["Vector3i"] = 3, ["Vector4"] = 4, ["Vector4i"] = 4, ["Rect2"] = 4, ["Rect2i"] = 4,
        ["Plane"] = 4, ["Quaternion"] = 4, ["Quat"] = 4, ["Color"] = 4, ["Transform2D"] = 6, ["Matrix32"] = 6, ["AABB"] = 6, ["Rect3"] = 6,
        ["Basis"] = 9, ["Matrix3"] = 9, ["Transform3D"] = 12, ["Transform"] = 12, ["Projection"] = 16,
    };

    /// <summary>The packed arrays of any count of numbers, by the names Godot reads.</summary>
    private static readonly HashSet<string> Numbers = new(StringComparer.Ordinal)
    {
        "PackedInt32Array", "PackedIntArray", "PoolIntArray", "IntArray", "PackedInt64Array", "PackedFloat32Array", "PackedRealArray", "PoolRealArray", "FloatArray",
        "PackedFloat64Array", "PackedVector2Array", "PoolVector2Array", "Vector2Array", "PackedVector3Array", "PoolVector3Array", "Vector3Array",
        "PackedVector4Array", "PoolVector4Array", "Vector4Array", "PackedColorArray", "PoolColorArray", "ColorArray",
    };

    /// <summary>Why text is no one value of a scene's, null where Godot reads it: what it names by id must be in
    /// <paramref name="scene"/>, or its load fails, and it must keep to the project's <paramref name="bounds"/>.</summary>
    public static string? WhyNot(string text, GodotFile scene, GodotVariantBounds? bounds = null)
    {
        Reader reader = new(text);
        if (reader.Value(reader.Next(), 0) is { } why)
        {
            return why;
        }
        if (reader.Next() != Token.End)
        {
            return "more follows the value";
        }
        return reader.Externals.FirstOrDefault(id => scene.External(id) is null) is { } external ? $"the scene names no external resource {external}"
            : reader.Inner.FirstOrDefault(id => !scene.Sections.Any(s => s.Tag == "sub_resource" && s.Text("id") == id)) is { } inner ? $"the scene names no sub-resource {inner}"
            : bounds?.Engine is { } engine && reader.Newer.Find(n => engine < n.Since) is { Words: not null } newer ? $"{newer.Words} needs Godot {newer.Since} or newer, and this one is {engine.ToString(2)}"
            : bounds?.IsFile is { } isFile && reader.Files.Find(f => !isFile(f)) is { } lacked ? $"the project has no {lacked}"
            : bounds?.IsClass is { } isClass && reader.Classes.Find(c => !isClass(c)) is { } unmade ? $"Godot makes no object of class {unmade}"
            : null;
    }

    private enum Token
    {
        End, Bad, CurlyOpen, CurlyClose, SquareOpen, SquareClose, RoundOpen, RoundClose, Colon, Comma, Other, Colour, Text, Name, Number, Word,
    }

    /// <summary>The text's tokens, and each value made of them; a value's reading answers why it is none, null where it is.</summary>
    private sealed class Reader(string text)
    {
        private int at;

        /// <summary>What the last string, number or word said.</summary>
        private string said = string.Empty;

        /// <summary>Why the last token was none.</summary>
        private string bad = string.Empty;

        /// <summary>The ids of the external resources named.</summary>
        public List<string> Externals { get; } = [];

        /// <summary>The ids of the sub-resources named.</summary>
        public List<string> Inner { get; } = [];

        /// <summary>The <c>res://</c> files named with no uid to find them by instead.</summary>
        public List<string> Files { get; } = [];

        /// <summary>The classes of the objects written.</summary>
        public List<string> Classes { get; } = [];

        /// <summary>What was written that an older Godot's reader refuses, and the first Godot that takes it.</summary>
        public List<(string Words, Version Since)> Newer { get; } = [];

        public Token Next()
        {
            while (at < text.Length)
            {
                char c = text[at++];
                switch (c)
                {
                    case '\0':
                        return Token.End;
                    case '{':
                        return Token.CurlyOpen;
                    case '}':
                        return Token.CurlyClose;
                    case '[':
                        return Token.SquareOpen;
                    case ']':
                        return Token.SquareClose;
                    case '(':
                        return Token.RoundOpen;
                    case ')':
                        return Token.RoundClose;
                    case ':':
                        return Token.Colon;
                    case ',':
                        return Token.Comma;
                    case '.' or '=':
                        return Token.Other;
                    case ';':
                        at = text.IndexOf('\n', at) is var line and >= 0 ? line + 1 : text.Length;
                        break;
                    case '#':
                        while (at < text.Length && char.IsAsciiHexDigit(text[at]))
                        {
                            at++;
                        }
                        return Token.Colour;
                    case '&' or '@':
                        return at < text.Length && text[at++] == '"' ? Quoted(Token.Name) : Refused("a quote is expected after &");
                    case '"':
                        return Quoted(Token.Text);
                    default:
                        if (c > ' ')
                        {
                            return NumberOrWord(c);
                        }
                        break;
                }
            }
            return Token.End;
        }

        private Token Refused(string why)
        {
            bad = why;
            return Token.Bad;
        }

        /// <summary>A string from after its opening quote, its escapes as Godot reads them: a \u's half of a surrogate pair
        /// must meet its other half at once.</summary>
        private Token Quoted(Token kind)
        {
            int quote = at - 1;
            bool lead = false;
            while (true)
            {
                if (at >= text.Length || text[at] == '\0')
                {
                    return Refused("a string has no closing quote");
                }
                char c = text[at++];
                if (c == '"')
                {
                    break;
                }
                if (c != '\\')
                {
                    if (lead)
                    {
                        return Refused("a string's surrogate has no pair");
                    }
                    continue;
                }
                if (at >= text.Length || text[at] == '\0')
                {
                    return Refused("a string has no closing quote");
                }
                char escaped = text[at++];
                int code = escaped;
                if (escaped is 'u' or 'U')
                {
                    int digits = escaped == 'U' ? 6 : 4;
                    if (at + digits > text.Length || !int.TryParse(text.AsSpan(at, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                    {
                        return Refused("a string's \\u is no number");
                    }
                    at += digits;
                }
                bool isLead = code is >= 0xD800 and <= 0xDBFF;
                bool isTrail = code is >= 0xDC00 and <= 0xDFFF;
                if (isLead ? lead : isTrail ? !lead : lead)
                {
                    return Refused("a string's surrogate has no pair");
                }
                lead = isLead;
            }
            if (lead)
            {
                return Refused("a string's surrogate has no pair");
            }
            said = GodotFile.Quoted(text, quote).Value;
            return kind;
        }

        private Token NumberOrWord(char first)
        {
            int start = at - 1;
            char c = first;
            if (c == '-')
            {
                c = at < text.Length ? text[at++] : '\0';
            }
            if (char.IsAsciiDigit(c))
            {
                Digits();
                if (at < text.Length && text[at] == '.')
                {
                    at++;
                    Digits();
                }
                if (at < text.Length && text[at] is 'e' or 'E')
                {
                    at++;
                    at += at < text.Length && text[at] is '+' or '-' ? 1 : 0;
                    Digits();
                }
                said = text[start..at];
                return Token.Number;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] == '_'))
                {
                    at++;
                }
                said = text[start..at];
                return Token.Word;
            }
            return Refused($"{(c == '\0' ? first : c)} has no place in a value");
        }

        private void Digits()
        {
            while (at < text.Length && char.IsAsciiDigit(text[at]))
            {
                at++;
            }
        }

        public string? Value(Token token, int depth)
        {
            if (depth > Deepest || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            {
                return "it is nested too deeply";
            }
            return token switch
            {
                Token.CurlyOpen => Entries(depth),
                Token.SquareOpen => Items(depth),
                Token.Word => Named(said, depth),
                Token.Number or Token.Text or Token.Name or Token.Colour => null,
                Token.Bad => bad,
                _ => "a value is expected",
            };
        }

        private string? Items(int depth)
        {
            bool item = false;
            while (true)
            {
                Token token = Next();
                if (token == Token.SquareClose)
                {
                    return null;
                }
                if (item)
                {
                    if (token != Token.Comma)
                    {
                        return "',' is expected between an array's items";
                    }
                    item = false;
                }
                else if (Value(token, depth + 1) is { } why)
                {
                    return why;
                }
                else
                {
                    item = true;
                }
            }
        }

        private string? Entries(int depth)
        {
            bool entry = false;
            while (true)
            {
                Token token = Next();
                if (token == Token.CurlyClose)
                {
                    return null;
                }
                if (entry)
                {
                    if (token != Token.Comma)
                    {
                        return "',' is expected between a dictionary's entries";
                    }
                    entry = false;
                }
                else if ((Value(token, depth + 1) ?? (Next() != Token.Colon ? "':' is expected after a key" : Value(Next(), depth + 1))) is { } why)
                {
                    return why;
                }
                else
                {
                    entry = true;
                }
            }
        }

        private string? Named(string id, int depth)
        {
            if (id is "true" or "false" or "null" or "nil" or "inf" or "-inf" or "inf_neg" or "nan")
            {
                return null;
            }
            if (Counted.TryGetValue(id, out int wanted))
            {
                (int count, string? why) = NumbersIn();
                return why ?? (count == wanted ? null : $"{id} takes {wanted} numbers");
            }
            if (Numbers.Contains(id))
            {
                if (id.Contains("Vector4", StringComparison.Ordinal))
                {
                    Newer.Add((id, Vector4Arrays));
                }
                return NumbersIn().WhyNot;
            }
            return id switch
            {
                "PackedByteArray" or "PoolByteArray" or "ByteArray" => Bytes(),
                "PackedStringArray" or "PoolStringArray" or "StringArray" => Strings(),
                "NodePath" => Next() != Token.RoundOpen || Next() != Token.Text || Next() != Token.RoundClose ? "NodePath takes a string" : null,
                "RID" => Rid(),
                "Signal" or "Callable" => Empty(id),
                "Object" => OfClass(depth),
                "Resource" or "SubResource" or "ExtResource" => Next() != Token.RoundOpen ? $"'(' is expected after {id}" : Reference(id),
                "Array" => TypedArray(depth),
                "Dictionary" => TypedDictionary(depth),
                _ => $"{id} is no value",
            };
        }

        private string? Empty(string id)
        {
            Newer.Add((id, Callables));
            return Next() != Token.RoundOpen || Next() != Token.RoundClose ? $"{id} takes nothing" : null;
        }

        private static bool IsNumberWord(string word) => word is "inf" or "-inf" or "inf_neg" or "nan";

        /// <summary>The numbers in parentheses after a constructor's name, and how many.</summary>
        private (int Count, string? WhyNot) NumbersIn()
        {
            if (Next() != Token.RoundOpen)
            {
                return (0, "'(' is expected after a constructor's name");
            }
            int count = 0;
            while (true)
            {
                if (count > 0)
                {
                    Token after = Next();
                    if (after == Token.RoundClose)
                    {
                        return (count, null);
                    }
                    if (after != Token.Comma)
                    {
                        return (0, "',' or ')' is expected after a number");
                    }
                }
                Token token = Next();
                if (count == 0 && token == Token.RoundClose)
                {
                    return (0, null);
                }
                if (token != Token.Number && !(token == Token.Word && IsNumberWord(said)))
                {
                    return (0, "a number is expected");
                }
                count++;
            }
        }

        /// <summary>A byte array's bytes: their base64 in a string, or numbers.</summary>
        private string? Bytes()
        {
            if (Next() != Token.RoundOpen)
            {
                return "'(' is expected after a constructor's name";
            }
            Token token = Next();
            if (token == Token.Text)
            {
                Newer.Add(("Bytes as base64", Vector4Arrays));
                return !Base64.IsValid(said) ? "the bytes are no base64" : Next() != Token.RoundClose ? "')' is expected after the bytes" : null;
            }
            while (token != Token.RoundClose)
            {
                if (token != Token.Number && !(token == Token.Word && IsNumberWord(said)))
                {
                    return "a number is expected";
                }
                Token after = Next();
                if (after == Token.RoundClose)
                {
                    return null;
                }
                if (after != Token.Comma)
                {
                    return "',' or ')' is expected after a number";
                }
                token = Next();
                if (token == Token.RoundClose)
                {
                    return "a number is expected";
                }
            }
            return null;
        }

        private string? Strings()
        {
            if (Next() != Token.RoundOpen)
            {
                return "'(' is expected after a constructor's name";
            }
            bool first = true;
            while (true)
            {
                if (!first)
                {
                    Token after = Next();
                    if (after == Token.RoundClose)
                    {
                        return null;
                    }
                    if (after != Token.Comma)
                    {
                        return "',' or ')' is expected after a string";
                    }
                }
                Token token = Next();
                if (token == Token.RoundClose)
                {
                    return null;
                }
                if (token != Token.Text)
                {
                    return "a string is expected";
                }
                first = false;
            }
        }

        private string? Rid()
        {
            if (Next() != Token.RoundOpen)
            {
                return "'(' is expected after RID";
            }
            Token token = Next();
            return token == Token.RoundClose || (token == Token.Number && Next() == Token.RoundClose) ? null : "RID takes a number or nothing";
        }

        /// <summary>An object by its class and its properties' strings and values.</summary>
        private string? OfClass(int depth)
        {
            if (Next() != Token.RoundOpen || Next() != Token.Word)
            {
                return "Object takes its class and a comma first";
            }
            Classes.Add(said);
            if (Next() != Token.Comma)
            {
                return "Object takes its class and a comma first";
            }
            bool property = false;
            while (true)
            {
                Token token = Next();
                if (token == Token.RoundClose)
                {
                    return null;
                }
                if (property)
                {
                    if (token != Token.Comma)
                    {
                        return "',' is expected between an object's properties";
                    }
                    property = false;
                }
                else if (token != Token.Text || Next() != Token.Colon)
                {
                    return "a property's name in quotes and ':' are expected";
                }
                else if (Value(Next(), depth + 1) is { } why)
                {
                    return why;
                }
                else
                {
                    property = true;
                }
            }
        }

        /// <summary>A resource named from after its '(': by its id in the scene, or by its path (with its uid beside it).</summary>
        private string? Reference(string kind)
        {
            Token token = Next();
            if (kind == "Resource")
            {
                if (token != Token.Text)
                {
                    return "Resource takes a path in quotes";
                }
                string first = said;
                string? second = null;
                token = Next();
                if (token == Token.Comma)
                {
                    if (Next() != Token.Text)
                    {
                        return "Resource takes a path in quotes";
                    }
                    second = said;
                    token = Next();
                }
                if (second is not null && IsUid(first) == IsUid(second))
                {
                    return "Resource takes one path and one uid";
                }
                if (second is null && first.StartsWith(GodotResPaths.Scheme, StringComparison.Ordinal))
                {
                    Files.Add(first);
                }
                return token == Token.RoundClose ? null : "')' is expected after a resource's path";
            }
            if (token is not (Token.Text or Token.Number))
            {
                return $"{kind} takes an id";
            }
            (kind == "ExtResource" ? Externals : Inner).Add(said);
            return Next() == Token.RoundClose ? null : "')' is expected after a resource's id";
        }

        private static bool IsUid(string path) => path.StartsWith(GodotUids.Scheme, StringComparison.Ordinal);

        /// <summary>A typed container's element type, up to the token that ends it: a type's name, however Godot takes it (one
        /// it doesn't know leaves the container untyped), or a script's resource.</summary>
        private string? ElementType(Token closing)
        {
            if (Next() != Token.Word)
            {
                return "a type is expected";
            }
            string type = said;
            if (type is "Resource" or "SubResource" or "ExtResource")
            {
                Token after = Next();
                if (after == closing && type == "Resource")
                {
                    return null;
                }
                if (after != Token.RoundOpen)
                {
                    return $"'(' is expected after {type}";
                }
                if (Reference(type) is { } why)
                {
                    return why;
                }
            }
            return Next() == closing ? null : "a typed container's types are not closed";
        }

        private string? TypedArray(int depth)
        {
            if (Next() != Token.SquareOpen)
            {
                return "Array takes its type in brackets";
            }
            return ElementType(Token.SquareClose) ?? (Next() != Token.RoundOpen || Next() != Token.SquareOpen ? "Array[…] takes an array in parentheses"
                : Items(depth + 1) ?? (Next() != Token.RoundClose ? "')' is expected after the array" : null));
        }

        private string? TypedDictionary(int depth)
        {
            if (Next() != Token.SquareOpen)
            {
                return "Dictionary takes its types in brackets";
            }
            Newer.Add(("A typed Dictionary", TypedDictionaries));
            return ElementType(Token.Comma) ?? ElementType(Token.SquareClose) ?? (Next() != Token.RoundOpen || Next() != Token.CurlyOpen ? "Dictionary[…] takes a dictionary in parentheses"
                : Entries(depth + 1) ?? (Next() != Token.RoundClose ? "')' is expected after the dictionary" : null));
        }
    }
}
