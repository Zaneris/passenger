using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace Passenger.Debugger;

/// <summary>The type a variable is declared as, so far as an expression of it needs to reach its value's members: a type by
/// its definition's name (<see cref="MetadataTypeNames"/>, nested types joined by '.'), whatever its type arguments, or an
/// array by its elements' (<see cref="Element"/>, with no name). A member of a type below it, which the value's own type
/// derives from it, is reached only through a cast.</summary>
public sealed record DeclaredType(string? Name, DeclaredType? Element = null)
{
    public static DeclaredType Object { get; } = new("System.Object");
}

/// <summary>A signature's types as a variable is declared with them; null for one naming no type whose members a value of
/// it could lack (a type parameter, a pointer, a primitive but object), so its value's members are reached as its own type
/// has them.</summary>
internal sealed class DeclaredTypeProvider : ISignatureTypeProvider<DeclaredType?, object?>
{
    public static DeclaredTypeProvider Instance { get; } = new();

    public DeclaredType? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode is PrimitiveTypeCode.Object ? DeclaredType.Object : null;
    public DeclaredType? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => new(MetadataTypeNames.Of(reader, handle, '.'));
    public DeclaredType? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => new(MetadataTypeNames.Of(reader, handle, '.'));
    public DeclaredType? GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    public DeclaredType? GetGenericInstantiation(DeclaredType? genericType, ImmutableArray<DeclaredType?> typeArguments) => genericType;
    public DeclaredType? GetSZArrayType(DeclaredType? elementType) => elementType is null ? null : new(null, elementType);
    public DeclaredType? GetArrayType(DeclaredType? elementType, ArrayShape shape) => elementType is null ? null : new(null, elementType);
    public DeclaredType? GetByReferenceType(DeclaredType? elementType) => elementType;
    public DeclaredType? GetPinnedType(DeclaredType? elementType) => elementType;
    public DeclaredType? GetModifiedType(DeclaredType? modifier, DeclaredType? unmodifiedType, bool isRequired) => unmodifiedType;
    public DeclaredType? GetPointerType(DeclaredType? elementType) => null;
    public DeclaredType? GetFunctionPointerType(MethodSignature<DeclaredType?> signature) => null;
    public DeclaredType? GetGenericMethodParameter(object? genericContext, int index) => null;
    public DeclaredType? GetGenericTypeParameter(object? genericContext, int index) => null;
}
