using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Add, New Item: dotnet new's item templates and Passenger's own in <see cref="NewItemDialog"/>, for the folder
/// of the Explorer's row (or of the file shown, else the open folder); and Add's New, the common kinds among them made from
/// a name alone. What either makes is one command Undo takes back whole, its files opened.</summary>
public partial class MainWindow
{
    /// <summary>The last New Item's or Add's New's adding of what dotnet new made, for a test to wait on: null once added,
    /// else why not.</summary>
    public Task<string?>? ItemAdded { get; private set; }

    private void AddNewItemAction()
    {
        actions!.Add(new IdeAction(AppActions.NewItem, ActionTitles.NewItem, ActionGroups.Explorer, c => ShowNewItem(NewItemPlace(c)!))
        {
            WhyNot = c => NewItemPlace(c) is { } place ? ExplorerNode.KeptOut(place)
                : RowOf(c) is { } row ? $"Nothing new goes in {row.Name}" : Refusals.NoFolder,
            // Shown in the Explorer's menus, where it takes the keys.
            KeysOf = () => AppActions.KeysIn(AppActions.NewItem) is var keys && keys != AppActions.NewItem ? keys : null,
        });
    }

    /// <summary>Where New Item makes things: where something new goes from the Explorer's row, else the folder of the file
    /// shown, else the open folder.</summary>
    private string? NewItemPlace(ActionContext context) => RowOf(context) is { } row ? NewGoesIn(row)
        : context.File?.Path is { } file && Path.IsPathRooted(file) && Path.GetDirectoryName(file) is { } beside && Directory.Exists(beside) ? beside
        : Folder;

    /// <summary>New Item for a folder: the dialog over this window, listing what the folder takes (<see cref="ItemTargetOf"/>),
    /// and what it made added once it closes.</summary>
    public NewItemDialog ShowNewItem(string folder)
    {
        NewItemDialog dialog = new(ItemTargetOf(folder), TemplatesFolder(folder));
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

    /// <summary>What a folder takes, as New Item and Add's New read it: its project's language (C# but for an F# or VB
    /// project's; none outside a project), the namespace its code takes, and the items its project's kind puts first.</summary>
    private ItemTarget ItemTargetOf(string folder)
    {
        string full = PathNames.Canonical(folder);
        ProjectModel? project = ProjectOf(full);
        string? language = project is null ? null : Path.GetExtension(project.Path).ToLowerInvariant() switch
        {
            ".fsproj" => "F#",
            ".vbproj" => "VB",
            _ => "C#",
        };
        return new ItemTarget(full, PathNames.Shown(full, Folder), language, NamespaceFor(full), ItemKinds.FirstOf(project));
    }

    /// <summary>The folder whose SDK's templates are read: the solution's, else the open folder, else the one given.</summary>
    private string TemplatesFolder(string folder) => Solution is { } solution ? Path.GetDirectoryName(solution.Path)! : Folder ?? folder;

    /// <summary>Add's New for a row, as its submenu opens: the common kinds its folder takes (<see cref="ItemKinds.Common"/>)
    /// of the templates read so far, each asking a name alone and made where New Type and New Item make them, a group
    /// apart from the one before; every one greyed saying why where nothing new goes.</summary>
    private IReadOnlyList<ListedItem> NewKinds(ExplorerNode? row)
    {
        if (row is null || NewGoesIn(row) is not { } into)
        {
            return [];
        }
        string? whyNot = ExplorerNode.KeptOut(into);
        ItemTarget target = ItemTargetOf(into);
        List<ListedItem> kinds = [];
        foreach (IReadOnlyList<ProjectTemplate> group in ItemKinds.Common(target, ProjectTemplates.Ready(TemplatesFolder(into))))
        {
            bool apart = kinds.Count > 0;
            kinds.AddRange(group.Select((kind, at) => new ListedItem(kind.Name, () => AskKind(target, kind)) { WhyNot = whyNot, Apart = apart && at == 0 }));
        }
        return kinds;
    }

    /// <summary>A kind of Add's New chosen: its name asked alone, then made as New Type makes Passenger's own (<see
    /// cref="CreateOwn"/>), else by dotnet new as New Item runs it, with the template's defaults and the folder's
    /// namespace where it takes one, what it made added as New Item adds it.</summary>
    private void AskKind(ItemTarget target, ProjectTemplate kind)
    {
        if (kind.Own is { } own)
        {
            AskName($"New {kind.Name}", string.Empty, name => CreateOwn(target.Folder, name, own));
            return;
        }
        TemplateVariant variant = kind.Variant(target.Language, null);
        string? space = ItemKinds.TakesNamespace(variant) ? target.Namespace : null;
        // Closed while dotnet new runs, what it made meanwhile goes.
        CancellationTokenSource closed = new();
        Vcs.NamePopup popup = new($"New {kind.Name}", string.Empty, async name =>
        {
            if (ProjectNames.Problem(name, "Item", namesCode: false) is { } problem)
            {
                return problem;
            }
            MadeItem? made;
            string? whyNot;
            try
            {
                (made, whyNot) = await ItemKinds.RunAsync(kind, target.Folder, name, ItemKinds.Arguments(kind, variant, name, target.Folder, space, []), closed.Token);
            }
            catch (OperationCanceledException)
            {
                return $"{name} was not made";
            }
            if (made is null)
            {
                return whyNot;
            }
            Task<string?> adding = AddItemAsync(made);
            ItemAdded = adding;
            return await adding;
        }, working: name => $"dotnet new {kind.ShortName} is making {name}…");
        popup.DetachedFromVisualTree += (_, _) => closed.Cancel();
        popups.Show(popup, _ => { });
    }

    /// <summary>What New Item made, one command Undo takes back whole: Passenger's own file written, or what dotnet new
    /// wrote taken in (its files made, its folders made whole, those in a shared project's folder written into its items
    /// file, the item of a folder its project kept empty taken out), then followed and opened, the first in front. Null once
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
            if ((IntoShared(files, recording) ?? KeepFolders([], [.. files, .. made.Folders.Select(PathNames.Canonical)], [], recording)) is { } unshared)
            {
                status.Text = unshared;
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
