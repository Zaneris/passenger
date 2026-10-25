using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
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
            ItemTemplate = new FuncDataTemplate<FileChange>((change, _) => change is null ? new TextBlock() : Row(change, repository), supportsRecycling: false),
        };
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

    private static Control Row(FileChange change, Git repository)
    {
        PathIcon icon = new()
        {
            Data = Explorer.FileIcons.ForFile(change.Path),
            Foreground = Explorer.FileIcons.BrushForFile(change.Path),
            Width = 14,
            Height = 14,
            Margin = new Thickness(8, 0, 6, 0),
        };
        TextBlock name = new() { Text = change.Name, VerticalAlignment = VerticalAlignment.Center };
        name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable(ToolWindows.CommitView.KindToken(change.Kind)));
        TextBlock folder = new()
        {
            Text = Path.GetDirectoryName(repository.Relative(change.Path))?.Replace('\\', '/') ?? string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 12,
        };
        folder.Classes.Add("muted");
        return new StackPanel { Orientation = Orientation.Horizontal, Height = ListRow.Height, Children = { icon, name, folder } };
    }
}
