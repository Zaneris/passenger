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

    [Theory]
    [InlineData(nameof(Holder<int>.Plain), Here + ".Base", 0)]
    [InlineData(nameof(Holder<int>.Nested), Here + ".Outer.Inner", 0)]
    [InlineData(nameof(Holder<int>.Generic), Here + ".Gen`1", 0)]
    [InlineData(nameof(Holder<int>.Everything), "System.Object", 0)]
    [InlineData(nameof(Holder<int>.Interface), "System.IDisposable", 0)]
    [InlineData(nameof(Holder<int>.Elements), Here + ".Base", 1)]
    [InlineData(nameof(Holder<int>.Jagged), Here + ".Base", 2)]
    [InlineData(nameof(Holder<int>.Grid), Here + ".Base", 1)]
    public void A_fields_declared_type_is_its_definitions_name_under_its_arrays(string field, string name, int arrays)
    {
        DeclaredType? declared = FieldType(field);
        for (int i = 0; i < arrays; i++)
        {
            Assert.True(declared is { Name: null, Element: not null }, $"{field} is declared as {declared}, not an array of its elements' type");
            declared = declared.Element;
        }
        Assert.Equal(new DeclaredType(name), declared);
    }

    [Theory]
    [InlineData(nameof(Holder<int>.Parameter))]
    [InlineData(nameof(Holder<int>.Number))]
    [InlineData(nameof(Holder<int>.Numbers))]
    [InlineData(nameof(Holder<int>.Text))]
    public void A_field_of_a_type_no_value_derives_below_has_no_declared_type(string field)
    {
        Assert.Null(FieldType(field));
    }

    [Fact]
    public void A_methods_locals_are_declared_as_its_signature_says_at_the_built_version()
    {
        using ModuleMetadataReader reader = Reader();
        int token = typeof(Holder<int>).GetMethod(nameof(Holder<int>.Locals))!.MetadataToken;
        IReadOnlyList<DeclaredType?> locals = reader.LocalTypes(token, version: 1);
        Assert.Contains(new DeclaredType(Here + ".Base"), locals);
        Assert.Contains(DeclaredType.Object, locals);
        Assert.Contains(new DeclaredType(null, new DeclaredType(Here + ".Base")), locals);
    }

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
