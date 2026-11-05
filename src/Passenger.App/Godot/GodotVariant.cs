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

/// <summary>A packed array of vectors or colours, by its own type (<c>PackedVector3Array</c>), which it keeps where it holds
/// no item to tell it by.</summary>
public sealed record GodotPackedMath(GodotType Type, IReadOnlyList<GodotMath> Items)
{
    public override string ToString() => $"[{string.Join(", ", Items)}]";

    public bool Equals(GodotPackedMath? other) => other is not null && Type == other.Type && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(Type, Items.Count);
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

/// <summary>What a typed container holds, as its encoding names it: nothing (Kind 0, any value), a built-in type (1), a
/// class by its name (2), or a script by its path (3).</summary>
public readonly record struct GodotElement(int Kind, GodotType Type, string Name)
{
    public static GodotElement Any => default;

    public static GodotElement Of(GodotType type) => type == GodotType.Nil ? default : new GodotElement(1, type, string.Empty);
}

/// <summary>An Array as read, its items in order, and what it is typed to hold (<see cref="GodotElement.Any"/> for an
/// untyped one), which a value sent back keeps: a typed member refuses an untyped array.</summary>
public sealed class GodotArray(IEnumerable<object?> items) : List<object?>(items)
{
    public GodotElement Element { get; init; }
}

/// <summary>A Dictionary, its entries in order, and what its keys and values are typed to hold.</summary>
public sealed record GodotDictionary(IReadOnlyList<KeyValuePair<object?, object?>> Entries)
{
    public GodotElement KeyType { get; init; }

    public GodotElement ValueType { get; init; }

    public override string ToString() => Entries.Count == 0 ? "{}"
        : $"{{ {string.Join(", ", Entries.Select(e => $"{GodotVariant.Shown(e.Key, quoted: true)}: {GodotVariant.Shown(e.Value, quoted: true)}"))} }}";
}

/// <summary>Why bytes are no Variant Godot wrote.</summary>
public sealed class GodotVariantException(string message) : Exception(message);

/// <summary>Godot's binary Variant encoding (its marshalls), read and written: a header of four bytes (the type in its low
/// byte, flags above: 64-bit numbers, an object sent as its id, a typed container's element type) and then the value, little
/// endian, a string as its UTF-8 length and bytes padded to four. Read into plain values: null, bool, long, double, string,
/// <see cref="GodotName"/>, <see cref="GodotNodePath"/>, <see cref="GodotMath"/>, lists for arrays, arrays for packed ones
/// (<see cref="GodotPackedMath"/> for vectors' and colours'), and the records here for the rest. Written for what a debugger sends: those plain values, lists, dictionaries and numbers.</summary>
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
                GodotElement keys = ContainerType(bytes, ref at, (header >> 16) & 3);
                GodotElement values = ContainerType(bytes, ref at, (header >> 18) & 3);
                return Dictionary(bytes, ref at, depth) with { KeyType = keys, ValueType = values };
            case GodotType.Array:
                GodotElement items = ContainerType(bytes, ref at, (header >> 16) & 3);
                return new GodotArray(Array(bytes, ref at, depth)) { Element = items };
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
    /// an object's id, a dictionary, a list or a packed array.</summary>
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
            case byte[] packed:
                Put(bytes, (uint)GodotType.PackedByteArray);
                Put(bytes, (uint)packed.Length);
                bytes.AddRange(packed);
                for (int i = 0; i < Pad(packed.Length); i++)
                {
                    bytes.Add(0);
                }
                break;
            case int[] packed:
                Put(bytes, (uint)GodotType.PackedInt32Array);
                Put(bytes, (uint)packed.Length);
                foreach (int item in packed)
                {
                    Put(bytes, (uint)item);
                }
                break;
            case long[] packed:
                Put(bytes, (uint)GodotType.PackedInt64Array);
                Put(bytes, (uint)packed.Length);
                foreach (long item in packed)
                {
                    PutLong(bytes, item);
                }
                break;
            case float[] packed:
                Put(bytes, (uint)GodotType.PackedFloat32Array);
                Put(bytes, (uint)packed.Length);
                foreach (float item in packed)
                {
                    Put(bytes, BitConverter.SingleToUInt32Bits(item));
                }
                break;
            case double[] packed:
                Put(bytes, (uint)GodotType.PackedFloat64Array);
                Put(bytes, (uint)packed.Length);
                foreach (double item in packed)
                {
                    PutLong(bytes, BitConverter.DoubleToInt64Bits(item));
                }
                break;
            case GodotPackedMath packed:
                WritePacked(bytes, packed);
                break;
            case GodotDictionary dictionary:
                Put(bytes, (uint)GodotType.Dictionary | (uint)(dictionary.KeyType.Kind << 16) | (uint)(dictionary.ValueType.Kind << 18));
                PutContainerType(bytes, dictionary.KeyType);
                PutContainerType(bytes, dictionary.ValueType);
                Put(bytes, (uint)dictionary.Entries.Count);
                foreach ((object? key, object? entry) in dictionary.Entries)
                {
                    Write(bytes, key, depth + 1);
                    Write(bytes, entry, depth + 1);
                }
                break;
            case IEnumerable<object?> items:
                List<object?> all = [.. items];
                GodotElement typed = items is GodotArray array ? array.Element : default;
                Put(bytes, (uint)GodotType.Array | (uint)(typed.Kind << 16));
                PutContainerType(bytes, typed);
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

    /// <summary>Vectors or colours as the packed array of their type, in single precision as Godot keeps them.</summary>
    private static void WritePacked(List<byte> bytes, GodotPackedMath packed)
    {
        GodotType each = ElementOf(packed.Type);
        Put(bytes, (uint)packed.Type);
        Put(bytes, (uint)packed.Items.Count);
        foreach (GodotMath item in packed.Items)
        {
            if (item.Type != each)
            {
                throw new GodotVariantException($"A packed array of {each} cannot hold a {item.Type}");
            }
            foreach (double value in item.Values)
            {
                Put(bytes, BitConverter.SingleToUInt32Bits((float)value));
            }
        }
    }

    /// <summary>A Variant type's name as Godot writes it ("int", "Vector2i", "AABB"); "Variant" for Nil, any value.</summary>
    public static string TypeName(GodotType type) => type switch
    {
        GodotType.Nil => "Variant",
        GodotType.Bool => "bool",
        GodotType.Int => "int",
        GodotType.Float => "float",
        GodotType.Vector2I => "Vector2i",
        GodotType.Vector3I => "Vector3i",
        GodotType.Vector4I => "Vector4i",
        GodotType.Rect2I => "Rect2i",
        GodotType.Aabb => "AABB",
        GodotType.Rid => "RID",
        _ => type.ToString(),
    };

    /// <summary>The Variant type of a value as read (<see cref="Read(ReadOnlySpan{byte})"/>).</summary>
    public static GodotType TypeOf(object? value) => value switch
    {
        null => GodotType.Nil,
        bool => GodotType.Bool,
        long or int => GodotType.Int,
        double or float => GodotType.Float,
        string => GodotType.String,
        GodotName => GodotType.StringName,
        GodotNodePath => GodotType.NodePath,
        GodotMath math => math.Type,
        GodotRid => GodotType.Rid,
        GodotObjectId or GodotObject => GodotType.Object,
        GodotCallable => GodotType.Callable,
        GodotSignal => GodotType.Signal,
        GodotDictionary => GodotType.Dictionary,
        byte[] => GodotType.PackedByteArray,
        int[] => GodotType.PackedInt32Array,
        long[] => GodotType.PackedInt64Array,
        float[] => GodotType.PackedFloat32Array,
        double[] => GodotType.PackedFloat64Array,
        string[] => GodotType.PackedStringArray,
        GodotPackedMath packed => packed.Type,
        _ => GodotType.Array,
    };

    /// <summary>A new value of a type, as Godot makes one: zero, false, empty, a colour opaque black; null for a type with no
    /// value a person types.</summary>
    public static object? Default(GodotType type) => type switch
    {
        GodotType.Bool => false,
        GodotType.Int => 0L,
        GodotType.Float => 0.0,
        GodotType.String => string.Empty,
        GodotType.StringName => new GodotName(string.Empty),
        GodotType.NodePath => new GodotNodePath([], [], false),
        GodotType.Color => new GodotMath(type, [0, 0, 0, 1]),
        GodotType.Quaternion => new GodotMath(type, [0, 0, 0, 1]),
        GodotType.Basis => new GodotMath(type, [1, 0, 0, 0, 1, 0, 0, 0, 1]),
        GodotType.Transform2D => new GodotMath(type, [1, 0, 0, 1, 0, 0]),
        GodotType.Transform3D => new GodotMath(type, [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0]),
        GodotType.Projection => new GodotMath(type, [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]),
        _ when Count(type) > 0 => new GodotMath(type, new double[Count(type)]),
        GodotType.Array => new GodotArray([]),
        GodotType.Dictionary => new GodotDictionary([]),
        >= GodotType.PackedByteArray => Packed(type, []),
        _ => null,
    };

    /// <summary>A new key of a dictionary typed <paramref name="type"/> that none of <paramref name="keys"/> is: false then
    /// true, the next whole number, the next whole real, "key" and "key 2" on as a string, a name or a node path, a math
    /// value's default with its first number moved on, an empty container. False where the type has none left, or none
    /// made without a person (an object).</summary>
    public static bool TryNewKey(GodotType type, IReadOnlyList<object?> keys, out object? key)
    {
        foreach (object? offered in KeysOf(type, keys))
        {
            if (!keys.Any(k => Same(k, offered)))
            {
                key = offered;
                return true;
            }
        }
        key = null;
        return false;
    }

    /// <summary>The keys of a type to offer, in order: endless where the type has no end of them.</summary>
    private static IEnumerable<object?> KeysOf(GodotType type, IReadOnlyList<object?> keys)
    {
        switch (type)
        {
            case GodotType.Bool:
                yield return false;
                yield return true;
                break;
            case GodotType.Int:
                for (long next = keys.OfType<long>().DefaultIfEmpty(-1).Max() + 1; ; next++)
                {
                    yield return next;
                }
            case GodotType.Float:
                for (double next = keys.OfType<double>().Where(double.IsFinite).Select(Math.Floor).DefaultIfEmpty(-1).Max() + 1; ; next++)
                {
                    yield return next;
                }
            case GodotType.Nil or GodotType.String or GodotType.StringName or GodotType.NodePath:
                for (int n = 1; ; n++)
                {
                    string text = n == 1 ? "key" : $"key {n}";
                    yield return type switch
                    {
                        GodotType.StringName => new GodotName(text),
                        GodotType.NodePath => new GodotNodePath([text], [], false),
                        _ => text,
                    };
                }
            case GodotType.Dictionary or GodotType.Array or >= GodotType.PackedByteArray:
                yield return Default(type);
                break;
            case var math when Default(math) is GodotMath start:
                for (int n = 0; ; n++)
                {
                    yield return new GodotMath(math, [start.Values[0] + n, .. start.Values.Skip(1)]);
                }
        }
    }

    /// <summary>What a packed array holds each of: an int's, a float's, a string's, a vector's or a colour's type; Nil for any
    /// other type.</summary>
    public static GodotType ElementOf(GodotType packed) => packed switch
    {
        GodotType.PackedByteArray or GodotType.PackedInt32Array or GodotType.PackedInt64Array => GodotType.Int,
        GodotType.PackedFloat32Array or GodotType.PackedFloat64Array => GodotType.Float,
        GodotType.PackedStringArray => GodotType.String,
        GodotType.PackedVector2Array => GodotType.Vector2,
        GodotType.PackedVector3Array => GodotType.Vector3,
        GodotType.PackedVector4Array => GodotType.Vector4,
        GodotType.PackedColorArray => GodotType.Color,
        _ => GodotType.Nil,
    };

    /// <summary>A list's or packed array's items as values, in order; null for a value that holds no items.</summary>
    public static IReadOnlyList<object?>? Items(object? value) => value switch
    {
        List<object?> items => items,
        string[] strings => strings,
        GodotPackedMath packed => packed.Items,
        byte[] packed => [.. packed.Select(b => (object?)(long)b)],
        int[] packed => [.. packed.Select(i => (object?)(long)i)],
        long[] packed => [.. packed.Select(i => (object?)i)],
        float[] packed => [.. packed.Select(f => (object?)(double)f)],
        double[] packed => [.. packed.Select(d => (object?)d)],
        _ => null,
    };

    /// <summary>Items as a value of a container's type: an array typed as <paramref name="element"/> says for an Array, else
    /// the packed array of that type, each item as it takes them.</summary>
    public static object? Packed(GodotType type, IReadOnlyList<object?> items, GodotElement element = default) => type switch
    {
        GodotType.PackedByteArray => items.Select(i => (byte)Convert.ToInt64(i, CultureInfo.InvariantCulture)).ToArray(),
        GodotType.PackedInt32Array => items.Select(i => (int)Convert.ToInt64(i, CultureInfo.InvariantCulture)).ToArray(),
        GodotType.PackedInt64Array => items.Select(i => Convert.ToInt64(i, CultureInfo.InvariantCulture)).ToArray(),
        GodotType.PackedFloat32Array => items.Select(i => (float)Convert.ToDouble(i, CultureInfo.InvariantCulture)).ToArray(),
        GodotType.PackedFloat64Array => items.Select(i => Convert.ToDouble(i, CultureInfo.InvariantCulture)).ToArray(),
        GodotType.PackedStringArray => items.Select(i => i as string ?? string.Empty).ToArray(),
        GodotType.PackedVector2Array or GodotType.PackedVector3Array or GodotType.PackedVector4Array or GodotType.PackedColorArray => new GodotPackedMath(type, [.. items.OfType<GodotMath>()]),
        _ => new GodotArray(items) { Element = element },
    };

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

    /// <summary>A typed container's element type: a builtin type's number, or a class's or script's name.</summary>
    private static GodotElement ContainerType(ReadOnlySpan<byte> bytes, ref int at, uint kind) => kind switch
    {
        1 => new GodotElement(1, (GodotType)UInt(bytes, ref at), string.Empty),
        2 or 3 => new GodotElement((int)kind, GodotType.Object, Text(bytes, ref at)),
        _ => default,
    };

    /// <summary>Writes a typed container's element type as <see cref="ContainerType"/> reads it.</summary>
    private static void PutContainerType(List<byte> bytes, GodotElement element)
    {
        if (element.Kind == 1)
        {
            Put(bytes, (uint)element.Type);
        }
        else if (element.Kind is 2 or 3)
        {
            PutText(bytes, element.Name);
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

    private static GodotPackedMath PackedMath(ReadOnlySpan<byte> bytes, ref int at, GodotType type, bool wide)
    {
        GodotType each = ElementOf(type);
        int numbers = Count(each);
        bool doubles = wide && type != GodotType.PackedColorArray;
        int count = Length(bytes, ref at, numbers * (doubles ? 8 : 4));
        GodotMath[] read = new GodotMath[count];
        for (int i = 0; i < count; i++)
        {
            read[i] = Real(bytes, ref at, each, numbers, doubles);
        }
        return new GodotPackedMath(type, read);
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
    /// node path as a path, a string as itself (inside a container, <paramref name="quoted"/>, as the literal
    /// <see cref="Literal"/> reads back).</summary>
    public static string Shown(object? value, bool quoted = false) => value switch
    {
        null => "null",
        bool flag => flag ? "true" : "false",
        long whole => whole.ToString(CultureInfo.InvariantCulture),
        double real => Number(real),
        string text => quoted ? GodotText.Escaped(text) : text,
        GodotName name => quoted ? "&" + GodotText.Escaped(name.Name) : name.Name,
        GodotNodePath path => quoted ? "^" + GodotText.Escaped(path.ToString()) : path.ToString(),
        List<object?> items => $"[{string.Join(", ", items.Select(i => Shown(i, quoted: true)))}]",
        string[] strings => $"[{string.Join(", ", strings.Select(GodotText.Escaped))}]",
        byte[] packed => $"[{packed.Length} bytes]",
        System.Array packed => $"[{string.Join(", ", packed.Cast<object?>().Select(i => i is float f ? Number(f) : Shown(i, quoted: true)))}]",
        GodotMath { Type: GodotType.Transform2D or GodotType.Basis or GodotType.Transform3D or GodotType.Aabb or GodotType.Plane or GodotType.Projection } math => Parts(math),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Whether two values a game holds are alike, whole: a byte array by its bytes, an array's, a dictionary's and an
    /// object's parts each so, any other value as <see cref="Shown"/> writes it.</summary>
    public static bool Same(object? a, object? b) => (a, b) switch
    {
        (byte[] left, byte[] right) => left.AsSpan().SequenceEqual(right),
        (List<object?> left, List<object?> right) => left.Count == right.Count && left.Zip(right).All(p => Same(p.First, p.Second)),
        (GodotDictionary left, GodotDictionary right) => left.Entries.Count == right.Entries.Count
            && left.Entries.Zip(right.Entries).All(p => Same(p.First.Key, p.Second.Key) && Same(p.First.Value, p.Second.Value)),
        (GodotObject left, GodotObject right) => left.Class == right.Class && left.Properties.Count == right.Properties.Count
            && left.Properties.Zip(right.Properties).All(p => p.First.Key == p.Second.Key && Same(p.First.Value, p.Second.Value)),
        _ => Shown(a, quoted: true) == Shown(b, quoted: true),
    };

    /// <summary>A value of several vectors in its parts, as it is typed back: a 2D transform's x and y axes and origin, a
    /// basis's rows, a 3D transform's basis and origin, a box's position and size, a plane's normal and distance, a
    /// projection's columns.</summary>
    private static string Parts(GodotMath math)
    {
        string Of(int from, int count) => $"({string.Join(", ", math.Values.Skip(from).Take(count).Select(Number))})";
        return math.Type switch
        {
            GodotType.Transform2D => $"x: {Of(0, 2)}, y: {Of(2, 2)}, origin: {Of(4, 2)}",
            GodotType.Basis => $"({Of(0, 3)}, {Of(3, 3)}, {Of(6, 3)})",
            GodotType.Transform3D => $"basis: ({Of(0, 3)}, {Of(3, 3)}, {Of(6, 3)}), origin: {Of(9, 3)}",
            GodotType.Aabb => $"position: {Of(0, 3)}, size: {Of(3, 3)}",
            GodotType.Plane => $"normal: {Of(0, 3)}, d: {Number(math.Values[3])}",
            _ => $"({Of(0, 4)}, {Of(4, 4)}, {Of(8, 4)}, {Of(12, 4)})",
        };
    }

    /// <summary>The value of a type a text gives, as a person types it: true or false, a number, the text itself for a string
    /// or a name, a path for a node path, and the numbers of a vector, colour, rectangle, transform, basis, box, plane,
    /// quaternion or projection separated by commas, their parentheses and parts' names as <see cref="Shown"/> writes
    /// them or left out; null with why not for text the type does not read or a type text cannot give.</summary>
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
        if (count == 0)
        {
            return (null, $"a {type} is not set from text");
        }
        StringBuilder numbersOnly = new();
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (char.IsAsciiLetter(c) && c is not ('e' or 'E') || c == '_')
            {
                // A part's name ("origin:") is passed over up to its colon; an exponent's e stays with its number.
                int colon = trimmed.IndexOf(':', i);
                i = colon < 0 ? i : colon;
                continue;
            }
            if (c is not ('(' or ')'))
            {
                numbersOnly.Append(c);
            }
        }
        string[] parts = numbersOnly.ToString().Split(',', StringSplitOptions.TrimEntries);
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

    /// <summary>The value of a type as a scene or Godot's variant text writes it (<c>Vector2(10, 20)</c>, <c>"text"</c>,
    /// <c>&amp;"name"</c>, <c>NodePath("Hud")</c>, <c>2.5</c>, <c>null</c>); null with why not for text of a type it does not
    /// read (an array, a dictionary, a resource).</summary>
    public static (object? Value, string? WhyNot) OfScene(GodotType type, string text)
    {
        string trimmed = text.Trim();
        if (trimmed == "null" && type is GodotType.Nil or GodotType.Object)
        {
            return (null, null);
        }
        if (type is GodotType.String or GodotType.StringName or GodotType.NodePath)
        {
            string inner = type == GodotType.NodePath && trimmed.StartsWith("NodePath(", StringComparison.Ordinal) && trimmed.EndsWith(')') ? trimmed["NodePath(".Length..^1] : trimmed;
            return Literal(inner) is { } literal ? Parse(type, literal) : (null, "a quoted string");
        }
        if (Count(type) > 0)
        {
            int open = trimmed.IndexOf('(');
            return open > 0 && trimmed.EndsWith(')') && trimmed[..open] == TypeName(type) ? Parse(type, trimmed[(open + 1)..^1]) : (null, $"a {TypeName(type)}(…)");
        }
        return type is GodotType.Bool or GodotType.Int or GodotType.Float ? Parse(type, trimmed) : (null, $"a {TypeName(type)} is not read from text");
    }

    /// <summary>The value a typed text gives a property of <paramref name="type"/>, read one way wherever a Godot value is
    /// typed (the Live Visual Tree's properties, Variables' Set Value): as <see cref="Parse"/> reads it, a string's in quotes
    /// ("…", &amp;"…", ^"…") as the literal it is; where that reads nothing, as GDScript in the paused frame
    /// <paramref name="script"/> reads in ("speed * 2", "Vector2.ZERO", "null"), which a running game has none of. WhyNot
    /// says what the type takes, and what GDScript said, where neither reads it.</summary>
    public static async Task<(object? Value, string? WhyNot)> ReadAsync(GodotType type, string text, Func<string, Task<(object? Value, string? Error)>>? script)
    {
        (object? value, string? wanted) = type is GodotType.String or GodotType.StringName or GodotType.NodePath && Literal(text) is { } literal
            ? Parse(type, literal)
            : Parse(type, text);
        if (wanted is null || script is null)
        {
            return (value, wanted);
        }
        (object? read, string? error) = await script(text);
        return error is null ? (read, null) : (null, $"{wanted}, or GDScript that reads as one ({error})");
    }

    /// <summary>A value as typed text reads with no type to read it as: null, true or false, a whole or real number, a
    /// string's literal (a name's after &amp;, a node path's after ^), a vector's two to four numbers in parentheses; else
    /// the text as typed, a string.</summary>
    public static object? Guessed(string text)
    {
        string trimmed = text.Trim();
        if (trimmed == "null")
        {
            return null;
        }
        if (trimmed is "true" or "false")
        {
            return trimmed == "true";
        }
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
        {
            return whole;
        }
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
        {
            return real;
        }
        if (Literal(trimmed) is { } said)
        {
            return trimmed[0] switch
            {
                '&' => new GodotName(said),
                '^' => Parse(GodotType.NodePath, said).Value,
                _ => said,
            };
        }
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')') && trimmed[1..^1].Split(',', StringSplitOptions.TrimEntries) is { Length: >= 2 and <= 4 } parts)
        {
            GodotType vector = parts.Length switch { 2 => GodotType.Vector2, 3 => GodotType.Vector3, _ => GodotType.Vector4 };
            if (Parse(vector, trimmed) is ({ } read, null))
            {
                return read;
            }
        }
        return text;
    }

    /// <summary>What a GDScript string literal says (one in quotes, a name's after &amp;, a node path's after ^, its escapes
    /// read), or null for text that is no one literal.</summary>
    public static string? Literal(string text)
    {
        string trimmed = text.Trim();
        int open = trimmed.StartsWith("&\"", StringComparison.Ordinal) || trimmed.StartsWith("^\"", StringComparison.Ordinal) ? 1 : 0;
        if (trimmed.Length < open + 2 || trimmed[open] != '"' || trimmed[^1] != '"')
        {
            return null;
        }
        StringBuilder said = new();
        for (int i = open + 1; i < trimmed.Length - 1; i++)
        {
            char c = trimmed[i];
            if (c == '"')
            {
                return null;
            }
            if (c != '\\')
            {
                said.Append(c);
                continue;
            }
            if (++i >= trimmed.Length - 1)
            {
                return null;
            }
            said.Append(trimmed[i] switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                char other => other,
            });
        }
        return said.ToString();
    }

    /// <summary>A real number as Godot writes it: whole ones without a fraction, the rest in their shortest round-trip form.</summary>
    public static string Number(double value) => value.ToString(value == Math.Floor(value) && Math.Abs(value) < 1e15 ? "0" : "R", CultureInfo.InvariantCulture);
}
