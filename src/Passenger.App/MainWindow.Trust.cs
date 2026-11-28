using Passenger.Core;

namespace Passenger.App;

/// <summary>The open folder's trust (<see cref="WorkspaceTrust"/>): a folder opened untrusted runs none of its code, the
/// banner over the editors saying so with Trust Folder; one the person never answered for asks once as it opens
/// (<see cref="WorkspaceTrustDialog"/>), the answer kept in their state where the window keeps it, and changed or forgotten
/// in Settings' Trusted Folders. A folder in a Git repository is trusted with the repository, as each of these says. A
/// folder trusted here, in another window or through a parent, has its session begin at once what it held back, as an open
/// begins it; one whose trust is taken back holds back what it can, the banner naming what runs on.</summary>
public partial class MainWindow
{
    /// <summary>The trust question while it asks, else null.</summary>
    public WorkspaceTrustDialog? AskingTrust { get; private set; }

    /// <summary>What the banner over an untrusted folder says, null while it says nothing.</summary>
    public string? TrustSaid => TrustBanner.Said;

    private void AddTrust()
    {
        actions!.Add(new IdeAction(AppActions.TrustFolder, "Trust Folder", "IDE", _ => TrustFolder(session.Folder!))
        {
            WhyNot = context => context.ByAgent ? Refusals.TrustIsThePersons
                : session.Folder is null ? Refusals.NoFolder
                : session.Trusted ? Refusals.TrustedAlready
                : null,
            Words = ["Workspace Trust", "Restricted", "Safe Mode"],
            Works = AppActions.TrustFolder,
        });
        TrustBanner.Link(actions[AppActions.TrustFolder]!.Title, () => Request(AppActions.TrustFolder));
        EventHandler changed = (_, _) => UiThread.Post(FollowTrust);
        WorkspaceTrust.Changed += changed;
        Closed += (_, _) => WorkspaceTrust.Changed -= changed;
    }

    /// <summary>The folders the person trusted as their state keeps them, read off the window's thread and taken in, before
    /// a folder opens; at once in a window keeping no state. One kept below the top of its Git repository was trusted
    /// before trusting it named the repository (<see cref="WorkspaceTrust.FolderToTrust"/>): it is forgotten instead, so
    /// the folder asks again, naming the repository, as it opens.</summary>
    private async Task TakeKeptTrustAsync()
    {
        if (!keepsWorkspace)
        {
            return;
        }
        string[] taking = [.. (await Store().Trust.ValueAsync()).Trusted.Where(f => !WorkspaceTrust.Trusts(f))];
        if (taking.Length == 0)
        {
            return;
        }
        string[] below = await BackgroundWork.Run(() =>
        {
            string[] widened = [.. taking.Where(f => !PathNames.Same(WorkspaceTrust.FolderToTrust(f), f))];
            WorkspaceTrust.Trust(taking.Except(widened, PathNames.Comparer));
            return widened;
        });
        if (below.Length > 0)
        {
            IdeErrors.Write($"Trust kept of {Plural.Listed(below)}, below the top of a Git repository, forgotten: each asks again, naming its repository");
            state!.Trust.Set(below.Aggregate(state.Trust.Value, (kept, folder) => kept.Forgotten(folder)));
        }
    }

    /// <summary>A folder trusted since its session began (here, in another window, or a folder above it) has the session
    /// begin what it held back, and its XAML previews start; one whose trust was taken back has the session hold back what
    /// trusting began, and its previews end; the banner says what stands.</summary>
    private void FollowTrust()
    {
        if (session.Folder is { } folder && session.Trusted != WorkspaceTrust.Trusts(folder))
        {
            if (session.Trusted)
            {
                session.Untrust();
            }
            else
            {
                AskingTrust?.Close();
                session.Trust();
            }
            // Each preview runs the project's build, or said why it drew nothing: it starts again as the trust now stands.
            RestartPreviews(null);
            UpdateStatus();
        }
        ShowTrust();
    }

    /// <summary>The banner over the editors says the open folder is untrusted, while it is, naming the Git repository its
    /// Trust Folder trusts where it is in one, and what the person started there runs on until it ends.</summary>
    private void ShowTrust() => TrustBanner.Say((session.Folder is not { } folder || session.Trusted ? null
        : session.StillRunning() is { Count: > 0 } running ? Refusals.UntrustedRunsOn(running)
        : WorkspaceTrust.FolderToTrust(folder) is { } trusting && !PathNames.Same(trusting, folder) ? Refusals.UntrustedInRepository(trusting)
        : Refusals.UntrustedSaid)?.Shown);

    /// <summary>Settings' Trusted Folders put in force: each folder trusted there trusted, one in a Git repository with the
    /// repository (<see cref="WorkspaceTrust.FolderToTrust"/>), each no longer trusted taken back (every window of it holding
    /// back what trusting began, <see cref="FollowTrust"/>), and the answers kept as changed from those Settings showed, so
    /// another window's change meanwhile stays; the answers as now kept, for Settings to list.</summary>
    private State.TrustState ApplyTrust(State.TrustState from, State.TrustState to)
    {
        UiThread.Check();
        static bool In(IReadOnlyList<string> folders, string folder) => folders.Any(f => PathNames.Same(f, folder));
        State.TrustState kept = Store().Trust.Value;
        foreach (string forgotten in from.Trusted.Concat(from.Untrusted).Where(f => !In(to.Trusted, f) && !In(to.Untrusted, f)))
        {
            kept = kept.Forgotten(forgotten);
        }
        foreach (string declined in to.Untrusted.Where(f => !In(from.Untrusted, f)))
        {
            kept = kept.StoppedTrusting(declined);
        }
        string[] trusting = [.. to.Trusted.Where(f => !In(from.Trusted, f)).Select(WorkspaceTrust.FolderToTrust)];
        foreach (string trusted in trusting)
        {
            kept = kept.With(trusted);
        }
        state!.Trust.Set(kept);
        foreach (string taken in from.Trusted.Where(f => !In(to.Trusted, f)))
        {
            WorkspaceTrust.TakeBack(taken);
        }
        WorkspaceTrust.Trust(trusting);
        return kept;
    }

    /// <summary>Asks of a folder opened untrusted whether the person trusts it, unless they answered for it, or a folder
    /// above it, before: once the window shows, and never while it asks of it already.</summary>
    private void AskTrust(string folder)
    {
        if (session.Trusted || (keepsWorkspace && Store().Trust.Value.Answered(folder)) || AskingTrust is { } asking && PathNames.Same(asking.Folder, folder))
        {
            return;
        }
        if (!IsVisible)
        {
            EventHandler? shown = null;
            shown = (_, _) =>
            {
                Opened -= shown;
                if (PathNames.Same(session.Folder ?? string.Empty, folder))
                {
                    AskTrust(folder);
                }
            };
            Opened += shown;
            return;
        }
        AskingTrust?.Close();
        WorkspaceTrustDialog dialog;
        try
        {
            dialog = new WorkspaceTrustDialog(folder);
        }
        catch (AgentRefusedException)
        {
            // Opened by an agent's action, which asks the person nothing: the banner offers Trust Folder.
            return;
        }
        AskingTrust = dialog;
        dialog.Closed += (_, _) =>
        {
            if (AskingTrust == dialog)
            {
                AskingTrust = null;
            }
            switch (dialog.Answered)
            {
                case TrustAnswer.Trusted:
                    TrustFolder(dialog.Trusting);
                    break;
                case TrustAnswer.ParentTrusted:
                    TrustFolder(dialog.Above ?? dialog.Trusting);
                    break;
                case TrustAnswer.Untrusted when keepsWorkspace:
                    state!.Trust.Set(state.Trust.Value.Declined(folder));
                    break;
            }
        };
        IdeErrors.Observe(dialog.ShowDialog(this));
    }

    /// <summary>Trusts a folder, with every folder under it, one in a Git repository with the whole repository
    /// (<see cref="WorkspaceTrust.FolderToTrust"/>), kept in the person's state where the window keeps it: every window's
    /// session of it begins what it held back (<see cref="FollowTrust"/>). Refused where an agent's input into the window
    /// asked for it, or started the work that does (<see cref="RefusedToAgentsInput"/>), the status bar saying why.</summary>
    private void TrustFolder(string folder)
    {
        UiThread.Check();
        if (RefusedToAgentsInput(Refusals.TrustIsThePersons.Shown))
        {
            return;
        }
        string trusted = WorkspaceTrust.FolderToTrust(folder);
        if (keepsWorkspace)
        {
            state!.Trust.Set(state.Trust.Value.With(trusted));
        }
        WorkspaceTrust.Trust(trusted);
    }
}
