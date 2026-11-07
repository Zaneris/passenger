using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Languages.CSharp;

namespace Passenger.UnitTests.Languages;

/// <summary>A declaration's parameters as words that tell its overloads apart: the same for a stub and its source however
/// each spells a type, different for overloads.</summary>
public sealed class ParameterTypesTests
{
    private static string Of(string parameters) =>
        ParameterTypes.Of(CSharpSyntaxTree.ParseText($"class C {{ void M({parameters}) {{ }} }}").GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single().ParameterList);

    [Theory]
    [InlineData("int a", "System.Int32 b")]
    [InlineData("int a", "Int32 b")]
    [InlineData("string a", "string? a")]
    [InlineData("string a = null", "String a")]
    [InlineData("int? a", "System.Nullable<int> a")]
    [InlineData("List<int> a", "System.Collections.Generic.List<System.Int32> a")]
    [InlineData("global::Shop.Order a", "Order a")]
    [InlineData("Order? a", "Order a")]
    [InlineData("params int[] a", "int[] a")]
    [InlineData("(int A, string B) a", "(int, string) a")]
    [InlineData("ref int a", "in int a")]
    [InlineData("object a, long b", "Object a, Int64 b")]
    public void A_stub_and_its_source_come_to_the_same_words_however_each_spells_a_type(string stub, string source)
    {
        Assert.Equal(Of(stub), Of(source));
    }

    [Theory]
    [InlineData("int a", "string a")]
    [InlineData("int a", "int? a")]
    [InlineData("int a", "ref int a")]
    [InlineData("int[] a", "int[,] a")]
    [InlineData("int[] a", "int a")]
    [InlineData("List<int> a", "List<string> a")]
    [InlineData("int a, string b", "string a, int b")]
    [InlineData("int* a", "int a")]
    public void Overloads_come_to_different_words(string one, string other)
    {
        Assert.NotEqual(Of(one), Of(other));
    }

    [Fact]
    public void A_type_deeper_than_a_stack_holds_is_named_and_ends_nothing()
    {
        string stars = new('*', 100_000);
        SmallStack.Run(() =>
        {
            SyntaxNode root = CSharpParsing.Parse($"class C {{ void M(int{stars} p) {{ }} }}").GetRoot();
            string named = ParameterTypes.Of(root.DescendantNodes().OfType<ParameterListSyntax>().Single());
            Assert.Equal("int" + stars, named);
        });
    }
}
