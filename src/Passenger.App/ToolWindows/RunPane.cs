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
    /// <summary>The Console and Telemetry tabs, the latter's key true.</summary>
    private readonly ToolTabs.Strip<bool> tabs = new() { Margin = new Thickness(Gaps.Normal, 0) };
    private RunDashboard? dashboard;

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
        tabs.Choice = shown;
        Console.IsVisible = !shown;
        Telemetry.IsVisible = shown;
    }
}
