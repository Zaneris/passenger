using Passenger.Core;

namespace Passenger.App;

/// <summary>The open folder's trust (<see cref="WorkspaceTrust"/>): a folder opened untrusted runs none of its code, the
/// banner over the editors saying so with Trust Folder; one the person never answered for asks once as it opens
/// (<see cref="WorkspaceTrustDialog"/>), the answer kept in their state where the window keeps it. A folder trusted here,
/// in another window or through a parent, has its session begin at once what it held back, as an open begins it.</summary>
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
    /// a folder opens; at once in a window keeping no state.</summary>
    private async Task TakeKeptTrustAsync()
    {
        if (keepsWorkspace)
        {
            WorkspaceTrust.Trust((await Store().Trust.ValueAsync()).Trusted);
        }
    }

    /// <summary>A folder trusted since its session began (here, in another window, or a folder above it) has the session
    /// begin what it held back; the banner says what stands.</summary>
    private void FollowTrust()
    {
        if (session.Folder is { } folder && !session.Trusted && WorkspaceTrust.Trusts(folder))
        {
            AskingTrust?.Close();
            session.Trust();
            UpdateStatus();
        }
        ShowTrust();
    }

    /// <summary>The banner over the editors says the open folder is untrusted, while it is.</summary>
    private void ShowTrust() => TrustBanner.Say(session.Folder is not null && !session.Trusted ? Refusals.UntrustedSaid : null);

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
        WorkspaceTrustDialog dialog = new(folder);
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
                    TrustFolder(folder);
                    break;
                case TrustAnswer.ParentTrusted:
                    TrustFolder(WorkspaceTrustDialog.ParentOf(folder) ?? folder);
                    break;
                case TrustAnswer.Untrusted when keepsWorkspace:
                    state!.Trust.Set(state.Trust.Value.Declined(folder));
                    break;
            }
        };
        IdeErrors.Observe(dialog.ShowDialog(this));
    }

    /// <summary>Trusts a folder, with every folder under it, kept in the person's state where the window keeps it: every
    /// window's session of it begins what it held back (<see cref="FollowTrust"/>).</summary>
    private void TrustFolder(string folder)
    {
        UiThread.Check();
        if (keepsWorkspace)
        {
            state!.Trust.Set(state.Trust.Value.With(folder));
        }
        WorkspaceTrust.Trust(folder);
    }
}
