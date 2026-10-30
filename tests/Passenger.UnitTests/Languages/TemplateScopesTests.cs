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

    [Theory]
    [InlineData(Derived + "|\n}\n", TemplateScope.Member)]
    [InlineData(Derived + "pro|\n}\n", TemplateScope.Member)]
    [InlineData(Derived + "[Obsolete]\n    pro|\n}\n", TemplateScope.Member)]
    [InlineData(Derived + "override |\n}\n", TemplateScope.None)]
    [InlineData(Derived + "override ct|\n}\n", TemplateScope.None)]
    [InlineData(Derived + "public static pro|\n}\n", TemplateScope.None)]
    [InlineData(Derived + "override |\n    public int Count => 1;\n}\n", TemplateScope.None)]
    [InlineData("namespace Shapes;\n\ncla|\n", TemplateScope.Type)]
    [InlineData("namespace Shapes;\n\npublic cla|\n", TemplateScope.None)]
    [InlineData("namespace Shapes;\n\npublic |\nclass Circle\n{\n}\n", TemplateScope.None)]
    [InlineData(Derived + "public int Count => fo|;\n}\n", TemplateScope.None)]
    public void A_template_goes_only_where_its_word_starts_the_declaration(string marked, TemplateScope scope)
    {
        (ParsedText syntax, int caret) = Parse(marked);
        TemplateScope found = TemplateScopes.At(syntax, caret).Scope;
        Assert.True(found == scope, $"At the caret of {marked.Replace("\n", " ", StringComparison.Ordinal)} templates of {found} are offered, not {scope}");
    }

    [Theory]
    [InlineData(Derived + "override |\n}\n", true)]
    [InlineData(Derived + "public override |\n}\n", true)]
    [InlineData(Derived + "override |\n    public int Count => 1;\n}\n", true)]
    [InlineData(Derived + "override  |\n}\n", false)]
    [InlineData(Derived + "public |\n}\n", false)]
    [InlineData(Derived + "void M()\n    {\n        string override_ = \"override |\";\n    }\n}\n", false)]
    [InlineData(Derived + "// override |\n}\n", false)]
    public void A_space_after_a_members_override_opens_completion(string marked, bool opens)
    {
        (ParsedText syntax, int caret) = Parse(marked);
        Assert.True(CompletionTriggers.AfterOverride(syntax, caret) == opens,
            $"A space typed before the caret of {marked.Replace("\n", " ", StringComparison.Ordinal)} {(opens ? "opens no" : "opens")} completion");
    }

    private static (ParsedText Syntax, int Caret) Parse(string marked)
    {
        int caret = marked.IndexOf('|', StringComparison.Ordinal);
        string text = marked.Remove(caret, 1);
        return (new ParsedText(CSharpSyntaxTree.ParseText(text, cancellationToken: TestContext.Current.CancellationToken), TextSnapshot.From(text)), caret);
    }
}
