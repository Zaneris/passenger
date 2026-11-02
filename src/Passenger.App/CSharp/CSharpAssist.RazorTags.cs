using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>A Razor file's tags closed as they are typed, as Razor reads its markup: a start tag's '>' takes what Razor
/// writes after it (the element's end tag, a void element or a component that takes no content closed in itself), and
/// "&lt;/" the name of the element open there, read from the file's HTML as Razor makes it, its C# blanked out, so a
/// generic's '&lt;' in the code is never taken for a tag. Each is a step of Undo of its own, made only on the text and
/// carets it was worked out for.</summary>
internal sealed partial class CSharpAssist
{
    /// <summary>Closes the tag a '>' or a '/' just typed at every caret ends or begins, unless the person turned
    /// closing tags off.</summary>
    private void CloseRazorTag(string typed)
    {
        if (typed is ">" or "/" && context.Editor.ClosesTags && context.Editors.Active is { } shown && context.Current?.Lsp is { IsRazor: true } lsp)
        {
            IdeErrors.Observe(ClosedAsync(typed == ">" ? EndTagAsync(shown, lsp) : EndTagNameAsync(lsp)));
        }
    }

    /// <summary>A tag's closing, held as the window's work until it is made or let go.</summary>
    private static async Task ClosedAsync(Task closing)
    {
        using (BackgroundWork.Scope())
        {
            await closing;
        }
    }

    /// <summary>Razor's own answer to a start tag's '>' at each caret, made once each has come if the text and carets
    /// are as they were.</summary>
    private async Task EndTagAsync(OpenDocument shown, LspDocument lsp)
    {
        CodeEditor editor = context.Editor;
        SelectionSet asked = editor.Selections;
        int version = lsp.Text.Version;
        if (asked.Items.Any(s => !s.IsEmpty))
        {
            return;
        }
        IndentOptions indent = shown.Indentation.Options;
        AutoInserted?[] answers = await Task.WhenAll(asked.Items.Select(s => lsp.AutoInsertAsync(s.Caret, ">", indent, shown.Conventions)));
        if (!Unmoved(editor, lsp, version, asked) || answers.All(a => a is null))
        {
            return;
        }
        TextSnapshot text = lsp.Text.Snapshot;
        List<TextEdit> edits = [];
        List<Selection> carets = [];
        int shift = 0;
        for (int i = 0; i < asked.Count; i++)
        {
            int caret = asked.Items[i].Caret;
            if (answers[i] is not { TextEdit: { } made } answer || Placed(text, lsp, made, answer.TextEditFormat, shown.LineEnding, indent, caret) is not ({ } edit, int into)
                || edit.Start < (edits.Count > 0 ? edits[^1].End : 0) || edit.End > caret)
            {
                carets.Add(Selection.At(caret + shift));
                continue;
            }
            edits.Add(edit);
            // A snippet's caret is its own place in what it writes; a plain edit before the caret carries it along.
            int moved = answer.TextEditFormat == InsertTextFormat.Snippet ? edit.Start + into
                : edit.End < caret || edit.Length > 0 ? caret + edit.Text.Length - edit.Length : caret;
            carets.Add(Selection.At(shift + moved));
            shift += edit.Text.Length - edit.Length;
        }
        if (edits.Count > 0)
        {
            editor.ApplyEdits(edits, SelectionSet.Create(carets, asked.PrimaryIndex));
        }
    }

    /// <summary>A Razor answer's edit in the editor's offsets, its line breaks and indentation the file's, and where in
    /// its text the caret goes: a snippet's end, else its end; null where it comes to nothing.</summary>
    private static (TextEdit? Edit, int Into) Placed(TextSnapshot text, LspDocument lsp, LspTextEdit made, InsertTextFormat format, string eol, IndentOptions indent, int caret)
    {
        int start = lsp.OffsetOf(made.Range.Start);
        int end = lsp.OffsetOf(made.Range.End);
        Snippet? snippet = format == InsertTextFormat.Snippet ? Snippet.FromProtocol(made.NewText) : null;
        string written = snippet?.Text ?? made.NewText;
        if (end < start || end > caret || LineEndings.Fit(text, [new TextEdit(start, end - start, written)], eol) is not [TextEdit broken])
        {
            return (null, 0);
        }
        int into = LineEndings.In(written[..(snippet?.End.Start ?? written.Length)], eol).Length + (start - broken.Start);
        return (Indentation.Fit(text, broken, indent, ref into), into);
    }

    /// <summary>The name of the element open at each "&lt;/" just typed, as HTML's typing closes it
    /// (<see cref="TextEditing.CloseTag"/>), over the file's HTML as Razor makes it of the text now.</summary>
    private async Task EndTagNameAsync(LspDocument lsp)
    {
        CodeEditor editor = context.Editor;
        SelectionSet asked = editor.Selections;
        int version = lsp.Text.Version;
        TextSnapshot text = lsp.Text.Snapshot;
        if (!asked.Items.Any(s => s.IsEmpty && s.Caret >= 2 && text[s.Caret - 2] == '<' && text[s.Caret - 1] == '/'))
        {
            return;
        }
        string? html = await lsp.RazorHtmlAsync();
        // Razor's HTML keeps every place of the file's: one of another length was made of another text.
        if (!Unmoved(editor, lsp, version, asked) || html is null || html.Length != text.Length)
        {
            return;
        }
        TextSnapshot markup = TextSnapshot.From(html);
        if (TextEditing.CloseTag(markup, asked, "/", MarkupKind.Html) is { } plan
            && plan.Edits.All(e => text.GetText(e.Start, e.Length) == markup.GetText(e.Start, e.Length) && text.GetText(e.Start - 2, 2) == "</"))
        {
            editor.ApplyEdits(plan.Edits, plan.Selections);
        }
    }

    /// <summary>Whether the editor shows the text and carets an answer was asked for.</summary>
    private bool Unmoved(CodeEditor editor, LspDocument lsp, int version, SelectionSet asked) =>
        context.Editor == editor && lsp.Text.Version == version && editor.Selections.SameAs(asked) && context.Current?.Lsp == lsp;
}
