using System.Runtime.InteropServices;
using Passenger.Core;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    public Dictionary<CorElementType, ICorDebugClass> CorElementToValueClassMap { get; set; } = [];
    public ICorDebugClass? CorDecimalClass { get; set; }
    public ICorDebugClass? CorVoidClass { get; set; }

    private void MapRuntimePrimitiveTypesToCorDebugClass(ICorDebugModule module)
    {
        if (Path.GetFileName(module.Name) is not "System.Private.CoreLib.dll")
        {
            throw new InvalidOperationException("Mapping primitive types to classes is only supported for System.Private.CoreLib.dll");
        }

        IMetaDataImport metadataImport = module.GetMetaDataInterface<IMetaDataImport>();

        mdTypeDef? typeDef = metadataImport.FindTypeDefByNameOrNull("System.Decimal", mdToken.Nil);
        if (typeDef is null || typeDef.Value.IsNil)
        {
            throw new InvalidOperationException("Could not find System.Decimal type definition");
        }

        CorDecimalClass = module.GetClassFromToken(typeDef.Value);

        typeDef = metadataImport.FindTypeDefByNameOrNull("System.Void", mdToken.Nil);
        if (typeDef is null || typeDef.Value.IsNil)
        {
            throw new InvalidOperationException("Could not find System.Void type definition");
        }

        CorVoidClass = module.GetClassFromToken(typeDef.Value);

        // The class a value of each primitive is boxed in, as a value returned is kept.
        foreach (PrimitiveType primitive in PrimitiveTypes.All.Where(row => row.IsPrimitiveValue))
        {
            mdTypeDef? typedef = metadataImport.FindTypeDefByNameOrNull(primitive.SystemName, mdToken.Nil);
            if (typedef is null || typedef.Value.IsNil)
            {
                throw new InvalidOperationException($"Could not find {primitive.SystemName} type definition");
            }

            CorElementToValueClassMap[MetadataTypeNames.ElementTypeOf(primitive.Code!.Value)] = module.GetClassFromToken(typedef.Value);
        }
    }
}
