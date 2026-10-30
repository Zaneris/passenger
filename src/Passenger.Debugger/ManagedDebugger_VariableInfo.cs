using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models.Response;
using Passenger.Debugger.PresentationHintModels;
using ZLinq;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    private async Task AddLocalVariables(ModuleInfo module, ICorDebugFunction corDebugFunction, List<VariableInfo> result, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue? classContainingHoistedLocalsValue)
    {
        if (classContainingHoistedLocalsValue is not null)
        {
            // Captured variables are fields of the closure chain; the lambda's own locals are still read below.
            await AddClosureChainMembers(classContainingHoistedLocalsValue, threadId, stackDepth, result);
        }
        ICorDebugILFrame corDebugIlFrame = GetIlFrameForThreadIdAndStackDepth(threadId, stackDepth);
        if (corDebugIlFrame.LocalVariables.Length is 0)
        {
            return;
        }

        int currentIlOffset = corDebugIlFrame.IP.pnOffset;
        foreach ((int index, ICorDebugValue? localVariableCorDebugValue) in corDebugIlFrame.LocalVariables.Index())
        {
            string? localVariableName = module.MetadataReader.GetLocalVariableName(corDebugFunction.Token, index, currentIlOffset, ModuleMetadataReader.VersionOf(corDebugIlFrame));
            if (localVariableName is null)
            {
                continue; // Compiler generated locals will not be found. E.g. DefaultInterpolatedStringHandler
            }

            if (IsLambdaDisplayClass(localVariableCorDebugValue))
            {
                await AddClosureChainMembers(localVariableCorDebugValue, threadId, stackDepth, result);
                continue;
            }
            await WithFailureHandling(result, localVariableName, async () =>
            {
                (string? friendlyTypeName, string? value, ICorDebugValue? debuggerProxyInstance, bool resultIsError) = await GetValueForCorDebugValueAsync(localVariableCorDebugValue, threadId, stackDepth, true);
                VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
                result.Add(new VariableInfo
                {
                    Name = localVariableName,
                    Value = value,
                    Type = friendlyTypeName,
                    PresentationHint = variablePresentationHint,
                    VariablesReference = GetVariablesReference(localVariableCorDebugValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance, localVariableName)
                });
            });
        }
    }

    private static bool IsLambdaDisplayClass(ICorDebugValue value)
    {
        ICorDebugType type = value.ExactType;
        if (type.Type is not CorElementType.CLASS)
        {
            return false;
        }

        IMetaDataImport metadataImport = type.Class.Module.GetMetaDataInterface<IMetaDataImport>();
        string typeName = metadataImport.GetTypeDefProps(type.Class.Token).szTypeDef;
        return GeneratedNameParser.GetKind(typeName) is GeneratedNameKind.LambdaDisplayClass;
    }

    /// Walks the compiler-generated closure chain starting at <paramref name="closureValue"/>,
    /// calling AddMembers on each closure class. Parent closures are linked via a field of
    /// kind <see cref="GeneratedNameKind.DisplayClassLocalOrField"/> (e.g. "&lt;&gt;8__1").
    private async Task AddClosureChainMembers(ICorDebugValue closureValue, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result)
    {
        // A captured variable's closure copy is the live one, in place of its own slot, which goes stale.
        List<VariableInfo> members = [];
        await AddMembers(closureValue, closureValue.ExactType, threadId, stackDepth, members);
        foreach (VariableInfo member in members)
        {
            int existing = result.FindIndex(v => v.Name == member.Name);
            if (existing >= 0)
            {
                result[existing] = member;
            }
            else
            {
                result.Add(member);
            }
        }

        ICorDebugObjectValue objectValue = closureValue.UnwrapDebugValueToObject();
        IMetaDataImport metadataImport = objectValue.Class.Module.GetMetaDataInterface<IMetaDataImport>();
        IEnumerable<mdFieldDef> fields = metadataImport.EnumFields(objectValue.Class.Token);
        foreach (mdFieldDef field in fields)
        {
            (string szField, mdTypeDef pClass, CorFieldAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, CorElementType pdwCPlusTypeFlag, nint ppValue, int pcchValue) fieldProps = metadataImport.GetFieldProps(field);
            if (GeneratedNameParser.GetKind(fieldProps.szField) is GeneratedNameKind.DisplayClassLocalOrField)
            {
                ICorDebugValue parentClosureValue = objectValue.GetFieldValue(objectValue.Class, field);
                await AddClosureChainMembers(parentClosureValue, threadId, stackDepth, result);
                break; // only one parent link per closure class
            }
        }
    }

    /// Returns classContainingHoistedLocalsValue if applicable
    private async Task<ICorDebugValue?> AddArguments(ModuleInfo module, ICorDebugFunction corDebugFunction, List<VariableInfo> result, ThreadId threadId, FrameStackDepth stackDepth)
    {
        ICorDebugILFrame corDebugIlFrame = GetIlFrameForThreadIdAndStackDepth(threadId, stackDepth);
        ICorDebugValue[] arguments = corDebugIlFrame.Arguments;
        if (arguments.Length is 0)
        {
            return null;
        }

        IMetaDataImport metadataImport = module.Module.GetMetaDataInterface<IMetaDataImport>();

        // Frame.Arguments has an instance method's 'this', which GetParamForMethodIndex leaves out.
        (string szMethod, mdTypeDef pClass, CorMethodAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, int pulCodeRVA, int pdwImplFlags) methodProps = metadataImport!.GetMethodProps(corDebugFunction.Token);
        bool isStatic = methodProps.pdwAttr.IsMdStatic();
        ICorDebugValue? classContainingHoistedLocalsValue = null;
        if (isStatic is false)
        {
            string methodName = methodProps.szMethod;
            ICorDebugValue? implicitThisValue = arguments[0];
            if (methodName is "MoveNext" || methodName.Contains(">b")) // async or lambda
            {
                string containingClassName = metadataImport.GetTypeDefProps(corDebugFunction.Class.Token).szTypeDef;
                GeneratedNameKind classGeneratedNameKind = GeneratedNameParser.GetKind(containingClassName);
                if (classGeneratedNameKind is GeneratedNameKind.StateMachineType or GeneratedNameKind.LambdaDisplayClass)
                {
                    // 'this' is a generated class holding the user's 'this' and the hoisted locals, so it is returned.
                    classContainingHoistedLocalsValue = implicitThisValue;
                    // Null for a static user method, whose MoveNext has a 'this' but no proxy field for one.
                    implicitThisValue = GetAsyncOrLambdaProxyFieldValue(implicitThisValue, metadataImport);
                }
            }
            if (implicitThisValue is not null)
            {
                await WithFailureHandling(result, "this", async () =>
                {
                    (string? friendlyTypeName, string? value, ICorDebugValue? debuggerProxyInstance, bool resultIsError) = await GetValueForCorDebugValueAsync(implicitThisValue, threadId, stackDepth, true);
                    VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
                    result.Add(new VariableInfo
                    {
                        Name = "this", // Hardcoded - 'this' has no metadata
                        Value = value,
                        Type = friendlyTypeName,
                        PresentationHint = variablePresentationHint,
                        VariablesReference = GetVariablesReference(implicitThisValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance)
                    });
                });
            }
        }
        int skipCount = isStatic ? 0 : 1; // Skip 'this' for instance methods, as we already handled it
        foreach ((int index, ICorDebugValue? argumentCorDebugValue) in arguments.Skip(skipCount).Index())
        {
            // Index 0 is the return value; a parameter without a row (emitted code) is named by its place.
            string? argumentName = metadataImport!.TryGetParamForMethodIndex(corDebugFunction.Token, checked((uint)(index + 1)), out mdParamDef paramDef) is Cor.S_OK
                ? metadataImport.GetParamProps(paramDef).szName
                : $"arg_{index}";
            if (argumentName is null)
            {
                continue;
            }

            await WithFailureHandling(result, argumentName, async () =>
            {
                (string? friendlyTypeName, string? value, ICorDebugValue? debuggerProxyInstance, bool resultIsError) = await GetValueForCorDebugValueAsync(argumentCorDebugValue, threadId, stackDepth, true);
                VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
                result.Add(new VariableInfo
                {
                    Name = argumentName,
                    Value = value,
                    Type = friendlyTypeName,
                    PresentationHint = variablePresentationHint,
                    VariablesReference = GetVariablesReference(argumentCorDebugValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance, argumentName)
                });
            });
        }
        return classContainingHoistedLocalsValue;
    }

    private async Task AddCurrentException(List<VariableInfo> result, ThreadId threadId, FrameStackDepth stackDepth)
    {
        ICorDebugThread? thread = _threads.GetValueOrDefault(threadId.Value)?.Thread;
        ArgumentNullException.ThrowIfNull(thread);
        thread.TryGetCurrentException(out ICorDebugValue? currentException);
        if (currentException is not null)
        {
            await WithFailureHandling(result, "$exception", async () =>
            {
                (string? friendlyTypeName, string? value, ICorDebugValue? debuggerProxyInstance, bool resultIsError) = await GetValueForCorDebugValueAsync(currentException, threadId, stackDepth, true);
                VariablePresentationHint? presentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
                result.Add(new VariableInfo
                {
                    Name = "$exception",
                    Value = value,
                    Type = friendlyTypeName,
                    PresentationHint = presentationHint,
                    VariablesReference = GetVariablesReference(currentException, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance)
                });
            });
        }
    }

    /// <summary>The reference a value's row opens by, 0 for one that opens to nothing: a union opens to the value it holds,
    /// and a pointer (<paramref name="name"/> its row's) to the value it points at.</summary>
    private int GetVariablesReference(ICorDebugValue corDebugValue, string friendlyTypeName, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue? debuggerProxyInstance, string? name = null)
    {
        if (corDebugValue is ICorDebugReferenceValue { Type: CorElementType.PTR } pointer)
        {
            return pointer.IsNull || pointer.ExactType.FirstTypeParameter.Type is CorElementType.VOID ? 0
                : _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.Pointer, pointer, threadId, stackDepth, null, Name: name));
        }
        if (TryGetUnionValue(corDebugValue, out _, out ICorDebugValue? held))
        {
            if (held is null)
            {
                return 0;
            }
            corDebugValue = held;
        }
        ICorDebugValue unwrappedDebugValue = corDebugValue.UnwrapDebugValue();
        if (unwrappedDebugValue is ICorDebugArrayValue arrayValue)
        {
            if (arrayValue.Count is 0)
            {
                return 0;
            }

            return GenerateUniqueVariableReference(corDebugValue, threadId, stackDepth, debuggerProxyInstance);
        }
        else if (unwrappedDebugValue is ICorDebugObjectValue objectValue)
        {
            if (TryGetNullableUnderlyingValue(corDebugValue, out _, out ICorDebugValue? underlyingValueOrNull))
            {
                if (underlyingValueOrNull is null)
                {
                    return 0;
                }

                if (underlyingValueOrNull.UnwrapDebugValue() is not ICorDebugObjectValue objValue)
                {
                    return 0; // underlying value is primitive
                }

                objectValue = objValue;
            }

            CorElementType type = objectValue.Type;
            // Strings are objects but typically displayed as primitives
            if (type is CorElementType.STRING)
            {
                return 0;
            }
            // Decimal is a struct but should be treated as a primitive
            if (friendlyTypeName is "decimal" or "decimal?")
            {
                return 0;
            }
            // a boxed primitive is CorElementType.VALUETYPE but should be displayed as a primitive. They can never be nullable.
            if (friendlyTypeName is "bool" or "byte" or "sbyte" or "char" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double" or "nint" or "nuint")
            {
                return 0;
            }

            if (type is CorElementType.CLASS or CorElementType.VALUETYPE or CorElementType.SZARRAY or CorElementType.ARRAY)
            {
                return GenerateUniqueVariableReference(corDebugValue, threadId, stackDepth, debuggerProxyInstance);
            }
        }
        return 0;
    }

    /// <summary>The one row a pointer opens to: the value it points at, named as C# reads it (*p).</summary>
    private async Task AddPointedAt(VariablesReference pointer, List<VariableInfo> result)
    {
        ICorDebugValue pointedAt = ((ICorDebugReferenceValue)pointer.ObjectValue!).Dereference();
        string name = pointer.Name is { } named ? (SyntaxFacts.IsValidIdentifier(named) ? $"*{named}" : $"*({named})") : "*";
        await WithFailureHandling(result, name, async () =>
        {
            (string friendlyTypeName, string value, ICorDebugValue? proxy, bool resultIsError) = await GetValueForCorDebugValueAsync(pointedAt, pointer.ThreadId, pointer.FrameStackDepth, true);
            result.Add(new VariableInfo
            {
                Name = name,
                Value = value,
                Type = friendlyTypeName,
                PresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null,
                VariablesReference = GetVariablesReference(pointedAt, friendlyTypeName, pointer.ThreadId, pointer.FrameStackDepth, proxy, name)
            });
        });
    }

    private int GenerateUniqueVariableReference(ICorDebugValue value, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue? debuggerProxyInstance)
    {
        VariablesReference variablesReference = new VariablesReference(StoredReferenceKind.StackVariable, value, threadId, stackDepth, debuggerProxyInstance);
        int reference = _variableManager.CreateReference(variablesReference);
        return reference;
    }

    private async Task AddMembersAndStaticPseudoVariable(ICorDebugValue corDebugValue, ICorDebugType corDebugType, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result, bool includeNonPublicMembers = true)
    {
        bool requiresStaticPseudoVariable = await AddMembers(corDebugValue, corDebugType, threadId, stackDepth, result, includeNonPublicMembers);
        if (requiresStaticPseudoVariable)
        {
            VariableInfo variableInfo = new VariableInfo
            {
                Name = "Static members",
                Value = "",
                Type = "",
                PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Class },
                VariablesReference = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.StaticClassVariable, corDebugValue, threadId, stackDepth, null))
            };
            result.Add(variableInfo);
        }
    }

    private void AddEnumerablePseudoVariables(VariablesReference variablesReference, List<VariableInfo> result)
    {
        result.Add(new VariableInfo
        {
            Name = "Raw View",
            Value = "",
            Type = "",
            PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Class },
            VariablesReference = _variableManager.CreateReference(variablesReference with { ReferenceKind = StoredReferenceKind.RawView })
        });
        result.Add(new VariableInfo
        {
            Name = "Results",
            Value = "Expanding will force enumeration of the object",
            Type = "",
            PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Class },
            VariablesReference = _variableManager.CreateReference(variablesReference with { ReferenceKind = StoredReferenceKind.EnumerableResults })
        });
    }

    private bool IsEnumerable(ICorDebugType type) => Implements(type, static name =>
        name is "System.Collections.IEnumerable" || name.StartsWith("System.Collections.Generic.IEnumerable`1<", StringComparison.Ordinal));

    /// <summary>Whether a type, or a type it derives from, implements an interface its full name says (a generic one's with
    /// its arguments in angle brackets).</summary>
    private bool Implements(ICorDebugType type, Func<string, bool> named)
    {
        for (ICorDebugType? currentType = type; currentType is not null; currentType = currentType.Base)
        {
            IMetaDataImport metadataReader = currentType.Class.Module.GetMetaDataInterface<IMetaDataImport>();
            foreach (mdInterfaceImpl interfaceImpl in metadataReader.EnumInterfaceImpls(currentType.Class.Token))
            {
                mdToken interfaceToken = metadataReader.GetInterfaceImplProps(interfaceImpl).ptkIface;
                Handle interfaceHandle = MetadataTokens.Handle(checked((int)interfaceToken.Value));
                MetadataReader peMetadataReader = GetModuleInfoForModule(currentType.Class.Module).MetadataReader.PeMetadataReader;
                string? interfaceName = interfaceHandle.Kind switch
                {
                    HandleKind.TypeDefinition => MetadataTypeNames.Of(peMetadataReader, (TypeDefinitionHandle)interfaceHandle, '.'),
                    HandleKind.TypeReference => MetadataTypeNames.Of(peMetadataReader, (TypeReferenceHandle)interfaceHandle, '.'),
                    HandleKind.TypeSpecification => peMetadataReader.GetTypeSpecification((TypeSpecificationHandle)interfaceHandle).DecodeSignature(SignatureKeyProvider.Dotted, null),
                    _ => null
                };
                if (interfaceName is not null && named(interfaceName))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private async Task AddEnumerableResults(VariablesReference variablesReference, List<VariableInfo> result)
    {
        ModuleInfo? linqModule = _modules.Values.FirstOrDefault(module => module.ModuleName is "System.Linq.dll");
        if (linqModule is null)
        {
            throw new InvalidOperationException("System.Linq is not loaded");
        }

        ICorDebugValue proxyInstance = await CreateDebuggerProxyInstance(variablesReference.ObjectValue!, variablesReference.ThreadId, linqModule.Module, "System.Linq.SystemCore_EnumerableDebugView", [])
            ?? throw new InvalidOperationException("Enumerating the value threw");
        try
        {
            ICorDebugObjectValue proxyObject = proxyInstance.UnwrapDebugValueToObject();
            await AddMembers(proxyInstance, proxyObject.ExactType, variablesReference.ThreadId, variablesReference.FrameStackDepth, result, false);
        }
        finally
        {
            if (proxyInstance is ICorDebugHandleValue handle)
            {
                handle.TryDispose();
            }
        }
    }

    /// <summary>A type's members and then its base types', each base's member that one derived from it hides by its name
    /// (<paramref name="derived"/>, the names they took) saying its type; true where a Static members row is needed.</summary>
    private async Task<bool> AddMembers(ICorDebugValue corDebugValue, ICorDebugType corDebugType, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result, bool includeNonPublicMembers = true, HashSet<string>? derived = null)
    {
        int first = result.Count;
        bool hasStaticMembers = false;
        ICorDebugClass corDebugClass = corDebugType.Class;
        ICorDebugModule module = corDebugClass.Module;
        mdTypeDef mdTypeDef = corDebugClass.Token;
        IMetaDataImport metadataImport = module.GetMetaDataInterface<IMetaDataImport>();
        IEnumerable<mdFieldDef> mdFieldDefs = includeNonPublicMembers ? metadataImport.EnumFields(mdTypeDef) : metadataImport.EnumFields(mdTypeDef).AsValueEnumerable().Where(s => s.IsPublic(metadataImport)).ToArray();
        IEnumerable<mdProperty> mdProperties = includeNonPublicMembers ? metadataImport.EnumProperties(mdTypeDef) : metadataImport.EnumProperties(mdTypeDef).AsValueEnumerable().Where(s => s.IsPublic(metadataImport)).ToArray();
        mdFieldDef[] staticFieldDefs = mdFieldDefs.AsValueEnumerable().Where(s => s.IsStatic(metadataImport)).ToArray();
        mdFieldDef[] nonStaticFieldDefs = mdFieldDefs.AsValueEnumerable().Except(staticFieldDefs).ToArray();
        mdProperty[] staticProperties = mdProperties.AsValueEnumerable().Where(p => p.IsStatic(metadataImport)).ToArray();
        mdProperty[] nonStaticProperties = mdProperties.AsValueEnumerable().Except(staticProperties).ToArray();
        if (staticFieldDefs.Length > 0 || staticProperties.Length > 0)
        {
            hasStaticMembers = true;
        }

        await AddFields(nonStaticFieldDefs, metadataImport, corDebugType, corDebugValue, result, threadId, stackDepth);
        // The reference itself, not unwrapped: the getters are called on it.
        await AddProperties(nonStaticProperties, metadataImport, corDebugType, threadId, stackDepth, corDebugValue, result);
        if (derived is not null)
        {
            foreach (VariableInfo hidden in result.Skip(first).Where(member => derived.Contains(member.Name)))
            {
                hidden.DeclaringType = GetCorDebugTypeFriendlyName(corDebugType);
            }
        }

        ICorDebugType? baseType = corDebugType.Base;
        if (baseType is null)
        {
            return hasStaticMembers;
        }

        string baseTypeName = GetCorDebugTypeFriendlyName(baseType);
        if (baseTypeName is "System.Object" or "System.ValueType" or "System.Enum")
        {
            return hasStaticMembers;
        }

        HashSet<string> taken = derived is null ? new(StringComparer.Ordinal) : new(derived, StringComparer.Ordinal);
        taken.UnionWith(result.Skip(first).Select(member => member.Name));
        return hasStaticMembers | await AddMembers(corDebugValue, baseType, threadId, stackDepth, result, derived: taken);
    }

    private async Task AddStaticMembers(ICorDebugValue corDebugValue, ICorDebugType corDebugType, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result)
    {
        ICorDebugClass corDebugClass = corDebugType.Class;
        ICorDebugModule module = corDebugClass.Module;
        mdTypeDef mdTypeDef = corDebugClass.Token;
        IMetaDataImport metadataImport = module.GetMetaDataInterface<IMetaDataImport>();
        mdFieldDef[] staticFieldDefs = metadataImport.EnumFields(mdTypeDef).AsValueEnumerable().Where(s => s.IsStatic(metadataImport)).ToArray();
        mdProperty[] staticProperties = metadataImport.EnumProperties(mdTypeDef).AsValueEnumerable().Where(s => s.IsStatic(metadataImport)).ToArray();

        await AddFields(staticFieldDefs, metadataImport, corDebugType, corDebugValue, result, threadId, stackDepth);
        // The reference itself, not unwrapped: the getters are called on it.
        await AddProperties(staticProperties, metadataImport, corDebugType, threadId, stackDepth, corDebugValue, result);

        ICorDebugType? baseType = corDebugType.Base;
        if (baseType is null)
        {
            return;
        }

        string baseTypeName = GetCorDebugTypeFriendlyName(baseType);
        if (baseTypeName is "System.Object" or "System.ValueType" or "System.Enum")
        {
            return;
        }

        await AddStaticMembers(corDebugValue, baseType, threadId, stackDepth, result);
    }

    private async Task AddFields(mdFieldDef[] mdFieldDefs, IMetaDataImport metadataImport, ICorDebugType corDebugType, ICorDebugValue corDebugValue, List<VariableInfo> result, ThreadId threadId, FrameStackDepth stackDepth)
    {
        ICorDebugClass corDebugClass = corDebugType.Class;
        foreach (mdFieldDef mdFieldDef in mdFieldDefs)
        {
            (string szField, mdTypeDef pClass, CorFieldAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, CorElementType pdwCPlusTypeFlag, nint ppValue, int pcchValue) fieldProps = metadataImport.GetFieldProps(mdFieldDef);
            string? fieldName = fieldProps.szField;
            if (fieldName is null)
            {
                continue;
            }

            await WithFailureHandling(result, fieldName, async () =>
            {
                GeneratedNameParser.TryParseGeneratedName(fieldName, out GeneratedNameKind generatedNameKind, out int openBracketOffset, out int closeBracketOffset);
                if (generatedNameKind is GeneratedNameKind.HoistedLocalField)
                {
                    // A hoisted local's field is named "<intVar>5__1": the user's name is between the brackets.
                    ReadOnlySpan<char> originalLocalVariableName = fieldName.AsSpan()[(openBracketOffset + 1)..closeBracketOffset];
                    fieldName = originalLocalVariableName.ToString();
                }
                else if (generatedNameKind is not GeneratedNameKind.None)
                {
                    return;
                }
                bool isStatic = fieldProps.pdwAttr.IsFdStatic();
                bool isLiteral = fieldProps.pdwAttr.IsFdLiteral();
                bool debuggerBrowsableRootHidden = false;
                bool hasDebuggerBrowsableAttribute = metadataImport.TryGetCustomAttributeByName(mdFieldDef, "System.Diagnostics.DebuggerBrowsableAttribute", out nint debuggerBrowsableAttributePointer, out uint debuggerBrowsableAttributeSize) is Cor.S_OK;
                if (hasDebuggerBrowsableAttribute)
                {
                    // https://github.com/Samsung/netcoredbg/blob/6476bc00c2beaab9255c750235a68de3a3d0cfae/src/debugger/evaluator.cpp#L913
                    DebuggerBrowsableState debuggerBrowsableState = (DebuggerBrowsableState)GetDebuggerBrowsableCustomAttributeResultInt(debuggerBrowsableAttributePointer, debuggerBrowsableAttributeSize);
                    if (debuggerBrowsableState == DebuggerBrowsableState.Never)
                    {
                        return;
                    }

                    if (debuggerBrowsableState == DebuggerBrowsableState.RootHidden)
                    {
                        debuggerBrowsableRootHidden = true;
                    }
                }
                if (isLiteral)
                {
                    object? literalValue = GetLiteralValue(fieldProps.ppValue, fieldProps.pdwCPlusTypeFlag, fieldProps.pcchValue);
                    (string FriendlyName, ModuleInfo Module, EntityHandle Handle)? fieldType = GetFieldType(corDebugClass, mdFieldDef);
                    string? fieldTypeName = fieldType?.FriendlyName ?? GetFriendlyTypeName(fieldProps.pdwCPlusTypeFlag);
                    string literalValueFormatted = literalValue switch
                    {
                        null => "null",
                        bool b => b.ToString().ToLowerInvariant(),
                        char c => $"{(int)c} '{c}'",
                        string str => SymbolDisplay.FormatLiteral(str, quote: true),
                        _ => literalValue.ToString()!
                    };
                    if (fieldType is { } declaredType && TryGetEnumDisplayValue(declaredType, literalValueFormatted, out string? enumDisplayValue))
                    {
                        literalValueFormatted = enumDisplayValue;
                    }
                    VariableInfo literalVariableInfo = new VariableInfo
                    {
                        Name = fieldName,
                        Value = literalValueFormatted,
                        Type = fieldTypeName,
                        VariablesReference = 0
                    };
                    result.Add(literalVariableInfo);
                    return;
                }

                ICorDebugObjectValue objectValue = corDebugValue.UnwrapDebugValueToObject();
                ICorDebugValue fieldCorDebugValue = isStatic ? await GetStaticFieldValueAsync(corDebugType, mdFieldDef, threadId, stackDepth) : objectValue.GetFieldValue(corDebugClass, mdFieldDef);
                if (debuggerBrowsableRootHidden)
                {
                    ICorDebugValue unwrappedDebugValue = fieldCorDebugValue.UnwrapDebugValue();
                    if (unwrappedDebugValue is ICorDebugArrayValue arrayValue)
                    {
                        await AddArrayElements(arrayValue, threadId, stackDepth, result, referenceValue: fieldCorDebugValue);
                        return;
                    }
                    if (unwrappedDebugValue is ICorDebugObjectValue and not ICorDebugStringValue)
                    {
                        await AddRootHiddenObject(fieldCorDebugValue, threadId, stackDepth, result);
                        return;
                    }
                }
                (string? friendlyTypeName, string? value, ICorDebugValue? debuggerProxyInstance, bool resultIsError, string? displayName) = await GetDisplayedValueAsync(fieldCorDebugValue, threadId, stackDepth, true);
                VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
                VariableInfo variableInfo = new VariableInfo
                {
                    Name = displayName ?? fieldName,
                    Value = value,
                    Type = friendlyTypeName,
                    PresentationHint = variablePresentationHint,
                    VariablesReference = GetVariablesReference(fieldCorDebugValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance, fieldName)
                };
                result.Add(variableInfo);
            });
        }
    }

    private (string FriendlyName, ModuleInfo Module, EntityHandle Handle)? GetFieldType(ICorDebugClass corDebugClass, mdFieldDef mdFieldDef)
    {
        if (FindModule(corDebugClass.Module) is not { } moduleInfo)
        {
            return null;
        }

        Handle handle = MetadataTokens.Handle(mdFieldDef);
        if (handle.Kind is not HandleKind.FieldDefinition)
        {
            return null;
        }

        MetadataReader reader = moduleInfo.MetadataReader.PeMetadataReader;
        FieldDefinition field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
        string typeName = field.DecodeSignature(SignatureKeyProvider.Dotted, null);
        EntityHandle typeHandle = field.DecodeSignature(FieldTypeHandleProvider.Instance, null);
        return (ClassNameToMaybeLanguageAlias(typeName), moduleInfo, typeHandle);
    }

    internal class EvalException(string message) : Exception(message);

    /// <summary>An evaluation the runtime would not start on the thread where it is stopped (a pause can catch it in
    /// the runtime's optimized or native code): nothing ran. Its HResult is the runtime's.</summary>
    internal sealed class EvalRefusedException : EvalException
    {
        public EvalRefusedException(System.Runtime.InteropServices.COMException refused) : base(Said(refused)) => HResult = refused.HResult;

        private static string Said(System.Runtime.InteropServices.COMException refused) => refused.HResult switch
        {
            Cor.CORDBG_E_ILLEGAL_IN_OPTIMIZED_CODE => "Cannot evaluate expression because the code of the current method is optimized",
            Cor.CORDBG_E_ILLEGAL_AT_GC_UNSAFE_POINT => "Cannot evaluate expression because a thread is stopped at a point where garbage collection is impossible, possibly because the code is optimized",
            _ => $"Cannot evaluate expression where the thread is stopped: {refused.Message}",
        };
    }

    /// <summary>The properties <paramref name="declaringType"/> declares, each read by its getter called with the type
    /// arguments the value's type gives that type (a closed generic base's own), never the value's type's.</summary>
    private async Task AddProperties(mdProperty[] mdProperties, IMetaDataImport metadataImport, ICorDebugType declaringType, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue corDebugValue, List<VariableInfo> result)
    {
        ICorDebugClass corDebugClass = declaringType.Class;
        foreach (mdProperty mdProperty in mdProperties)
        {
            (string szProperty, mdTypeDef pClass, CorPropertyAttr pdwPropFlags, nint ppvSig, int pbSig, CorElementType pdwCPlusTypeFlag, nint ppDefaultValue, int pcchDefaultValue, mdMethodDef pmdSetter, mdMethodDef pmdGetter, mdMethodDef rmdOtherMethod, int pcOtherMethod) propertyProps = metadataImport.GetPropertyProps(mdProperty);
            string? propertyName = propertyProps.szProperty;
            if (propertyName is null)
            {
                continue;
            }

            await WithFailureHandling(result, propertyName, async () =>
            {
                ICorDebugILFrame variablesReferenceIlFrame = GetIlFrameForThreadIdAndStackDepth(threadId, stackDepth);

                mdMethodDef getMethodDef = propertyProps.pmdGetter;
                if (getMethodDef == 0)
                {
                    return;
                }

                (string szMethod, mdTypeDef pClass, CorMethodAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, int pulCodeRVA, int pdwImplFlags) getterMethodProps = metadataImport.GetMethodProps(getMethodDef);
                CorMethodAttr getterAttr = getterMethodProps.pdwAttr;

                bool isStatic = getterAttr.IsMdStatic();

                bool debuggerBrowsableRootHidden = false;
                bool hasDebuggerBrowsableAttribute = metadataImport.TryGetCustomAttributeByName(mdProperty, "System.Diagnostics.DebuggerBrowsableAttribute", out nint debuggerBrowsableAttributePointer, out uint debuggerBrowsableAttributeSize) is Cor.S_OK;
                if (hasDebuggerBrowsableAttribute)
                {
                    // https://github.com/Samsung/netcoredbg/blob/6476bc00c2beaab9255c750235a68de3a3d0cfae/src/debugger/evaluator.cpp#L913
                    DebuggerBrowsableState debuggerBrowsableState = (DebuggerBrowsableState)GetDebuggerBrowsableCustomAttributeResultInt(debuggerBrowsableAttributePointer, debuggerBrowsableAttributeSize);
                    if (debuggerBrowsableState == DebuggerBrowsableState.Never)
                    {
                        return;
                    }

                    if (debuggerBrowsableState == DebuggerBrowsableState.RootHidden)
                    {
                        debuggerBrowsableRootHidden = true;
                    }
                }

                ICorDebugFunction getMethod = corDebugClass.Module.GetFunctionFromToken(getMethodDef);
                ICorDebugEval eval = variablesReferenceIlFrame.Chain.Thread.CreateEval();

                ICorDebugType[] typeParameterTypes = declaringType.TypeParameters;

                ICorDebugValue[] corDebugValues = isStatic ? [] : [corDebugValue];

                ICorDebugValue? returnValue = await eval.CallParameterizedFunctionAsync(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, getMethod, typeParameterTypes.Length, typeParameterTypes, corDebugValues.Length, corDebugValues);

                if (returnValue is null)
                {
                    return;
                }

                bool retainReturnValue = false;
                try
                {
                    if (debuggerBrowsableRootHidden)
                    {
                        ICorDebugValue unwrappedDebugValue = returnValue.UnwrapDebugValue();
                        if (unwrappedDebugValue is ICorDebugArrayValue arrayValue)
                        {
                            retainReturnValue = await AddArrayElements(arrayValue, threadId, stackDepth, result, referenceValue: returnValue);
                            return;
                        }
                        if (unwrappedDebugValue is ICorDebugObjectValue and not ICorDebugStringValue)
                        {
                            // Its children are read from it later, as they open.
                            retainReturnValue = true;
                            await AddRootHiddenObject(returnValue, threadId, stackDepth, result);
                            return;
                        }
                    }
                    (string? friendlyTypeName, string? value, ICorDebugValue? debuggerProxyInstance, bool resultIsError, string? displayName) = await GetDisplayedValueAsync(returnValue, threadId, stackDepth, true);
                    VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
                    int variablesReference = GetVariablesReference(returnValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance);
                    VariableInfo variableInfo = new VariableInfo
                    {
                        Name = displayName ?? propertyName,
                        Value = value,
                        Type = friendlyTypeName,
                        PresentationHint = variablePresentationHint,
                        VariablesReference = variablesReference
                    };
                    retainReturnValue = variablesReference != 0;
                    result.Add(variableInfo);
                }
                finally
                {
                    if (!retainReturnValue && returnValue is ICorDebugHandleValue handle)
                    {
                        handle.TryDispose();
                    }
                }
            });
        }
    }

    /// <summary>A RootHidden member that is an object, not an array: its children in its place, as they would show
    /// under it, its proxy's if it has one.</summary>
    private async Task AddRootHiddenObject(ICorDebugValue value, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result)
    {
        (_, _, ICorDebugValue? proxy, _, _) = await GetDisplayedValueAsync(value, threadId, stackDepth, true);
        if (proxy is not null)
        {
            await AddMembers(proxy, proxy.UnwrapDebugValueToObject().ExactType, threadId, stackDepth, result, includeNonPublicMembers: false);
        }
        else
        {
            await AddMembers(value, value.UnwrapDebugValueToObject().ExactType, threadId, stackDepth, result);
        }
    }

    private async Task<bool> AddArrayElements(ICorDebugArrayValue arrayValue, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result, uint[]? indexPrefix = null, uint? startOffset = null, uint? count = null, ICorDebugValue? referenceValue = null)
    {
        int rank = arrayValue.Rank;
        indexPrefix ??= [];
        uint[] dimensions = arrayValue.GetDimensions(rank);
        uint[] baseIndices = arrayValue.HasBaseIndicies() ? arrayValue.GetBaseIndicies(rank) : new uint[rank];
        int currentDimension = indexPrefix.Length;
        uint currentDimensionStart = baseIndices[currentDimension];
        uint currentDimensionLength = dimensions[currentDimension];

        if (currentDimension < rank - 1)
        {
            for (uint offset = 0u; offset < currentDimensionLength; offset++)
            {
                uint[] indices = [.. indexPrefix, currentDimensionStart + offset];
                string name = $"[{string.Join(", ", indices)}, ...]";
                result.Add(new VariableInfo
                {
                    Name = name,
                    Value = "",
                    Type = "",
                    PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Class },
                    VariablesReference = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.ArrayRange, referenceValue ?? arrayValue, threadId, stackDepth, null, indices))
                });
            }
            return true;
        }
        if (count is null && currentDimensionLength > 100)
        {
            for (uint rangeStart = 0u; rangeStart < currentDimensionLength; rangeStart += 100)
            {
                uint rangeCount = Math.Min(100u, currentDimensionLength - rangeStart);
                uint firstIndex = currentDimensionStart + rangeStart;
                uint lastIndex = firstIndex + rangeCount - 1;
                string name = indexPrefix.Length is 0
                    ? $"[{firstIndex}..{lastIndex}]"
                    : $"[{string.Join(", ", indexPrefix)}, {firstIndex}..{lastIndex}]";
                result.Add(new VariableInfo
                {
                    Name = name,
                    Value = "",
                    Type = "",
                    PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Class },
                    VariablesReference = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.ArrayRange, referenceValue ?? arrayValue, threadId, stackDepth, null, indexPrefix, rangeStart, rangeCount))
                });
            }
            return true;
        }

        // The elements first: an evaluation below may neuter the array value.
        uint elementCount = count ?? currentDimensionLength;
        (uint[] Indices, ICorDebugValue Element)[] elements = ValueEnumerable.Range(0, checked((int)elementCount))
            .Select(offset =>
            {
                uint[] indices = [.. indexPrefix, currentDimensionStart + (startOffset ?? 0) + checked((uint)offset)];
                return (Indices: indices, Element: arrayValue.GetElement(rank, indices));
            })
            .ToArray();
        foreach ((uint[]? indices, ICorDebugValue? element) in elements)
        {
            string name = indices.Length == 1 ? $"[{indices[0]}]" : $"[{string.Join(", ", indices)}]";
            await WithFailureHandling(result, name, async () =>
            {
                (string? friendlyTypeName, string? value, ICorDebugValue? debuggerProxyInstance, bool resultIsError, string? displayName) = await GetDisplayedValueAsync(element, threadId, stackDepth, true);
                VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : new VariablePresentationHint { Kind = PresentationHintKind.Data };
                int variableReference = GetVariablesReference(element, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance);
                VariableInfo variableInfo = new VariableInfo
                {
                    Name = displayName ?? name,
                    Type = friendlyTypeName,
                    Value = value,
                    PresentationHint = variablePresentationHint,
                    VariablesReference = variableReference
                };
                result.Add(variableInfo);
            });
        }
        return false;
    }

    /// <summary>A row's value read, any failure of it (the runtime's, the program's, an evaluation's) shown as the row's
    /// value, so the rows beside it are read on.</summary>
    private async Task WithFailureHandling(List<VariableInfo> result, string fieldName, Func<Task> func)
    {
        try
        {
            await func();
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"{fieldName} could not be read: {ex}");
            result.Add(new VariableInfo
            {
                Name = fieldName,
                Value = ex.Message,
                Type = null,
                PresentationHint = new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation },
                VariablesReference = 0
            });
        }
    }
}
