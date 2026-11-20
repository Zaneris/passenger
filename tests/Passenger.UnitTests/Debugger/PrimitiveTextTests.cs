using System.Globalization;
using Passenger.Debugger;
using Passenger.Debugger.Interop;

namespace Passenger.UnitTests.Debugger;

/// <summary>A primitive's row written from its bytes as the runtime holds them, as a local's is and a value a call returned
/// is with no code run in the program: by its type's width, a native integer as wide as its bytes, little-endian.</summary>
public class PrimitiveTextTests
{
    [Fact]
    public void A_bool_a_char_and_whole_numbers_read_as_a_local_of_their_type()
    {
        Assert.Equal("true", ManagedDebugger.PrimitiveText(CorElementType.BOOLEAN, [1]));
        Assert.Equal("false", ManagedDebugger.PrimitiveText(CorElementType.BOOLEAN, [0]));
        Assert.Equal("90 'Z'", ManagedDebugger.PrimitiveText(CorElementType.CHAR, BitConverter.GetBytes('Z')));
        Assert.Equal("42", ManagedDebugger.PrimitiveText(CorElementType.I4, BitConverter.GetBytes(42)));
        Assert.Equal("-7", ManagedDebugger.PrimitiveText(CorElementType.I1, [unchecked((byte)-7)]));
        Assert.Equal("4000000000", ManagedDebugger.PrimitiveText(CorElementType.U4, BitConverter.GetBytes(4000000000u)));
        Assert.Equal("-9000000000", ManagedDebugger.PrimitiveText(CorElementType.I8, BitConverter.GetBytes(-9000000000L)));
    }

    [Fact]
    public void A_real_number_is_written_as_the_debugger_writes_numbers()
    {
        Assert.Equal(0.75.ToString(CultureInfo.CurrentCulture), ManagedDebugger.PrimitiveText(CorElementType.R8, BitConverter.GetBytes(0.75)));
        Assert.Equal(1.5f.ToString(CultureInfo.CurrentCulture), ManagedDebugger.PrimitiveText(CorElementType.R4, BitConverter.GetBytes(1.5f)));
    }

    [Fact]
    public void A_native_integer_is_as_wide_as_the_bytes_the_program_holds_it_in()
    {
        Assert.Equal("-42", ManagedDebugger.PrimitiveText(CorElementType.I, BitConverter.GetBytes(-42)));
        Assert.Equal("-42", ManagedDebugger.PrimitiveText(CorElementType.I, BitConverter.GetBytes(-42L)));
        Assert.Equal("4294967295", ManagedDebugger.PrimitiveText(CorElementType.U, BitConverter.GetBytes(uint.MaxValue)));
        Assert.Equal("4000000000", ManagedDebugger.PrimitiveText(CorElementType.U, BitConverter.GetBytes(4000000000UL)));
    }

    [Fact]
    public void Bytes_short_of_the_types_width_read_the_rest_as_zero()
    {
        Assert.Equal("42", ManagedDebugger.PrimitiveText(CorElementType.I4, [42]));
        Assert.Equal("0", ManagedDebugger.PrimitiveText(CorElementType.U8, []));
    }
}
