using System.Reflection;
using System.Runtime.CompilerServices;

namespace Passenger.Tests;

/// <summary>The bytes an object keeps, counted by walking what it references through fields and elements, never into what
/// something else holds anyway (as the test says, and the runtime's own reflection objects). It reads no collector's total,
/// so what the rest of the process lets go of meanwhile, a class run beside the test, counts for nothing.</summary>
internal static class HeldBytes
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<Type, (FieldInfo[] Referencing, long Size)> Types = [];

    /// <summary>The bytes <paramref name="root"/> reaches but through what <paramref name="elsewhere"/> says is held
    /// anyway, with the three types holding the most of them.</summary>
    public static (long Bytes, string Most) Of(object root, Func<object, bool> elsewhere)
    {
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance) { root };
        Stack<object> next = new();
        Dictionary<Type, long> byType = [];
        next.Push(root);
        void Reach(object? value)
        {
            if (value is not null && value is not (MemberInfo or Module or Assembly) && !elsewhere(value) && seen.Add(value))
            {
                next.Push(value);
            }
        }
        void Inside(object value)
        {
            foreach (FieldInfo field in Of(value.GetType()).Referencing)
            {
                object? held = field.GetValue(value);
                if (!field.FieldType.IsValueType)
                {
                    Reach(held);
                }
                else if (held is not null)
                {
                    Inside(held);
                }
            }
        }
        while (next.TryPop(out object? item))
        {
            Type type = item.GetType();
            byType[type] = byType.GetValueOrDefault(type) + SizeOf(item, type);
            if (item is Array array)
            {
                Type element = type.GetElementType()!;
                if (!element.IsValueType || Of(element).Referencing.Length > 0)
                {
                    foreach (object? value in array)
                    {
                        if (element.IsValueType)
                        {
                            Inside(value!);
                        }
                        else
                        {
                            Reach(value);
                        }
                    }
                }
            }
            else if (item is not string)
            {
                Inside(item);
            }
        }
        string most = string.Join(", ", byType.OrderByDescending(t => t.Value).Take(3).Select(t => $"{t.Key} ({t.Value:N0} bytes)"));
        return (byType.Values.Sum(), most);
    }

    /// <summary>A type's fields that hold a reference, or a value holding one, its base types' too; and the size of an
    /// instance of it on the heap.</summary>
    private static (FieldInfo[] Referencing, long Size) Of(Type type)
    {
        lock (Types)
        {
            if (Types.TryGetValue(type, out (FieldInfo[] Referencing, long Size) known))
            {
                return known;
            }
        }
        List<FieldInfo> referencing = [];
        long fields = 0;
        // A primitive's one field is of its own type.
        for (Type? at = type; at is not null && !at.IsPrimitive && !at.IsEnum; at = at.BaseType)
        {
            foreach (FieldInfo field in at.GetFields(Declared))
            {
                Type held = field.FieldType;
                fields += held.IsValueType ? RuntimeHelpers.SizeOf(held.TypeHandle) : IntPtr.Size;
                if (!held.IsPointer && !held.IsFunctionPointer && (!held.IsValueType || Of(held).Referencing.Length > 0))
                {
                    referencing.Add(field);
                }
            }
        }
        (FieldInfo[] Referencing, long Size) found = ([.. referencing], Math.Max(3 * IntPtr.Size, Aligned(2 * IntPtr.Size + fields)));
        lock (Types)
        {
            Types[type] = found;
        }
        return found;
    }

    /// <summary>An object's size on the heap: a string's or an array's by its length.</summary>
    private static long SizeOf(object item, Type type)
    {
        long header = 2 * IntPtr.Size;
        if (item is string text)
        {
            return Aligned(header + sizeof(int) + 2L * (text.Length + 1));
        }
        if (item is Array array)
        {
            Type element = type.GetElementType()!;
            long each = element.IsValueType ? RuntimeHelpers.SizeOf(element.TypeHandle) : IntPtr.Size;
            return Aligned(header + IntPtr.Size + each * array.LongLength);
        }
        return Of(type).Size;
    }

    private static long Aligned(long size) => (size + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
}
