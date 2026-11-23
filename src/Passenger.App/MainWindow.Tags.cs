using Passenger.App.Vcs;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>Tags, made and moved in the New Tag dialog (from the Git menu at HEAD, a commit of the log, a branch, or Move
/// Tag… of a tag), pushed from a tag's menu (a remote that has its own tag of the name asked about first), and deleted on
/// a remote once the person agrees.</summary>
public partial class MainWindow
{
    private TagDialog? tagDialog;

    /// <summary>The New Tag dialog while it is open.</summary>
    public TagDialog? TagWindow => tagDialog;

    /// <summary>New Tag… at a revision (a commit's hash, a branch, HEAD), or Move Tag… of the tag <paramref name="moving"/>:
    /// the dialog over the window, then the plan it closes with carried out.</summary>
    public async Task ShowNewTagAsync(string at, string? moving = null)
    {
        VcsController vcs = session.Vcs;
        TagStart start;
        using (Working? reading = BeginWork(AppActions.NewTag, "Reading the remotes and how tags are signed…"))
        {
            if (reading is null)
            {
                return;
            }
            start = new TagStart(at, moving, await vcs.SigningAsync(), await vcs.RemotesAsync(), vcs.WhyNotRemote);
        }
        TagDialog dialog = new(start, vcs.CommitAtAsync, vcs.TagOfAsync);
        tagDialog = dialog;
        dialog.Closed += (_, _) => tagDialog = tagDialog == dialog ? null : tagDialog;
        if (await dialog.ShowDialog<TagPlan?>(this) is { } plan && vcs == session.Vcs)
        {
            await vcs.MakeTagAsync(plan);
        }
    }

    /// <summary>A tag's Push…: to the only remote or the one chosen of several; where that remote has its own tag of the
    /// name, naming another commit, the person is asked whether to push this one in its place.</summary>
    public async Task PushTagAsync(BranchInfo tag)
    {
        VcsController vcs = session.Vcs;
        if (await vcs.TagRemoteAsync(tag.Name, $"Push tag {tag.Name} to") is not { } remote)
        {
            return;
        }
        TagPush pushed = await vcs.PushTagAsync(tag.Name, remote);
        if (pushed.RemoteHasAnother && await Confirm($"Replace {remote}'s Tag {tag.Name}",
            $"{remote} has its own tag {tag.Name}, naming another commit. Push this one in its place? Whoever fetches from {remote} gets this one from then on; "
            + $"whoever fetched {remote}'s keeps it until they fetch tags again with --force.", "Replace"))
        {
            await vcs.PushTagAsync(tag.Name, remote, replace: true);
        }
    }

    /// <summary>A tag's Delete on Remote…: on the only remote or the one chosen of several, once the person agrees.</summary>
    public async Task DeleteRemoteTagAsync(BranchInfo tag)
    {
        VcsController vcs = session.Vcs;
        if (await vcs.TagRemoteAsync(tag.Name, $"Delete tag {tag.Name} on") is not { } remote)
        {
            return;
        }
        if (await Confirm("Delete Tag on Remote", $"Delete tag {tag.Name} on {remote}? Whoever fetches from {remote} gets it no more; "
            + "a repository that fetched it, this one too, keeps its own until it is deleted there.", "Delete"))
        {
            await vcs.DeleteRemoteTagAsync(tag.Name, remote);
        }
    }
}
