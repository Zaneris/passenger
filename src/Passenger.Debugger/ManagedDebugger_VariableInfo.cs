using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Passenger.Core;
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
        ImmutableArray<DeclaredType?> declared = DeclaredLocals(module, corDebugIlFrame);
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
            string expression = RowExpressions.Name(localVariableName);
            await WithFailureHandling(result, localVariableName, async () =>
            {
                (string? friendlyTypeName, string? value, ICorDebugValue? debuggerProxyInstance, bool resultIsError) = await GetValueForCorDebugValueAsync(localVariableCorDebugValue, threadId, stackDepth, true);
                VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
                result.Add(new VariableInfo
                {
                    Name = localVariableName,
                    EvaluateName = expression,
                    Value = value,
                    Type = friendlyTypeName,
                    PresentationHint = variablePresentationHint,
                    VariablesReference = GetVariablesReference(localVariableCorDebugValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance, localVariableName,
                        index < declared.Length ? declared[index] : null, expression)
                });
            });
        }
    }

    /// <summary>Whether a value is a closure the compiler made: a class a lambda's captures live in, or a struct a local
    /// function's do, held in place or by reference.</summary>
    private static bool IsLambdaDisplayClass(ICorDebugValue value)
    {
        ICorDebugType type = value.ExactType;
        if (type.Type is CorElementType.BYREF)
        {
            type = type.FirstTypeParameter;
        }
        if (type.Type is not (CorElementType.CLASS or CorElementType.VALUETYPE))
        {
            return false;
        }

        IMetaDataImport metadataImport = type.Class.Module.GetMetaDataInterface<IMetaDataImport>();
        string typeName = metadataImport.GetTypeDefProps(type.Class.Token).szTypeDef;
        return GeneratedNameParser.GetKind(typeName) is GeneratedNameKind.LambdaDisplayClass;
    }

    /// Walks the compiler-generated closure chain starting at <paramref name="closureValue"/> (a closure or a state
    /// machine), listing each one's fields, the variables it holds, and none of its properties. Parent closures are linked
    /// via a field of kind <see cref="GeneratedNameKind.DisplayClassLocalOrField"/> (e.g. "&lt;&gt;8__1").
    private async Task AddClosureChainMembers(ICorDebugValue closureValue, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result)
    {
        if (closureValue.UnwrapDebugValueToObjectOrNull() is not { } objectValue)
        {
            return;
        }
        // A captured variable's closure copy is the live one, in place of its own slot, which goes stale.
        List<VariableInfo> members = [];
        await AddHeldVariables(closureValue, objectValue.ExactType, threadId, stackDepth, members);
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

    /// <summary>The variables a closure or state machine holds: its instance fields, each a captured or hoisted variable
    /// under its own name (the compiler's own fields passed over), never a property its interfaces give it.</summary>
    private async Task AddHeldVariables(ICorDebugValue holder, ICorDebugType holderType, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result)
    {
        IMetaDataImport metadataImport = holderType.Class.Module.GetMetaDataInterface<IMetaDataImport>();
        mdFieldDef[] fields = metadataImport.EnumFields(holderType.Class.Token).AsValueEnumerable().Where(field => !field.IsStatic(metadataImport)).ToArray();
        int first = result.Count;
        await AddFields(fields, metadataImport, holderType, holder, result, threadId, stackDepth);
        Express(result, first, row => RowExpressions.Name(row.Member!));
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
            if (methodName is "MoveNext" || GeneratedNameParser.GetKind(methodName) is GeneratedNameKind.LambdaMethod or GeneratedNameKind.LocalFunction)
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
                DeclaredType? declaredThis = DeclaredThis(module, corDebugFunction, generated: classContainingHoistedLocalsValue is not null);
                await WithFailureHandling(result, "this", async () =>
                {
                    (string? friendlyTypeName, string? value, ICorDebugValue? debuggerProxyInstance, bool resultIsError) = await GetValueForCorDebugValueAsync(implicitThisValue, threadId, stackDepth, true);
                    VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
                    result.Add(new VariableInfo
                    {
                        Name = "this", // Hardcoded - 'this' has no metadata
                        EvaluateName = "this",
                        Value = value,
                        Type = friendlyTypeName,
                        PresentationHint = variablePresentationHint,
                        VariablesReference = GetVariablesReference(implicitThisValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance, declared: declaredThis, expression: "this")
                    });
                });
            }
        }
        int skipCount = isStatic ? 0 : 1; // Skip 'this' for instance methods, as we already handled it
        ImmutableArray<DeclaredType?> declared = DeclaredParameters(module, corDebugFunction);
        foreach ((int index, ICorDebugValue? argumentCorDebugValue) in arguments.Skip(skipCount).Index())
        {
            if (IsLambdaDisplayClass(argumentCorDebugValue))
            {
                // A local function's closure, passed by reference: the variables it holds, in its place.
                await AddClosureChainMembers(argumentCorDebugValue, threadId, stackDepth, result);
                continue;
            }
            // Index 0 is the return value; a parameter without a row (emitted code) is named by its place, which no expression reaches.
            string? parameterName = metadataImport!.TryGetParamForMethodIndex(corDebugFunction.Token, checked((uint)(index + 1)), out mdParamDef paramDef) is Cor.S_OK
                ? metadataImport.GetParamProps(paramDef).szName
                : null;
            string? argumentName = parameterName ?? $"arg_{index}";
            if (argumentName is null)
            {
                continue;
            }
            string? expression = parameterName is null ? null : RowExpressions.Name(parameterName);

            await WithFailureHandling(result, argumentName, async () =>
            {
                (string? friendlyTypeName, string? value, ICorDebugValue? debuggerProxyInstance, bool resultIsError) = await GetValueForCorDebugValueAsync(argumentCorDebugValue, threadId, stackDepth, true);
                VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
                result.Add(new VariableInfo
                {
                    Name = argumentName,
                    EvaluateName = expression,
                    Value = value,
                    Type = friendlyTypeName,
                    PresentationHint = variablePresentationHint,
                    VariablesReference = GetVariablesReference(argumentCorDebugValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance, argumentName,
                        index < declared.Length ? declared[index] : null, expression)
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
                    EvaluateName = "$exception",
                    Value = value,
                    Type = friendlyTypeName,
                    PresentationHint = presentationHint,
                    VariablesReference = GetVariablesReference(currentException, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance, expression: "$exception")
                });
            });
        }
    }

    /// <summary>The reference a value's row opens by, 0 for one that opens to nothing: a union opens to the value it holds,
    /// and a pointer (<paramref name="name"/> its row's) to the value it points at. It keeps the type the value's variable is
    /// declared as, where known (<paramref name="declared"/>), and the C# reaching the value (<paramref name="expression"/>),
    /// for its rows' expressions.</summary>
    private int GetVariablesReference(ICorDebugValue corDebugValue, string friendlyTypeName, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue? debuggerProxyInstance, string? name = null,
        DeclaredType? declared = null, string? expression = null)
    {
        if (corDebugValue is ICorDebugReferenceValue { Type: CorElementType.PTR } pointer)
        {
            return pointer.IsNull || pointer.ExactType.FirstTypeParameter.Type is CorElementType.VOID ? 0
                : _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.Pointer, pointer, threadId, stackDepth, null, Name: name, Expression: expression));
        }
        if (TryGetUnionValue(corDebugValue, out _, out ICorDebugValue? held))
        {
            if (held is null)
            {
                return 0;
            }
            corDebugValue = held;
            declared = null;
        }
        ICorDebugValue unwrappedDebugValue = corDebugValue.UnwrapDebugValue();
        if (unwrappedDebugValue is ICorDebugArrayValue arrayValue)
        {
            if (arrayValue.Count is 0)
            {
                return 0;
            }

            return GenerateUniqueVariableReference(corDebugValue, threadId, stackDepth, debuggerProxyInstance, declared, expression);
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
            if (PrimitiveTypes.OfKeyword(friendlyTypeName) is { IsPrimitiveValue: true })
            {
                return 0;
            }

            if (type is CorElementType.CLASS or CorElementType.VALUETYPE or CorElementType.SZARRAY or CorElementType.ARRAY)
            {
                return GenerateUniqueVariableReference(corDebugValue, threadId, stackDepth, debuggerProxyInstance, declared, expression);
            }
        }
        return 0;
    }

    /// <summary>The one row a pointer opens to: the value it points at, named as C# reads it (*p).</summary>
    private async Task AddPointedAt(VariablesReference pointer, List<VariableInfo> result)
    {
        ICorDebugValue pointedAt = ((ICorDebugReferenceValue)pointer.ObjectValue!).Dereference();
        string name = pointer.Name is { } named ? (SyntaxFacts.IsValidIdentifier(named) ? $"*{named}" : $"*({named})") : "*";
        string? expression = RowExpressions.Pointed(pointer.Expression);
        await WithFailureHandling(result, name, async () =>
        {
            (string friendlyTypeName, string value, ICorDebugValue? proxy, bool resultIsError) = await GetValueForCorDebugValueAsync(pointedAt, pointer.ThreadId, pointer.FrameStackDepth, true);
            result.Add(new VariableInfo
            {
                Name = name,
                EvaluateName = expression,
                Value = value,
                Type = friendlyTypeName,
                PresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null,
                VariablesReference = GetVariablesReference(pointedAt, friendlyTypeName, pointer.ThreadId, pointer.FrameStackDepth, proxy, name, expression: expression)
            });
        });
    }

    private int GenerateUniqueVariableReference(ICorDebugValue value, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue? debuggerProxyInstance, DeclaredType? declared, string? expression)
    {
        VariablesReference variablesReference = new VariablesReference(StoredReferenceKind.StackVariable, value, threadId, stackDepth, debuggerProxyInstance, Declared: declared, Expression: expression);
        int reference = _variableManager.CreateReference(variablesReference);
        return reference;
    }

    private async Task AddMembersAndStaticPseudoVariable(ICorDebugValue corDebugValue, ICorDebugType corDebugType, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result, bool includeNonPublicMembers = true,
        DeclaredType? declared = null, string? owner = null, string? listed = null)
    {
        bool requiresStaticPseudoVariable = await AddMembers(corDebugValue, corDebugType, threadId, stackDepth, result, includeNonPublicMembers, declared, owner, listed);
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

    /// <summary>Whether a type is a list, whose elements C# reaches by their index.</summary>
    private bool IsList(ICorDebugType type) => Implements(type, static name =>
        name is "System.Collections.IList" || name.StartsWith("System.Collections.Generic.IList`1<", StringComparison.Ordinal));

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

    /// <summary>A type's members and then its base types', each reached from <paramref name="owner"/> (the value's
    /// expression; none where null), a list's view's elements by index from <paramref name="listed"/>; true where a Static
    /// members row is needed. A member the value's variable (declared as <paramref name="declared"/>, else as the value's own
    /// type) reaches only through a cast says the type declaring it: one of a type below the declared one but a property an
    /// interface declared reaches by its name, or one that a type between the declared one and its own hides by its name.</summary>
    private Task<bool> AddMembers(ICorDebugValue corDebugValue, ICorDebugType corDebugType, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result, bool includeNonPublicMembers = true,
        DeclaredType? declared = null, string? owner = null, string? listed = null)
    {
        int below = Below(corDebugType, declared);
        return AddMembers(corDebugValue, corDebugType, threadId, stackDepth, result, includeNonPublicMembers, below, new HashSet<string>(StringComparer.Ordinal),
            below > 0 ? ThroughInterface(corDebugType, declared) : null, owner, listed);
    }

    /// <summary>A type's members and then its base types', each saying its type where it lies <paramref name="below"/> the
    /// declared type (more than none) or a type between the declared one and its own took its name (<paramref name="hidden"/>),
    /// but for the first property, at or above the type implementing it, of each name a declared interface reaches
    /// (<paramref name="reach"/>).</summary>
    private async Task<bool> AddMembers(ICorDebugValue corDebugValue, ICorDebugType corDebugType, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result, bool includeNonPublicMembers,
        int below, HashSet<string> hidden, InterfaceReach? reach, string? owner, string? listed)
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

        await AddFields(nonStaticFieldDefs, metadataImport, corDebugType, corDebugValue, result, threadId, stackDepth, owner, listed);
        int properties = result.Count;
        // The reference itself, not unwrapped: the getters are called on it.
        await AddProperties(nonStaticProperties, metadataImport, corDebugType, threadId, stackDepth, corDebugValue, result, owner, listed);
        if (below > 0 || hidden.Count > 0)
        {
            for (int at = first; at < result.Count; at++)
            {
                bool reached = at >= properties && reach is { Below: <= 0 } && reach.Names.Remove(result[at].Name);
                if (!reached && (below > 0 || hidden.Contains(result[at].Name)))
                {
                    result[at].DeclaringType = GetCorDebugTypeFriendlyName(corDebugType);
                }
            }
        }
        Express(result, first, row => RowExpressions.Member(owner, row.Member!, row.Interface ?? row.DeclaringType));

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

        // Only the declared type and those above it hide a name from its variable's expression.
        HashSet<string> taken = new(hidden, StringComparer.Ordinal);
        if (below <= 0)
        {
            taken.UnionWith(result.Skip(first).Select(member => member.Name));
        }
        return hasStaticMembers | await AddMembers(corDebugValue, baseType, threadId, stackDepth, result, true, below - 1, taken, reach is null ? null : reach with { Below = reach.Below - 1 }, owner, listed);
    }

    /// <summary>Gives each member row from <paramref name="from"/> on the expression <paramref name="of"/> makes for it, which
    /// the rows under it, when it opens, are reached from too.</summary>
    private void Express(List<VariableInfo> rows, int from, Func<VariableInfo, string?> of)
    {
        for (int at = from; at < rows.Count; at++)
        {
            VariableInfo row = rows[at];
            if (row.Member is null || row.EvaluateName is not null)
            {
                continue;
            }
            row.EvaluateName = of(row);
            if (row.VariablesReference > 0)
            {
                _variableManager.Express(row.VariablesReference, row.EvaluateName);
            }
        }
    }

    /// <summary>Marks rows lying in a copy the debugger made to show a value (a debugger view's): each says it is read-only,
    /// and one holding its value in place (a struct, a range of the copy's elements) opens to rows of the copy too, where one
    /// holding a reference opens to the program's own object.</summary>
    private void Copies(List<VariableInfo> rows)
    {
        foreach (VariableInfo row in rows)
        {
            if (row.PresentationHint?.Attributes is AttributesValue.FailedEvaluation)
            {
                continue;
            }
            row.PresentationHint = (row.PresentationHint ?? new VariablePresentationHint()) with { Attributes = AttributesValue.ReadOnly };
            if (row.VariablesReference > 0 && _variableManager.GetReference(row.VariablesReference) is { } opened
                && (opened.ReferenceKind is StoredReferenceKind.ArrayRange || opened is { ReferenceKind: StoredReferenceKind.StackVariable, ObjectValue: not ICorDebugReferenceValue }))
            {
                _variableManager.Copy(row.VariablesReference);
            }
        }
    }

    /// <summary>The static members of a type and of its base types, each reached through the type declaring it
    /// (Shelf.Reads), but an interface's static member a type implements explicitly, which no expression names.</summary>
    private async Task AddStaticMembers(ICorDebugValue corDebugValue, ICorDebugType corDebugType, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result)
    {
        ICorDebugClass corDebugClass = corDebugType.Class;
        ICorDebugModule module = corDebugClass.Module;
        mdTypeDef mdTypeDef = corDebugClass.Token;
        IMetaDataImport metadataImport = module.GetMetaDataInterface<IMetaDataImport>();
        mdFieldDef[] staticFieldDefs = metadataImport.EnumFields(mdTypeDef).AsValueEnumerable().Where(s => s.IsStatic(metadataImport)).ToArray();
        mdProperty[] staticProperties = metadataImport.EnumProperties(mdTypeDef).AsValueEnumerable().Where(s => s.IsStatic(metadataImport)).ToArray();

        int first = result.Count;
        string declaring = GetCorDebugTypeFriendlyName(corDebugType);
        string? owner = SyntaxFactory.ParseTypeName(declaring).ContainsDiagnostics ? null : declaring;
        await AddFields(staticFieldDefs, metadataImport, corDebugType, corDebugValue, result, threadId, stackDepth, owner);
        // The reference itself, not unwrapped: the getters are called on it.
        await AddProperties(staticProperties, metadataImport, corDebugType, threadId, stackDepth, corDebugValue, result, owner);
        Express(result, first, row => row.Interface is null ? RowExpressions.Member(owner, row.Member!) : null);

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

    /// <summary>The fields given, each a row named as C# reaches it (<see cref="VariableInfo.Member"/>), a RootHidden one's
    /// elements or members in its place reached from <paramref name="owner"/> (a list's view's elements from
    /// <paramref name="listed"/>), and an enum's value as its number.</summary>
    private async Task AddFields(mdFieldDef[] mdFieldDefs, IMetaDataImport metadataImport, ICorDebugType corDebugType, ICorDebugValue corDebugValue, List<VariableInfo> result, ThreadId threadId, FrameStackDepth stackDepth,
        string? owner = null, string? listed = null)
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
                        Member = fieldName,
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
                        await AddArrayElements(arrayValue, threadId, stackDepth, result, referenceValue: fieldCorDebugValue, expression: listed ?? RowExpressions.Member(owner, fieldName));
                        return;
                    }
                    if (unwrappedDebugValue is ICorDebugObjectValue and not ICorDebugStringValue)
                    {
                        await AddRootHiddenObject(fieldCorDebugValue, threadId, stackDepth, result, RowExpressions.Member(owner, fieldName));
                        return;
                    }
                }
                (string? friendlyTypeName, string? value, ICorDebugValue? debuggerProxyInstance, bool resultIsError, string? displayName) = await GetDisplayedValueAsync(fieldCorDebugValue, threadId, stackDepth, true);
                VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
                // An enum's value__ is its number, which C# reaches by a cast.
                bool number = fieldName is "value__" && fieldProps.pdwAttr.HasFlag(CorFieldAttr.fdRTSpecialName);
                VariableInfo variableInfo = new VariableInfo
                {
                    Name = displayName ?? fieldName,
                    EvaluateName = number ? RowExpressions.Cast(owner, friendlyTypeName) : null,
                    Member = number ? null : fieldName,
                    Value = value,
                    Type = friendlyTypeName,
                    PresentationHint = variablePresentationHint,
                    VariablesReference = GetVariablesReference(fieldCorDebugValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance, fieldName, DeclaredField(corDebugClass, mdFieldDef))
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
        return (PrimitiveTypes.Named(typeName)?.Keyword ?? typeName, moduleInfo, typeHandle);
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
    private async Task AddProperties(mdProperty[] mdProperties, IMetaDataImport metadataImport, ICorDebugType declaringType, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue corDebugValue, List<VariableInfo> result,
        string? owner = null, string? listed = null)
    {
        ICorDebugClass corDebugClass = declaringType.Class;
        foreach (mdProperty mdProperty in mdProperties)
        {
            (string szProperty, mdTypeDef pClass, CorPropertyAttr pdwPropFlags, nint ppvSig, int pbSig, CorElementType pdwCPlusTypeFlag, nint ppDefaultValue, int pcchDefaultValue, mdMethodDef pmdSetter, mdMethodDef pmdGetter, mdMethodDef rmdOtherMethod, int pcOtherMethod) propertyProps = metadataImport.GetPropertyProps(mdProperty);
            string? propertyName = propertyProps.szProperty;
            if (propertyName is null || TakesParameters(propertyProps.ppvSig, propertyProps.pbSig))
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
                            retainReturnValue = await AddArrayElements(arrayValue, threadId, stackDepth, result, referenceValue: returnValue, expression: listed ?? RowExpressions.Member(owner, propertyName));
                            return;
                        }
                        if (unwrappedDebugValue is ICorDebugObjectValue and not ICorDebugStringValue)
                        {
                            // Its children are read from it later, as they open.
                            retainReturnValue = true;
                            await AddRootHiddenObject(returnValue, threadId, stackDepth, result, RowExpressions.Member(owner, propertyName));
                            return;
                        }
                    }
                    (string? friendlyTypeName, string? value, ICorDebugValue? debuggerProxyInstance, bool resultIsError, string? displayName) = await GetDisplayedValueAsync(returnValue, threadId, stackDepth, true);
                    VariablePresentationHint? variablePresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null;
                    int variablesReference = GetVariablesReference(returnValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance, declared: DeclaredProperty(corDebugClass, mdProperty));
                    (string Interface, string Member)? implemented = propertyName.Contains('.') ? ExplicitOf(declaringType, propertyName) : null;
                    VariableInfo variableInfo = new VariableInfo
                    {
                        Name = displayName ?? propertyName,
                        Member = implemented?.Member ?? propertyName,
                        Interface = implemented?.Interface,
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

    /// <summary>Whether a property's signature (<paramref name="length"/> bytes at <paramref name="signature"/>) has
    /// parameters, as an indexer's has: no row shows one, its getter having nothing to be called with.</summary>
    private static unsafe bool TakesParameters(nint signature, int length)
    {
        if (signature == 0 || length < 2)
        {
            return false;
        }
        BlobReader blob = new((byte*)signature, length);
        blob.ReadSignatureHeader();
        return blob.ReadCompressedInteger() > 0;
    }

    /// <summary>The interface and member an explicit implementation's property, named <paramref name="name"/> in
    /// <paramref name="declaringType"/>, is: the interface closed by the type arguments the value's type gives.</summary>
    private (string Interface, string Member)? ExplicitOf(ICorDebugType declaringType, string name)
    {
        if (FindModule(declaringType.Class.Module) is not { } module)
        {
            return null;
        }
        MetadataReader reader = module.MetadataReader.PeMetadataReader;
        TypeDefinition type = reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(declaringType.Class.Token.Rid));
        List<string> parameters = [.. type.GetGenericParameters().Select(parameter => reader.GetString(reader.GetGenericParameter(parameter).Name))];
        return RowExpressions.Explicit(name, parameters, [.. declaringType.TypeParameters.Select(GetCorDebugTypeFriendlyName)]);
    }

    /// <summary>A RootHidden member that is an object, not an array: its children in its place, as they would show
    /// under it, its proxy's if it has one, reached from <paramref name="expression"/>, the member's.</summary>
    private async Task AddRootHiddenObject(ICorDebugValue value, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result, string? expression)
    {
        (_, _, ICorDebugValue? proxy, _, _) = await GetDisplayedValueAsync(value, threadId, stackDepth, true);
        if (proxy is not null)
        {
            ICorDebugType view = proxy.UnwrapDebugValueToObject().ExactType;
            await AddMembers(proxy, view, threadId, stackDepth, result, includeNonPublicMembers: false, owner: RowExpressions.Viewed(expression, GetCorDebugTypeFriendlyName(view)));
        }
        else
        {
            await AddMembers(value, value.UnwrapDebugValueToObject().ExactType, threadId, stackDepth, result, owner: expression);
        }
    }

    /// <summary>An array's rows under <paramref name="indexPrefix"/>, its outer dimensions' indices opened so far: a dimension
    /// over 100 long in ranges of 100 (<paramref name="startOffset"/> and <paramref name="count"/> one opened), an outer
    /// dimension's indices each opening to the next, the last's elements, declared as <paramref name="declared"/>'s element
    /// type (else the array's own), reached by their whole index from <paramref name="expression"/>. A range and an outer
    /// index are virtual rows, which none reaches. True when the rows open through the array, which is then kept.</summary>
    private async Task<bool> AddArrayElements(ICorDebugArrayValue arrayValue, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result, uint[]? indexPrefix = null, uint? startOffset = null, uint? count = null, ICorDebugValue? referenceValue = null,
        DeclaredType? declared = null, string? expression = null)
    {
        int rank = arrayValue.Rank;
        indexPrefix ??= [];
        uint[] dimensions = arrayValue.GetDimensions(rank);
        uint[] baseIndices = arrayValue.HasBaseIndicies() ? arrayValue.GetBaseIndicies(rank) : new uint[rank];
        int currentDimension = indexPrefix.Length;
        uint currentDimensionStart = baseIndices[currentDimension];
        uint currentDimensionLength = dimensions[currentDimension];
        bool outer = currentDimension < rank - 1;

        if (count is null && currentDimensionLength > 100)
        {
            for (uint rangeStart = 0u; rangeStart < currentDimensionLength; rangeStart += 100)
            {
                uint rangeCount = Math.Min(100u, currentDimensionLength - rangeStart);
                uint firstIndex = currentDimensionStart + rangeStart;
                uint lastIndex = firstIndex + rangeCount - 1;
                string name = $"[{string.Concat(indexPrefix.Select(index => $"{index}, "))}{firstIndex}..{lastIndex}{(outer ? ", ..." : "")}]";
                result.Add(new VariableInfo
                {
                    Name = name,
                    Value = "",
                    Type = "",
                    PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Virtual },
                    VariablesReference = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.ArrayRange, referenceValue ?? arrayValue, threadId, stackDepth, null, indexPrefix, rangeStart, rangeCount, Declared: declared,
                        Expression: expression))
                });
            }
            return true;
        }
        if (outer)
        {
            uint first = startOffset ?? 0;
            for (uint offset = first; offset < first + (count ?? currentDimensionLength); offset++)
            {
                uint[] indices = [.. indexPrefix, currentDimensionStart + offset];
                string name = $"[{string.Join(", ", indices)}, ...]";
                result.Add(new VariableInfo
                {
                    Name = name,
                    Value = "",
                    Type = "",
                    PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Virtual },
                    VariablesReference = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.ArrayRange, referenceValue ?? arrayValue, threadId, stackDepth, null, indices, Declared: declared,
                        Expression: expression))
                });
            }
            return true;
        }

        // The elements first: an evaluation below may neuter the array value.
        DeclaredType? elementDeclared = declared?.Element ?? DeclaredAs(arrayValue.ExactType.FirstTypeParameter);
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
                string? reached = RowExpressions.Element(expression, indices);
                int variableReference = GetVariablesReference(element, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance, declared: elementDeclared, expression: reached);
                VariableInfo variableInfo = new VariableInfo
                {
                    Name = displayName ?? name,
                    EvaluateName = reached,
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
