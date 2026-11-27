using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>What the caret or selection lights up: the language server's uses of the symbol in the file (writes apart
/// from reads), drawn in the editor and marked on its stripe. Where the language lights nothing, a whole word chosen in
/// a comment, string or plain text lights its other whole-word places (<see cref="TextSearch.PlacesOfWord"/>), found off
/// the window's thread. C# identifiers require a symbol even when the language server is unavailable.
/// Highlights go when the caret or selection leaves them or the text changes, so none is ever left
/// on the wrong symbol, and come again once it rests. Highlight Usages in File asks at once and keeps them, through
/// edits and caret moves, until Escape or the action again. Each editor of a split has its own, for the file it shows.</summary>
public sealed class UsageHighlighting
{
    private readonly EditorArea editors;
    private readonly Func<CSharpService?> service;
    private readonly ConditionalWeakTable<CodeEditor, Watch> watches = [];

    public UsageHighlighting(EditorArea editors, Func<CSharpService?> service)
    {
        this.editors = editors;
        this.service = service;
        editors.ForEachEditor(editor =>
        {
            Watch watch = new(this, editor);
            watches.Add(editor, watch);
            editor.SelectionChanged += (_, _) => watch.Moved();
        });
        // An edit that leaves the caret where it was (Delete) takes the uses away all the same, and the rest brings them back.
        editors.Opened += (_, document) => document.Text.Changed += (_, _) =>
        {
            foreach (EditorGroup group in editors.Groups)
            {
                if (group.Active == document && watches.TryGetValue(group.Editor, out Watch? watch))
                {
                    watch.AskAgain();
                }
            }
        };
    }

    /// <summary>The server's state changed (started, its projects loaded, started again): every editor asks again once its
    /// caret rests, since what the server could tell before may be more now, or a caret placed as the file opened was
    /// never asked about.</summary>
    public void Refresh()
    {
        foreach (EditorGroup group in editors.Groups)
        {
            if (watches.TryGetValue(group.Editor, out Watch? watch))
            {
                watch.AskAgain();
            }
        }
    }

    /// <summary>What the status bar says of the uses Highlight Usages in File found.</summary>
    public event EventHandler<string>? Message;

    public void AddActions(ActionRegistry actions) =>
        actions.Add(new IdeAction(AppActions.HighlightUsages, "Highlight Usages in File", "Code", context => IdeErrors.Observe(HighlightInFile()))
        {
            WhyNot = context => context.File is null ? Refusals.NoFile
                : service()?.For(context.File) is not null ? null
                : service() is null ? Refusals.NoCSharp
                : Refusals.NotCSharp,
            Menus = [new("Edit/Find", 2, 0)],
        });

    /// <summary>The file an editor shows, of the split it is in.</summary>
    private OpenDocument? DocumentOf(CodeEditor editor) => editors.Groups.FirstOrDefault(g => g.Editor == editor)?.Active;

    /// <summary>The server's highlights for the selection or caret in a file, or null when the text or the file shown
    /// changed while it answered; none when the server cannot answer.</summary>
    private async Task<List<UsageHighlight>?> AskAsync(CodeEditor editor, OpenDocument document, Selection selection, CancellationToken cancel)
    {
        if (service()?.For(document)?.Lsp is not { } lsp)
        {
            return [];
        }
        int version = document.Text.Version;
        int offset = selection.Start + selection.Length / 2;
        try
        {
            IReadOnlyList<DocumentHighlight> found = await lsp.HighlightsAsync(offset, cancel);
            if (cancel.IsCancellationRequested || document.Text.Version != version || editor.Document != document.Text)
            {
                return null;
            }
            ParsedText? syntax = document.Syntax is { } parsing ? await parsing.CurrentAsync() : null;
            if (cancel.IsCancellationRequested || document.Text.Version != version || editor.Document != document.Text)
            {
                return null;
            }
            LspPosition[] ends = new LspPosition[found.Count * 2];
            for (int i = 0; i < found.Count; i++)
            {
                ends[2 * i] = found[i].Range.Start;
                ends[(2 * i) + 1] = found[i].Range.End;
            }
            int[] offsets = lsp.OffsetsOf(ends);
            List<UsageHighlight> uses = new(found.Count);
            for (int i = 0; i < found.Count; i++)
            {
                int start = offsets[2 * i];
                bool write = found[i].Kind == DocumentHighlightKind.Write || (syntax is not null && UsageKinds.DeclaresWithValue(syntax.Root, start));
                uses.Add(new UsageHighlight(TextRange.FromBounds(start, Math.Max(start, offsets[(2 * i) + 1])), write));
            }
            return uses.Any(u => Covers(u.Range, selection)) ? uses : [];
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (LanguageServerFailed)
        {
            return [];
        }
    }

    /// <summary>The places of the word an editor's selection is, the chosen one among them, found off the window's thread;
    /// none for a selection that is no whole word, and null when the text or the file shown changed meanwhile.</summary>
    private static async Task<List<UsageHighlight>?> WordPlacesAsync(CodeEditor editor, OpenDocument document, Selection chosen, CancellationToken cancel)
    {
        if (chosen.IsEmpty)
        {
            return [];
        }
        TextSnapshot text = editor.Document.Snapshot;
        if (document.Syntax is { } parsing)
        {
            if (await parsing.ParsedAsync(text) is not { } syntax || cancel.IsCancellationRequested || editor.Document != document.Text)
            {
                return null;
            }
            SyntaxToken token = syntax.Root.FindToken(chosen.Start, findInsideTrivia: true);
            if (token.IsKind(SyntaxKind.IdentifierToken) && token.Span.Contains(chosen.Start))
            {
                return [];
            }
        }
        IReadOnlyList<TextRange> places = await BackgroundWork.Run(() => TextSearch.PlacesOfWord(text, chosen.Range));
        if (cancel.IsCancellationRequested || !ReferenceEquals(editor.Document.Snapshot, text) || editor.Document != document.Text)
        {
            return null;
        }
        List<UsageHighlight> found = new(places.Count);
        foreach (TextRange place in places)
        {
            found.Add(new UsageHighlight(place, IsWrite: false, ByText: true));
        }
        return found;
    }

    /// <summary>Highlight Usages in File: the uses of the symbol at the caret, kept until Escape, the status bar saying
    /// how many; asked again on one of them, it takes them away.</summary>
    private async Task HighlightInFile()
    {
        CodeEditor editor = editors.CodeEditor;
        if (DocumentOf(editor) is not { } document || !watches.TryGetValue(editor, out Watch? watch))
        {
            return;
        }
        Selection point = Selection.At(editor.Selections.Primary.Caret);
        if (editor.UsagesKept && OnOne(editor.UsageHighlights, point))
        {
            editor.SetUsageHighlights([]);
            return;
        }
        if (service() is { } csharp && csharp.For(document)?.Lsp is null)
        {
            Message?.Invoke(this, csharp.Unready("Highlight Usages in File", document));
            return;
        }
        watch.Stop();
        using (BackgroundWork.Scope())
        {
            CancellationTokenSource cancel = watch.Begin();
            if (await AskAsync(editor, document, point, cancel.Token) is not { } uses)
            {
                return;
            }
            if (uses.Count == 0)
            {
                editor.SetUsageHighlights([]);
                Message?.Invoke(this, "No usages found");
                return;
            }
            editor.SetUsageHighlights(uses, keep: true);
            string name = editor.Document.Snapshot.GetText(uses.First(u => Covers(u.Range, point)).Range);
            // Each named by the keys the keymap in force gives it, or by its name while nothing runs it.
            string again = ActionTips.Keys(AppActions.HighlightUsages) is { } keys ? $"press {keys} again" : "run Highlight Usages in File again";
            string escape = ActionTips.Keys(EditorActions.Escape) ?? "Escape";
            Message?.Invoke(this, $"{Core.Plural.Of(uses.Count, "usage")} of {name} found ({again} to remove the highlighting, {escape} to remove all highlighting)");
        }
    }

    /// <summary>Whether one occurrence covers the selection or touches the caret.</summary>
    private static bool Covers(TextRange range, Selection selection) => range.Start <= selection.Start && selection.End <= range.End;

    /// <summary>Whether one of the sorted highlights still applies to the selection or caret.</summary>
    private static bool OnOne(IReadOnlyList<UsageHighlight> uses, Selection selection)
    {
        int at = Sorted.FirstAtOrAfter(uses, static u => u.Range.End, selection.End);
        return at < uses.Count && Covers(uses[at].Range, selection)
            && (!uses[at].ByText || selection.IsEmpty || uses[at].Range == selection.Range);
    }

    /// <summary>One editor's caret, watched: a move off the uses shown takes them away and asks again once it rests.</summary>
    private sealed class Watch
    {
        private readonly UsageHighlighting owner;
        private readonly CodeEditor editor;
        private readonly Debounce rest;
        private CancellationTokenSource? asking;

        public Watch(UsageHighlighting owner, CodeEditor editor)
        {
            this.owner = owner;
            this.editor = editor;
            rest = new Debounce(TypingPauses.Uses, AskAsync);
        }

        public void Moved()
        {
            UiThread.Check();
            if (editor.UsagesKept)
            {
                return;
            }
            IReadOnlyList<UsageHighlight> uses = editor.UsageHighlights;
            if (!editor.Selections.HasMultiple && OnOne(uses, editor.Selections.Primary)
                && (!uses[0].ByText || owner.DocumentOf(editor)?.Syntax is null))
            {
                return;
            }
            Stop();
            if (uses.Count > 0)
            {
                editor.SetUsageHighlights([]);
            }
            AskOnceRested();
        }

        /// <summary>Waits for the caret to rest where the server may light something or a word may be chosen; a caret
        /// moved in a file no server reads, with nothing selected, waits for nothing.</summary>
        private void AskOnceRested()
        {
            if (!editor.Selections.HasMultiple && owner.DocumentOf(editor) is { } document
                && (!editor.Selections.Primary.IsEmpty || owner.service()?.For(document) is not null))
            {
                rest.Restart();
            }
        }

        /// <summary>Asks once the caret rests, whatever it did: the text changed (which the editor takes the uses away for),
        /// or the server did.</summary>
        public void AskAgain()
        {
            UiThread.Check();
            if (editor.UsagesKept)
            {
                return;
            }
            Stop();
            AskOnceRested();
        }

        public void Stop()
        {
            rest.Stop();
            asking?.Cancel();
            asking = null;
        }

        public CancellationTokenSource Begin()
        {
            asking?.Cancel();
            asking = new CancellationTokenSource();
            return asking;
        }

        private async Task AskAsync()
        {
            if (editor.UsagesKept || editor.Selections.HasMultiple || owner.DocumentOf(editor) is not { } document)
            {
                return;
            }
            CancellationTokenSource cancel = Begin();
            Selection selection = editor.Selections.Primary;
            if (await owner.AskAsync(editor, document, selection, cancel.Token) is not { } uses || asking != cancel || editor.UsagesKept)
            {
                return;
            }
            if (uses.Count == 0)
            {
                if (await WordPlacesAsync(editor, document, selection, cancel.Token) is not { } places || asking != cancel || editor.UsagesKept)
                {
                    return;
                }
                uses = places;
            }
            editor.SetUsageHighlights(uses);
        }
    }
}
