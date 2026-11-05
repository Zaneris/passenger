using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Passenger.App.Telemetry;

namespace Passenger.App.ToolWindows;

/// <summary>The Run window: its console, and while its run collects logs and traces (<see cref="Dashboard"/>), the run's
/// Telemetry beside it, Console and Telemetry tabs over the two; with no records to show, the console alone.</summary>
public sealed class RunPane : DockPanel
{
    private readonly Border tabBar;
    private readonly ToggleButton consoleTab;
    private readonly ToggleButton telemetryTab;
    private RunDashboard? dashboard;

    public RunPane(OutputView console)
    {
        Console = console;
        consoleTab = ToolTabs.Tab("Console", true);
        telemetryTab = ToolTabs.Tab(RunWindows.TelemetryTab, false);
        consoleTab.Name = "runConsoleTab";
        telemetryTab.Name = "runTelemetryTab";
        consoleTab.Click += (_, _) => Show(telemetry: false);
        telemetryTab.Click += (_, _) => Show(telemetry: true);
        StackPanel strip = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { consoleTab, telemetryTab },
        };
        tabBar = ToolBars.Top(strip);
        tabBar[DockProperty] = Dock.Top;
        tabBar.IsVisible = false;
        Telemetry.IsVisible = false;
        Children.Add(tabBar);
        Children.Add(new Panel { Children = { console, Telemetry } });
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
        tabBar.IsVisible = source is not null;
        if (source is null)
        {
            Show(telemetry: false);
        }
    }

    /// <summary>Shows the Telemetry tab, or the console.</summary>
    public void Show(bool telemetry)
    {
        bool shown = telemetry && Telemetry.Source is not null;
        consoleTab.IsChecked = !shown;
        telemetryTab.IsChecked = shown;
        Console.IsVisible = !shown;
        Telemetry.IsVisible = shown;
    }
}
