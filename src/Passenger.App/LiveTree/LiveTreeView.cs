using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;
using Passenger.Inspector;
using Passenger.App.ToolWindows;

namespace Passenger.App.LiveTree;

/// <summary>A value typed for a property of the element chosen.</summary>
public sealed record PropertySet(PropertyValue Value, string Text);

/// <summary>A row of the tree: an element at its depth, open or not.</summary>
public sealed class LiveTreeRow(Element element, int depth, bool expanded) : Observable, ITreeRow
{
    private bool isExpanded = expanded;

    public Element Element { get; } = element;

    public int Depth { get; } = depth;

    public bool Expands => Element.Children.Count > 0;

    public bool IsExpanded
    {
        get => isExpanded;
        internal set => Set(ref isExpanded, value);
    }
}

/// <summary>The details' three pages.</summary>
public enum LiveTreePage
{
    Properties,
    Styles,
    Layout,
}

/// <summary>The Live Visual Tree window: Select Element and Refresh atop the app's windows and their visual trees (a
/// control's type, #name and .classes; a template's parts dimmed; at first open down to the app's own XAML), and under
/// them the chosen control: its XAML (a link to it, or why there is none), then its properties with each value's source,
/// the styles that apply and those that do not with why, and its layout's boxes; for a Godot game, what Select Element
/// picks (2D or 3D) and Pause Game Loop and Next Frame beside them. Between the tree and the details, a split that gives a
/// short tree its rows' height, or stands where the person dragged it. The window only shows; <see cref="LiveTreeController"/>
/// asks the app.</summary>
public sealed class LiveTreeView : DockPanel
{
    private readonly TreeRows<LiveTreeRow> rows = [];
    private readonly ListBox tree;
    private readonly TextBlock placeholder;
    private readonly TextBlock status;
    private readonly TextBlock monitors;
    private readonly Border banner;
    private readonly TextBlock bannerText;
    private readonly StackPanel heading;
    private readonly Dictionary<LiveTreePage, ToggleButton> tabs = [];
    private readonly ListBox properties;
    private readonly TextBox filter;
    private readonly ToggleButton defaults;
    private readonly StackPanel styles;
    private readonly StackPanel layout;
    private readonly Panel pages;
    private readonly Grid split;
    private readonly Panel treePane;

    /// <summary>Rows the person opened or closed themselves, which keep that over the tree's own choice.</summary>
    private readonly HashSet<long> opened = [];
    private readonly HashSet<long> closed = [];

    private AppTree? shown;
    private bool sourced;
    private bool choosing;

    public LiveTreeView()
    {
        SelectElement = ToolButtons.Toggle(IconKeys.SelectElement, "Select Element: point at a control in the app, or a node in the game, and click it");
        SelectElement.Click += (_, _) => PickRequested?.Invoke(this, SelectElement.IsChecked == true);
        Button refresh = ToolButtons.Of(AppActions.LiveTreeRefresh, IconKeys.Rerun, "Refresh", _ => RefreshRequested?.Invoke(this, EventArgs.Empty));
        status = new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        sources = new ComboBox { Classes = { StyleClasses.Compact }, MinWidth = 130, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), IsVisible = false };
        ToolTip.SetTip(sources, "The running game whose nodes are shown");
        sources.SelectionChanged += (_, _) =>
        {
            if (!choosingSource && sources.SelectedIndex >= 0)
            {
                SourceChosen?.Invoke(this, sources.SelectedIndex);
            }
        };
        PickIn = new ComboBox { Classes = { StyleClasses.Compact }, ItemsSource = PickKinds, SelectedIndex = 0, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        ToolTip.SetTip(PickIn, "What Select Element picks in the game: its 2D nodes and controls, or its 3D nodes");
        PickIn.SelectionChanged += (_, _) =>
        {
            if (!showingPickIn && PickIn.SelectedIndex >= 0)
            {
                PickInChosen?.Invoke(this, PicksIn3D);
            }
        };
        PauseLoop = ToolButtons.Toggle(IconKeys.Pause, PauseLoopTip);
        ActionTips.Set(PauseLoop, AppActions.PauseGameLoop, PauseLoopTip);
        PauseLoop.IsVisible = false;
        PauseLoop.Click += (_, _) => LoopRequested?.Invoke(this, AppActions.PauseGameLoop);
        NextFrame = ToolButtons.Of(AppActions.NextFrame, IconKeys.NextFrame, "Next Frame: run one frame of the game's loop paused", action => LoopRequested?.Invoke(this, action));
        NextFrame.IsVisible = false;
        DockPanel top = new() { Margin = new Thickness(6, 0) };
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { SelectElement, PickIn, refresh, PauseLoop, NextFrame, sources },
        };
        DockPanel.SetDock(buttons, Dock.Left);
        top.Children.Add(buttons);
        top.Children.Add(status);
        Children.Add(ToolBars.Top(top));

        monitors = new TextBlock { [DockProperty] = Dock.Top, Classes = { StyleClasses.Muted }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 4), IsVisible = false };
        Children.Add(monitors);
        bannerText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        banner = new Border { [DockProperty] = Dock.Top, Padding = new Thickness(10, 6), IsVisible = false, Child = bannerText };
        banner.Bind(Border.BackgroundProperty, banner.GetResourceObservable(TokenKeys.Banner));
        Children.Add(banner);

        tree = new ListBox
        {
            Classes = { StyleClasses.Tree },
            ItemsSource = rows,
        };
        ReusedRows.Use(tree, Row);
        // An app's tree goes deeper than the window is wide (Passenger's own, twenty levels down to its status bar).
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Auto);
        tree.SelectionChanged += (_, _) =>
        {
            if (!choosing && tree.SelectedItem is LiveTreeRow row)
            {
                ElementChosen?.Invoke(this, row.Element.Id);
            }
        };
        TreeKeys.Attach<LiveTreeRow>(tree, Toggle, row =>
        {
            if (Details is not { Source: { } source } || Details.Id != row.Element.Id)
            {
                return false;
            }
            GoToRequested?.Invoke(this, source);
            return true;
        });
        placeholder = new TextBlock { Classes = { StyleClasses.EmptyText } };

        heading = new StackPanel { [DockProperty] = Dock.Top, Margin = new Thickness(10, 8, 10, 4), Spacing = 2 };
        StackPanel tabRow = new() { [DockProperty] = Dock.Top, Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 2, 6, 4) };
        foreach (LiveTreePage page in Enum.GetValues<LiveTreePage>())
        {
            ToggleButton tab = new() { Classes = { StyleClasses.PanelTab }, Content = page.ToString() };
            tab.Click += (_, _) => ShowPage(page);
            tabs[page] = tab;
            tabRow.Children.Add(tab);
        }
        filter = new TextBox { Classes = { StyleClasses.Search }, PlaceholderText = "Filter properties", Margin = new Thickness(0, 0, 6, 0) };
        filter.TextChanged += (_, _) => ShowProperties();
        // An option toggle with words, as wide as they are, where the style's are an icon's square.
        defaults = new ToggleButton { Classes = { StyleClasses.Option }, Content = "Defaults", FontSize = 12, Width = double.NaN, Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(defaults, "Show the properties at their default values too");
        defaults.Click += (_, _) => ShowProperties();
        DockPanel filterRow = new() { [DockProperty] = Dock.Top, Margin = new Thickness(8, 2, 8, 6) };
        DockPanel.SetDock(defaults, Dock.Right);
        filterRow.Children.Add(defaults);
        filterRow.Children.Add(filter);
        properties = new ListBox
        {
            Classes = { StyleClasses.Tree },
            ItemsSource = PropertyRows,
        };
        ReusedRows.Use(properties, PropertyRow);
        properties.DoubleTapped += (_, _) =>
        {
            if (properties.SelectedItem is PropertyValue { Place: { } place })
            {
                GoToRequested?.Invoke(this, place);
            }
            else if (properties.SelectedItem is PropertyValue value)
            {
                Edit(value);
            }
        };
        properties.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.F2 && editing is null && properties.SelectedItem is PropertyValue { Place: null } value && WhyNotEdit?.Invoke(value) is null)
            {
                e.Handled = true;
                Edit(value);
            }
        };
        ToolWindows.ListMenus.Attach(properties, PropertyMenu);
        styles = new StackPanel { Margin = new Thickness(10, 4, 10, 10), Spacing = 10 };
        layout = new StackPanel { Margin = new Thickness(10, 4, 10, 10), Spacing = 8 };
        pages = new Panel
        {
            Children =
            {
                new DockPanel { Tag = LiveTreePage.Properties, Children = { filterRow, properties } },
                new ScrollViewer { Tag = LiveTreePage.Styles, Content = styles },
                new ScrollViewer { Tag = LiveTreePage.Layout, Content = layout },
            },
        };
        DockPanel details = new() { Children = { heading, tabRow, pages } };

        split = new Grid { RowDefinitions = new RowDefinitions("*,1,*") };
        treePane = new Panel { Children = { tree, placeholder } };
        Grid.SetRow(treePane, 0);
        HairlineDivider divider = new(GridResizeDirection.Rows) { [Grid.RowProperty] = 1 };
        divider.Splitter.DragStarted += (_, _) => dragged = treePane.Bounds.Height;
        divider.Splitter.DragCompleted += (_, _) => Dragged(details);
        Grid.SetRow(details, 2);
        split.Children.Add(treePane);
        split.Children.Add(divider);
        split.Children.Add(details);
        split.SizeChanged += (_, _) => FitSplit();
        rows.CollectionChanged += (_, _) => FitSplit();
        Children.Add(split);
        ShowPage(LiveTreePage.Properties);
        ShowDetails(null, null);
        ShowStatus(LiveTreeController.NothingShown, null);
    }

    /// <summary>The property whose value is being typed, by its name, or null.</summary>
    private string? editing;

    /// <summary>The room under a short tree's last row, for the list's edges and a sideways scrollbar.</summary>
    private const double TreeSlack = 10;

    /// <summary>The tree's share of the split as the person dragged it for the kind of source shown; null where the split
    /// follows what is shown: a short tree as tall as its rows, the details taking the rest, and a tree of half the room or
    /// more, or none, half.</summary>
    public double? DraggedSplit
    {
        get;
        set
        {
            field = value;
            FitSplit();
        }
    }

    /// <summary>The person dragged the split: the tree's share of it.</summary>
    public event EventHandler<double>? SplitDragged;

    /// <summary>The tree's half of the split, and what shows over it.</summary>
    public Control TreeHalf => treePane;

    /// <summary>The details' half: the chosen element's heading, tabs and properties.</summary>
    public Control DetailsHalf => split.Children[2];

    /// <summary>The splitter between the tree and the details.</summary>
    public GridSplitter Splitter => ((HairlineDivider)split.Children[1]).Splitter;

    /// <summary>The tree's height as the person began to drag the splitter, which the split then follows alone; null
    /// while it is not dragged.</summary>
    private double? dragged;

    private void FitSplit()
    {
        if (dragged is not null)
        {
            return;
        }
        double room = split.Bounds.Height - split.RowDefinitions[1].Height.Value;
        double rowsHeight = (rows.Count * ListRow.Height) + tree.Padding.Top + tree.Padding.Bottom + tree.BorderThickness.Top + tree.BorderThickness.Bottom + TreeSlack;
        (GridLength top, GridLength bottom) = DraggedSplit is double share ? (new GridLength(share, GridUnitType.Star), new GridLength(1 - share, GridUnitType.Star))
            : room > 0 && rows.Count > 0 && aside is null && rowsHeight < room / 2 ? (new GridLength(rowsHeight), GridLength.Star)
            : (GridLength.Star, GridLength.Star);
        if (split.RowDefinitions[0].Height != top || split.RowDefinitions[2].Height != bottom)
        {
            split.RowDefinitions[0].Height = top;
            split.RowDefinitions[2].Height = bottom;
        }
    }

    /// <summary>The split as the person left it, kept as the tree's share for the kind of source shown; a press on the
    /// splitter that moved nothing keeps nothing.</summary>
    private void Dragged(Control details)
    {
        double began = dragged ?? treePane.Bounds.Height;
        dragged = null;
        double whole = treePane.Bounds.Height + details.Bounds.Height;
        if (whole <= 0 || Math.Abs(treePane.Bounds.Height - began) < 1)
        {
            FitSplit();
            return;
        }
        double share = treePane.Bounds.Height / whole;
        DraggedSplit = share;
        SplitDragged?.Invoke(this, share);
    }

    /// <summary>The editors the source followed gives its values (a Godot game's), each row's made once from it; null for
    /// none, every value then shown as its text.</summary>
    public ILiveValueCells? Cells
    {
        get;
        set
        {
            field = value;
            PropertiesRefreshed?.Invoke();
        }
    }

    /// <summary>The rows shown again where they stand, each its editor and note anew, the list kept as it is (a value set
    /// as the pointer drags, a value kept).</summary>
    public void RefreshCells() => PropertiesRefreshed?.Invoke();

    private Button? headingLink;
    private Action? headingRun;

    /// <summary>A link after the chosen element's heading (Keep All), or none for null words.</summary>
    public void ShowHeadingLink(string? words, Action? run)
    {
        headingRun = run;
        if (words is null)
        {
            if (headingLink is not null)
            {
                headingLink.IsVisible = false;
            }
            return;
        }
        if (headingLink is null)
        {
            headingLink = new Button { Classes = { StyleClasses.Link }, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 12 };
            headingLink.Click += (_, _) => headingRun?.Invoke();
        }
        if (headingLink.Parent is null && Details is not null)
        {
            heading.Children.Add(headingLink);
        }
        headingLink.Content = words;
        headingLink.IsVisible = Details is not null;
    }

    /// <summary>The link after the heading, while it shows.</summary>
    public Button? HeadingLink => headingLink is { IsVisible: true } shown ? shown : null;

    private Control? aside;

    /// <summary>What shows over the tree, whatever the tree shows (the values a game that ended changed and did not keep), or
    /// nothing for null.</summary>
    public void ShowAside(Control? shown)
    {
        if (aside is not null)
        {
            treePane.Children.Remove(aside);
        }
        aside = shown;
        if (shown is not null)
        {
            treePane.Children.Add(shown);
        }
        tree.IsVisible = shown is null;
        placeholder.IsVisible = shown is null && rows.Count == 0 && placeholder.Text is { Length: > 0 };
        FitSplit();
    }

    /// <summary>What shows over the tree, while something does.</summary>
    public Control? Aside => aside;

    /// <summary>Why a property's value cannot be typed here, or null when it can (a Godot game's).</summary>
    public Func<PropertyValue, string?>? WhyNotEdit { get; set; }

    /// <summary>A value typed for a property and taken with Enter.</summary>
    public event EventHandler<PropertySet>? ValueSet;

    /// <summary>Whether an element has properties alone, no styles, layout or defaults (a Godot game's node): the Properties
    /// page shown, the others' tabs and the Defaults toggle hidden.</summary>
    public bool PropertiesAlone
    {
        get;
        set
        {
            field = value;
            // Every property of a game's node is its own: none is at a default for the toggle to hide.
            defaults.IsVisible = !value;
            foreach ((LiveTreePage page, ToggleButton tab) in tabs)
            {
                tab.IsVisible = Details is not null && (!value || page == LiveTreePage.Properties);
            }
            if (value)
            {
                ShowPage(LiveTreePage.Properties);
            }
        }
    }

    /// <summary>A property's value made a box to type in, where it can be set; <see cref="NotEdited"/> says why where not.</summary>
    public void Edit(PropertyValue value)
    {
        if (WhyNotEdit?.Invoke(value) is { } why)
        {
            NotEdited?.Invoke(this, why);
            return;
        }
        editing = value.Name;
        PropertiesRefreshed?.Invoke();
    }

    /// <summary>A property could not be edited, saying why.</summary>
    public event EventHandler<string>? NotEdited;

    /// <summary>The box a property's value is being typed in, while one is.</summary>
    public TextBox? Editing { get; private set; }

    /// <summary>A control chosen in the tree.</summary>
    public event EventHandler<long>? ElementChosen;

    /// <summary>Select Element pressed (on) or let go (off).</summary>
    public event EventHandler<bool>? PickRequested;

    /// <summary>Refresh pressed: its action asked for.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>A link to XAML followed: a control's element, a style's selector, a setter.</summary>
    public event EventHandler<SourcePlace>? GoToRequested;

    /// <summary>Break When Value Changes chosen on a property of the chosen control: its breakpoint to set, or to take away.</summary>
    public event EventHandler<PropertyValue>? BreakRequested;

    /// <summary>Whether a property of the chosen control has a property breakpoint, which its row shows.</summary>
    public Func<PropertyValue, bool>? Watched { get; set; }

    /// <summary>Why no property breakpoint can be set now, or null when one can.</summary>
    public Func<string?>? WhyNotBreak { get; set; }

    /// <summary>The Properties page's list, whose rows' menu offers Break When Value Changes.</summary>
    public ListBox PropertyList => properties;

    /// <summary>The box the Properties page's rows are filtered by, by name.</summary>
    public TextBox PropertyFilter => filter;

    /// <summary>A property's row as the list shows it, while it does.</summary>
    public Control? PropertyRowOf(string name) =>
        properties.GetRealizedContainers().FirstOrDefault(c => c.DataContext is PropertyValue value && value.Name == name);

    /// <summary>The Properties page's rows again, as their breakpoints changed.</summary>
    public void RefreshProperties()
    {
        PropertyValue? kept = properties.SelectedItem as PropertyValue;
        ShowProperties();
        PropertiesRefreshed?.Invoke();
        properties.SelectedItem = kept is null ? null : PropertyRows.FirstOrDefault(p => p.Name == kept.Name);
    }

    /// <summary>The menu of a property's row: Break When Value Changes (checked while it has one), and Go to Source where
    /// its value is written.</summary>
    private IReadOnlyList<Control> PropertyMenu()
    {
        if (properties.SelectedItem is not PropertyValue value)
        {
            return [];
        }
        MenuItem watch = ToolWindows.ListMenus.Item(BreakWhenValueChanges, null, () => BreakRequested?.Invoke(this, value), enabled: WhyNotBreak?.Invoke() is null);
        watch.ToggleType = MenuItemToggleType.CheckBox;
        watch.IsChecked = Watched?.Invoke(value) == true;
        ToolTip.SetTip(watch, WhyNotBreak?.Invoke() ?? "Stop the app wherever this property of this control is changed: a style, a binding, code or an animation");
        List<Control> items = [watch];
        if (value.Place is { } place)
        {
            items.Add(ToolWindows.ListMenus.Item("Go to Source", null, () => GoToRequested?.Invoke(this, place)));
        }
        return items;
    }

    /// <summary>The properties' breakpoints changed: a row shows its dot anew, whether or not it is handed another property.</summary>
    private event Action? PropertiesRefreshed;

    /// <summary>The menu item's words.</summary>
    public const string BreakWhenValueChanges = "Break When Value Changes";

    /// <summary>Select Element, pressed while it is on.</summary>
    public ToggleButton SelectElement { get; }

    /// <summary>What Select Element picks in a game, as <see cref="PickIn"/> offers it.</summary>
    public static readonly IReadOnlyList<string> PickKinds = ["2D", "3D"];

    /// <summary>What Select Element picks in a game, its 2D nodes or its 3D ones: shown while a game is followed.</summary>
    public ComboBox PickIn { get; }

    /// <summary>Whether Select Element picks a game's 3D nodes, as <see cref="PickIn"/> is chosen.</summary>
    public bool PicksIn3D => PickIn.SelectedIndex == 1;

    private bool showingPickIn;

    /// <summary>What Select Element picks shown chosen, without raising the choice back.</summary>
    public void ShowPickIn(bool in3D)
    {
        showingPickIn = true;
        try
        {
            PickIn.SelectedIndex = in3D ? 1 : 0;
        }
        finally
        {
            showingPickIn = false;
        }
    }

    /// <summary>What Select Element picks chosen in <see cref="PickIn"/>: true for a game's 3D nodes.</summary>
    public event EventHandler<bool>? PickInChosen;

    private const string PauseLoopTip = "Pause Game Loop: hold the game at its frame, the debugger not breaking in";

    /// <summary>Pause Game Loop, pressed while the game's loop is held: shown while a game is followed.</summary>
    public ToggleButton PauseLoop { get; }

    /// <summary>Next Frame, enabled while the game's loop is held: shown while a game is followed.</summary>
    public Button NextFrame { get; }

    /// <summary>Pause Game Loop or Next Frame pressed: the action asked for.</summary>
    public event EventHandler<string>? LoopRequested;

    /// <summary>The game's tools shown while a game is followed (<paramref name="game"/>), Pause Game Loop pressed while its
    /// loop is held and Next Frame enabled then; hidden for an app.</summary>
    public void ShowGame(bool game, bool held)
    {
        PickIn.IsVisible = game;
        PauseLoop.IsVisible = game;
        PauseLoop.IsChecked = held;
        NextFrame.IsVisible = game;
        NextFrame.IsEnabled = held;
    }

    /// <summary>The tree as shown, a row a visible element.</summary>
    public IReadOnlyList<LiveTreeRow> Rows => rows;

    public ListBox Tree => tree;

    /// <summary>The control chosen, as last shown, its places at the editor's lines (<see cref="AppPlaces"/>); null with none.</summary>
    public ElementDetails? Details { get; private set; }

    /// <summary>The chosen control's properties as the Properties page lists them (filtered, defaults as chosen).</summary>
    public ObservableCollection<PropertyValue> PropertyRows { get; } = [];

    public LiveTreePage Page { get; private set; }

    /// <summary>What the window says while it has no tree (nothing debugged, the app not up, refused, ended), or atop the
    /// tree (paused); and the status bar's line beside the buttons.</summary>
    public void ShowStatus(string? message, string? line)
    {
        placeholder.Text = message ?? string.Empty;
        placeholder.IsVisible = message is not null && rows.Count == 0 && aside is null;
        status.Text = line ?? string.Empty;
    }

    /// <summary>A note over the tree that what it shows is not live (the app paused), or none.</summary>
    /// <summary>A running game's performance monitors, on a line of their own under the buttons; none for null.</summary>
    public void ShowMonitors(string? text)
    {
        monitors.Text = text;
        monitors.IsVisible = text is not null;
    }

    private readonly ComboBox sources;
    private bool choosingSource;

    /// <summary>The sources the window can show, by their names, the one shown chosen; hidden with fewer than two.</summary>
    public void ShowSources(IReadOnlyList<string> names, int shown)
    {
        choosingSource = true;
        try
        {
            if (!names.SequenceEqual(SourceNames))
            {
                sources.ItemsSource = names.ToList();
            }
            sources.SelectedIndex = shown;
            sources.IsVisible = names.Count > 1;
        }
        finally
        {
            choosingSource = false;
        }
    }

    /// <summary>The sources offered, while the chooser shows.</summary>
    public IReadOnlyList<string> SourceNames => sources.ItemsSource is IEnumerable<string> names ? [.. names] : [];

    /// <summary>The chooser, shown while more than one source runs.</summary>
    public ComboBox Sources => sources;

    /// <summary>A source chosen in the chooser, by its place among <see cref="SourceNames"/>.</summary>
    public event EventHandler<int>? SourceChosen;

    /// <summary>The game's monitors as shown, while they are.</summary>
    public string? MonitorsLine => monitors.IsVisible ? monitors.Text : null;

    public void ShowBanner(string? text)
    {
        bannerText.Text = text ?? string.Empty;
        banner.IsVisible = text is not null;
    }

    /// <summary>The note over the tree, while it shows one.</summary>
    public string? BannerText => banner.IsVisible ? bannerText.Text : null;

    /// <summary>The line beside the buttons: what the app runs, or why the last ask of it failed.</summary>
    public string StatusLine => status.Text ?? string.Empty;

    /// <summary>Shows the app's tree, keeping which rows are open and which is chosen; none to empty it.</summary>
    public void ShowTree(AppTree? tree)
    {
        shown = tree;
        sourced = tree is not null && tree.Roots.Any(HasSourced);
        Rebuild(Details?.Id);
        placeholder.IsVisible = rows.Count == 0 && placeholder.Text is { Length: > 0 } && aside is null;
        if (tree is null)
        {
            opened.Clear();
            closed.Clear();
        }
    }

    /// <summary>Chooses an element in the tree, opening its ancestors, as its caller shows its details; false when the tree
    /// has no such element.</summary>
    public bool Choose(long id)
    {
        if (shown is null || Path(shown.Roots, id) is not { } path)
        {
            return false;
        }
        foreach (Element ancestor in path.Take(path.Count - 1))
        {
            opened.Add(ancestor.Id);
            closed.Remove(ancestor.Id);
        }
        Rebuild(id);
        if (rows.FirstOrDefault(r => r.Element.Id == id) is { } row)
        {
            tree.ScrollIntoView(row);
        }
        return true;
    }

    /// <summary>Shows the chosen control's details, or why there are none, or nothing. The same details again (the tree asked
    /// again as the app's other controls come and go) leave what is shown as it is, so a link or row under the person's
    /// pointer is never swapped for its copy between press and release, which would lose the click.</summary>
    public void ShowDetails(ElementDetails? details, string? problem)
    {
        if (details is not null && Details is not null && Same(details, Details))
        {
            Details = details;
            return;
        }
        Details = details;
        heading.Children.Clear();
        styles.Children.Clear();
        layout.Children.Clear();
        if (details is null)
        {
            if (headingLink is not null)
            {
                headingLink.IsVisible = false;
            }
            heading.Children.Add(new TextBlock { Text = problem ?? "Choose a control in the tree, or with Select Element in the app.", Classes = { StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap });
            PropertyRows.Clear();
            pages.IsVisible = false;
            foreach (ToggleButton tab in tabs.Values)
            {
                tab.IsVisible = false;
            }
            return;
        }
        pages.IsVisible = true;
        editing = null;
        foreach ((LiveTreePage page, ToggleButton tab) in tabs)
        {
            tab.IsVisible = !PropertiesAlone || page == LiveTreePage.Properties;
        }
        StackPanel title = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock { Text = details.Type, FontWeight = FontWeight.SemiBold, FontSize = 14 });
        if (details.Name is { Length: > 0 } name)
        {
            title.Children.Add(Colored(new TextBlock { Text = $"#{name}", FontSize = 14 }, TokenKeys.Syntax_Field));
        }
        heading.Children.Add(title);
        string classes = string.Join(" ", details.Classes.Select(c => "." + c).Concat(details.PseudoClasses));
        if (classes.Length > 0)
        {
            heading.Children.Add(new TextBlock { Text = classes, Classes = { StyleClasses.Muted }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }
        if (details.TemplatedParent is { } parent)
        {
            heading.Children.Add(new TextBlock { Text = $"A part of {parent}'s template", Classes = { StyleClasses.Muted }, FontSize = 12 });
        }
        if (details.Source is { } source)
        {
            GoToSource = Link(Placed(source), source);
            heading.Children.Add(GoToSource);
        }
        else
        {
            GoToSource = null;
            heading.Children.Add(new TextBlock { Text = details.NoSource, Classes = { StyleClasses.Muted }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }
        if (headingLink is { } link)
        {
            heading.Children.Add(link);
        }
        ShowProperties();
        foreach (StyleReport style in details.Styles)
        {
            styles.Children.Add(StyleBlock(style));
        }
        if (details.Styles.Count == 0)
        {
            styles.Children.Add(new TextBlock { Text = "No style could apply to it.", Classes = { StyleClasses.Muted } });
        }
        ShowLayout(details.Layout);
    }

    /// <summary>The chosen control's link to its XAML, while it has one.</summary>
    public Button? GoToSource { get; private set; }

    /// <summary>The Styles page's blocks, a style each, in the order Avalonia takes them.</summary>
    public IReadOnlyList<Control> StyleBlocks => [.. styles.Children];

    public void ShowPage(LiveTreePage page)
    {
        Page = page;
        foreach ((LiveTreePage key, ToggleButton tab) in tabs)
        {
            tab.IsChecked = key == page;
        }
        foreach (Control content in pages.Children)
        {
            content.IsVisible = Equals(content.Tag, page);
        }
    }

    private void ShowProperties()
    {
        PropertyRows.Clear();
        if (Details is null)
        {
            return;
        }
        string typed = filter.Text?.Trim() ?? string.Empty;
        bool all = defaults.IsChecked == true;
        foreach (PropertyValue value in Details.Properties)
        {
            if ((all || value.Source != ValueSources.Default) && (typed.Length == 0 || value.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)))
            {
                PropertyRows.Add(value);
            }
        }
    }

    private void Rebuild(long? select)
    {
        choosing = true;
        try
        {
            rows.Show([.. (shown?.Roots ?? []).SelectMany(root => RowsOf(root, 0))]);
            if (select is long id && rows.FirstOrDefault(r => r.Element.Id == id) is { } row)
            {
                tree.SelectedItem = row;
            }
        }
        finally
        {
            choosing = false;
        }
    }

    /// <summary>An element's row, and the rows under it while it is open.</summary>
    private IEnumerable<LiveTreeRow> RowsOf(Element element, int depth)
    {
        LiveTreeRow row = new(element, depth, element.Children.Count > 0 && IsOpen(element, depth));
        yield return row;
        foreach (LiveTreeRow below in row.IsExpanded ? element.Children.SelectMany(child => RowsOf(child, depth + 1)) : [])
        {
            yield return below;
        }
    }

    /// <summary>Whether a row is open: as the person left it, else down to the app's own XAML (a template's parts closed),
    /// else, for an app built without source information, its first levels.</summary>
    private bool IsOpen(Element element, int depth) =>
        opened.Contains(element.Id) || (!closed.Contains(element.Id) && (sourced ? element.Children.Any(HasSourced) : depth < 6 && element.Children.Any(c => !c.FromTemplate)));

    private static bool HasSourced(Element element) => element.Sourced || element.Children.Any(HasSourced);

    private static List<Element>? Path(IReadOnlyList<Element> level, long id)
    {
        foreach (Element element in level)
        {
            if (element.Id == id)
            {
                return [element];
            }
            if (Path(element.Children, id) is { } below)
            {
                below.Insert(0, element);
                return below;
            }
        }
        return null;
    }

    /// <summary>Opens a row, or closes it, where it is: the rows round it, and the one chosen, stay as they are.</summary>
    public void Toggle(LiveTreeRow row)
    {
        long id = row.Element.Id;
        if (row.IsExpanded)
        {
            opened.Remove(id);
            closed.Add(id);
            row.IsExpanded = false;
            rows.Close(row);
        }
        else if (row.Expands)
        {
            closed.Remove(id);
            opened.Add(id);
            row.IsExpanded = true;
            rows.Open(row, row.Element.Children.SelectMany(child => RowsOf(child, row.Depth + 1)));
        }
    }

    /// <summary>A row for whichever element the list hands its container: its chevron, type, name and classes, faint where
    /// the element is hidden.</summary>
    private Control Row()
    {
        RowIcon chevron = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 4, 0) };
        TextBlock type = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock name = Colored(new TextBlock { Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, TokenKeys.Syntax_Field);
        TextBlock classes = new() { Classes = { StyleClasses.Muted }, Margin = new Thickness(6, 0, 0, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        StackPanel line = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Children = { chevron, type, name, classes } };
        chevron.PointerPressed += (_, e) =>
        {
            if (line.DataContext is LiveTreeRow row)
            {
                Toggle(row);
                e.Handled = true;
            }
        };
        return line.Showing<LiveTreeRow>(row =>
        {
            Element element = row.Element;
            chevron.Opacity = row.Expands ? 1 : 0;
            chevron.DataKey = row.IsExpanded ? IconKeys.ChevronDown : IconKeys.ChevronRight;
            type.Text = element.Type;
            type.Classes.Set(StyleClasses.Muted, element.FromTemplate);
            line.Margin = new Thickness(6 + (row.Depth * 12), 0, 8, 0);
            line.Opacity = element.Visible ? 1 : 0.5;
            name.IsVisible = element.Name is { Length: > 0 };
            name.Text = name.IsVisible ? $"#{element.Name}" : null;
            classes.IsVisible = element.Classes.Count > 0;
            classes.Text = classes.IsVisible ? string.Join(" ", element.Classes.Select(c => "." + c)) : null;
        });
    }

    /// <summary>A row for whichever property the list hands its container: its name (a breakpoint's dot before it where it
    /// stops the app), its value, and where the value comes from, a link to its place where it has one.</summary>
    private Control PropertyRow()
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("11*,9*"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(8, 3) };
        TextBlock label = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Controls.Shapes.Ellipse dot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, dot.GetResourceObservable(TokenKeys.Editor_Breakpoint));
        DockPanel.SetDock(dot, Dock.Left);
        DockPanel name = new() { Margin = new Thickness(0, 0, 8, 0), Children = { dot, label } };
        TextBlock shown = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }.InCodeFont();
        Grid.SetColumn(shown, 1);
        TextBox editor = new TextBox { FontSize = 12, IsVisible = false, VerticalAlignment = VerticalAlignment.Center }.InCodeFont();
        Grid.SetColumn(editor, 1);
        TextBlock linked = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        Button link = new() { Classes = { StyleClasses.Link }, Content = linked, HorizontalAlignment = HorizontalAlignment.Left };
        link.Click += (_, _) =>
        {
            if (grid.DataContext is PropertyValue { Place: { } place })
            {
                GoToRequested?.Invoke(this, place);
            }
        };
        TextBlock source = new() { Classes = { StyleClasses.Muted }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        foreach (Control from in new Control[] { link, source })
        {
            Grid.SetRow(from, 1);
            Grid.SetColumnSpan(from, 2);
        }
        grid.Children.Add(name);
        grid.Children.Add(shown);
        grid.Children.Add(editor);
        grid.Children.Add(link);
        grid.Children.Add(source);
        (ILiveValueCells From, LiveValueCell Cell)? made = null;
        editor.KeyDown += (_, e) =>
        {
            if (grid.DataContext is not PropertyValue value || e.Key is not (Key.Enter or Key.Escape))
            {
                return;
            }
            e.Handled = true;
            editing = null;
            Editing = null;
            if (e.Key == Key.Enter)
            {
                ValueSet?.Invoke(this, new PropertySet(value, editor.Text ?? string.Empty));
            }
            Show(value);
            properties.Focus();
        };
        editor.LostFocus += (_, _) =>
        {
            if (grid.DataContext is PropertyValue value && editing == value.Name)
            {
                editing = null;
                Editing = null;
                Show(value);
            }
        };
        PropertiesRefreshed += () =>
        {
            if (grid.DataContext is PropertyValue value)
            {
                Show(value);
            }
        };
        return grid.Showing<PropertyValue>(Show);

        void Show(PropertyValue value)
        {
            label.Text = value.Name;
            // A breakpoint's dot before a property that stops the app when it changes.
            dot.IsVisible = Watched?.Invoke(value) == true;
            ToolTip.SetTip(dot, dot.IsVisible ? $"The app stops wherever {value.Name} is changed" : null);
            ToolTip.SetTip(label, $"{value.Name} ({value.Type}, from {value.Owner})");
            shown.Text = value.Value;
            ToolTip.SetTip(shown, value.Value);
            bool typing = editing == value.Name;
            if (typing && !editor.IsVisible)
            {
                editor.Text = value.Value;
                Editing = editor;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    editor.Focus();
                    editor.SelectAll();
                });
            }
            (bool edits, bool notes) = (false, false);
            if (Cells is { } cells && Details is { } chosen)
            {
                if (made is not { } kept || kept.From != cells)
                {
                    if (made is { } old)
                    {
                        grid.Children.Remove(old.Cell.Editor);
                        grid.Children.Remove(old.Cell.Note);
                    }
                    LiveValueCell cell = cells.Make();
                    Grid.SetColumn(cell.Editor, 1);
                    Grid.SetRow(cell.Note, 1);
                    Grid.SetColumnSpan(cell.Note, 2);
                    grid.Children.Add(cell.Editor);
                    grid.Children.Add(cell.Note);
                    made = (cells, cell);
                }
                (edits, notes) = made.Value.Cell.Show(chosen.Id, value);
                edits &= !typing;
            }
            if (made is { } shownCell)
            {
                shownCell.Cell.Editor.IsVisible = edits;
                shownCell.Cell.Note.IsVisible = notes;
            }
            // An editor of a game's value takes more of the row than a value's words.
            grid.ColumnDefinitions[0].Width = new GridLength(made is null ? 11 : 8, GridUnitType.Star);
            grid.ColumnDefinitions[1].Width = new GridLength(made is null ? 9 : 12, GridUnitType.Star);
            editor.IsVisible = typing;
            shown.IsVisible = !typing && !edits;
            string from = value.Detail is { Length: > 0 } detail ? $"{value.Source} · {detail}" : value.Source;
            link.IsVisible = value.Place is not null && !notes;
            source.IsVisible = value.Place is null && !notes;
            if (value.Place is { } place)
            {
                linked.Text = $"{from} · {Placed(place)}";
                ToolTip.SetTip(link, $"Go to {place.File}{(place.Line > 0 ? $":{place.Line}" : string.Empty)}");
            }
            else
            {
                source.Text = from;
            }
        }
    }

    private Control StyleBlock(StyleReport style)
    {
        StackPanel block = new() { Spacing = 2 };
        DockPanel top = new();
        if (style.Place is { } place)
        {
            Button link = Link(Placed(place), place, small: true);
            DockPanel.SetDock(link, Dock.Right);
            top.Children.Add(link);
        }
        TextBlock selector = new TextBlock { Text = style.Selector, FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }.InCodeFont();
        top.Children.Add(selector);
        block.Children.Add(top);
        block.Children.Add(new TextBlock { Text = $"{style.Kind} · {style.Origin}", Classes = { StyleClasses.Muted }, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        TextBlock state = new() { Text = style.Applies ? "Applies" : $"Does not apply: {style.WhyNot}", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Colored(state, style.Applies ? TokenKeys.Test_Passed : TokenKeys.TextMuted);
        block.Children.Add(state);
        foreach (SetterReport setter in style.Setters)
        {
            block.Children.Add(Setter(setter));
        }
        if (!style.Applies)
        {
            selector.Opacity = 0.7;
        }
        return block;
    }

    private Control Setter(SetterReport setter)
    {
        StackPanel line = new() { Margin = new Thickness(14, 0, 0, 0) };
        TextBlock text = new TextBlock { Text = $"{setter.Property}: {setter.Value}", FontSize = 12, TextWrapping = TextWrapping.Wrap }.InCodeFont();
        switch (setter.State)
        {
            case SetterStates.Overridden:
                text.TextDecorations = TextDecorations.Strikethrough;
                text.Classes.Add(StyleClasses.Muted);
                break;
            case SetterStates.Inactive:
                text.Classes.Add(StyleClasses.Muted);
                break;
        }
        line.Children.Add(setter.Place is { } place ? Link(text, place) : text);
        if (AppPlaces.OverriddenBy(setter) is { } by)
        {
            line.Children.Add(new TextBlock { Text = $"overridden by {by}", Classes = { StyleClasses.Muted }, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 2) });
        }
        return line;
    }

    private void ShowLayout(LayoutInfo info)
    {
        // The content's box at the middle: the bounds less the border and padding.
        Edges inside = new((info.Border?.Left ?? 0) + (info.Padding?.Left ?? 0), (info.Border?.Top ?? 0) + (info.Padding?.Top ?? 0),
            (info.Border?.Right ?? 0) + (info.Padding?.Right ?? 0), (info.Border?.Bottom ?? 0) + (info.Padding?.Bottom ?? 0));
        double width = Math.Max(0, info.Bounds.Width - inside.Left - inside.Right);
        double height = Math.Max(0, info.Bounds.Height - inside.Top - inside.Bottom);
        Control content = Centered($"{Number(width)} × {Number(height)}");
        Control padded = info.Padding is { } padding ? Boxed("padding", padding, content) : content;
        Control bordered = info.Border is { } border ? Boxed("border", border, padded) : padded;
        layout.Children.Add(Boxed("margin", info.Margin, bordered));
        Box where = info.InWindow ?? info.Bounds;
        layout.Children.Add(Fact("Size", $"{Number(info.Bounds.Width)} × {Number(info.Bounds.Height)}"));
        layout.Children.Add(Fact("In its window", $"{Number(where.X)}, {Number(where.Y)}"));
        layout.Children.Add(Fact("In its parent", $"{Number(info.Bounds.X)}, {Number(info.Bounds.Y)}"));
        layout.Children.Add(Fact("Desired size", $"{Number(info.DesiredWidth)} × {Number(info.DesiredHeight)}"));
        layout.Children.Add(Fact("Alignment", $"{info.HorizontalAlignment}, {info.VerticalAlignment}"));
    }

    /// <summary>One of the layout's boxes: its name in its corner, its four sides' sizes around what it holds.</summary>
    private static Control Boxed(string label, Edges edges, Control inner)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        TextBlock name = new() { Text = label, Classes = { StyleClasses.Muted }, FontSize = 10, Margin = new Thickness(4, 2) };
        grid.Children.Add(name);
        grid.Children.Add(Side(edges.Top, 0, 1));
        grid.Children.Add(Side(edges.Left, 1, 0));
        Grid.SetRow(inner, 1);
        Grid.SetColumn(inner, 1);
        grid.Children.Add(inner);
        grid.Children.Add(Side(edges.Right, 1, 2));
        grid.Children.Add(Side(edges.Bottom, 2, 1));
        Border box = new() { BorderThickness = new Thickness(1), Padding = new Thickness(2), Child = grid };
        box.Bind(Border.BorderBrushProperty, box.GetResourceObservable(TokenKeys.Divider));
        return box;
    }

    private static TextBlock Side(double size, int row, int column)
    {
        TextBlock side = new() { Text = Number(size), FontSize = 11, Margin = new Thickness(6, 2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(side, row);
        Grid.SetColumn(side, column);
        return side;
    }

    private static Control Centered(string text)
    {
        Border content = new()
        {
            Padding = new Thickness(10, 6),
            Child = new TextBlock { Text = text, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center }.InCodeFont(),
        };
        content.Bind(Border.BackgroundProperty, content.GetResourceObservable(TokenKeys.Selected));
        return content;
    }

    private static Control Fact(string name, string value)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("140,*") };
        TextBlock label = new() { Text = name, Classes = { StyleClasses.Muted } };
        TextBlock shown = new TextBlock { Text = value, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.InCodeFont();
        Grid.SetColumn(shown, 1);
        grid.Children.Add(label);
        grid.Children.Add(shown);
        return grid;
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Whether two looks at a control found the same: every value alike, their lists item by item (a record compares
    /// its lists by reference).</summary>
    private static bool Same(ElementDetails a, ElementDetails b) =>
        a with { Classes = b.Classes, PseudoClasses = b.PseudoClasses, Properties = b.Properties, Styles = b.Styles } == b
        && a.Classes.SequenceEqual(b.Classes)
        && a.PseudoClasses.SequenceEqual(b.PseudoClasses)
        && a.Properties.SequenceEqual(b.Properties)
        && a.Styles.Count == b.Styles.Count
        && a.Styles.Zip(b.Styles).All(s => s.First with { Setters = s.Second.Setters } == s.Second && s.First.Setters.SequenceEqual(s.Second.Setters));

    /// <summary>A place in XAML as the window writes it: the file's name and the line, or the file alone for its root.</summary>
    public static string Placed(SourcePlace place) =>
        place.Line > 0 ? $"{System.IO.Path.GetFileName(place.File)}:{place.Line}" : System.IO.Path.GetFileName(place.File);

    private Button Link(string text, SourcePlace place, bool small = false) =>
        Link(new TextBlock { Text = text, FontSize = small ? 11 : 12, TextTrimming = TextTrimming.CharacterEllipsis }, place);

    private Button Link(Control content, SourcePlace place)
    {
        Button link = new() { Classes = { StyleClasses.Link }, Content = content, HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(link, $"Go to {place.File}{(place.Line > 0 ? $":{place.Line}" : string.Empty)}");
        link.Click += (_, _) => GoToRequested?.Invoke(this, place);
        return link;
    }

    private static T Colored<T>(T text, string token) where T : TextBlock
    {
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(token));
        return text;
    }

    private static PathIcon Icon(string key)
    {
        PathIcon icon = new();
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(key));
        return icon;
    }
}
