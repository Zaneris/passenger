using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Passenger.App.ToolWindows;

/// <summary>The Run and Debug windows' toggle for Avalonia's renderer debug overlays in the app they run: a toolbar button,
/// there only while an app with Passenger's inspector in it runs, whose menu checks each overlay the app's windows show
/// (FPS, Dirty Rects, Layout Time Graph, Render Time Graph); choosing one turns it on or off in the app.</summary>
public sealed class OverlaysButton : Button
{
    /// <summary>Avalonia's overlays, by the names of its RendererDebugOverlays flags, with the menu's words for each.</summary>
    public static readonly IReadOnlyList<(string Name, string Title)> All =
        [("Fps", "FPS"), ("DirtyRects", "Dirty Rects"), ("LayoutTimeGraph", "Layout Time Graph"), ("RenderTimeGraph", "Render Time Graph")];

    public OverlaysButton()
    {
        Classes.Add("tool");
        Name = "AvaloniaOverlays";
        PathIcon icon = new();
        icon.Bind(PathIcon.DataProperty, this.GetResourceObservable(IconKeys.Overlays));
        Content = icon;
        ToolTip.SetTip(this, "Avalonia Debug Overlays");
        IsVisible = false;
    }

    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>The overlays the app's windows show now, by Avalonia's names.</summary>
    public Func<IReadOnlyList<string>> Shown { get; set; } = () => [];

    /// <summary>An overlay chosen in the menu, by Avalonia's name: to turn on if it is off, off if on.</summary>
    public event EventHandler<string>? Toggled;

    /// <summary>The menu while it is open.</summary>
    public ContextMenu? Menu { get; private set; }

    /// <summary>Where the menu opens: under the button on a toolbar across the top, beside it on one down the left.</summary>
    public PlacementMode MenuPlacement { get; init; } = PlacementMode.BottomEdgeAlignedLeft;

    protected override void OnClick()
    {
        base.OnClick();
        IReadOnlyList<string> shown = Shown();
        ContextMenu menu = new() { PlacementTarget = this, Placement = MenuPlacement };
        foreach ((string name, string title) in All)
        {
            MenuItem item = new() { Header = title, ToggleType = MenuItemToggleType.CheckBox, IsChecked = shown.Contains(name) };
            item.Click += (_, _) => Toggled?.Invoke(this, name);
            menu.Items.Add(item);
        }
        Menu = menu;
        menu.Closed += (_, _) => Menu = null;
        menu.Open(this);
    }

    /// <summary>Chooses an overlay in the menu, as a click on its item does.</summary>
    public void Choose(string name)
    {
        if (Menu?.Items.OfType<MenuItem>().ElementAtOrDefault(All.ToList().FindIndex(o => o.Name == name)) is { } item)
        {
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Menu.Close();
        }
    }
}
