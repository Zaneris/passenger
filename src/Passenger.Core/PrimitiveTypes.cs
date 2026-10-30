using System.Reflection.Metadata;

namespace Passenger.Core;

/// <summary>The types metadata or C# names as its own, one row each: the code a signature gives it (none for decimal, which
/// only C# names), C#'s keyword for it (none for TypedReference) and the type itself, whose name in System the row carries.
/// Linked into the inspector, inside an app that never loads Passenger's assemblies.</summary>
public static class PrimitiveTypes
{
    public static IReadOnlyList<PrimitiveType> All { get; } =
    [
        new(PrimitiveTypeCode.Boolean, "bool", typeof(bool)),
        new(PrimitiveTypeCode.Char, "char", typeof(char)),
        new(PrimitiveTypeCode.SByte, "sbyte", typeof(sbyte)),
        new(PrimitiveTypeCode.Byte, "byte", typeof(byte)),
        new(PrimitiveTypeCode.Int16, "short", typeof(short)),
        new(PrimitiveTypeCode.UInt16, "ushort", typeof(ushort)),
        new(PrimitiveTypeCode.Int32, "int", typeof(int)),
        new(PrimitiveTypeCode.UInt32, "uint", typeof(uint)),
        new(PrimitiveTypeCode.Int64, "long", typeof(long)),
        new(PrimitiveTypeCode.UInt64, "ulong", typeof(ulong)),
        new(PrimitiveTypeCode.Single, "float", typeof(float)),
        new(PrimitiveTypeCode.Double, "double", typeof(double)),
        new(PrimitiveTypeCode.IntPtr, "nint", typeof(nint)),
        new(PrimitiveTypeCode.UIntPtr, "nuint", typeof(nuint)),
        new(null, "decimal", typeof(decimal)),
        new(PrimitiveTypeCode.String, "string", typeof(string)),
        new(PrimitiveTypeCode.Object, "object", typeof(object)),
        new(PrimitiveTypeCode.Void, "void", typeof(void)),
        new(PrimitiveTypeCode.TypedReference, null, typeof(TypedReference)),
    ];

    private static readonly Dictionary<PrimitiveTypeCode, PrimitiveType> ByCode = All.Where(row => row.Code is not null).ToDictionary(row => row.Code!.Value);
    private static readonly Dictionary<string, PrimitiveType> ByKeyword = All.Where(row => row.Keyword is not null).ToDictionary(row => row.Keyword!, StringComparer.Ordinal);
    private static readonly Dictionary<string, PrimitiveType> BySystemName = All.ToDictionary(row => row.SystemName, StringComparer.Ordinal);
    private static readonly Dictionary<Type, PrimitiveType> ByType = All.ToDictionary(row => row.Type);

    /// <summary>The row of the primitive a signature's code names; every code has one.</summary>
    public static PrimitiveType Of(PrimitiveTypeCode code) =>
        ByCode.TryGetValue(code, out PrimitiveType? row) ? row : throw new ArgumentOutOfRangeException(nameof(code), code, "No primitive has this code");

    /// <summary>The row of a type, null for one not in the table (an enum is not its underlying type's).</summary>
    public static PrimitiveType? Of(Type type) => ByType.GetValueOrDefault(type);

    /// <summary>The row C#'s keyword names ("int"), null for any other word.</summary>
    public static PrimitiveType? OfKeyword(string keyword) => ByKeyword.GetValueOrDefault(keyword);

    /// <summary>The row a full name in System names ("System.Int32"), null for any other name.</summary>
    public static PrimitiveType? Named(string systemName) => BySystemName.GetValueOrDefault(systemName);
}

/// <summary>A row of <see cref="PrimitiveTypes"/>.</summary>
public sealed record PrimitiveType(PrimitiveTypeCode? Code, string? Keyword, Type Type)
{
    /// <summary>Its full name ("System.Int32").</summary>
    public string SystemName { get; } = Type.FullName!;

    /// <summary>Its name within System ("Int32").</summary>
    public string Name { get; } = Type.Name;

    /// <summary>The name C# writes it by: its keyword, else its full name.</summary>
    public string CSharpName => Keyword ?? SystemName;

    /// <summary>Whether the runtime holds a value of it as an element type of its own, its box being of its class: bool,
    /// char, the integers, the floating-point numbers, nint and nuint.</summary>
    public bool IsPrimitiveValue { get; } = Type.IsPrimitive;
}
