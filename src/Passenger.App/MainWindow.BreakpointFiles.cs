using Avalonia.Platform.Storage;
using Passenger.App.Debugging;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App;

/// <summary>The folder's breakpoints as a file of their own (<see cref="BreakpointFiles"/>): Export Breakpoints writes them
/// where the person names, Import Breakpoints adds a file's beside those there are.</summary>
public partial class MainWindow
{
    /// <summary>Asks the person where to export the breakpoints, in the system's own panel, starting in the open folder; null
    /// where they chose nowhere.</summary>
    public Func<Task<string?>> PickBreakpointsExport
    {
        get => pickBreakpointsExport ?? (() => PickBreakpointsFileAsync(save: true));
        set => pickBreakpointsExport = value;
    }

    /// <summary>Asks the person for a file of breakpoints to import; null where they chose none.</summary>
    public Func<Task<string?>> PickBreakpointsImport
    {
        get => pickBreakpointsImport ?? (() => PickBreakpointsFileAsync(save: false));
        set => pickBreakpointsImport = value;
    }

    private Func<Task<string?>>? pickBreakpointsExport;
    private Func<Task<string?>>? pickBreakpointsImport;

    /// <summary>Export Breakpoints: every line, method and exception breakpoint written where the person names, as one step
    /// of Undo where that is the folder's.</summary>
    private async Task ExportBreakpointsAsync()
    {
        if (Folder is not { } folder || await PickBreakpointsExport() is not { } path)
        {
            return;
        }
        string text = BreakpointFiles.Write(session.Debug.Breakpoints.Capture(), folder);
        string? refused = await ChangeFileAsync("Export Breakpoints", [path], () =>
        {
            if (File.Exists(path) && FileText.Load(path) is { } had)
            {
                _ = FileText.SaveOver(path, had, text);
            }
            else
            {
                _ = FileText.SaveNew(path, text);
            }
            return null;
        });
        Say(refused is null ? $"Breakpoints exported to {PathNames.Shown(path, folder)}" : $"The breakpoints could not be exported: {refused}");
    }

    /// <summary>Import Breakpoints: the breakpoints of a file the person picks added beside those there are, each file it
    /// names found from the open folder; a file that is none says why.</summary>
    private async Task ImportBreakpointsAsync()
    {
        if (Folder is not { } folder || await PickBreakpointsImport() is not { } path)
        {
            return;
        }
        BreakpointsState read;
        try
        {
            string text = await BackgroundWork.Run(() => FileText.Load(path)?.Text) ?? throw new FormatException("It could not be read");
            read = BreakpointFiles.Read(text, folder);
        }
        catch (Exception unread) when (unread is FormatException or IOException or UnauthorizedAccessException)
        {
            Say($"{PathNames.Shown(path, folder)} could not be imported: {unread.Message}");
            return;
        }
        int added = session.Debug.Breakpoints.Import(read);
        Say(added == 0 ? $"{PathNames.Shown(path, folder)} held no breakpoint that is not set already"
            : $"Imported {Plural.Of(added, "breakpoint")} from {PathNames.Shown(path, folder)}");
    }

    private async Task<string?> PickBreakpointsFileAsync(bool save)
    {
        FilePickerFileType type = new("Breakpoints") { Patterns = ["*.json"] };
        IStorageFolder? start = Folder is { } folder ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;
        if (save)
        {
            IStorageFile? picked = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Breakpoints",
                SuggestedFileName = "breakpoints.json",
                DefaultExtension = "json",
                FileTypeChoices = [type],
                SuggestedStartLocation = start,
            });
            return picked?.TryGetLocalPath();
        }
        IReadOnlyList<IStorageFile> chosen = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Breakpoints",
            FileTypeFilter = [type],
            SuggestedStartLocation = start,
        });
        return chosen.Count == 1 ? chosen[0].TryGetLocalPath() : null;
    }
}
