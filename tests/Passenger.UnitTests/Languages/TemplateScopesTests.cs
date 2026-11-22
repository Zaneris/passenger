using Microsoft.CodeAnalysis.CSharp;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.UnitTests.Languages;

/// <summary>Where a live template may go, read at the caret ('|') of a file's text: a member's or a type's place only
/// where its word starts the declaration, never after a modifier the template would write again; and where a space opens
/// completion of the members to override.</summary>
public sealed class TemplateScopesTests
{
    private const string Derived = "class Base\n{\n    public virtual int Size() => 0;\n}\n\nclass Derived : Base\n{\n    ";

    [Fact]
    public void A_template_goes_only_where_its_word_starts_the_declaration_and_a_space_after_override_opens_completion() => Cases.All(
        ("a template goes only where its word starts the declaration", () => Cases.Each(
        [
            (Derived + "|\n}\n", TemplateScope.Member),
            (Derived + "[Obsolete]\n    pro|\n}\n", TemplateScope.Member),
            (Derived + "override ct|\n}\n", TemplateScope.None),
            (Derived + "public static pro|\n}\n", TemplateScope.None),
            ("namespace Shapes;\n\ncla|\n", TemplateScope.Type),
            ("namespace Shapes;\n\npublic |\nclass Circle\n{\n}\n", TemplateScope.None),
            (Derived + "public int Count => fo|;\n}\n", TemplateScope.None),
        ], row =>
        {
            (ParsedText syntax, int caret) = Parse(row.Item1);
            TemplateScope found = TemplateScopes.At(syntax, caret).Scope;
            Assert.True(found == row.Item2, $"At the caret of {row.Item1.Replace("\n", " ", StringComparison.Ordinal)} templates of {found} are offered, not {row.Item2}");
        })),
        ("a space after a member's override opens completion", () => Cases.Each(
        [
            (Derived + "public override |\n}\n", true),
            (Derived + "override |\n    public int Count => 1;\n}\n", true),
            (Derived + "override  |\n}\n", false),
            (Derived + "public |\n}\n", false),
            (Derived + "void M()\n    {\n        string override_ = \"override |\";\n    }\n}\n", false),
            (Derived + "// override |\n}\n", false),
        ], row =>
        {
            (ParsedText syntax, int caret) = Parse(row.Item1);
            Assert.True(CompletionTriggers.AfterOverride(syntax, caret) == row.Item2,
                $"A space typed before the caret of {row.Item1.Replace("\n", " ", StringComparison.Ordinal)} {(row.Item2 ? "opens no" : "opens")} completion");
        })));

    private static (ParsedText Syntax, int Caret) Parse(string marked)
    {
        int caret = marked.IndexOf('|', StringComparison.Ordinal);
        string text = marked.Remove(caret, 1);
        return (new ParsedText(CSharpSyntaxTree.ParseText(text, cancellationToken: TestContext.Current.CancellationToken), TextSnapshot.From(text)), caret);
    }
}
