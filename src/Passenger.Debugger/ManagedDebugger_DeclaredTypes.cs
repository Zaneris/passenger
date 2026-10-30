using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>The types variables are declared as, read from metadata (the runtime knows only each value's own type): a local's
/// from its method's signature at its frame's version, a parameter's and this's from its method, a field's and a property's
/// from their own. A member row's expression casts its object to the type declaring the member where the declared type
/// does not reach it (<see cref="Below"/>).</summary>
public partial class ManagedDebugger
{
    /// <summary>The types a frame's locals are declared as, by their slots, at the frame's version of its method.</summary>
    private ImmutableArray<DeclaredType?> DeclaredLocals(ModuleInfo module, ICorDebugILFrame frame) =>
        Declared(() => module.MetadataReader.LocalTypes(frame.Function.Token, ModuleMetadataReader.VersionOf(frame))) is { IsDefault: false } types ? types : [];

    /// <summary>The types a method's parameters are declared as, this left out.</summary>
    private ImmutableArray<DeclaredType?> DeclaredParameters(ModuleInfo module, ICorDebugFunction function) =>
        Declared(() => module.MetadataReader.PeMetadataReader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(function.Token))
            .DecodeSignature(DeclaredTypeProvider.Instance, null).ParameterTypes) is { IsDefault: false } types ? types : [];

    /// <summary>The type a method's this is declared as: its class, or for the class the compiler made of a lambda or an async
    /// method, which keeps the user's this in a field, the class that one is nested in.</summary>
    private DeclaredType? DeclaredThis(ModuleInfo module, ICorDebugFunction function, bool generated) => Declared(() =>
    {
        MetadataReader reader = module.MetadataReader.PeMetadataReader;
        TypeDefinitionHandle type = (TypeDefinitionHandle)MetadataTokens.EntityHandle(function.Class.Token);
        if (generated)
        {
            type = reader.GetTypeDefinition(type).GetDeclaringType();
        }
        return type.IsNil ? null : new DeclaredType(MetadataTypeNames.Of(reader, type, '.'));
    });

    /// <summary>The type a field is declared as.</summary>
    private DeclaredType? DeclaredField(ICorDebugClass declaring, mdFieldDef field) =>
        FindModule(declaring.Module) is { } module
            ? Declared(() => module.MetadataReader.PeMetadataReader.GetFieldDefinition((FieldDefinitionHandle)MetadataTokens.EntityHandle(field))
                .DecodeSignature(DeclaredTypeProvider.Instance, null))
            : null;

    /// <summary>The type a property is declared as.</summary>
    private DeclaredType? DeclaredProperty(ICorDebugClass declaring, mdProperty property) =>
        FindModule(declaring.Module) is { } module
            ? Declared(() => module.MetadataReader.PeMetadataReader.GetPropertyDefinition((PropertyDefinitionHandle)MetadataTokens.EntityHandle(property))
                .DecodeSignature(DeclaredTypeProvider.Instance, null).ReturnType)
            : null;

    /// <summary>A runtime type as a variable would be declared with it, where none is known: an array's elements' type, a
    /// class's own.</summary>
    private DeclaredType? DeclaredAs(ICorDebugType type) => type.Type switch
    {
        CorElementType.OBJECT => DeclaredType.Object,
        CorElementType.CLASS or CorElementType.VALUETYPE => DefinitionName(type) is { } name ? new DeclaredType(name) : null,
        CorElementType.SZARRAY or CorElementType.ARRAY => DeclaredAs(type.FirstTypeParameter) is { } element ? new DeclaredType(null, element) : null,
        _ => null,
    };

    /// <summary>How many of a type and its base types, from it up, lie below the type its value's variable is declared as, so
    /// that its expression reaches their members only through a cast: none where that is not known, and every one where it
    /// is none of them (an interface, object).</summary>
    private int Below(ICorDebugType type, DeclaredType? declared)
    {
        if (declared?.Name is not { } name)
        {
            return 0;
        }
        int below = 0;
        for (ICorDebugType? at = type; at is { Type: CorElementType.CLASS or CorElementType.VALUETYPE }; at = at.Base)
        {
            if (DefinitionName(at) == name)
            {
                return below;
            }
            below++;
        }
        return below;
    }

    /// <summary>A class's or struct's definition's name as <see cref="DeclaredType"/> writes it; null where its module's
    /// metadata is not read.</summary>
    private string? DefinitionName(ICorDebugType type) => FindModule(type.Class.Module) is { } module
        ? Declared(() => MetadataTypeNames.Of(module.MetadataReader.PeMetadataReader, (TypeDefinitionHandle)MetadataTokens.EntityHandle(type.Class.Token), '.'))
        : null;

    /// <summary>A declared type read from metadata; the default where the metadata does not read (a member an update added to
    /// a module whose metadata the debugger did not take), so the value's members are reached as its own type has them.</summary>
    private static T? Declared<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception error) when (Failures.OfReading(error) || Failures.OfRuntime(error))
        {
            return default;
        }
    }
}
