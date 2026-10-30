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

    [Theory]
    [InlineData(nameof(Holder<int>.Listed), "System.Collections.Generic.List<T>", "System.Collections.Generic.List<int>")]
    [InlineData(nameof(Holder<int>.Maybe), "System.Nullable<T>", "int?")]
    [InlineData(nameof(Holder<int>.Items), "T[]", "int[]")]
    [InlineData(nameof(Holder<int>.Grids), "T[][,]", "int[][,]")]
    [InlineData(nameof(Holder<int>.Keyed), "System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<T>>",
        "System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>")]
    [InlineData(nameof(Holder<int>.Nested), Here + ".Outer<T>.Inner<long>", Here + ".Outer<int>.Inner<long>")]
    [InlineData(nameof(Holder<int>.Plain), "System.Nullable<decimal>", "decimal?")]
    public void A_fields_type_is_named_for_a_cast_with_its_type_parameters_and_with_the_types_they_run_as(string field, string cast, string runAs)
    {
        Assert.Equal(cast, FieldType(field, "T", CSharpTypeNames.NullableSpelled));
        Assert.Equal(runAs, FieldType(field, "int", CSharpTypeNames.Instance));
    }

    [Fact]
    public void A_type_parameter_given_no_name_leaves_its_type_unnamed()
    {
        Assert.Null(FieldType(nameof(Holder<int>.Listed), null, CSharpTypeNames.Instance));
        Assert.Equal("decimal?", FieldType(nameof(Holder<int>.Plain), null, CSharpTypeNames.Instance));
    }

    [Theory]
    [InlineData("int", "int[]")]
    [InlineData("int[]", "int[][]")]
    [InlineData("int[,]", "int[][,]")]
    [InlineData("int?[]", "int?[][]")]
    [InlineData("System.Collections.Generic.List<int[]>", "System.Collections.Generic.List<int[]>[]")]
    public void An_array_of_an_array_takes_its_rank_before_its_elements(string element, string array) =>
        Assert.Equal(array, CSharpTypeNames.ArrayOf(element, "[]"));

    [Fact]
    public void A_frames_parameter_of_a_nested_generic_type_gives_each_type_its_own_arguments()
    {
        using ModuleMetadataReader reader = ModuleMetadataReader.TryLoad(typeof(Holder<>).Assembly.Location)
            ?? throw new InvalidOperationException("The unit tests' own assembly could not be read");
        MethodInfo take = typeof(Holder<>).GetMethod(nameof(Holder<int>.Take)) ?? throw new InvalidOperationException("Holder has no method Take");
        ImmutableArray<string> parameters = reader.PeMetadataReader.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.EntityHandle(take.MetadataToken))
            .DecodeSignature(FrameDisplaySignatureTypeProvider.Instance, new FrameDisplayGenericContext(["int"], [])).ParameterTypes;
        Assert.True(parameters.SequenceEqual([Here + ".Outer<int>.Inner<long>", "System.Nullable<int>", "System.Collections.Generic.List<int>"]),
            $"A frame showed Take's parameters as {string.Join(", ", parameters)}: a nested generic type's arguments went to the wrong type");
    }

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
