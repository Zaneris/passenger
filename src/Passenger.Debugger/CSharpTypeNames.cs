using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using System.Text;

namespace Passenger.Debugger;

/// <summary>A signature's types as C# names them in an expression: a primitive or decimal by its keyword, a type by its
/// namespace and the types it is nested in joined by '.', each taking its own type arguments, a nullable as T?, and each
/// type parameter as <see cref="TypeParameterNames"/> names it. Null for a type C# cannot name there: a type parameter given
/// no name, a by-ref or a function pointer.</summary>
internal sealed class CSharpTypeNames(bool spelledNullable) : ISignatureTypeProvider<string?, TypeParameterNames>
{
    public static CSharpTypeNames Instance { get; } = new(spelledNullable: false);

    /// <summary>Names a nullable as System.Nullable&lt;T&gt;, which the expression compiler takes for a type parameter's, where it
    /// fails on T?.</summary>
    public static CSharpTypeNames NullableSpelled { get; } = new(spelledNullable: true);

    public string? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Void => "void",
        _ => MetadataTypeNames.Of(typeCode),
    };

    public string? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Keyword(MetadataTypeNames.Of(reader, handle, '.'));
    public string? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Keyword(MetadataTypeNames.Of(reader, handle, '.'));
    public string? GetTypeFromSpecification(MetadataReader reader, TypeParameterNames genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    /// <summary>A generic type closed: each type it is nested in, outermost first, taking as many arguments as its arity.</summary>
    public string? GetGenericInstantiation(string? genericType, ImmutableArray<string?> typeArguments)
    {
        if (genericType is null || typeArguments.Any(argument => argument is null))
        {
            return null;
        }
        if (genericType == "System.Nullable`1" && !spelledNullable)
        {
            return $"{typeArguments[0]}?";
        }
        StringBuilder named = new();
        int taken = 0;
        foreach (string part in genericType.Split('.'))
        {
            named.Append(named.Length == 0 ? "" : ".");
            int tick = part.IndexOf('`', StringComparison.Ordinal);
            if (tick < 0 || !int.TryParse(part.AsSpan(tick + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int arity) || taken + arity > typeArguments.Length)
            {
                named.Append(part);
                continue;
            }
            named.Append(part, 0, tick).Append('<').AppendJoin(", ", typeArguments.Skip(taken).Take(arity)).Append('>');
            taken += arity;
        }
        return taken == typeArguments.Length ? named.ToString() : null;
    }

    public string? GetSZArrayType(string? elementType) => elementType is null ? null : ArrayOf(elementType, "[]");
    public string? GetArrayType(string? elementType, ArrayShape shape) => elementType is null ? null : ArrayOf(elementType, $"[{new string(',', shape.Rank - 1)}]");
    public string? GetByReferenceType(string? elementType) => null;
    public string? GetPinnedType(string? elementType) => elementType;
    public string? GetModifiedType(string? modifier, string? unmodifiedType, bool isRequired) => unmodifiedType;
    public string? GetPointerType(string? elementType) => elementType is null ? null : $"{elementType}*";
    public string? GetFunctionPointerType(MethodSignature<string?> signature) => null;
    public string? GetGenericMethodParameter(TypeParameterNames genericContext, int index) => index < genericContext.OfMethod.Count ? genericContext.OfMethod[index] : null;
    public string? GetGenericTypeParameter(TypeParameterNames genericContext, int index) => index < genericContext.OfType.Count ? genericContext.OfType[index] : null;

    /// <summary>The one type C# has a keyword for that a signature names as a type, not a primitive.</summary>
    private static string Keyword(string type) => type == "System.Decimal" ? "decimal" : type;

    /// <summary>An array of <paramref name="element"/> as C# writes it, its <paramref name="rank"/> ("[]", "[,]") before the
    /// ranks of an element that is an array itself: an array of int[,] is int[][,].</summary>
    public static string ArrayOf(string element, string rank)
    {
        int ranks = element.Length;
        while (ranks > 0 && element[ranks - 1] == ']' && element.LastIndexOf('[', ranks - 1) is int open and >= 0)
        {
            ranks = open;
        }
        return $"{element[..ranks]}{rank}{element[ranks..]}";
    }
}

/// <summary>The names a signature's type parameters take in C#: its type's (its outer types' first) and its method's own,
/// null for one with none.</summary>
internal sealed record TypeParameterNames(IReadOnlyList<string?> OfType, IReadOnlyList<string?> OfMethod);
