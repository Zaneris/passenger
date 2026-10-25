using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>Live and postfix templates: a shortcut and Tab, or one chosen from completion, becomes its template, its
/// variables worked out by their macros; a postfix template that could wrap more than one expression asks
/// which.</summary>
internal sealed partial class CodeCompletion
{
    /// <summary>The templates in force, as Settings leaves them.</summary>
    public TemplateSet Templates { get; set; } = TemplateSet.Default;

    /// <summary>An abbreviation and Tab: the word before the caret, when a live template has it as its abbreviation and
    /// may go here, becomes the template. Whether one did.</summary>
    private bool TryExpandTemplate()
    {
        if (context.Current is null || context.WebCurrent is not null || Editor.Selections.HasMultiple || !Editor.Selections.Primary.IsEmpty)
        {
            return false;
        }
        int caret = Editor.Selections.Primary.Caret;
        int start = context.WordStart(caret);
        if (start == caret)
        {
            return false;
        }
        string word = Editor.Document.Snapshot.GetText(start, caret - start);
        if (Postfixes(start, caret).FirstOrDefault(p => p.Template.Name == word) is { Template: { } postfix } found)
        {
            ExpandPostfix(postfix, found.Target, caret);
            return true;
        }
        TemplateScope scope = context.Syntax is { } syntax ? TemplateScopes.At(syntax, caret).Scope : TemplateScope.None;
        if (Templates.In(scope).FirstOrDefault(t => t.Abbreviation == word) is not { } template)
        {
            return false;
        }
        Expand(template, start, caret);
        return true;
    }

    /// <summary>The postfix templates turned on that may wrap the expression before a dot ending just before a word,
    /// and what each would wrap; none when no dot or no expression is there.</summary>
    private IEnumerable<(PostfixTemplate Template, PostfixTarget Target)> Postfixes(int word, int caret)
    {
        if (word < 1 || context.Syntax is not { } syntax || word > syntax.Text.Length || syntax.Text[word - 1] != '.'
            || PostfixTemplates.Before(syntax, word - 1, caret) is not { } target)
        {
            return [];
        }
        return Templates.Postfix.Where(t => !t.Statement || target.WholeStatement).Select(t => (t, target));
    }

    /// <summary>A postfix template in place of its expression, the dot and the name typed after it. An expression's
    /// template with more than one expression ending at the dot asks which first, the innermost chosen to start with
    /// and the one chosen highlighted in the editor as the list moves.</summary>
    private void ExpandPostfix(PostfixTemplate template, PostfixTarget target, int caret)
    {
        if (template.Statement || target.Expressions.Count == 1)
        {
            Wrap(template, template.Statement ? target.Outermost : target.Expressions[0], caret);
            return;
        }
        CodeEditor editor = Editor;
        TextDocument document = editor.Document;
        int version = document.Version;
        IReadOnlyList<TextRange> found = editor.Highlights;
        int current = editor.CurrentHighlight;
        void Show(PostfixExpression shown) => editor.SetHighlights([new TextRange(shown.Start, shown.Text.Length)], -1);
        ChoicePopup popup = new(template.SelectTitle, [.. target.Expressions.Select(e => new Choice(Shown(e.Text), string.Empty, e))]);
        popup.Moved += (_, choice) =>
        {
            if (choice?.Value is PostfixExpression moved)
            {
                Show(moved);
            }
        };
        popups.ShowModal(popup, caret, template.Name, choice =>
        {
            if (choice?.Value is not PostfixExpression chosen)
            {
                return Task.CompletedTask;
            }
            if (Editor != editor || editor.Document != document || document.Version != version)
            {
                context.Say($".{template.Name} was not applied: the code it wraps was edited meanwhile");
                return Task.CompletedTask;
            }
            Wrap(template, chosen, caret);
            return Task.CompletedTask;
        });
        popups.WhenClosed(() => editor.SetHighlights(found, current));
        Show(target.Expressions[0]);
    }

    /// <summary>An expression as the list shows it: on one line, its spaces run together, and past 50 characters cut
    /// short with an ellipsis.</summary>
    private static string Shown(string expression)
    {
        string line = Regex.Replace(expression.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim(), " {2,}", " ");
        return line.Length <= 50 ? line : line[..50] + "…";
    }

    /// <summary>A postfix template around one expression, in place of it, the dot and the name typed after it.</summary>
    private void Wrap(PostfixTemplate template, PostfixExpression expression, int caret)
    {
        TextSnapshot text = Editor.Document.Snapshot;
        int start = expression.Start;
        int lineStart = text.GetLineStart(text.GetLineFromOffset(start));
        string before = text.GetText(lineStart, start - lineStart);
        string indent = before[..(before.Length - before.TrimStart().Length)];
        SnippetVariable[] variables = [.. template.Defaults.Select(d => new SnippetVariable(d.Key, d.Value == "$name" ? expression.Name : d.Value))];
        string written = template.Text.Replace("$EXPR$", expression.Text.Replace("$", "$$", StringComparison.Ordinal), StringComparison.Ordinal);
        Editor.InsertSnippet(TextRange.FromBounds(start, caret), Snippet.Parse(written, variables, indent, Editor.Indent.Unit));
    }

    /// <summary>Puts a live template over its shortcut and starts filling it in: reformatted, its lines indented as the
    /// line it is on and its tabs the file's indentation, or as it is written when it asks not to be.</summary>
    private void Expand(LiveTemplate template, int start, int end)
    {
        TextSnapshot text = Editor.Document.Snapshot;
        string line = text.GetText(text.GetLineStart(text.GetLineFromOffset(start)), start - text.GetLineStart(text.GetLineFromOffset(start)));
        (string indent, string unit) = template.Reformat ? (line[..(line.Length - line.TrimStart().Length)], Editor.Indent.Unit) : (string.Empty, "\t");
        Editor.InsertSnippet(TextRange.FromBounds(start, end), Snippet.Parse(template.Text, Values(template, end), indent, unit));
    }

    /// <summary>A template's variables as they go in at an offset, in its order: each its macro's value there (empty with
    /// none), a stop for Tab while it is editable.</summary>
    private SnippetVariable[] Values(LiveTemplate template, int at)
    {
        TextSnapshot text = Editor.Document.Snapshot;
        string? type = null;
        string? space = null;
        if (context.Syntax is { } syntax)
        {
            type = TemplateScopes.At(syntax, at).TypeName;
            SyntaxNode? node = syntax.Root.FindToken(Math.Max(0, Math.Min(at, syntax.Root.FullSpan.End) - 1)).Parent;
            string[] names = [.. (node?.AncestorsAndSelf() ?? []).OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString())];
            space = names.Length > 0 ? string.Join('.', names) : null;
        }
        TemplateSite site = new(type, space, context.Editors.Active?.Path, text.GetLineFromOffset(Math.Min(at, text.Length)) + 1);
        return [.. template.VariablesInText().Select(variable =>
            new SnippetVariable(variable.Name, LiveTemplates.Macros.FirstOrDefault(m => m.Name == variable.Macro)?.Value(site, variable.Parameter) ?? string.Empty,
                variable.Editable))];
    }
}
