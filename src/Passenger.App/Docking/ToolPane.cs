using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Passenger.App.Docking;

/// <summary>One place's window: its header (title, Move to and Hide in a menu, a hide button) over its content.
/// The header drags the window elsewhere.</summary>
internal sealed class ToolPane : DockPanel
{
    private readonly DockHost host;
    private readonly TextBlock title = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ContentControl body = new();
    private readonly ContentControl tools = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer toolStrip;
    private readonly Grid heading;
    private readonly StackPanel actions;
    private readonly Border header;

    public ToolPane(DockHost host)
    {
        this.host = host;
        Button options = HeaderButton("Icon.More", "Options");
        Button hide = HeaderButton("Icon.Minimize", "Hide (⇧⎋)");
        options.Click += (_, _) =>
        {
            MenuFlyout menu = OptionsMenu();
            // Down from the ⋮, inside the pane, as Rider's options drop; Fluent's centres a flyout on its button.
            menu.Placement = PlacementMode.BottomEdgeAlignedRight;
            host.OpenOptions = menu;
            menu.Closed += (_, _) => host.OpenOptions = host.OpenOptions == menu ? null : host.OpenOptions;
            menu.ShowAt(options);
        };
        hide.Click += (_, _) =>
        {
            if (Window is { } window)
            {
                host.Hide(window.Id);
            }
        };
        actions = new StackPanel
        {
            [Grid.ColumnProperty] = 2,
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(0, 0, 6, 0),
            Children = { options, hide },
        };
        toolStrip = new ScrollViewer { Name = "ToolHeaderStrip", Classes = { "toolTabs" }, Content = tools, [Grid.ColumnProperty] = 1 };
        toolStrip.SizeChanged += (_, _) => RevealTool();
        toolStrip.AddHandler(Button.ClickEvent, (_, _) => RevealTool(), RoutingStrategies.Bubble, handledEventsToo: true);
        heading = new Grid
        {
            Name = "ToolHeader",
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("32,0"),
            Children = { title, toolStrip, actions },
        };
        heading.SizeChanged += (_, _) => FitHeader();
        tools.SizeChanged += (_, _) => FitHeader();
        title.SizeChanged += (_, _) => FitHeader();
        actions.SizeChanged += (_, _) => FitHeader();
        header = new Border
        {
            [DockProperty] = Dock.Top,
            Background = Brushes.Transparent,
            Child = heading,
        };
        header.PointerPressed += (_, e) =>
        {
            if (Window is not { } window || (e.Source is Visual source && (source.FindAncestorOfType<Button>(includeSelf: true) is not null
                || source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)))
            {
                return;
            }
            if (e.ClickCount == 2 && Floating is null)
            {
                host.ToggleMaximize(window);
                e.Handled = true;
            }
            else if (Floating is { } own)
            {
                own.BeginMoveDrag(e);
            }
            else
            {
                host.Drag.Begin(window, e, header);
            }
        };
        header.PointerMoved += (_, e) => host.Drag.Continue(e);
        header.PointerReleased += (_, e) => host.Drag.End(e);
        Children.Add(header);
        Children.Add(body);
        this.Bind(BackgroundProperty, this.GetResourceObservable("Passenger.Chrome"));
        IsVisible = false;
    }

    /// <summary>The window of its own it stands in, floating or windowed, if any: its header then moves that window.</summary>
    public Window? Floating { get; init; }

    public ToolWindow? Window { get; private set; }

    public void Show(ToolWindow? window)
    {
        if (Window == window)
        {
            return;
        }
        if (Window is not null)
        {
            Window.PropertyChanged -= OnWindowChanged;
        }
        Window = window;
        body.Content = null;
        tools.Content = null;
        if (window is not null)
        {
            window.PropertyChanged += OnWindowChanged;
            // A content lives in one pane at a time, so it leaves its old one first.
            if (window.Content.Parent is ContentControl old)
            {
                old.Content = null;
            }
            body.Content = window.Content;
            title.Text = window.Title;
            ToolTip.SetTip(title, window.Title);
            if (window.HeaderTools?.Parent is ContentControl holder)
            {
                holder.Content = null;
            }
            tools.Content = window.HeaderTools;
        }
        IsVisible = window is not null;
        FitHeader();
    }

    private void OnWindowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolWindow.Title) && Window is { } window)
        {
            title.Text = window.Title;
            ToolTip.SetTip(title, window.Title);
        }
    }

    private void FitHeader()
    {
        if (heading.Bounds.Width <= 0)
        {
            return;
        }
        title.MaxWidth = Math.Max(0, heading.Bounds.Width - actions.DesiredSize.Width - title.Margin.Left);
        bool hasTools = tools.Content is not null;
        bool stacked = hasTools && title.DesiredSize.Width + tools.DesiredSize.Width + 16 + actions.DesiredSize.Width > heading.Bounds.Width;
        toolStrip.IsVisible = hasTools;
        heading.RowDefinitions[1].Height = stacked ? new GridLength(32) : new GridLength(0);
        Grid.SetRow(toolStrip, stacked ? 1 : 0);
        Grid.SetColumn(toolStrip, stacked ? 0 : 1);
        Grid.SetColumnSpan(toolStrip, stacked ? 3 : 1);
    }

    private void RevealTool() => Dispatcher.UIThread.Post(() => tools.GetVisualDescendants().OfType<ToggleButton>()
        .FirstOrDefault(button => button.IsChecked == true)?.BringIntoView(), DispatcherPriority.Loaded);

    private MenuFlyout OptionsMenu()
    {
        MenuFlyout menu = new();
        MenuItem move = new() { Header = "Move to" };
        foreach (ToolAnchor anchor in Enum.GetValues<ToolAnchor>())
        {
            MenuItem item = new() { Header = ToolWindow.Name(anchor), IsEnabled = Window?.Anchor != anchor };
            item.Click += (_, _) =>
            {
                if (Window is { } window)
                {
                    host.Move(window, anchor);
                }
            };
            move.Items.Add(item);
        }
        MenuItem mode = new() { Header = "View Mode" };
        foreach ((ToolMode each, string name) in new[] { (ToolMode.Docked, "Dock"), (ToolMode.Floating, "Float"), (ToolMode.Windowed, "Window") })
        {
            MenuItem item = new() { Header = name, IsEnabled = Window?.Mode != each };
            item.Click += (_, _) =>
            {
                if (Window is { } window)
                {
                    host.SetMode(window, each);
                }
            };
            mode.Items.Add(item);
        }
        MenuItem maximize = new() { Header = host.IsMaximized ? "Restore Size" : "Maximize", IsEnabled = Floating is null };
        maximize.Click += (_, _) => host.ToggleMaximize(Window);
        MenuItem hide = new() { Header = "Hide" };
        hide.Click += (_, _) =>
        {
            if (Window is { } window)
            {
                host.Hide(window.Id);
            }
        };
        if (Window?.Options?.Invoke() is { Count: > 0 } own)
        {
            foreach (Control item in own)
            {
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(mode);
        menu.Items.Add(move);
        menu.Items.Add(maximize);
        menu.Items.Add(new Separator());
        menu.Items.Add(hide);
        return menu;
    }

    private static Button HeaderButton(string icon, string tip)
    {
        PathIcon glyph = new() { Classes = { "small" } };
        glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable(icon));
        Button button = new() { Classes = { "tool" }, Content = glyph };
        ToolTip.SetTip(button, tip);
        return button;
    }
}
