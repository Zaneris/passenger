using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Passenger.App.Godot;

/// <summary>Godot's Variant types, by the number its encoding writes in a value's header.</summary>
public enum GodotType
{
    Nil, Bool, Int, Float, String,
    Vector2, Vector2I, Rect2, Rect2I, Vector3, Vector3I, Transform2D, Vector4, Vector4I, Plane, Quaternion, Aabb, Basis, Transform3D, Projection,
    Color, StringName, NodePath, Rid, Object, Callable, Signal, Dictionary, Array,
    PackedByteArray, PackedInt32Array, PackedInt64Array, PackedFloat32Array, PackedFloat64Array, PackedStringArray, PackedVector2Array,
    PackedVector3Array, PackedColorArray, PackedVector4Array,
}

/// <summary>A StringName, which Godot tells apart from a String.</summary>
public sealed record GodotName(string Name)
{
    public override string ToString() => Name;
}

/// <summary>A NodePath: its names ("root", "Main"), its subnames (a property's path after a colon), and whether it starts at
/// the root.</summary>
public sealed record GodotNodePath(IReadOnlyList<string> Names, IReadOnlyList<string> Subnames, bool Absolute)
{
    public override string ToString() =>
        (Absolute ? "/" : string.Empty) + string.Join('/', Names) + string.Concat(Subnames.Select(s => ":" + s));
}

/// <summary>A value of numbers alone (a vector, a rectangle, a colour, a transform), its numbers in the order Godot writes
/// them; whole numbers for the integer kinds.</summary>
public sealed record GodotMath(GodotType Type, IReadOnlyList<double> Values)
{
    public override string ToString() => $"({string.Join(", ", Values.Select(GodotVariant.Number))})";

    public bool Equals(GodotMath? other) => other is not null && Type == other.Type && Values.SequenceEqual(other.Values);

    public override int GetHashCode() => HashCode.Combine(Type, Values.Count);
}

/// <summary>An object named by its id alone, as a debugged game sends one.</summary>
public sealed record GodotObjectId(ulong Id)
{
    public override string ToString() => $"<Object#{Id}>";
}

/// <summary>An object sent whole: its class and the properties set on it.</summary>
public sealed record GodotObject(string Class, IReadOnlyList<KeyValuePair<string, object?>> Properties)
{
    public override string ToString() => $"<{Class}>";
}

/// <summary>A server's resource id.</summary>
public sealed record GodotRid(ulong Id)
{
    public override string ToString() => $"RID({Id})";
}

/// <summary>A signal of an object, by its name and the object's id.</summary>
public sealed record GodotSignal(string Name, ulong Object)
{
    public override string ToString() => $"{Name} of <Object#{Object}>";
}

/// <summary>A Callable, which the encoding carries no part of.</summary>
public sealed record GodotCallable
{
    public override string ToString() => "<Callable>";
}

/// <summary>A Dictionary, its entries in order.</summary>
public sealed record GodotDictionary(IReadOnlyList<KeyValuePair<object?, object?>> Entries)
{
    public override string ToString() => Entries.Count == 0 ? "{}"
        : $"{{ {string.Join(", ", Entries.Select(e => $"{GodotVariant.Shown(e.Key, quoted: true)}: {GodotVariant.Shown(e.Value, quoted: true)}"))} }}";
}

/// <summary>Why bytes are no Variant Godot wrote.</summary>
public sealed class GodotVariantException(string message) : Exception(message);

/// <summary>Godot's binary Variant encoding (its marshalls), read and written: a header of four bytes (the type in its low
/// byte, flags above: 64-bit numbers, an object sent as its id, a typed container's element type) and then the value, little
/// endian, a string as its UTF-8 length and bytes padded to four. Read into plain values: null, bool, long, double, string,
/// <see cref="GodotName"/>, <see cref="GodotNodePath"/>, <see cref="GodotMath"/>, lists for arrays, arrays for packed ones,
/// and the records here for the rest. Written for what a debugger sends: those plain values, lists and numbers.</summary>
public static class GodotVariant
{
    private const int Type = 0xFF;
    private const int Wide = 1 << 16;
    private const int ObjectAsId = 1 << 16;
    private const int Deepest = 512;

    /// <summary>The value the bytes hold, all of them.</summary>
    public static object? Read(ReadOnlySpan<byte> bytes)
    {
        int at = 0;
        object? value = Read(bytes, ref at, 0);
        return at == bytes.Length ? value : throw new GodotVariantException($"{bytes.Length - at} bytes follow the value");
    }

    /// <summary>The value at <paramref name="at"/>, which is moved past it.</summary>
    public static object? Read(ReadOnlySpan<byte> bytes, ref int at, int depth)
    {
        if (depth > Deepest)
        {
            throw new GodotVariantException("The value is nested too deep");
        }
        uint header = UInt(bytes, ref at);
        bool wide = (header & Wide) != 0;
        GodotType type = (GodotType)(header & Type);
        switch (type)
        {
            case GodotType.Nil:
                return null;
            case GodotType.Bool:
                return UInt(bytes, ref at) != 0;
            case GodotType.Int:
                return wide ? Long(bytes, ref at) : (long)(int)UInt(bytes, ref at);
            case GodotType.Float:
                return wide ? Double(bytes, ref at) : Single(bytes, ref at);
            case GodotType.String:
                return Text(bytes, ref at);
            case GodotType.Vector2I or GodotType.Rect2I or GodotType.Vector3I or GodotType.Vector4I:
                double[] whole = new double[Count(type)];
                for (int i = 0; i < whole.Length; i++)
                {
                    whole[i] = (int)UInt(bytes, ref at);
                }
                return new GodotMath(type, whole);
            case GodotType.Color:
                return new GodotMath(type, [Single(bytes, ref at), Single(bytes, ref at), Single(bytes, ref at), Single(bytes, ref at)]);
            case GodotType.Vector2 or GodotType.Rect2 or GodotType.Vector3 or GodotType.Transform2D or GodotType.Vector4 or GodotType.Plane
                or GodotType.Quaternion or GodotType.Aabb or GodotType.Basis or GodotType.Transform3D or GodotType.Projection:
                return Real(bytes, ref at, type, Count(type), wide);
            case GodotType.StringName:
                return new GodotName(Text(bytes, ref at));
            case GodotType.NodePath:
                return Path(bytes, ref at);
            case GodotType.Rid:
                return new GodotRid((ulong)Long(bytes, ref at));
            case GodotType.Object:
                return Object(bytes, ref at, (header & ObjectAsId) != 0, depth);
            case GodotType.Callable:
                return new GodotCallable();
            case GodotType.Signal:
                return new GodotSignal(Text(bytes, ref at), (ulong)Long(bytes, ref at));
            case GodotType.Dictionary:
                ContainerType(bytes, ref at, (header >> 16) & 3);
                ContainerType(bytes, ref at, (header >> 18) & 3);
                return Dictionary(bytes, ref at, depth);
            case GodotType.Array:
                ContainerType(bytes, ref at, (header >> 16) & 3);
                return Array(bytes, ref at, depth);
            case GodotType.PackedByteArray:
                return PackedBytes(bytes, ref at);
            case GodotType.PackedInt32Array:
                return Packed(bytes, ref at, 4, (b, a) => (int)UInt(b, ref a));
            case GodotType.PackedInt64Array:
                return Packed(bytes, ref at, 8, (b, a) => Long(b, ref a));
            case GodotType.PackedFloat32Array:
                return Packed(bytes, ref at, 4, (b, a) => (float)Single(b, ref a));
            case GodotType.PackedFloat64Array:
                return Packed(bytes, ref at, 8, (b, a) => Double(b, ref a));
            case GodotType.PackedStringArray:
                return PackedStrings(bytes, ref at);
            case GodotType.PackedVector2Array or GodotType.PackedVector3Array or GodotType.PackedVector4Array or GodotType.PackedColorArray:
                return PackedMath(bytes, ref at, type, wide);
            default:
                throw new GodotVariantException($"Type {(int)type} is no Variant type");
        }
    }

    /// <summary>The bytes of a value: null, bool, a whole number, a real one, a string, a name, a node path, a math value,
    /// an object's id, a list or a string array.</summary>
    public static byte[] Write(object? value)
    {
        List<byte> bytes = [];
        Write(bytes, value, 0);
        return [.. bytes];
    }

    private static void Write(List<byte> bytes, object? value, int depth)
    {
        if (depth > Deepest)
        {
            throw new GodotVariantException("The value is nested too deep");
        }
        switch (value)
        {
            case null:
                Put(bytes, (uint)GodotType.Nil);
                break;
            case bool flag:
                Put(bytes, (uint)GodotType.Bool);
                Put(bytes, flag ? 1u : 0u);
                break;
            case int or long or uint or short or byte:
                long whole = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                if (whole is > int.MaxValue or < int.MinValue)
                {
                    Put(bytes, (uint)GodotType.Int | Wide);
                    PutLong(bytes, whole);
                }
                else
                {
                    Put(bytes, (uint)GodotType.Int);
                    Put(bytes, (uint)(int)whole);
                }
                break;
            case float or double:
                double real = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if ((double)(float)real == real)
                {
                    Put(bytes, (uint)GodotType.Float);
                    Put(bytes, BitConverter.SingleToUInt32Bits((float)real));
                }
                else
                {
                    Put(bytes, (uint)GodotType.Float | Wide);
                    PutLong(bytes, BitConverter.DoubleToInt64Bits(real));
                }
                break;
            case string text:
                Put(bytes, (uint)GodotType.String);
                PutText(bytes, text);
                break;
            case GodotName name:
                Put(bytes, (uint)GodotType.StringName);
                PutText(bytes, name.Name);
                break;
            case GodotNodePath path:
                Put(bytes, (uint)GodotType.NodePath);
                Put(bytes, (uint)path.Names.Count | 0x80000000);
                Put(bytes, (uint)path.Subnames.Count);
                Put(bytes, path.Absolute ? 1u : 0u);
                foreach (string part in path.Names.Concat(path.Subnames))
                {
                    PutText(bytes, part);
                }
                break;
            case GodotMath math:
                WriteMath(bytes, math);
                break;
            case GodotObjectId id:
                Put(bytes, (uint)GodotType.Object | ObjectAsId);
                PutLong(bytes, (long)id.Id);
                break;
            case string[] strings:
                Put(bytes, (uint)GodotType.PackedStringArray);
                Put(bytes, (uint)strings.Length);
                foreach (string text in strings)
                {
                    PutText(bytes, text + "\0");
                }
                break;
            case IEnumerable<object?> items:
                List<object?> all = [.. items];
                Put(bytes, (uint)GodotType.Array);
                Put(bytes, (uint)all.Count);
                foreach (object? item in all)
                {
                    Write(bytes, item, depth + 1);
                }
                break;
            default:
                throw new GodotVariantException($"A {value.GetType().Name} has no Variant to write");
        }
    }

    private static void WriteMath(List<byte> bytes, GodotMath math)
    {
        bool whole = math.Type is GodotType.Vector2I or GodotType.Rect2I or GodotType.Vector3I or GodotType.Vector4I;
        bool single = whole || math.Type == GodotType.Color || math.Values.All(v => (double)(float)v == v);
        Put(bytes, (uint)math.Type | (single ? 0u : Wide));
        foreach (double value in math.Values)
        {
            if (whole)
            {
                Put(bytes, (uint)(int)value);
            }
            else if (single)
            {
                Put(bytes, BitConverter.SingleToUInt32Bits((float)value));
            }
            else
            {
                PutLong(bytes, BitConverter.DoubleToInt64Bits(value));
            }
        }
    }

    /// <summary>How many numbers a math type holds.</summary>
    internal static int Count(GodotType type) => type switch
    {
        GodotType.Vector2 or GodotType.Vector2I => 2,
        GodotType.Vector3 or GodotType.Vector3I => 3,
        GodotType.Rect2 or GodotType.Rect2I or GodotType.Vector4 or GodotType.Vector4I or GodotType.Plane or GodotType.Quaternion or GodotType.Color => 4,
        GodotType.Transform2D or GodotType.Aabb => 6,
        GodotType.Basis => 9,
        GodotType.Transform3D => 12,
        GodotType.Projection => 16,
        _ => 0,
    };

    private static GodotMath Real(ReadOnlySpan<byte> bytes, ref int at, GodotType type, int count, bool wide)
    {
        double[] values = new double[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = wide ? Double(bytes, ref at) : Single(bytes, ref at);
        }
        return new GodotMath(type, values);
    }

    private static GodotNodePath Path(ReadOnlySpan<byte> bytes, ref int at)
    {
        uint first = UInt(bytes, ref at);
        if ((first & 0x80000000) == 0)
        {
            // The old form: a string alone, its length the word just read.
            at -= 4;
            string text = Text(bytes, ref at);
            return new GodotNodePath([.. text.Split('/', StringSplitOptions.RemoveEmptyEntries)], [], text.StartsWith('/'));
        }
        int names = (int)(first & 0x7FFFFFFF);
        int subnames = (int)UInt(bytes, ref at);
        uint flags = UInt(bytes, ref at);
        if ((flags & 2) != 0)
        {
            subnames++;
        }
        if (names < 0 || subnames < 0 || (long)(names + subnames) * 4 > bytes.Length - at)
        {
            throw new GodotVariantException("A node path names more parts than its bytes hold");
        }
        List<string> parts = [];
        for (int i = 0; i < names + subnames; i++)
        {
            parts.Add(Text(bytes, ref at));
        }
        return new GodotNodePath(parts[..names], parts[names..], (flags & 1) != 0);
    }

    private static object? Object(ReadOnlySpan<byte> bytes, ref int at, bool asId, int depth)
    {
        if (asId)
        {
            ulong id = (ulong)Long(bytes, ref at);
            return id == 0 ? null : new GodotObjectId(id);
        }
        string type = Text(bytes, ref at);
        if (type.Length == 0)
        {
            return null;
        }
        int count = Length(bytes, ref at, 8);
        List<KeyValuePair<string, object?>> properties = [];
        for (int i = 0; i < count; i++)
        {
            string name = Text(bytes, ref at);
            properties.Add(new(name, Read(bytes, ref at, depth + 1)));
        }
        return new GodotObject(type, properties);
    }

    /// <summary>Passes over a typed container's element type: a builtin type's number, or a class's or script's name.</summary>
    private static void ContainerType(ReadOnlySpan<byte> bytes, ref int at, uint kind)
    {
        if (kind == 1)
        {
            UInt(bytes, ref at);
        }
        else if (kind is 2 or 3)
        {
            Text(bytes, ref at);
        }
    }

    private static GodotDictionary Dictionary(ReadOnlySpan<byte> bytes, ref int at, int depth)
    {
        int count = (int)(UInt(bytes, ref at) & 0x7FFFFFFF);
        if ((long)count * 8 > bytes.Length - at)
        {
            throw new GodotVariantException("A dictionary counts more entries than its bytes hold");
        }
        List<KeyValuePair<object?, object?>> entries = [];
        for (int i = 0; i < count; i++)
        {
            object? key = Read(bytes, ref at, depth + 1);
            entries.Add(new(key, Read(bytes, ref at, depth + 1)));
        }
        return new GodotDictionary(entries);
    }

    private static List<object?> Array(ReadOnlySpan<byte> bytes, ref int at, int depth)
    {
        int count = (int)(UInt(bytes, ref at) & 0x7FFFFFFF);
        if ((long)count * 4 > bytes.Length - at)
        {
            throw new GodotVariantException("An array counts more items than its bytes hold");
        }
        List<object?> items = new(count);
        for (int i = 0; i < count; i++)
        {
            items.Add(Read(bytes, ref at, depth + 1));
        }
        return items;
    }

    private static byte[] PackedBytes(ReadOnlySpan<byte> bytes, ref int at)
    {
        int count = Length(bytes, ref at, 1);
        byte[] read = bytes.Slice(at, count).ToArray();
        at += count + Pad(count);
        return read;
    }

    private delegate T Item<T>(ReadOnlySpan<byte> bytes, int at);

    private static T[] Packed<T>(ReadOnlySpan<byte> bytes, ref int at, int size, Item<T> item)
    {
        int count = Length(bytes, ref at, size);
        T[] read = new T[count];
        for (int i = 0; i < count; i++)
        {
            read[i] = item(bytes, at + (i * size));
        }
        at += count * size;
        return read;
    }

    private static string[] PackedStrings(ReadOnlySpan<byte> bytes, ref int at)
    {
        int count = Length(bytes, ref at, 4);
        string[] read = new string[count];
        for (int i = 0; i < count; i++)
        {
            // Godot writes each with its terminating zero.
            read[i] = Text(bytes, ref at).TrimEnd('\0');
        }
        return read;
    }

    private static GodotMath[] PackedMath(ReadOnlySpan<byte> bytes, ref int at, GodotType type, bool wide)
    {
        (GodotType each, int numbers) = type switch
        {
            GodotType.PackedVector2Array => (GodotType.Vector2, 2),
            GodotType.PackedVector3Array => (GodotType.Vector3, 3),
            GodotType.PackedVector4Array => (GodotType.Vector4, 4),
            _ => (GodotType.Color, 4),
        };
        bool doubles = wide && type != GodotType.PackedColorArray;
        int count = Length(bytes, ref at, numbers * (doubles ? 8 : 4));
        GodotMath[] read = new GodotMath[count];
        for (int i = 0; i < count; i++)
        {
            read[i] = Real(bytes, ref at, each, numbers, doubles);
        }
        return read;
    }

    /// <summary>A count read, checked against the bytes left for items of the size given.</summary>
    private static int Length(ReadOnlySpan<byte> bytes, ref int at, int size)
    {
        int count = (int)UInt(bytes, ref at);
        return count < 0 || (long)count * size > bytes.Length - at ? throw new GodotVariantException("A count is more than the bytes hold") : count;
    }

    private static string Text(ReadOnlySpan<byte> bytes, ref int at)
    {
        int length = Length(bytes, ref at, 1);
        string text = Encoding.UTF8.GetString(bytes.Slice(at, length));
        at += length + Pad(length);
        if (at > bytes.Length)
        {
            throw new GodotVariantException("A string's padding runs past the bytes");
        }
        return text;
    }

    private static int Pad(int length) => (4 - (length % 4)) % 4;

    private static uint UInt(ReadOnlySpan<byte> bytes, ref int at)
    {
        if (at + 4 > bytes.Length)
        {
            throw new GodotVariantException("The value runs past the bytes");
        }
        uint read = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
        at += 4;
        return read;
    }

    private static long Long(ReadOnlySpan<byte> bytes, ref int at)
    {
        if (at + 8 > bytes.Length)
        {
            throw new GodotVariantException("The value runs past the bytes");
        }
        long read = BinaryPrimitives.ReadInt64LittleEndian(bytes[at..]);
        at += 8;
        return read;
    }

    private static double Single(ReadOnlySpan<byte> bytes, ref int at) => BitConverter.UInt32BitsToSingle(UInt(bytes, ref at));

    private static double Double(ReadOnlySpan<byte> bytes, ref int at) => BitConverter.Int64BitsToDouble(Long(bytes, ref at));

    private static void Put(List<byte> bytes, uint value)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(four, value);
        foreach (byte b in four)
        {
            bytes.Add(b);
        }
    }

    private static void PutLong(List<byte> bytes, long value)
    {
        Span<byte> eight = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(eight, value);
        foreach (byte b in eight)
        {
            bytes.Add(b);
        }
    }

    private static void PutText(List<byte> bytes, string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        Put(bytes, (uint)utf8.Length);
        bytes.AddRange(utf8);
        for (int i = 0; i < Pad(utf8.Length); i++)
        {
            bytes.Add(0);
        }
    }

    /// <summary>A value as Godot's inspector writes it: a number in its shortest form, a vector's numbers in parentheses, a
    /// node path as a path, a string as itself (in quotes inside a container, <paramref name="quoted"/>).</summary>
    public static string Shown(object? value, bool quoted = false) => value switch
    {
        null => "null",
        bool flag => flag ? "true" : "false",
        long whole => whole.ToString(CultureInfo.InvariantCulture),
        double real => Number(real),
        string text => quoted ? $"\"{text}\"" : text,
        GodotName name => quoted ? $"&\"{name.Name}\"" : name.Name,
        GodotNodePath path => quoted ? $"^\"{path}\"" : path.ToString(),
        List<object?> items => $"[{string.Join(", ", items.Select(i => Shown(i, quoted: true)))}]",
        string[] strings => $"[{string.Join(", ", strings.Select(s => $"\"{s}\""))}]",
        byte[] packed => $"[{packed.Length} bytes]",
        System.Array packed => $"[{string.Join(", ", packed.Cast<object?>().Select(i => i is float f ? Number(f) : Shown(i, quoted: true)))}]",
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>The value of a type a text gives, as a person types it: true or false, a number, the text itself for a string
    /// or a name, a path for a node path, and a vector's or colour's numbers with or without their parentheses, separated by
    /// commas; null with why not for text the type does not read or a type text cannot give.</summary>
    public static (object? Value, string? WhyNot) Parse(GodotType type, string text)
    {
        string trimmed = text.Trim();
        switch (type)
        {
            case GodotType.String:
                return (text, null);
            case GodotType.StringName:
                return (new GodotName(text), null);
            case GodotType.NodePath:
                return (new GodotNodePath([.. trimmed.Split(':')[0].Split('/', StringSplitOptions.RemoveEmptyEntries)], [.. trimmed.Split(':').Skip(1)],
                    trimmed.StartsWith('/')), null);
            case GodotType.Bool:
                return bool.TryParse(trimmed, out bool flag) ? (flag, null) : (null, "true or false");
            case GodotType.Int:
                return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole) ? (whole, null) : (null, "a whole number");
            case GodotType.Float:
                return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) ? (real, null) : (null, "a number");
        }
        int count = Count(type);
        if (count == 0 || type is GodotType.Transform2D or GodotType.Aabb or GodotType.Basis or GodotType.Transform3D or GodotType.Projection
            or GodotType.Plane or GodotType.Quaternion)
        {
            return (null, $"a {type} is not set from text");
        }
        string[] parts = trimmed.TrimStart('(').TrimEnd(')').Split(',', StringSplitOptions.TrimEntries);
        bool integers = type is GodotType.Vector2I or GodotType.Rect2I or GodotType.Vector3I or GodotType.Vector4I;
        List<double> numbers = [];
        foreach (string part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || (integers && number != Math.Floor(number)))
            {
                return (null, $"{count} {(integers ? "whole numbers" : "numbers")} separated by commas");
            }
            numbers.Add(number);
        }
        // A colour may leave out its alpha.
        if (type == GodotType.Color && numbers.Count == 3)
        {
            numbers.Add(1);
        }
        return numbers.Count == count ? (new GodotMath(type, numbers), null) : (null, $"{count} {(integers ? "whole numbers" : "numbers")} separated by commas");
    }

    /// <summary>A real number as Godot writes it: whole ones without a fraction, the rest in their shortest round-trip form.</summary>
    public static string Number(double value) => value.ToString(value == Math.Floor(value) && Math.Abs(value) < 1e15 ? "0" : "R", CultureInfo.InvariantCulture);
}
