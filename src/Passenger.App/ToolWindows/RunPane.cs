using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Passenger.App.Run;
using Passenger.App.Telemetry;

namespace Passenger.App.ToolWindows;

/// <summary>The Run window: its console, and while its run collects logs and traces (<see cref="Dashboard"/>), the run's
/// Telemetry beside it, Console and Telemetry tabs over the two; with no records to show, the console alone. While a run of
/// several parts is the last started, a tab of each part's own console in their place (<see cref="ShowParts"/>), each saying
/// where its part stands.</summary>
public sealed class RunPane : DockPanel
{
    private readonly Border tabBar;
    /// <summary>The Console and Telemetry tabs, the latter's key true.</summary>
    private readonly ToolTabs.Strip<bool> tabs = new() { Margin = new Thickness(Gaps.Normal, 0) };
    private RunDashboard? dashboard;

    /// <summary>The tabs of a run of several parts, a part's console each.</summary>
    private readonly ToolTabs.Strip<PartRun> partTabs = new() { Margin = new Thickness(Gaps.Normal, 0) };
    private readonly Border partBar;
    private readonly Panel consoles;
    private readonly List<PartRun> parts = [];

    public RunPane(OutputView console)
    {
        Console = console;
        tabs.Add(false, "Console", name: "runConsoleTab");
        tabs.Add(true, RunWindows.TelemetryTab, name: "runTelemetryTab");
        tabs.Choice = false;
        tabs.Chosen += (_, telemetry) => Show(telemetry);
        tabBar = ToolBars.Top(tabs);
        tabBar[DockProperty] = Dock.Top;
        tabBar.IsVisible = false;
        Telemetry.IsVisible = false;
        Children.Add(tabBar);
        partTabs.Chosen += (_, part) => ShowPart(part);
        partBar = ToolBars.Top(new ScrollViewer { Content = partTabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        partBar[DockProperty] = Dock.Top;
        partBar.IsVisible = false;
        Children.Add(partBar);
        consoles = new Panel { Children = { console, Telemetry } };
        Children.Add(consoles);
    }

    /// <summary>The parts whose consoles it shows, in their order; none while the console shows.</summary>
    public IReadOnlyList<PartRun> Parts => parts;

    /// <summary>The part whose console is shown, or null while the console is.</summary>
    public PartRun? ShownPart => parts.Count > 0 && partTabs.HasChoice ? partTabs.Choice : null;

    /// <summary>Shows a run of several parts' consoles, a tab of each in their order, the first chosen; none puts the console
    /// (and its Telemetry) back.</summary>
    public void ShowParts(IReadOnlyList<PartRun> shown)
    {
        foreach (PartRun part in parts)
        {
            part.StateChanged -= OnStateChanged;
            // A Compose part's console is the window's own, which stays.
            if (part.Console != Console)
            {
                consoles.Children.Remove(part.Console);
            }
        }
        parts.Clear();
        partTabs.Clear();
        foreach (PartRun part in shown)
        {
            parts.Add(part);
            TextBlock state = new() { Text = part.Said, Classes = { StyleClasses.Muted } };
            partTabs.Add(part, new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gaps.Small, Children = { new TextBlock { Text = part.Name }, state } },
                name: $"runPart{part.Place}Tab");
            part.StateChanged += OnStateChanged;
            if (part.Console != Console)
            {
                part.Console.IsVisible = false;
                consoles.Children.Add(part.Console);
            }
        }
        partBar.IsVisible = parts.Count > 0;
        if (parts.Count > 0)
        {
            tabBar.IsVisible = false;
            ShowPart(parts[0]);
        }
        else
        {
            Console.IsVisible = true;
            Follow();
        }
    }

    /// <summary>Shows a part's console, its tab chosen.</summary>
    public void ShowPart(PartRun part)
    {
        if (!parts.Contains(part))
        {
            return;
        }
        partTabs.Choice = part;
        Telemetry.IsVisible = false;
        foreach (PartRun each in parts.Where(p => p.Console != Console))
        {
            each.Console.IsVisible = each == part;
        }
        Console.IsVisible = part.Console == Console;
    }

    /// <summary>A part's tab says where it stands now.</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (sender is PartRun part && parts.Contains(part) && partTabs[part].Content is StackPanel { Children: [_, TextBlock state] })
        {
            state.Text = part.Said;
        }
    }

    public OutputView Console { get; }

    /// <summary>The run's Telemetry, streaming while its tab is shown.</summary>
    public TelemetryPane Telemetry { get; } = new();

    /// <summary>Whether the Telemetry tab is shown, rather than the console.</summary>
    public bool ShowsTelemetry => Telemetry.IsVisible;

    /// <summary>The dashboard of the run the console shows, whose records the Telemetry tab reads; null while it has none,
    /// the console alone shown.</summary>
    public RunDashboard? Dashboard
    {
        get => dashboard;
        set
        {
            dashboard = value;
            Follow();
        }
    }

    /// <summary>The dashboard came up, was kept or went: the tabs follow.</summary>
    public void Follow()
    {
        TelemetrySource? source = dashboard?.Telemetry;
        Telemetry.Source = source;
        tabBar.IsVisible = source is not null && parts.Count == 0;
        if (source is null)
        {
            Show(telemetry: false);
        }
    }

    /// <summary>Shows the Telemetry tab, or the console.</summary>
    public void Show(bool telemetry)
    {
        if (parts.Count > 0)
        {
            return;
        }
        bool shown = telemetry && Telemetry.Source is not null;
        tabs.Choice = shown;
        Console.IsVisible = !shown;
        Telemetry.IsVisible = shown;
    }
}
