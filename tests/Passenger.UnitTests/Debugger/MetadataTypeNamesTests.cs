using System.Reflection.Metadata;
using Passenger.Core;
using Passenger.Debugger;
using Passenger.Debugger.Interop;

namespace Passenger.UnitTests.Debugger;

/// <summary>The primitives as the debugger reads them from metadata and from the runtime: a signature's code by its name in
/// System, its keyword and the element type the runtime gives a value of it, and each element type back to its primitive.</summary>
public class MetadataTypeNamesTests
{
    [Theory]
    [InlineData(PrimitiveTypeCode.Boolean, CorElementType.BOOLEAN, "System.Boolean", "bool")]
    [InlineData(PrimitiveTypeCode.Char, CorElementType.CHAR, "System.Char", "char")]
    [InlineData(PrimitiveTypeCode.SByte, CorElementType.I1, "System.SByte", "sbyte")]
    [InlineData(PrimitiveTypeCode.Byte, CorElementType.U1, "System.Byte", "byte")]
    [InlineData(PrimitiveTypeCode.Int16, CorElementType.I2, "System.Int16", "short")]
    [InlineData(PrimitiveTypeCode.UInt16, CorElementType.U2, "System.UInt16", "ushort")]
    [InlineData(PrimitiveTypeCode.Int32, CorElementType.I4, "System.Int32", "int")]
    [InlineData(PrimitiveTypeCode.UInt32, CorElementType.U4, "System.UInt32", "uint")]
    [InlineData(PrimitiveTypeCode.Int64, CorElementType.I8, "System.Int64", "long")]
    [InlineData(PrimitiveTypeCode.UInt64, CorElementType.U8, "System.UInt64", "ulong")]
    [InlineData(PrimitiveTypeCode.Single, CorElementType.R4, "System.Single", "float")]
    [InlineData(PrimitiveTypeCode.Double, CorElementType.R8, "System.Double", "double")]
    [InlineData(PrimitiveTypeCode.IntPtr, CorElementType.I, "System.IntPtr", "nint")]
    [InlineData(PrimitiveTypeCode.UIntPtr, CorElementType.U, "System.UIntPtr", "nuint")]
    [InlineData(PrimitiveTypeCode.String, CorElementType.STRING, "System.String", "string")]
    [InlineData(PrimitiveTypeCode.Object, CorElementType.OBJECT, "System.Object", "object")]
    [InlineData(PrimitiveTypeCode.Void, CorElementType.VOID, "System.Void", "void")]
    [InlineData(PrimitiveTypeCode.TypedReference, CorElementType.TYPEDBYREF, "System.TypedReference", null)]
    public void A_primitive_is_named_and_runs_as_its_element_type(PrimitiveTypeCode code, CorElementType element, string systemName, string? keyword)
    {
        Assert.True(MetadataTypeNames.ElementTypeOf(code) == element,
            $"{code} was taken for the runtime's {MetadataTypeNames.ElementTypeOf(code)}: a value of it would be read, boxed or made as another type");
        Assert.True(MetadataTypeNames.PrimitiveOf(element) is { } primitive && primitive.Code == code,
            $"The runtime's {element} was read as {MetadataTypeNames.PrimitiveOf(element)?.Code.ToString() ?? "no primitive"}, not {code}");
        Assert.Equal((systemName, keyword), (MetadataTypeNames.Of(code), PrimitiveTypes.Of(code).Keyword));
    }

    [Fact]
    public void Every_code_a_signature_can_give_names_a_primitive() =>
        Assert.All(Enum.GetValues<PrimitiveTypeCode>(), code => Assert.Equal(code, PrimitiveTypes.Of(code).Code));

    [Theory]
    [InlineData(CorElementType.CLASS)]
    [InlineData(CorElementType.VALUETYPE)]
    [InlineData(CorElementType.SZARRAY)]
    [InlineData(CorElementType.PTR)]
    [InlineData(CorElementType.END)]
    public void An_element_type_of_a_class_an_array_or_a_pointer_is_no_primitive(CorElementType element) =>
        Assert.Null(MetadataTypeNames.PrimitiveOf(element));

    [Fact]
    public void Decimal_is_named_by_its_keyword_though_no_signature_names_it_as_a_primitive()
    {
        Assert.True(PrimitiveTypes.OfKeyword("decimal") is { Code: null, SystemName: "System.Decimal" },
            "decimal was not taken for System.Decimal: a function breakpoint or a cast naming decimal would name no type");
        Assert.Equal("decimal", PrimitiveTypes.Named("System.Decimal")?.Keyword);
    }
}
