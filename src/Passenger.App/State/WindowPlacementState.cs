using System.Text.Json.Nodes;
using Avalonia.Controls;

namespace Passenger.App.State;

/// <summary>The last main window's normal bounds and its last visible mode, kept for the next launch.</summary>
public sealed record WindowPlacementState : IStateDocument<WindowPlacementState>
{
    public int X { get; init; }

    public int Y { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public WindowState Mode { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public WindowPlacementState Clamped() => this with
    {
        Width = double.IsFinite(Width) ? Math.Max(0, Width) : 0,
        Height = double.IsFinite(Height) ? Math.Max(0, Height) : 0,
        Mode = Mode is WindowState.Maximized or WindowState.FullScreen ? Mode : WindowState.Normal,
    };
}
