using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>Manage Remotes…: the repository's remotes, each by its name and its URL (a password or token in it masked),
/// with a push URL of its own said beside it; Add Remote…, Edit Remote… (a double-click or Enter too) and Remove Remote…
/// act through the window, which asks and says what each does, and the list is read again after each.</summary>
public sealed class RemotesDialog : IdeWindow
{
    private readonly Func<Task<IReadOnlyList<RemoteInfo>>> read;
    private readonly Func<RemoteInfo?, Task> edit;
    private readonly Func<RemoteInfo, Task> remove;
    private readonly ObservableCollection<RemoteInfo> rows = [];
    private readonly ListBox list;
    private readonly ToolWindows.EmptyState empty = new("No remotes: Add Remote… adds one") { IsVisible = false };
    private readonly Button editButton;
    private readonly Button removeButton;
    private int reads;

    /// <param name="read">The remotes as git has them now.</param>
    /// <param name="edit">Add Remote… (null) or Edit Remote… of one, done once it is carried out or given up.</param>
    /// <param name="remove">Remove Remote… of one, done once it is removed or the person kept it.</param>
    public RemotesDialog(Func<Task<IReadOnlyList<RemoteInfo>>> read, Func<RemoteInfo?, Task> edit, Func<RemoteInfo, Task> remove)
    {
        this.read = read;
        this.edit = edit;
        this.remove = remove;
        Title = "Remotes";
        Width = DialogWidths.Wide;
        Height = 360;
        MinWidth = 480;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        list = new ListBox { Classes = { StyleClasses.Tree }, ItemsSource = rows };
        ReusedRows.Use(list, Row);
        list.SelectionChanged += (_, _) => UpdateButtons();
        ListKeys.Act(list, new ListActs { Open = () => Run(EditChosenAsync) });
        Border table = new() { Classes = { StyleClasses.Framed }, ClipToBounds = true, Child = new Panel { Children = { list, empty } } };
        AddButton = ActionButton("Add Remote…", () => Run(() => AfterAsync(edit(null))));
        editButton = ActionButton("Edit Remote…", () => Run(EditChosenAsync));
        removeButton = ActionButton("Remove Remote…", () => Run(async () =>
        {
            if (list.SelectedItem is RemoteInfo chosen)
            {
                await AfterAsync(remove(chosen));
            }
        }));
        StackPanel actions = new()
        {
            [DockPanel.DockProperty] = Dock.Right,
            Spacing = Gaps.Normal,
            Margin = new Thickness(Gaps.Wide, 0, 0, 0),
            Children = { AddButton, editButton, removeButton },
        };
        TextBlock about = new()
        {
            [DockPanel.DockProperty] = Dock.Top,
            Classes = { StyleClasses.Muted },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, Gaps.Wide),
            Text = "Each remote is another copy of the repository that this one fetches branches from and pushes them to, by its name.",
        };
        Button close = new() { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();
        Content = DialogFooter.Page(new DockPanel { [DialogFooter.InsetProperty] = true, Children = { about, actions, table } }, DialogFooter.Bar(DialogFooter.Listed(close)));
        Opened += (_, _) =>
        {
            IdeErrors.Observe(ReadAsync());
            Dispatcher.UIThread.Post(() => ListKeys.Hold(list), DispatcherPriority.Loaded);
        };
        UpdateButtons();
    }

    public Button AddButton { get; }

    public Button EditButton => editButton;

    public Button RemoveButton => removeButton;

    /// <summary>The remotes listed.</summary>
    public IReadOnlyList<RemoteInfo> Rows => rows;

    /// <summary>The remote chosen, which Edit and Remove act on.</summary>
    public RemoteInfo? Chosen
    {
        get => list.SelectedItem as RemoteInfo;
        set => list.SelectedItem = value;
    }

    /// <summary>How each remote's row reads: its name, then its URL, masked as everywhere.</summary>
    public static string Said(RemoteInfo remote) =>
        remote.PushUrl is { } push && push != remote.Url ? $"{VcsController.UrlShown(remote.Url)} (pushes to {VcsController.UrlShown(push)})" : VcsController.UrlShown(remote.Url);

    /// <summary>Reads the remotes again, the one chosen kept by its name.</summary>
    public async Task ReadAsync()
    {
        int at = ++reads;
        IReadOnlyList<RemoteInfo> remotes = await read();
        if (at != reads)
        {
            return;
        }
        string? chosen = Chosen?.Name;
        rows.Clear();
        foreach (RemoteInfo remote in remotes)
        {
            rows.Add(remote);
        }
        Chosen = rows.FirstOrDefault(r => r.Name == chosen) ?? rows.FirstOrDefault();
        empty.IsVisible = rows.Count == 0;
        UpdateButtons();
    }

    private async Task EditChosenAsync()
    {
        if (list.SelectedItem is RemoteInfo chosen)
        {
            await AfterAsync(edit(chosen));
        }
    }

    private async Task AfterAsync(Task acted)
    {
        await acted;
        await ReadAsync();
    }

    private static void Run(Func<Task> act) => IdeErrors.Observe(act());

    private void UpdateButtons()
    {
        string? none = list.SelectedItem is null ? "Choose a remote first" : null;
        ToolButtons.Grey(editButton, none);
        ToolButtons.Grey(removeButton, none);
    }

    /// <summary>A remote's row: its name, then its URL muted.</summary>
    private static Control Row()
    {
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0, 0, 0), [DockPanel.DockProperty] = Dock.Left };
        TextBlock url = new() { Classes = { StyleClasses.Muted }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0, Gaps.Normal, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel line = new() { Height = ListRow.Height, Children = { name, url } };
        return line.Showing<RemoteInfo>(remote =>
        {
            name.Text = remote.Name;
            url.Text = Said(remote);
        });
    }

    private static Button ActionButton(string label, Action run)
    {
        // The column's buttons one width, the widest's.
        Button button = new() { Content = label, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => run();
        return button;
    }
}
