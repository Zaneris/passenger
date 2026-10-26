using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>Show Diff with Working Tree: the files that differ between a branch or tag and the working tree, in a
/// window over the IDE's; a double-click or Enter on one shows it as the branch has it beside the file. Escape closes
/// it.</summary>
public sealed class ChangesWindow : Window
{
    private readonly ListBox list;

    public ChangesWindow(string revision, IReadOnlyList<FileChange> files, Git repository)
    {
        Title = $"Changes Between {revision} and Current Working Tree";
        Width = 720;
        Height = 480;
        MinWidth = 400;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Files = files;
        list = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = files,
        };
        ReusedRows.Use(list, () => Row(repository));
        list.DoubleTapped += (_, _) => Open();
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                Open();
                e.Handled = true;
            }
        };
        TextBlock empty = new() { Text = "No differences", IsVisible = files.Count == 0, Classes = { "emptyText" } };
        Content = new Panel { Children = { list, empty } };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                Close();
                e.Handled = true;
            }
        };
        Opened += (_, _) =>
        {
            if (files.Count > 0)
            {
                list.SelectedIndex = 0;
                list.ContainerFromIndex(0)?.Focus();
            }
        };
    }

    /// <summary>The files that differ.</summary>
    public IReadOnlyList<FileChange> Files { get; }

    /// <summary>A file to show as the branch has it beside the file now.</summary>
    public event EventHandler<FileChange>? DiffRequested;

    /// <summary>Shows the chosen file's difference, as a double-click does.</summary>
    public void Open()
    {
        if (list.SelectedItem is FileChange change)
        {
            DiffRequested?.Invoke(this, change);
        }
    }

    public void Select(FileChange change) => list.SelectedItem = change;

    /// <summary>A file's row: its icon, its name in its change's colour, and its folder from the repository's top.</summary>
    private static Control Row(Git repository)
    {
        PathIcon icon = new()
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(8, 0, 6, 0),
        };
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock folder = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 12,
        };
        folder.Classes.Add("muted");
        string? colour = null;
        StackPanel row = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Children = { icon, name, folder } };
        return row.Showing<FileChange>(change =>
        {
            icon.Data = Explorer.FileIcons.ForFile(change.Path);
            icon.Foreground = Explorer.FileIcons.BrushForFile(change.Path);
            name.Text = change.Name;
            name.Resource(TextBlock.ForegroundProperty, ref colour, ToolWindows.CommitView.KindToken(change.Kind));
            folder.Text = Path.GetDirectoryName(repository.Relative(change.Path))?.Replace('\\', '/') ?? string.Empty;
        });
    }
}
