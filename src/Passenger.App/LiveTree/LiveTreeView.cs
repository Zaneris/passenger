using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.CSharp;
using Passenger.Inspector;
using Passenger.App.ToolWindows;

namespace Passenger.App.LiveTree;

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
/// the styles that apply and those that do not with why, and its layout's boxes. The window only shows;
/// <see cref="LiveTreeController"/> asks the app.</summary>
public sealed class LiveTreeView : DockPanel
{
    private readonly TreeRows<LiveTreeRow> rows = [];
    private readonly ListBox tree;
    private readonly TextBlock placeholder;
    private readonly TextBlock status;
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

    /// <summary>Rows the person opened or closed themselves, which keep that over the tree's own choice.</summary>
    private readonly HashSet<long> opened = [];
    private readonly HashSet<long> closed = [];

    private AppTree? shown;
    private bool sourced;
    private bool choosing;

    public LiveTreeView()
    {
        SelectElement = ToolButtons.Toggle(IconKeys.SelectElement, "Select Element: point at a control in the app and click it");
        SelectElement.Click += (_, _) => PickRequested?.Invoke(this, SelectElement.IsChecked == true);
        Button refresh = ToolButtons.Of(IconKeys.Rerun, "Refresh", () => RefreshRequested?.Invoke(this, EventArgs.Empty));
        status = new TextBlock { Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel top = new() { Margin = new Thickness(6, 0) };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { SelectElement, refresh } };
        DockPanel.SetDock(buttons, Dock.Left);
        top.Children.Add(buttons);
        top.Children.Add(status);
        Children.Add(ToolBars.Top(top));

        bannerText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        banner = new Border { [DockProperty] = Dock.Top, Padding = new Thickness(10, 6), IsVisible = false, Child = bannerText };
        banner.Bind(Border.BackgroundProperty, banner.GetResourceObservable(TokenKeys.Banner));
        Children.Add(banner);

        tree = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = rows,
            ItemTemplate = new FuncDataTemplate<LiveTreeRow>((row, _) => row is null ? new TextBlock() : Row(row), supportsRecycling: false),
        };
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
        placeholder = new TextBlock { Classes = { "emptyText" } };

        heading = new StackPanel { [DockProperty] = Dock.Top, Margin = new Thickness(10, 8, 10, 4), Spacing = 2 };
        StackPanel tabRow = new() { [DockProperty] = Dock.Top, Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 2, 6, 4) };
        foreach (LiveTreePage page in Enum.GetValues<LiveTreePage>())
        {
            ToggleButton tab = new() { Classes = { "panelTab" }, Content = page.ToString() };
            tab.Click += (_, _) => ShowPage(page);
            tabs[page] = tab;
            tabRow.Children.Add(tab);
        }
        filter = new TextBox { Classes = { "search" }, PlaceholderText = "Filter properties", Margin = new Thickness(0, 0, 6, 0) };
        filter.TextChanged += (_, _) => ShowProperties();
        // An option toggle with words, as wide as they are, where the style's are an icon's square.
        defaults = new ToggleButton { Classes = { "option" }, Content = "Defaults", FontSize = 12, Width = double.NaN, Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(defaults, "Show the properties at their default values too");
        defaults.Click += (_, _) => ShowProperties();
        DockPanel filterRow = new() { [DockProperty] = Dock.Top, Margin = new Thickness(8, 2, 8, 6) };
        DockPanel.SetDock(defaults, Dock.Right);
        filterRow.Children.Add(defaults);
        filterRow.Children.Add(filter);
        properties = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = PropertyRows,
            ItemTemplate = new FuncDataTemplate<PropertyValue>((value, _) => value is null ? new TextBlock() : PropertyRow(value), supportsRecycling: false),
        };
        properties.DoubleTapped += (_, _) =>
        {
            if (properties.SelectedItem is PropertyValue { Place: { } place })
            {
                GoToRequested?.Invoke(this, place);
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
        Panel treePane = new() { Children = { tree, placeholder } };
        Grid.SetRow(treePane, 0);
        HairlineDivider divider = new(GridResizeDirection.Rows) { [Grid.RowProperty] = 1 };
        Grid.SetRow(details, 2);
        split.Children.Add(treePane);
        split.Children.Add(divider);
        split.Children.Add(details);
        Children.Add(split);
        ShowPage(LiveTreePage.Properties);
        ShowDetails(null, null);
        ShowStatus("Debug an Avalonia or WPF app to see its windows and controls here.", null);
    }

    /// <summary>A control chosen in the tree.</summary>
    public event EventHandler<long>? ElementChosen;

    /// <summary>Select Element pressed (on) or let go (off).</summary>
    public event EventHandler<bool>? PickRequested;

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

    /// <summary>The Properties page's rows again, as their breakpoints changed.</summary>
    public void RefreshProperties()
    {
        PropertyValue? kept = properties.SelectedItem as PropertyValue;
        ShowProperties();
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
        ToolTip.SetShowOnDisabled(watch, true);
        List<Control> items = [watch];
        if (value.Place is { } place)
        {
            items.Add(ToolWindows.ListMenus.Item("Go to Source", null, () => GoToRequested?.Invoke(this, place)));
        }
        return items;
    }

    /// <summary>The menu item's words.</summary>
    public const string BreakWhenValueChanges = "Break When Value Changes";

    /// <summary>Select Element, pressed while it is on.</summary>
    public ToggleButton SelectElement { get; }

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
        placeholder.IsVisible = message is not null && rows.Count == 0;
        status.Text = line ?? string.Empty;
    }

    /// <summary>A note over the tree that what it shows is not live (the app paused), or none.</summary>
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
        placeholder.IsVisible = rows.Count == 0 && placeholder.Text is { Length: > 0 };
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
            heading.Children.Add(new TextBlock { Text = problem ?? "Choose a control in the tree, or with Select Element in the app.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
            PropertyRows.Clear();
            pages.IsVisible = false;
            foreach (ToggleButton tab in tabs.Values)
            {
                tab.IsVisible = false;
            }
            return;
        }
        pages.IsVisible = true;
        foreach (ToggleButton tab in tabs.Values)
        {
            tab.IsVisible = true;
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
            heading.Children.Add(new TextBlock { Text = classes, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }
        if (details.TemplatedParent is { } parent)
        {
            heading.Children.Add(new TextBlock { Text = $"A part of {parent}'s template", Classes = { "muted" }, FontSize = 12 });
        }
        if (details.Source is { } source)
        {
            GoToSource = Link(Placed(source), source);
            heading.Children.Add(GoToSource);
        }
        else
        {
            GoToSource = null;
            heading.Children.Add(new TextBlock { Text = details.NoSource, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }
        ShowProperties();
        foreach (StyleReport style in details.Styles)
        {
            styles.Children.Add(StyleBlock(style));
        }
        if (details.Styles.Count == 0)
        {
            styles.Children.Add(new TextBlock { Text = "No style could apply to it.", Classes = { "muted" } });
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

    private Control Row(LiveTreeRow row)
    {
        Element element = row.Element;
        PathIcon chevron = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 4, 0), Opacity = row.Expands ? 1 : 0 };
        void Point() => chevron.Bind(PathIcon.DataProperty, chevron.GetResourceObservable(row.IsExpanded ? IconKeys.ChevronDown : IconKeys.ChevronRight));
        Point();
        row.FollowWhileShown(chevron, (_, _) => Point());
        chevron.PointerPressed += (_, e) =>
        {
            Toggle(row);
            e.Handled = true;
        };
        TextBlock type = new() { Text = element.Type, VerticalAlignment = VerticalAlignment.Center };
        if (element.FromTemplate)
        {
            type.Classes.Add("muted");
        }
        StackPanel line = new()
        {
            Orientation = Orientation.Horizontal,
            Height = ListRow.Height,
            Margin = new Thickness(6 + (row.Depth * 12), 0, 8, 0),
            Opacity = element.Visible ? 1 : 0.5,
            Children = { chevron, type },
        };
        if (element.Name is { Length: > 0 } name)
        {
            line.Children.Add(Colored(new TextBlock { Text = $"#{name}", Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }, TokenKeys.Syntax_Field));
        }
        if (element.Classes.Count > 0)
        {
            line.Children.Add(new TextBlock
            {
                Text = string.Join(" ", element.Classes.Select(c => "." + c)),
                Classes = { "muted" },
                Margin = new Thickness(6, 0, 0, 0),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        return line;
    }

    private Control PropertyRow(PropertyValue value)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("11*,9*"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(8, 3) };
        TextBlock label = new() { Text = value.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        DockPanel name = new() { Margin = new Thickness(0, 0, 8, 0), Children = { label } };
        if (Watched?.Invoke(value) == true)
        {
            // A breakpoint's dot before a property that stops the app when it changes.
            Avalonia.Controls.Shapes.Ellipse dot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, dot.GetResourceObservable(TokenKeys.Editor_Breakpoint));
            ToolTip.SetTip(dot, $"The app stops wherever {value.Name} is changed");
            DockPanel.SetDock(dot, Dock.Left);
            name.Children.Insert(0, dot);
        }
        ToolTip.SetTip(label, $"{value.Name} ({value.Type}, from {value.Owner})");
        TextBlock shown = new() { Text = value.Value, FontFamily = Fonts.Mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(shown, value.Value);
        Grid.SetColumn(shown, 1);
        string source = value.Detail is { Length: > 0 } detail ? $"{value.Source} · {detail}" : value.Source;
        Control from = value.Place is { } place
            ? Link($"{source} · {Placed(place)}", place, small: true)
            : new TextBlock { Text = source, Classes = { "muted" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(from, 1);
        Grid.SetColumnSpan(from, 2);
        grid.Children.Add(name);
        grid.Children.Add(shown);
        grid.Children.Add(from);
        return grid;
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
        TextBlock selector = new() { Text = style.Selector, FontFamily = Fonts.Mono, FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        top.Children.Add(selector);
        block.Children.Add(top);
        block.Children.Add(new TextBlock { Text = $"{style.Kind} · {style.Origin}", Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap });
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
        TextBlock text = new() { Text = $"{setter.Property}: {setter.Value}", FontFamily = Fonts.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        switch (setter.State)
        {
            case SetterStates.Overridden:
                text.TextDecorations = TextDecorations.Strikethrough;
                text.Classes.Add("muted");
                break;
            case SetterStates.Inactive:
                text.Classes.Add("muted");
                break;
        }
        line.Children.Add(setter.Place is { } place ? Link(text, place) : text);
        if (AppPlaces.OverriddenBy(setter) is { } by)
        {
            line.Children.Add(new TextBlock { Text = $"overridden by {by}", Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 2) });
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
        TextBlock name = new() { Text = label, Classes = { "muted" }, FontSize = 10, Margin = new Thickness(4, 2) };
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
            Child = new TextBlock { Text = text, FontFamily = Fonts.Mono, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
        };
        content.Bind(Border.BackgroundProperty, content.GetResourceObservable(TokenKeys.Selected));
        return content;
    }

    private static Control Fact(string name, string value)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("140,*") };
        TextBlock label = new() { Text = name, Classes = { "muted" } };
        TextBlock shown = new() { Text = value, FontFamily = Fonts.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
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
        Button link = new() { Classes = { "link" }, Content = content, HorizontalAlignment = HorizontalAlignment.Left };
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
