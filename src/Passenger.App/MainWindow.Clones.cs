using System.Runtime.CompilerServices;
using Passenger.App.CSharp;
using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>Code clones in the window: Find Code Clones over the solution, or over what the Explorer's menu is asked of,
/// and Find Clones of Selection over the statements chosen, each through Passenger's extension in the C# language server
/// off the window's thread, one search at a time (asked again, the one under way ends), its stages said; the Clones
/// window listing what they found, two clones compared side by side, and Extract Method from Clones making a group share
/// one method as one command of global Undo, its name then typed where the first clone was.</summary>
public partial class MainWindow
{
    private ClonesView? clones;
    private FoundClones? clonesFound;
    private Task? clonesSearch;
    private CancellationTokenSource? clonesAsked;

    /// <summary>Why each group's clones can't share one method, as the extension said once the group was chosen; null where they can.</summary>
    private readonly ConditionalWeakTable<CloneSet, StrongBox<string?>> clonesExtracted = [];

    /// <summary>The Clones tool window, built as it is first shown or asked for.</summary>
    public ClonesView ClonesWindow => clones ?? (ClonesView)Dock[ToolWindowIds.Clones].Content;

    private void AddClones()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Clones, "Clones", IconKeys.Copy, () =>
        {
            ClonesView view = new() { WhyNotExtracted = set => clonesExtracted.TryGetValue(set, out StrongBox<string?>? why) ? why.Value : null };
            clones = view;
            view.ActionRequested += (_, action) => Request(action);
            view.GoToRequested += (_, clone) => GoToClone(clone);
            view.ChosenChanged += (_, _) =>
            {
                if (view.Chosen.Set is { } set && !clonesExtracted.TryGetValue(set, out _))
                {
                    IdeErrors.Observe(AskExtractedAsync(set));
                }
            };
            view.Show(clonesFound, Folder);
            return view;
        }, Docking.ToolAnchor.BottomLeft)
        {
            Serves = ToolWindowServes.EveryFolder,
            ExpandAll = () => ClonesWindow.ExpandAll(),
            CollapseAll = () => ClonesWindow.CollapseAll(),
        });
        actions!.Add(new IdeAction(AppActions.FindClones, "Find Code Clones", "Code", context =>
        {
            (IReadOnlyList<string> within, string searched) = ClonesScope(context);
            IdeErrors.Observe(FindClonesAsync(AppActions.FindClones, within, searched, null));
        })
        {
            WhyNot = _ => session.CSharp is null ? session.WhyNoCSharp : null,
            Menus = [new MenuPlace("Code", 3, 0)],
            Words = ["duplicates", "duplicated code", "copy and paste"],
        });
        actions.Add(new IdeAction(AppActions.FindClonesOfSelection, "Find Clones of Selection", "Code", context => IdeErrors.Observe(FindSelectionClonesAsync()))
        {
            WhyNot = context => Refusals.NeedsFile(context) ?? (context.File!.Language != Languages.Language.CSharp ? Refusals.NotCSharpStatements
                : session.CSharp is null ? session.WhyNoCSharp : null),
            Menus = [new MenuPlace("Code", 3, 1)],
            Words = ["duplicates"],
        });
        actions.Add(new IdeAction(AppActions.CompareClones, "Compare Clones Side by Side", "Code", _ => IdeErrors.Observe(CompareClonesAsync()))
        {
            WhyNot = _ => clones?.Chosen.Set is null ? Refusals.NoCloneChosen : null,
        });
        actions.Add(new IdeAction(AppActions.ExtractClones, "Extract Method from Clones…", "Code", _ => IdeErrors.Observe(ExtractClonesAsync()))
        {
            WhyNot = _ => clones?.Chosen.Set is not { } set ? Refusals.NoCloneChosen
                : clonesExtracted.TryGetValue(set, out StrongBox<string?>? why) && why.Value is { } refused ? refused
                : set.Clones.Select(c => ReadOnlyFiles.Refused(c.Path, Path.GetFileName(c.Path))).FirstOrDefault(w => w is not null),
        });
    }

    /// <summary>A session's folder lists no clones of another's: the window empties, and a search under way ends.</summary>
    private void BindClones(ProjectSession next)
    {
        // The search's own source, let go of as it ends, is cancelled only while it runs.
        if (clonesSearch is { IsCompleted: false })
        {
            clonesAsked?.Cancel();
        }
        clonesFound = null;
        clones?.Show(null, next.Folder);
    }

    /// <summary>What Find Code Clones looks through: the rows the Explorer's menu or keys ask of (a file, a folder, a project,
    /// a solution folder's projects), with the rows chosen with it; else the whole solution.</summary>
    private (IReadOnlyList<string> Within, string Searched) ClonesScope(ActionContext context)
    {
        if (RowOf(context) is not { } row || row.Kind == NodeKind.Solution)
        {
            return ([], "the solution");
        }
        IReadOnlyList<ExplorerNode> rows = Explorer.SelectedNodes is { Count: > 1 } chosen && chosen.Contains(row) ? chosen : [row];
        if (rows.Any(r => r.Kind == NodeKind.Solution))
        {
            return ([], "the solution");
        }
        List<string> within = [];
        foreach (ExplorerNode each in rows)
        {
            switch (each)
            {
                case { Kind: NodeKind.Project or NodeKind.SolutionFolder }:
                    within.AddRange(Explorer.Tree.ProjectsOf(each));
                    break;
                case { Path: { } path }:
                    within.Add(path);
                    break;
            }
        }
        return (within, rows.Count > 1 ? $"the {Numbers.Said(rows.Count)} chosen" : row.Name);
    }

    /// <summary>Find Clones of Selection: the statements the selection covers, or the one the caret is in, looked for across
    /// the solution.</summary>
    private Task FindSelectionClonesAsync()
    {
        if (Editors.Active is not { } active)
        {
            return Task.CompletedTask;
        }
        CodeEditor editor = Editors.CodeEditor;
        TextSnapshot text = editor.Document.Snapshot;
        Selection chosen = editor.Selections.Primary;
        CloneRun asked = CloneSearch.Selection(active.Path, text, text.GetPosition(chosen.Start), text.GetPosition(chosen.End));
        return FindClonesAsync(AppActions.FindClonesOfSelection, [], $"the solution for the selection in {active.Name}", asked);
    }

    /// <summary>A search for code clones, the one under way ended first.</summary>
    public async Task<FoundClones?> FindClonesAsync(string id, IReadOnlyList<string> within, string searched, CloneRun? selection)
    {
        if (clonesSearch is { IsCompleted: false } under)
        {
            clonesAsked?.Cancel();
            await under.Ended();
        }
        Task<FoundClones?> search = SearchClonesAsync(id, within, searched, selection);
        clonesSearch = search;
        return await search;
    }

    private async Task<FoundClones?> SearchClonesAsync(string id, IReadOnlyList<string> within, string searched, CloneRun? selection)
    {
        ProjectSession current = session;
        using CancellationTokenSource asked = new();
        clonesAsked = asked;
        using Working? work = actions!.Work.Begin(id, "Waiting for the C# language server to load the solution…");
        if (work is null)
        {
            return null;
        }
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(asked.Token, work.Stopping);
        try
        {
            if (current.CSharp is not { } csharp || !await ReadyAsync(csharp, stop.Token) || csharp.Session is not { } roslyn)
            {
                string why = (current.CSharp is null ? current.WhyNoCSharp : current.CSharp.IsStopped ? Refusals.CSharpStopped
                    : current.CSharp.Unavailable is { } down ? Refusals.CSharpUnavailable(down) : Refusals.NoCSharp).Shown;
                work.Tell(false, why);
                Refuse(why);
                return null;
            }
            work.Say($"Finding code clones in {searched}…");
            Dictionary<string, TextSnapshot> open = new(PathNames.Comparer);
            foreach (OpenDocument document in Editors.Documents.Where(d => d.HoldsText))
            {
                open.TryAdd(document.Path, document.Text.Snapshot);
            }
            FoundClones found = await BackgroundWork.Run(() => CloneSearch.FindAsync(roslyn, within, selection, searched, CloneSearch.LeastTokens, open, stop.Token), stop.Token);
            if (session != current)
            {
                return null;
            }
            clonesFound = found;
            ShowToolWindow(ToolWindowIds.Clones);
            ClonesWindow.Show(found, Folder);
            if (found.Problem is null)
            {
                Say(found.Said);
            }
            else
            {
                Refuse(found.Said);
            }
            work.Tell(found.Problem is null, found.Said);
            return found;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            if (work.IsStopped)
            {
                Say("Find Code Clones stopped");
            }
            return null;
        }
        catch (LanguageServerFailed failed)
        {
            string said = $"Find Code Clones failed: {failed.Reason}";
            work.Tell(false, said);
            Refuse(said);
            return null;
        }
    }

    /// <summary>Waits until the C# language server has loaded the solution, or can't: stopped, or unable to start.</summary>
    private static async Task<bool> ReadyAsync(CSharpService csharp, CancellationToken cancel)
    {
        while (!csharp.IsReady && !csharp.IsStopped && csharp.Unavailable is null)
        {
            TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void Heard(object? sender, EventArgs e) => changed.TrySetResult();
            csharp.StateChanged += Heard;
            try
            {
                if (csharp.IsReady)
                {
                    break;
                }
                await changed.Task.WaitAsync(cancel);
            }
            finally
            {
                csharp.StateChanged -= Heard;
            }
        }
        return csharp.IsReady;
    }

    /// <summary>A clone's file shown with its statements chosen.</summary>
    private void GoToClone(ClonePlace clone)
    {
        NavigateTo(clone.Path, clone.Start);
        if (Editors.Active is { } active && PathNames.Same(active.Path, clone.Path))
        {
            TextSnapshot text = Editors.CodeEditor.Document.Snapshot;
            if (clone.End.Line < text.LineCount)
            {
                Editors.CodeEditor.Selections = SelectionSet.Single(new Selection(text.GetOffset(clone.Start), text.GetOffset(clone.End)));
            }
        }
    }

    /// <summary>Compare Clones Side by Side: the chosen group's first clone on the left, the clone chosen under it (else its
    /// second) on the right, each as its whole lines, read-only, where Settings opens diffs.</summary>
    private async Task CompareClonesAsync()
    {
        if (clones?.Chosen is not ({ } set, var chosen))
        {
            return;
        }
        ClonePlace left = set.Clones[0];
        ClonePlace right = chosen is not null && !ReferenceEquals(chosen, left) ? chosen : set.Clones[1];
        TextSnapshot? leftOpen = Editors.TextOf(left.Path);
        TextSnapshot? rightOpen = Editors.TextOf(right.Path);
        (string before, TextDocument mine, FileDiff diff) = await BackgroundWork.Run(async () =>
        {
            string before = CloneSearch.LinesOf(leftOpen ?? ServerPlaces.Read(left.Path) ?? new TextDocument(string.Empty).Snapshot, left);
            TextDocument mine = new(CloneSearch.LinesOf(rightOpen ?? ServerPlaces.Read(right.Path) ?? new TextDocument(string.Empty).Snapshot, right));
            return (before, mine, await ComparedAsync(before, mine.Snapshot));
        });
        string leftTitle = left.Shown(Folder);
        string rightTitle = right.Shown(Folder);
        string title = $"{Path.GetFileName(left.Path)}:{left.Start.Line + 1} vs {Path.GetFileName(right.Path)}:{right.Start.Line + 1}";
        ShowDiff(new DiffContent(title, $"clones\n{leftTitle}\n{rightTitle}", right.Path, null,
            () => Diffs.ForFile(title, diff, mine, null, (leftTitle, rightTitle), left.Path, right.Path)));
    }

    /// <summary>Whether the extension finds a group's clones can share one method, kept for the group and greying Extract
    /// Method from Clones saying why where not.</summary>
    private async Task AskExtractedAsync(CloneSet set)
    {
        if (session.CSharp is not { IsReady: true, Session: { } roslyn })
        {
            return;
        }
        ClonesExtraction asked = new([.. set.Clones.Select(c => c.Run)], "NewMethod", Apply: false);
        try
        {
            Refactored found = await BackgroundWork.Run(() => roslyn.ExtensionAsync<Refactored>(Handlers.ExtractClones, asked));
            clonesExtracted.AddOrUpdate(set, new StrongBox<string?>(found.Problem));
        }
        catch (LanguageServerFailed)
        {
            // Unknown until Extract Method from Clones asks again.
        }
    }

    /// <summary>Extract Method from Clones: the chosen group's first clone shown with its statements chosen, Extract Method
    /// made over it and every other clone called in its place as one command of global Undo, then the new method's name
    /// typed where the first clone was, as Extract Method has it typed; refused saying why where the clones can't share one.</summary>
    private async Task ExtractClonesAsync()
    {
        if (clones?.Chosen.Set is not { } set)
        {
            return;
        }
        if (session.CSharp is not { IsReady: true, Session: { } roslyn })
        {
            Refuse(session.CSharp is null ? session.WhyNoCSharp : Refusals.NoCSharp);
            return;
        }
        using Working? work = actions!.Work.Begin(AppActions.ExtractClones, $"Extracting a method from {Plural.Of(set.Clones.Count, "clone")}…", stoppable: false);
        ClonePlace first = set.Clones[0];
        GoToClone(first);
        if (Editors.Active is not { Syntax: { } syntax } shown || !PathNames.Same(shown.Path, first.Path))
        {
            return;
        }
        CodeEditor editor = Editors.CodeEditor;
        using ExtractedMethod made = new(editor.Document, syntax, editor.Selections.Primary);
        RefactorOutcome outcome = await Assist.Actions.MakeAsync("Extract Method from Clones",
            () => roslyn.ExtensionAsync<Refactored>(Handlers.ExtractClones, new ClonesExtraction([.. set.Clones.Select(c => c.Run)], "NewMethod", Apply: true)),
            files => $"Extracted a method from {Plural.Of(set.Clones.Count, "clone")} in {Plural.Of(files, "file")}", null);
        if (outcome.Problem is { } problem)
        {
            clonesExtracted.AddOrUpdate(set, new StrongBox<string?>(problem));
            work?.Tell(false, problem);
            Refuse(problem);
            return;
        }
        work?.Tell(true, outcome.Said);
        Say(outcome.Said);
        // Those clones are one call each now; the rest stand where the search found them until it is asked again.
        if (clonesFound is { } found)
        {
            clonesFound = found with { Sets = [.. found.Sets.Where(s => !ReferenceEquals(s, set))] };
            clones?.Show(clonesFound, Folder);
        }
        // The call that took the first clone's place is where the new name is typed, in the file still shown.
        if (made.Document == Editors.CodeEditor.Document && made.Call() is { } call)
        {
            Editors.CodeEditor.Selections = SelectionSet.Single(call);
            Assist.Actions.Rename();
        }
    }
}
