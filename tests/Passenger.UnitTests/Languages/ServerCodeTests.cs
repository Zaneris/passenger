using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Roslyn.Extension;

namespace Passenger.UnitTests.Languages;

/// <summary>A C# card's code as the server gives it in parts: its text the parts' together, each part of a type the editor
/// colours a span of that kind where the editor's semantic colours put it, the blank lines around the code left out, and
/// told the same as a Markdown code block that writes it with spaces ending its lines.</summary>
public sealed class ServerCodeTests
{
    [Fact]
    public void A_cards_code_is_its_parts_text_each_coloured_as_the_editor_colours_its_kind() => Cases.All(
        ("parts are the codes text each typed part a span of the editors kind for its token type", Parts_are_the_codes_text_each_typed_part_a_span_of_the_editors_kind_for_its_token_type),
        ("a part of a type the editor leaves uncoloured is no span", A_part_of_a_type_the_editor_leaves_uncoloured_is_no_span),
        ("code is the same as a markdown block writing it with spaces ending its lines and blank lines around it", Code_is_the_same_as_a_markdown_block_writing_it_with_spaces_ending_its_lines_and_blank_lines_around_it));

    private void Parts_are_the_codes_text_each_typed_part_a_span_of_the_editors_kind_for_its_token_type()
    {
        (string text, SyntaxSpan[] spans) = ServerCode.Of([new("\n", null), new("int", "keyword"), new(" ", null), new("Helper", "class"), new(".", null),
            new("Twice", "method"), new("(", null), new("int", "keyword"), new(" ", null), new("value", "parameter"), new(")", null), new("\n", null)]);
        Assert.Equal("int Helper.Twice(int value)", text);
        Assert.Equal([new SyntaxSpan(0, 3, TokenKind.Keyword), new SyntaxSpan(4, 6, TokenKind.Class), new SyntaxSpan(11, 5, TokenKind.Method),
            new SyntaxSpan(17, 3, TokenKind.Keyword), new SyntaxSpan(21, 5, TokenKind.Parameter)], spans);
    }

    private void A_part_of_a_type_the_editor_leaves_uncoloured_is_no_span()
    {
        (string text, SyntaxSpan[] spans) = ServerCode.Of([new("Missing", "variable-unknown"), new(" ", null), new("x", "local")]);
        Assert.Equal("Missing x", text);
        Assert.Equal([new SyntaxSpan(8, 1, TokenKind.Local)], spans);
    }

    private void Code_is_the_same_as_a_markdown_block_writing_it_with_spaces_ending_its_lines_and_blank_lines_around_it()
    {
        Assert.True(ServerCode.SameCode("void A()\nvoid B()", "\nvoid A()  \nvoid B()  \n"));
        Assert.True(ServerCode.SameCode("void A()\r\nvoid B()", "void A()\nvoid B()"));
        Assert.False(ServerCode.SameCode("void A()", "void  A()"), "Code differing inside a line was taken for the same code");
        Assert.False(ServerCode.SameCode("void A()\nvoid B()", "void A() void B()"), "Code broken into other lines was taken for the same code");
    }
}
