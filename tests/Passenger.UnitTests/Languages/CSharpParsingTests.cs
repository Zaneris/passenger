using System.Text;
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

    /// <summary>A documentation comment's elements nested past what a parser is given are too deep, as brackets are: its parser
    /// recurses through each unchecked, some 3,000 deep running a thread pool's thread out of stack, which would end the C#
    /// language server reading the file. On one line or many, in a block comment, with attributes, opened alone or closed.</summary>
    [Theory]
    [InlineData("/// ", "<a>", "", "\nclass C { }\n")]
    [InlineData("/// ", "<a>", "</a>", "\nclass C { }\n")]
    [InlineData("", "/// <a>\n", "/// </a>\n", "class C { }\n")]
    [InlineData("/** ", "<a>", "", " */\nclass C { }\n")]
    [InlineData("/// ", "<a b='x'>", "", "\nclass C { }\n")]
    [InlineData("class C {\n", "/// <a>\n", "", "int x;\n}\n")]
    public void A_documentation_comments_elements_nested_past_what_a_parser_is_given_are_too_deep(string before, string open, string close, string after)
    {
        Assert.Null(CSharpParsing.DeepAt(CSharpParsing.Parse(Nested(before, open, string.Empty, close, after, 900))));
        string text = Nested(before, open, string.Empty, close, after, 1_001);

        SyntaxTree tree = CSharpParsing.Parse(text);

        Assert.True(CSharpParsing.DeepAt(tree) is not null, $"A documentation comment's elements 1,001 deep, {open}, read as a parser's tree");
        Assert.Empty(((CompilationUnitSyntax)tree.GetRoot(TestContext.Current.CancellationToken)).Members);
    }

    /// <summary>An element typed into a documentation comment nested as deep as a parser is given takes it too deep, as the
    /// file opened so would be.</summary>
    [Fact]
    public void Typing_an_element_into_a_documentation_comment_as_deep_as_a_parser_is_given_reads_as_too_deep()
    {
        SourceText text = SourceText.From(Nested("/// ", "<a>", string.Empty, string.Empty, "\nclass C { }\n", 1_000));
        SyntaxTree tree = CSharpParsing.Parse(text);
        Assert.Null(CSharpParsing.DeepAt(tree));
        int at = text.ToString().IndexOf("\nclass", StringComparison.Ordinal);

        SyntaxTree after = CSharpParsing.Changed(tree, text.WithChanges(new TextChange(new TextSpan(at, 0), "<a>")));

        Assert.True(CSharpParsing.DeepAt(after) is not null, "An element typed into a documentation comment 1,000 deep left a parser's tree");
    }

    private static string Links(int count) => string.Concat(Enumerable.Repeat("?[0]", count));

    /// <summary>Typing that makes a run of conditional accesses one past the eleventh, by a link typed, a semicolon taken
    /// away between two runs, a quote or a comment taken away from one, a run in a lambda's statement within another, or a
    /// run past a branch left out, is too deep as a file opened so would be.</summary>
    [Theory]
    [InlineData("class C { void M() { x = a{11}; } }", "; }", "?[0]; }")]
    [InlineData("class C { void M() { x = a{6};\n+ a{6}; } }", ";\n+", "\n+")]
    [InlineData("class C { void M() { x = \"a{12}\"; } }", "\"a", "a")]
    [InlineData("class C { void M() { x = a // {12}\n; } }", "// ", "")]
    [InlineData("class C { void M() { x = a /* {12} */; } }", "/* ", "")]
    [InlineData("class C { void M() { x = a{6} ?? F(y => { y = b; }); } }", "y = b;", "y = b{6};")]
    [InlineData("class C { int x = a{6} ?? F(y => { y = b; }); }", "y = b;", "y = b{6};")]
    [InlineData("#if !NEVER\nclass C { void M() { x = a{6}\n#else\n;\n#endif\n+ b; } }\n", "+ b;", "+ b{6};")]
    public void Typing_a_run_of_conditional_accesses_past_the_eleventh_reads_as_too_deep(string written, string find, string typed)
    {
        SourceText text = SourceText.From(written.Replace("{6}", Links(6)).Replace("{11}", Links(11)).Replace("{12}", Links(12)));
        SyntaxTree tree = CSharpParsing.Parse(text);
        Assert.Null(CSharpParsing.DeepAt(tree));
        int at = text.ToString().IndexOf(find, StringComparison.Ordinal);
        SourceText changed = text.WithChanges(new TextChange(new TextSpan(at, find.Length), typed.Replace("{6}", Links(6))));
        SyntaxTree after = CSharpParsing.Changed(tree, changed);
        Assert.NotNull(CSharpParsing.DeepAt(after));
        Assert.Empty(((CompilationUnitSyntax)after.GetRoot(TestContext.Current.CancellationToken)).Members);
    }

    /// <summary>Typing that keeps every run at eleven or fewer, or changes only words, leaves the tree a parser made.</summary>
    [Theory]
    [InlineData("class C { void M() { x = a{10}; } }", "; }", "?[0]; }")]
    [InlineData("class C { void M() { x = a{6};\n y = a{6}; } }", "y = a", "y = b")]
    [InlineData("class C { void M() { x = a{6}; y = b{6}; } }", "y = b", "y = b?[1]")]
    [InlineData("class C { int x = a{6} ?? F(y => { y = b; }); }", "y = b;", "y = b?[0]?[0];")]
    public void Typing_runs_of_eleven_or_fewer_keeps_the_tree(string written, string find, string typed)
    {
        SourceText text = SourceText.From(written.Replace("{6}", Links(6)).Replace("{10}", Links(10)));
        SyntaxTree tree = CSharpParsing.Parse(text);
        int at = text.ToString().IndexOf(find, StringComparison.Ordinal);
        SyntaxTree after = CSharpParsing.Changed(tree, text.WithChanges(new TextChange(new TextSpan(at, find.Length), typed)));
        Assert.Null(CSharpParsing.DeepAt(after));
        Assert.NotEmpty(((CompilationUnitSyntax)after.GetRoot(TestContext.Current.CancellationToken)).Members);
    }

    /// <summary>A key typed in a statement of a long file reads that statement, not the file: what measuring it reads and
    /// allocates in a file of 4,000 methods is what it does in one of 40.</summary>
    [Fact]
    public void A_key_typed_is_measured_by_its_statement_not_the_file()
    {
        static (long Read, long Bytes) Measured(int methods)
        {
            StringBuilder written = new("class C\n{\n");
            for (int i = 0; i < methods; i++)
            {
                written.Append("    void M").Append(i).Append("(object a)\n    {\n        var x = a?[0]?[1] ?? F(\"(\", ')');\n        G(x);\n    }\n");
            }
            SourceText text = SourceText.From(written.Append("}\n").ToString());
            SyntaxTree tree = CSharpParsing.Parse(text);
            int at = text.ToString().IndexOf("?[1]", text.Length / 2, StringComparison.Ordinal);
            SourceText typed = text.WithChanges(new TextChange(new TextSpan(at, 0), "?[2]"));
            IReadOnlyList<TextChangeRange> changes = typed.GetChangeRanges(text);
            Assert.False(CSharpParsing.TypedMayGoDeep(tree, typed, changes));
            (long read, long bytes) = (CSharpParsing.TypedRead, GC.GetAllocatedBytesForCurrentThread());
            Assert.False(CSharpParsing.TypedMayGoDeep(tree, typed, changes));
            return (CSharpParsing.TypedRead - read, GC.GetAllocatedBytesForCurrentThread() - bytes);
        }
        (long smallRead, long small) = Measured(40);
        (long largeRead, long large) = Measured(4_000);
        Assert.True(largeRead == smallRead && smallRead < 1_000, $"Measuring a key read {largeRead} characters of a long file against {smallRead} of a short one, so a key costs what the file holds");
        Assert.True(large <= small + 256, $"Measuring a key allocated {large} bytes in a long file against {small} in a short one, so a key costs what the file holds");
    }

    [Fact]
    public void A_read_on_a_stack_of_its_own_gives_its_answer_and_what_it_threw()
    {
        Assert.Equal(1000, DeepText.Read(1000, () => 1000));
        Assert.Throws<FormatException>(() => DeepText.Read<int>(1000, () => throw new FormatException()));
    }

    /// <summary>An expression or a type's name long but shallow, as the debugger reads one per row, is read on the thread that
    /// asks, with no thread of its own; one nested deeper than any stack is sure to hold goes to a stack sized for it, and
    /// one nested past what the parser reads is still refused.</summary>
    [Fact]
    public void A_long_shallow_expression_is_read_on_the_asking_thread_and_a_deep_one_on_a_stack_of_its_own()
    {
        const string Type = "System.Collections.Generic.Dictionary<System.String, System.Collections.Generic.List<System.Int32>>";
        string expression = $"(({Type})values)[\"key\"].Count > 3 && items?.Count is not null";
        int asking = Environment.CurrentManagedThreadId;
        long spawned = DeepText.ReadBytes;
        Assert.True(CodeBrackets.ReadsAsType(Type), "A long generic type's name did not read as a type");
        Assert.True(CodeBrackets.Expression(expression) is { ContainsDiagnostics: false }, "A long shallow expression did not read");
        Assert.Equal(asking, CodeBrackets.OnStack(expression, () => Environment.CurrentManagedThreadId));
        Assert.True(DeepText.ReadBytes == spawned, "A long shallow expression or type's name was read on a thread of its own");

        string nested = new string('(', 100) + "1" + new string(')', 100);
        Assert.NotEqual(asking, CodeBrackets.OnStack(nested, () => Environment.CurrentManagedThreadId));
        Assert.True(CodeBrackets.Expression(nested) is { ContainsDiagnostics: false }, "Brackets 100 deep did not read on a stack sized for them");
        Assert.True(DeepText.ReadBytes > spawned, "Brackets 100 deep were read on the asking thread");
        string past = new string('(', 20_000) + "1" + new string(')', 20_000);
        SmallStack.Run(() => Assert.True(CodeBrackets.Expression(past) is null && !CodeBrackets.ReadsAsType(past), "Brackets 20,000 deep were read"));
    }
}
