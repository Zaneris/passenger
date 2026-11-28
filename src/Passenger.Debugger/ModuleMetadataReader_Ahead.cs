using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Passenger.Debugger;

/// <summary>What reading a method's code ahead of a stop asks of its metadata: its try and handler blocks, a string it
/// loads, a call's shape, a field's name, and whether a method only reads a field.</summary>
public partial class ModuleMetadataReader
{
    /// <summary>A method's try, catch, finally, fault and filter blocks at a version; null where its body is not read.</summary>
    internal ImmutableArray<ExceptionRegion>? Regions(int methodToken, int? version) => Body(methodToken, version)?.ExceptionRegions;

    /// <summary>The string an ldstr's token names.</summary>
    internal string UserString(int token) => _peMetadataReader.GetUserString(MetadataTokens.UserStringHandle(token & 0x00FFFFFF));

    /// <summary>What a call takes off the stack and gives back: its parameters, whether it takes an instance, whether it
    /// returns a value; and what it calls, its declaring type's full name and its own name. Null for a token of no method.</summary>
    internal readonly record struct CallShape(int Parameters, bool HasThis, bool ReturnsValue, string? DeclaringType, string Name);

    internal CallShape? ShapeOf(int token)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            handle = _peMetadataReader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        }
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
            {
                MethodDefinition method = _peMetadataReader.GetMethodDefinition((MethodDefinitionHandle)handle);
                MethodSignature<string> signature = method.DecodeSignature(CallLabelProvider.Instance, null);
                (string space, string name) = GetTypeName(method.GetDeclaringType());
                return new CallShape(signature.ParameterTypes.Length, signature.Header.IsInstance, signature.ReturnType != "void", Named(space, name),
                    _peMetadataReader.GetString(method.Name));
            }
            case HandleKind.MemberReference:
            {
                MemberReference member = _peMetadataReader.GetMemberReference((MemberReferenceHandle)handle);
                if (member.GetKind() != MemberReferenceKind.Method)
                {
                    return null;
                }
                MethodSignature<string> signature = member.DecodeMethodSignature(CallLabelProvider.Instance, null);
                string? declaring = member.Parent.Kind switch
                {
                    HandleKind.TypeDefinition => GetTypeName((TypeDefinitionHandle)member.Parent) is var (space, name) ? Named(space, name) : null,
                    HandleKind.TypeReference => GetTypeName((TypeReferenceHandle)member.Parent) is var (space, name) ? Named(space, name) : null,
                    _ => null,
                };
                return new CallShape(signature.ParameterTypes.Length, signature.Header.IsInstance, signature.ReturnType != "void", declaring, _peMetadataReader.GetString(member.Name));
            }
            default:
                return null;
        }

        static string Named(string space, string name) => space.Length == 0 ? name : $"{space}.{name}";
    }

    /// <summary>A field's name, by a FieldDef's token or a MemberRef's; null for any other token.</summary>
    internal string? FieldName(int token)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        return handle.Kind switch
        {
            HandleKind.FieldDefinition => _peMetadataReader.GetString(_peMetadataReader.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
            HandleKind.MemberReference when _peMetadataReader.GetMemberReference((MemberReferenceHandle)handle) is { } member && member.GetKind() == MemberReferenceKind.Field
                => _peMetadataReader.GetString(member.Name),
            _ => null,
        };
    }

    /// <summary>The type a FieldDef of this module is declared in, by its token, where that type takes no type arguments;
    /// null for any other.</summary>
    internal int? FieldOwner(int token)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.FieldDefinition)
        {
            return null;
        }
        TypeDefinitionHandle owner = _peMetadataReader.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType();
        return _peMetadataReader.GetTypeDefinition(owner).GetGenericParameters().Count == 0 ? MetadataTokens.GetToken(owner) : null;
    }

    /// <summary>The field a method of this module only reads and returns (an auto-property's getter, an expression-bodied
    /// one over a field), and whether it is static; null for any method that does more, or one a derived type may override.</summary>
    internal (int Field, bool Static)? FieldReadBy(int token)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.MethodDefinition)
        {
            return null;
        }
        MethodDefinition method = _peMetadataReader.GetMethodDefinition((MethodDefinitionHandle)handle);
        if (method.Attributes.HasFlag(System.Reflection.MethodAttributes.Virtual) && !method.Attributes.HasFlag(System.Reflection.MethodAttributes.Final)
            || MethodIL(token) is not { } il)
        {
            return null;
        }
        // ldarg.0; ldfld F; ret, or ldsfld F; ret.
        return il switch
        {
            [0x02, 0x7B, _, _, _, _, 0x2A] => (BitConverter.ToInt32(il, 2), false),
            [0x7E, _, _, _, _, 0x2A] => (BitConverter.ToInt32(il, 1), true),
            _ => null,
        };
    }
}
