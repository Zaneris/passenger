using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>Remotes, managed in the Remotes dialog (Manage Remotes… in the Git menu) and from a remote's menu in the Git
/// log's branches panel: added and edited in the Add Remote dialog, which says what each does before it runs, and removed
/// once the person agrees; and a branch pushed to any remote in the Push dialog (Push to…, the Git menu and a branch's
/// menu), whatever it tracks.</summary>
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
        actions!.Add(new IdeAction(AppActions.PushTo, "Push to…", "Git", _ => IdeErrors.Observe(ShowPushToAsync(null)))
        {
            WhyNot = context => NeedsFolder(context) ?? (session.Vcs.Repository is null ? session.Vcs.NoRepository
                : session.Vcs.WhyNotRemote ?? (session.Vcs.Branch is null ? Refusals.Detached : null)),
            Menus = [new MenuPlace("Git", 0, 1)],
        });
        actions.Add(new IdeAction(AppActions.ManageRemotes, "Manage Remotes…", "Git", _ => ShowRemotes())
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
            start = new RemoteStart(editing, remotes, branches, editing is null ? [] : await vcs.TrackingAsync(editing.Name), vcs.WhyNotRemote);
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
        string its = branches == 0 ? "It has no branches here" : $"Its {Plural.Of(branches, "branch", "branches")} here {Plural.Agreeing(branches, "goes", "go")} with it";
        string untracked = tracking.Count == 0 ? string.Empty : $", and {Plural.Listed(tracking)} {(tracking.Count == 1 ? "tracks" : "track")} nothing from then on";
        string text = $"Remove the remote {remote.Name}? {its}{untracked}. The repository at {VcsController.UrlShown(remote.Url)} keeps everything: "
            + "adding the remote again and fetching brings its branches back.";
        if (await ConfirmAsync("Remove Remote", text, "Remove", null) && vcs == session.Vcs)
        {
            await vcs.RemoveRemoteAsync(remote.Name);
        }
    }

    /// <summary>Push to…: the Push dialog for a local branch (the one checked out where none is given), then the plan it
    /// closes with pushed.</summary>
    public async Task ShowPushToAsync(BranchInfo? branch)
    {
        VcsController vcs = session.Vcs;
        PushStart start;
        using (Working? reading = BeginWork(AppActions.PushTo, "Reading the remotes…"))
        {
            if (reading is null)
            {
                return;
            }
            BranchInfo? pushed = branch ?? (await vcs.LocalBranchesAsync()).FirstOrDefault(b => b.IsCurrent);
            if (pushed is null)
            {
                status.Text = Refusals.Detached;
                reading.Tell(false, Refusals.Detached);
                return;
            }
            string? tracked = pushed.UpstreamBranch is { } full && full.StartsWith("refs/heads/", StringComparison.Ordinal) ? full["refs/heads/".Length..] : pushed.UpstreamBranch;
            start = new PushStart(pushed.Name, pushed.UpstreamRemote, tracked, await vcs.RemotesAsync());
        }
        PushDialog dialog = new(start, vcs.PushPreviewAsync);
        pushDialog = dialog;
        dialog.Closed += (_, _) => pushDialog = pushDialog == dialog ? null : pushDialog;
        if (await dialog.ShowDialog<PushPlan?>(Frontmost) is { } plan && vcs == session.Vcs)
        {
            await vcs.PushToAsync(plan);
        }
    }
}
