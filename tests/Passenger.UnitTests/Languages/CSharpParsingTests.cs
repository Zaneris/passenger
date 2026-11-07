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
    [InlineData("/// <see cref=\"", "A{", "B", "}", "\"/>\nclass C { }\n")]
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

    /// <summary>The shapes of real test and data files, whose brackets are only text: a list of a parser's inputs in one
    /// statement, with no semicolon to stop the count, and braces that never close.</summary>
    [Theory]
    [InlineData("class C { string[] x = { ", "\"(\", ", "", "}; }")]
    [InlineData("class C { string[] x = { ", "@\"[\", ", "", "}; }")]
    [InlineData("class C { char[] x = { ", "'(', ", "", "}; }")]
    [InlineData("class C { string[] x = { ", "$\"(\", ", "", "}; }")]
    [InlineData("class C { string x = \"\"\"\n", "((\n", "", "\"\"\"; }")]
    [InlineData("class C { void M() {\n", "F(\"{\");\n", "", "} }")]
    [InlineData("class C {\n", "// {\n", "", "}")]
    [InlineData("class C {\n/*", " ( { [", "*/", "}")]
    [InlineData("class C {\n#if NEVER\n", "(((\n", "#endif\n", "}")]
    public void Brackets_that_are_only_text_read_whole_however_many(string before, string each, string inner, string after)
    {
        string text = Nested(before, each, inner, string.Empty, after, 5_000);
        Assert.True(DeepText.TooDeep(text), "The text as it stands is no longer read as deep, so nothing here reads its tokens");
        SmallStack.Run(() => Assert.NotEmpty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot()).Members));
    }

    /// <summary>What a text reader would take for a string or a comment left open is what the lexer reads past: a branch left
    /// out and a merge's other side are no code, so the brackets after them are.</summary>
    [Theory]
    [InlineData("#if NEVER\n/*\n#endif\nclass C { int x = ")]
    [InlineData("#if NEVER\n@\"\n#endif\nclass C { int x = ")]
    [InlineData("<<<<<<< ours\n=======\n/*\n>>>>>>> theirs\nclass C { int x = ")]
    [InlineData("#region /*\nclass C { int x = ")]
    public void Brackets_after_what_only_looks_like_an_open_comment_are_still_too_deep(string before)
    {
        string text = Nested(before, "(", "1", ")", "; }", 100_000);
        SmallStack.Run(() => Assert.Empty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot()).Members));
    }

    [Fact]
    public void A_text_past_what_a_stack_holds_is_too_deep_as_it_reads_as_the_lexer_could_run_out()
    {
        string text = Nested("class C { string x = $\"{", "(", "1", ")", "}\"; }", 2_000_000);
        Assert.False(DeepText.Holds(text.Length), "This text no longer reaches past what a stack holds");
        SmallStack.Run(() => Assert.Empty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot()).Members));
    }

    [Fact]
    public void A_text_too_deep_says_where_it_went_too_deep_and_one_read_says_nothing()
    {
        string text = Nested("class C { int x = ", "(", "1", ")", "; }", 100_000);
        // The class's brace and 999 round ones before it make 1,000.
        SmallStack.Run(() => Assert.Equal("class C { int x = ".Length + 999, CSharpParsing.DeepAt(CSharpParsing.Parse(text))));
        string strings = Nested("class C { string[] x = { ", "\"(\", ", "", string.Empty, "}; }", 5_000);
        SmallStack.Run(() => Assert.Null(CSharpParsing.DeepAt(CSharpParsing.Parse(strings))));
        string commented = "// (((\n" + text;
        SmallStack.Run(() => Assert.Equal("// (((\nclass C { int x = ".Length + 999, CSharpParsing.DeepAt(CSharpParsing.Parse(commented))));
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
