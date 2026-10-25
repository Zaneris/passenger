using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Features;
using Passenger.Xaml.Syntax;

namespace Passenger.App.CSharp;

/// <summary>What completion's templates and the file's own syntax do with no language server to ask: Surround With, C#'s
/// and XAML's, and Complete context.Current Statement.</summary>
internal sealed partial class CodeCompletion
{
    /// <summary>Surround With: the surround templates that fit the code chosen (or the caret's line), each with its
    /// digit; the one chosen goes around that code and is filled in as a live template, from its first variable to
    /// where it leaves the caret.</summary>
    public void ShowSurround()
    {
        if (context.Editors.Active is { } markup && XamlService.IsXaml(markup.Path))
        {
            ShowXamlSurround(markup);
            return;
        }
        if (context.Editors.Active is not { Syntax: { } parser } document)
        {
            return;
        }
        Close();
        SurroundTarget target = SurroundScopes.Of(parser.Current, Editor.Selections.Primary.Range);
        IReadOnlyList<LiveTemplate> offered = Templates.Around(target.Scope);
        if (offered.Count == 0)
        {
            context.Say("Nothing here to surround: choose code, not a comment or a string");
            return;
        }
        ChangeTracker opened = new(document.Text);
        // At the caret, below the code chosen when the caret ends it.
        popups.ShowModal(new ChoicePopup("Surround With", [.. offered.Select(t => new Choice(t.Abbreviation, t.Description, t, Mnemonic: t.Mnemonic))]),
            context.Caret, "Surround With", choice =>
            {
                IReadOnlyList<TextEdit>? still = opened.Map([new TextEdit(target.Range.Start, target.Range.Length, string.Empty)]);
                opened.Dispose();
                if (choice?.Value is not LiveTemplate template)
                {
                    return Task.CompletedTask;
                }
                if (still is not [TextEdit wrapped] || Editor.Document != document.Text)
                {
                    context.Say("Surround With was not applied: the code it wraps was edited meanwhile");
                    return Task.CompletedTask;
                }
                Surround(template, new TextRange(wrapped.Start, wrapped.Length));
                return Task.CompletedTask;
            });
    }

    /// <summary>XAML's Surround With: the templates that fit the elements chosen (the element at the caret with nothing
    /// chosen), each with its digit; the one chosen goes around them, filled in from its first variable.</summary>
    private void ShowXamlSurround(OpenDocument document)
    {
        Close();
        XmlTree tree = XmlTree.Parse(document.Text.Snapshot.ToString());
        if (XamlRefactorings.SurroundTarget(tree, Editor.Selections.Primary.Range) is not { } target)
        {
            context.Say("Nothing here to surround: choose elements, not text, a comment or the root");
            return;
        }
        bool avalonia = tree.Root?.Attribute("xmlns")?.Value == AvaloniaDialect.Uri;
        IReadOnlyList<LiveTemplate> offered = [.. LiveTemplates.Xaml.Where(t => (target.Count == 1 || !t.OneChild) && (avalonia || !t.AvaloniaOnly)).Select(t => t.Template)];
        ChangeTracker opened = new(document.Text);
        popups.ShowModal(new ChoicePopup("Surround With", [.. offered.Select(t => new Choice(t.Abbreviation, t.Description, t, Mnemonic: t.Mnemonic))]),
            context.Caret, "Surround With", choice =>
            {
                IReadOnlyList<TextEdit>? still = opened.Map([new TextEdit(target.Range.Start, target.Range.Length, string.Empty)]);
                opened.Dispose();
                if (choice?.Value is not LiveTemplate template)
                {
                    return Task.CompletedTask;
                }
                if (still is not [TextEdit wrapped] || Editor.Document != document.Text)
                {
                    context.Say("Surround With was not applied: the code it wraps was edited meanwhile");
                    return Task.CompletedTask;
                }
                Surround(template, new TextRange(wrapped.Start, wrapped.Length));
                return Task.CompletedTask;
            });
    }

    /// <summary>A surround template over code: the code's lines after its first written relative to its first line's
    /// indentation, which the template's lines take.</summary>
    private void Surround(LiveTemplate template, TextRange range)
    {
        TextSnapshot text = Editor.Document.Snapshot;
        int lineStart = text.GetLineStart(text.GetLineFromOffset(range.Start));
        int indented = lineStart;
        while (indented < range.Start && text[indented] is ' ' or '\t')
        {
            indented++;
        }
        string indent = text.GetText(lineStart, indented - lineStart);
        string[] lines = text.GetText(range).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            int strip = 0;
            while (strip < indent.Length && strip < lines[i].Length && lines[i][strip] is ' ' or '\t')
            {
                strip++;
            }
            lines[i] = lines[i][strip..];
        }
        Editor.InsertSnippet(range, Snippet.Parse(template.Text, Values(template, range.Start), indent, Editor.Indent.Unit, string.Join('\n', lines)));
    }

    /// <summary>Complete context.Current Statement on the caret's line (<see cref="StatementCompletion"/>), as one undo
    /// step.</summary>
    public void CompleteStatement()
    {
        if (context.Editors.Active?.Syntax is not { } parser)
        {
            return;
        }
        Close();
        if (StatementCompletion.At(parser.Current, context.Caret, Editor.Indent, Editor.Document.LineEnding) is not { } completed)
        {
            context.Say("Nothing to complete on this line");
            return;
        }
        if (completed.Edits.Count == 0)
        {
            Editor.Selections = SelectionSet.Single(completed.Caret);
            return;
        }
        Editor.ApplyEdits(completed.Edits, SelectionSet.Single(completed.Caret));
    }
}
