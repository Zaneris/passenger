using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.Core;
using Passenger.Debugger.ExpressionEvaluator;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.ExpressionEvaluator.Compiler;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public enum CorDebugValueFormatKind
{
    None,
    DebuggerDisplay,
    ToString,
    /// <summary>An exception whose types all write System.Exception's ToString, written from what it stores.</summary>
    Exception,
    Tuple,
    AnonymousType
}

public readonly record struct CorDebugValueValueResult(string FriendlyTypeName, string Value, CorDebugValueFormatKind FormatKind, string? DebuggerProxyTypeName, DebuggerDisplay? Display = null);

/// <summary>A value as a variables window shows it: its type and value columns, the proxy its children
/// come from, whether it is an error, and the name its [DebuggerDisplay] gives the row, if any.</summary>
public readonly record struct FormattedValue(string FriendlyTypeName, string Value, string? DebuggerProxyTypeName, bool ResultIsError, string? DisplayName = null);
public partial class ManagedDebugger
{
    public async Task<(string friendlyTypeName, string value, ICorDebugValue? debuggerProxyInstance, bool resultIsError)> GetValueForCorDebugValueAsync(ICorDebugValue corDebugValue, ThreadId threadId, FrameStackDepth frameStackDepth, bool escapeStringValue)
    {
        (string friendlyTypeName, string value, ICorDebugValue? proxyInstance, bool resultIsError, _) = await GetDisplayedValueAsync(corDebugValue, threadId, frameStackDepth, escapeStringValue);
        return (friendlyTypeName, value, proxyInstance, resultIsError);
    }

    /// <summary>A value as its row shows it, with the name its [DebuggerDisplay] gives the row, which a
    /// member or an element takes in place of its own.</summary>
    public async Task<(string friendlyTypeName, string value, ICorDebugValue? debuggerProxyInstance, bool resultIsError, string? displayName)> GetDisplayedValueAsync(ICorDebugValue corDebugValue, ThreadId threadId, FrameStackDepth frameStackDepth, bool escapeStringValue)
    {
        ArgumentNullException.ThrowIfNull(corDebugValue);
        ResolvedCorDebugValue resolvedValue = ResolveCorDebugValue(corDebugValue);
        FormattedValue formatted = await FormatCorDebugValueAsync(resolvedValue, threadId, frameStackDepth, escapeStringValue);
        ICorDebugValue? proxyInstance = null;
        if (resolvedValue.Value is { } valueToProxy && formatted.DebuggerProxyTypeName is { } debuggerProxyTypeName)
        {
            (ICorDebugModule? module, string proxyName) = FindProxyType(debuggerProxyTypeName, valueToProxy.ExactType.Class.Module);
            if (module is not null)
            {
                proxyInstance = await CreateDebuggerProxyInstance(valueToProxy, threadId, module, proxyName,
                    ProxyTypeArguments(debuggerProxyTypeName, valueToProxy.ExactType.Class.Module) ?? valueToProxy.ExactType.TypeParameters);
            }
            else
            {
                _logger?.Invoke($"The proxy {debuggerProxyTypeName} is in no module the program has loaded");
            }
        }
        return (formatted.FriendlyTypeName, formatted.Value, proxyInstance, formatted.ResultIsError, formatted.DisplayName);
    }

    private async Task<FormattedValue> FormatCorDebugValueAsync(ResolvedCorDebugValue resolvedValue, ThreadId threadId, FrameStackDepth frameStackDepth, bool escapeStringValue, int nesting = 0)
    {
        if (resolvedValue.Value is not { } valueToFormat)
        {
            return new FormattedValue(resolvedValue.FriendlyTypeNameOverride!, "null", null, false);
        }

        (string? friendlyTypeName, string? value, CorDebugValueFormatKind formatKind, string? debuggerProxyTypeName, DebuggerDisplay? display) = GetValueForCorDebugValue(valueToFormat, escapeStringValue);
        friendlyTypeName = resolvedValue.FriendlyTypeNameOverride ?? friendlyTypeName;
        bool resultIsError = false;
        if (formatKind is CorDebugValueFormatKind.Exception)
        {
            // Its trace is read rather than formatted in the program; where it cannot be, its ToString runs.
            if (await ExceptionTextAsync(valueToFormat, threadId, frameStackDepth) is { } written)
            {
                return new FormattedValue(friendlyTypeName, written, debuggerProxyTypeName, false);
            }
            formatKind = CorDebugValueFormatKind.ToString;
        }
        if (EvalStatus.ImplicitOff is not null && !EvalStatus.Explicit && formatKind is CorDebugValueFormatKind.ToString)
        {
            // Its ToString does not run while implicit evaluation is off: its type in braces.
            return new FormattedValue(friendlyTypeName, $"{{{friendlyTypeName}}}", debuggerProxyTypeName, false);
        }
        string? displayName = null;
        if (formatKind is CorDebugValueFormatKind.Tuple or CorDebugValueFormatKind.AnonymousType)
        {
            ICorDebugObjectValue objectValue = valueToFormat.UnwrapDebugValue() as ICorDebugObjectValue
                ?? throw new InvalidOperationException($"Cannot format non-object value as {formatKind}");
            (value, resultIsError) = await FormatCompositeValueAsync(objectValue, threadId, frameStackDepth, formatKind);
        }
        else if (formatKind is CorDebugValueFormatKind.ToString)
        {
            // Fast path to avoid EE, which is far slower
            ICorDebugFunction toStringFunction = FindMethodOnType(valueToFormat.ExactType, "ToString", [], false, false) ?? throw new InvalidOperationException($"Could not find ToString on {friendlyTypeName}");
            ICorDebugThread thread = _process!.GetThread(threadId.Value);
            ICorDebugType[] typeParameters = DeclaringTypeOf(valueToFormat.ExactType, toStringFunction).TypeParameters;
            ICorDebugValue result;
            try
            {
                result = await thread.CreateEval().CallParameterizedFunctionAsync(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, toStringFunction, typeParameters.Length, typeParameters, 1, [valueToFormat], throwOnException: true) ?? throw new InvalidOperationException($"ToString returned no value for {friendlyTypeName}");
            }
            catch (EvalException)
            {
                // A ToString that throws, never returns or cannot run: its type in braces, its fields there to open.
                return new FormattedValue(friendlyTypeName, $"{{{friendlyTypeName}}}", debuggerProxyTypeName, false);
            }
            try
            {
                value = (result.UnwrapDebugValue() as ICorDebugStringValue)?.String ?? throw new InvalidOperationException($"ToString returned a non-string value for {friendlyTypeName}");
            }
            finally
            {
                if (result is ICorDebugHandleValue handle)
                {
                    handle.TryDispose();
                }
            }
        }
        else if (formatKind is CorDebugValueFormatKind.DebuggerDisplay && display is not null)
        {
            if (nesting >= DisplayNesting)
            {
                value = $"{{{friendlyTypeName}}}";
            }
            else
            {
                (value, resultIsError, string? shownType) = await RenderDebuggerDisplayAsync(display.Value, valueToFormat, threadId, frameStackDepth, nesting);
                if (resultIsError && EvalStatus.ImplicitOff is not null && !EvalStatus.Explicit)
                {
                    // While implicit evaluation is off a display reads only fields, which run no code: one needing more is its type in braces.
                    return new FormattedValue(friendlyTypeName, $"{{{friendlyTypeName}}}", debuggerProxyTypeName, false);
                }
                if (display.Name is { } nameFormat)
                {
                    (displayName, _, _) = await RenderDebuggerDisplayAsync(nameFormat, valueToFormat, threadId, frameStackDepth, nesting);
                    // A row named by its display that shows one value alone (a dictionary's key and value) stands for that value.
                    friendlyTypeName = shownType ?? friendlyTypeName;
                }
            }
            friendlyTypeName = display.Type ?? friendlyTypeName;
        }
        return new FormattedValue(friendlyTypeName, value, debuggerProxyTypeName, resultIsError, displayName);
    }

    /// <summary>The module holding the type a [DebuggerTypeProxy] names, and its name there: in the assembly an
    /// assembly-qualified name gives, else in the shown type's own module or CoreLib, as the runtime resolves it. A null
    /// module where no module the program has loaded holds it, so the value opens into its own members.</summary>
    private (ICorDebugModule? Module, string TypeName) FindProxyType(string proxyTypeName, ICorDebugModule shownModule)
    {
        int depth = 0;
        int comma = -1;
        for (int at = 0; at < proxyTypeName.Length && comma < 0; at++)
        {
            depth += proxyTypeName[at] switch { '[' => 1, ']' => -1, _ => 0 };
            comma = proxyTypeName[at] == ',' && depth == 0 ? at : -1;
        }
        string typeName = (comma < 0 ? proxyTypeName : proxyTypeName[..comma]).Trim();
        // A generic proxy is found by its definition, made with the type arguments its name gives, else the value's own.
        typeName = typeName.IndexOf('[', StringComparison.Ordinal) is var arguments and >= 0 ? typeName[..arguments] : typeName;
        string? assemblyName = comma < 0 ? null : proxyTypeName[(comma + 1)..].Split(',')[0].Trim();
        ModuleInfo? coreLib = CoreLib;
        // One a reference assembly names (System.Runtime) is defined where it forwards to, which is CoreLib or another loaded.
        IEnumerable<ICorDebugModule> candidates = assemblyName is null
            ? [shownModule, .. coreLib is not null ? [coreLib.Module] : Array.Empty<ICorDebugModule>()]
            : _modules.Values.OrderBy(module => string.Equals(AssemblyNameOf(module), assemblyName, StringComparison.OrdinalIgnoreCase) ? 0 : module == coreLib ? 1 : 2)
                .Select(module => module.Module);
        foreach (ICorDebugModule candidate in candidates)
        {
            if (candidate.GetMetaDataInterface<IMetaDataImport>().FindMaybeNestedTypeDefByNameOrNull(typeName) is not null)
            {
                return (candidate, typeName);
            }
        }
        return (null, typeName);
    }

    /// <summary>The type arguments a [DebuggerTypeProxy] names for its generic type (typeof(View&lt;Variant&gt;) on a type that
    /// is not generic), each found as <see cref="FindProxyType"/> finds the proxy: View`1[Variant] where it is the attribute's
    /// assembly's, View`1[[Int32, System.Runtime]] where another's. Null for a name that gives none, or one no module the
    /// program has loaded holds, where the value's own type arguments instantiate it.</summary>
    private ICorDebugType[]? ProxyTypeArguments(string proxyTypeName, ICorDebugModule shownModule)
    {
        int open = proxyTypeName.IndexOf('[', StringComparison.Ordinal);
        if (open < 0 || !proxyTypeName.Contains('`', StringComparison.Ordinal))
        {
            return null;
        }
        List<ICorDebugType> arguments = [];
        int depth = 0;
        int start = open + 1;
        for (int at = open + 1; at < proxyTypeName.Length; at++)
        {
            char c = proxyTypeName[at];
            if (c == '[')
            {
                depth++;
            }
            else if ((c is ',' or ']') && depth == 0)
            {
                string argument = proxyTypeName[start..at].Trim();
                if (ProxyTypeArgument(argument.StartsWith('[') && argument.EndsWith(']') ? argument[1..^1] : argument, shownModule) is not { } found)
                {
                    return null;
                }
                arguments.Add(found);
                start = at + 1;
                if (c == ']')
                {
                    break;
                }
            }
            else if (c == ']')
            {
                depth--;
            }
        }
        return [.. arguments];
    }

    /// <summary>One type argument of a proxy's name, as an attribute writes it, with its own arguments.</summary>
    private ICorDebugType? ProxyTypeArgument(string name, ICorDebugModule shownModule)
    {
        (ICorDebugModule? module, string typeName) = FindProxyType(name, shownModule);
        if (module?.GetMetaDataInterface<IMetaDataImport>().FindMaybeNestedTypeDefByNameOrNull(typeName) is not { } definition)
        {
            return null;
        }
        ICorDebugType[] arguments = ProxyTypeArguments(name, module) ?? [];
        bool valueType = EvaluationMetadataResolver.IsValueType(new ResolvedRuntimeType(GetModuleInfoForModule(module), MetadataTokens.TypeDefinitionHandle(definition.Rid)));
        return ((ICorDebugClass2)module.GetClassFromToken(definition)).GetParameterizedType(valueType ? CorElementType.VALUETYPE : CorElementType.CLASS, arguments.Length, arguments);
    }

    private static string? AssemblyNameOf(ModuleInfo module) =>
        module.MetadataReader.PeMetadataReader is { IsAssembly: true } reader ? reader.GetString(reader.GetAssemblyDefinition().Name) : null;

    /// <summary>The [DebuggerTypeProxy] made for a value, or null when its constructor throws, so the value opens into
    /// its own members rather than the exception's.</summary>
    private async Task<ICorDebugValue?> CreateDebuggerProxyInstance(ICorDebugValue value, ThreadId threadId, ICorDebugModule module, string proxyTypeName, ICorDebugType[] typeArguments)
    {
        IMetaDataImport metadataImport = module.GetMetaDataInterface<IMetaDataImport>();
        mdTypeDef? proxyTypeDef = metadataImport.FindMaybeNestedTypeDefByNameOrNull(proxyTypeName);
        ArgumentNullException.ThrowIfNull(proxyTypeDef);
        ICorDebugClass proxyClass = module.GetClassFromToken(proxyTypeDef.Value);

        mdMethodDef constructor = metadataImport.FindMethod(proxyClass.Token, ".ctor", 0, 0);
        ICorDebugEval eval = _process!.GetThread(threadId.Value).CreateEval();
        try
        {
            return await eval.NewParameterizedObjectAsync(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, module.GetFunctionFromToken(constructor), typeArguments.Length, typeArguments, 1, [value], throwOnException: true);
        }
        catch (EvalException error)
        {
            _logger?.Invoke($"The proxy {proxyTypeName} failed: {error.Message}");
            return null;
        }
    }

    private CorDebugValueValueResult GetValueForCorDebugValue(ICorDebugValue corDebugValue, bool escapeStringValue)
    {
        return corDebugValue switch
        {
            ICorDebugBoxValue corDebugBoxValue => GetCorDebugBoxValue_Value_AsString(corDebugBoxValue, escapeStringValue),
            ICorDebugArrayValue corDebugArrayValue => Get_CorDebugArrayValue_AsString(corDebugArrayValue),
            ICorDebugStringValue stringValue => Get_CorDebugStringValue_AsString(stringValue, escapeStringValue),

            ICorDebugContext corDebugContext => throw new NotImplementedException(),
            ICorDebugObjectValue corDebugObjectValue => GetCorDebugObjectValue_Value_AsString(corDebugObjectValue, escapeStringValue),
            ICorDebugReferenceValue corDebugReferenceValue => GetCorDebugReferenceValue_Value_AsString(corDebugReferenceValue, escapeStringValue),

            ICorDebugHeapValue corDebugHeapValue => throw new NotImplementedException(),
            ICorDebugGenericValue corDebugGenericValue => GetCorDebugGenericValue_Value_AsString(corDebugGenericValue),  // This should be already handled by the above classes, so we should never get here
            _ => throw new ArgumentOutOfRangeException(nameof(corDebugValue))
        };
    }

    private static CorDebugValueValueResult Get_CorDebugStringValue_AsString(ICorDebugStringValue corDebugStringValue, bool escapeStringValue)
    {
        string text = corDebugStringValue.String;
        if (escapeStringValue)
        {
            text = SymbolDisplay.FormatLiteral(text, quote: true);
        }

        return new("string", text, CorDebugValueFormatKind.None, null);
    }

    public static CorDebugValueValueResult Get_CorDebugArrayValue_AsString(ICorDebugArrayValue corDebugArrayValue)
    {
        string typeName = GetCorDebugTypeFriendlyName(corDebugArrayValue.ExactType);
        ReadOnlySpan<char> typeNameSpan = typeName.AsSpan();
        int arraySuffixStart = typeNameSpan.Length;
        while (arraySuffixStart > 0 && typeNameSpan[arraySuffixStart - 1] == ']')
        {
            arraySuffixStart = typeNameSpan[..arraySuffixStart].LastIndexOf('[');
        }
        ReadOnlySpan<char> elementTypeName = typeNameSpan[..arraySuffixStart];
        ReadOnlySpan<char> remainingArraySuffix = typeNameSpan[(arraySuffixStart + typeNameSpan[arraySuffixStart..].IndexOf(']') + 1)..];
        uint[] dimensions = corDebugArrayValue.GetDimensions(corDebugArrayValue.Rank);
        string value = $"{elementTypeName}[{string.Join(", ", dimensions)}]{remainingArraySuffix}";
        return new(typeName, value, CorDebugValueFormatKind.None, null);
    }

    private CorDebugValueValueResult GetCorDebugBoxValue_Value_AsString(ICorDebugBoxValue corDebugBoxValue, bool escapeStringValue)
    {
        ICorDebugObjectValue unboxedValue = corDebugBoxValue.Object;
        CorDebugValueValueResult value = GetValueForCorDebugValue(unboxedValue, escapeStringValue);
        return value;
    }

    private CorDebugValueValueResult GetCorDebugObjectValue_Value_AsString(ICorDebugObjectValue corDebugObjectValue, bool escapeStringValue)
    {
        ICorDebugModule module = corDebugObjectValue.Class.Module;
        IMetaDataImport metaDataImport = module.GetMetaDataInterface<IMetaDataImport>();
        string? baseTypeName = corDebugObjectValue.ExactType.Base is { } baseType ? GetCorDebugTypeFriendlyName(baseType) : null; // ExactType.Base is null when ExactType is System.Object
        if (baseTypeName is "System.Enum")
        {
            mdFieldDef valueFieldDef = metaDataImport.FindField(corDebugObjectValue.Class.Token, "value__", 0, 0);
            ICorDebugValue valueField = corDebugObjectValue.GetFieldValue(corDebugObjectValue.Class, valueFieldDef);
            CorDebugValueValueResult value = GetValueForCorDebugValue(valueField, escapeStringValue);

            string enumDisplayValue = GetEnumDisplayValue(metaDataImport, corDebugObjectValue.Class.Token, value.Value);
            return new(GetCorDebugTypeFriendlyName(corDebugObjectValue.ExactType), enumDisplayValue, CorDebugValueFormatKind.None, null);
        }
        string typeName = GetCorDebugTypeFriendlyName(corDebugObjectValue.ExactType);
        bool hasDebuggerTypeProxyAttribute = metaDataImport.TryGetCustomAttributeByName(corDebugObjectValue.Class.Token, "System.Diagnostics.DebuggerTypeProxyAttribute", out nint debuggerTypeProxyAttributePointer, out uint debuggerTypeProxyAttributeSize) is Cor.S_OK;

        // A view an assembly declares for the type is taken as its own, before the one it carries.
        string? debugProxyTypeName = TargetedAt(metaDataImport, corDebugObjectValue.Class.Token).LastOrDefault(t => t.Proxy is not null)?.Proxy
            ?? (hasDebuggerTypeProxyAttribute ? GetCustomAttributeResultString(debuggerTypeProxyAttributePointer, debuggerTypeProxyAttributeSize) : null);
        if (IsTupleType(typeName))
        {
            return new(typeName, "", CorDebugValueFormatKind.Tuple, debugProxyTypeName);
        }
        if (typeName.StartsWith("<>f__AnonymousType", StringComparison.Ordinal))
        {
            return new(typeName, "", CorDebugValueFormatKind.AnonymousType, debugProxyTypeName);
        }
        if (FindDebuggerDisplay(corDebugObjectValue.ExactType) is { } display)
        {
            return new(typeName, display.Value, CorDebugValueFormatKind.DebuggerDisplay, debugProxyTypeName, display);
        }
        if (corDebugObjectValue.ExactType.IsExceptionType())
        {
            return new(typeName, "", Declares(corDebugObjectValue.ExactType, "ToString") ? CorDebugValueFormatKind.ToString : CorDebugValueFormatKind.Exception, debugProxyTypeName);
        }
        if (typeName == "decimal")
        {
            // Read from its fields, not by its ToString, which would run the program.
            string decimalString = GetDecimalValueString(corDebugObjectValue);
            return new(typeName, decimalString, CorDebugValueFormatKind.None, null);
        }
        if (TypeOverridesToString(corDebugObjectValue.ExactType))
        {
            return new(typeName, "", CorDebugValueFormatKind.ToString, debugProxyTypeName);
        }

        return new(typeName, $"{{{typeName}}}", CorDebugValueFormatKind.None, debugProxyTypeName);
    }

    /// Returns true if <paramref name="corDebugType"/> or any of its base types (up to but not
    /// including System.Object / System.ValueType) declares a no-arg "ToString" method directly on itself.
    private static bool TypeOverridesToString(ICorDebugType corDebugType)
    {
        ICorDebugType? type = corDebugType;
        while (type is not null)
        {
            ICorDebugClass cls = type.Class;
            ICorDebugModule module = cls.Module;
            IMetaDataImport metaDataImport = module.GetMetaDataInterface<IMetaDataImport>();
            string typeName = metaDataImport.GetTypeDefProps(cls.Token).szTypeDef;
            if (typeName is "System.Object" or "System.ValueType")
            {
                return false;
            }

            foreach (mdMethodDef methodToken in metaDataImport.EnumMethods(cls.Token))
            {
                (string szMethod, mdTypeDef pClass, CorMethodAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, int pulCodeRVA, int pdwImplFlags) methodProps = metaDataImport.GetMethodProps(methodToken);
                CorMethodAttr methodAttr = methodProps.pdwAttr;
                if (methodProps.szMethod is "ToString" && methodAttr.IsMdStatic() is false && methodAttr.IsMdVirtual() && methodAttr.IsMdNewSlot() is false && Marshal.ReadByte(methodProps.ppvSigBlob, 1) is var parameterCount && parameterCount is 0)
                {
                    return true;
                }
            }
            type = type.Base;
        }
        return false;
    }

    private static ICorDebugValue? GetUnderlyingValueOrNullFromNullableStruct(ICorDebugObjectValue corDebugObjectValue)
    {
        ICorDebugModule module = corDebugObjectValue.Class.Module;
        IMetaDataImport metaDataImport = module.GetMetaDataInterface<IMetaDataImport>();
        mdFieldDef hasValueFieldDef = metaDataImport.FindField(corDebugObjectValue.Class.Token, "hasValue", 0, 0);
        mdFieldDef valueFieldDef = metaDataImport.FindField(corDebugObjectValue.Class.Token, "value", 0, 0);

        ICorDebugValue hasValueDebugObjectValue = corDebugObjectValue.GetFieldValue(corDebugObjectValue.Class, hasValueFieldDef);
        CorDebugValueValueResult hasValueValue = GetCorDebugGenericValue_Value_AsString((ICorDebugGenericValue)hasValueDebugObjectValue.UnwrapDebugValue());
        if (hasValueValue.Value is "false")
        {
            return null;
        }

        ICorDebugValue valueValue = corDebugObjectValue.GetFieldValue(corDebugObjectValue.Class, valueFieldDef);
        return valueValue;
    }

    private static bool TryGetNullableUnderlyingValue(ICorDebugValue corDebugValue, out string nullableTypeName, out ICorDebugValue? underlyingValue)
    {
        nullableTypeName = "";
        underlyingValue = null;
        if (corDebugValue.UnwrapDebugValue() is not ICorDebugObjectValue objectValue)
        {
            return false;
        }

        IMetaDataImport metadata = objectValue.Class.Module.GetMetaDataInterface<IMetaDataImport>();
        if (metadata.GetTypeDefProps(objectValue.Class.Token).szTypeDef is not "System.Nullable`1")
        {
            return false;
        }

        nullableTypeName = GetCorDebugTypeFriendlyName(objectValue.ExactType);
        underlyingValue = GetUnderlyingValueOrNullFromNullableStruct(objectValue);
        return true;
    }

    private readonly record struct ResolvedCorDebugValue(ICorDebugValue? Value, string? FriendlyTypeNameOverride);

    /// <summary>A value as it is shown: a nullable's underlying value under its own type's name, and a union's (C# 15)
    /// the value it holds, as that value's type; either holding none, null.</summary>
    private static ResolvedCorDebugValue ResolveCorDebugValue(ICorDebugValue corDebugValue)
    {
        if (TryGetNullableUnderlyingValue(corDebugValue, out string? nullableTypeName, out ICorDebugValue? underlyingValue))
        {
            return new(underlyingValue, nullableTypeName);
        }
        return TryGetUnionValue(corDebugValue, out string? unionTypeName, out ICorDebugValue? held)
            ? new(held, held is null ? unionTypeName : null)
            : new(corDebugValue, null);
    }

    /// <summary>Whether a value is a union the compiler made ([Union], its value in an auto-property's field), with the
    /// union's type name and the value it holds, null when none.</summary>
    private static bool TryGetUnionValue(ICorDebugValue corDebugValue, out string unionTypeName, out ICorDebugValue? held)
    {
        unionTypeName = "";
        held = null;
        if (corDebugValue.UnwrapDebugValue() is not ICorDebugObjectValue objectValue || objectValue.Type is CorElementType.STRING)
        {
            return false;
        }
        IMetaDataImport metadata = objectValue.Class.Module.GetMetaDataInterface<IMetaDataImport>();
        if (metadata.TryGetCustomAttributeByName(objectValue.Class.Token, "System.Runtime.CompilerServices.UnionAttribute", out _, out _) is not Cor.S_OK
            || metadata.EnumFieldsWithName(objectValue.Class.Token, UnionValueField).SingleOrDefault() is not { IsNil: false } field)
        {
            return false;
        }
        unionTypeName = GetCorDebugTypeFriendlyName(objectValue.ExactType);
        ICorDebugValue value = objectValue.GetFieldValue(objectValue.Class, field);
        held = value is ICorDebugReferenceValue { IsNull: true } ? null : value;
        return true;
    }

    /// <summary>The field a compiler-made union keeps its value in, its Value property's.</summary>
    private const string UnionValueField = "<Value>k__BackingField";

    private CorDebugValueValueResult GetCorDebugReferenceValue_Value_AsString(ICorDebugReferenceValue corDebugReferenceValue, bool escapeStringValue)
    {
        if (corDebugReferenceValue.Type is CorElementType.PTR)
        {
            // A pointer is its address, never the value it points at, which it opens to.
            return new(GetCorDebugTypeFriendlyName(corDebugReferenceValue.ExactType), Address(corDebugReferenceValue), CorDebugValueFormatKind.None, null);
        }
        if (corDebugReferenceValue.IsNull)
        {
            // Get the type information even though the reference is null
            string typeName = GetCorDebugTypeFriendlyName(corDebugReferenceValue.ExactType);
            return new(typeName, "null", CorDebugValueFormatKind.None, null);
        }
        ICorDebugValue referencedValue = corDebugReferenceValue.Dereference();
        CorDebugValueValueResult value = GetValueForCorDebugValue(referencedValue, escapeStringValue);
        return value;
    }

    /// <summary>A pointer's address, as C# writes one in hexadecimal.</summary>
    internal static string Address(ICorDebugReferenceValue pointer) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"0x{pointer.Value.Value:x16}");

    internal static string GetCorDebugTypeFriendlyName(ICorDebugType corDebugType)
    {
        string? primitiveName = GetFriendlyTypeName(corDebugType.Type);
        if (primitiveName is not null)
        {
            return primitiveName;
        }
        if (corDebugType.Type is CorElementType.PTR)
        {
            return $"{GetCorDebugTypeFriendlyName(corDebugType.FirstTypeParameter)}*";
        }

        if (corDebugType.Type is CorElementType.SZARRAY)
        {
            string elementName = GetCorDebugTypeFriendlyName(corDebugType.FirstTypeParameter);
            return $"{elementName}[]";
        }
        if (corDebugType.Type is CorElementType.ARRAY)
        {
            string elementName = GetCorDebugTypeFriendlyName(corDebugType.FirstTypeParameter);
            return $"{elementName}[{new string(',', corDebugType.Rank - 1)}]";
        }
        // Type parameters cover enclosing types too, each level taking as many as its arity (`1, `2), outermost first.
        ICorDebugClass corDebugClass = corDebugType.Class;
        string name = MetadataTypeNames.Of(corDebugClass.Module.GetMetaDataInterface<IMetaDataImport>(), corDebugClass.Token, '.');
        string[] typeArguments = [.. corDebugType.TypeParameters.Select(GetCorDebugTypeFriendlyName)];
        string closed = CSharpTypeNames.Closed(name, typeArguments, spelledNullable: false) ?? name;
        return PrimitiveTypes.Named(closed)?.Keyword ?? closed;
    }

    public static CorDebugValueValueResult GetCorDebugGenericValue_Value_AsString(ICorDebugGenericValue corDebugGenericValue)
    {
        IntPtr buffer = Marshal.AllocHGlobal(corDebugGenericValue.Size);
        try
        {
            corDebugGenericValue.GetValue(buffer);
            string value = corDebugGenericValue.Type switch
            {
                CorElementType.VOID => "void",
                CorElementType.BOOLEAN => Marshal.ReadByte(buffer) != 0 ? "true" : "false",
                CorElementType.CHAR => Marshal.ReadInt16(buffer) is var v ? $"{v} '{(char)v}'" : throw new UnreachableException(),
                CorElementType.I1 => ((sbyte)Marshal.ReadByte(buffer)).ToString(),
                CorElementType.I2 => Marshal.ReadInt16(buffer).ToString(),
                CorElementType.I4 => Marshal.ReadInt32(buffer).ToString(),
                CorElementType.I8 => Marshal.ReadInt64(buffer).ToString(),
                CorElementType.U1 => Marshal.ReadByte(buffer).ToString(),
                CorElementType.U2 => ((ushort)Marshal.ReadInt16(buffer)).ToString(),
                CorElementType.U4 => ((uint)Marshal.ReadInt32(buffer)).ToString(),
                CorElementType.U8 => ((ulong)Marshal.ReadInt64(buffer)).ToString(),
                // Little-endian only.
                CorElementType.R4 => BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(buffer)), 0).ToString(),
                CorElementType.R8 => BitConverter.ToDouble(BitConverter.GetBytes(Marshal.ReadInt64(buffer)), 0).ToString(),
                // As wide as the program's native integers, which the value's own size says.
                CorElementType.I => corDebugGenericValue.Size is 4 ? Marshal.ReadInt32(buffer).ToString() : Marshal.ReadInt64(buffer).ToString(),
                CorElementType.U => corDebugGenericValue.Size is 4 ? ((uint)Marshal.ReadInt32(buffer)).ToString() : ((ulong)Marshal.ReadInt64(buffer)).ToString(),
                CorElementType.STRING => throw new ArgumentOutOfRangeException(),
                CorElementType.PTR => throw new ArgumentOutOfRangeException(),
                CorElementType.BYREF => throw new ArgumentOutOfRangeException(),
                CorElementType.VALUETYPE => throw new NotImplementedException(),
                CorElementType.CLASS => throw new NotImplementedException(),
                _ => throw new ArgumentOutOfRangeException()
            };
            string friendlyTypeName = GetFriendlyTypeName(corDebugGenericValue.Type) ?? throw new ArgumentOutOfRangeException();
            return new(friendlyTypeName, value, CorDebugValueFormatKind.None, null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static object? GetLiteralValue(IntPtr ppValue, CorElementType elementType, int pcchValue)
    {
        if (ppValue == IntPtr.Zero)
        {
            return elementType is CorElementType.STRING or CorElementType.CLASS or CorElementType.OBJECT or CorElementType.VOID ? null : throw new ArgumentNullException(nameof(ppValue));
        }

        object? result = elementType switch
        {
            CorElementType.BOOLEAN => Marshal.ReadByte(ppValue) != 0,
            CorElementType.CHAR => (char)Marshal.ReadInt16(ppValue),
            CorElementType.I1 => (sbyte)Marshal.ReadByte(ppValue),
            CorElementType.I2 => Marshal.ReadInt16(ppValue),
            CorElementType.I4 => Marshal.ReadInt32(ppValue),
            CorElementType.I8 => Marshal.ReadInt64(ppValue),
            CorElementType.U1 => Marshal.ReadByte(ppValue),
            CorElementType.U2 => (ushort)Marshal.ReadInt16(ppValue),
            CorElementType.U4 => (uint)Marshal.ReadInt32(ppValue),
            CorElementType.U8 => (ulong)Marshal.ReadInt64(ppValue),
            CorElementType.R4 => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(ppValue)),
            CorElementType.R8 => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(ppValue)),
            CorElementType.STRING => Marshal.PtrToStringUni(ppValue, pcchValue),
            CorElementType.CLASS => null,
            CorElementType.OBJECT => null,
            CorElementType.VOID => null,
            _ => throw new ArgumentOutOfRangeException(nameof(elementType), $"Unsupported literal type: {elementType}"),
        };
        return result;
    }

    private static string? GetFriendlyTypeName(CorElementType elementType) => MetadataTypeNames.PrimitiveOf(elementType)?.Keyword;
}
