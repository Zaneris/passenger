using Passenger.App.Editors;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>Stashes as the Commit window's Stashes tab shows them: a stash's file beside what it was before the stash, and a
/// stash dropped once the person agrees, one command of global Undo that puts it back while git keeps its commit.</summary>
public partial class MainWindow
{
    /// <summary>What a stash did to a file: the head it was made on's text beside the stash's, both read-only; a new file it
    /// put away beside an empty side.</summary>
    public async Task<IDiffHost?> ShowStashDiffAsync(StashInfo stash, FileChange file)
    {
        VcsController vcs = session.Vcs;
        if (vcs.Repository is null)
        {
            status.Text = vcs.NoRepository;
            return null;
        }
        bool untracked = file.Kind is ChangeKind.Unversioned;
        FileVersion? before = untracked || file.Kind is ChangeKind.Added ? null : await vcs.VersionAsync($"{stash.Commit}^1", file.From ?? file.Path, file.Path);
        FileVersion? after = file.Kind is ChangeKind.Deleted ? null : await vcs.VersionAsync(untracked ? $"{stash.Commit}^3" : stash.Commit, file.Path, file.Path);
        if (before is { IsBinary: true } || after is { IsBinary: true })
        {
            status.Text = $"{file.Name} is binary in the stash, so it has no lines to compare";
            return null;
        }
        StashSaid said = StashSaid.Of(stash.Message);
        TextDocument stashed = new(after?.Text ?? string.Empty);
        TextSnapshot text = stashed.Snapshot;
        string head = before?.Text ?? string.Empty;
        FileDiff diff = await BackgroundWork.Run(() => FileDiff.Compare(head, text));
        string left = untracked || file.Kind is ChangeKind.Added ? "Not there before" : said.Branch is { } branch ? $"{branch} as it was" : "Before the stash";
        return ShowDiff(new DiffContent($"{file.Name}: stash \"{said.Message}\"", $"{stash.Commit}\n{file.From ?? file.Path}\n{file.Path}", file.Path, null,
            () => Diffs.ForFile(file.Name, diff, stashed, null, (left, "Stashed"), file.From ?? file.Path, file.Path)));
    }

    /// <summary>Drop… of a stash in the Commit window: dropped once the person agrees (<see cref="DropStashAsync"/>).</summary>
    public async Task AskDropStashAsync(StashInfo stash)
    {
        StashSaid said = StashSaid.Of(stash.Message);
        if (await ConfirmAsync("Drop Stash", $"Drop the stash \"{said.Message}\" without putting its changes back? Undo brings it back while git keeps its commit, "
            + $"{GitHashes.Short(stash.Commit)}.", "Drop", null))
        {
            await DropStashAsync(stash);
        }
    }

    /// <summary>A stash dropped, kept as one command of global Undo, whose Undo stores its commit back at the top of the list
    /// and whose Redo drops it again; whether it was dropped.</summary>
    public async Task<bool> DropStashAsync(StashInfo stash)
    {
        VcsController vcs = session.Vcs;
        VcsOutcome dropped = await vcs.DropStashAsync(stash);
        if (!dropped.Succeeded || vcs != session.Vcs)
        {
            return dropped.Succeeded;
        }
        string name = StashSaid.Of(stash.Message).Message;
        using CommandHistory.Recording made = Editors.Commands.Record($"Drop Stash {name}");
        made.Also(new CommandHistory.Part($"the stash {name}", _ => null, async redo => redo
            ? (await vcs.StashNowAsync(stash) is { } now ? (await vcs.DropStashAsync(now)).Problem : null)
            : await vcs.StashNowAsync(stash) is null ? await vcs.RestoreStashAsync(stash) : null));
        return true;
    }
}
