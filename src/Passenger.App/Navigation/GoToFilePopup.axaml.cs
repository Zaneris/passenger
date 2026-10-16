using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.App.Explorer;
using Passenger.App.Workspace;

namespace Passenger.App.Navigation;

/// <summary>Rider's Go to File: a query over every file in the folder, ranked as it is typed, the
/// recent files when it is empty. Up and Down move through the results, Enter opens one, Escape
/// leaves.</summary>
public partial class GoToFilePopup : UserControl
{
    private readonly FileIndex index;
    private readonly IReadOnlyList<string> recent;

    public GoToFilePopup(FileIndex index, IReadOnlyList<string> recent)
    {
        InitializeComponent();
        this.index = index;
        this.recent = recent;
        Query.TextChanged += (_, _) => Refresh();
        AddHandler(KeyDownEvent, OnPopupKeyDown, RoutingStrategies.Tunnel);
        Refresh();
        Dispatcher.UIThread.Post(() => Query.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>The chosen file (a full path), or null when the popup was dismissed.</summary>
    public event EventHandler<string?>? Done;

    public GoToFilePopup()
        : this(new FileIndex(), [])
    {
    }

    private void Refresh()
    {
        string query = Query.Text ?? string.Empty;
        string root = index.Root ?? string.Empty;
        IEnumerable<string> paths = query.Trim().Length == 0
            ? recent.Select(full => Path.GetRelativePath(root, full))
            : index.Search(query);
        List<FileHit> hits = paths.Select(p => new FileHit(Path.Combine(root, p), p)).ToList();
        Results.ItemsSource = hits;
        Results.SelectedIndex = hits.Count > 0 ? 0 : -1;
        Empty.IsVisible = hits.Count == 0;
        Empty.Text = query.Trim().Length == 0 ? "No recent files" : "Nothing matches";
    }

    private void OnPopupKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Results.SelectedIndex = Math.Min(Results.SelectedIndex + 1, Results.ItemCount - 1);
                Results.ScrollIntoView(Results.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Up:
                Results.SelectedIndex = Math.Max(Results.SelectedIndex - 1, 0);
                Results.ScrollIntoView(Results.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Enter:
                Choose();
                e.Handled = true;
                break;
            case Key.Escape:
                Done?.Invoke(this, null);
                e.Handled = true;
                break;
        }
    }

    private void OnResultDoubleTapped(object? sender, RoutedEventArgs e) => Choose();

    private void Choose()
    {
        if (Results.SelectedItem is FileHit hit)
        {
            Done?.Invoke(this, hit.FullPath);
        }
    }
}

/// <summary>One result: the file's name, the folder it is in, and its icon.</summary>
public sealed record FileHit(string FullPath, string RelativePath)
{
    public string Name => Path.GetFileName(RelativePath);
    public string Folder => Path.GetDirectoryName(RelativePath) is { Length: > 0 } folder ? folder : string.Empty;
    public GridLength FolderWidth => Folder.Length == 0 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    public Geometry Icon => FileIcons.ForFile(FullPath);
    public IBrush IconBrush => FileIcons.BrushForFile(FullPath);
}
