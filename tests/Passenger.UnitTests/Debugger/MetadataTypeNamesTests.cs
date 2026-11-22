using System.Reflection.Metadata;
using Passenger.Core;
using Passenger.Debugger;
using Passenger.Debugger.Interop;

namespace Passenger.UnitTests.Debugger;

/// <summary>The primitives as the debugger reads them from metadata and from the runtime: a signature's code by its name in
/// System, its keyword and the element type the runtime gives a value of it, and each element type back to its primitive.</summary>
public class MetadataTypeNamesTests
{
    [Fact]
    public void A_primitive_is_named_and_runs_as_its_element_type_and_back() => Cases.All(
        ("every code a signature can give names a primitive and runs as an element type read back as it", () => Cases.Each(Enum.GetValues<PrimitiveTypeCode>(), code =>
        {
            Assert.Equal(code, PrimitiveTypes.Of(code).Code);
            CorElementType element = MetadataTypeNames.ElementTypeOf(code);
            Assert.True(MetadataTypeNames.PrimitiveOf(element) is { } primitive && primitive.Code == code,
                $"{code} runs as the runtime's {element}, read back as {MetadataTypeNames.PrimitiveOf(element)?.Code.ToString() ?? "no primitive"}: a value of it would be read, boxed or made as another type");
        })),
        ("a primitive is named by its name in System and its keyword and runs as its element type", () => Cases.Each(
        [
            (PrimitiveTypeCode.Char, CorElementType.CHAR, "System.Char", "char"),
            (PrimitiveTypeCode.IntPtr, CorElementType.I, "System.IntPtr", "nint"),
            (PrimitiveTypeCode.Object, CorElementType.OBJECT, "System.Object", "object"),
            (PrimitiveTypeCode.TypedReference, CorElementType.TYPEDBYREF, "System.TypedReference", (string?)null),
        ], row =>
        {
            (PrimitiveTypeCode code, CorElementType element, string systemName, string? keyword) = row;
            Assert.Equal(element, MetadataTypeNames.ElementTypeOf(code));
            Assert.Equal((systemName, keyword), (MetadataTypeNames.Of(code), PrimitiveTypes.Of(code).Keyword));
        })),
        ("an element type of a class, an array or a pointer is no primitive", () => Cases.Each(
            [CorElementType.VALUETYPE, CorElementType.SZARRAY, CorElementType.PTR], element => Assert.Null(MetadataTypeNames.PrimitiveOf(element)))),
        ("decimal is named by its keyword though no signature names it as a primitive", () =>
        {
            Assert.True(PrimitiveTypes.OfKeyword("decimal") is { Code: null, SystemName: "System.Decimal" },
                "decimal was not taken for System.Decimal: a function breakpoint or a cast naming decimal would name no type");
            Assert.Equal("decimal", PrimitiveTypes.Named("System.Decimal")?.Keyword);
        }));
}
