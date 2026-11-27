using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>The types variables are declared as, read from this assembly's own metadata as the debugger reads a program's: a
/// class by its definition's name whatever its type arguments, an array by its elements', object as itself, and none for a
/// type a value of it could not derive below (a type parameter, a primitive), so a member row's expression casts only where
/// the declared type does not reach the member.</summary>
public class DeclaredTypesTests
{
    private const string Here = "Passenger.UnitTests.Debugger.DeclaredTypesTests";

    [Fact]
    public void A_declared_type_is_its_definitions_name_under_its_arrays_and_none_where_no_value_derives_below() => Cases.All(
        ("a field's declared type is its definition's name under its arrays", () => Cases.Each(
        [
            (nameof(Holder<int>.Generic), Here + ".Gen`1", 0),
            (nameof(Holder<int>.Interface), "System.IDisposable", 0),
            (nameof(Holder<int>.Jagged), Here + ".Base", 2),
            (nameof(Holder<int>.Grid), Here + ".Base", 1),
        ], row =>
        {
            (string field, string name, int arrays) = row;
            DeclaredType? declared = FieldType(field);
            for (int i = 0; i < arrays; i++)
            {
                Assert.True(declared is { Name: null, Element: not null }, $"{field} is declared as {declared}, not an array of its elements' type");
                declared = declared.Element;
            }
            Assert.Equal(new DeclaredType(name), declared);
        })),
        ("a field of a type no value derives below has no declared type", () => Cases.Each(
            [nameof(Holder<int>.Parameter), nameof(Holder<int>.Numbers), nameof(Holder<int>.Text)], field => Assert.Null(FieldType(field)))),
        ("a method's locals are declared as its signature says at the built version", () =>
        {
            using ModuleMetadataReader reader = Reader();
            int token = typeof(Holder<int>).GetMethod(nameof(Holder<int>.Locals))!.MetadataToken;
            IReadOnlyList<DeclaredType?> locals = reader.LocalTypes(token, version: 1);
            Assert.Contains(new DeclaredType(Here + ".Base"), locals);
            Assert.Contains(DeclaredType.Object, locals);
            Assert.Contains(new DeclaredType(null, new DeclaredType(Here + ".Base")), locals);
        }),
        ("each local's type is the run of its signature's bytes for that slot, as the runtime reads a local at an address", () =>
        {
            using ModuleMetadataReader reader = Reader();
            int token = typeof(Holder<int>).GetMethod(nameof(Holder<int>.Locals))!.MetadataToken;
            ImmutableArray<DeclaredType?> locals = reader.LocalTypes(token, version: 1);
            ImmutableArray<byte[]> written = reader.LocalTypeSignatures(token, version: 1);
            byte[] whole = reader.PeMetadataReader.GetBlobBytes(reader.PeMetadataReader.GetStandaloneSignature(reader.LocalSignature(token, 1)!.Value).Signature);
            // The header and a count under 128 take a byte each.
            Assert.Equal(whole[2..], written.SelectMany(type => type));
            Assert.Equal(locals.Length, written.Length);
            Assert.Equal(0x12, written[locals.IndexOf(new DeclaredType(Here + ".Base"))][0]);
            Assert.Equal(0x1C, written[locals.IndexOf(DeclaredType.Object)][0]);
            Assert.Equal(0x1D, written[locals.IndexOf(new DeclaredType(null, new DeclaredType(Here + ".Base")))][0]);
        }));

    /// <summary>A field of <see cref="Holder{T}"/> as the debugger decodes it.</summary>
    private static DeclaredType? FieldType(string field)
    {
        using ModuleMetadataReader reader = Reader();
        FieldInfo info = typeof(Holder<>).GetField(field) ?? throw new InvalidOperationException($"Holder has no field {field}");
        return reader.PeMetadataReader.GetFieldDefinition((FieldDefinitionHandle)MetadataTokens.EntityHandle(info.MetadataToken))
            .DecodeSignature(DeclaredTypeProvider.Instance, null);
    }

    private static ModuleMetadataReader Reader() => ModuleMetadataReader.TryLoad(typeof(Holder<>).Assembly.Location)
        ?? throw new InvalidOperationException("The unit tests' own assembly could not be read");

    public class Base;

    public sealed class Derived : Base;

    public static class Outer
    {
        public sealed class Inner;
    }

    public sealed class Gen<T>;

    public sealed class Holder<T>
    {
        public Base? Plain;
        public Outer.Inner? Nested;
        public Gen<int>? Generic;
        public object? Everything;
        public IDisposable? Interface;
        public Base[]? Elements;
        public Base[][]? Jagged;
        public Base[,]? Grid;
        public T? Parameter;
        public int Number;
        public int[]? Numbers;
        public string? Text;

        public static string Locals()
        {
            Base one = new Derived();
            object two = one;
            Base[] three = [one];
            // Each read twice, so an optimized build keeps every one in its slot.
            return $"{one}{one.GetHashCode()}{two}{two.GetHashCode()}{three.Length}{three[0]}";
        }
    }
}
