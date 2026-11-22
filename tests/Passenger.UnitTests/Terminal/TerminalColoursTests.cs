using Passenger.Editor;
using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>A cell's style as an editor draws it (a console's): the sixteen colours by the theme's tokens, the 256 and
/// 24-bit ones as colours, inverse and hidden text taking the defaults they are given, faint text drawn see-through; and
/// each look named by one kind of its own for the process.</summary>
public class TerminalColoursTests
{
    [Fact]
    public void A_style_is_drawn_in_the_themes_sixteen_colours_and_the_rest_of_the_256_and_24_bit_ones() => Cases.All(
        ("The sixteen colours are the themes tokens and the rest of the 256 and 24 bit ones their own", The_sixteen_colours_are_the_themes_tokens_and_the_rest_of_the_256_and_24_bit_ones_their_own),
        ("A style is drawn in its colours its defaults where it names none and inverse and hidden text from them", A_style_is_drawn_in_its_colours_its_defaults_where_it_names_none_and_inverse_and_hidden_text_from_them));

    private void The_sixteen_colours_are_the_themes_tokens_and_the_rest_of_the_256_and_24_bit_ones_their_own()
    {
        Assert.Equal(TokenKeys.Terminal_Ansi1, TerminalColours.Token(1));
        Assert.Equal(TokenKeys.Terminal_Ansi15, TerminalColours.Token(15));
        Assert.Null(TerminalColours.Token(CellStyle.Default));
        Assert.Null(TerminalColours.Rgb(9));
        Assert.Equal(0xFF0000, TerminalColours.Rgb(196));
        Assert.Equal(0x5F87AF, TerminalColours.Rgb(67));
        Assert.Equal(0x080808, TerminalColours.Rgb(232));
        Assert.Equal(0xEEEEEE, TerminalColours.Rgb(255));
        Assert.Equal(0x0AC81E, TerminalColours.Rgb(CellStyle.FromRgb(10, 200, 30)));
    }

    private void A_style_is_drawn_in_its_colours_its_defaults_where_it_names_none_and_inverse_and_hidden_text_from_them()
    {
        const string Text = "text";
        const string Ground = "ground";
        Assert.Equal(new TextLook(LookColour.Of(Text), default, LookStyle.Bold | LookStyle.Underline),
            TerminalColours.Look(new CellStyle(CellStyle.Default, CellStyle.Default, CellFlags.Bold | CellFlags.Underline), Text, Ground));
        Assert.Equal(new TextLook(LookColour.Of(TokenKeys.Terminal_Ansi2), LookColour.Of(0xFF0000), LookStyle.Italic | LookStyle.Strike),
            TerminalColours.Look(new CellStyle(2, 196, CellFlags.Italic | CellFlags.Strike), Text, Ground));
        Assert.Equal(new TextLook(LookColour.Of(Ground), LookColour.Of(Text), LookStyle.None),
            TerminalColours.Look(new CellStyle(CellStyle.Default, CellStyle.Default, CellFlags.Inverse), Text, Ground));
        Assert.Equal(new TextLook(LookColour.Of(TokenKeys.Terminal_Ansi4), LookColour.Of(TokenKeys.Terminal_Ansi1), LookStyle.None),
            TerminalColours.Look(new CellStyle(1, 4, CellFlags.Inverse), Text, Ground));
        Assert.Equal(new TextLook(LookColour.Of(Ground), default, LookStyle.None),
            TerminalColours.Look(new CellStyle(3, CellStyle.Default, CellFlags.Hidden), Text, Ground));
        Assert.Equal(TerminalColours.Faint, TerminalColours.Look(new CellStyle(3, CellStyle.Default, CellFlags.Dim), Text, Ground).Opacity);
    }

}
