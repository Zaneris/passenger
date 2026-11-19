using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>C# formatted as it is typed, where Settings has it: the ';' that ends a statement formats the statement, and
/// the '}' that closes a block formats the block, as the language server lays them out by the file's .editorconfig, Reformat
/// Code's rules. Joined to the key's step of Undo, and made only while the text stands as the key left it
/// (<see cref="CodeEditor.FormatTyped"/>); a block a line break opened, its '}' written, formatted as the caret leaves it
/// (<see cref="FormatLeft"/>). A ';' of a for loop's header, a string's or a comment's is left as typed
/// (<see cref="TypedFormatting"/>), and so is a file shown read-only, a Razor file, and typing at several carets.</summary>
internal sealed partial class CSharpAssist
{
    /// <summary>Whether C# is formatted as it is typed, as Settings leaves it.</summary>
    public bool FormatsOnType { get; set; } = true;

    /// <summary>Whether what a key types in the file shown is formatted once typed, asked before it is typed: a ';' or a
    /// '}' at one caret in a C# file its language server formats around.</summary>
    public bool FormatsTyped(string typed) =>
        FormatsOnType && typed is ";" or "}" && !context.Editor.Selections.HasMultiple && Formatting() is { } lsp && lsp.FormatsTyped(typed[0]);

    /// <summary>The C# file shown where its language server may format it as it is typed; null for any other.</summary>
    private LspDocument? Formatting() =>
        context.Editors.Active is { IsReadOnly: false } && context.Current is CSharpDocument { Lsp: { IsRazor: false } lsp } ? lsp : null;

    /// <summary>The statement a ';' just typed ends, or the block a '}' closes, formatted as the server lays it out.</summary>
    private void FormatTyped(string typed)
    {
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
            IReadOnlyList<LspTextEdit> answer;
            try
            {
                answer = await lsp.FormatTypedAsync(caret, typed, indent, shown.Conventions);
            }
            catch (LanguageServerFailed failed)
            {
                // A text changed since makes the answer moot, whatever the server said of it.
                if (!failed.Outdated && ReferenceEquals(shown.Text.Snapshot, asked))
                {
                    context.Failed("Formatting as you type", failed);
                }
                return;
            }
            if (answer.Count == 0 || !ReferenceEquals(shown.Text.Snapshot, asked))
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
}
