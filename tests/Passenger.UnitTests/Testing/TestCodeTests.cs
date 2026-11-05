using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.App.Testing;
using Passenger.Languages.CSharp;

namespace Passenger.UnitTests.Testing;

/// <summary>A test's code as the number a result is compared by: it moves with what the test says, a literal's whitespace
/// and GDScript's blocks among that, and stays where only the layout changed, so a result reads as stale when its test
/// changed and at no other time.</summary>
public class TestCodeTests
{
    private static ulong CSharp(string body) => TestCode.Of(Assert.Single(CSharpSyntaxTree.ParseText(
        $"public class Sums\n{{\n    [Fact]\n    public void Adds()\n    {{\n{body}\n    }}\n}}\n", CSharpParsing.Default).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()));

    private static ulong Script(string function) => TestCode.OfScript(function);

    [Theory]
    [InlineData("Assert.Equal(\"a b\", Got());", "Assert.Equal(\"ab\", Got());")]
    [InlineData("Assert.Equal(@\"a b\", Got());", "Assert.Equal(@\"ab\", Got());")]
    [InlineData("Assert.Equal($\"a {x} b\", Got());", "Assert.Equal($\"a {x}b\", Got());")]
    [InlineData("Assert.Equal(\"\"\"a b\"\"\", Got());", "Assert.Equal(\"\"\"ab\"\"\", Got());")]
    [InlineData("Assert.Equal(' ', Got());", "Assert.Equal('\t', Got());")]
    public void A_literals_whitespace_is_what_a_CSharp_test_says(string was, string now)
    {
        Assert.True(CSharp(was) != CSharp(now), $"Changing {was} to {now} reads as the same test, so its old result stands as current");
    }

    [Fact]
    public void Two_CSharp_tokens_are_no_one_token_of_their_letters()
    {
        Assert.True(CSharp("return x;") != CSharp("returnx;"), "A statement and the name of its letters read as one test");
    }

    [Fact]
    public void A_CSharp_tests_layout_is_not_what_it_says()
    {
        ulong code = CSharp("        int sum = Add(1, 2);\n        Assert.Equal(3, sum);");
        Assert.Equal(code, CSharp("  int   sum=Add( 1,2 ) ;\n\n\n\t\tAssert.Equal(3,\n            sum);   "));
        Assert.Equal(code, CSharp("        int sum = Add(1, 2);\r\n        Assert.Equal(3, sum);"));
    }

    [Fact]
    public void A_CSharp_tests_comment_counts_but_not_how_it_is_spaced()
    {
        ulong code = CSharp("        // adds one\n        Assert.True(true);");
        Assert.True(code != CSharp("        // adds two\n        Assert.True(true);"), "A changed comment reads as the same test");
        Assert.Equal(code, CSharp("        //   adds   one\n        Assert.True(true);"));
    }

    [Theory]
    [InlineData("\tassert_eq(got, \"a b\")", "\tassert_eq(got, \"ab\")")]
    [InlineData("\tassert_eq(got, 'a b')", "\tassert_eq(got, 'ab')")]
    [InlineData("\tassert_eq(got, \"\"\"a\n\tb\"\"\")", "\tassert_eq(got, \"\"\"a\n\t\tb\"\"\")")]
    [InlineData("\tassert_eq(got, &\"a b\")", "\tassert_eq(got, &\"ab\")")]
    public void A_literals_whitespace_is_what_a_GDScript_test_says(string was, string now)
    {
        Assert.True(Script($"func test_adds():\n{was}\n") != Script($"func test_adds():\n{now}\n"), $"Changing {was} to {now} reads as the same test, so its old result stands as current");
    }

    [Fact]
    public void A_line_moved_out_of_its_block_is_what_a_GDScript_test_says()
    {
        Assert.True(Script("func test_adds():\n\tif ready:\n\t\tprepare()\n\t\tassert_true(done)\n") != Script("func test_adds():\n\tif ready:\n\t\tprepare()\n\tassert_true(done)\n"),
            "A line moved out of its if reads as the same test");
    }

    [Fact]
    public void A_line_moved_out_of_a_block_of_a_lambda_in_brackets_is_what_a_GDScript_test_says()
    {
        Assert.True(Script("func test_adds():\n\twatch(func():\n\t\tif ready:\n\t\t\tprepare()\n\t\t\tfinish()\n\t)\n") != Script("func test_adds():\n\twatch(func():\n\t\tif ready:\n\t\t\tprepare()\n\t\tfinish()\n\t)\n"),
            "A line moved out of an if inside a lambda reads as the same test");
    }

    [Fact]
    public void Two_GDScript_tokens_are_no_one_token_of_their_letters()
    {
        Assert.True(Script("func test_adds():\n\treturn x\n") != Script("func test_adds():\n\treturnx\n"), "A statement and the name of its letters read as one test");
    }

    [Fact]
    public void A_GDScript_tests_layout_is_not_what_it_says()
    {
        ulong code = Script("func test_adds():\n\tvar sum := add(1, 2)\n\tif sum > 0:\n\t\tassert_eq(sum, 3)\n");
        Assert.Equal(code, Script("func test_adds():\n\n\tvar sum:=add( 1,2 )   \n\n\tif sum>0:\n\t\tassert_eq(sum,\n\t\t\t3)\n"));
        Assert.Equal(code, Script("func test_adds():\n    var sum := add(1, 2)\n    if sum > 0:\n        assert_eq(sum, 3)\n"));
        Assert.Equal(code, Script("func test_adds():\r\n\tvar sum := add(1, 2)\r\n\tif sum > 0:\r\n\t\tassert_eq(sum, 3)\r\n"));
    }

    [Fact]
    public void A_GDScript_tests_comment_counts_but_not_how_it_is_spaced()
    {
        ulong code = Script("func test_adds():\n\t# adds one\n\tassert_true(true)\n");
        Assert.True(code != Script("func test_adds():\n\t# adds two\n\tassert_true(true)\n"), "A changed comment reads as the same test");
        Assert.Equal(code, Script("func test_adds():\n\t#   adds   one\n\tassert_true(true)\n"));
    }

    [Fact]
    public void A_test_of_an_inner_class_reads_as_one_at_the_top_does()
    {
        Assert.True(Script("\tfunc test_adds():\n\t\tassert_eq(got, \"a b\")\n") != Script("\tfunc test_adds():\n\t\tassert_eq(got, \"ab\")\n"),
            "An inner class's test reads as the same test with its literal changed");
        Assert.True(Script("\tfunc test_adds():\n\t\tif ready:\n\t\t\tprepare()\n\t\t\tfinish()\n") != Script("\tfunc test_adds():\n\t\tif ready:\n\t\t\tprepare()\n\t\tfinish()\n"),
            "A line moved out of its if in an inner class's test reads as the same test");
    }

    [Fact]
    public void An_unfinished_GDScript_test_still_reads_to_its_end()
    {
        Assert.True(Script("func test_adds():\n\tassert_eq(got, \"a b\n") != Script("func test_adds():\n\tassert_eq(got, \"ab\n"), "A string left open reads as the same test with its words changed");
        Assert.True(Script("func test_adds():\n\tassert_eq((got, 1\n") != Script("func test_adds():\n\tassert_eq((got, 2\n"), "A bracket left open reads as the same test with what follows changed");
    }
}
