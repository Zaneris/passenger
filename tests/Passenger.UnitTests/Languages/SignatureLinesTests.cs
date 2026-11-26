using Passenger.Editor;
using Passenger.Languages;

namespace Passenger.UnitTests.Languages;

/// <summary>A card's signature laid out a parameter a line: broken after its list's opening bracket and after each
/// parameter's comma, each parameter indented, its colours moved with their text; what is no list of parameters, or holds
/// none, left as it is.</summary>
public sealed class SignatureLinesTests
{
    [Fact]
    public void A_signature_is_laid_out_a_parameter_a_line_and_what_holds_no_list_is_left_as_it_is() => Cases.All(
        ("each parameter takes a line of its own and its colours move with it", Each_parameter_takes_a_line_of_its_own_and_its_colours_move_with_it),
        ("commas in a types arguments a nested bracket or a quoted default stay and a tag before the signature is no list", () => Cases.Each<(string, string)>(
        [
            ("void Log(string format = \"a, b\", params object[] args)", "void Log(\n    string format = \"a, b\",\n    params object[] args)"),
            ("int[] Pick((int, int) pair, Func<int, (int, int)> made)", "int[] Pick(\n    (int, int) pair,\n    Func<int, (int, int)> made)"),
            ("[Trace(1, 2)]\nvoid Log([Check(1, 2)] int count, [Name] string name)",
                "[Trace(1, 2)]\nvoid Log(\n    [Check(1, 2)] int count,\n    [Name] string name)"),
        ], row => Commas_in_a_types_arguments_a_nested_bracket_or_a_quoted_default_stay_and_a_tag_before_the_signature_is_no_list(row.Item1, row.Item2))),
        ("code with no parameters on one line is left as it is", () => Cases.Each<string>(
        [
            "int Twice(int value)\nint Thrice(int value)",
        ], Code_with_no_parameters_on_one_line_is_left_as_it_is)));

    private void Each_parameter_takes_a_line_of_its_own_and_its_colours_move_with_it()
    {
        const string code = "string Kinds.Described(string first, int year, bool middle = false) where T : new()";
        SignatureLines laid = SignatureLines.Of(code)!;
        Assert.Equal("string Kinds.Described(\n    string first,\n    int year,\n    bool middle = false) where T : new()", laid.Text);
        SyntaxSpan[] spans = [new(0, 6, TokenKind.Keyword), new(13, 9, TokenKind.Method), new(23, 6, TokenKind.Keyword), new(30, 5, TokenKind.Parameter),
            new(37, 3, TokenKind.Keyword), new(41, 4, TokenKind.Parameter), new(61, 5, TokenKind.Keyword)];
        foreach ((SyntaxSpan given, SyntaxSpan moved) in spans.Zip(laid.Map(spans)))
        {
            Assert.Equal(code.Substring(given.Start, given.Length), laid.Text.Substring(moved.Start, moved.Length));
            Assert.Equal(given.Kind, moved.Kind);
        }
    }

    private void Commas_in_a_types_arguments_a_nested_bracket_or_a_quoted_default_stay_and_a_tag_before_the_signature_is_no_list(string code, string laid) =>
        Assert.Equal(laid, SignatureLines.Of(code)?.Text);

    private void Code_with_no_parameters_on_one_line_is_left_as_it_is(string code) => Assert.Null(SignatureLines.Of(code));
}
