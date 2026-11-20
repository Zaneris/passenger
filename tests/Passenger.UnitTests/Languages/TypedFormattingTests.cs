using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Languages;

/// <summary>A ';' or a '}' just typed in C# is formatted around only where it is the code's own and ends something: a
/// statement's or a declaration's ';', a block's, a type's or an initializer's '}'; never a ';' of a for loop's header,
/// nor one or a '}' in a string, a comment or a directive, nor an interpolation's '}'. A block a line break opened is laid
/// out only where the caret stands on a line of its own between the code's own '{' and its '}', with nothing else between.</summary>
public sealed class TypedFormattingTests
{
    private const string Code = """
        using System;

        static class Loop
        {
            static int field=1;

            static void Run()
            {
                for (var i=0;i<10;i+=1)
                {
                    var x=i+1;
                }
                var named=$"{field}";
                var text="a;b}";
                // x=1; y}
                /* x=2; } */
                var list=new int[] { 1,2 };
        #if NEVER
                var gone=3;
        #endif
            }
        }
        """;

    [Theory]
    [InlineData("field=1;", true)]
    [InlineData("x=i+1;", true)]
    [InlineData("var list=new int[] { 1,2 };", true)]
    [InlineData("1,2 }", true)]
    [InlineData("var x=i+1;\n        }", true)]
    [InlineData("#endif\n    }", true)]
    [InlineData("for (var i=0;", false)]
    [InlineData("for (var i=0;i<10;", false)]
    [InlineData("$\"{field}", false)]
    [InlineData("\"a;", false)]
    [InlineData("\"a;b}", false)]
    [InlineData("// x=1;", false)]
    [InlineData("// x=1; y}", false)]
    [InlineData("/* x=2;", false)]
    [InlineData("/* x=2; }", false)]
    [InlineData("var gone=3;", false)]
    public void A_character_is_formatted_around_only_where_the_code_ends_something_with_it(string upTo, bool formats)
    {
        int caret = Code.IndexOf(upTo, StringComparison.Ordinal) + upTo.Length;
        ParsedText parsed = new(CSharpParsing.Parse(Code), TextSnapshot.From(Code));
        char typed = Code[caret - 1];
        Assert.True(TypedFormatting.Formats(parsed, caret, typed) == formats,
            $"The '{typed}' ending \"{upTo}\" would {(formats ? "not " : "")}be formatted around");
    }

    [Fact]
    public void Only_the_character_said_typed_and_found_before_the_caret_is_formatted_around()
    {
        ParsedText parsed = new(CSharpParsing.Parse(Code), TextSnapshot.From(Code));
        int statement = Code.IndexOf("x=i+1;", StringComparison.Ordinal) + "x=i+1;".Length;
        Assert.False(TypedFormatting.Formats(parsed, statement, '}'), "A ';' was formatted around as the '}' said typed");
        Assert.False(TypedFormatting.Formats(parsed, statement - 1, ';'), "The '1' before a ';' was formatted around as the ';' said typed");
        Assert.False(TypedFormatting.Formats(parsed, 0, ';'), "Nothing before the file's start was formatted around");
        Assert.False(TypedFormatting.Formats(parsed, Code.Length + 1, '}'), "A place past the file's end was formatted around");
        Assert.False(TypedFormatting.Formats(parsed, statement, '='), "A character other than ';' or '}' was formatted around");
    }

    [Theory]
    [InlineData("var item = new() {\n    |\n};", true)]
    [InlineData("if (true) {\n        |\n    }", true)]
    [InlineData("if (true)\n    {\n|\n    }", true)]
    [InlineData("var items = new[] {\r\n    |\r\n};", true)]
    [InlineData("var item = new() {|};", false)]
    [InlineData("var item = new() {\n    |};", false)]
    [InlineData("var item = new() {|\n};", false)]
    [InlineData("var item = new() {\n    // kept\n    |\n};", false)]
    [InlineData("var item = new() {\n    X = 1,\n    |\n};", false)]
    [InlineData("var text = $\"{\n    |\n}\";", false)]
    [InlineData("var text = \"{\n    |\n}\";", false)]
    [InlineData("var list = new int[] [\n    |\n];", false)]
    public void A_block_is_laid_out_only_where_the_caret_stands_alone_between_the_codes_own_braces(string body, bool opens)
    {
        string code = "static class Opened\n{\n    static void Run()\n    {\n        " + body.Replace("|", "", StringComparison.Ordinal) + "\n    }\n}\n";
        int caret = code.IndexOf(body[..body.IndexOf('|', StringComparison.Ordinal)], StringComparison.Ordinal) + body.IndexOf('|', StringComparison.Ordinal);
        ParsedText parsed = new(CSharpParsing.Parse(code), TextSnapshot.From(code));
        Assert.True(TypedFormatting.OpensBlock(parsed, caret) == opens, $"The block around the caret in \"{body}\" would {(opens ? "not " : "")}be laid out");
    }

    [Fact]
    public void No_block_is_laid_out_at_the_files_ends()
    {
        const string Braces = "class A\n{\n}";
        ParsedText parsed = new(CSharpParsing.Parse(Braces), TextSnapshot.From(Braces));
        Assert.False(TypedFormatting.OpensBlock(parsed, 0), "A block was laid out at the file's start");
        Assert.False(TypedFormatting.OpensBlock(parsed, Braces.Length), "A block was laid out at the file's end");
    }
}
