using System.Runtime.CompilerServices;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>Highlighting of the symbol at the caret: once the caret rests on it, the language server's highlights of
/// its uses in the file (writes apart from reads), drawn in the editor and marked on its stripe. They go as soon as the
/// caret leaves them or the text changes, so none is ever left on the wrong symbol, and come again once it rests.
/// Highlight Usages in File asks at once and keeps them, through edits and caret moves, until Escape or the action
/// again. Each editor of a split has its own, for the file it shows.</summary>
public sealed class UsageHighlighting
{
    /// <summary>How long the caret rests before the server is asked.</summary>
    private static readonly TimeSpan Rest = TimeSpan.FromMilliseconds(300);

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
                : "The file is not C#",
            Menus = [new("Edit/Find", 2, 0)],
        });

    /// <summary>The file an editor shows, of the split it is in.</summary>
    private OpenDocument? DocumentOf(CodeEditor editor) => editors.Groups.FirstOrDefault(g => g.Editor == editor)?.Active;

    /// <summary>The server's highlights for the caret of an editor showing a file, or null when the text or the file shown
    /// changed while it answered; none when the server cannot answer.</summary>
    private async Task<List<UsageHighlight>?> AskAsync(CodeEditor editor, OpenDocument document, CancellationToken cancel)
    {
        if (service()?.For(document)?.Lsp is not { } lsp)
        {
            return [];
        }
        int version = document.Text.Version;
        int caret = editor.Selections.Primary.Caret;
        try
        {
            IReadOnlyList<DocumentHighlight> found = await lsp.HighlightsAsync(caret, cancel);
            if (cancel.IsCancellationRequested || document.Text.Version != version || editor.Document != document.Text)
            {
                return null;
            }
            ParsedText? syntax = document.Syntax?.Current;
            List<UsageHighlight> uses = [.. found.Select(h =>
            {
                int start = lsp.OffsetOf(h.Range.Start);
                bool write = h.Kind == DocumentHighlightKind.Write || (syntax is not null && UsageKinds.DeclaresWithValue(syntax.Root, start));
                return new UsageHighlight(TextRange.FromBounds(start, Math.Max(start, lsp.OffsetOf(h.Range.End))), write);
            })];
            // Uses of something other than what the caret is on are none of its.
            return uses.Any(u => Covers(u.Range, caret)) ? uses : [];
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

    /// <summary>Highlight Usages in File: the uses of the symbol at the caret, kept until Escape, the status bar saying
    /// how many; asked again on one of them, it takes them away.</summary>
    private async Task HighlightInFile()
    {
        CodeEditor editor = editors.CodeEditor;
        if (DocumentOf(editor) is not { } document || !watches.TryGetValue(editor, out Watch? watch))
        {
            return;
        }
        int caret = editor.Selections.Primary.Caret;
        if (editor.UsagesKept && editor.UsageHighlights.Any(u => Covers(u.Range, caret)))
        {
            editor.SetUsageHighlights([]);
            return;
        }
        if (service() is { } csharp && csharp.For(document)?.Lsp is null)
        {
            Message?.Invoke(this, csharp.Unready("Highlight Usages in File"));
            return;
        }
        watch.Stop();
        using (BackgroundWork.Scope())
        {
            CancellationTokenSource cancel = watch.Begin();
            if (await AskAsync(editor, document, cancel.Token) is not { } uses)
            {
                return;
            }
            string keys = AppActions.Keymap().ShortcutsFor(AppActions.HighlightUsages).FirstOrDefault()?.Display() ?? "the action";
            if (uses.Count == 0)
            {
                editor.SetUsageHighlights([]);
                Message?.Invoke(this, "No usages found");
                return;
            }
            editor.SetUsageHighlights(uses, keep: true);
            string name = editor.Document.Snapshot.GetText(uses.First(u => Covers(u.Range, caret)).Range);
            Message?.Invoke(this, $"{Core.Plural.Of(uses.Count, "usage")} of {name} found "
                + $"(press {keys} again to remove the highlighting, Escape to remove all highlighting)");
        }
    }

    /// <summary>Whether a caret stands on a range, at either end of it included.</summary>
    private static bool Covers(TextRange range, int caret) => caret >= range.Start && caret <= range.End;

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
            rest = new Debounce(Rest, AskAsync);
        }

        public void Moved()
        {
            UiThread.Check();
            if (editor.UsagesKept)
            {
                return;
            }
            int caret = editor.Selections.Primary.Caret;
            if (!editor.Selections.HasMultiple && editor.UsageHighlights.Any(u => Covers(u.Range, caret)))
            {
                // Still on the symbol they are the uses of.
                return;
            }
            Stop();
            if (editor.UsageHighlights.Count > 0)
            {
                editor.SetUsageHighlights([]);
            }
            AskOnceRested();
        }

        private void AskOnceRested()
        {
            if (!editor.Selections.HasMultiple && owner.DocumentOf(editor) is { } document && owner.service()?.For(document) is not null)
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
            if (await owner.AskAsync(editor, document, cancel.Token) is { } uses && asking == cancel && !editor.UsagesKept)
            {
                editor.SetUsageHighlights(uses);
            }
        }
    }
}
