using Avalonia.Controls;
using Avalonia.Threading;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Autosave: an edit is never lost and closing never asks. Every changed file is saved before the window
/// closes (quitting included) or another folder opens, and, each as Settings says, when the focus goes to another
/// program or to the terminal and once the IDE has been idle a while. Nor is a character the file's encoding cannot
/// hold: the file is saved as UTF-8 instead, and the status bar says so. A file whose folder another program moved or
/// deleted is never written back there but by the person's Save, which the status bar says.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer idleSave = new();

    /// <summary>Whether changed files are saved without being asked; off for a screenshot, which only looks.</summary>
    public bool Autosaves { get; set; } = true;

    private void AddAutosave()
    {
        idleSave.Tick += (_, _) =>
        {
            idleSave.Stop();
            if (settings.SaveWhenIdle)
            {
                Autosave();
            }
        };
        // Leaving Passenger saves (OnLeft), as does going to its terminal; going to one of its own windows does not.
        Editors.Opened += (_, document) => document.Text.Changed += (_, _) => RestartIdleSave();
        Editors.Opened += (_, document) =>
        {
            document.Converted += (_, message) => status.Text = message;
            // The status bar's encoding follows a file's: saved as UTF-8, or a BOM added on disk.
            document.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OpenDocument.EncodingName) && Editors.Active == document)
                {
                    status.ShowStorage(document);
                }
            };
        };
    }

    private void SaveOnLeaving()
    {
        if (settings.SaveOnDeactivation)
        {
            Autosave();
        }
    }

    private void RestartIdleSave()
    {
        idleSave.Stop();
        if (settings.SaveWhenIdle && Editors.Documents.Any(d => d.IsModified))
        {
            idleSave.Interval = TimeSpan.FromSeconds(Math.Max(1, settings.IdleSeconds));
            idleSave.Start();
        }
    }

    /// <summary>Saves before anything else hears of the close (but a close refused to an agent's input, <see
    /// cref="CloseRefused"/>, or asked about first, <see cref="ConfirmFirst"/>); a file that cannot be written keeps the window
    /// open and asks whether to make it writable or close without it (<see cref="CloseUnsavedAsync"/>). A save still landing
    /// holds the close until it has (<see cref="CloseLandedAsync"/>); programs Hot Reload changed, runs Detach let go of and
    /// database changes not written are asked about (<see cref="CloseHeldAsync"/>, <see cref="CloseLetGoAsync"/>, <see cref="CloseDatabaseAsync"/>).</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (CloseRefused())
        {
            e.Cancel = true;
            return;
        }
        if (ConfirmFirst(e) is { } asking)
        {
            e.Cancel = true;
            IdeErrors.Observe(asking);
            return;
        }
        // Passenger's quit (File, Exit; Restart to Update) and the system's (the application menu's Quit) keep each solution.
        bool quit = PassengerApp.Quitting || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown;
        if (Autosaves && !(Editors.SaveAll(new HashSet<OpenDocument>(conflicted.Concat(leftUnsaved))) && conflicted.Count == 0))
        {
            e.Cancel = true;
            IdeErrors.Observe(CloseUnsavedAsync(quit));
            return;
        }
        if (Editors.Documents.Select(d => d.Landing).OfType<Task<bool>>().Where(t => !t.IsCompleted).ToList() is { Count: > 0 } landing)
        {
            e.Cancel = true;
            IdeErrors.Observe(CloseLandedAsync(landing, quit));
            return;
        }
        // Nobody is at an agent's own IDE to ask, nor does the system's shutdown wait for an answer: its close stops them unasked.
        if (e.CloseReason != WindowCloseReason.OSShutdown && AsksOfHeld)
        {
            e.Cancel = true;
            IdeErrors.Observe(CloseHeldAsync(HeldByLeaving(), quit));
            return;
        }
        // The containers of runs Detach let go of: stopped with the folder, or left running as the person's.
        if (e.CloseReason != WindowCloseReason.OSShutdown && AsksOfLetGo)
        {
            e.Cancel = true;
            IdeErrors.Observe(CloseLetGoAsync(quit));
            return;
        }
        // A table's changes not submitted and a console's open transaction: written, or dropped, as the person answers.
        if (e.CloseReason != WindowCloseReason.OSShutdown && AsksOfDatabase)
        {
            e.Cancel = true;
            IdeErrors.Observe(CloseDatabaseAsync(quit));
            return;
        }
        quitting = quit;
        base.OnClosing(e);
    }

    /// <summary>The window was closed while saves another program held their files against were still landing: it closes
    /// once they have, its thread free meanwhile, saving what was typed since, or asks about the changes of those that
    /// never did (<see cref="CloseUnsavedAsync"/>).</summary>
    private async Task CloseLandedAsync(IReadOnlyList<Task<bool>> landing, bool quit)
    {
        bool[] landed;
        ClosesLater = true;
        try
        {
            landed = await Task.WhenAll(landing);
        }
        finally
        {
            ClosesLater = false;
        }
        if (landed.Contains(false))
        {
            await CloseUnsavedAsync(quit);
        }
        else if (quit)
        {
            PassengerApp.Answered(carryOn: true);
        }
        else
        {
            CloseAsAnswered();
        }
    }

    /// <summary>File, Save (the file shown as it is pressed) or Save All: each changed file first gets what Settings
    /// ask of a save (Run eslint --fix on save, then Prettier's Run on save, or a GDScript's Reformat on save, else
    /// Reformat on save), which an explicit save runs and autosave never does, each one undoable edit. The files are saved
    /// whatever came of those, a file Reformat on save left as it was said once. An agent's ide_action that ran it answers
    /// once its saves have landed (<see cref="ActionWork.Unseen"/>), the person seeing nothing of it.</summary>
    private async Task SaveExplicitly(bool all)
    {
        using Working underWay = work.Unseen(all ? AppActions.SaveAll : AppActions.Save);
        OpenDocument? pressed = all ? null : Editors.Active?.Saves;
        List<OpenDocument> saving = all ? [.. Editors.Documents.Where(d => d.IsModified)] : pressed is { IsModified: true } ? [pressed] : [];
        string? held = await Editors.ClearAsync([.. saving.Select(d => d.Path)]);
        if (held is not null)
        {
            // The rest are saved as asked, those held left as they are.
            status.Text = held;
            saving.RemoveAll(d => Editors.HeldElsewhere(d.Path) is not null);
        }
        List<string> unformatted = [];
        BackgroundWork.Begin();
        try
        {
            foreach (OpenDocument document in saving.Where(d => Editors.Documents.Contains(d)).ToList())
            {
                try
                {
                    if (await assist.BeforeSave(document) is { } why)
                    {
                        unformatted.Add(why);
                    }
                }
                catch (Exception error)
                {
                    // The save goes on without what failed, which IDE Internal Errors shows.
                    IdeErrors.Report(error);
                }
            }
            if (unformatted.Count > 0)
            {
                status.Text = unformatted.Count == 1 ? unformatted[0] : $"{unformatted[0]} (and {Plural.Of(unformatted.Count - 1, "more file")})";
            }
            if (all && held is null)
            {
                Editors.SaveAll(asked: true);
            }
            else
            {
                foreach (OpenDocument document in saving.Where(d => d.IsModified))
                {
                    Editors.SaveAsked(document);
                }
            }
        }
        finally
        {
            BackgroundWork.End();
        }
        if (held is not null || saving.Any(d => d.IsModified && Editors.Documents.Contains(d)))
        {
            Cue(SoundCue.SaveRefused);
        }
        if (ActionWork.Heard is not null)
        {
            await EditorArea.LandedAsync(saving.Select(d => d.Path), null);
        }
        AskSaveFormatting(saving);
        // Not held while the person is asked, so a screenshot or a test finds the question.
        await SaveReadOnlyAsync(saving);
    }

    /// <summary>Saves every changed file but those awaiting a File Cache Conflict answer, unless this window does not
    /// autosave; whether nothing was left unsaved.</summary>
    private bool Autosave() => !Autosaves || (Editors.SaveAll(conflicted) && conflicted.Count == 0);

    /// <summary>Whether one of Passenger's own windows has the focus (this one, a dialog over it, another window).</summary>
    private bool OwnWindowActive() =>
        IsActive || OwnedWindows.Any(w => w.IsActive)
        || (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.Windows.Any(w => w.IsActive));
}
