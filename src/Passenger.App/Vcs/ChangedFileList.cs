using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Passenger.App.ToolWindows;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>A list of the files a revision changed as the Git window shows one (a commit's, a branch's beside the working
/// tree): a <see cref="ChangedFileRow"/> each, its folder from the repository's top; Enter, a double click or Show Diff's
/// keys ask for the chosen file's diff, F4 for the file as it is now (none for a file deleted), and its menu offers those,
/// what its owner adds after Show Diff, and Copy Path.</summary>
public sealed class ChangedFileList
{
    private readonly Func<Git?> repository;

    /// <param name="repository">The repository the files are of, as it stands when a row is shown.</param>
    /// <param name="beside">What the menu offers after Show Diff for the chosen file, its owner's own.</param>
    public ChangedFileList(Func<Git?> repository, Func<FileChange, IEnumerable<Control>>? beside = null)
    {
        this.repository = repository;
        List = new ListBox { Classes = { StyleClasses.Tree }, ItemsSource = Files };
        ReusedRows.Use(List, Row);
        ListKeys.Act(List, new ListActs
        {
            Open = AskDiff,
            Source = () =>
            {
                if (Chosen is { Kind: not ChangeKind.Deleted } shown)
                {
                    OpenRequested?.Invoke(this, shown.Path);
                }
            },
            Keys = e =>
            {
                bool diff = AppActions.Presses(AppActions.ShowDiff, e);
                if (diff)
                {
                    AskDiff();
                }
                return diff;
            },
        });
        ListMenus.Attach(List, () => Chosen is not { } change ? [] :
        [
            ListMenus.Item("Show Diff", "Enter", () => DiffRequested?.Invoke(this, change)),
            .. beside?.Invoke(change) ?? [],
            ListMenus.Item("Jump to Source", "F4", () => OpenRequested?.Invoke(this, change.Path), change.Kind is not ChangeKind.Deleted),
            new Separator(),
            ListMenus.Item("Copy Path", null, () => IdeErrors.Observe(ListMenus.CopyAsync(List, change.Path))),
        ]);
    }

    /// <summary>The list, for its owner to lay out.</summary>
    public ListBox List { get; }

    /// <summary>The files listed, which the owner fills.</summary>
    public ObservableCollection<FileChange> Files { get; } = [];

    /// <summary>The file chosen, if any.</summary>
    public FileChange? Chosen => List.SelectedItem as FileChange;

    /// <summary>The chosen file's diff asked for.</summary>
    public event EventHandler<FileChange>? DiffRequested;

    /// <summary>A file asked for as it is now, by its path.</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>Show Diff's keys while the list has the keys and a file is chosen, ahead of what they run elsewhere
    /// (<see cref="AppActions.KeysIn"/>).</summary>
    public bool TakesKey(KeyEventArgs e) => List.IsKeyboardFocusWithin && Chosen is not null && AppActions.Presses(AppActions.ShowDiff, e);

    private void AskDiff()
    {
        if (Chosen is { } change)
        {
            DiffRequested?.Invoke(this, change);
        }
    }

    /// <summary>A file's row for whichever file the list hands its container.</summary>
    private Control Row()
    {
        ChangedFileRow row = new();
        return row.Line.Showing<FileChange>(change => row.Show(change, repository()));
    }
}
