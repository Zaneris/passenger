using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Passenger.App.Docking;

/// <summary>One place's window: its header (title, Move to and Hide in a menu, a hide button) over its content.
/// The header drags the window elsewhere; in a narrow one, what follows the title gives way, then the title, each ending in
/// an ellipsis, never under the buttons.</summary>
internal sealed class ToolPane : DockPanel
{
    private readonly DockHost host;
    private readonly TextBlock title = new() { Classes = { "toolTitle" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl body = new();
    // What the window puts after its title (its tabs) narrows before the title does and never draws past its room, over
    // the header's buttons.
    private readonly ContentControl tools = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ClipToBounds = true };
    private readonly Border header;

    public ToolPane(DockHost host)
    {
        this.host = host;
        Button options = HeaderButton(IconKeys.More, "Options");
        Button hide = HeaderButton(IconKeys.Minimize, "Hide (⇧⎋)");
        options.Click += (_, _) =>
        {
            MenuFlyout menu = OptionsMenu();
            // Down from the ⋮ and inside the pane, where Fluent's default centres it on the button.
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
        header = new Border
        {
            Classes = { "toolHeader" },
            [DockProperty] = Dock.Top,
            Child = new DockPanel
            {
                Children =
                {
                    new StackPanel { [DockProperty] = Dock.Right, Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 6, 0), Children = { options, hide } },
                    new ShrinkingRow { Children = { title, tools } },
                },
            },
        };
        header.PointerPressed += (_, e) =>
        {
            if (Window is not { } window || (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null))
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
        Classes.Add("toolPane");
        IsVisible = false;
    }

    /// <summary>Styled as a DockPanel of its own class (Shell.axaml's toolPane), its look the theme's.</summary>
    protected override Type StyleKeyOverride => typeof(DockPanel);

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
            if (window.HeaderTools?.Parent is ContentControl holder)
            {
                holder.Content = null;
            }
            tools.Content = window.HeaderTools;
        }
        IsVisible = window is not null;
    }

    private void OnWindowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolWindow.Title) && Window is { } window)
        {
            title.Text = window.Title;
        }
    }

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
        Button button = ToolButtons.Of(icon, tip);
        ((PathIcon)button.Content!).Classes.Add("small");
        return button;
    }
}
