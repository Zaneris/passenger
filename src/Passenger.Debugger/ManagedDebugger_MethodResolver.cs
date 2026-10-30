using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    /// <summary>The type in a type's chain of bases that declares a function, as the type instantiates it: the type
    /// arguments a call of the function takes are its own, a closed generic base's where the function is the base's.</summary>
    internal static ICorDebugType DeclaringTypeOf(ICorDebugType type, ICorDebugFunction function)
    {
        ICorDebugClass declaring = function.Class;
        for (ICorDebugType? current = type; current is not null; current = current.Base)
        {
            if (current.Class.Token == declaring.Token && current.Class.Module.BaseAddress == declaring.Module.BaseAddress)
            {
                return current;
            }
        }
        return type;
    }

    internal ICorDebugFunction? FindMethodOnType(
        ICorDebugType type,
        string methodName,
        ICorDebugValue[] args,
        bool searchStatic,
        bool idsEmpty)
    {
        ICorDebugClass typeClass = type.Class;
        ICorDebugModule module = typeClass.Module;
        IMetaDataImport metadataImport = module.GetMetaDataInterface<IMetaDataImport>();

        foreach (mdMethodDef methodToken in metadataImport.EnumMethods(typeClass.Token))
        {
            (string szMethod, mdTypeDef pClass, CorMethodAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, int pulCodeRVA, int pdwImplFlags) methodProps = metadataImport.GetMethodProps(methodToken);
            if (methodProps.szMethod != methodName)
            {
                continue;
            }

            bool isStatic = methodProps.pdwAttr.IsMdStatic();
            if ((searchStatic && !isStatic) || (!searchStatic && isStatic && !idsEmpty))
            {
                continue;
            }

            ICorDebugFunction method = module.GetFunctionFromToken(methodToken);
            if (IsMethodParameterMatch(method, args))
            {
                return method;
            }
        }

        ICorDebugType? baseType = type.Base;
        if (baseType is not null)
        {
            ICorDebugFunction? baseMethod = FindMethodOnType(baseType, methodName, args, searchStatic, idsEmpty);
            if (baseMethod is not null)
            {
                return baseMethod;
            }
        }

        // Extension methods are declared on static classes, not the receiver type, so only look them up once instance member search has failed.
        if (searchStatic is false)
        {
            return FindExtensionMethod(type, methodName, args);
        }

        return null;
    }

    private ICorDebugFunction? FindExtensionMethod(ICorDebugType receiverType, string methodName, ICorDebugValue[] args)
    {
        foreach (ModuleInfo moduleInfo in _modules.Values)
        {
            ICorDebugModule module = moduleInfo.Module;
            IMetaDataImport metadataImport = module.GetMetaDataInterface<IMetaDataImport>();

            foreach (mdTypeDef typeToken in metadataImport.EnumTypeDefs())
            {
                // Extension methods are declared in static classes marked with [Extension], so skip everything else up front
                if (metadataImport.HasAnyAttribute(typeToken, [AttributeConstants.ExtensionMethodAttributeName]) is false)
                {
                    continue;
                }

                foreach (mdMethodDef methodToken in metadataImport.EnumMethodsWithName(typeToken, methodName))
                {
                    (string szMethod, mdTypeDef pClass, CorMethodAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, int pulCodeRVA, int pdwImplFlags) methodProps = metadataImport.GetMethodProps(methodToken);
                    if (methodProps.pdwAttr.IsMdStatic() is false)
                    {
                        continue;
                    }

                    if (metadataImport.HasAnyAttribute(methodToken, [AttributeConstants.ExtensionMethodAttributeName]) is false)
                    {
                        continue;
                    }

                    ICorDebugFunction method = module.GetFunctionFromToken(methodToken);
                    if (IsMethodParameterMatch(method, args, receiverType.Type))
                    {
                        return method;
                    }
                }
            }
        }

        return null;
    }


    private bool IsMethodParameterMatch(ICorDebugFunction method, ICorDebugValue[] args, CorElementType? extensionReceiverType = null)
    {
        Handle handle = MetadataTokens.Handle(method.Token);
        if (handle.Kind is not HandleKind.MethodDefinition)
        {
            return false;
        }

        MetadataReader metadataReader = GetModuleInfoForModule(method.Class.Module).MetadataReader.PeMetadataReader;
        MethodDefinition methodDefinition = metadataReader.GetMethodDefinition((MethodDefinitionHandle)handle);
        ImmutableArray<CorElementType> parameterTypes = methodDefinition.DecodeSignature(CorElementTypeSignatureProvider.Instance, null).ParameterTypes;

        int expectedParameterCount = args.Length + (extensionReceiverType is not null ? 1 : 0);
        if (parameterTypes.Length != expectedParameterCount)
        {
            return false;
        }

        if (extensionReceiverType is not null && parameterTypes[0] != extensionReceiverType.Value)
        {
            return false;
        }

        for (int i = 0; i < args.Length; i++)
        {
            CorElementType argType = args[i].ExactType?.Type ?? args[i].Type;
            if (parameterTypes[i + (extensionReceiverType is not null ? 1 : 0)] != argType)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class CorElementTypeSignatureProvider : ISignatureTypeProvider<CorElementType, object?>
    {
        public static CorElementTypeSignatureProvider Instance { get; } = new();

        public CorElementType GetArrayType(CorElementType elementType, ArrayShape shape) => CorElementType.ARRAY;
        public CorElementType GetByReferenceType(CorElementType elementType) => CorElementType.BYREF;
        public CorElementType GetFunctionPointerType(MethodSignature<CorElementType> signature) => CorElementType.FNPTR;
        public CorElementType GetGenericInstantiation(CorElementType genericType, ImmutableArray<CorElementType> typeArguments) => CorElementType.GENERICINST;
        public CorElementType GetGenericMethodParameter(object? genericContext, int index) => CorElementType.MVAR;
        public CorElementType GetGenericTypeParameter(object? genericContext, int index) => CorElementType.VAR;
        public CorElementType GetModifiedType(CorElementType modifier, CorElementType unmodifiedType, bool isRequired) => unmodifiedType;
        public CorElementType GetPinnedType(CorElementType elementType) => elementType;
        public CorElementType GetPointerType(CorElementType elementType) => CorElementType.PTR;
        public CorElementType GetSZArrayType(CorElementType elementType) => CorElementType.SZARRAY;

        public CorElementType GetPrimitiveType(PrimitiveTypeCode typeCode) => MetadataTypeNames.ElementTypeOf(typeCode);

        public CorElementType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            rawTypeKind == (byte)SignatureTypeKind.ValueType ? CorElementType.VALUETYPE : CorElementType.CLASS;

        public CorElementType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            rawTypeKind == (byte)SignatureTypeKind.ValueType ? CorElementType.VALUETYPE : CorElementType.CLASS;

        public CorElementType GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
