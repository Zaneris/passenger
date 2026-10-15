using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Welcome;

/// <summary>Rider's Welcome screen, in the window while nothing is open: the folders, solutions and projects opened lately,
/// newest first and narrowed as a search is typed, beside New Solution, Open, Open Folder and Clone Repository. Up and Down
/// choose one from the search box or the list, Enter opens it, and Delete (Backspace on the Mac) or its menu removes it.
/// One gone from disk is marked, and the window says so when it is opened (<see cref="Say"/>).</summary>
public sealed class WelcomeView : UserControl
{
    private readonly TextBox search;
    private readonly ListBox list;
    private readonly TextBlock empty;
    private readonly TextBlock notice;
    private IReadOnlyList<RecentRow> all = [];

    public WelcomeView()
    {
        this.Bind(BackgroundProperty, this.GetResourceObservable("Passenger.Editor.Background"));
        TextBlock title = new() { Text = "Welcome to Passenger", FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("Passenger.Text"));
        TextBlock subtitle = new()
        {
            Classes = { "muted" },
            Text = "Start a new solution, open a solution, project or folder, or clone a repository.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 20),
        };

        search = new TextBox { Classes = { "search" }, PlaceholderText = "Search recent solutions" };
        search.TextChanged += (_, _) => Filter();
        search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        Button open = new() { Content = "Open", MinWidth = 72 };
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        Button openFolder = new() { Content = "Open Folder" };
        openFolder.Click += (_, _) => OpenFolderRequested?.Invoke(this, EventArgs.Empty);
        Button clone = new() { Content = "Clone Repository" };
        clone.Click += (_, _) => CloneRequested?.Invoke(this, EventArgs.Empty);
        // New Solution goes first, as Rider's does.
        NewSolutionButton = new Button { Content = "New Solution" };
        NewSolutionButton.Click += (_, _) => NewSolutionRequested?.Invoke(this, EventArgs.Empty);
        WrapPanel buttons = new() { ItemSpacing = 8, LineSpacing = 8, Children = { NewSolutionButton, open, openFolder, clone } };
        Grid bar = new() { Margin = new Thickness(0, 0, 0, 12), ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        bar.Children.Add(search);
        bar.Children.Add(buttons);
        // Too narrow for the field and the buttons side by side, the buttons go under it, wrapping as they must: beside
        // it they were drawn over it, and cut off at the page's edge.
        bar.SizeChanged += (_, e) =>
        {
            buttons.Measure(Size.Infinity);
            bool under = e.NewSize.Width < buttons.DesiredSize.Width + 12 + 240;
            Grid.SetRow(buttons, under ? 1 : 0);
            Grid.SetColumn(buttons, under ? 0 : 1);
            Grid.SetColumnSpan(buttons, under ? 2 : 1);
            Grid.SetColumnSpan(search, under ? 2 : 1);
            search.Margin = under ? default : new Thickness(0, 0, 12, 0);
            buttons.Margin = under ? new Thickness(0, 8, 0, 0) : default;
        };
        search.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(buttons, 1);

        notice = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(4, 0, 4, 10) };
        notice.Bind(TextBlock.ForegroundProperty, notice.GetResourceObservable("Passenger.Editor.Warning"));
        list = new ListBox
        {
            Classes = { "tree" },
            Padding = new Thickness(0),
            // A container let go of is given no row.
            ItemTemplate = new FuncDataTemplate<RecentRow?>((row, _) => row is null ? new Panel() : Row(row), supportsRecycling: false),
        };
        list.DoubleTapped += (_, _) => OpenChosen();
        list.KeyDown += OnListKeyDown;
        ListMenus.Attach(list, () => list.SelectedItem is not RecentRow chosen ? [] :
        [
            ListMenus.Item("Open", "Enter", () => Chosen?.Invoke(this, chosen)),
            ListMenus.Item(OperatingSystem.IsMacOS() ? "Reveal in Finder" : $"Show in {ChildProcesses.FileManager}", null, () => ChildProcesses.Reveal(chosen.Path), !chosen.Missing),
            ListMenus.Item("Copy Path", null, () => _ = ListMenus.CopyAsync(this, chosen.Path)),
            new Separator(),
            ListMenus.Item("Remove from Recent Solutions", OperatingSystem.IsMacOS() ? "Back" : "Delete", () => RemoveRequested?.Invoke(this, chosen)),
        ]);
        empty = new TextBlock { Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0) };

        DockPanel page = new() { MaxWidth = 760, Margin = new Thickness(32, 56, 32, 24) };
        foreach (Control top in (Control[])[title, subtitle, bar, notice])
        {
            DockPanel.SetDock(top, Dock.Top);
            page.Children.Add(top);
        }
        page.Children.Add(new Panel { Children = { list, empty } });
        Content = page;
        Filter();
    }

    /// <summary>New Solution, as File, New Solution does.</summary>
    public event EventHandler? NewSolutionRequested;

    /// <summary>Open, as File, Open does: a solution or project.</summary>
    public event EventHandler? OpenRequested;

    public event EventHandler? OpenFolderRequested;

    public event EventHandler? CloneRequested;

    /// <summary>A recent folder or solution to open, by Enter, a double-click or its menu.</summary>
    public event EventHandler<RecentRow>? Chosen;

    /// <summary>One to take off the list, by its key or its menu.</summary>
    public event EventHandler<RecentRow>? RemoveRequested;

    /// <summary>The New Solution button, for a click on it.</summary>
    public Button NewSolutionButton { get; }

    public string Query
    {
        get => search.Text ?? string.Empty;
        set => search.Text = value;
    }

    /// <summary>The rows listed, after the search.</summary>
    public IReadOnlyList<RecentRow> Rows => list.ItemsSource as IReadOnlyList<RecentRow> ?? [];

    /// <summary>The row Enter opens.</summary>
    public RecentRow? Selected => list.SelectedItem as RecentRow;

    /// <summary>What the screen says of the last one opened, when it could not be.</summary>
    public string? Notice => notice.IsVisible ? notice.Text : null;

    /// <summary>A row's place in the list, for a click on it; null when it is not listed.</summary>
    public Control? RowControl(RecentRow row) => Rows.ToList().IndexOf(row) is var at and >= 0 ? list.ContainerFromIndex(at) : null;

    /// <summary>The recent ones, newest first: the one chosen stays chosen if it is still there, else the one now in its
    /// place.</summary>
    public void Show(IReadOnlyList<RecentRow> rows)
    {
        all = rows;
        Filter();
    }

    /// <summary>Says something of the one last opened (it is gone), or nothing.</summary>
    public void Say(string? text)
    {
        notice.Text = text;
        notice.IsVisible = text is not null;
    }

    /// <summary>The keys go to the search, as the screen opens.</summary>
    public void FocusSearch() => Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Loaded);

    private void Filter()
    {
        RecentRow? was = Selected;
        int at = Math.Max(0, list.SelectedIndex);
        bool focused = list.IsKeyboardFocusWithin;
        string[] words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<RecentRow> shown = [.. all.Where(row => words.All(w => Matches(row, w)))];
        list.ItemsSource = shown;
        int chosen = was is null ? -1 : shown.FindIndex(r => r.Path == was.Path);
        list.SelectedIndex = shown.Count == 0 ? -1 : chosen >= 0 ? chosen : Math.Min(at, shown.Count - 1);
        empty.Text = all.Count == 0 ? "No recent solutions yet" : "Nothing matches";
        empty.IsVisible = shown.Count == 0;
        if (focused && list.SelectedIndex >= 0)
        {
            // The rows are made again: the keys stay with the one chosen.
            Dispatcher.UIThread.Post(() => list.ContainerFromIndex(list.SelectedIndex)?.Focus(), DispatcherPriority.Loaded);
        }
    }

    /// <summary>A word typed names a row by its name (CamelHumps too) or anywhere in its path.</summary>
    private static bool Matches(RecentRow row, string word) =>
        row.Name.Contains(word, StringComparison.OrdinalIgnoreCase) || row.Shown.Contains(word, StringComparison.OrdinalIgnoreCase)
        || FuzzyMatch.Score(word, row.Name) is not null;

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                OpenChosen();
                e.Handled = true;
                break;
            case Key.Escape when Query.Length > 0:
                Query = string.Empty;
                e.Handled = true;
                break;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || Selected is not { } chosen)
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            Chosen?.Invoke(this, chosen);
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back)
        {
            RemoveRequested?.Invoke(this, chosen);
            e.Handled = true;
        }
    }

    private void Move(int step)
    {
        if (list.ItemCount == 0)
        {
            return;
        }
        list.SelectedIndex = Math.Clamp(list.SelectedIndex + step, 0, list.ItemCount - 1);
        list.ScrollIntoView(list.SelectedIndex);
    }

    private void OpenChosen()
    {
        if (Selected is { } chosen)
        {
            Chosen?.Invoke(this, chosen);
        }
    }

    /// <summary>A row as Rider's: the badge with its initials, the name over where it is, and Missing for one gone.</summary>
    private static Control Row(RecentRow row)
    {
        TextBlock letters = new() { Text = MainWindow.Initials(row.Name), FontSize = 11, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        letters.Bind(TextBlock.ForegroundProperty, letters.GetResourceObservable(row.Missing ? "Passenger.TextMuted" : "Passenger.AccentText"));
        Border badge = new() { Width = 28, Height = 28, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 10, 0), Child = letters, VerticalAlignment = VerticalAlignment.Center };
        badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable(row.Missing ? "Passenger.Hover" : "Passenger.Badge"));
        TextBlock name = new() { Text = row.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        if (row.Missing)
        {
            name.Classes.Add("muted");
        }
        TextBlock where = new() { Classes = { "muted" }, Text = row.Shown, FontSize = 12, TextTrimming = TextTrimming.PathSegmentEllipsis };
        DockPanel line = new() { Height = 44 };
        DockPanel.SetDock(badge, Dock.Left);
        line.Children.Add(badge);
        if (row.Missing)
        {
            TextBlock gone = new() { Text = "Missing", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
            gone.Bind(TextBlock.ForegroundProperty, gone.GetResourceObservable("Passenger.Editor.Warning"));
            DockPanel.SetDock(gone, Dock.Right);
            line.Children.Add(gone);
        }
        line.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { name, where } });
        return line;
    }
}
