using System.Runtime.InteropServices;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public static class CorDebugValueExtensions
{
    public static ICorDebugObjectValue UnwrapDebugValueToObject(this ICorDebugValue corDebugValue)
    {
        ICorDebugValue unwrappedValue = corDebugValue.UnwrapDebugValue();
        if (unwrappedValue is ICorDebugObjectValue objectValue)
        {
            return objectValue;
        }
        throw new InvalidOperationException("CorDebugValue is not an CorDebugObjectValue");
    }

    public static ICorDebugObjectValue? UnwrapDebugValueToObjectOrNull(this ICorDebugValue corDebugValue)
    {
        ICorDebugValue unwrappedValue = corDebugValue.UnwrapDebugValue();
        if (unwrappedValue is ICorDebugObjectValue objectValue)
        {
            return objectValue;
        }
        return null;
    }

    public static ICorDebugValue UnwrapDebugValue(this ICorDebugValue corDebugValue)
    {
        ICorDebugValue valueToCheck = corDebugValue;
        if (valueToCheck is ICorDebugReferenceValue { IsNull: false } refValue)
        {
            valueToCheck = refValue.Dereference();
        }
        if (valueToCheck is ICorDebugBoxValue boxValue)
        {
            valueToCheck = boxValue.Object;
        }

        return valueToCheck;
    }

    public static byte[] GetValueAsBytes(this ICorDebugGenericValue corDebugGenericValue)
    {
        IntPtr buffer = Marshal.AllocHGlobal(corDebugGenericValue.Size);
        try
        {
            corDebugGenericValue.GetValue(buffer);
            byte[] result = new byte[corDebugGenericValue.Size];
            Marshal.Copy(buffer, result, 0, corDebugGenericValue.Size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Writes a value's bytes from <paramref name="bytes"/>, the one way any value is written: what writes it takes
    /// the value's whole size from where it is pointed, so a value larger than the bytes given is refused, saying so.</summary>
    public static unsafe void SetValue(this ICorDebugGenericValue value, ReadOnlySpan<byte> bytes)
    {
        int size = value.Size;
        if (bytes.Length < size)
        {
            throw new InvalidOperationException($"The value takes {size} bytes, more than the {bytes.Length} written to it");
        }
        fixed (byte* from = bytes)
        {
            Marshal.ThrowExceptionForHR(value.TrySetValue((nint)from));
        }
    }

    /// <summary>How many bytes a constant of <paramref name="type"/> holds, as metadata writes its value; null for a string,
    /// whose length the metadata gives, and for a null reference, the one other constant.</summary>
    internal static int? ConstantWidth(CorElementType type) => type switch
    {
        CorElementType.BOOLEAN or CorElementType.I1 or CorElementType.U1 => 1,
        CorElementType.CHAR or CorElementType.I2 or CorElementType.U2 => 2,
        CorElementType.I4 or CorElementType.U4 or CorElementType.R4 => 4,
        CorElementType.I8 or CorElementType.U8 or CorElementType.R8 => 8,
        _ => null,
    };

    public static ICorDebugValue? GetClassFieldValue(this ICorDebugObjectValue objectValue, ICorDebugILFrame ilFrame, string fieldName)
    {
        ICorDebugType? currentType = objectValue.ExactType;
        mdFieldDef foundFieldDef = default;
        ICorDebugClass? foundClass = null;
        IMetaDataImport? foundMetadata = null;

        while (currentType is not null)
        {
            ICorDebugClass cls = currentType.Class;
            IMetaDataImport meta = cls.Module.GetMetaDataInterface<IMetaDataImport>();
            mdFieldDef field = meta.EnumFieldsWithName(cls.Token, fieldName).SingleOrDefault();
            if (field.IsNil is false)
            {
                foundFieldDef = field;
                foundClass = cls;
                foundMetadata = meta;
                break;
            }
            currentType = currentType.Base;
        }

        if (foundClass is null || foundMetadata is null || currentType is null || foundFieldDef.IsNil)
        {
            return null;
        }

        bool isStatic = foundFieldDef.IsStatic(foundMetadata);
        bool isLiteral = foundFieldDef.IsLiteral(foundMetadata);
        ICorDebugValue fieldCorDebugValue = isLiteral ? foundFieldDef.GetLiteralCorDebugValue(foundMetadata, ilFrame) : isStatic ? currentType.GetStaticFieldValue(foundFieldDef, ilFrame) : objectValue.GetFieldValue(foundClass, foundFieldDef);
        return fieldCorDebugValue;
    }

    /// <summary>A constant field's value made in the program, its bytes the constant's own; a null reference's is the null
    /// the runtime makes.</summary>
    public static unsafe ICorDebugGenericValue GetLiteralCorDebugValue(this mdFieldDef fieldDef, IMetaDataImport metadataImport, ICorDebugILFrame ilFrame)
    {
        (string szField, mdTypeDef pClass, CorFieldAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, CorElementType pdwCPlusTypeFlag, nint ppValue, int pcchValue) fieldProps = metadataImport.GetFieldProps(fieldDef);
        nint ppValue = fieldProps.ppValue;
        CorElementType corElementType = fieldProps.pdwCPlusTypeFlag;
        ICorDebugEval eval = ilFrame.Chain.Thread.CreateEval();
        ICorDebugValue createdValue = eval.CreateValue(corElementType, null);
        if (createdValue is not ICorDebugGenericValue corDebugGenericValue)
        {
            throw new InvalidOperationException("Expected a CorDebugGenericValue for literal value");
        }
        if (ConstantWidth(corElementType) is int width)
        {
            corDebugGenericValue.SetValue(ppValue != 0 ? new ReadOnlySpan<byte>((void*)ppValue, width) : throw new InvalidOperationException($"The constant {fieldProps.szField} has no value"));
        }
        return corDebugGenericValue;
    }

    internal static async Task<ICorDebugValue?> GetPropertyValue(this ICorDebugValue objectValue, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, ICorDebugILFrame ilFrame, string propertyName)
    {
        return (await GetPropertyValueWithSetter(objectValue, processEventsUntilEvalEventFunc, evalStatus, ilFrame, propertyName))?.Value;
    }

    internal static async Task<(ICorDebugValue Value, ICorDebugFunction? SetterFunction)?> GetPropertyValueWithSetter(this ICorDebugValue objectValue, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, ICorDebugILFrame ilFrame, string propertyName)
    {
        ICorDebugObjectValue unwrappedValue = objectValue.UnwrapDebugValueToObject();

        ICorDebugType? currentType = unwrappedValue.ExactType;
        mdProperty foundPropertyDef = default;
        ICorDebugClass? foundClass = null;
        IMetaDataImport? foundMetadata = null;

        while (currentType is not null)
        {
            ICorDebugClass cls = currentType.Class;
            IMetaDataImport meta = cls.Module.GetMetaDataInterface<IMetaDataImport>();
            mdProperty? prop = meta.GetPropertyWithName(cls.Token, propertyName);
            if (prop?.IsNil is false)
            {
                foundPropertyDef = prop.Value;
                foundClass = cls;
                foundMetadata = meta;
                break;
            }
            currentType = currentType.Base;
        }

        if (foundClass is null || foundMetadata is null || foundPropertyDef.IsNil)
        {
            return null;
        }

        (string szProperty, mdTypeDef pClass, CorPropertyAttr pdwPropFlags, nint ppvSig, int pbSig, CorElementType pdwCPlusTypeFlag, nint ppDefaultValue, int pcchDefaultValue, mdMethodDef pmdSetter, mdMethodDef pmdGetter, mdMethodDef rmdOtherMethod, int pcOtherMethod) propertyProps = foundMetadata.GetPropertyProps(foundPropertyDef);
        mdMethodDef getMethodDef = propertyProps.pmdGetter;
        if (getMethodDef == mdMethodDef.Nil)
        {
            return null;
        }

        (string szMethod, mdTypeDef pClass, CorMethodAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, int pulCodeRVA, int pdwImplFlags) getterMethodProps = foundMetadata.GetMethodProps(getMethodDef);
        CorMethodAttr getterAttr = getterMethodProps.pdwAttr;

        bool isStatic = getterAttr.IsMdStatic();

        ICorDebugFunction getMethod = foundClass.Module.GetFunctionFromToken(getMethodDef);
        mdMethodDef setMethodDef = propertyProps.pmdSetter;
        ICorDebugFunction? setterFunction = setMethodDef == mdMethodDef.Nil ? null : foundClass.Module.GetFunctionFromToken(setMethodDef);
        ICorDebugEval eval = ilFrame.Chain.Thread.CreateEval();

        ICorDebugType parameterizedContainingType = objectValue.ExactType;

        ICorDebugType[] typeParameterTypes = parameterizedContainingType.TypeParameters;

        // An instance property is given the original reference value, never the dereferenced one.
        ICorDebugValue[] corDebugValues = isStatic ? [] : [objectValue];

        ICorDebugValue? returnValue = await eval.CallParameterizedFunctionAsync(processEventsUntilEvalEventFunc, evalStatus, getMethod, typeParameterTypes.Length, typeParameterTypes, corDebugValues.Length, corDebugValues);
        return returnValue is null ? null : (returnValue, setterFunction);
    }

    public static bool IsExceptionType(this ICorDebugType corDebugType)
    {
        ICorDebugType? type = corDebugType;
        while (type is not null)
        {
            if (ManagedDebugger.GetCorDebugTypeFriendlyName(type) == "System.Exception")
            {
                return true;
            }

            type = type.Base;
        }
        return false;
    }

    public static string? GetThreadNameOrNull(this ICorDebugThread thread)
    {
        // GetObject fails with CORDBG_E_BAD_THREAD_STATE while a thread has no managed Thread object yet
        if (thread.TryGetObject(out ICorDebugValue? threadObjectValue) is not Cor.S_OK)
        {
            return null;
        }

        if (threadObjectValue.UnwrapDebugValue() is not ICorDebugObjectValue threadObject)
        {
            return null;
        }

        ICorDebugClass corClass = threadObject.Class;
        IMetaDataImport metadataImport = corClass.Module.GetMetaDataInterface<IMetaDataImport>();

        // Only the name the program gave it: Main's thread is known from the runtime's first thread-created callback.
        string? name = GetThreadNameField(threadObject, corClass, metadataImport);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string? GetThreadNameField(ICorDebugObjectValue threadObject, ICorDebugClass corClass, IMetaDataImport metadataImport)
    {
        mdFieldDef fieldDef = metadataImport.EnumFieldsWithName(corClass.Token, "_name").SingleOrDefault();
        if (fieldDef.IsNil)
        {
            return null;
        }

        ICorDebugValue fieldValue = threadObject.GetFieldValue(corClass, fieldDef);
        if (fieldValue?.UnwrapDebugValue() is not ICorDebugStringValue stringValue)
        {
            return null;
        }

        return stringValue.String;
    }

}
