using Avalonia.Controls;
using Passenger.App.Workspace;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Clear Read-Only Status: a file marked read-only (a Perforce or TFVC file not checked out, one made a-w) is
/// never written by a save, on any system (<see cref="ReadOnlyFileException"/>). Its first edit asks whether to make it
/// writable; Save and Save All ask again of one still read-only; and closing the window with changes it cannot save
/// asks once whether to make them writable or close without them, rather than stay open.</summary>
public partial class MainWindow
{
    /// <summary>Files the person chose to close the window without saving.</summary>
    private readonly HashSet<OpenDocument> leftUnsaved = [];

    private bool askingWritable;

    /// <summary>Whether the window, closed with changes it could not save, is asking the person what to do with them.</summary>
    internal bool AsksBeforeClosing { get; private set; }

    private void AddReadOnly() => Editors.Opened += (_, document) =>
    {
        bool modified = document.IsModified;
        document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(OpenDocument.IsModified))
            {
                return;
            }
            bool was = modified;
            modified = document.IsModified;
            if (modified && !was && !document.IsReadOnly && FileText.IsReadOnly(document.Path))
            {
                IdeErrors.Observe(MakeWritableAsync([document]));
            }
        };
    };

    /// <summary>Asks whether to make read-only files writable, and makes them so when the person agrees; whether every one
    /// is writable now. Kept read-only, their changes stay unsaved in the editor, and each save says why.</summary>
    private async Task<bool> MakeWritableAsync(IReadOnlyList<OpenDocument> files)
    {
        if (askingWritable || files.Count == 0)
        {
            return false;
        }
        askingWritable = true;
        try
        {
            string text = files.Count == 1
                ? $"{files[0].Name} is read-only, so its changes cannot be saved. Make it writable?"
                : $"These files are read-only, so their changes cannot be saved:\n{string.Join('\n', files.Select(f => f.Name))}\nMake them writable?";
            if (!await Confirm("Clear Read-Only Status", text, "Make Writable"))
            {
                status.Text = files.Count == 1 ? $"{files[0].Name} is read-only: its changes are not saved" : "Read-only files' changes are not saved";
                return false;
            }
            return MakeWritable(files);
        }
        finally
        {
            askingWritable = false;
        }
    }

    private bool MakeWritable(IEnumerable<OpenDocument> files)
    {
        foreach (OpenDocument document in files)
        {
            try
            {
                FileText.MakeWritable(document.Path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                status.Text = $"{document.Name} could not be made writable: {error.Message}";
                return false;
            }
        }
        return true;
    }

    /// <summary>Save or Save All left read-only files unsaved: asked, those made writable are saved now.</summary>
    private async Task SaveReadOnlyAsync(IEnumerable<OpenDocument> saving)
    {
        List<OpenDocument> locked = [.. saving.Where(d => d.IsModified && Editors.Documents.Contains(d) && FileText.IsReadOnly(d.Path))];
        if (await MakeWritableAsync(locked))
        {
            foreach (OpenDocument document in locked)
            {
                Editors.Save(document);
            }
        }
    }

    /// <summary>The window was closed with changes it could not save (read-only, or refused by the system): rather than
    /// staying open with no way to close, it asks once, Make Writable for read-only ones, else Close Without Saving them,
    /// or Cancel to keep it open with their changes; then it closes again, saving what it now can, and a quit it held
    /// carries on (<see cref="PassengerApp.Answered"/>).</summary>
    private async Task CloseUnsavedAsync(bool quit)
    {
        List<OpenDocument> unsaved = [.. Editors.Documents.Where(d => d.IsModified && !conflicted.Contains(d) && !leftUnsaved.Contains(d))];
        if (unsaved.Count == 0 || askingWritable)
        {
            return;
        }
        askingWritable = true;
        AsksBeforeClosing = true;
        string? answer;
        List<OpenDocument> locked = [.. unsaved.Where(d => FileText.IsReadOnly(d.Path))];
        try
        {
            string names = string.Join('\n', unsaved.Select(d => locked.Contains(d) ? $"{d.Name} (read-only)" : d.Name));
            answer = await Choose(locked.Count > 0 ? "Clear Read-Only Status" : "Unsaved Changes",
                $"{(unsaved.Count == 1 ? "This file's changes" : "These files' changes")} could not be saved:\n{names}",
                locked.Count > 0 ? ["Make Writable", "Close Without Saving"] : ["Close Without Saving"], locked.Count > 0 ? "Make Writable" : null);
        }
        finally
        {
            askingWritable = false;
            AsksBeforeClosing = false;
        }
        bool closes = answer == "Close Without Saving" || (answer == "Make Writable" && MakeWritable(locked));
        if (answer == "Close Without Saving")
        {
            leftUnsaved.UnionWith(unsaved);
        }
        if (quit)
        {
            PassengerApp.Answered(carryOn: closes);
        }
        else if (closes)
        {
            Close();
        }
    }
}
