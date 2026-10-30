using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger.ExpressionEvaluator.Cil;

internal readonly record struct ResolvedRuntimeType(ModuleInfo Module, TypeDefinitionHandle Handle, ImmutableArray<ResolvedCilType> TypeArguments = default)
{
    public ICorDebugClass Class => Module.Module.GetClassFromToken((mdTypeDef)MetadataTokens.GetToken(Handle));
}

internal readonly record struct ResolvedRuntimeField(ResolvedRuntimeType DeclaringType, FieldDefinitionHandle Handle, bool IsStatic)
{
    public mdFieldDef Token => (mdFieldDef)MetadataTokens.GetToken(Handle);
}

internal readonly record struct ResolvedRuntimeMethod(ResolvedRuntimeType DeclaringType, MethodDefinitionHandle Handle, string Name, MethodSignature<string> Signature, bool IsStatic, ImmutableArray<ResolvedCilType> MethodTypeArguments = default)
{
    public ICorDebugFunction Function => DeclaringType.Module.Module.GetFunctionFromToken((mdMethodDef)MetadataTokens.GetToken(Handle));
}

internal readonly record struct ResolvedEvaluationMethod(MethodDefinitionHandle Handle, MethodSignature<string> Signature, bool IsStatic);
internal sealed record ResolvedCilType(PrimitiveTypeCode? Primitive, ResolvedRuntimeType? RuntimeType, ResolvedCilType? ElementType = null, int ArrayRank = 0, bool IsSzArray = false);
internal sealed class EvaluationMetadataResolver(ManagedDebugger debugger, MetadataReader evaluationReader, PEReader evaluationPeReader, ICorDebugAppDomain appDomain, ICorDebugType[] typeGenericArguments, ICorDebugType[] methodGenericArguments, ModuleInfo? currentFrameModule)
{
    /// <summary>The concrete generic arguments the evaluation method's type parameters (<c>!i</c>, then <c>!!i</c>) map
    /// to: the frame's instantiation, or for a type-context evaluation (DebuggerDisplay) the root value's own type
    /// arguments.</summary>
    private readonly (ICorDebugType[] TypeArguments, ICorDebugType[] MethodArguments) _genericArguments = (typeGenericArguments, methodGenericArguments);
    private readonly Dictionary<int, ResolvedRuntimeField> _fieldCache = new();
    private readonly Dictionary<int, ResolvedRuntimeMethod> _methodCache = new();
    private readonly Dictionary<int, ResolvedCilType> _typeTokenCache = new();
    private readonly Dictionary<string, ResolvedRuntimeMethod> _runtimeMethodCache = new();
    private readonly Dictionary<string, ResolvedRuntimeType> _runtimeTypeCache = new();
    private readonly Dictionary<StandaloneSignatureHandle, ImmutableArray<ResolvedCilType>?> _localTypesCache = new();

    public string ResolveUserString(int token) => evaluationReader.GetUserString(MetadataTokens.UserStringHandle(token));

    public ResolvedCilType ResolveMethodReturnType(MethodDefinitionHandle handle) =>
        evaluationReader.GetMethodDefinition(handle).DecodeSignature(new RuntimeTypeSignatureProvider(this), genericContext: null).ReturnType;

    public MethodSignature<string> ResolveEvaluationMethodSignature(MethodDefinitionHandle handle) =>
        evaluationReader.GetMethodDefinition(handle).DecodeSignature(SignatureKeyProvider.Instance, genericContext: null);

    public TypeDefinitionHandle GetEvaluationMethodDeclaringType(MethodDefinitionHandle handle) =>
        evaluationReader.GetMethodDefinition(handle).GetDeclaringType();

    public Guid CurrentFrameModuleVersionId =>
        currentFrameModule?.MetadataReader.Mvid ?? throw new InvalidOperationException("The current frame module is unavailable");

    public IReadOnlyList<FieldDefinitionHandle> GetEvaluationInstanceFields(TypeDefinitionHandle handle) =>
        evaluationReader.GetTypeDefinition(handle).GetFields()
            .Where(field => (evaluationReader.GetFieldDefinition(field).Attributes & FieldAttributes.Static) == 0)
            .ToArray();

    public ResolvedCilType ResolveTypeToken(int token)
    {
        if (_typeTokenCache.TryGetValue(token, out ResolvedCilType? cached))
        {
            return cached;
        }

        EntityHandle handle = MetadataTokens.EntityHandle(token);
        ResolvedCilType result = handle.Kind == HandleKind.TypeSpecification
            ? evaluationReader.GetTypeSpecification((TypeSpecificationHandle)handle)
                .DecodeSignature(new RuntimeTypeSignatureProvider(this), genericContext: null)
            : new ResolvedCilType(null, ResolveType(handle));
        _typeTokenCache[token] = result;
        return result;
    }

    public ResolvedCilType ResolveGenericTypeParameter(int index) =>
        ResolveGenericParameter(index, _genericArguments.TypeArguments, "!");

    public ResolvedCilType ResolveGenericMethodParameter(int index) =>
        ResolveGenericParameter(index, _genericArguments.MethodArguments, "!!");

    private ResolvedCilType ResolveGenericParameter(int index, IReadOnlyList<ICorDebugType> arguments, string prefix)
    {
        if (index >= 0 && index < arguments.Count)
        {
            return ResolveCorDebugType(arguments[index]);
        }

        throw new NotSupportedException($"The generic parameter '{prefix}{index}' is not available in the current frame");
    }

    private ResolvedCilType ResolveCorDebugType(ICorDebugType type)
    {
        return type.Type switch
        {
            CorElementType.BOOLEAN => new ResolvedCilType(PrimitiveTypeCode.Boolean, null),
            CorElementType.CHAR => new ResolvedCilType(PrimitiveTypeCode.Char, null),
            CorElementType.I1 => new ResolvedCilType(PrimitiveTypeCode.SByte, null),
            CorElementType.U1 => new ResolvedCilType(PrimitiveTypeCode.Byte, null),
            CorElementType.I2 => new ResolvedCilType(PrimitiveTypeCode.Int16, null),
            CorElementType.U2 => new ResolvedCilType(PrimitiveTypeCode.UInt16, null),
            CorElementType.I4 => new ResolvedCilType(PrimitiveTypeCode.Int32, null),
            CorElementType.U4 => new ResolvedCilType(PrimitiveTypeCode.UInt32, null),
            CorElementType.I8 => new ResolvedCilType(PrimitiveTypeCode.Int64, null),
            CorElementType.U8 => new ResolvedCilType(PrimitiveTypeCode.UInt64, null),
            CorElementType.R4 => new ResolvedCilType(PrimitiveTypeCode.Single, null),
            CorElementType.R8 => new ResolvedCilType(PrimitiveTypeCode.Double, null),
            CorElementType.I => new ResolvedCilType(PrimitiveTypeCode.IntPtr, null),
            CorElementType.U => new ResolvedCilType(PrimitiveTypeCode.UIntPtr, null),
            CorElementType.STRING => new ResolvedCilType(PrimitiveTypeCode.String, null),
            CorElementType.OBJECT => new ResolvedCilType(PrimitiveTypeCode.Object, null),
            CorElementType.CLASS or CorElementType.VALUETYPE => new ResolvedCilType(null, ResolveRuntimeType(type)),
            CorElementType.SZARRAY => new ResolvedCilType(null, null, ResolveCorDebugType(type.FirstTypeParameter), 1, true),
            CorElementType.ARRAY => new ResolvedCilType(null, null, ResolveCorDebugType(type.FirstTypeParameter), type.Rank),
            _ => throw new NotSupportedException($"Cannot resolve a CIL type from CorElementType '{type.Type}'")
        };
    }

    private ResolvedRuntimeType ResolveRuntimeType(ICorDebugType type)
    {
        ICorDebugClass @class = type.Class;
        ModuleInfo moduleInfo = debugger.GetModuleInfoForModule(@class.Module);
        TypeDefinitionHandle handle = (TypeDefinitionHandle)MetadataTokens.Handle(@class.Token);
        ICorDebugType[] typeParameters;
        try
        {
            typeParameters = type.TypeParameters;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            typeParameters = [];
        }
        return new ResolvedRuntimeType(moduleInfo, handle, typeParameters.Length == 0 ? default : typeParameters.Select(ResolveCorDebugType).ToImmutableArray());
    }

    public ResolvedRuntimeField ResolveField(int token)
    {
        if (_fieldCache.TryGetValue(token, out ResolvedRuntimeField cached))
        {
            return cached;
        }

        EntityHandle handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.MemberReference)
        {
            throw new NotSupportedException($"Evaluation field token kind '{handle.Kind}' is not supported");
        }

        MemberReference member = evaluationReader.GetMemberReference((MemberReferenceHandle)handle);
        ResolvedRuntimeType declaringType = ResolveType(member.Parent);
        string name = evaluationReader.GetString(member.Name);
        MetadataReader reader = declaringType.Module.MetadataReader.PeMetadataReader;
        TypeDefinition type = reader.GetTypeDefinition(declaringType.Handle);
        foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
        {
            FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
            if (reader.GetString(field.Name) == name)
            {
                ResolvedRuntimeField result = new ResolvedRuntimeField(declaringType, fieldHandle, (field.Attributes & FieldAttributes.Static) != 0);
                _fieldCache[token] = result;
                return result;
            }
        }

        throw new MissingFieldException(GetTypeName(declaringType), name);
    }

    public ResolvedRuntimeMethod ResolveMethod(int token)
    {
        if (_methodCache.TryGetValue(token, out ResolvedRuntimeMethod cached))
        {
            return cached;
        }

        EntityHandle handle = MetadataTokens.EntityHandle(token);
        ImmutableArray<ResolvedCilType> methodTypeArguments = default;
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            MethodSpecification specification = evaluationReader.GetMethodSpecification((MethodSpecificationHandle)handle);
            methodTypeArguments = specification.DecodeSignature(new RuntimeTypeSignatureProvider(this), genericContext: null);
            handle = specification.Method;
        }
        if (handle.Kind != HandleKind.MemberReference)
        {
            throw new NotSupportedException($"Evaluation method token kind '{handle.Kind}' is not supported");
        }

        MemberReference member = evaluationReader.GetMemberReference((MemberReferenceHandle)handle);
        ResolvedRuntimeType declaringType = ResolveType(member.Parent);
        string name = evaluationReader.GetString(member.Name);
        string expected = SignatureKeyProvider.Of(member.DecodeMethodSignature(SignatureKeyProvider.Instance, genericContext: null));
        MetadataReader reader = declaringType.Module.MetadataReader.PeMetadataReader;
        foreach (MethodDefinitionHandle methodHandle in reader.GetTypeDefinition(declaringType.Handle).GetMethods())
        {
            MethodDefinition method = reader.GetMethodDefinition(methodHandle);
            if (reader.GetString(method.Name) != name)
            {
                continue;
            }

            MethodSignature<string> signature = method.DecodeSignature(SignatureKeyProvider.Instance, genericContext: null);
            if (SignatureKeyProvider.Of(signature) == expected)
            {
                ResolvedRuntimeMethod result = new ResolvedRuntimeMethod(declaringType, methodHandle, name, signature, (method.Attributes & MethodAttributes.Static) != 0, methodTypeArguments);
                _methodCache[token] = result;
                return result;
            }
        }

        throw new MissingMethodException(GetTypeName(declaringType), name);
    }

    /// <summary>A multi-dimensional array's constructor, Get, Set or Address (<c>new int[2, 3]</c>, <c>grid[1, 2]</c>), which
    /// the runtime makes for each such array type and no type defines: the method's name, the array's type and how many
    /// parameters the method takes.</summary>
    public bool TryResolveArrayMethod(int token, out string name, [NotNullWhen(true)] out ResolvedCilType? array, out int parameters)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MemberReference
            && evaluationReader.GetMemberReference((MemberReferenceHandle)handle) is { Parent.Kind: HandleKind.TypeSpecification } member
            && evaluationReader.GetString(member.Name) is ".ctor" or "Get" or "Set" or "Address"
            && ResolveTypeToken(MetadataTokens.GetToken(member.Parent)) is { ArrayRank: > 0, IsSzArray: false } resolved)
        {
            name = evaluationReader.GetString(member.Name);
            array = resolved;
            parameters = member.DecodeMethodSignature(SignatureKeyProvider.Instance, genericContext: null).RequiredParameterCount;
            return true;
        }
        name = string.Empty;
        array = null;
        parameters = 0;
        return false;
    }

    public bool TryResolveEvaluationMethod(int token, out ResolvedEvaluationMethod result)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            handle = evaluationReader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        }
        if (handle.Kind != HandleKind.MethodDefinition)
        {
            result = default;
            return false;
        }
        MethodDefinitionHandle methodHandle = (MethodDefinitionHandle)handle;
        MethodDefinition method = evaluationReader.GetMethodDefinition(methodHandle);
        result = new ResolvedEvaluationMethod(
            methodHandle,
            method.DecodeSignature(SignatureKeyProvider.Instance, genericContext: null),
            (method.Attributes & MethodAttributes.Static) != 0);
        return true;
    }

    public bool TryResolveDebuggerIntrinsic(int token, out string methodName)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            handle = evaluationReader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        }

        if (handle.Kind == HandleKind.MemberReference)
        {
            MemberReference member = evaluationReader.GetMemberReference((MemberReferenceHandle)handle);
            if (member.Parent.Kind == HandleKind.TypeReference)
            {
                TypeReference type = evaluationReader.GetTypeReference((TypeReferenceHandle)member.Parent);
                if (evaluationReader.GetString(type.Namespace) == "Microsoft.VisualStudio.Debugger.Clr" &&
                    evaluationReader.GetString(type.Name) == "IntrinsicMethods")
                {
                    methodName = evaluationReader.GetString(member.Name);
                    return true;
                }
            }
        }
        methodName = string.Empty;
        return false;
    }

    public MethodBodyBlock GetEvaluationMethodBody(MethodDefinitionHandle handle) =>
        evaluationPeReader.GetMethodBody(evaluationReader.GetMethodDefinition(handle).RelativeVirtualAddress);

    public int GetEvaluationLocalCount(StandaloneSignatureHandle handle) => handle.IsNil
        ? 0
        : evaluationReader.GetStandaloneSignature(handle).DecodeLocalSignature(LocalCountSignatureProvider.Instance, genericContext: null).Length;

    /// <summary>The type an evaluation method's local is declared as, a ref local's as what it refers to; null where a type
    /// of its signature is one the debugger cannot resolve.</summary>
    public ResolvedCilType? ResolveEvaluationLocalType(StandaloneSignatureHandle handle, int index)
    {
        if (!_localTypesCache.TryGetValue(handle, out ImmutableArray<ResolvedCilType>? types))
        {
            try
            {
                types = evaluationReader.GetStandaloneSignature(handle).DecodeLocalSignature(new RuntimeTypeSignatureProvider(this), genericContext: null);
            }
            catch (Exception error) when (error is TypeLoadException or NotSupportedException or MissingMemberException or BadImageFormatException)
            {
                types = null;
            }
            _localTypesCache[handle] = types;
        }
        return types is { } declared && index < declared.Length ? declared[index] : null;
    }

    public ICorDebugType GetCorDebugType(ResolvedRuntimeType type)
    {
        CorElementType elementType = IsValueType(type) ? CorElementType.VALUETYPE : CorElementType.CLASS;
        ICorDebugType[] typeArguments = type.TypeArguments.IsDefaultOrEmpty ? [] : type.TypeArguments.Select(GetCorDebugType).ToArray();
        return ((ICorDebugClass2)type.Class).GetParameterizedType(elementType, typeArguments.Length, typeArguments);
    }

    public ICorDebugType GetCorDebugType(ResolvedCilType type)
    {
        if (type.ElementType is not null)
        {
            return ((ICorDebugAppDomain2)appDomain).GetArrayOrPointerType(
                type.IsSzArray ? CorElementType.SZARRAY : CorElementType.ARRAY,
                type.ArrayRank,
                GetCorDebugType(type.ElementType));
        }
        if (type.RuntimeType is { } runtimeType)
        {
            return GetCorDebugType(runtimeType);
        }

        if (type.Primitive is not { } primitive)
        {
            throw new NotSupportedException("The CIL type cannot be materialized");
        }

        (string? name, CorElementType elementType) = primitive switch
        {
            PrimitiveTypeCode.Boolean => ("Boolean", CorElementType.VALUETYPE),
            PrimitiveTypeCode.Byte => ("Byte", CorElementType.VALUETYPE),
            PrimitiveTypeCode.SByte => ("SByte", CorElementType.VALUETYPE),
            PrimitiveTypeCode.Char => ("Char", CorElementType.VALUETYPE),
            PrimitiveTypeCode.Int16 => ("Int16", CorElementType.VALUETYPE),
            PrimitiveTypeCode.UInt16 => ("UInt16", CorElementType.VALUETYPE),
            PrimitiveTypeCode.Int32 => ("Int32", CorElementType.VALUETYPE),
            PrimitiveTypeCode.UInt32 => ("UInt32", CorElementType.VALUETYPE),
            PrimitiveTypeCode.Int64 => ("Int64", CorElementType.VALUETYPE),
            PrimitiveTypeCode.UInt64 => ("UInt64", CorElementType.VALUETYPE),
            PrimitiveTypeCode.Single => ("Single", CorElementType.VALUETYPE),
            PrimitiveTypeCode.Double => ("Double", CorElementType.VALUETYPE),
            PrimitiveTypeCode.String => ("String", CorElementType.CLASS),
            PrimitiveTypeCode.Object => ("Object", CorElementType.CLASS),
            PrimitiveTypeCode.IntPtr => ("IntPtr", CorElementType.VALUETYPE),
            PrimitiveTypeCode.UIntPtr => ("UIntPtr", CorElementType.VALUETYPE),
            _ => throw new NotSupportedException($"Primitive CIL type '{primitive}' cannot be materialized")
        };
        ResolvedRuntimeType primitiveRuntimeType = FindRuntimeType("System", name);
        return ((ICorDebugClass2)primitiveRuntimeType.Class).GetParameterizedType(elementType, 0, []);
    }

    public string GetRuntimeTypeName(ResolvedRuntimeType type) => GetTypeName(type);
    public int GetRuntimeTypeGenericArity(ResolvedRuntimeType type) =>
        type.Module.MetadataReader.PeMetadataReader.GetTypeDefinition(type.Handle).GetGenericParameters().Count();

    public string GetAssemblyQualifiedTypeName(ResolvedCilType type) => $"{GetReflectionTypeName(type)}, {GetTypeAssemblyName(type)}";

    public ResolvedCilType ResolveEvaluationFieldType(FieldDefinitionHandle handle) =>
        evaluationReader.GetFieldDefinition(handle).DecodeSignature(new RuntimeTypeSignatureProvider(this), genericContext: null);

    /// <summary>Whether a field of the evaluation's own holds data the compilation laid out in its image.</summary>
    public bool HasEvaluationFieldData(FieldDefinitionHandle handle) =>
        (evaluationReader.GetFieldDefinition(handle).Attributes & FieldAttributes.HasFieldRVA) != 0;

    /// <summary>The bytes the compilation laid out in the evaluation's image for a field of its own, an array initializer's
    /// constants: as many as the field's type holds.</summary>
    public byte[] GetEvaluationFieldData(FieldDefinitionHandle handle)
    {
        FieldDefinition field = evaluationReader.GetFieldDefinition(handle);
        int address = field.GetRelativeVirtualAddress();
        BlobReader signature = evaluationReader.GetBlobReader(field.Signature);
        signature.ReadSignatureHeader();
        int size = signature.ReadSignatureTypeCode() switch
        {
            SignatureTypeCode.TypeHandle when signature.ReadTypeHandle() is { Kind: HandleKind.TypeDefinition } type =>
                evaluationReader.GetTypeDefinition((TypeDefinitionHandle)type).GetLayout().Size,
            var code => SizeOf(code),
        };
        PEMemoryBlock data = address == 0 ? default : evaluationPeReader.GetSectionData(address);
        if (size <= 0 || data.Length < size)
        {
            throw new BadImageFormatException($"The evaluation's field '{evaluationReader.GetString(field.Name)}' holds no data of its type's size");
        }
        return [.. data.GetContent(0, size)];
    }

    /// <summary>How many bytes a value of a primitive or an enum takes; 0 for any other type.</summary>
    public static int SizeOf(ResolvedCilType type)
    {
        if (type.Primitive is { } primitive)
        {
            return SizeOf((SignatureTypeCode)primitive);
        }
        return EnumUnderlyingType(type) is { } underlying ? SizeOf((SignatureTypeCode)underlying) : 0;
    }

    /// <summary>The primitive a type is stored as: its own, the one CoreLib's class of it stands for (System.Int32 as a
    /// type reference names it), or an enum's underlying one; null for any other type.</summary>
    public PrimitiveTypeCode? StoredAs(ResolvedCilType type)
    {
        if (type.Primitive is { } primitive)
        {
            return primitive;
        }
        if (EnumUnderlyingType(type) is { } underlying)
        {
            return underlying;
        }
        if (type is not { RuntimeType: { } runtimeType, ElementType: null }
            || runtimeType.Module.MetadataReader.PeMetadataReader is not { IsAssembly: true } reader
            || reader.GetString(reader.GetAssemblyDefinition().Name) != "System.Private.CoreLib")
        {
            return null;
        }
        string name = GetTypeName(runtimeType);
        return Enum.GetValues<PrimitiveTypeCode>().Where(code => MetadataTypeNames.ElementTypeOf(code)
            is not (CorElementType.END or CorElementType.VOID or CorElementType.STRING or CorElementType.OBJECT or CorElementType.TYPEDBYREF))
            .Select(code => (PrimitiveTypeCode?)code).FirstOrDefault(code => MetadataTypeNames.Of(code!.Value) == name);
    }

    /// <summary>The primitive an enum is stored as, from its one instance field; null for any other type.</summary>
    public static PrimitiveTypeCode? EnumUnderlyingType(ResolvedCilType type)
    {
        if (type is { RuntimeType: { } runtimeType, ElementType: null } && IsValueType(runtimeType))
        {
            MetadataReader reader = runtimeType.Module.MetadataReader.PeMetadataReader;
            foreach (FieldDefinitionHandle handle in reader.GetTypeDefinition(runtimeType.Handle).GetFields())
            {
                FieldDefinition field = reader.GetFieldDefinition(handle);
                if ((field.Attributes & FieldAttributes.Static) == 0 && reader.GetString(field.Name) == "value__")
                {
                    BlobReader signature = reader.GetBlobReader(field.Signature);
                    signature.ReadSignatureHeader();
                    return (PrimitiveTypeCode)signature.ReadSignatureTypeCode();
                }
            }
        }
        return null;
    }

    private static int SizeOf(SignatureTypeCode code) => code switch
    {
        SignatureTypeCode.Boolean or SignatureTypeCode.SByte or SignatureTypeCode.Byte => 1,
        SignatureTypeCode.Char or SignatureTypeCode.Int16 or SignatureTypeCode.UInt16 => 2,
        SignatureTypeCode.Int32 or SignatureTypeCode.UInt32 or SignatureTypeCode.Single => 4,
        SignatureTypeCode.Int64 or SignatureTypeCode.UInt64 or SignatureTypeCode.Double => 8,
        _ => 0,
    };

    public ResolvedRuntimeMethod ResolveRuntimeMethod(string @namespace, string typeName, string methodName, params string[] parameterTypes)
    {
        string cacheKey = $"{@namespace}.{typeName}.{methodName}({string.Join(",", parameterTypes)})";
        if (_runtimeMethodCache.TryGetValue(cacheKey, out ResolvedRuntimeMethod cached))
        {
            return cached;
        }

        ResolvedRuntimeMethod result = ResolveMethodOf(FindRuntimeType(@namespace, typeName), methodName, parameterTypes);
        _runtimeMethodCache[cacheKey] = result;
        return result;
    }

    /// <summary>A method of a type, by its name and its parameters' signature keys (<see cref="SignatureKeyProvider"/>): of
    /// a generic type's instance as the type gives it.</summary>
    public static ResolvedRuntimeMethod ResolveMethodOf(ResolvedRuntimeType type, string methodName, params string[] parameterTypes)
    {
        MetadataReader reader = type.Module.MetadataReader.PeMetadataReader;
        foreach (MethodDefinitionHandle handle in reader.GetTypeDefinition(type.Handle).GetMethods())
        {
            MethodDefinition method = reader.GetMethodDefinition(handle);
            if (reader.GetString(method.Name) != methodName)
            {
                continue;
            }

            MethodSignature<string> signature = method.DecodeSignature(SignatureKeyProvider.Instance, genericContext: null);
            if (signature.ParameterTypes.SequenceEqual(parameterTypes))
            {
                return new ResolvedRuntimeMethod(type, handle, methodName, signature, (method.Attributes & MethodAttributes.Static) != 0);
            }
        }
        throw new MissingMethodException(GetTypeName(type), methodName);
    }

    /// <summary>Whether a type is a ref struct, whose value may hold a reference to where another value lives.</summary>
    internal static bool IsByRefLike(ResolvedRuntimeType type)
    {
        MetadataReader reader = type.Module.MetadataReader.PeMetadataReader;
        return ModuleMetadataReader.HasAttribute(reader, reader.GetTypeDefinition(type.Handle).GetCustomAttributes(), [ByRefLikeAttribute]);
    }

    /// <summary>The attribute the compiler marks a ref struct with.</summary>
    internal const string ByRefLikeAttribute = "System.Runtime.CompilerServices.IsByRefLikeAttribute";

    private ResolvedRuntimeType ResolveType(EntityHandle handle)
    {
        return handle.Kind switch
        {
            HandleKind.TypeReference => ResolveTypeReference((TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => ResolveTypeSpecification((TypeSpecificationHandle)handle),
            _ => throw new NotSupportedException($"Evaluation type token kind '{handle.Kind}' is not supported")
        };
    }

    private ResolvedRuntimeType ResolveTypeSpecification(TypeSpecificationHandle handle)
    {
        return evaluationReader.GetTypeSpecification(handle)
            .DecodeSignature(new RuntimeTypeSignatureProvider(this), genericContext: null).RuntimeType
            ?? throw new TypeLoadException("The type specification does not identify a runtime type");
    }

    private ResolvedRuntimeType ResolveTypeReference(TypeReferenceHandle handle)
    {
        TypeReference reference = evaluationReader.GetTypeReference(handle);
        string name = evaluationReader.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            ResolvedRuntimeType containing = ResolveTypeReference((TypeReferenceHandle)reference.ResolutionScope);
            MetadataReader reader = containing.Module.MetadataReader.PeMetadataReader;
            foreach (TypeDefinitionHandle nestedHandle in reader.GetTypeDefinition(containing.Handle).GetNestedTypes())
            {
                if (reader.GetString(reader.GetTypeDefinition(nestedHandle).Name) == name)
                {
                    return new ResolvedRuntimeType(containing.Module, nestedHandle);
                }
            }
            throw new TypeLoadException($"Nested type '{name}' was not found in '{GetTypeName(containing)}'");
        }

        string @namespace = evaluationReader.GetString(reference.Namespace);
        if (reference.ResolutionScope.Kind == HandleKind.AssemblyReference)
        {
            string assemblyName = evaluationReader.GetString(evaluationReader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
            foreach (ModuleInfo module in FindModules((AssemblyReferenceHandle)reference.ResolutionScope))
            {
                if (TryFindTypeInModule(module, name, @namespace, out TypeDefinitionHandle typeHandle))
                {
                    return new ResolvedRuntimeType(module, typeHandle);
                }
            }
            throw new TypeLoadException($"Type '{@namespace}.{name}' from assembly '{assemblyName}' is not loaded");
        }

        foreach (ModuleInfo module in FindModules(assemblyName: null))
        {
            if (TryFindTypeInModule(module, name, @namespace, out TypeDefinitionHandle typeHandle))
            {
                return new ResolvedRuntimeType(module, typeHandle);
            }
        }
        throw new TypeLoadException($"Type '{@namespace}.{name}' is not loaded");
    }

    private static bool TryFindTypeInModule(ModuleInfo module, string name, string @namespace, out TypeDefinitionHandle handle)
    {
        MetadataReader reader = module.MetadataReader.PeMetadataReader;
        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) == name && reader.GetString(type.Namespace) == @namespace)
            {
                handle = typeHandle;
                return true;
            }
        }
        handle = default;
        return false;
    }

    /// <summary>The loaded modules an assembly reference of the evaluation assembly names, the one the evaluation was
    /// compiled against first (the frame's module, or the root value's for DebuggerDisplay): of several with one
    /// identity (one assembly in several load contexts), the one being debugged, which Roslyn bound the expression
    /// against.</summary>
    private IEnumerable<ModuleInfo> FindModules(AssemblyReferenceHandle assemblyReference)
    {
        AssemblyReference reference = evaluationReader.GetAssemblyReference(assemblyReference);
        string name = evaluationReader.GetString(reference.Name);
        Version version = reference.Version;
        string culture = evaluationReader.GetString(reference.Culture);
        byte[]? publicKeyOrToken = reference.PublicKeyOrToken.IsNil ? null : evaluationReader.GetBlobBytes(reference.PublicKeyOrToken);

        List<ModuleInfo> identityMatches = new List<ModuleInfo>();
        ModuleInfo? preferredIdentityMatch = null;
        foreach (ModuleInfo module in debugger.AllModules)
        {
            MetadataReader reader = module.MetadataReader.PeMetadataReader;
            if (!reader.IsAssembly)
            {
                continue;
            }

            AssemblyDefinition assembly = reader.GetAssemblyDefinition();
            if (reader.GetString(assembly.Name) != name)
            {
                continue;
            }

            if (!MatchesAssemblyIdentity(reader, assembly, version, culture, publicKeyOrToken))
            {
                continue;
            }

            if (module == currentFrameModule)
            {
                preferredIdentityMatch = module;
            }
            else
            {
                identityMatches.Add(module);
            }
        }
        if (preferredIdentityMatch is not null)
        {
            yield return preferredIdentityMatch;
        }

        foreach (ModuleInfo module in identityMatches)
        {
            yield return module;
        }

        // No loaded module has the identity (a redirect, another version): by simple name, the frame's module first.
        if (identityMatches.Count == 0 && preferredIdentityMatch is null)
        {
            foreach (ModuleInfo module in FindModules(name))
            {
                yield return module;
            }
        }
    }

    private static bool MatchesAssemblyIdentity(MetadataReader reader, AssemblyDefinition assembly, Version? version, string culture, byte[]? publicKeyOrToken)
    {
        if (version is not null && assembly.Version is not null && version != assembly.Version)
        {
            return false;
        }

        if (!string.Equals(culture, reader.GetString(assembly.Culture), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return PublicKeysMatch(publicKeyOrToken, assembly.PublicKey.IsNil ? null : reader.GetBlobBytes(assembly.PublicKey));
    }

    private static bool PublicKeysMatch(byte[]? referencedToken, byte[]? definitionKey)
    {
        if (referencedToken is null or { Length: 0 } || definitionKey is null or { Length: 0 })
        {
            return true;
        }

        if (referencedToken.Length == 8 && definitionKey.Length > 8)
        {
            // The AssemblyRef carries the public key token (the last 8 bytes of the SHA1 of the full public key).
            byte[] hash = SHA1.HashData(definitionKey);
            hash.AsSpan().Reverse();
            bool matches = referencedToken.AsSpan().SequenceEqual(hash.AsSpan(0, 8));
            return matches;
        }
        return referencedToken.AsSpan().SequenceEqual(definitionKey);
    }

    private IEnumerable<ModuleInfo> FindModules(string? assemblyName)
    {
        List<ModuleInfo> others = new List<ModuleInfo>();
        foreach (ModuleInfo module in debugger.AllModules)
        {
            MetadataReader reader = module.MetadataReader.PeMetadataReader;
            if (assemblyName is not null && (reader.IsAssembly == false || reader.GetString(reader.GetAssemblyDefinition().Name) != assemblyName))
            {
                continue;
            }
            if (module == currentFrameModule)
            {
                yield return module;
            }
            else
            {
                others.Add(module);
            }
        }
        foreach (ModuleInfo module in others)
        {
            yield return module;
        }
    }

    private ResolvedRuntimeType FindRuntimeType(string @namespace, string name)
    {
        string cacheKey = $"{@namespace}.{name}";
        if (_runtimeTypeCache.TryGetValue(cacheKey, out ResolvedRuntimeType cached))
        {
            return cached;
        }

        foreach (ModuleInfo module in FindModules(null))
        {
            MetadataReader reader = module.MetadataReader.PeMetadataReader;
            foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
            {
                TypeDefinition type = reader.GetTypeDefinition(handle);
                if (reader.GetString(type.Namespace) == @namespace && reader.GetString(type.Name) == name)
                {
                    ResolvedRuntimeType result = new ResolvedRuntimeType(module, handle);
                    _runtimeTypeCache[cacheKey] = result;
                    return result;
                }
            }
        }
        throw new TypeLoadException($"Type '{@namespace}.{name}' is not loaded");
    }

    internal static bool IsValueType(ResolvedRuntimeType type)
    {
        MetadataReader reader = type.Module.MetadataReader.PeMetadataReader;
        TypeDefinition definition = reader.GetTypeDefinition(type.Handle);
        return definition.BaseType.Kind switch
        {
            // CoreLib defines System.ValueType and System.Enum itself: its base type is a definition, not a reference.
            HandleKind.TypeDefinition => IsSystemValueType(reader, reader.GetTypeDefinition((TypeDefinitionHandle)definition.BaseType)),
            HandleKind.TypeReference => IsSystemValueType(reader, reader.GetTypeReference((TypeReferenceHandle)definition.BaseType)),
            _ => false
        };
    }

    private static bool IsSystemValueType(MetadataReader reader, TypeDefinition baseType) =>
        IsSystemValueType(reader, baseType.Namespace, baseType.Name);

    private static bool IsSystemValueType(MetadataReader reader, TypeReference baseType) =>
        IsSystemValueType(reader, baseType.Namespace, baseType.Name);

    private static bool IsSystemValueType(MetadataReader reader, StringHandle @namespace, StringHandle name) =>
        reader.GetString(@namespace) == "System" && reader.GetString(name) is "ValueType" or "Enum";

    private static string GetTypeName(ResolvedRuntimeType type) => MetadataTypeNames.Of(type.Module.MetadataReader.PeMetadataReader, type.Handle);

    private string GetReflectionTypeName(ResolvedCilType type)
    {
        if (type.ElementType is not null)
        {
            string suffix = type.IsSzArray ? "[]" : $"[{new string(',', type.ArrayRank - 1)}]";
            return GetReflectionTypeName(type.ElementType) + suffix;
        }
        if (type.Primitive is { } primitive)
        {
            return MetadataTypeNames.Of(primitive);
        }

        ResolvedRuntimeType runtimeType = type.RuntimeType ?? throw new TypeLoadException("The CIL type is unresolved");
        string name = GetTypeName(runtimeType);
        if (!runtimeType.TypeArguments.IsDefaultOrEmpty)
        {
            name += $"[[{string.Join("],[", runtimeType.TypeArguments.Select(GetAssemblyQualifiedTypeName))}]]";
        }
        return name;
    }

    private string GetTypeAssemblyName(ResolvedCilType type)
    {
        if (type.ElementType is not null)
        {
            return GetTypeAssemblyName(type.ElementType);
        }

        ModuleInfo module = type.Primitive is not null
            ? FindModules("System.Private.CoreLib").First()
            : type.RuntimeType?.Module ?? throw new TypeLoadException("The CIL type is unresolved");
        MetadataReader reader = module.MetadataReader.PeMetadataReader;
        if (!reader.IsAssembly)
        {
            return Path.GetFileNameWithoutExtension(module.Module.Name);
        }

        AssemblyDefinition definition = reader.GetAssemblyDefinition();
        AssemblyName assemblyName = new AssemblyName
        {
            Name = reader.GetString(definition.Name),
            Version = definition.Version,
            CultureName = definition.Culture.IsNil ? null : reader.GetString(definition.Culture)
        };
        if (!definition.PublicKey.IsNil)
        {
            assemblyName.SetPublicKey(reader.GetBlobBytes(definition.PublicKey));
        }

        return assemblyName.FullName!;
    }

    private sealed class RuntimeTypeSignatureProvider(EvaluationMetadataResolver resolver) : ISignatureTypeProvider<ResolvedCilType, object?>
    {
        public ResolvedCilType GetPrimitiveType(PrimitiveTypeCode typeCode) => new(typeCode, null);
        public ResolvedCilType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            new(null, resolver.ResolveType(handle));
        public ResolvedCilType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            new(null, resolver.ResolveType(handle));
        public ResolvedCilType GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public ResolvedCilType GetGenericInstantiation(ResolvedCilType genericType, ImmutableArray<ResolvedCilType> typeArguments) =>
            genericType.RuntimeType is { } runtimeType
                ? genericType with { RuntimeType = runtimeType with { TypeArguments = typeArguments } }
                : throw new TypeLoadException("A generic instantiation must identify a runtime type");
        public ResolvedCilType GetArrayType(ResolvedCilType elementType, ArrayShape shape) => new(null, null, elementType, shape.Rank);
        public ResolvedCilType GetSZArrayType(ResolvedCilType elementType) => new(null, null, elementType, 1, true);
        public ResolvedCilType GetByReferenceType(ResolvedCilType elementType) => elementType;
        public ResolvedCilType GetPointerType(ResolvedCilType elementType) => elementType;
        public ResolvedCilType GetPinnedType(ResolvedCilType elementType) => elementType;
        public ResolvedCilType GetModifiedType(ResolvedCilType modifier, ResolvedCilType unmodifiedType, bool isRequired) => unmodifiedType;
        public ResolvedCilType GetFunctionPointerType(MethodSignature<ResolvedCilType> signature) => throw new NotSupportedException("Function pointer types are not supported");
        public ResolvedCilType GetGenericMethodParameter(object? genericContext, int index) => resolver.ResolveGenericMethodParameter(index);
        public ResolvedCilType GetGenericTypeParameter(object? genericContext, int index) => resolver.ResolveGenericTypeParameter(index);
    }
}
