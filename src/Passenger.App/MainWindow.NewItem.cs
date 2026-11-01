using Passenger.App.Editors;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Add, New Item: dotnet new's item templates and Passenger's own in <see cref="NewItemDialog"/>, for the folder
/// of the Explorer's row (or of the file shown, else the open folder); what it makes is one command Undo takes back whole,
/// its files opened.</summary>
public partial class MainWindow
{
    /// <summary>The last New Item's adding of what its dialog made, for a test to wait on: null once added, else why not.</summary>
    public Task<string?>? ItemAdded { get; private set; }

    private void AddNewItemAction()
    {
        actions!.Add(new IdeAction(AppActions.NewItem, "New Item…", ExplorerGroup, c => IdeErrors.Observe(ShowNewItemAsync(NewItemPlace(c)!)))
        {
            WhyNot = c => RowOf(c) is { } row ? NewGoesIn(row) is null ? $"Nothing new goes in {row.Name}" : null
                : NewItemPlace(c) is null ? Refusals.NoFolder : null,
            // Shown in the Explorer's menus, where it takes the keys.
            KeysOf = () => AppActions.KeysIn(AppActions.NewItem) is var keys && keys != AppActions.NewItem ? keys : null,
        });
    }

    /// <summary>Where New Item makes things: where something new goes from the Explorer's row, else the folder of the file
    /// shown, else the open folder.</summary>
    private string? NewItemPlace(ActionContext context) => RowOf(context) is { } row ? NewGoesIn(row)
        : context.File?.Path is { } file && Path.IsPathRooted(file) && Path.GetDirectoryName(file) is { } beside && Directory.Exists(beside) ? beside
        : Folder;

    /// <summary>New Item for a folder: its project's language and kind read (its own Razor files looked for off the window's
    /// thread), then the dialog over this window, and what it made added once it closes.</summary>
    public async Task<NewItemDialog> ShowNewItemAsync(string folder)
    {
        string full = PathNames.Canonical(folder);
        ProjectModel? project = ProjectOf(full);
        string? language = project is null ? null : Path.GetExtension(project.Path).ToLowerInvariant() switch
        {
            ".fsproj" => "F#",
            ".vbproj" => "VB",
            _ => "C#",
        };
        IReadOnlyList<string> first = [];
        if (project is { IsWeb: true } web)
        {
            bool razor = web.RunsInBrowser || await BackgroundWork.Run(() =>
                PathNames.FilesUnder(web.Folder, ProjectFolders.IsOwn).Any(f => f.Files.Any(n => n.EndsWith(RazorFiles.Component, StringComparison.OrdinalIgnoreCase))));
            first = razor ? ["razorcomponent", "page"] : ["apicontroller", "mvccontroller"];
        }
        string read = Solution is { } solution ? Path.GetDirectoryName(solution.Path)! : Folder ?? full;
        NewItemDialog dialog = new(new ItemTarget(full, PathNames.Shown(full, Folder), language, NamespaceFor(full), first), read);
        dialog.Closed += (_, _) =>
        {
            if (dialog.Made is { } made)
            {
                Task<string?> adding = AddItemAsync(made);
                ItemAdded = adding;
                IdeErrors.Observe(adding);
            }
        };
        IdeErrors.Observe(dialog.ShowDialog(this));
        return dialog;
    }

    /// <summary>What New Item made, one command Undo takes back whole: Passenger's own file written, or what dotnet new
    /// wrote taken in (its files made, its folders made whole), then followed and opened, the first in front. Null once
    /// added, else why not.</summary>
    private async Task<string?> AddItemAsync(MadeItem made)
    {
        List<string> files = [.. made.Files];
        using (CommandHistory.Recording recording = Editors.Commands.RecordWrites($"New {made.Template} {made.Name}"))
        {
            if (made.Own is { } own)
            {
                recording.Made(own.Path);
                try
                {
                    Following([own.Path], () => FileText.SaveNew(own.Path, own.Text));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return status.Text = await recording.TakenBackAsync(NotMade(Path.GetFileName(own.Path), error));
                }
                files.Add(PathNames.Canonical(own.Path));
            }
            foreach (string file in made.Files)
            {
                recording.Made(file);
            }
            foreach (string folder in made.Folders)
            {
                recording.Leave(folder);
                recording.Also(await MadeFolderAsync(folder));
                files.AddRange(await BackgroundWork.Run(() => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(PathNames.Canonical).Order(StringComparer.Ordinal).ToList()));
            }
            if (made.Own is null)
            {
                CameOrWent([.. made.Files, .. made.Folders]);
            }
        }
        // Opened last first, so the first is in front.
        foreach (string file in Enumerable.Reverse(files))
        {
            OpenFile(file);
        }
        if (files.Count > 0)
        {
            Explorer.Reveal(files[0]);
        }
        status.Text = $"New {made.Template}: made {Plural.Listed([.. files.Select(f => PathNames.Shown(f, Folder))])}";
        return null;
    }
}
