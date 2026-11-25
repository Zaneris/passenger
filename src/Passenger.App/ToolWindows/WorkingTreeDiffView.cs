using Avalonia.Controls;
using Avalonia.Input;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App.ToolWindows;

/// <summary>A tab of the Git window, Show Diff with Working Tree's: the files that differ between a branch and the working
/// tree, or those a stash holds, read again while shown as the working tree changes, the first chosen; a file's diff (the
/// branch's or stash's text beside the file's) opens wherever Settings opens diffs, the list staying, and a branch's file's
/// menu's Get from takes the branch's version whole. Where nothing differs it says so.</summary>
public sealed class WorkingTreeDiffView : Panel, IOwnKeys
{
    private readonly VcsController vcs;
    private readonly ChangedFileList files;
    private readonly EmptyState placeholder = new() { IsVisible = false };
    private readonly EventHandler onChanged;
    private bool stale = true;
    private bool read;
    private int loads;

    public WorkingTreeDiffView(VcsController git, WorkingTreeComparison compared)
    {
        vcs = git;
        Compared = compared;
        files = compared.Stash is null
            ? new ChangedFileList(() => vcs.Repository, change => [ListMenus.Item($"Get from '{compared.Name}'", () => GetRequested?.Invoke(this, change))])
            : new ChangedFileList(() => vcs.Repository);
        files.DiffRequested += (_, change) => DiffRequested?.Invoke(this, change);
        files.OpenRequested += (_, path) => OpenRequested?.Invoke(this, path);
        Children.Add(files.List);
        Children.Add(placeholder);
        onChanged = (_, _) =>
        {
            // Read again now when shown, else when it next is: a tab not shown is out of the visual tree.
            stale = true;
            if (VisualRoot is not null)
            {
                IdeErrors.Observe(LoadAsync());
            }
        };
        vcs.Changed += onChanged;
        AttachedToVisualTree += (_, _) =>
        {
            if (stale)
            {
                IdeErrors.Observe(LoadAsync());
            }
        };
    }

    /// <summary>The branch or stash the working tree is compared with.</summary>
    public WorkingTreeComparison Compared { get; }

    /// <summary>The files that differ, as last read.</summary>
    public IReadOnlyList<FileChange> Files => files.Files;

    /// <summary>The list of files, its rows the person's to choose.</summary>
    public ListBox List => files.List;

    /// <summary>What it says while nothing differs.</summary>
    public string? Placeholder => placeholder.IsVisible ? placeholder.Text : null;

    /// <summary>A file's diff asked for: the branch's or stash's text beside the file's.</summary>
    public event EventHandler<FileChange>? DiffRequested;

    /// <summary>A file to take the branch's version of, whole; a stash's tab asks for none.</summary>
    public event EventHandler<FileChange>? GetRequested;

    /// <summary>A file asked for as it is now, by its path.</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>Reads the files that differ again, keeping the file chosen; a newer read overtakes an older one.</summary>
    public async Task LoadAsync()
    {
        int load = ++loads;
        stale = false;
        IReadOnlyList<FileChange> changed = await vcs.ChangesSinceAsync(Compared);
        if (load != loads)
        {
            return;
        }
        bool first = !read;
        read = true;
        placeholder.Text = vcs.Repository is null ? vcs.NoRepository : "No differences";
        placeholder.IsVisible = changed.Count == 0;
        // The same files again leave the rows, their choice and their keys as they are.
        if (!changed.SequenceEqual(files.Files))
        {
            string? chosen = files.Chosen?.Path;
            files.Files.Clear();
            foreach (FileChange change in changed)
            {
                files.Files.Add(change);
            }
            files.List.SelectedItem = files.Files.FirstOrDefault(f => chosen is not null && PathNames.Same(f.Path, chosen)) ?? files.Files.FirstOrDefault();
        }
        // Opened from a menu whose list went with the tab it was in, the keys come to the first file, as nowhere else has them.
        if (first && files.Files.Count > 0 && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is null or TopLevel)
        {
            ListKeys.Hold(files.List);
        }
    }

    public bool TakesKey(KeyEventArgs e) => files.TakesKey(e);

    /// <summary>Its tab closed: it stops following the working tree.</summary>
    public void Close() => vcs.Changed -= onChanged;
}
