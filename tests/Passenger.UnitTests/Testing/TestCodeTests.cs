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

    private static void CSharpLiteral(string was, string now)
    {
        Assert.True(CSharp(was) != CSharp(now), $"Changing {was} to {now} reads as the same test, so its old result stands as current");
    }

    [Fact]
    public void A_tests_code_changes_only_where_what_it_says_changes_in_CSharp_and_GDScript() => Cases.All(
        ("two C# tokens are no one token of their letters", () =>
        {
            Assert.True(CSharp("return x;") != CSharp("returnx;"), "A statement and the name of its letters read as one test");
        }),
        ("a C# test's layout is not what it says", () =>
        {
            ulong code = CSharp("        int sum = Add(1, 2);\n        Assert.Equal(3, sum);");
            Assert.Equal(code, CSharp("  int   sum=Add( 1,2 ) ;\n\n\n\t\tAssert.Equal(3,\n            sum);   "));
            Assert.Equal(code, CSharp("        int sum = Add(1, 2);\r\n        Assert.Equal(3, sum);"));
        }),
        ("a C# test's comment counts but not how it is spaced", () =>
        {
            ulong code = CSharp("        // adds one\n        Assert.True(true);");
            Assert.True(code != CSharp("        // adds two\n        Assert.True(true);"), "A changed comment reads as the same test");
            Assert.Equal(code, CSharp("        //   adds   one\n        Assert.True(true);"));
        }),
        ("a line moved out of its block is what a GDScript test says", () =>
        {
            Assert.True(Script("func test_adds():\n\tif ready:\n\t\tprepare()\n\t\tassert_true(done)\n") != Script("func test_adds():\n\tif ready:\n\t\tprepare()\n\tassert_true(done)\n"),
                "A line moved out of its if reads as the same test");
        }),
        ("two GDScript tokens are no one token of their letters", () =>
        {
            Assert.True(Script("func test_adds():\n\treturn x\n") != Script("func test_adds():\n\treturnx\n"), "A statement and the name of its letters read as one test");
        }),
        ("a GDScript test's layout is not what it says", () =>
        {
            ulong code = Script("func test_adds():\n\tvar sum := add(1, 2)\n\tif sum > 0:\n\t\tassert_eq(sum, 3)\n");
            Assert.Equal(code, Script("func test_adds():\n\n\tvar sum:=add( 1,2 )   \n\n\tif sum>0:\n\t\tassert_eq(sum,\n\t\t\t3)\n"));
            Assert.Equal(code, Script("func test_adds():\n    var sum := add(1, 2)\n    if sum > 0:\n        assert_eq(sum, 3)\n"));
            Assert.Equal(code, Script("func test_adds():\r\n\tvar sum := add(1, 2)\r\n\tif sum > 0:\r\n\t\tassert_eq(sum, 3)\r\n"));
        }),
        ("a GDScript test's comment counts but not how it is spaced", () =>
        {
            ulong code = Script("func test_adds():\n\t# adds one\n\tassert_true(true)\n");
            Assert.True(code != Script("func test_adds():\n\t# adds two\n\tassert_true(true)\n"), "A changed comment reads as the same test");
            Assert.Equal(code, Script("func test_adds():\n\t#   adds   one\n\tassert_true(true)\n"));
        }),
        ("a test of an inner class reads as one at the top does", () =>
        {
            Assert.True(Script("\tfunc test_adds():\n\t\tassert_eq(got, \"a b\")\n") != Script("\tfunc test_adds():\n\t\tassert_eq(got, \"ab\")\n"),
                "An inner class's test reads as the same test with its literal changed");
            Assert.True(Script("\tfunc test_adds():\n\t\tif ready:\n\t\t\tprepare()\n\t\t\tfinish()\n") != Script("\tfunc test_adds():\n\t\tif ready:\n\t\t\tprepare()\n\t\tfinish()\n"),
                "A line moved out of its if in an inner class's test reads as the same test");
        }),
        ("an unfinished GDScript test still reads to its end", () =>
        {
            Assert.True(Script("func test_adds():\n\tassert_eq(got, \"a b\n") != Script("func test_adds():\n\tassert_eq(got, \"ab\n"), "A string left open reads as the same test with its words changed");
            Assert.True(Script("func test_adds():\n\tassert_eq((got, 1\n") != Script("func test_adds():\n\tassert_eq((got, 2\n"), "A bracket left open reads as the same test with what follows changed");
        }),
        ("a literal's white space is what a C# or GDScript test says", () =>
        {
            Cases.Each([("Assert.Equal(\"a b\", Got());", "Assert.Equal(\"ab\", Got());"), ("Assert.Equal($\"a {x} b\", Got());", "Assert.Equal($\"a {x}b\", Got());"),
                ("Assert.Equal(\"\"\"a b\"\"\", Got());", "Assert.Equal(\"\"\"ab\"\"\", Got());")], row => CSharpLiteral(row.Item1, row.Item2));
            Cases.Each([("\tassert_eq(got, 'a b')", "\tassert_eq(got, 'ab')"), ("\tassert_eq(got, \"\"\"a\n\tb\"\"\")", "\tassert_eq(got, \"\"\"a\n\t\tb\"\"\")")],
                row => ScriptLiteral(row.Item1, row.Item2));
        }));

    private static void ScriptLiteral(string was, string now)
    {
        Assert.True(Script($"func test_adds():\n{was}\n") != Script($"func test_adds():\n{now}\n"), $"Changing {was} to {now} reads as the same test, so its old result stands as current");
    }

}
