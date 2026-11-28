using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>Remotes, managed in the Remotes dialog (Manage Remotes… in the Git menu) and from a remote's menu in the Git
/// log's branches panel: added and edited in the Add Remote dialog, which says what each does before it runs, and removed
/// once the person agrees; and a branch pushed in the Push dialog (Push… and Push to…, the Git menu and a branch's menu)
/// to where it tracks or any remote, forced under a lease once the person agrees.</summary>
public partial class MainWindow
{
    private RemotesDialog? remotesDialog;
    private RemoteDialog? remoteDialog;
    private PushDialog? pushDialog;

    /// <summary>The Remotes dialog while it is open.</summary>
    public RemotesDialog? RemotesWindow => remotesDialog;

    /// <summary>The Add Remote or Edit Remote dialog while it is open.</summary>
    public RemoteDialog? RemoteWindow => remoteDialog;

    /// <summary>The Push dialog while it is open.</summary>
    public PushDialog? PushWindow => pushDialog;

    private void AddRemoteActions()
    {
        actions!.Add(new IdeAction(AppActions.PushTo, ActionTitles.PushTo, ActionGroups.Git, _ => IdeErrors.Observe(ShowPushAsync(null, elsewhere: true)))
        {
            WhyNot = context => NeedsFolder(context) ?? (session.Vcs.Repository is null ? session.Vcs.NoRepository
                : session.Vcs.WhyNotRemote ?? (session.Vcs.Branch is null ? Refusals.Detached : null)),
            Menus = [new MenuPlace("Git", 0, 1)],
        });
        actions.Add(new IdeAction(AppActions.ManageRemotes, ActionTitles.ManageRemotes, ActionGroups.Git, _ => ShowRemotes())
        {
            WhyNot = context => NeedsFolder(context) ?? (session.Vcs.Repository is null ? session.Vcs.NoRepository : null),
            Menus = [new MenuPlace("Git", 0, 3)],
        });
    }

    /// <summary>Manage Remotes…: the Remotes dialog over the window, each change made through it read again as it lands.</summary>
    public RemotesDialog ShowRemotes()
    {
        RemotesDialog dialog = new(session.Vcs.RemoteUrlsAsync, EditRemoteAsync, AskRemoveRemoteAsync);
        remotesDialog = dialog;
        dialog.Closed += (_, _) => remotesDialog = remotesDialog == dialog ? null : remotesDialog;
        dialog.Show(this);
        return dialog;
    }

    /// <summary>Add Remote… (<paramref name="editing"/> null) or Edit Remote…: the dialog over the window in front, then the
    /// plan it closes with carried out.</summary>
    public async Task EditRemoteAsync(RemoteInfo? editing)
    {
        VcsController vcs = session.Vcs;
        RemoteStart start;
        using (Working? reading = BeginWork(AppActions.ManageRemotes, "Reading the remotes…"))
        {
            if (reading is null)
            {
                return;
            }
            IReadOnlyList<string> remotes = await vcs.RemotesAsync();
            IReadOnlyList<string> branches = editing is null ? [] : [.. (await vcs.BranchTreeAsync()).Branches
                .Where(b => b.Kind == RefKind.Remote && b.Name.StartsWith(editing.Name + "/", StringComparison.Ordinal)).Select(b => b.Name)];
            start = new RemoteStart(editing, remotes, branches, editing is null ? [] : await vcs.TrackingAsync(editing.Name), vcs.WhyNotRemote?.Shown);
        }
        RemoteDialog dialog = new(start);
        remoteDialog = dialog;
        dialog.Closed += (_, _) => remoteDialog = remoteDialog == dialog ? null : remoteDialog;
        if (await dialog.ShowDialog<RemotePlan?>(Frontmost) is { } plan && vcs == session.Vcs)
        {
            await vcs.SaveRemoteAsync(plan);
        }
    }

    /// <summary>A remote of the branches panel, by its name, given to <paramref name="act"/> with its URLs as git has them
    /// now; one gone since the panel was read is said so.</summary>
    private async Task EditRemoteNamedAsync(string name, Func<RemoteInfo, Task> act)
    {
        if ((await session.Vcs.RemoteUrlsAsync()).FirstOrDefault(r => r.Name == name) is { } remote)
        {
            await act(remote);
        }
        else
        {
            status.Text = $"There is no remote {name} now";
        }
    }

    /// <summary>Remove Remote…: what goes said first (its branches here, the local branches tracking it, the repository it
    /// names kept whole), then removed once the person agrees.</summary>
    public async Task AskRemoveRemoteAsync(RemoteInfo remote)
    {
        VcsController vcs = session.Vcs;
        int branches = (await vcs.BranchTreeAsync()).Branches.Count(b => b.Kind == RefKind.Remote && b.Name.StartsWith(remote.Name + "/", StringComparison.Ordinal));
        IReadOnlyList<string> tracking = await vcs.TrackingAsync(remote.Name);
        string text = VcsWords.RemoveRemoteAsks(remote.Name, branches, tracking.Count, Plural.Listed(tracking), VcsController.UrlShown(remote.Url)).Shown;
        if (await ConfirmAsync("Remove Remote", text, "Remove", null) && vcs == session.Vcs)
        {
            await vcs.RemoveRemoteAsync(remote.Name);
        }
    }

    /// <summary>Push… (where the branch goes as it tracks, or for a branch tracking none, to origin or the first remote) or
    /// Push to… (<paramref name="elsewhere"/>: to another remote than the one tracked): the Push dialog for a local branch of
    /// <paramref name="of"/>'s repository (the window's where none is given), the one checked out where none is given, what a
    /// push there sends read first; then the plan it closes with pushed, a force push only once the person agrees.</summary>
    public async Task ShowPushAsync(BranchInfo? branch, bool elsewhere, VcsController? of = null)
    {
        VcsController vcs = of ?? session.Vcs;
        PushStart start;
        PushPreview? first;
        using (Working? reading = BeginWork(elsewhere ? AppActions.PushTo : AppActions.Push, "Reading what Push sends…"))
        {
            if (reading is null)
            {
                return;
            }
            BranchInfo? pushed = branch ?? (await vcs.LocalBranchesAsync()).FirstOrDefault(b => b.IsCurrent);
            if (pushed is null)
            {
                status.Text = Refusals.Detached.Shown;
                reading.Tell(false, Refusals.Detached);
                return;
            }
            string? tracked = pushed.UpstreamBranch is { } full && full.StartsWith("refs/heads/", StringComparison.Ordinal) ? full["refs/heads/".Length..] : pushed.UpstreamBranch;
            start = new PushStart(pushed.Name, pushed.UpstreamRemote, tracked, await vcs.RemotesAsync(), elsewhere);
            first = start.Remote is { } remote && RefNames.WhyNot(start.RemoteBranch, "branch") is null ? await vcs.PushPreviewAsync(start.Branch, remote, start.RemoteBranch) : null;
        }
        PushDialog dialog = new(start, vcs.PushPreviewAsync, first);
        pushDialog = dialog;
        dialog.Closed += (_, _) => pushDialog = pushDialog == dialog ? null : pushDialog;
        if (await dialog.ShowDialog<PushPlan?>(Frontmost) is not { } plan || (vcs != session.Vcs && !session.Others.Contains(vcs)))
        {
            return;
        }
        string there = $"{plan.Remote}/{plan.RemoteBranch}";
        if (plan.Force && !await Confirm("Force Push", $"Force push {plan.Branch} to {there}? The commits there that {plan.Branch} lacks are replaced. "
            + $"Git replaces only what {plan.Branch} once held, as {there} was last fetched: where someone pushed there since, it refuses and nothing is replaced.", "Force Push"))
        {
            return;
        }
        await vcs.PushToAsync(plan);
    }
}
