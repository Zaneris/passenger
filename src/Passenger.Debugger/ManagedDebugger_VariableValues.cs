using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis.CSharp;
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
            ICorDebugModule module = valueToProxy.ExactType.Class.Module;
            proxyInstance = await CreateDebuggerProxyInstance(valueToProxy, threadId, module, debuggerProxyTypeName, valueToProxy.ExactType.TypeParameters);
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
        if (EvalStatus.ImplicitOff is not null && !EvalStatus.Explicit && formatKind is CorDebugValueFormatKind.ToString or CorDebugValueFormatKind.DebuggerDisplay)
        {
            // Neither its ToString nor its [DebuggerDisplay] runs while implicit evaluation is off: its type in braces.
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
            ICorDebugType[] typeParameters = valueToFormat.ExactType.TypeParameters;
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
                (value, resultIsError) = await RenderDebuggerDisplayAsync(display.Value, valueToFormat, threadId, frameStackDepth, nesting);
                if (display.Name is { } nameFormat)
                {
                    (displayName, _) = await RenderDebuggerDisplayAsync(nameFormat, valueToFormat, threadId, frameStackDepth, nesting);
                }
            }
            friendlyTypeName = display.Type ?? friendlyTypeName;
        }
        return new FormattedValue(friendlyTypeName, value, debuggerProxyTypeName, resultIsError, displayName);
    }

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

    private static CorDebugValueValueResult GetValueForCorDebugValue(ICorDebugValue corDebugValue, bool escapeStringValue)
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

    public static CorDebugValueValueResult GetCorDebugBoxValue_Value_AsString(ICorDebugBoxValue corDebugBoxValue, bool escapeStringValue)
    {
        ICorDebugObjectValue unboxedValue = corDebugBoxValue.Object;
        CorDebugValueValueResult value = GetValueForCorDebugValue(unboxedValue, escapeStringValue);
        return value;
    }

    public static CorDebugValueValueResult GetCorDebugObjectValue_Value_AsString(ICorDebugObjectValue corDebugObjectValue, bool escapeStringValue)
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

        string? debugProxyTypeName = hasDebuggerTypeProxyAttribute ? GetCustomAttributeResultString(debuggerTypeProxyAttributePointer, debuggerTypeProxyAttributeSize) : null;
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
            return new(typeName, "", CorDebugValueFormatKind.ToString, debugProxyTypeName);
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
        CorDebugValueValueResult hasValueValue = GetValueForCorDebugValue(hasValueDebugObjectValue, false);
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

    public static CorDebugValueValueResult GetCorDebugReferenceValue_Value_AsString(ICorDebugReferenceValue corDebugReferenceValue, bool escapeStringValue)
    {
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

    internal static string GetCorDebugTypeFriendlyName(ICorDebugType corDebugType)
    {
        string? primitiveName = GetFriendlyTypeName(corDebugType.Type);
        if (primitiveName is not null)
        {
            return primitiveName;
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
        ICorDebugClass corDebugClass = corDebugType.Class;
        // Type parameters cover enclosing types too, each level taking as many as its arity (`1, `2), outermost first.
        List<ICorDebugType> typeParameters = corDebugType.TypeParameters.ToList();
        string name = GetCorDebugTypeFriendlyNameInternal(corDebugClass, typeParameters);
        return name;
    }

    private static string GetCorDebugTypeFriendlyNameInternal(ICorDebugClass corDebugClass, List<ICorDebugType> typeParameterTypes)
    {
        ICorDebugModule module = corDebugClass.Module;
        mdTypeDef token = corDebugClass.Token;
        IMetaDataImport metadataImport = module.GetMetaDataInterface<IMetaDataImport>();
        (string szTypeDef, CorTypeAttr pdwTypeDefFlags, mdToken ptkExtends) typeDefProps = metadataImport.GetTypeDefProps(token);
        string typeName = typeDefProps.szTypeDef;
        bool isNested = typeDefProps.pdwTypeDefFlags.IsTdNested();

        string? parentTypeName = null;
        if (isNested)
        {
            mdTypeDef parentTypeDef = metadataImport.GetNestedClassProps(token);
            ICorDebugClass parentTypeCorDebugClass = module.GetClassFromToken(parentTypeDef);
            parentTypeName = GetCorDebugTypeFriendlyNameInternal(parentTypeCorDebugClass, typeParameterTypes);
        }

        // Reached first by the outermost type, which takes the type parameters its arity names.

        int backtickIndex = typeName.LastIndexOf('`');
        bool typeHasTypeParameters = backtickIndex is not -1;
        if (typeHasTypeParameters)
        {
            ReadOnlySpan<char> typeNameAsSpan = typeName.AsSpan();
            ReadOnlySpan<char> aritySpan = typeNameAsSpan[(backtickIndex + 1)..];
            if (int.TryParse(aritySpan, out int arity) is false)
            {
                throw new InvalidOperationException("Failed to parse generic type arity from type name");
            }

            ImmutableArray<string> typeParametersFriendlyNamesForType = typeParameterTypes.Take(arity).Select(GetCorDebugTypeFriendlyName).ToImmutableArray();
            typeParameterTypes.RemoveRange(0, arity);
            typeName = $"{typeName[..backtickIndex]}<{string.Join(", ", typeParametersFriendlyNamesForType)}>";
        }

        if (typeName.StartsWith("System.Nullable<")) // unwrap System.Nullable<int> to int?
        {
            ReadOnlySpan<char> span = typeName.AsSpan();
            int openingIndex = span.IndexOf('<');
            int closingIndex = span.LastIndexOf('>');
            ReadOnlySpan<char> underlyingType = span.Slice(openingIndex + 1, closingIndex - openingIndex - 1);
            typeName = $"{underlyingType}?";
        }

        string languageAlias = ClassNameToMaybeLanguageAlias(typeName);
        return isNested ? $"{parentTypeName}.{languageAlias}" : languageAlias;
    }

    private static string ClassNameToMaybeLanguageAlias(string className)
    {
        className = className switch
        {
            "System.String" => "string",
            "System.Object" => "object",
            "System.Decimal" => "decimal",

            // These will be hit in the case that a primitive is boxed, e.g. object myInt = 4;
            "System.Boolean" => "bool",
            "System.Byte" => "byte",
            "System.SByte" => "sbyte",
            "System.Char" => "char",
            "System.Int16" => "short",
            "System.UInt16" => "ushort",
            "System.Int32" => "int",
            "System.UInt32" => "uint",
            "System.Int64" => "long",
            "System.UInt64" => "ulong",
            "System.Single" => "float",
            "System.Double" => "double",
            "System.IntPtr" => "nint",
            "System.UIntPtr" => "nuint",

            _ => className
        };
        return className;
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
                // native integer
                CorElementType.I => IntPtr.Size is 4 ? Marshal.ReadInt32(buffer).ToString() : Marshal.ReadInt64(buffer).ToString(),
                CorElementType.U => IntPtr.Size is 4 ? ((uint)Marshal.ReadInt32(buffer)).ToString() : ((ulong)Marshal.ReadInt64(buffer)).ToString(),
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

    private static string? GetFriendlyTypeName(CorElementType elementType)
    {
        return elementType switch
        {
            CorElementType.VOID => "void",
            CorElementType.BOOLEAN => "bool",
            CorElementType.CHAR => "char",
            CorElementType.I1 => "sbyte",
            CorElementType.U1 => "byte",
            CorElementType.I2 => "short",
            CorElementType.U2 => "ushort",
            CorElementType.I4 => "int",
            CorElementType.U4 => "uint",
            CorElementType.I8 => "long",
            CorElementType.U8 => "ulong",
            CorElementType.R4 => "float",
            CorElementType.R8 => "double",
            CorElementType.STRING => "string",
            CorElementType.OBJECT => "object",
            CorElementType.I => "nint",
            CorElementType.U => "nuint",
            _ => null
        };
    }
}
