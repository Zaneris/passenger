using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Roslyn.Extension;

namespace Passenger.UnitTests.Languages;

/// <summary>A C# card's code as the server gives it in parts: its text the parts' together, each part of a type the
/// editor colours a span of that kind where the editor's semantic colours put it.</summary>
public sealed class ServerCodeTests
{
    [Fact]
    public void A_cards_code_is_its_parts_text_each_coloured_as_the_editor_colours_its_kind() =>
        Parts_are_the_codes_text_each_typed_part_a_span_of_the_editors_kind_for_its_token_type();

    private void Parts_are_the_codes_text_each_typed_part_a_span_of_the_editors_kind_for_its_token_type()
    {
        (string text, SyntaxSpan[] spans) = ServerCode.Of([new("\n", null), new("int", "keyword"), new(" ", null), new("Helper", "class"), new(".", null),
            new("Twice", "method"), new("(", null), new("int", "keyword"), new(" ", null), new("value", "parameter"), new(")", null), new("\n", null)]);
        Assert.Equal("int Helper.Twice(int value)", text);
        Assert.Equal([new SyntaxSpan(0, 3, TokenKind.Keyword), new SyntaxSpan(4, 6, TokenKind.Class), new SyntaxSpan(11, 5, TokenKind.Method),
            new SyntaxSpan(17, 3, TokenKind.Keyword), new SyntaxSpan(21, 5, TokenKind.Parameter)], spans);
    }
}
