using Passenger.Editor;
using Passenger.Languages;

namespace Passenger.UnitTests.Languages;

/// <summary>A card's signature laid out a parameter a line: broken after its list's opening bracket and after each
/// parameter's comma, each parameter indented, its colours moved with their text; what is no list of parameters, or holds
/// none, left as it is.</summary>
public sealed class SignatureLinesTests
{
    [Fact]
    public void Each_parameter_takes_a_line_of_its_own_and_its_colours_move_with_it()
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

    [Theory]
    [InlineData("(extension) char IEnumerable<char>.Aggregate<char>(Func<char, char, char> func)",
        "(extension) char IEnumerable<char>.Aggregate<char>(\n    Func<char, char, char> func)")]
    [InlineData("void Log(string format = \"a, b\", params object[] args)", "void Log(\n    string format = \"a, b\",\n    params object[] args)")]
    [InlineData("int[] Pick((int, int) pair, Func<int, (int, int)> made)", "int[] Pick(\n    (int, int) pair,\n    Func<int, (int, int)> made)")]
    [InlineData("string List<T>.this[int index, bool wrap] { get; }", "string List<T>.this[\n    int index,\n    bool wrap] { get; }")]
    [InlineData("Task Run(Func<Task> work, Action<int> done = null)", "Task Run(\n    Func<Task> work,\n    Action<int> done = null)")]
    public void Commas_in_a_types_arguments_a_nested_bracket_or_a_quoted_default_stay_and_a_tag_before_the_signature_is_no_list(string code, string laid) =>
        Assert.Equal(laid, SignatureLines.Of(code)?.Text);

    [Fact]
    public void Each_parameter_is_found_where_it_stands_without_the_spaces_round_it()
    {
        const string code = "Task.Run<T>(Func<T, int> work, [bool  loud = false] ,  int count)";
        Assert.Equal(["Func<T, int> work", "[bool  loud = false]", "int count"], SignatureLines.Parameters(code).Select(p => code.Substring(p.Start, p.Length)));
        Assert.Empty(SignatureLines.Parameters("int Helper.Count()"));
        Assert.Empty(SignatureLines.Parameters("(local variable) string? c"));
    }

    [Theory]
    [InlineData("int Helper.Count()")]
    [InlineData("(local variable) string? c")]
    [InlineData("class Program")]
    [InlineData("int Twice(int value)\nint Thrice(int value)")]
    [InlineData("void Broken(int value")]
    public void Code_with_no_parameters_on_one_line_is_left_as_it_is(string code) => Assert.Null(SignatureLines.Of(code));
}
