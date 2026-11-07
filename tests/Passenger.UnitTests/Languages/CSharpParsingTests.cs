using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Languages;

/// <summary>Every in-process parse of a C# text: nesting the parser recurses through unchecked runs out of no stack, and
/// brackets past what's worth its time are a tree of nothing the text's length, while real code reads as ever.</summary>
public class CSharpParsingTests
{
    private static string Nested(string before, string open, string inner, string close, string after, int deep) =>
        before + string.Concat(Enumerable.Repeat(open, deep)) + inner + string.Concat(Enumerable.Repeat(close, deep)) + after;

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

    /// <summary>C#'s chains that nest with no bracket, each a level of a parser's and a binder's stack: a conditional access, a
    /// pattern's <c>not</c>, a type's arguments and a lambda's body.</summary>
    [Theory]
    [InlineData("class C { object x = a", "?.b", "", "", "; }")]
    [InlineData("class C { bool x = a is ", "not ", "null", "", "; }")]
    [InlineData("class C { ", "A<", "int", ">", " x; }")]
    [InlineData("class C { object x = ", "x => ", "0", "", "; }")]
    [InlineData("class C { object x = ", "(int x) => ", "0", "", "; }")]
    public void Links_past_what_a_parser_is_given_are_a_tree_of_nothing_the_texts_length(string before, string open, string inner, string close, string after)
    {
        string text = Nested(before, open, inner, close, after, 10_000);
        SmallStack.Run(() =>
        {
            SyntaxNode root = CSharpParsing.Parse(text).GetRoot();
            Assert.Empty(((CompilationUnitSyntax)root).Members);
            Assert.Equal(text.Length, root.FullSpan.Length);
        });
    }

    /// <summary>Where a chain of links goes past the bound: at its 1,000th link, the class's brace the first level, or its
    /// 999th where a link opens a bracket of its own. A conditional access's <c>?</c> is read only before what it accesses,
    /// which may come after a space.</summary>
    [Theory]
    [InlineData("class C { object x = a", "?.b")]
    [InlineData("class C { object x = a", "? .b")]
    [InlineData("class C { bool x = a is ", "not ")]
    [InlineData("class C { ", "A<")]
    [InlineData("class C { object x = ", "x => ")]
    [InlineData("class C { object x = ", "(int x) => ")]
    public void A_chain_of_links_goes_too_deep_at_its_thousandth(string before, string link)
    {
        string text = before + string.Concat(Enumerable.Repeat(link, 10_000));
        int? at = null;
        SmallStack.Run(() => at = CSharpParsing.DeepAt(TextSnapshot.From(text), CSharpParsing.Default));
        Assert.InRange(at ?? -1, before.Length + (998 * link.Length), before.Length + (1000 * link.Length) - 1);
    }

    /// <summary>The shapes in which a parser reads past a <c>?[</c> to the end of what follows, a <c>?[</c> there read past
    /// in turn, each level at least doubling its time: one past the eleventh is too deep, wherever it lies in the expression,
    /// and however the <c>?</c> and the <c>[</c> are apart.</summary>
    [Theory]
    [InlineData("class C { object x = a", "?[0]")]
    [InlineData("class C { object x = a", "?\n[0]")]
    [InlineData("class C { object x = a", "? /* */ [0]")]
    [InlineData("class C { object x = a", "?.b?[0]")]
    [InlineData("class C { object x = a", "?[0]!")]
    [InlineData("class C { object x = a", "?[0](1).b[2]")]
    [InlineData("class C { object x = ", "a?[")]
    [InlineData("class C { object x = ", "a?[x => ")]
    [InlineData("class C { object x = ", "a?[x => { return ")]
    [InlineData("class C { object x = ", "a?[new C { P = ")]
    [InlineData("class C { object x = ", "a?[0] switch { _ => ")]
    [InlineData("class C { object x = 0", " + a?[0]")]
    [InlineData("class C { object x = 0", " ?? a?[0]")]
    [InlineData("class C { object x = 0", " + F<A, B>(0) + a?[0]")]
    [InlineData("class C { object x = ", "c ? a?[0] : ")]
    [InlineData("class C { object x = ", "c ? [")]
    [InlineData("class C { object x = ", "c ? b?[")]
    [InlineData("class C { bool x = a is int", "?[]")]
    public void A_run_of_conditional_accesses_read_ahead_through_goes_too_deep_at_its_twelfth(string before, string link)
    {
        string text = before + string.Concat(Enumerable.Repeat(link, 30));
        int? at = null;
        SmallStack.Run(() => at = CSharpParsing.DeepAt(TextSnapshot.From(text), CSharpParsing.Default));
        Assert.InRange(at ?? -1, before.Length + (11 * link.Length), before.Length + (12 * link.Length) - 1);
        string sixteen = before + string.Concat(Enumerable.Repeat(link, 16));
        SmallStack.Run(() => Assert.Empty(((CompilationUnitSyntax)CSharpParsing.Parse(sixteen).GetRoot()).Members));
        Assert.False(CSharpParsing.ReadsAsType("int" + string.Concat(Enumerable.Repeat("?[]", 12))), "A type a parser reads past twelve times reads as a type");
    }

    /// <summary>What real code holds of <c>?[</c>: types of nullable arrays in member after member, chains of eleven, and
    /// accesses in statements of a lambda an access reads ahead through, each statement ending its own.</summary>
    [Theory]
    [InlineData("class C {\n", "    public static int?[] M(int?[] a, object?[] b) { return a?[0] is null ? null : a; }\n", "}")]
    [InlineData("class C {\n", "    public float?[]? P { get; set; }\n", "}")]
    [InlineData("class C {\n", "    int?[] f = new int?[] { 1 };\n", "}")]
    [InlineData("class C { object x = a?[0] ?? F(y => {\n", "    y = b?[0]?[1]?[2];\n", "}); }")]
    [InlineData("class C { void M() {\n", "    x = a?[0]?[1]?[2]?[3]?[4]?[5]?[6]?[7]?[8]?[9]?[10];\n", "} }")]
    public void Conditional_accesses_of_real_code_read_whole_however_many(string before, string each, string after)
    {
        string text = before + string.Concat(Enumerable.Repeat(each, 1_000)) + after;
        Assert.Null(CSharpParsing.DeepAt(TextSnapshot.From(text), CSharpParsing.Default));
        Assert.NotEmpty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot(TestContext.Current.CancellationToken)).Members);
        Assert.True(CSharpParsing.ReadsAsType("int?[]?[]"), "A nullable array of nullable arrays no longer reads as a type");
    }

    [Theory]
    [InlineData("class C { int x = ", "(", "1", ")", "; }")]
    [InlineData("class C { bool x = a is ", "{ a: ", "1", " }", "; }")]
    [InlineData("class C { ", "A<", "int", ">", " x; }")]
    [InlineData("class C { object x = ", "x => ", "0", "", "; }")]
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
    public void A_file_of_statements_each_with_links_reads_whole()
    {
        string statement = "    x = a?.b?[0] is not null ? F<List<int>>(y => y) : a < b;\n";
        string text = "class C { void M() {\n" + string.Concat(Enumerable.Repeat(statement, 5_000)) + "} }\n";
        Assert.False(DeepText.TooDeep(text, csharp: true), "Links added up over a file's statements");
        Assert.NotEmpty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot(TestContext.Current.CancellationToken)).Members);
    }

    /// <summary>The shapes of real test and data files, whose brackets are only text: a list of a parser's inputs in one
    /// statement, with no semicolon to stop the count, and braces that never close. The text count reads past a string or a
    /// comment it knows, and hands the lexer one it doesn't (a raw string, a branch left out).</summary>
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
    [InlineData("class C { string[] x = { ", "\"a?.b\", ", "", "}; }")]
    [InlineData("class C { string[] x = { ", "\"not\", ", "", "}; }")]
    [InlineData("class C { string[] x = { ", "\"<\", ", "", "}; }")]
    [InlineData("class C { string[] x = { ", "\"=>\", ", "", "}; }")]
    [InlineData("class C {\n", "// is it not here?\n", "", "}")]
    public void Brackets_that_are_only_text_read_whole_however_many(string before, string each, string inner, string after)
    {
        string text = Nested(before, each, inner, string.Empty, after, 5_000);
        SmallStack.Run(() => Assert.NotEmpty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot()).Members));
    }

    /// <summary>A string, a character or a comment the text count reads as the lexer does opens nothing, so a file of them
    /// never waits on the lexer.</summary>
    [Theory]
    [InlineData("class C { string[] x = { ", "\"(\\\"{[\", ")]
    [InlineData("class C { string[] x = { ", "@\"(\n\"\"{\", ")]
    [InlineData("class C { char[] x = { ", "'(', '\\'', ")]
    [InlineData("class C { string[] x = { ", "$\"({{{x}\", ")]
    [InlineData("class C { string[] x = { ", "$\"{F(\"(\")}({x:N2}\", ")]
    [InlineData("class C { string[] x = { ", "$@\"{x}(\n\"\"{{\", ")]
    [InlineData("class C { string[] x = { ", "@$\"{F(\"(\")}(\", ")]
    [InlineData("class C { string[] x = { ", "$\"{F($\"{x}(\")}(\", ")]
    [InlineData("class C { string[] x = { ", "\"\"\"\n    (({{\"\"\n    \"\"\", ")]
    [InlineData("class C { string[] x = { ", "\"\"\"\"(\"\"\"\"\"\", ")]
    [InlineData("class C { string[] x = { ", "$$\"\"\"{{x}}(({\"\"\", ")]
    [InlineData("class C { string[] x = { ", "$\"\"\"\n    {F(\"(\")}(\n    \"\"\", ")]
    [InlineData("class C {\n", "// ( { [\n")]
    [InlineData("class C {\n", "/* ( { [ */\n")]
    [InlineData("class C {\n", "/// <see cref=\"A{B}\"/> (\nint x;\n")]
    public void Strings_and_comments_the_text_count_reads_open_nothing(string before, string each)
    {
        Assert.False(DeepText.TooDeep(before + string.Concat(Enumerable.Repeat(each, 5_000)) + "}", csharp: true), "What a string or a comment holds counted as open");
    }

    /// <summary>What a string, a character, a comment or a branch left out holds closes nothing, so brackets nested past
    /// what a parser is given around them are still too deep: in the text as it stands, and in an interpolated string's text
    /// read on its own before its holes.</summary>
    [Theory]
    [InlineData("class C { object x = ", "(\";\" + ", ")")]
    [InlineData("class C { object x = ", "(\"\\\";\" + ", ")")]
    [InlineData("class C { object x = ", "(\")]}\" + ", ")")]
    [InlineData("class C { object x = ", "(';' + ", ")")]
    [InlineData("class C { object x = ", "(@\"\n;\n\" + ", ")")]
    [InlineData("class C { object x = ", "($\";{0}\" + ", ")")]
    [InlineData("class C { object x = ", "($\"{\";\"}\" + ", ")")]
    [InlineData("class C { object x = ", "(/*;)*/", ")")]
    [InlineData("class C { object x = ", "(// ;\n", ")")]
    [InlineData("class C { object x = ", "(\n#if NEVER\n;\n#endif\n", ")")]
    [InlineData("class C { object x = ", "(@$\"\n;\n\" + ", ")")]
    [InlineData("class C { object x = ", "($\"{$\";\"}\" + ", ")")]
    [InlineData("class C { object x = ", "(\"\"\"\n;)\n\"\"\" + ", ")")]
    [InlineData("class C { object x = ", "($$\"\"\"{{\";\"}};)\"\"\" + ", ")")]
    [InlineData("class C { object x = ", "(\n/// ;)\n", ")")]
    [InlineData("class C { ", "A</*>*/", ">")]
    public void Brackets_around_what_closes_nothing_are_still_too_deep(string before, string open, string close)
    {
        string text = before + string.Concat(Enumerable.Repeat(open, 1_200)) + "int" + string.Concat(Enumerable.Repeat(close, 1_200)) + " x; }";
        SmallStack.Run(() =>
        {
            Assert.NotNull(CSharpParsing.DeepAt(TextSnapshot.From(text), CSharpParsing.Default));
            Assert.Empty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot()).Members);
        });
        string around = before + new string('(', 600) + "$\"; {" + new string('(', 600) + "1" + new string(')', 600) + "}\"" + new string(')', 600) + "; }";
        SmallStack.Run(() => Assert.NotNull(CSharpParsing.DeepAt(TextSnapshot.From(around), CSharpParsing.Default)));
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
