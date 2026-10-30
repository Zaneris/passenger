using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection.Metadata;
using System.Text;
using Passenger.Core;

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

    public string? GetPrimitiveType(PrimitiveTypeCode typeCode) => PrimitiveTypes.Of(typeCode).CSharpName;

    public string? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Keyword(MetadataTypeNames.Of(reader, handle, '.'));
    public string? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Keyword(MetadataTypeNames.Of(reader, handle, '.'));
    public string? GetTypeFromSpecification(MetadataReader reader, TypeParameterNames genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string? GetGenericInstantiation(string? genericType, ImmutableArray<string?> typeArguments) =>
        genericType is null || typeArguments.Any(argument => argument is null) ? null : Closed(genericType, typeArguments!, spelledNullable);

    /// <summary>A generic type's metadata name (A.B`1.C`2) closed as C# writes it: each type it is nested in, outermost
    /// first, taking as many arguments as its arity (A.B&lt;x&gt;.C&lt;y, z&gt;), and a nullable as T? unless
    /// <paramref name="spelledNullable"/>; null where the arguments are not as many as the arities.</summary>
    public static string? Closed(string genericType, IReadOnlyList<string> typeArguments, bool spelledNullable)
    {
        if (genericType == "System.Nullable`1" && typeArguments.Count == 1 && !spelledNullable)
        {
            return $"{typeArguments[0]}?";
        }
        StringBuilder named = new();
        int taken = 0;
        foreach (string part in genericType.Split('.'))
        {
            named.Append(named.Length == 0 ? "" : ".");
            int tick = part.IndexOf('`', StringComparison.Ordinal);
            if (tick < 0 || !int.TryParse(part.AsSpan(tick + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int arity) || taken + arity > typeArguments.Count)
            {
                named.Append(part);
                continue;
            }
            named.Append(part, 0, tick).Append('<').AppendJoin(", ", typeArguments.Skip(taken).Take(arity)).Append('>');
            taken += arity;
        }
        return taken == typeArguments.Count ? named.ToString() : null;
    }

    /// <summary>A type as C# writes it without its type arguments: List&lt;int&gt; is List (a compiler's name, which begins
    /// with '&lt;', kept whole).</summary>
    [return: NotNullIfNotNull(nameof(type))]
    public static string? WithoutTypeArguments(string? type) => type?.IndexOf('<', StringComparison.Ordinal) is int generic and > 0 ? type[..generic] : type;

    public string? GetSZArrayType(string? elementType) => elementType is null ? null : ArrayOf(elementType, "[]");
    public string? GetArrayType(string? elementType, ArrayShape shape) => elementType is null ? null : ArrayOf(elementType, $"[{new string(',', shape.Rank - 1)}]");
    public string? GetByReferenceType(string? elementType) => null;
    public string? GetPinnedType(string? elementType) => elementType;
    public string? GetModifiedType(string? modifier, string? unmodifiedType, bool isRequired) => unmodifiedType;
    public string? GetPointerType(string? elementType) => elementType is null ? null : $"{elementType}*";
    public string? GetFunctionPointerType(MethodSignature<string?> signature) => null;
    public string? GetGenericMethodParameter(TypeParameterNames genericContext, int index) => index < genericContext.OfMethod.Count ? genericContext.OfMethod[index] : null;
    public string? GetGenericTypeParameter(TypeParameterNames genericContext, int index) => index < genericContext.OfType.Count ? genericContext.OfType[index] : null;

    /// <summary>A type a signature names by its token, by C#'s keyword where it has one (decimal, which is no primitive).</summary>
    private static string Keyword(string type) => PrimitiveTypes.Named(type)?.Keyword ?? type;

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
