using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>A Razor file's tags closed as they are typed, as Razor reads its markup: a start tag's '>' takes what Razor
/// writes after it (the element's end tag, a void element or a component that takes no content closed in itself), and
/// "&lt;/" the name of the element open there, read from the file's HTML as Razor makes it, its C# blanked out, so a
/// generic's '&lt;' in the code is never taken for a tag. Each is a step of Undo of its own, made on the text and carets
/// it was worked out for; an end tag Razor gives after more was typed, after what was typed, where it still fits.</summary>
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
    /// are as they were; typed on since, mapped through what was typed at each caret (<see cref="Late"/>).</summary>
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
        TextSnapshot before = lsp.Text.Snapshot;
        // Where each '>' was typed, carried through what is typed while Razor answers: typing there goes after it.
        Anchor[] places = [.. asked.Items.Select(s => lsp.Text.AnchorAt(s.Caret, AnchorGravity.Before))];
        try
        {
            AutoInserted?[] answers = await Task.WhenAll(asked.Items.Select(s => lsp.AutoInsertAsync(s.Caret, ">", indent, shown.Conventions)));
            if (answers.All(a => a is null))
            {
                return;
            }
            if (Unmoved(editor, lsp, version, asked))
            {
                Apply(editor, lsp, shown, indent, asked, answers);
            }
            else
            {
                Late(editor, lsp, shown, indent, before, asked, answers, places);
            }
        }
        finally
        {
            foreach (Anchor place in places)
            {
                place.Remove();
            }
        }
    }

    /// <summary>Razor's answers at each caret, on the text and carets they were asked for.</summary>
    private static void Apply(CodeEditor editor, LspDocument lsp, OpenDocument shown, IndentOptions indent, SelectionSet asked, AutoInserted?[] answers)
    {
        TextSnapshot text = lsp.Text.Snapshot;
        ServerLines lines = ServerLines.Of(text);
        List<TextEdit> edits = [];
        List<Selection> carets = [];
        int shift = 0;
        for (int i = 0; i < asked.Count; i++)
        {
            int caret = asked.Items[i].Caret;
            if (answers[i] is not { TextEdit: { } made } answer || Placed(text, lines, made, answer.TextEditFormat, shown.LineEnding, indent, caret) is not ({ } edit, int into)
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

    /// <summary>Razor's end tags for the '>'s it answered after more was typed: each written at its caret, after what was
    /// typed there since, where it still fits (<see cref="FitsLate"/>), the carets as many as were asked, each after its own
    /// '>''s place (carried through the typing, <paramref name="places"/>). One step of Undo for every one written.</summary>
    private void Late(CodeEditor editor, LspDocument lsp, OpenDocument shown, IndentOptions indent, TextSnapshot before, SelectionSet asked, AutoInserted?[] answers,
        Anchor[] places)
    {
        if (context.Editor != editor || context.Current?.Lsp != lsp || editor.Selections is not { } now || now.Count != asked.Count || now.Items.Any(s => !s.IsEmpty))
        {
            return;
        }
        TextSnapshot text = lsp.Text.Snapshot;
        ServerLines lines = ServerLines.Of(before);
        string written = before.ToString();
        List<TextEdit> edits = [];
        List<Selection> carets = [];
        int shift = 0;
        int after = 0;
        for (int i = 0; i < now.Count; i++)
        {
            int caret = now.Items[i].Caret;
            if (answers[i] is { } answer && places[i].Offset >= after
                && FitsLate(before, written, lines, text, answer, shown.LineEnding, indent, asked.Items[i].Caret, places[i].Offset, caret) is ({ } endTag, int into))
            {
                edits.Add(new TextEdit(caret, 0, endTag));
                carets.Add(Selection.At(caret + shift + into));
                shift += endTag.Length;
            }
            else
            {
                carets.Add(Selection.At(caret + shift));
            }
            after = caret;
        }
        if (edits.Count > 0)
        {
            editor.ApplyEdits(edits, SelectionSet.Create(carets, now.PrimaryIndex));
        }
    }

    /// <summary>The end tag a caret takes from Razor's answer to the '>' it typed at <paramref name="asked"/>, and where in it
    /// the caret goes, where it still fits: the start tag it closes still written just before that place now (<paramref name="at"/>),
    /// the caret after it with no tag begun or ended between (no '&lt;' or '>' typed), and the end tag not already after the
    /// caret; null where it does not.</summary>
    private static (string EndTag, int Into)? FitsLate(TextSnapshot before, string written, ServerLines lines, TextSnapshot text, AutoInserted answer, string eol,
        IndentOptions indent, int asked, int at, int caret)
    {
        if (answer is not { TextEdit: { } made } || Placed(before, lines, made, answer.TextEditFormat, eol, indent, asked) is not ({ } edit, int into)
            || edit.Length != 0 || edit.Start != asked || asked < 2 || before[asked - 1] != '>')
        {
            return null;
        }
        int open = written.LastIndexOf('<', asked - 1);
        if (open < 0)
        {
            return null;
        }
        string tag = before.GetText(open, asked - open);
        if (at < tag.Length || caret < at || text.GetText(at - tag.Length, tag.Length) != tag || text.GetText(at, caret - at).IndexOfAny(['<', '>']) >= 0
            || caret + edit.Text.Length <= text.Length && text.GetText(caret, edit.Text.Length) == edit.Text)
        {
            return null;
        }
        return (edit.Text, answer.TextEditFormat == InsertTextFormat.Snippet ? into : 0);
    }

    /// <summary>A Razor answer's edit in the editor's offsets of the text it answered (its server's lines,
    /// <paramref name="lines"/>), its line breaks and indentation the file's, and where in its text the caret goes: a
    /// snippet's end, else its end; null where it comes to nothing.</summary>
    private static (TextEdit? Edit, int Into) Placed(TextSnapshot text, ServerLines lines, LspTextEdit made, InsertTextFormat format, string eol, IndentOptions indent, int caret)
    {
        int start = lines.OffsetOf(made.Range.Start);
        int end = lines.OffsetOf(made.Range.End);
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
