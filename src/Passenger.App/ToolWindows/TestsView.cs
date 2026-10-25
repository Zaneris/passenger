using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.App.Testing;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.ToolWindows;

/// <summary>The Unit Tests window: the tests grouped as its Group By chooses (by project and namespace at first), then by
/// class and method, a theory's data rows under it, each marked with how it last ran and how long it took; the chosen
/// one's message, stack trace (its frames links to their lines), output and attachments beside the tree, the pictures
/// among them (a failing headless test's windows) as thumbnails over it; above it the toolbar (Expand All, Collapse All,
/// Group By, a filter for each outcome with how many tests have it, the search) and the run's tally; and while there are
/// none, why. Double-click or F4 goes to a test's source.</summary>
public sealed class TestsView : DockPanel
{
    /// <summary>The most failed tests a group's details show, so choosing a project of thousands shows at once.</summary>
    private const int GroupDetailsLimit = 100;

    /// <summary>The outcome filters after All: the outcome, the name for its tests, and its mark as the tree draws
    /// it.</summary>
    private static readonly (TestState State, string Name, string Icon, string Brush)[] Filters =
    [
        (TestState.Passed, "Passed Tests", IconKeys.TestPassed, TokenKeys.Test_Passed),
        (TestState.Failed, "Failed Tests", IconKeys.TestFailed, TokenKeys.Test_Failed),
        (TestState.Skipped, "Ignored Tests", IconKeys.TestSkipped, TokenKeys.Test_Skipped),
        (TestState.NotRun, "Tests Not Run", IconKeys.TestNotRun, TokenKeys.Test_NotRun),
        (TestState.Running, "Running Tests", IconKeys.TestRunning, TokenKeys.Test_Running),
    ];

    private readonly ListBox tree;
    private readonly TextDocument detailsText = new();
    private readonly DockPanel result;

    /// <summary>The details' editor, made as the window is first shown or they are asked for (<see cref="Details"/>).</summary>
    private CodeEditor? details;
    private readonly TextBlock status;
    private readonly TextBlock placeholder;
    private readonly TextBox search;
    private readonly Button groupBy;
    private readonly Button stop;
    private readonly ToggleButton all;
    private readonly TextBlock allCount;
    private readonly Dictionary<TestState, (ToggleButton Button, TextBlock Count)> filters = [];
    private bool detailsQueued;
    private bool countsQueued;

    /// <summary>Whether the filter being clicked adds to those chosen (Ctrl, ⌘ or Shift held) rather than replacing them.</summary>
    private bool adding;

    public TestsView(TestTree model)
    {
        Model = model;
        tree = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = model.Rows,
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = new FuncDataTemplate<TestNode>((_, _) => new TestRow(model), supportsRecycling: true),
        };
        tree.SelectionChanged += (_, _) => ShowDetails();
        tree.DoubleTapped += (_, _) =>
        {
            if (tree.SelectedItem is not TestNode node)
            {
                return;
            }
            if (node.HasChildren && node.Kind != TestNodeKind.Method)
            {
                model.Toggle(node);
            }
            else
            {
                GoToRequested?.Invoke(this, node);
            }
        };
        TreeKeys.Attach<TestNode>(tree, model.Toggle, GoTo);
        ListMenus.Attach(tree, () => tree.SelectedItem is not TestNode chosen ? [] :
        [
            ListMenus.Item("Run", Keys(AppActions.RunTests), () => RunRequested?.Invoke(this, false)),
            ListMenus.Item("Debug", Keys(AppActions.DebugTests), () => RunRequested?.Invoke(this, true)),
            ListMenus.Item("Run Until Failure", Keys(AppActions.RunTestsUntilFailure), () => UntilFailureRequested?.Invoke(this, EventArgs.Empty)),
            new Separator(),
            ListMenus.Item("Go to Source", "F4", () => GoToRequested?.Invoke(this, chosen), chosen.Path is not null),
        ]);
        // Results come by the thousand: the details and the filters' counts are written again once they have, not once each.
        model.ResultApplied += (_, _) =>
        {
            QueueDetails();
            QueueCounts();
        };
        model.Updated += (_, _) =>
        {
            QueueDetails();
            QueueCounts();
        };
        model.ViewChanged += (_, _) => ShowFilters();
        status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        status.Classes.Add("muted");
        placeholder = new TextBlock { Text = model.EmptyText, Classes = { "emptyText" } };
        model.Rows.CollectionChanged += (_, _) => ShowPlaceholder();
        model.EmptyTextChanged += (_, _) => ShowPlaceholder();

        Grid split = new() { ColumnDefinitions = new ColumnDefinitions("2*,1,3*") };
        HairlineDivider divider = new(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 };
        DockPanel.SetDock(Frames, Dock.Top);
        result = new() { [Grid.ColumnProperty] = 2, Children = { Frames } };
        result.SizeChanged += (_, e) => Frames.Fit(e.NewSize.Height);
        split.Children.Add(new Panel { Children = { tree, placeholder } });
        split.Children.Add(divider);
        split.Children.Add(result);

        // Above the tree: what changes it, then a filter for each outcome; the search on the right.
        PathIcon magnifier = new() { Width = 14, Height = 14, Margin = new Thickness(8, 0, 0, 0) };
        magnifier.Bind(PathIcon.DataProperty, magnifier.GetResourceObservable(IconKeys.Search));
        search = new TextBox
        {
            [DockProperty] = Dock.Right,
            Classes = { "search" },
            Width = 220,
            Margin = new Thickness(6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = $"{(OperatingSystem.IsMacOS() ? "⌘F" : "Ctrl+F")} to filter",
            InnerLeftContent = magnifier,
        };
        search.TextChanged += (_, _) => model.SearchFor(search.Text ?? string.Empty);
        search.KeyDown += OnSearchKey;
        groupBy = ToolButtons.Of(IconKeys.GroupBy, "Group By", ShowGroupBy);
        all = Filter(IconKeys.TestAll, TokenKeys.Icon, "All Tests", out allCount);
        all.Click += (_, _) =>
        {
            model.ShowOutcomes([]);
            // A click toggles the button itself; what it shows is what was chosen.
            ShowFilters();
        };
        StackPanel bar = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                ToolButtons.Of(IconKeys.ExpandAll, "Expand All", model.ExpandAll),
                ToolButtons.Of(IconKeys.CollapseAll, "Collapse All", model.CollapseAll),
                groupBy,
                Divider(),
                all,
            },
        };
        foreach ((TestState state, string name, string icon, string brush) in Filters)
        {
            ToggleButton button = Filter(icon, brush, name, out TextBlock count);
            button.Click += (_, _) => Choose(state);
            filters[state] = (button, count);
            bar.Children.Add(button);
        }
        DockPanel top = new() { Children = { search, bar, status } };
        DockPanel.SetDock(bar, Dock.Left);
        Border topBar = ToolBars.Top(top);
        ShowFilters();
        ShowPlaceholder();
        stop = ToolButtons.Of(AppActions.StopTests, IconKeys.Stop, "Stop", Request);
        stop.IsEnabled = false;
        Border leftBar = ToolBars.Side(
            ToolButtons.Of(AppActions.RunAllTests, IconKeys.Run, "Run All Tests", Request),
            ToolButtons.Of(AppActions.RunTests, IconKeys.RunOutline, "Run Selected Tests", Request),
            ToolButtons.Of(AppActions.DebugTests, IconKeys.Debug, "Debug Selected Tests", Request),
            ToolButtons.Of(AppActions.RerunFailedTests, IconKeys.Rerun, "Rerun Failed Tests", Request),
            stop);
        Children.Add(leftBar);
        Children.Add(topBar);
        Children.Add(split);
    }

    public TestTree Model { get; }

    /// <summary>The chosen row's results: its message, stack trace and output.</summary>
    public CodeEditor Details => details ?? MakeDetails();

    /// <summary>Whether the details' editor has been made, as the window was shown or they were asked for.</summary>
    public bool HasDetails => details is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = Details;
    }

    /// <summary>The details' editor over their text so far, from its top.</summary>
    private CodeEditor MakeDetails()
    {
        details = new CodeEditor
        {
            Document = detailsText,
            IsReadOnly = true,
            LineCommentPrefix = null,
            ShowLineNumbers = false,
            ShowErrorStripe = false,
            FontSize = 12,
            Highlighter = Links,
            Links = Links.On,
            Margin = new Thickness(6, 4, 0, 0),
        };
        details.LinkClicked += (_, link) => LinkClicked?.Invoke(this, (LinkTarget)link.Target);
        result.Children.Add(details);
        return details;
    }

    /// <summary>The pictures the chosen row's results carry, above its details: a failing headless test's windows.</summary>
    public TestFramesStrip Frames { get; } = new();

    /// <summary>The details' file locations and web addresses; its folders are where a relative path is looked for.</summary>
    public ConsoleLinks Links { get; } = new();

    /// <summary>A link in the details was clicked: a frame's file and line to open, or an address for the browser.</summary>
    public event EventHandler<LinkTarget>? LinkClicked;

    /// <summary>A toolbar button pressed: the app action it runs.</summary>
    public event EventHandler<string>? ActionRequested;

    private void Request(string action) => ActionRequested?.Invoke(this, action);

    /// <summary>The rows chosen to run (false) or debug (true), from their menu.</summary>
    public event EventHandler<bool>? RunRequested;

    /// <summary>The rows chosen to run until one fails, from their menu.</summary>
    public event EventHandler? UntilFailureRequested;

    /// <summary>Chooses a row and shows its details (a failed test's output), opening what holds it.</summary>
    public void Choose(TestNode node)
    {
        Model.Reveal(node);
        tree.SelectedItem = node;
        tree.ScrollIntoView(node);
    }

    /// <summary>A test's source to show.</summary>
    public event EventHandler<TestNode>? GoToRequested;

    private static string? Keys(string action) =>
        AppActions.Keymap().ShortcutsFor(action).FirstOrDefault(s => s.Second is null) is { } shortcut ? new Avalonia.Input.KeyGesture(shortcut.First.Key, shortcut.First.Modifiers).ToString() : null;

    /// <summary>The rows chosen, for Run Selected and Debug Selected.</summary>
    public IReadOnlyList<TestNode> Selected => [.. tree.SelectedItems?.OfType<TestNode>() ?? []];

    public bool HasFocus => tree.IsKeyboardFocusWithin;

    public string Status
    {
        get => status.Text ?? string.Empty;
        set => status.Text = value;
    }

    /// <summary>Tests are being found or run: Stop has something to end, and is disabled while it has not.</summary>
    public bool IsRunning
    {
        get => stop.IsEnabled;
        set => stop.IsEnabled = value;
    }

    /// <summary>The chosen row's details: a method's results, or the failures under a group, the first hundred of them.</summary>
    public void ShowDetails()
    {
        if (tree.SelectedItem is not TestNode node)
        {
            Show(string.Empty);
            Frames.Show(string.Empty, []);
            return;
        }
        if (node.Kind is TestNodeKind.Method or TestNodeKind.Row)
        {
            Show(node.Details);
            Frames.Show(node.Name, node.Frames);
            return;
        }
        Frames.Show(string.Empty, []);
        // A test under two categories is one failure.
        IEnumerable<TestNode> failing = node.Methods.Where(m => m.State == TestState.Failed).DistinctBy(m => m.Identity);
        List<TestNode> failed = [.. failing.Take(GroupDetailsLimit + 1)];
        string shown = string.Join("\n\n", failed.Take(GroupDetailsLimit).Select(m => $"{m.Name}\n{m.Details}"));
        if (failed.Count > GroupDetailsLimit)
        {
            int more = failing.Count() - GroupDetailsLimit;
            shown += $"\n\n…and {more} more failed";
        }
        Show(shown);
    }

    /// <summary>The details' text, written only when it changed, from its top.</summary>
    private void Show(string text)
    {
        TextDocument document = detailsText;
        if (document.Snapshot.Length == text.Length && document.Snapshot.ToString() == text)
        {
            return;
        }
        document.Rewrite(0, document.Snapshot.Length, text);
        if (details is not null)
        {
            details.Selections = SelectionSet.Single(0);
            details.ScrollTo(default);
        }
    }

    private void QueueDetails()
    {
        if (detailsQueued || tree.SelectedItem is null)
        {
            return;
        }
        detailsQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            detailsQueued = false;
            ShowDetails();
        }, DispatcherPriority.Background);
    }

    /// <summary>Goes to a class's, a method's or a data row's code; false for a group, which stands for no code of its own.</summary>
    private bool GoTo(TestNode node)
    {
        if (node.Kind is not (TestNodeKind.Class or TestNodeKind.Method or TestNodeKind.Row))
        {
            return false;
        }
        GoToRequested?.Invoke(this, node);
        return true;
    }

    /// <summary>The search, its text chosen, from Find's keys while the window has them.</summary>
    public void FocusSearch()
    {
        search.Focus();
        search.SelectAll();
    }

    /// <summary>The Group By menu while it is open.</summary>
    public ContextMenu? OpenMenu { get; private set; }

    /// <summary>An outcome's filter: its mark and how many tests have it. A click with Ctrl, ⌘ or Shift held adds it to
    /// those chosen.</summary>
    private ToggleButton Filter(string icon, string brush, string name, out TextBlock count)
    {
        PathIcon mark = new();
        mark.Bind(PathIcon.DataProperty, mark.GetResourceObservable(icon));
        mark.Bind(PathIcon.ForegroundProperty, mark.GetResourceObservable(brush));
        count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        ToggleButton button = new()
        {
            Classes = { "outcome" },
            Focusable = false,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { mark, count } },
        };
        ToolTip.SetTip(button, $"Filter {name}\nHold {(OperatingSystem.IsMacOS() ? "⌘" : "Ctrl")} to select multiple filters");
        button.AddHandler(PointerPressedEvent, (_, e) => adding = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift)) != 0,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        return button;
    }

    private Border Divider()
    {
        Border line = new() { Width = 1, Height = 18, Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
        line.Bind(Border.BackgroundProperty, line.GetResourceObservable(TokenKeys.Divider));
        return line;
    }

    /// <summary>An outcome's filter clicked: that outcome alone (All again if it was so already), or with a modifier held
    /// that outcome added to or taken from those chosen.</summary>
    private void Choose(TestState state)
    {
        bool add = adding;
        adding = false;
        HashSet<TestState> next = add ? [.. Model.Outcomes] : [];
        if (add)
        {
            if (!next.Remove(state))
            {
                next.Add(state);
            }
        }
        else if (Model.Outcomes.Count != 1 || !Model.Outcomes.Contains(state))
        {
            next.Add(state);
        }
        Model.ShowOutcomes(next);
        // A click toggles the button itself; what it shows is what was chosen.
        ShowFilters();
    }

    /// <summary>The filters as chosen, each with how many tests have its outcome; one no test has hidden unless it is
    /// chosen.</summary>
    private void ShowFilters()
    {
        int[] counts = Model.Counts();
        allCount.Text = counts.Sum().ToString(System.Globalization.CultureInfo.CurrentCulture);
        all.IsChecked = Model.Outcomes.Count == 0;
        foreach ((TestState state, (ToggleButton button, TextBlock count)) in filters)
        {
            count.Text = counts[(int)state].ToString(System.Globalization.CultureInfo.CurrentCulture);
            button.IsChecked = Model.Outcomes.Contains(state);
            button.IsVisible = counts[(int)state] > 0 || Model.Outcomes.Contains(state);
        }
    }

    private void QueueCounts()
    {
        if (countsQueued)
        {
            return;
        }
        countsQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            countsQueued = false;
            ShowFilters();
        }, DispatcherPriority.Background);
    }

    /// <summary>Why the tree has no rows: none yet, or none the filters and search let through.</summary>
    private void ShowPlaceholder()
    {
        placeholder.IsVisible = Model.Rows.Count == 0;
        placeholder.Text = Model.Roots.Count > 0 ? "No matching tests" : Model.EmptyText;
    }

    /// <summary>Group By: the predefined groupings, the one in use ticked, then each level, ticked while it is on.</summary>
    private void ShowGroupBy()
    {
        List<Control> items = [];
        foreach ((string name, IReadOnlyList<TestGroupLevel> levels) in TestGroupings.Predefined)
        {
            MenuItem item = new() { Header = name, ToggleType = MenuItemToggleType.Radio, IsChecked = levels.SequenceEqual(Model.Grouping) };
            item.Click += (_, _) => Model.Group(levels);
            items.Add(item);
        }
        items.Add(new Separator());
        foreach (TestGroupLevel level in Enum.GetValues<TestGroupLevel>())
        {
            bool on = Model.Grouping.Contains(level);
            MenuItem item = new() { Header = TestGroupings.Title(level), ToggleType = MenuItemToggleType.CheckBox, IsChecked = on };
            item.Click += (_, _) => Model.Group(on ? Model.Grouping.Where(l => l != level) : [.. Model.Grouping, level]);
            items.Add(item);
        }
        ContextMenu menu = new() { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft };
        OpenMenu?.Close();
        OpenMenu = menu;
        menu.Closed += (_, _) => OpenMenu = OpenMenu == menu ? null : OpenMenu;
        menu.Open(groupBy);
    }

    /// <summary>The search's keys: Down or Enter goes to the tree, Escape empties the search, then goes there.</summary>
    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when !string.IsNullOrEmpty(search.Text):
                search.Text = string.Empty;
                e.Handled = true;
                break;
            case Key.Down or Key.Enter or Key.Escape:
                if (tree.SelectedItem is null && Model.Rows.Count > 0)
                {
                    tree.SelectedIndex = 0;
                }
                Control? row = tree.SelectedItem is { } chosen ? tree.ContainerFromItem(chosen) : null;
                (row ?? tree).Focus();
                e.Handled = true;
                break;
        }
    }

    private static string Format(TimeSpan duration) =>
        duration.TotalSeconds >= 1 ? $"{duration.TotalSeconds:0.0} s" : $"{Math.Max(1, (int)duration.TotalMilliseconds)} ms";

    /// <summary>A row of the tree for whichever node the list gives it, recycled as the list scrolls: it follows its node's
    /// changes only while it is shown, and lets go of them when the list takes it away or gives it another node.</summary>
    private sealed class TestRow : StackPanel
    {
        private static readonly string[] StateClasses = ["running", "passed", "failed", "skipped"];

        private readonly PathIcon chevron;
        private readonly RotateTransform turn = new();
        private readonly PathIcon state;
        private readonly TextBlock label;
        private readonly TextBlock duration;
        private TestNode? followed;
        private bool attached;

        public TestRow(TestTree model)
        {
            Orientation = Orientation.Horizontal;
            Height = ListRow.Height;
            chevron = new PathIcon { Classes = { "chevron" }, RenderTransformOrigin = RelativePoint.Center, RenderTransform = turn };
            chevron.Bind(PathIcon.DataProperty, chevron.GetResourceObservable(IconKeys.ChevronRight));
            Border toggle = new() { Width = 18, Height = ListRow.Height, Background = Brushes.Transparent, Child = chevron };
            toggle.PointerPressed += (_, e) =>
            {
                if (DataContext is TestNode node)
                {
                    model.Toggle(node);
                    e.Handled = true;
                }
            };
            state = new PathIcon { Classes = { "testState" }, VerticalAlignment = VerticalAlignment.Center };
            label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            duration = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 12 };
            duration.Classes.Add("muted");
            Children.Add(toggle);
            Children.Add(state);
            Children.Add(label);
            Children.Add(duration);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            Follow();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            attached = true;
            Follow();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            attached = false;
            Follow();
        }

        private void Follow()
        {
            TestNode? node = DataContext as TestNode;
            TestNode? following = attached ? node : null;
            if (following != followed)
            {
                if (followed is not null)
                {
                    followed.PropertyChanged -= OnChanged;
                }
                followed = following;
                if (following is not null)
                {
                    following.PropertyChanged += OnChanged;
                }
            }
            if (node is null)
            {
                return;
            }
            Margin = node.Indent;
            label.Text = node.Name;
            chevron.IsVisible = node.HasChildren;
            ShowExpanded(node);
            ShowState(node);
        }

        private void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not TestNode node)
            {
                return;
            }
            switch (e.PropertyName)
            {
                case nameof(TestNode.State) or nameof(TestNode.Duration):
                    ShowState(node);
                    break;
                case nameof(TestNode.IsExpanded):
                    ShowExpanded(node);
                    break;
                case nameof(TestNode.HasChildren):
                    chevron.IsVisible = node.HasChildren;
                    break;
                case nameof(TestNode.Depth):
                    Margin = node.Indent;
                    break;
            }
        }

        private void ShowExpanded(TestNode node) => turn.Angle = node.IsExpanded ? 90 : 0;

        private void ShowState(TestNode node)
        {
            string? mark = node.State switch
            {
                TestState.Running => "running",
                TestState.Passed => "passed",
                TestState.Failed => "failed",
                TestState.Skipped => "skipped",
                _ => null,
            };
            foreach (string each in StateClasses)
            {
                state.Classes.Set(each, each == mark);
            }
            duration.Text = node.State is TestState.Passed or TestState.Failed && node.Duration > TimeSpan.Zero ? Format(node.Duration) : string.Empty;
        }
    }
}
