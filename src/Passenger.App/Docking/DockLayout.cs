using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Docking;

/// <summary>Where each tool window was and whether it was open, and the areas' sizes, as the window last stood: kept
/// in the store with the folder open, and as the one a folder without its own opens on. Only the window a person uses
/// keeps it; a headless IDE (a screenshot, an agent's, a test's) starts on the defaults and keeps nothing.</summary>
public sealed record DockLayout : IStateDocument<DockLayout>
{
    /// <summary>The widest an area is kept, past any screen: a size beyond it is the default's.</summary>
    private const double LargestArea = 10000;

    public Dictionary<string, DockedWindow> Windows { get; init; } = [];
    public double LeftWidth { get; init; }
    public double RightWidth { get; init; }
    public double BottomHeight { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned, DatabaseOnTheRight];

    private static void DatabaseOnTheRight(JsonObject document)
    {
        if (document["windows"] is JsonObject windows && windows[ToolWindowIds.Database] is JsonObject database
            && database["anchor"] is JsonValue anchor && anchor.TryGetValue(out string? placed)
            && string.Equals(placed, nameof(ToolAnchor.BottomLeft), StringComparison.OrdinalIgnoreCase))
        {
            database["anchor"] = nameof(ToolAnchor.RightBottom);
        }
    }

    /// <summary>A window placed where this build has no place (a later build's) stays where it was added, one in a mode it
    /// does not have is docked, and a size or a window's bounds out of reach are the defaults'.</summary>
    public DockLayout Clamped() => this with
    {
        Windows = (Windows ?? []).Where(w => w.Value is { } placed && Enum.IsDefined(placed.Anchor))
            .ToDictionary(w => w.Key, w => w.Value with
            {
                Order = Math.Max(0, w.Value.Order),
                Mode = Enum.IsDefined(w.Value.Mode) ? w.Value.Mode : ToolMode.Docked,
                Bounds = w.Value.Bounds is [_, _, > 0, > 0] ? w.Value.Bounds : null,
            }),
        LeftWidth = Size(LeftWidth),
        RightWidth = Size(RightWidth),
        BottomHeight = Size(BottomHeight),
    };

    private static double Size(double value) => value is >= 0 and <= LargestArea ? value : 0;
}

/// <summary>One tool window's place: its anchor, its order among that anchor's buttons, and whether it was open.</summary>
public sealed record DockedWindow(ToolAnchor Anchor, int Order, bool Shown)
{
    public ToolMode Mode { get; init; }

    /// <summary>A floating or windowed one's window, in screen pixels.</summary>
    public int[]? Bounds { get; init; }
}
