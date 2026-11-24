using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>C# formatted as it is typed, where Settings has it: the ';' that ends a statement formats the statement, the
/// '}' that closes a block formats the block, and Enter between a '{' and its '}' lays the '{' out (<see cref="FormatOpened"/>),
/// as the language server lays them out by the file's .editorconfig, Reformat Code's rules; a block a line break opened,
/// its '}' written, formatted as the caret leaves it (<see cref="FormatLeft"/>). And a doc comment as the server writes it:
/// its template once a member's '///' is typed, its next line's '///' after Enter in it (<see cref="DocumentedAsync"/>).
/// Joined to the key's step of Undo, and made only while the text stands as the key left it (<see cref="CodeEditor.FormatTyped"/>).
/// A ';' of a for loop's header, a string's or a comment's is left as typed (<see cref="TypedFormatting"/>), and so is a
/// file shown read-only, a Razor file, and typing at several carets.</summary>
internal sealed partial class CSharpAssist
{
    /// <summary>Whether C# is formatted as it is typed, as Settings leaves it.</summary>
    public bool FormatsOnType { get; set; } = true;

    /// <summary>Whether what a key types in the file shown is formatted once typed, asked before it is typed, at one caret
    /// in a C# file: a ';' or a '}' its language server formats around, a line break where it lays a block out after one,
    /// and, whatever Settings has, a doc comment's: the '/' that makes a line's '///' and a line break in one.</summary>
    public bool FormatsTyped(string typed)
    {
        if (context.Editor.Selections.HasMultiple || Formatting() is not { } lsp)
        {
            return false;
        }
        TextSnapshot text = context.Editor.Document.Snapshot;
        int caret = context.Editor.Selections.Primary.Caret;
        return typed switch
        {
            ";" or "}" => FormatsOnType && lsp.FormatsTyped(typed[0]),
            "\n" => lsp.AutoInserts('\n') && (FormatsOnType || DocumentationLine(text, text.GetLineFromOffset(caret))),
            "/" => lsp.AutoInserts('/') && OnlyBefore(text, caret, "//"),
            _ => false,
        };
    }

    /// <summary>Whether a line holds a doc comment's: its code begins with '///', no fourth.</summary>
    private static bool DocumentationLine(TextSnapshot text, int line)
    {
        string code = text.GetLineText(line).TrimStart();
        return code.StartsWith("///", StringComparison.Ordinal) && !code.StartsWith("////", StringComparison.Ordinal);
    }

    /// <summary>Whether the caret's line holds <paramref name="marks"/> alone before it, after white space, and white space
    /// alone after it.</summary>
    private static bool OnlyBefore(TextSnapshot text, int caret, string marks)
    {
        LineSpan line = text.GetLine(text.GetLineFromOffset(caret));
        return text.GetText(line.Start, caret - line.Start).TrimStart() == marks && text.GetText(caret, line.End - caret).Trim().Length == 0;
    }

    /// <summary>The C# file shown where its language server may format it as it is typed; null for any other.</summary>
    private LspDocument? Formatting() =>
        context.Editors.Active is { IsReadOnly: false } && context.Current is CSharpDocument { Lsp: { IsRazor: false } lsp } ? lsp : null;

    /// <summary>The statement a ';' just typed ends, or the block a '}' closes, formatted as the server lays it out; a doc
    /// comment's '///' written out as the server writes it.</summary>
    private void FormatTyped(string typed)
    {
        if (typed == "/")
        {
            if (Formatting() is { } documented && context.Editors.Active is { } file && !context.Editor.Selections.HasMultiple
                && context.Editor.Selections.Primary is { IsEmpty: true, Caret: int at } && documented.AutoInserts('/') && OnlyBefore(file.Text.Snapshot, at, "///"))
            {
                IdeErrors.Observe(DocumentedAsync(context.Editor, file, documented, typed, at));
            }
            return;
        }
        if (FormatsTyped(typed) && Formatting() is { } lsp && context.Editors.Active is { Syntax: { } syntax } shown
            && context.Editor.Selections.Primary is { IsEmpty: true, Caret: int caret })
        {
            IdeErrors.Observe(FormattedAsync(context.Editor, shown, syntax, lsp, typed, caret, left: false));
        }
    }

    /// <summary>A block the caret left, its closer the '}' at <paramref name="closer"/> the editor wrote as a line break
    /// opened it (<see cref="CodeEditor.LeftBlock"/>), formatted as the server lays it out at that '}' typed, as a step of
    /// Undo of its own; one where the person typed the '}' was formatted at its key.</summary>
    public void FormatLeft(int closer)
    {
        if (FormatsOnType && Formatting() is { } lsp && lsp.FormatsTyped('}') && !context.Editor.Selections.HasMultiple
            && context.Editors.Active is { Syntax: { } syntax } shown && closer < shown.Text.Snapshot.Length && shown.Text.Snapshot[closer] == '}')
        {
            IdeErrors.Observe(FormattedAsync(context.Editor, shown, syntax, lsp, "}", closer + 1, left: true));
        }
    }

    /// <summary>The block Enter opened between a '{' and its '}' (<see cref="CodeEditor.OpenedBlock"/>), its '{' laid out as
    /// the server lays a block out after a line break: where the file's .editorconfig puts it, the caret on the line between
    /// at the block's indentation, joined to Enter's step of Undo.</summary>
    public void FormatOpened()
    {
        if (FormatsOnType && FormatsTyped("\n") && Formatting() is { } lsp && context.Editors.Active is { Syntax: { } syntax } shown
            && context.Editor.Selections.Primary is { IsEmpty: true, Caret: int caret })
        {
            IdeErrors.Observe(OpenedAsync(context.Editor, shown, syntax, lsp, caret));
        }
    }

    /// <summary>Enter that opened no block (<see cref="CodeEditor.LineBroken"/>) after a line of a doc comment: the comment
    /// goes on as the server writes it, joined to Enter's step of Undo.</summary>
    public void FormatLineBreak()
    {
        if (Formatting() is { } lsp && lsp.AutoInserts('\n') && context.Editors.Active is { } shown && !context.Editor.Selections.HasMultiple
            && context.Editor.Selections.Primary is { IsEmpty: true, Caret: int caret } && shown.Text.Snapshot.GetLineFromOffset(caret) is > 0 and int line
            && DocumentationLine(shown.Text.Snapshot, line - 1))
        {
            IdeErrors.Observe(DocumentedAsync(context.Editor, shown, lsp, "\n", caret));
        }
    }

    /// <summary>The server's layout after the line break that opened a block at <paramref name="caret"/>, where the file's
    /// own parse of the text as it stands, landed off the window's thread, says the caret stands between a '{' and its '}'
    /// (<see cref="TypedFormatting.OpensBlock"/>): its one edit, a snippet saying where the caret goes, given to the editor
    /// as the white space it changes (<see cref="TextEditing.Respaced"/>), made only where the text still stands as it was
    /// asked. An answer that writes code is no layout. Held as the window's work until made or let go.</summary>
    private async Task OpenedAsync(CodeEditor editor, OpenDocument shown, CSharpHighlighter syntax, LspDocument lsp, int caret)
    {
        using (BackgroundWork.Scope())
        {
            TextSnapshot asked = shown.Text.Snapshot;
            if (await syntax.ParsedAsync(asked) is not { } parsed || !TypedFormatting.OpensBlock(parsed, caret) || !ReferenceEquals(shown.Text.Snapshot, asked))
            {
                return;
            }
            IndentOptions indent = shown.Indentation.Options;
            AutoInserted? answer = await AskedAsync(shown, asked, () => lsp.AutoInsertAsync(caret, "\n", indent, shown.Conventions));
            if (answer is { TextEdit: { } made, TextEditFormat: InsertTextFormat.Snippet } && ReferenceEquals(shown.Text.Snapshot, asked)
                && Placed(asked, ServerLines.Of(asked), made, InsertTextFormat.Snippet, shown.LineEnding, indent) is ({ } laid, int into)
                && TextEditing.Respaced(asked, laid, caret, into) is { Count: > 0 } edits)
            {
                editor.FormatKeyAt(asked, edits, laid.Start + into);
            }
        }
    }

    /// <summary>What the server writes on its own after <paramref name="typed"/> in a doc comment, standing before
    /// <paramref name="caret"/>: a member's template after its '///', the next line's '///' after a line break. Given to
    /// the editor with the caret where its snippet ends, made only where the text still stands as it was asked, joined to
    /// the key's step of Undo. Held as the window's work until made or let go.</summary>
    private async Task DocumentedAsync(CodeEditor editor, OpenDocument shown, LspDocument lsp, string typed, int caret)
    {
        using (BackgroundWork.Scope())
        {
            TextSnapshot asked = shown.Text.Snapshot;
            IndentOptions indent = shown.Indentation.Options;
            AutoInserted? answer = await AskedAsync(shown, asked, () => lsp.AutoInsertAsync(caret, typed, indent, shown.Conventions), "Writing the doc comment");
            if (answer is { TextEdit: { } made } && ReferenceEquals(shown.Text.Snapshot, asked)
                && Placed(asked, ServerLines.Of(asked), made, answer.TextEditFormat, shown.LineEnding, indent) is ({ } written, int into))
            {
                editor.FormatKeyAt(asked, [written], written.Start + into);
            }
        }
    }

    /// <summary>The server's formatting around <paramref name="typed"/>, standing before <paramref name="caret"/>, where the
    /// file's own parse of the text as it stands, landed off the window's thread, says the code ends something with it
    /// (<see cref="TypedFormatting"/>): given to the editor, which makes it only where the text still stands as it was
    /// asked, joined to the key that typed it or, for a block <paramref name="left"/>, a step of its own; the character
    /// itself is never changed, so an answer reaching it is no formatting. Held as the window's work until made or let go.</summary>
    private async Task FormattedAsync(CodeEditor editor, OpenDocument shown, CSharpHighlighter syntax, LspDocument lsp, string typed, int caret, bool left)
    {
        using (BackgroundWork.Scope())
        {
            TextSnapshot asked = shown.Text.Snapshot;
            if (await syntax.ParsedAsync(asked) is not { } parsed || !TypedFormatting.Formats(parsed, caret, typed[0]) || !ReferenceEquals(shown.Text.Snapshot, asked))
            {
                return;
            }
            IndentOptions indent = shown.Indentation.Options;
            IReadOnlyList<LspTextEdit>? answer = await AskedAsync(shown, asked, () => lsp.FormatTypedAsync(caret, typed, indent, shown.Conventions));
            if (answer is not { Count: > 0 } || !ReferenceEquals(shown.Text.Snapshot, asked))
            {
                return;
            }
            List<TextEdit> laid = WorkspaceEdits.Convert(asked, answer, shown.LineEnding, indent);
            if (laid.Count > 0 && laid[^1].End < caret && left)
            {
                editor.FormatLeftBlock(asked, laid);
            }
            else if (laid.Count > 0 && laid[^1].End < caret)
            {
                editor.FormatTyped(asked, laid);
            }
        }
    }

    /// <summary>The server's answer to a layout asked of the text <paramref name="asked"/>; default where it failed, said as
    /// <paramref name="what"/>'s unless the text has changed since, which makes the answer moot whatever the server said of it.</summary>
    private async Task<T?> AskedAsync<T>(OpenDocument shown, TextSnapshot asked, Func<Task<T>> ask, string what = "Formatting as you type")
    {
        try
        {
            return await ask();
        }
        catch (LanguageServerFailed failed)
        {
            if (!failed.Outdated && ReferenceEquals(shown.Text.Snapshot, asked))
            {
                context.Failed(what, failed);
            }
            return default;
        }
    }
}
