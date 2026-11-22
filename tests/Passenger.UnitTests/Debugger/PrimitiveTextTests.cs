using System.Globalization;
using Passenger.Debugger;
using Passenger.Debugger.Interop;

namespace Passenger.UnitTests.Debugger;

/// <summary>A primitive's row written from its bytes as the runtime holds them, as a local's is and a value a call returned
/// is with no code run in the program: by its type's width, a native integer as wide as its bytes, little-endian.</summary>
public class PrimitiveTextTests
{
    [Fact]
    public void A_primitive_reads_from_its_bytes_as_a_local_of_its_type() => Cases.All(
        ("a char and whole numbers read as a local of their type", () =>
        {
            Assert.Equal("90 'Z'", ManagedDebugger.PrimitiveText(CorElementType.CHAR, BitConverter.GetBytes('Z')));
            Assert.Equal("-7", ManagedDebugger.PrimitiveText(CorElementType.I1, [unchecked((byte)-7)]));
            Assert.Equal("4000000000", ManagedDebugger.PrimitiveText(CorElementType.U4, BitConverter.GetBytes(4000000000u)));
        }),
        ("a real number is written as the debugger writes numbers", () =>
            Assert.Equal(0.75.ToString(CultureInfo.CurrentCulture), ManagedDebugger.PrimitiveText(CorElementType.R8, BitConverter.GetBytes(0.75)))),
        ("a native integer is as wide as the bytes the program holds it in", () =>
        {
            Assert.Equal("-42", ManagedDebugger.PrimitiveText(CorElementType.I, BitConverter.GetBytes(-42)));
            Assert.Equal("4000000000", ManagedDebugger.PrimitiveText(CorElementType.U, BitConverter.GetBytes(4000000000UL)));
        }),
        ("bytes short of the type's width read the rest as zero", () => Assert.Equal("0", ManagedDebugger.PrimitiveText(CorElementType.U8, []))));
}
