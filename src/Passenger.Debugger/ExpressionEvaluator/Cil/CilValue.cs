using Passenger.Debugger.Interop;

namespace Passenger.Debugger.ExpressionEvaluator.Cil;

internal sealed class CilValue
{
    /// <summary>The debuggee's pointer size, for native integers: its runtime's, set when CoreLib loads, not the
    /// debugger's own (a 32-bit program under a 64-bit debugger). A static of the adapter's process, which debugs one
    /// program.</summary>
    public static int PointerSize { get; set; } = IntPtr.Size;

    /// <summary>The major version of the debuggee's runtime, its CoreLib's, set when CoreLib loads: how its numbers convert
    /// follows it.</summary>
    public static int RuntimeVersion { get; set; } = Environment.Version.Major;

    private CilValue(object? value, ICorDebugValue? corValue, ICilLocation? location = null, ICilLocation? sourceLocation = null, bool inBox = false)
    {
        Value = value;
        CorValue = corValue;
        Location = location;
        SourceLocation = sourceLocation;
        _inBox = inBox;
    }

    /// <summary>Whether <see cref="CorValue"/> is a box the evaluation made to hold a struct, standing for the struct
    /// rather than for an object.</summary>
    private readonly bool _inBox;

    public object? Value { get; }
    public ICorDebugValue? CorValue { get; }
    public ICilLocation? Location { get; }
    public ICilLocation? SourceLocation { get; }
    public bool IsNull => Value is null && (CorValue is null || CorValue is ICorDebugReferenceValue { IsNull: true });

    public static CilValue FromPrimitive(object value) => new(value, null);
    public static CilValue FromVirtual(object value) => new(value, null);
    public static CilValue FromTypeToken(ResolvedCilType type, ICorDebugValue value) => new(type, value);
    public static CilValue FromCorValue(ICorDebugValue value)
    {
        object? primitive = ReadPrimitive(value);
        return primitive is null ? new(null, value) : new(primitive, null);
    }
    public static CilValue FromLocation(ICilLocation location) => new(null, null, location);
    /// <summary>A struct the evaluation holds in a box of its own.</summary>
    public static CilValue InBox(ICorDebugValue box) => new(null, box, inBox: true);
    public static CilValue Null() => new(null, null);
    /// <summary>
    /// Wraps a debuggee value without collapsing primitives (e.g. strings) to host objects.
    /// Used for values being stored into the debuggee, where the underlying ICorDebugValue must be preserved.
    /// </summary>
    public static CilValue FromDebuggeeValue(ICorDebugValue value) => new(null, value);

    /// <summary>
    /// Returns the string text for either a host string (<see cref="Value"/>) or a debuggee string value
    /// (<see cref="CorValue"/>), without discarding the original debuggee reference.
    /// </summary>
    public string? GetStringText() => Value as string ?? (CorValue?.UnwrapDebugValue() as ICorDebugStringValue)?.String;

    public CilValue WithSourceLocation(ICilLocation location) => new(Value, CorValue, Location, location, _inBox);

    /// <summary>The struct the value is, where it lives in the program or in the evaluation's box for it: a value a call
    /// or a field's store can change there. Null for any other value: an object that boxes a struct, an enum, and a
    /// primitive as unboxed from an object.</summary>
    public ICorDebugGenericValue? Struct => Location is null && (_inBox || CorValue is not ICorDebugReferenceValue)
        && CorValue?.UnwrapDebugValue() is ICorDebugGenericValue { Type: CorElementType.VALUETYPE } generic
        && UnderlyingType(generic) is null && PrimitiveClassName(generic) is null
        ? generic
        : null;

    /// <summary>Where a store through the value writes: the managed location it is, or what a pointer points at; null for
    /// any other value, a null pointer among them.</summary>
    public ICilLocation? Target => Location
        ?? (CorValue is ICorDebugReferenceValue { Type: CorElementType.PTR, IsNull: false } pointer ? new CorDebugLocation(pointer.Dereference()) : null);

    /// <summary>What a managed location holds, or what a pointer points at.</summary>
    public CilValue Dereference() => Location?.Read()
        ?? (CorValue is ICorDebugReferenceValue { Type: CorElementType.PTR, IsNull: false } pointer ? FromCorValue(pointer.Dereference()) : null)
        ?? throw new InvalidOperationException("CIL value is not a managed location");

    public int AsInt32() => Value switch
    {
        bool value => value ? 1 : 0,
        char value => value,
        sbyte value => value,
        byte value => value,
        short value => value,
        ushort value => value,
        int value => value,
        uint value => unchecked((int)value),
        _ when TryReadValueTypeInteger(out long integer) => unchecked((int)integer),
        _ => throw new InvalidOperationException($"Value '{Value?.GetType().Name ?? "null"}' is not an int32 stack value")
    };

    public long AsInt64() => Value switch
    {
        long value => value,
        ulong value => unchecked((long)value),
        _ when TryReadValueTypeInteger(out long integer) => integer,
        _ => AsInt32()
    };

    /// <summary>The integer's bits read as unsigned, as wide as it is on the evaluation stack: an int32's -1 is 2^32 - 1.</summary>
    public ulong AsUInt64() => IsInt64 ? unchecked((ulong)AsInt64()) : unchecked((uint)AsInt32());

    /// <summary>Whether the value is an int64 on the evaluation stack: a long or ulong, a 64-bit program's native integer,
    /// or an enum over one; any other integer is an int32 there.</summary>
    public bool IsInt64 => Value is long or ulong
        || (Value is null && CorValue?.UnwrapDebugValue() is ICorDebugGenericValue { Type: CorElementType.VALUETYPE } generic && generic.Size == 8);

    public bool TryGetInt64(out long value)
    {
        try
        {
            value = AsInt64();
            return true;
        }
        catch (InvalidOperationException)
        {
            value = 0;
            return false;
        }
    }

    public double AsFloat() => Value switch
    {
        float value => value,
        double value => value,
        _ => throw new InvalidOperationException($"Value '{Value?.GetType().Name ?? "null"}' is not a floating-point stack value")
    };

    public bool IsTrue() => CorValue is ICorDebugReferenceValue reference
        ? !reference.IsNull
        : CorValue is not null
            ? true
            : Value switch
            {
                null => false,
                bool value => value,
                float value => value != 0,
                double value => value != 0,
                long value => value != 0,
                ulong value => value != 0,
                string => true,
                _ => AsInt32() != 0
            };

    private static object? ReadPrimitive(ICorDebugValue value)
    {
        if (value is ICorDebugReferenceValue { Type: CorElementType.PTR })
        {
            // A pointer stays one, its address, until an indirection reads what it points at.
            return null;
        }
        ICorDebugValue unwrapped = value.UnwrapDebugValue();
        if (unwrapped is not ICorDebugGenericValue generic)
        {
            return null;
        }

        byte[] data = generic.GetValueAsBytes();
        return generic.Type switch
        {
            CorElementType.BOOLEAN => data[0] != 0,
            CorElementType.CHAR => BitConverter.ToChar(data),
            CorElementType.I1 => unchecked((sbyte)data[0]),
            CorElementType.U1 => data[0],
            CorElementType.I2 => BitConverter.ToInt16(data),
            CorElementType.U2 => BitConverter.ToUInt16(data),
            CorElementType.I4 => BitConverter.ToInt32(data),
            CorElementType.U4 => BitConverter.ToUInt32(data),
            CorElementType.I8 => BitConverter.ToInt64(data),
            CorElementType.U8 => BitConverter.ToUInt64(data),
            CorElementType.R4 => BitConverter.ToSingle(data),
            CorElementType.R8 => BitConverter.ToDouble(data),
            // As wide as the debuggee's native integers are, which its value's own size says.
            CorElementType.I => data.Length == 8 ? BitConverter.ToInt64(data) : BitConverter.ToInt32(data),
            CorElementType.U => data.Length == 8 ? BitConverter.ToUInt64(data) : BitConverter.ToUInt32(data),
            // What a box of a floating-point value holds, unboxed (a reference to the box stays one): as its class.
            CorElementType.VALUETYPE when ReferenceEquals(unwrapped, value) => PrimitiveClassName(value) switch
            {
                "System.Double" => BitConverter.ToDouble(data),
                "System.Single" => BitConverter.ToSingle(data),
                _ => null,
            },
            _ => null
        };
    }

    /// <summary>The name of a value's class when it is one of CoreLib's primitive types, as a box of one holds it (the
    /// floating-point ones no integer reading covers); null for any other.</summary>
    private static string? PrimitiveClassName(ICorDebugValue value)
    {
        try
        {
            ICorDebugClass corClass = value.ExactType.Class;
            string name = corClass.Module.GetMetaDataInterface<IMetaDataImport>().GetTypeDefProps(corClass.Token).szTypeDef;
            return name is "System.Double" or "System.Single" or "System.Boolean" or "System.Char" or "System.SByte" or "System.Byte"
                or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64"
                or "System.IntPtr" or "System.UIntPtr" ? name : null;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private bool TryReadValueTypeInteger(out long value)
    {
        value = 0;
        if (CorValue?.UnwrapDebugValue() is not ICorDebugGenericValue { Type: CorElementType.VALUETYPE } generic)
        {
            return false;
        }

        byte[] data = generic.GetValueAsBytes();
        // An enum reads as its underlying type: an sbyte enum's -1 is not 255, nor a ushort enum's 65535 -1.
        bool unsigned = UnderlyingType(CorValue) is CorElementType.U1 or CorElementType.U2 or CorElementType.U4 or CorElementType.U8 or CorElementType.CHAR or CorElementType.BOOLEAN;
        value = data.Length switch
        {
            1 => unsigned ? data[0] : unchecked((sbyte)data[0]),
            2 => unsigned ? BitConverter.ToUInt16(data) : BitConverter.ToInt16(data),
            4 => unsigned ? BitConverter.ToUInt32(data) : BitConverter.ToInt32(data),
            8 => BitConverter.ToInt64(data),
            _ => 0
        };
        return data.Length is 1 or 2 or 4 or 8;
    }

    /// <summary>An enum's underlying element type, from its value__ field's signature; null for a struct
    /// that is not an enum.</summary>
    private static CorElementType? UnderlyingType(ICorDebugValue value)
    {
        try
        {
            ICorDebugClass corClass = value.ExactType.Class;
            IMetaDataImport metadata = corClass.Module.GetMetaDataInterface<IMetaDataImport>();
            mdFieldDef field = metadata.FindField(corClass.Token, "value__", 0, 0);
            nint signature = metadata.GetFieldProps(field).ppvSigBlob;
            // A field signature is FIELD (0x06), then the field's type.
            return (CorElementType)System.Runtime.InteropServices.Marshal.ReadByte(signature, 1);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }
}

internal interface ICilLocation
{
    CilValue Read();
    void Write(CilValue value);
}

/// <summary>A location whose storage is a debuggee value (a by-ref argument passes it, a field read goes through it).</summary>
internal interface IDebuggeeLocation : ICilLocation
{
    ICorDebugValue Value { get; }
}

/// <summary>A local or an argument of the frame, fetched afresh each time: what ICorDebug hands out for a slot is a
/// snapshot, so a struct written through one copy would read back as it was ("sum = 10m" showing the old sum).</summary>
internal sealed class FrameSlotLocation(ICorDebugILFrame frame, int index, bool argument, Func<ICorDebugILFrame> refind) : IDebuggeeLocation
{
    public ICorDebugValue Value
    {
        get
        {
            try
            {
                return Slot(frame);
            }
            catch (System.Runtime.InteropServices.COMException error) when (error.HResult == Cor.CORDBG_E_OBJECT_NEUTERED)
            {
                // A function evaluation ran the program and neutered the frame: the same frame, found again.
                frame = refind();
                return Slot(frame);
            }
        }
    }

    public CilValue Read() => new CorDebugLocation(Value).Read();
    public void Write(CilValue value) => new CorDebugLocation(Value).Write(value);

    private ICorDebugValue Slot(ICorDebugILFrame at) => argument ? at.GetArgument(index) : at.GetLocalVariable(index);
}

internal sealed class CorDebugLocation(ICorDebugValue value) : IDebuggeeLocation
{
    public ICorDebugValue Value => value;
    public CilValue Read() => CilValue.FromCorValue(GetStorageValue());
    public void Write(CilValue source)
    {
        ICorDebugValue storage = GetStorageValue();
        if (storage is ICorDebugReferenceValue slot && IsReference(storage.Type))
        {
            // An object holding a box is given another reference, never a new value in the box another may share.
            WriteReference(slot, source);
            return;
        }
        ICorDebugValue destination = storage.UnwrapDebugValue();

        // Value-typed storage, told by the destination.
        if (destination is ICorDebugGenericValue destinationGeneric && IsValueType(destinationGeneric.Type))
        {
            if (source.CorValue?.UnwrapDebugValue() is ICorDebugGenericValue sourceGeneric)
            {
                if (destinationGeneric.Size != sourceGeneric.Size)
                {
                    throw new InvalidOperationException("CIL value sizes do not match");
                }

                byte[] sourceData = sourceGeneric.GetValueAsBytes();
                unsafe
                {
                    fixed (byte* pointer = sourceData)
                    {
                        destinationGeneric.SetValue((nint)pointer);
                    }
                }
                return;
            }
            if (source.Value is not null)
            {
                byte[] data = destinationGeneric.Type == CorElementType.VALUETYPE
                    ? CilValueEncoding.GetBytesForSize(source.Value, destinationGeneric.Size)
                    : CilValueEncoding.GetBytes(source.Value, destinationGeneric.Type);
                unsafe
                {
                    fixed (byte* pointer = data)
                    {
                        destinationGeneric.SetValue((nint)pointer);
                    }
                }
                return;
            }
            if (source.IsNull)
            {
                byte[] zeroed = new byte[destinationGeneric.Size];
                unsafe
                {
                    fixed (byte* pointer = zeroed)
                    {
                        destinationGeneric.SetValue((nint)pointer);
                    }
                }
                return;
            }
            throw new NotSupportedException("The CIL value cannot be stored in this debuggee location");
        }

        if (storage is ICorDebugReferenceValue destinationReference)
        {
            WriteReference(destinationReference, source);
            return;
        }

        throw new NotSupportedException("The CIL value cannot be stored in this debuggee location");
    }

    /// <summary>A reference-typed slot written itself, never its target (a string's data, an object's box); null zeroes it.</summary>
    private static void WriteReference(ICorDebugReferenceValue destination, CilValue source)
    {
        if (source.IsNull)
        {
            destination.Value = default;
            return;
        }
        if (source.CorValue is ICorDebugReferenceValue sourceReference)
        {
            destination.Value = sourceReference.Value;
            return;
        }
        if (source.CorValue is ICorDebugHeapValue2 sourceHeap)
        {
            ICorDebugHandleValue handle = sourceHeap.CreateHandle(CorDebugHandleType.HANDLE_STRONG);
            try
            {
                destination.Value = handle.Value;
            }
            finally
            {
                handle.TryDispose();
            }
            return;
        }
        throw new NotSupportedException("Cannot store a non-reference CIL value in a reference debuggee location");
    }

    private ICorDebugValue GetStorageValue() => value is ICorDebugReferenceValue { Type: CorElementType.BYREF } byRef
        ? byRef.Dereference()
        : value;

    /// <summary>Whether a slot's own type is a reference's (an object, an interface, ValueType, a string, an array), whatever
    /// the value it refers to is.</summary>
    private static bool IsReference(CorElementType elementType) => elementType is
        CorElementType.CLASS or CorElementType.OBJECT or CorElementType.STRING or CorElementType.SZARRAY or CorElementType.ARRAY;

    private static bool IsValueType(CorElementType elementType) => elementType is
        CorElementType.VALUETYPE or
        CorElementType.BOOLEAN or
        CorElementType.CHAR or
        CorElementType.I1 or
        CorElementType.U1 or
        CorElementType.I2 or
        CorElementType.U2 or
        CorElementType.I4 or
        CorElementType.U4 or
        CorElementType.I8 or
        CorElementType.U8 or
        CorElementType.R4 or
        CorElementType.R8 or
        CorElementType.I or
        CorElementType.U;
}

/// <summary>A variable or argument of the evaluation's own, or a field of an object its compilation made.</summary>
internal sealed class TemporaryLocation(CilValue initialValue, Func<ResolvedCilType?>? declaredType = null) : ICilLocation
{
    private CilValue _value = initialValue;
    public CilValue Read() => _value;
    public void Write(CilValue value)
    {
        _value = value;
        Written = true;
    }

    /// <summary>Whether anything has been stored in it: a local holds its type's zero until then, as the runtime's do.</summary>
    public bool Written { get; private set; }

    /// <summary>The type a local of the evaluation's own is declared as, when the debugger can resolve it.</summary>
    public ResolvedCilType? DeclaredType => declaredType?.Invoke();
}

internal sealed class SyntheticVariableLocation(ICorDebugValue arrayReference) : ICilLocation
{
    public ICorDebugValue ArrayReference { get; } = arrayReference;
    public ICorDebugValue StorageValue => ((ICorDebugArrayValue)ArrayReference.UnwrapDebugValue()).GetElementAtPosition(0);
    public CilValue Read() => CilValue.FromCorValue(StorageValue);
    public void Write(CilValue value) => new CorDebugLocation(StorageValue).Write(value);
}

/// <summary>A stack value as the bytes of the type it is stored as. An integer keeps only the bits that fit, as a CIL
/// store does: an int32's -1 stored in a uint is uint.MaxValue, not an overflow.</summary>
internal static class CilValueEncoding
{
    public static byte[] GetBytesForSize(object value, int size)
    {
        long bits = Bits(value);
        return size switch
        {
            1 => [unchecked((byte)bits)],
            2 => BitConverter.GetBytes(unchecked((short)bits)),
            4 => BitConverter.GetBytes(unchecked((int)bits)),
            8 => BitConverter.GetBytes(bits),
            _ => throw new NotSupportedException($"Cannot encode a primitive CIL value into a {size}-byte value type")
        };
    }

    public static byte[] GetBytes(object value, CorElementType targetType) => targetType switch
    {
        CorElementType.BOOLEAN => [(value is bool flag ? flag : Bits(value) != 0) ? (byte)1 : (byte)0],
        CorElementType.I1 or CorElementType.U1 => GetBytesForSize(value, 1),
        CorElementType.CHAR or CorElementType.I2 or CorElementType.U2 => GetBytesForSize(value, 2),
        CorElementType.I4 or CorElementType.U4 => GetBytesForSize(value, 4),
        CorElementType.I8 or CorElementType.U8 => GetBytesForSize(value, 8),
        CorElementType.I or CorElementType.U => GetBytesForSize(value, CilValue.PointerSize),
        CorElementType.R4 => BitConverter.GetBytes(Convert.ToSingle(value)),
        CorElementType.R8 => BitConverter.GetBytes(Convert.ToDouble(value)),
        _ => throw new NotSupportedException($"Cannot encode a primitive CIL value as '{targetType}'")
    };

    private static long Bits(object value) => value switch
    {
        bool flag => flag ? 1 : 0,
        ulong large => unchecked((long)large),
        _ => Convert.ToInt64(value),
    };
}
