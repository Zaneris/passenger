using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using Passenger.Languages.CSharp;

namespace Passenger.UnitTests.Languages;

/// <summary>Every in-process parse of a C# text: nesting the parser recurses through unchecked runs out of no stack, and
/// brackets past what's worth its time are a tree of nothing the text's length, while real code reads as ever.</summary>
public class CSharpParsingTests
{
    private static string Nested(string before, string open, string inner, string close, string after, int deep) =>
        before + string.Concat(Enumerable.Repeat(open, deep)) + inner + string.Concat(Enumerable.Repeat(close, deep)) + after;

    [Theory]
    [InlineData("class C { int x = ", "a?.b", "", "", "; }")]
    [InlineData("class C { ", "A<", "int", ">", " x; }")]
    public void Nesting_of_no_bracket_a_stack_cant_hold_reads_whole(string before, string open, string inner, string close, string after)
    {
        string text = Nested(before, open, inner, close, after, 20_000);
        SmallStack.Run(() => Assert.Equal(text.Length, CSharpParsing.Parse(text).GetRoot().FullSpan.Length));
    }

    [Theory]
    [InlineData("class C { int x = ", "(", "1", ")", "; }")]
    [InlineData("class C { ", "(int a, ", "int b", ")", " x; }")]
    [InlineData("class C { bool x = a is ", "{ a: ", "1", " }", "; }")]
    [InlineData("class C { string x = ", "$\"{", "1", "}\"", "; }")]
    [InlineData("class C { [A(", "(", "1", ")", ")] int x; }")]
    [InlineData("", "#if A\n", "", "#endif\n", "")]
    public void Brackets_past_what_a_parser_is_given_are_a_tree_of_nothing_the_texts_length(string before, string open, string inner, string close, string after)
    {
        string text = Nested(before, open, inner, close, after, 100_000);
        SmallStack.Run(() =>
        {
            SyntaxNode root = CSharpParsing.Parse(text).GetRoot();
            Assert.Empty(((CompilationUnitSyntax)root).Members);
            Assert.Equal(text.Length, root.FullSpan.Length);
        });
    }

    [Theory]
    [InlineData("class C { int x = ", "(", "1", ")", "; }")]
    [InlineData("class C { bool x = a is ", "{ a: ", "1", " }", "; }")]
    public void Brackets_deeper_than_a_small_stack_holds_read_whole_where_a_parser_is_given_them(string before, string open, string inner, string close, string after)
    {
        string text = Nested(before, open, inner, close, after, 900);
        SmallStack.Run(() => Assert.NotEmpty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot()).Members));
    }

    [Fact]
    public void A_file_of_strings_each_leaving_a_bracket_open_reads_whole()
    {
        string text = "class C { void M() {\n" + string.Concat(Enumerable.Repeat("    F(\"(\", '[');\n", 5_000)) + "} }\n";
        Assert.False(DeepText.TooDeep(text), "Brackets in strings added up over a file's statements");
        Assert.NotEmpty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot(TestContext.Current.CancellationToken)).Members);
    }

    [Fact]
    public void A_change_reads_again_only_what_changed_and_a_paste_too_deep_leaves_a_tree_of_nothing()
    {
        SourceText written = SourceText.From("class C { int x = 1; }\n");
        SyntaxTree tree = CSharpParsing.Parse(written);
        SourceText typed = written.WithChanges(new TextChange(new TextSpan(18, 1), "2"));
        SyntaxTree changed = CSharpParsing.Changed(tree, typed);
        Assert.Contains("= 2", changed.GetRoot(TestContext.Current.CancellationToken).ToFullString(), StringComparison.Ordinal);
        string deep = Nested(string.Empty, "(", "1", ")", string.Empty, 100_000);
        SourceText pasted = written.WithChanges(new TextChange(new TextSpan(18, 1), deep));
        SmallStack.Run(() =>
        {
            SyntaxNode root = CSharpParsing.Changed(tree, pasted).GetRoot();
            Assert.Empty(((CompilationUnitSyntax)root).Members);
            Assert.Equal(pasted.Length, root.FullSpan.Length);
        });
    }

    [Fact]
    public void A_read_on_a_stack_of_its_own_gives_its_answer_and_what_it_threw()
    {
        Assert.Equal(1000, DeepText.Read(1000, () => 1000));
        Assert.Throws<FormatException>(() => DeepText.Read<int>(1000, () => throw new FormatException()));
    }
}
