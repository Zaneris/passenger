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

/// <summary>Highlights a symbol's uses in the current file, with separate colours for reads and writes and marks on its
/// scrollbar. C# identifiers require a semantic match. Other whole words can fall back to a text search.
/// Each editor updates its own highlights as the selection or text changes. Highlight Usages in File pins them until
/// Escape or the action is run again on the same symbol.</summary>
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
        actions.Add(new IdeAction(AppActions.HighlightUsages, ActionTitles.HighlightUsages, "Code", context => IdeErrors.Observe(HighlightInFile()))
        {
            WhyNot = context => context.File is null ? Refusals.NoFile
                : service()?.For(context.File) is not null ? null
                : service() is null ? Refusals.NoCSharp
                : Refusals.NotCSharp,
            Menus = [new("Edit/Find", 2, 0)],
        });

    /// <summary>The file an editor shows, of the split it is in.</summary>
    private OpenDocument? DocumentOf(CodeEditor editor) => editors.Groups.FirstOrDefault(g => g.Editor == editor)?.Active;

    /// <summary>The server's highlights for the selection or caret. Null means the request has expired.</summary>
    private async Task<List<UsageHighlight>?> AskAsync(CodeEditor editor, OpenDocument document, TextRange target, CancellationToken cancel)
    {
        if (service()?.For(document)?.Lsp is not { } lsp)
        {
            return [];
        }
        TextSnapshot text = document.Text.Snapshot;
        try
        {
            IReadOnlyList<DocumentHighlight> found = await lsp.HighlightsAsync(target.Start + target.Length / 2, cancel);
            if (!StillShown(editor, document, text, cancel))
            {
                return null;
            }
            ParsedText? syntax = document.Syntax is { } parsing ? await parsing.CurrentAsync() : null;
            if (!StillShown(editor, document, text, cancel))
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
            return uses.Any(u => Covers(u.Range, target)) ? uses : [];
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

    /// <summary>Searches for a selected whole word once the C# parse confirms it is outside an identifier.</summary>
    private static async Task<List<UsageHighlight>?> WordPlacesAsync(CodeEditor editor, OpenDocument document, TextRange chosen, CancellationToken cancel)
    {
        if (chosen.Length == 0)
        {
            return [];
        }
        TextSnapshot text = document.Text.Snapshot;
        if (document.Syntax is { } parsing)
        {
            if (await parsing.ParsedAsync(text) is not { } syntax || !StillShown(editor, document, text, cancel))
            {
                return null;
            }
            SyntaxToken token = syntax.Root.FindToken(chosen.Start, findInsideTrivia: true);
            if (token.IsKind(SyntaxKind.IdentifierToken) && token.Span.Contains(chosen.Start))
            {
                return [];
            }
        }
        List<UsageHighlight> places = await BackgroundWork.Run(() => TextSearch.PlacesOfWord(text, chosen)
            .Select(static place => new UsageHighlight(place, IsWrite: false, ByText: true)).ToList());
        return StillShown(editor, document, text, cancel) ? places : null;
    }

    private static bool StillShown(CodeEditor editor, OpenDocument document, TextSnapshot text, CancellationToken cancel) =>
        !cancel.IsCancellationRequested && editor.Document == document.Text && ReferenceEquals(document.Text.Snapshot, text);

    /// <summary>Highlight Usages in File: the uses of the symbol at the caret, kept until Escape, the status bar saying
    /// how many; asked again on one of them, it takes them away.</summary>
    private async Task HighlightInFile()
    {
        CodeEditor editor = editors.CodeEditor;
        if (DocumentOf(editor) is not { } document || !watches.TryGetValue(editor, out Watch? watch))
        {
            return;
        }
        TextRange point = new(editor.Selections.Primary.Caret, 0);
        if (editor.UsagesKept && UseAt(editor.UsageHighlights, point) is not null)
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
    private static bool Covers(TextRange range, TextRange target) => range.Start <= target.Start && target.End <= range.End;

    /// <summary>Finds a matching occurrence in the editor's sorted highlights.</summary>
    private static UsageHighlight? UseAt(IReadOnlyList<UsageHighlight> uses, TextRange target)
    {
        int at = Sorted.FirstAtOrAfter(uses, static u => u.Range.End, target.End);
        if (at == uses.Count)
        {
            return null;
        }
        UsageHighlight use = uses[at];
        return Covers(use.Range, target) && (!use.ByText || target.Length == 0 || use.Range == target) ? use : null;
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
            if (!editor.Selections.HasMultiple && UseAt(uses, editor.Selections.Primary.Range) is { } use
                && (!use.ByText || owner.DocumentOf(editor)?.Syntax is null))
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
            CancellationToken cancel = Begin().Token;
            TextRange target = editor.Selections.Primary.Range;
            List<UsageHighlight>? uses = await owner.AskAsync(editor, document, target, cancel);
            if (uses is { Count: 0 } && !cancel.IsCancellationRequested)
            {
                uses = await WordPlacesAsync(editor, document, target, cancel);
            }
            if (uses is not null && !cancel.IsCancellationRequested && !editor.UsagesKept)
            {
                editor.SetUsageHighlights(uses);
            }
        }
    }
}
