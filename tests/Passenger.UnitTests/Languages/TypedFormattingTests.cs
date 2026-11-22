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

    [Fact]
    public void A_character_or_a_block_is_laid_out_only_where_the_code_ends_or_opens_it() => Cases.All(
        ("a character is formatted around only where the code ends something with it", () => Cases.Each(
        [
            ("field=1;", true),
            ("1,2 }", true),
            ("var x=i+1;\n        }", true),
            ("#endif\n    }", true),
            ("for (var i=0;", false),
            ("$\"{field}", false),
            ("\"a;b}", false),
            ("// x=1;", false),
            ("/* x=2; }", false),
            ("var gone=3;", false),
        ], row =>
        {
            int caret = Code.IndexOf(row.Item1, StringComparison.Ordinal) + row.Item1.Length;
            ParsedText parsed = new(CSharpParsing.Parse(Code), TextSnapshot.From(Code));
            char typed = Code[caret - 1];
            Assert.True(TypedFormatting.Formats(parsed, caret, typed) == row.Item2,
                $"The '{typed}' ending \"{row.Item1}\" would {(row.Item2 ? "not " : "")}be formatted around");
        })),
        ("only the character said typed and found before the caret is formatted around", () =>
        {
            ParsedText parsed = new(CSharpParsing.Parse(Code), TextSnapshot.From(Code));
            int statement = Code.IndexOf("x=i+1;", StringComparison.Ordinal) + "x=i+1;".Length;
            Assert.False(TypedFormatting.Formats(parsed, statement, '}'), "A ';' was formatted around as the '}' said typed");
            Assert.False(TypedFormatting.Formats(parsed, statement - 1, ';'), "The '1' before a ';' was formatted around as the ';' said typed");
            Assert.False(TypedFormatting.Formats(parsed, 0, ';'), "Nothing before the file's start was formatted around");
            Assert.False(TypedFormatting.Formats(parsed, Code.Length + 1, '}'), "A place past the file's end was formatted around");
            Assert.False(TypedFormatting.Formats(parsed, statement, '='), "A character other than ';' or '}' was formatted around");
        }),
        ("a block is laid out only where the caret stands alone between the code's own braces", () => Cases.Each(
        [
            ("var item = new() {\n    |\n};", true),
            ("if (true)\n    {\n|\n    }", true),
            ("var items = new[] {\r\n    |\r\n};", true),
            ("var item = new() {|};", false),
            ("var item = new() {\n    |};", false),
            ("var item = new() {\n    // kept\n    |\n};", false),
            ("var text = $\"{\n    |\n}\";", false),
            ("var list = new int[] [\n    |\n];", false),
        ], row =>
        {
            string body = row.Item1;
            string code = "static class Opened\n{\n    static void Run()\n    {\n        " + body.Replace("|", "", StringComparison.Ordinal) + "\n    }\n}\n";
            int caret = code.IndexOf(body[..body.IndexOf('|', StringComparison.Ordinal)], StringComparison.Ordinal) + body.IndexOf('|', StringComparison.Ordinal);
            ParsedText parsed = new(CSharpParsing.Parse(code), TextSnapshot.From(code));
            Assert.True(TypedFormatting.OpensBlock(parsed, caret) == row.Item2, $"The block around the caret in \"{body}\" would {(row.Item2 ? "not " : "")}be laid out");
        })),
        ("no block is laid out at the file's ends", () =>
        {
            const string Braces = "class A\n{\n}";
            ParsedText parsed = new(CSharpParsing.Parse(Braces), TextSnapshot.From(Braces));
            Assert.False(TypedFormatting.OpensBlock(parsed, 0), "A block was laid out at the file's start");
            Assert.False(TypedFormatting.OpensBlock(parsed, Braces.Length), "A block was laid out at the file's end");
        }));
}
