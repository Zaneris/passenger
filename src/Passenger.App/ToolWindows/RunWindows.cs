namespace Passenger.App.ToolWindows;

/// <summary>What the two windows that list a run's services (Containers, Aspire) share of their layout, decided once: one
/// vocabulary of the chosen row's tabs in one order, Log first and shown at first, each window showing those it has; and the
/// actions on a .NET service of a run in one order, their toolbars' group after their Refresh and their rows' menus' first
/// group.</summary>
public static class RunWindows
{
    /// <summary>The row's log: a container's, a project's or AppHost's merged, a resource's console merged with the rest.</summary>
    public const string LogTab = "Log";

    public const string StatsTab = "Stats";

    public const string FilesTab = "Files";

    public const string DetailsTab = "Details";

    /// <summary>The run's structured logs and traces, the two pages of the Telemetry view under it.</summary>
    public const string TelemetryTab = "Telemetry";

    /// <summary>An AppHost's own console, its program's.</summary>
    public const string AppHostLogTab = "AppHost Log";

    /// <summary>Every tab either window has, in the order each shows those it has.</summary>
    public static IReadOnlyList<string> Tabs { get; } = [LogTab, StatsTab, FilesTab, DetailsTab, TelemetryTab, AppHostLogTab];

    /// <summary>The actions on a .NET service of a run with their icons, in their one order.</summary>
    public static IReadOnlyList<(string Action, string Icon)> ServiceActions { get; } =
    [
        (AppActions.ServiceDebug, IconKeys.Debug),
        (AppActions.ServiceAttach, IconKeys.Attach),
        (AppActions.ServiceRun, IconKeys.RunOutline),
        (AppActions.ServiceProfile, IconKeys.Profile),
    ];
}
