using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App.Debugging;

/// <summary>A stop's values as rows of a <see cref="VariablesTree"/>: the one list the Debug window's Variables view and the
/// debugger's hover card both are. A row's chevron, Right or Enter opens it (its members asked of the debugger the first
/// time) and Left closes it; a double-click opens what has members and sets what has none (an EF Core query's SQL, read,
/// opens in a tab); F2 types a new value in its row; Delete takes a watch away; and its menu is the Variables and
/// Watches menu. What a row asks for it raises, for the debugger to do.</summary>
public sealed class VariablesList : ListBox
{
    // Styled as the ListBox it is, so the theme's ListBox.tree reaches it.
    protected override Type StyleKeyOverride => typeof(ListBox);

    public VariablesList(VariablesTree tree)
    {
        Tree = tree;
        Classes.Add("tree");
        ItemsSource = tree.Rows;
        ItemTemplate = new FuncDataTemplate<VariableNode>((node, _) => Row(node), supportsRecycling: false);
        DoubleTapped += (_, _) =>
        {
            // A double-click opens what has members, and sets what has none; a query's SQL opens its text.
            if (SelectedItem is VariableNode node && node.HasChildren)
            {
                IdeErrors.Observe(Toggle(node));
            }
            else if (SelectedItem is VariableNode { IsQueryText: true } text)
            {
                TextOpened?.Invoke(this, text);
            }
            else
            {
                EditSelectedValue();
            }
        };
        TreeKeys.Attach<VariableNode>(this, node => IdeErrors.Observe(Toggle(node)), node =>
        {
            if (!node.IsQueryText || Tree.Rows.Any(r => r.IsEditing))
            {
                return false;
            }
            TextOpened?.Invoke(this, node);
            return true;
        });
        // Ahead of the list's own keys, as the tree's are.
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // The menu is made again as the choice moves: each one made says as it closes.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ContextMenuProperty && e.NewValue is ContextMenu menu)
            {
                menu.Closed += (_, _) => MenuClosed?.Invoke(this, EventArgs.Empty);
            }
        };
        ListMenus.Attach(this, Menu);
    }

    /// <summary>A row's menu closed, chosen from or not.</summary>
    public event EventHandler? MenuClosed;

    /// <summary>A query's SQL to open whole in a read-only editor tab (a double-click, Enter or its menu's Open in Editor).</summary>
    public event EventHandler<VariableNode>? TextOpened;

    /// <summary>The menu's item that opens a query's SQL in a tab.</summary>
    public const string OpenInEditor = "Open in Editor";

    /// <summary>Opens or closes a row as a person does: a lazy row read keeps the choice, and the keys, on the row it read as.</summary>
    private async Task Toggle(VariableNode node)
    {
        bool chosen = SelectedItem == node;
        bool focused = IsKeyboardFocusWithin;
        await Tree.Toggle(node);
        if (node.ReadAs is { } read && chosen && Tree.Rows.Contains(read))
        {
            SelectedItem = read;
            if (focused)
            {
                Dispatcher.UIThread.Post(FocusRow, DispatcherPriority.Loaded);
            }
        }
    }

    public VariablesTree Tree { get; }

    /// <summary>An expression to evaluate once (false: Inspect), or to keep as a watch (true: Add to Watches).</summary>
    public event EventHandler<(string Expression, bool Watch)>? EvaluateRequested;

    /// <summary>A watch to stop watching.</summary>
    public event EventHandler<string>? WatchRemoved;

    /// <summary>Set Value: a row's new value typed in its place (a C# expression), Enter pressed.</summary>
    public event EventHandler<(VariableNode Node, string Value)>? SetValueRequested;

    /// <summary>Set Data Breakpoint, or Remove Data Breakpoint, chosen on a row.</summary>
    public event EventHandler<VariableNode>? DataBreakpointRequested;

    /// <summary>What Set Data Breakpoint offers on a row, the debugger asked: the menu's item says so once it answers.</summary>
    public Func<VariableNode, Task<DataWatchOffer>>? DataWatch { get; set; }

    /// <summary>Whether a row's value is being typed over (Set Value), or its menu is open: the list is in use.</summary>
    public bool IsInUse => Tree.Rows.Any(r => r.IsEditing) || ContextMenu?.IsOpen == true;

    /// <summary>Starts typing over the selected row's value, as F2 does (Set Value).</summary>
    public bool EditSelectedValue()
    {
        if (SelectedItem is not VariableNode { Expression: not null } node)
        {
            return false;
        }
        node.IsEditing = true;
        return true;
    }

    /// <summary>Gives the keys to the chosen row, the first when none is chosen, as a person's click on it would.</summary>
    public void FocusRow()
    {
        if (SelectedIndex < 0 && Tree.Rows.Count > 0)
        {
            SelectedIndex = 0;
        }
        if (SelectedIndex >= 0 && ContainerFromIndex(SelectedIndex) is Control row)
        {
            row.Focus(NavigationMethod.Directional);
            return;
        }
        Focus(NavigationMethod.Directional);
    }

    /// <summary>Shows rows read again in place of these (a value set changes what they say), those that were open opened
    /// again, and the row chosen chosen again, with the keys if the list had them.</summary>
    public async Task ShowAgain(IEnumerable<VariableNode> top)
    {
        string? chosen = SelectedItem is VariableNode node ? VariablesTree.PathOf(node) : null;
        bool focused = IsKeyboardFocusWithin;
        await Tree.ShowAgain(top);
        if (chosen is not null && Tree.Rows.FirstOrDefault(r => VariablesTree.PathOf(r) == chosen) is { } again)
        {
            SelectedItem = again;
        }
        if (focused)
        {
            Dispatcher.UIThread.Post(FocusRow, DispatcherPriority.Loaded);
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        // A value being typed in its row takes its own keys.
        if (SelectedItem is not VariableNode node || e.Source is TextBox || Tree.Rows.Any(r => r.IsEditing))
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Delete or Key.Back when node.Role == VariableRole.Watch:
                WatchRemoved?.Invoke(this, node.Name);
                e.Handled = true;
                break;
            case Key.F2 when node.Expression is not null:
                node.IsEditing = true;
                e.Handled = true;
                break;
        }
    }

    /// <summary>The Variables and Watches menu: Set Value, Copy Value and Copy Name, Add to Watches, Inspect (the value
    /// evaluated on its own, to open), Remove Watch for a watch, and last Set Data Breakpoint (Remove Data Breakpoint on a
    /// member watched), greyed with why where it cannot be set. A query's SQL opens first.</summary>
    private IReadOnlyList<Control> Menu()
    {
        if (SelectedItem is not VariableNode node)
        {
            return [];
        }
        List<Control> items = node.IsQueryText ? [ListMenus.Item(OpenInEditor, "Enter", () => TextOpened?.Invoke(this, node)), new Separator()] : [];
        items.AddRange(
        [
            ListMenus.Item("Set Value…", "F2", () => EditSelectedValue(), node.Expression is not null),
            ListMenus.Item("Copy Value", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, node.Value))),
            ListMenus.Item("Copy Name", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, node.Name))),
            new Separator(),
            ListMenus.Item("Add to Watches", null, () => EvaluateRequested?.Invoke(this, (node.Expression!, true)), node.Expression is not null && node.Role != VariableRole.Watch),
            ListMenus.Item("Inspect", null, () => EvaluateRequested?.Invoke(this, (node.Expression!, false)), node.Expression is not null),
        ]);
        if (node.Role == VariableRole.Watch)
        {
            items.Add(new Separator());
            items.Add(ListMenus.Item("Remove Watch", "Delete", () => WatchRemoved?.Invoke(this, node.Name)));
        }
        if (DataWatch is { } offer)
        {
            MenuItem watch = ListMenus.Item(DataWatchOffer.Set, null, () => DataBreakpointRequested?.Invoke(this, node), enabled: false);
            ToolTip.SetShowOnDisabled(watch, true);
            items.Add(new Separator());
            items.Add(watch);
            IdeErrors.Observe(Offer(watch, offer(node)));
            // Asked again each time the menu opens: the row may have been watched, or let go, since it was chosen.
            watch.AttachedToVisualTree += (_, _) => IdeErrors.Observe(Offer(watch, offer(node)));
        }
        return items;
    }

    /// <summary>Shows on the menu's item what the debugger said can be done with the row: held until then, so a picture
    /// taken as the menu opens shows its answer.</summary>
    private static async Task Offer(MenuItem item, Task<DataWatchOffer> asked)
    {
        using IDisposable held = BackgroundWork.Scope();
        DataWatchOffer offered = await asked;
        item.Header = offered.Title;
        item.IsEnabled = offered.WhyNot is null;
        ToolTip.SetTip(item, offered.WhyNot);
    }

    private Control Row(VariableNode? node)
    {
        if (node is null)
        {
            return new TextBlock();
        }
        PathIcon chevron = new() { Classes = { "chevron" }, IsVisible = node.HasChildren, RenderTransformOrigin = RelativePoint.Center };
        chevron.Bind(PathIcon.DataProperty, chevron.GetResourceObservable(IconKeys.ChevronRight));
        chevron.RenderTransform = new RotateTransform(node.ChevronAngle);
        node.FollowWhileShown(chevron, (_, e) =>
        {
            if (e.PropertyName is null or nameof(VariableNode.ChevronAngle))
            {
                chevron.RenderTransform = new RotateTransform(node.ChevronAngle);
            }
        });
        Border toggle = new() { Width = 18, Height = ListRow.Height, Background = Brushes.Transparent, Child = chevron };
        toggle.PointerPressed += (_, e) =>
        {
            if (node.HasChildren)
            {
                IdeErrors.Observe(Toggle(node));
                e.Handled = true;
            }
        };
        PathIcon icon = new() { Margin = new Thickness(0, 0, 6, 0) };
        // Marks for what the debugger adds to a frame's values: a call's value, the exception stopped at, a query's SQL.
        string iconKey = node switch
        {
            { Role: VariableRole.Watch } => IconKeys.Evaluate,
            { IsReturnValue: true } => IconKeys.StepOut,
            { IsException: true } => IconKeys.Error,
            { IsQuerySql: true } => IconKeys.Database,
            _ => IconKeys.Variable,
        };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(iconKey));
        TextBlock type = new() { Text = node.TypeLabel, VerticalAlignment = VerticalAlignment.Center };
        type.Classes.Add("muted");
        TextBlock value = new() { Text = node.Shown, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 900 };
        if (node.Role == VariableRole.Error)
        {
            value.Bind(TextBlock.ForegroundProperty, value.GetResourceObservable(TokenKeys.Error));
        }
        ToolTip.SetTip(value, node.Lines);
        TextBox editor = new() { Classes = { "search" }, MinHeight = 22, Height = 22, MinWidth = 240, Padding = new Thickness(6, 0), IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        editor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                SetValueRequested?.Invoke(this, (node, editor.Text ?? string.Empty));
                // The keys go back to the row, as the field they were in goes.
                FocusRow();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                node.IsEditing = false;
                FocusRow();
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        editor.LostFocus += (_, _) => node.IsEditing = false;
        node.FollowWhileShown(editor, (_, e) =>
        {
            if (e.PropertyName != nameof(VariableNode.IsEditing))
            {
                return;
            }
            editor.IsVisible = node.IsEditing;
            value.IsVisible = type.IsVisible = !node.IsEditing;
            if (node.IsEditing)
            {
                // It starts from the value as shown, all of it chosen, so typing replaces it and an arrow edits it.
                editor.Text = node.Value;
                Dispatcher.UIThread.Post(() =>
                {
                    editor.Focus();
                    editor.SelectAll();
                }, DispatcherPriority.Loaded);
            }
        });
        // Narrower than its row, the value gives way first, ending in an ellipsis, then its type.
        return new ShrinkingRow
        {
            Height = ListRow.Height,
            Margin = node.Indent,
            Children =
            {
                toggle,
                icon,
                new TextBlock { Text = node.Name, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = " = ", VerticalAlignment = VerticalAlignment.Center },
                type,
                value,
                editor,
            },
        };
    }
}
