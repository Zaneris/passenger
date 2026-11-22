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

    [Fact]
    public void A_stub_and_its_source_come_to_the_same_words_and_overloads_to_different_ones() => Cases.All(
        ("a stub and its source come to the same words however each spells a type", () => Cases.Each(
        [
            ("int a", "System.Int32 b"),
            ("string a = null", "String a"),
            ("int? a", "System.Nullable<int> a"),
            ("List<int> a", "System.Collections.Generic.List<System.Int32> a"),
            ("global::Shop.Order a", "Order a"),
            ("Order? a", "Order a"),
            ("params int[] a", "int[] a"),
            ("(int A, string B) a", "(int, string) a"),
            ("ref int a", "in int a"),
        ], row => Assert.Equal(Of(row.Item1), Of(row.Item2)))),
        ("overloads come to different words", () => Cases.Each(
        [
            ("int a", "int? a"),
            ("int a", "ref int a"),
            ("int[] a", "int[,] a"),
            ("List<int> a", "List<string> a"),
            ("int a, string b", "string a, int b"),
            ("int* a", "int a"),
        ], row => Assert.NotEqual(Of(row.Item1), Of(row.Item2)))),
        ("a type deeper than a stack holds is named and ends nothing", () =>
        {
            string stars = new('*', 100_000);
            SmallStack.Run(() =>
            {
                SyntaxNode root = CSharpParsing.Parse($"class C {{ void M(int{stars} p) {{ }} }}").GetRoot();
                string named = ParameterTypes.Of(root.DescendantNodes().OfType<ParameterListSyntax>().Single());
                Assert.Equal("int" + stars, named);
            });
        }));
}
