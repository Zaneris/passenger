using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Languages;

/// <summary>A ';' or a '}' just typed in C# is formatted around only where it is the code's own and ends something: a
/// statement's or a declaration's ';', a block's, a type's or an initializer's '}'; never a ';' of a for loop's header,
/// nor one or a '}' in a string, a comment or a directive, nor an interpolation's '}'.</summary>
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
}
