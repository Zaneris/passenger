using Passenger.App.Updates;
using Passenger.Packages;

namespace Passenger.App;

/// <summary>What's New: the running version's release notes, carried beside the app (<see cref="ReleaseNotes"/>), in an
/// editor tab of their own, rendered and read-only. The person's window shows them once after an update, as it starts, in
/// front of the folder's own tabs once those are back; Help, What's New shows them whenever asked, Find Action and an
/// agent's ide_action too.</summary>
public partial class MainWindow
{
    /// <summary>The notes the window shows: this build's, by the version it is, unless others are given.</summary>
    public ReleaseNotes Notes { get; init; } = ReleaseNotes.Carried;

    private void AddWhatsNew()
    {
        actions!.Add(new IdeAction(AppActions.WhatsNew, "What's New", "Help", _ => ShowWhatsNew())
        {
            WhyNot = _ => Notes.Exists ? null : $"Passenger {Notes.Version} carries no release notes",
            Menus = [new MenuPlace("Help", 3, 0)],
            Words = ["Release Notes"],
        });
        Editors.BrowseRequested += (_, address) => Browse(address);
    }

    /// <summary>The notes in their tab, open or opened now; null once shown, else why not, which the status bar says too.</summary>
    public string? ShowWhatsNew()
    {
        if (!Notes.Exists)
        {
            status.Text = $"Passenger {Notes.Version} carries no release notes";
            return status.Text;
        }
        return OpenFile(Notes.File);
    }

    /// <summary>The person's window as it starts (App, never a headless or agent's window): the notes of the version it
    /// is, once, when the state it keeps says an older Passenger ran here, its record of the notes shown older or
    /// missing from an older Passenger's state, recorded as shown then. A folder the start is still opening comes back
    /// with its own tabs behind them (<c>EditorArea.Restore</c>). A fresh state records this version and shows nothing:
    /// the Welcome screen greets a first start.</summary>
    public void ShowWhatsNewOnce()
    {
        if (!keepsSettings)
        {
            return;
        }
        UpdatesState kept = state!.Updates.Value;
        if (kept.NotesShown is not { } shown)
        {
            if (!Store().UsedBefore)
            {
                state.Updates.Set(kept with { NotesShown = Notes.Version });
                IdeErrors.Write($"What's New: a fresh state, which begins on Passenger {Notes.Version} with no notes shown");
                return;
            }
        }
        else if (VersionOrder.Instance.Compare(Notes.Version, shown) <= 0)
        {
            return;
        }
        if (!Notes.Exists)
        {
            IdeErrors.Write($"What's New: Passenger {Notes.Version} carries no release notes to show after {kept.NotesShown ?? "an older Passenger"}");
            return;
        }
        if (ShowWhatsNew() is null)
        {
            state.Updates.Set(kept with { NotesShown = Notes.Version });
            IdeErrors.Write($"What's New: Passenger {Notes.Version}'s notes shown, after {kept.NotesShown ?? "an older Passenger"}");
        }
    }
}
