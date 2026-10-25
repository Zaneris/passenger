using Avalonia.Controls;
using Passenger.App.Vcs;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>The window's merge conflicts: the Conflicts dialog (from Git | Resolve Conflicts..., the Commit window's
/// Merge Conflicts, and by itself when an update, an unstash or a cherry-pick stops for them), the three-way merge
/// window over it, and Continue Rebase, Abort Rebase and Abort Merge.</summary>
public partial class MainWindow
{
    private ConflictsDialog? conflictsDialog;

    /// <summary>The Conflicts dialog while it is open.</summary>
    public ConflictsDialog? ConflictsWindow => conflictsDialog;

    private void AddMergeConflicts()
    {
        Add(AppActions.ResolveConflicts, "Resolve Conflicts…", _ => ShowConflicts(), 5,
            () => session.Vcs.Conflicts.Count == 0 ? "No conflicts found" : null);
        Add(AppActions.ContinueRebase, "Continue Rebase", context => IdeErrors.Observe(session.Vcs.ContinueRebaseAsync()), 6, NoRebase);
        Add(AppActions.AbortRebase, "Abort Rebase", context => IdeErrors.Observe(AbortAsync("Abort Rebase", $"Abort rebase in {Path.GetFileName(session.Vcs.Repository?.Root)}?")), 7, NoRebase);
        Add(AppActions.AbortMerge, "Abort Merge", context => IdeErrors.Observe(AbortAsync("Abort Merge", "Abort merge?")), 8,
            () => session.Vcs.Merge.Operation is MergeOperation.Merge ? null : "No merge in progress");

        string? NoRebase() => session.Vcs.Merge.Operation is MergeOperation.Rebase ? null : "No rebase in progress";

        void Add(string id, string title, Action<ActionContext> run, int order, Func<string?> whyNot) =>
            actions!.Add(new IdeAction(id, title, "Git", run)
            {
                WhyNot = context => NeedsFolder(context) ?? (session.Vcs.Repository is null ? Refusals.NoRepository : whyNot()),
                Menus = [new MenuPlace("Git", 0, order)],
            });
    }

    /// <summary>The Conflicts dialog over the window, or the one open already; null when nothing is in conflict. Once
    /// it closes with every conflict of a rebase resolved, the rebase is offered to go on.</summary>
    public ConflictsDialog? ShowConflicts()
    {
        if (session.Vcs.Repository is null || session.Vcs.Conflicts.Count == 0)
        {
            status.Text = session.Vcs.Repository is null ? Refusals.NoRepository : "No conflicts found";
            return null;
        }
        if (conflictsDialog is { } open)
        {
            open.Activate();
            return open;
        }
        ConflictsDialog dialog = null!;
        dialog = new ConflictsDialog(session.Vcs, conflict => MergeAsync(conflict.Path, dialog));
        conflictsDialog = dialog;
        dialog.Closed += (_, _) =>
        {
            conflictsDialog = null;
            IdeErrors.Observe(OfferToContinueRebaseAsync());
        };
        IdeErrors.Observe(dialog.ShowDialog(this));
        return dialog;
    }

    /// <summary>Once the Conflicts dialog closes with a rebase's last conflict resolved, whether to continue the rebase.</summary>
    private async Task OfferToContinueRebaseAsync()
    {
        // Closed as the last file was resolved, while that is still being read.
        await session.Vcs.Idle;
        if (session.Vcs.Merge.Operation is MergeOperation.Rebase && session.Vcs.Conflicts.Count == 0
            && await Confirm("Resolve Conflicts", "All conflicts have been resolved. Do you want to continue rebase?", "Continue Rebase"))
        {
            await session.Vcs.ContinueRebaseAsync();
        }
    }

    /// <summary>The merge window on a conflicted file, over the Conflicts dialog (or this window), done once it closes:
    /// whether the file was resolved there. A file a side lacks has nothing to merge, and never becomes an empty file:
    /// the Conflicts dialog shows it chosen instead, its Accept Yours and Accept Theirs keeping or deleting it.</summary>
    public async Task<bool> MergeAsync(string path, Window? over = null)
    {
        if (session.Vcs.Repository is not { } repository || session.Vcs.Conflicts.FirstOrDefault(c => c.Path == path) is not { } conflict)
        {
            return false;
        }
        if (!conflict.CanMerge)
        {
            Unmergeable(conflict);
            return false;
        }
        // What the editors show is on disk before the merge reads and writes it.
        Editors.SaveAll();
        if (await session.Vcs.MergeTextsAsync(path) is not { } texts)
        {
            return false;
        }
        MergeView view = DiffPanes.ForMerge(texts, path);
        MergeWindow window = null!;
        window = new MergeWindow($"Merge Revisions for {repository.Relative(path)}", view, async text => (await session.Vcs.ResolveAsync(path, text)).Succeeded,
            (title, text, yes, no) => AskAsync(window, title, text, yes, no));
        // A dialog, yet an editor of the IDE's: its keymap runs there as in a diff's window.
        windowKeys.Attach(window);
        Window owner = over is { IsVisible: true } ? over : this;
        await window.ShowDialog<bool?>(owner);
        return window.Resolved;
    }

    /// <summary>The Conflicts dialog with a file one side lacks chosen, and the status bar saying which keeps it.</summary>
    private void Unmergeable(GitConflict conflict)
    {
        ConflictSides sides = session.Vcs.Sides;
        bool yours = sides.YoursHas(conflict);
        bool theirs = sides.TheirsHas(conflict);
        string why = yours || theirs
            ? $"{conflict.Name} is only in {(yours ? "yours" : "theirs")}, so there is nothing to merge: Accept {(yours ? "Yours" : "Theirs")} keeps it, Accept {(yours ? "Theirs" : "Yours")} deletes it"
            : $"{conflict.Name} is in neither side, so there is nothing to merge: Accept Yours or Accept Theirs deletes it";
        if (ShowConflicts() is { } dialog)
        {
            dialog.Chosen = [.. dialog.Rows.Where(r => r.Conflict.Path == conflict.Path)];
        }
        status.Text = why;
    }

    /// <summary>Asks as <see cref="Confirm"/> does, over the window asking, with its own answer for no.</summary>
    private Task<bool> AskAsync(Window over, string title, string text, string yes, string no) =>
        confirm is { } answer ? answer(title, text, yes) : new ConfirmDialog(title, text, yes, no).ShowDialog<bool>(over);

    private async Task AbortAsync(string title, string question)
    {
        if (await Confirm(title, question, "Abort"))
        {
            await session.Vcs.AbortAsync();
        }
    }
}
