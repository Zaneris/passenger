using Avalonia.Controls;
using Avalonia.Threading;
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

    /// <summary>Saves before anything else hears of the close, so the language server and the rest go only once the
    /// edits are on disk; a file that cannot be written keeps the window open, its edits with it, and asks whether to make
    /// it writable or close without it (<see cref="CloseUnsavedAsync"/>).</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Passenger's quit (File, Exit; Restart to Update) and the system's (the application menu's Quit) keep each solution.
        bool quit = PassengerApp.Quitting || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown;
        if (Autosaves && !(Editors.SaveAll(new HashSet<OpenDocument>(conflicted.Concat(leftUnsaved))) && conflicted.Count == 0))
        {
            e.Cancel = true;
            IdeErrors.Observe(CloseUnsavedAsync(quit));
            return;
        }
        quitting = quit;
        base.OnClosing(e);
    }

    /// <summary>File, Save (the file shown as it is pressed) or Save All: each changed file first gets what Settings
    /// ask of a save (Run eslint --fix on save, then Prettier's Run on save), which an explicit save runs and autosave
    /// never does, each one undoable edit. The files are saved whatever came of those.</summary>
    private async Task SaveExplicitly(bool all)
    {
        OpenDocument? pressed = all ? null : Editors.Active;
        List<OpenDocument> saving = all ? [.. Editors.Documents.Where(d => d.IsModified)] : pressed is { IsModified: true } ? [pressed] : [];
        BackgroundWork.Begin();
        try
        {
            foreach (OpenDocument document in saving.Where(d => Editors.Documents.Contains(d)).ToList())
            {
                try
                {
                    await assist.BeforeSave(document);
                }
                catch (Exception error)
                {
                    // The save goes on without what failed, which IDE Internal Errors shows.
                    IdeErrors.Report(error);
                }
            }
            if (all)
            {
                Editors.SaveAll(asked: true);
            }
            else if (pressed is not null)
            {
                Editors.SaveAsked(pressed);
            }
        }
        finally
        {
            BackgroundWork.End();
        }
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
