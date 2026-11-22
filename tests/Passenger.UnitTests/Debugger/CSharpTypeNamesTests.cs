using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>Types as C# names them in an expression, read from this assembly's own metadata as Set Value reads a target's:
/// with a type parameter's own name for the cast a value takes (a nullable spelled out), and with the type a frame runs it
/// as for the value's conversion.</summary>
public class CSharpTypeNamesTests
{
    private const string Here = "Passenger.UnitTests.Debugger.CSharpTypeNamesTests";

    [Fact]
    public void A_type_is_named_for_a_cast_with_its_type_parameters_and_with_the_types_a_frame_runs_it_as() => Cases.All(
        ("a field's type is named for a cast with its type parameters and with the types they run as", () => Cases.Each(
        [
            (nameof(Holder<int>.Maybe), "System.Nullable<T>", "int?"),
            (nameof(Holder<int>.Grids), "T[][,]", "int[][,]"),
            (nameof(Holder<int>.Keyed), "System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<T>>",
                "System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>"),
            (nameof(Holder<int>.Nested), Here + ".Outer<T>.Inner<long>", Here + ".Outer<int>.Inner<long>"),
        ], row =>
        {
            Assert.Equal(row.Item2, FieldType(row.Item1, "T", CSharpTypeNames.NullableSpelled));
            Assert.Equal(row.Item3, FieldType(row.Item1, "int", CSharpTypeNames.Instance));
        })),
        ("a type parameter given no name leaves its type unnamed", () => Assert.Null(FieldType(nameof(Holder<int>.Listed), null, CSharpTypeNames.Instance))),
        ("an array of an array takes its rank before its elements", () =>
        {
            Assert.Equal("int[][,]", CSharpTypeNames.ArrayOf("int[,]", "[]"));
            Assert.Equal("int?[][]", CSharpTypeNames.ArrayOf("int?[]", "[]"));
        }),
        ("a frame's parameter of a nested generic type gives each type its own arguments", () =>
        {
            using ModuleMetadataReader reader = ModuleMetadataReader.TryLoad(typeof(Holder<>).Assembly.Location)
                ?? throw new InvalidOperationException("The unit tests' own assembly could not be read");
            MethodInfo take = typeof(Holder<>).GetMethod(nameof(Holder<int>.Take)) ?? throw new InvalidOperationException("Holder has no method Take");
            ImmutableArray<string> parameters = reader.PeMetadataReader.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.EntityHandle(take.MetadataToken))
                .DecodeSignature(FrameDisplaySignatureTypeProvider.Instance, new FrameDisplayGenericContext(["int"], [])).ParameterTypes;
            Assert.True(parameters.SequenceEqual([Here + ".Outer<int>.Inner<long>", "System.Nullable<int>", "System.Collections.Generic.List<int>"]),
                $"A frame showed Take's parameters as {string.Join(", ", parameters)}: a nested generic type's arguments went to the wrong type");
        }));

    /// <summary>A field of <see cref="Holder{T}"/> named by <paramref name="names"/>, with T as <paramref name="parameter"/>.</summary>
    private static string? FieldType(string field, string? parameter, CSharpTypeNames names)
    {
        using ModuleMetadataReader reader = ModuleMetadataReader.TryLoad(typeof(Holder<>).Assembly.Location)
            ?? throw new InvalidOperationException("The unit tests' own assembly could not be read");
        FieldInfo info = typeof(Holder<>).GetField(field) ?? throw new InvalidOperationException($"Holder has no field {field}");
        return reader.PeMetadataReader.GetFieldDefinition((FieldDefinitionHandle)MetadataTokens.EntityHandle(info.MetadataToken))
            .DecodeSignature(names, new TypeParameterNames([parameter], []));
    }

    public static class Outer<TOuter>
    {
        public sealed class Inner<TInner>;
    }

    public sealed class Holder<T> where T : struct
    {
        public List<T>? Listed;
        public T? Maybe;
        public T[]? Items;
        public T[][,]? Grids;
        public Dictionary<string, List<T>>? Keyed;
        public Outer<T>.Inner<long>? Nested;
        public decimal? Plain;

        public static void Take(Outer<T>.Inner<long> nested, T? maybe, List<T> listed)
        {
        }
    }
}
