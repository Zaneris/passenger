using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Welcome;

/// <summary>The Welcome screen, a start page in the window while nothing is open: Passenger's mark and version, New
/// Solution, Open, Open Folder and Clone Repository, and the folders, solutions and projects opened lately (each with what
/// it opens as), newest first and narrowed as a search is typed. Up and Down
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
        Classes.Add("welcome");
        Image mark = new() { Classes = { "welcomeMark" }, Source = new Bitmap(AssetLoader.Open(new Uri("avares://Passenger/Assets/passenger.png"))) };
        RenderOptions.SetBitmapInterpolationMode(mark, BitmapInterpolationMode.HighQuality);
        TextBlock title = new() { Classes = { "welcomeTitle" }, Text = "Welcome to Passenger" };
        TextBlock subtitle = new()
        {
            Classes = { "welcomeSubtitle" },
            Text = $"Version {Product.Version}. Start a new solution, open a solution, project or folder, or clone a repository.",
            TextWrapping = TextWrapping.Wrap,
        };
        Grid hero = new() { Classes = { "welcomeHero" }, ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        hero.Children.Add(mark);
        hero.Children.Add(new StackPanel { [Grid.ColumnProperty] = 1, VerticalAlignment = VerticalAlignment.Center, Children = { title, subtitle } });

        NewSolutionButton = Action(IconKeys.Add, "New Solution", "From a template");
        NewSolutionButton.Click += (_, _) => NewSolutionRequested?.Invoke(this, EventArgs.Empty);
        Button open = Action(IconKeys.Solution, "Open", "A solution or project");
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        Button openFolder = Action(IconKeys.FolderOpen, "Open Folder", "A folder of code");
        openFolder.Click += (_, _) => OpenFolderRequested?.Invoke(this, EventArgs.Empty);
        Button clone = Action(IconKeys.Branch, "Clone Repository", "From a Git URL");
        clone.Click += (_, _) => CloneRequested?.Invoke(this, EventArgs.Empty);
        WrapPanel actions = new() { Classes = { "welcomeActions" }, Children = { NewSolutionButton, open, openFolder, clone } };
        // Two or one across in a narrow page, so no tile is cut off at its edge.
        actions.SizeChanged += (_, e) =>
        {
            double gap = actions.ItemSpacing;
            int across = e.NewSize.Width >= 4 * ActionWidth + 3 * gap ? 4 : e.NewSize.Width >= 2 * ActionWidth + gap ? 2 : 1;
            actions.ItemWidth = Math.Floor((e.NewSize.Width - (across - 1) * gap) / across);
        };

        TextBlock recent = new() { Classes = { "welcomeSection" }, Text = "Recent solutions", VerticalAlignment = VerticalAlignment.Center };
        search = new TextBox { Classes = { "search", "welcomeSearch" }, PlaceholderText = "Search recent solutions", [Grid.ColumnProperty] = 1 };
        search.TextChanged += (_, _) => Filter();
        search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        Grid bar = new() { Classes = { "welcomeRecentBar" }, ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { recent, search } };

        notice = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(4, 0, 4, 10) };
        notice.Bind(TextBlock.ForegroundProperty, notice.GetResourceObservable(TokenKeys.Warning));
        list = new ListBox
        {
            Classes = { "tree", "welcomeList" },
            ItemTemplate = new FuncDataTemplate<RecentRow?>((row, _) => row is null ? new Panel() : Row(row), supportsRecycling: false),
        };
        list.DoubleTapped += (_, _) => OpenChosen();
        list.KeyDown += OnListKeyDown;
        ListMenus.Attach(list, () => list.SelectedItem is not RecentRow chosen ? [] :
        [
            ListMenus.Item("Open", "Enter", () => Chosen?.Invoke(this, chosen)),
            ListMenus.Item(FileManager.Showing, null, () => RevealRequested?.Invoke(this, chosen), !chosen.Missing),
            ListMenus.Item("Copy Path", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.Path))),
            new Separator(),
            ListMenus.Item("Remove from Recent Solutions", OperatingSystem.IsMacOS() ? "Back" : "Delete", () => RemoveRequested?.Invoke(this, chosen)),
        ]);
        empty = new TextBlock { Classes = { "emptyText", "welcomeEmpty" } };

        DockPanel page = new() { Classes = { "welcomePage" } };
        foreach (Control top in (Control[])[hero, actions, bar, notice])
        {
            DockPanel.SetDock(top, Dock.Top);
            page.Children.Add(top);
        }
        page.Children.Add(new Border { Classes = { "welcomeRecent" }, Child = new Panel { Children = { list, empty } } });
        Content = page;
        Filter();
    }

    /// <summary>The narrowest an action's tile is before the tiles go two or one across.</summary>
    private const double ActionWidth = 160;

    /// <summary>One of the page's actions: its icon, its name, and in a few words what it opens.</summary>
    private static Button Action(string icon, string name, string hint)
    {
        PathIcon glyph = new() { Classes = { "welcomeActionIcon" } };
        glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable(icon));
        Border chip = new() { Classes = { "welcomeActionChip" }, Child = glyph, VerticalAlignment = VerticalAlignment.Center };
        TextBlock label = new() { Classes = { "welcomeActionTitle" }, Text = name, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock what = new() { Classes = { "welcomeActionHint" }, Text = hint, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(chip, Dock.Left);
        return new Button
        {
            Classes = { "welcomeAction" },
            Content = new DockPanel { Children = { chip, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { label, what } } } },
        };
    }

    /// <summary>A click on New Solution.</summary>
    public event EventHandler? NewSolutionRequested;

    /// <summary>A click on Open, for a solution or project.</summary>
    public event EventHandler? OpenRequested;

    public event EventHandler? OpenFolderRequested;

    public event EventHandler? CloneRequested;

    /// <summary>A recent folder or solution to open, by Enter, a double-click or its menu.</summary>
    public event EventHandler<RecentRow>? Chosen;

    /// <summary>One to take off the list, by its key or its menu.</summary>
    public event EventHandler<RecentRow>? RemoveRequested;

    /// <summary>A recent folder or solution to show in the file manager.</summary>
    public event EventHandler<RecentRow>? RevealRequested;

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
        empty.Text = all.Count == 0 ? "No recent solutions yet\nThe solutions, projects and folders you open are listed here" : "Nothing matches";
        empty.IsVisible = shown.Count == 0;
        if (focused && list.SelectedIndex >= 0)
        {
            // The rows are made anew, so the focus goes back to the one chosen.
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
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown when ListKeys.Move(list, e):
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

    private void OpenChosen()
    {
        if (Selected is { } chosen)
        {
            Chosen?.Invoke(this, chosen);
        }
    }

    /// <summary>A row: the badge with its initials, the name over where it is, what it opens as (a solution, a project or a
    /// folder), and Missing for one gone.</summary>
    private static Control Row(RecentRow row)
    {
        TextBlock letters = new() { Classes = { "welcomeBadgeText" }, Text = MainWindow.Initials(row.Name), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Border badge = new() { Classes = { "welcomeBadge" }, Child = letters, VerticalAlignment = VerticalAlignment.Center };
        badge.Classes.Set("missing", row.Missing);
        TextBlock name = new() { Classes = { "welcomeRowName" }, Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        if (row.Missing)
        {
            name.Classes.Add("muted");
        }
        TextBlock where = new() { Classes = { "muted", "welcomeRowPath" }, Text = row.Shown, TextTrimming = TextTrimming.PathSegmentEllipsis };
        DockPanel line = new() { Classes = { "welcomeRow" } };
        DockPanel.SetDock(badge, Dock.Left);
        line.Children.Add(badge);
        if (row.Missing)
        {
            TextBlock gone = new() { Text = "Missing", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
            gone.Bind(TextBlock.ForegroundProperty, gone.GetResourceObservable(TokenKeys.Warning));
            DockPanel.SetDock(gone, Dock.Right);
            line.Children.Add(gone);
        }
        Border kind = new() { Classes = { "welcomeKind" }, Child = new TextBlock { Classes = { "welcomeKindText" }, Text = Kind(row.Path) }, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(kind, Dock.Right);
        line.Children.Add(kind);
        line.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { name, where } });
        return line;
    }

    /// <summary>What a recent path opens as: a solution file, a project opened alone, or a folder.</summary>
    private static string Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".sln" or ".slnx" => "Solution",
        ".csproj" => "Project",
        _ => "Folder",
    };
}
