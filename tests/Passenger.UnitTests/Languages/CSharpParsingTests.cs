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

    /// <summary>Each shape the parser recurses through unchecked, nested past what it is given, is a tree of nothing the
    /// text's length and says where it went too deep; less, on a small stack, reads whole; and what is read on a stack of
    /// its own goes there only when deep.</summary>
    [Fact]
    public void Nesting_past_what_a_parser_is_given_is_a_tree_of_nothing_the_texts_length_and_less_reads_whole() => Cases.All(
        ("brackets past what a parser is given are a tree of nothing the text's length", () => Cases.Each(
        [
            ("class C { int x = ", "(", "1", ")", "; }"),
            ("class C { ", "(int a, ", "int b", ")", " x; }"),
            ("class C { bool x = a is ", "{ a: ", "1", " }", "; }"),
            ("class C { string x = ", "$\"{", "1", "}\"", "; }"),
            ("class C { [A(", "(", "1", ")", ")] int x; }"),
            ("", "#if A\n", "", "#endif\n", ""),
            ("/// <see cref=\"", "A{", "B", "}", "\"/>\nclass C { }\n"),
        ], row => TreeOfNothing(Nested(row.Item1, row.Item2, row.Item3, row.Item4, row.Item5, 100_000)))),
        ("links past what a parser is given (a conditional access, a pattern's not, a type's arguments, a lambda's body) are a tree of nothing the text's length", () => Cases.Each(
        [
            ("class C { object x = a", "?.b", "", "", "; }"),
            ("class C { bool x = a is ", "not ", "null", "", "; }"),
            ("class C { ", "A<", "int", ">", " x; }"),
            ("class C { object x = ", "x => ", "0", "", "; }"),
            ("class C { object x = ", "(int x) => ", "0", "", "; }"),
        ], row => TreeOfNothing(Nested(row.Item1, row.Item2, row.Item3, row.Item4, row.Item5, 10_000)))),
        ("a chain of links goes too deep at its thousandth, or its 999th where a link opens a bracket, a ? read only before what it accesses", () => Cases.Each(
        [
            ("class C { object x = a", "?.b"),
            ("class C { object x = a", "? .b"),
            ("class C { ", "A<"),
            ("class C { object x = ", "(int x) => "),
        ], row =>
        {
            string text = row.Item1 + string.Concat(Enumerable.Repeat(row.Item2, 10_000));
            int? at = null;
            SmallStack.Run(() => at = CSharpParsing.DeepAt(TextSnapshot.From(text), CSharpParsing.Default));
            Assert.InRange(at ?? -1, row.Item1.Length + (998 * row.Item2.Length), row.Item1.Length + (1000 * row.Item2.Length) - 1);
        })),
        ("a documentation comment's elements nested past what a parser is given are too deep, on one line or many, in a block comment or with attributes", () => Cases.Each(
        [
            ("/// ", "<a>", "", "\nclass C { }\n"),
            ("", "/// <a>\n", "/// </a>\n", "class C { }\n"),
            ("/** ", "<a>", "", " */\nclass C { }\n"),
            ("/// ", "<a b='x'>", "", "\nclass C { }\n"),
        ], row =>
        {
            Assert.Null(CSharpParsing.DeepAt(CSharpParsing.Parse(Nested(row.Item1, row.Item2, string.Empty, row.Item3, row.Item4, 900))));
            SyntaxTree tree = CSharpParsing.Parse(Nested(row.Item1, row.Item2, string.Empty, row.Item3, row.Item4, 1_001));
            Assert.True(CSharpParsing.DeepAt(tree) is not null, $"A documentation comment's elements 1,001 deep, {row.Item2}, read as a parser's tree");
            Assert.Empty(((CompilationUnitSyntax)tree.GetRoot(TestContext.Current.CancellationToken)).Members);
        })),
        ("brackets deeper than a small stack holds read whole where a parser is given them", () => Cases.Each(
        [
            ("class C { int x = ", "(", "1", ")", "; }"),
            ("class C { object x = ", "x => ", "0", "", "; }"),
        ], row =>
        {
            string text = Nested(row.Item1, row.Item2, row.Item3, row.Item4, row.Item5, 900);
            SmallStack.Run(() => Assert.NotEmpty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot()).Members));
        })),
        ("a text past what a stack holds is too deep, as the lexer could run out reading it", () =>
        {
            string text = Nested("class C { string x = $\"{", "(", "1", ")", "}\"; }", 2_000_000);
            Assert.False(DeepText.Holds(text.Length), "This text no longer reaches past what a stack holds");
            SmallStack.Run(() => Assert.Empty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot()).Members));
        }),
        ("a text too deep says where it went too deep and one read says nothing", () =>
        {
            string text = Nested("class C { int x = ", "(", "1", ")", "; }", 100_000);
            // The class's brace and 999 round ones before it make 1,000.
            SmallStack.Run(() => Assert.Equal("class C { int x = ".Length + 999, CSharpParsing.DeepAt(CSharpParsing.Parse(text))));
            string strings = Nested("class C { string[] x = { ", "\"(\", ", "", string.Empty, "}; }", 5_000);
            SmallStack.Run(() => Assert.Null(CSharpParsing.DeepAt(CSharpParsing.Parse(strings))));
            string commented = "// (((\n" + text;
            SmallStack.Run(() => Assert.Equal("// (((\nclass C { int x = ".Length + 999, CSharpParsing.DeepAt(CSharpParsing.Parse(commented))));
        }),
        ("a read on a stack of its own gives its answer and what it threw", () =>
        {
            Assert.Equal(1000, DeepText.Read(1000, () => 1000));
            Assert.Throws<FormatException>(() => DeepText.Read<int>(1000, () => throw new FormatException()));
        }),
        ("a long shallow expression is read on the asking thread, a deep one on a stack of its own, and one past what the parser reads refused", LongShallowExpressionsReadOnTheAskingThread));

    /// <summary>The shapes of real test and data files, whose brackets are only text, read whole; and what a string, a
    /// character, a comment or a branch left out holds closes nothing, so brackets nested past what a parser is given around
    /// it are still too deep.</summary>
    [Fact]
    public void Strings_and_comments_open_and_close_nothing_however_many() => Cases.All(
        ("brackets that are only text read whole however many, the lexer handed what the text count doesn't know", () => Cases.Each(
        [
            ("class C { string[] x = { ", "\"(\", ", "", "}; }"),
            ("class C { char[] x = { ", "'(', ", "", "}; }"),
            ("class C { string x = \"\"\"\n", "((\n", "", "\"\"\"; }"),
            ("class C {\n/*", " ( { [", "*/", "}"),
            ("class C {\n#if NEVER\n", "(((\n", "#endif\n", "}"),
            ("class C { string[] x = { ", "\"a?.b\", ", "", "}; }"),
        ], row =>
        {
            string text = Nested(row.Item1, row.Item2, row.Item3, string.Empty, row.Item4, 5_000);
            SmallStack.Run(() => Assert.NotEmpty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot()).Members));
        })),
        ("a string, a character or a comment the text count reads as the lexer does opens nothing", () => Cases.Each(
        [
            ("class C { string[] x = { ", "\"(\\\"{[\", "),
            ("class C { string[] x = { ", "@\"(\n\"\"{\", "),
            ("class C { char[] x = { ", "'(', '\\'', "),
            ("class C { string[] x = { ", "$\"{F(\"(\")}({x:N2}\", "),
            ("class C { string[] x = { ", "\"\"\"\"(\"\"\"\"\"\", "),
            ("class C { string[] x = { ", "$$\"\"\"{{x}}(({\"\"\", "),
            ("class C {\n", "/// <see cref=\"A{B}\"/> (\nint x;\n"),
        ], row => Assert.False(DeepText.TooDeep(row.Item1 + string.Concat(Enumerable.Repeat(row.Item2, 5_000)) + "}", csharp: true), "What a string or a comment holds counted as open"))),
        ("a name holding a mark, or starting with a letter number, is read whole and opens nothing", () => Cases.Each(
        [
            ("class C { object[] x = { ", "ánot, "),
            ("class C { void M() {\n", "    F(@Ⅻ);\n"),
        ], row => Assert.False(DeepText.TooDeep(row.Item1 + string.Concat(Enumerable.Repeat(row.Item2, 5_000)) + "}", csharp: true), "A name's mark or letter number was read as no name's, so what follows counted as open"))),
        ("brackets around what closes nothing are still too deep, in the text and in an interpolated string's text read alone", () => Cases.Each(
        [
            ("class C { object x = ", "(\";\" + ", ")"),
            ("class C { object x = ", "(\"\\\";\" + ", ")"),
            ("class C { object x = ", "(';' + ", ")"),
            ("class C { object x = ", "(@\"\n;\n\" + ", ")"),
            ("class C { object x = ", "($\";{0}\" + ", ")"),
            ("class C { object x = ", "($\"{\";\"}\" + ", ")"),
            ("class C { object x = ", "(/*;)*/", ")"),
            ("class C { object x = ", "(// ;\n", ")"),
            ("class C { object x = ", "(\n#if NEVER\n;\n#endif\n", ")"),
            ("class C { object x = ", "(\"\"\"\n;)\n\"\"\" + ", ")"),
            ("class C { object x = ", "($$\"\"\"{{\";\"}};)\"\"\" + ", ")"),
            ("class C { object x = ", "(\n/// ;)\n", ")"),
            ("class C { ", "A</*>*/", ">"),
        ], row =>
        {
            string text = row.Item1 + string.Concat(Enumerable.Repeat(row.Item2, 1_200)) + "int" + string.Concat(Enumerable.Repeat(row.Item3, 1_200)) + " x; }";
            SmallStack.Run(() =>
            {
                Assert.NotNull(CSharpParsing.DeepAt(TextSnapshot.From(text), CSharpParsing.Default));
                Assert.Empty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot()).Members);
            });
            string around = row.Item1 + new string('(', 600) + "$\"; {" + new string('(', 600) + "1" + new string(')', 600) + "}\"" + new string(')', 600) + "; }";
            SmallStack.Run(() => Assert.NotNull(CSharpParsing.DeepAt(TextSnapshot.From(around), CSharpParsing.Default)));
        })),
        ("brackets after what only looks like an open comment (a branch left out, a merge's other side, a region's words) are still too deep", () => Cases.Each(
        [
            "#if NEVER\n/*\n#endif\nclass C { int x = ",
            "#if NEVER\n@\"\n#endif\nclass C { int x = ",
            "<<<<<<< ours\n=======\n/*\n>>>>>>> theirs\nclass C { int x = ",
            "#region /*\nclass C { int x = ",
        ], before =>
        {
            string text = Nested(before, "(", "1", ")", "; }", 100_000);
            SmallStack.Run(() => Assert.Empty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot()).Members));
        })),
        ("a file of strings each leaving a bracket open reads whole", () =>
        {
            string text = "class C { void M() {\n" + string.Concat(Enumerable.Repeat("    F(\"(\", '[');\n", 5_000)) + "} }\n";
            Assert.False(DeepText.TooDeep(text), "Brackets in strings added up over a file's statements");
            Assert.NotEmpty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot(TestContext.Current.CancellationToken)).Members);
        }),
        ("a file of statements each with links reads whole", () =>
        {
            string statement = "    x = a?.b?[0] is not null ? F<List<int>>(y => y) : a < b;\n";
            string text = "class C { void M() {\n" + string.Concat(Enumerable.Repeat(statement, 5_000)) + "} }\n";
            Assert.False(DeepText.TooDeep(text, csharp: true), "Links added up over a file's statements");
            Assert.NotEmpty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot(TestContext.Current.CancellationToken)).Members);
        }));

    /// <summary>A parser reads past a <c>?[</c> to the end of what follows, a <c>?[</c> there read past in turn, each level
    /// at least doubling its time: one past the eleventh is too deep, wherever it lies and however it was typed, while real
    /// code's accesses read whole however many.</summary>
    [Fact]
    public void A_run_of_conditional_accesses_goes_too_deep_at_its_twelfth_and_real_codes_read_whole() => Cases.All(
        ("a run of conditional accesses read ahead through goes too deep at its twelfth, whatever its shape", () => Cases.Each(
        [
            ("class C { object x = a", "?[0]"),
            ("class C { object x = a", "?\n[0]"),
            ("class C { object x = a", "?.b?[0]"),
            ("class C { object x = a", "?[0](1).b[2]"),
            ("class C { object x = ", "a?[x => { return "),
            ("class C { object x = ", "a?[new C { P = "),
            ("class C { object x = ", "a?[0] switch { _ => "),
            ("class C { object x = 0", " ?? a?[0]"),
            ("class C { object x = ", "c ? ["),
            ("class C { bool x = a is int", "?[]"),
        ], row =>
        {
            string text = row.Item1 + string.Concat(Enumerable.Repeat(row.Item2, 30));
            int? at = null;
            SmallStack.Run(() => at = CSharpParsing.DeepAt(TextSnapshot.From(text), CSharpParsing.Default));
            Assert.InRange(at ?? -1, row.Item1.Length + (11 * row.Item2.Length), row.Item1.Length + (12 * row.Item2.Length) - 1);
            string sixteen = row.Item1 + string.Concat(Enumerable.Repeat(row.Item2, 16));
            SmallStack.Run(() => Assert.Empty(((CompilationUnitSyntax)CSharpParsing.Parse(sixteen).GetRoot()).Members));
            Assert.False(CSharpParsing.ReadsAsType("int" + string.Concat(Enumerable.Repeat("?[]", 12))), "A type a parser reads past twelve times reads as a type");
        })),
        ("conditional accesses of real code (nullable arrays, chains of eleven, a lambda's statements) read whole however many", () => Cases.Each(
        [
            ("class C {\n", "    public static int?[] M(int?[] a, object?[] b) { return a?[0] is null ? null : a; }\n", "}"),
            ("class C { object x = a?[0] ?? F(y => {\n", "    y = b?[0]?[1]?[2];\n", "}); }"),
            ("class C { void M() {\n", "    x = a?[0]?[1]?[2]?[3]?[4]?[5]?[6]?[7]?[8]?[9]?[10];\n", "} }"),
        ], row =>
        {
            string text = row.Item1 + string.Concat(Enumerable.Repeat(row.Item2, 1_000)) + row.Item3;
            Assert.Null(CSharpParsing.DeepAt(TextSnapshot.From(text), CSharpParsing.Default));
            Assert.NotEmpty(((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot(TestContext.Current.CancellationToken)).Members);
            Assert.True(CSharpParsing.ReadsAsType("int?[]?[]"), "A nullable array of nullable arrays no longer reads as a type");
        })),
        ("typing that makes a run one past the eleventh (a link, a semicolon, quote or comment taken away, a lambda's statement, past a branch left out) is too deep", () => Cases.Each(
        [
            ("class C { void M() { x = a{11}; } }", "; }", "?[0]; }"),
            ("class C { void M() { x = a{6};\n+ a{6}; } }", ";\n+", "\n+"),
            ("class C { void M() { x = \"a{12}\"; } }", "\"a", "a"),
            ("class C { void M() { x = a // {12}\n; } }", "// ", ""),
            ("class C { int x = a{6} ?? F(y => { y = b; }); }", "y = b;", "y = b{6};"),
            ("#if !NEVER\nclass C { void M() { x = a{6}\n#else\n;\n#endif\n+ b; } }\n", "+ b;", "+ b{6};"),
        ], row =>
        {
            SourceText text = SourceText.From(row.Item1.Replace("{6}", Links(6)).Replace("{11}", Links(11)).Replace("{12}", Links(12)));
            SyntaxTree tree = CSharpParsing.Parse(text);
            Assert.Null(CSharpParsing.DeepAt(tree));
            int at = text.ToString().IndexOf(row.Item2, StringComparison.Ordinal);
            SyntaxTree after = CSharpParsing.Changed(tree, text.WithChanges(new TextChange(new TextSpan(at, row.Item2.Length), row.Item3.Replace("{6}", Links(6)))));
            Assert.NotNull(CSharpParsing.DeepAt(after));
            Assert.Empty(((CompilationUnitSyntax)after.GetRoot(TestContext.Current.CancellationToken)).Members);
        })),
        ("typing that keeps every run at eleven or fewer, or changes only words, keeps the tree a parser made", () => Cases.Each(
        [
            ("class C { void M() { x = a{10}; } }", "; }", "?[0]; }"),
            ("class C { int x = a{6} ?? F(y => { y = b; }); }", "y = b;", "y = b?[0]?[0];"),
        ], row =>
        {
            SourceText text = SourceText.From(row.Item1.Replace("{6}", Links(6)).Replace("{10}", Links(10)));
            SyntaxTree tree = CSharpParsing.Parse(text);
            int at = text.ToString().IndexOf(row.Item2, StringComparison.Ordinal);
            SyntaxTree after = CSharpParsing.Changed(tree, text.WithChanges(new TextChange(new TextSpan(at, row.Item2.Length), row.Item3)));
            Assert.Null(CSharpParsing.DeepAt(after));
            Assert.NotEmpty(((CompilationUnitSyntax)after.GetRoot(TestContext.Current.CancellationToken)).Members);
        })));

    /// <summary>A change is read again only where it reaches, a paste or an element typed too deep leaving a tree of
    /// nothing, and measuring a key costs its statement, not the file; a project's newest C# reads as the parser's newest.</summary>
    [Fact]
    public void A_change_reads_again_only_what_it_reached_and_costs_its_statement_not_the_file() => Cases.All(
        ("a change reads again only what changed and a paste too deep leaves a tree of nothing", () =>
        {
            SourceText written = SourceText.From("class C { int x = 1; }\n");
            SyntaxTree tree = CSharpParsing.Parse(written);
            SourceText typed = written.WithChanges(new TextChange(new TextSpan(18, 1), "2"));
            Assert.Contains("= 2", CSharpParsing.Changed(tree, typed).GetRoot(TestContext.Current.CancellationToken).ToFullString(), StringComparison.Ordinal);
            string deep = Nested(string.Empty, "(", "1", ")", string.Empty, 100_000);
            SourceText pasted = written.WithChanges(new TextChange(new TextSpan(18, 1), deep));
            SmallStack.Run(() =>
            {
                SyntaxNode root = CSharpParsing.Changed(tree, pasted).GetRoot();
                Assert.Empty(((CompilationUnitSyntax)root).Members);
                Assert.Equal(pasted.Length, root.FullSpan.Length);
            });
        }),
        ("an element typed into a documentation comment as deep as a parser is given reads as too deep", () =>
        {
            SourceText text = SourceText.From(Nested("/// ", "<a>", string.Empty, string.Empty, "\nclass C { }\n", 1_000));
            SyntaxTree tree = CSharpParsing.Parse(text);
            Assert.Null(CSharpParsing.DeepAt(tree));
            int at = text.ToString().IndexOf("\nclass", StringComparison.Ordinal);
            SyntaxTree after = CSharpParsing.Changed(tree, text.WithChanges(new TextChange(new TextSpan(at, 0), "<a>")));
            Assert.True(CSharpParsing.DeepAt(after) is not null, "An element typed into a documentation comment 1,000 deep left a parser's tree");
        }),
        ("a key typed is measured by its statement, not the file", KeyTypedIsMeasuredByItsStatement),
        ("a mark typed in a name is measured as a word typed", () =>
        {
            SourceText text = SourceText.From("class C\n{\n    void M()\n    {\n        int नमस = F(\"(\", ')');\n    }\n}\n");
            SyntaxTree tree = CSharpParsing.Parse(text);
            int at = text.ToString().IndexOf("नमस", StringComparison.Ordinal) + "नमस".Length;
            SourceText typed = text.WithChanges(new TextChange(new TextSpan(at, 0), "्ते"));
            long read = CSharpParsing.TypedRead;
            Assert.False(CSharpParsing.TypedMayGoDeep(tree, typed, typed.GetChangeRanges(text)));
            Assert.True(CSharpParsing.TypedRead == read, $"A virama typed in a name read {CSharpParsing.TypedRead - read} characters around it, as though it could open or close what follows");
        }),
        ("a union at a LangVersion of the newest C# parses as a union with no syntax error, a number staying it (#1352)", () => Cases.Each(["latest", "default"], languageVersion =>
        {
            SyntaxTree tree = CSharpParsing.Parse("public record Cat;\npublic record Dog;\npublic union Pet(Cat, Dog);\n", CSharpParsing.Defining([], languageVersion));
            Diagnostic[] errors = [.. tree.GetDiagnostics(TestContext.Current.CancellationToken)];
            Assert.True(errors.Length == 0, $"A union at LangVersion {languageVersion} parsed with errors: {string.Join("; ", errors.Select(e => e.ToString()))}");
            Assert.Contains("Pet", tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Select(type => type.Identifier.Text));
            Assert.Equal(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp12, CSharpParsing.Defining([], "12").LanguageVersion);
        })));

    private static void TreeOfNothing(string text) => SmallStack.Run(() =>
    {
        SyntaxNode root = CSharpParsing.Parse(text).GetRoot();
        Assert.Empty(((CompilationUnitSyntax)root).Members);
        Assert.Equal(text.Length, root.FullSpan.Length);
    });

    private static string Links(int count) => string.Concat(Enumerable.Repeat("?[0]", count));

    /// <summary>What measuring a key reads and allocates in a file of 4,000 methods is what it does in one of 40. Roslyn
    /// holds a method body's statements only weakly, so the tree's nodes are held while it is measured, and what is counted
    /// is the measuring's own.</summary>
    private static void KeyTypedIsMeasuredByItsStatement()
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
            SyntaxNode[] held = [.. tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes()];
            int at = text.ToString().IndexOf("?[1]", text.Length / 2, StringComparison.Ordinal);
            SourceText typed = text.WithChanges(new TextChange(new TextSpan(at, 0), "?[2]"));
            IReadOnlyList<TextChangeRange> changes = typed.GetChangeRanges(text);
            Assert.False(CSharpParsing.TypedMayGoDeep(tree, typed, changes));
            (long read, long bytes) = (CSharpParsing.TypedRead, GC.GetAllocatedBytesForCurrentThread());
            Assert.False(CSharpParsing.TypedMayGoDeep(tree, typed, changes));
            (long Read, long Bytes) measured = (CSharpParsing.TypedRead - read, GC.GetAllocatedBytesForCurrentThread() - bytes);
            GC.KeepAlive(held);
            return measured;
        }
        (long smallRead, long small) = Measured(40);
        (long largeRead, long large) = Measured(4_000);
        Assert.True(largeRead == smallRead && smallRead < 1_000, $"Measuring a key read {largeRead} characters of a long file against {smallRead} of a short one, so a key costs what the file holds");
        Assert.True(large <= small + 256, $"Measuring a key allocated {large} bytes in a long file against {small} in a short one, so a key costs what the file holds");
    }

    /// <summary>An expression or a type's name long but shallow, as the debugger reads one per row, is read on the thread that
    /// asks; one nested deeper than any stack is sure to hold goes to a stack sized for it, and one nested past what the
    /// parser reads is still refused.</summary>
    private static void LongShallowExpressionsReadOnTheAskingThread()
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
