using System.Text.Json.Nodes;
using Avalonia.Controls;

namespace Passenger.App.State;

/// <summary>Where the person's window last stood, in the store's windowPlacement.json, which the next start's window
/// opens on: its bounds while neither maximized, full screen nor minimized (its frame's top-left in the screen's pixels,
/// its client size in its own units, as the window keeps each), and whether it was maximized or full screen. A width or
/// height of none keeps no place.</summary>
public sealed record WindowPlacementState : IStateDocument<WindowPlacementState>
{
    public int X { get; init; }

    public int Y { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    /// <summary>Normal, maximized or full screen; never minimized, which the next start opens as normal.</summary>
    public WindowState State { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    /// <summary>Whether it keeps a place to open at.</summary>
    public bool KeepsPlace() => Width > 0 && Height > 0;

    /// <summary>A size not a size keeps no place, and a state but maximized or full screen is normal.</summary>
    public WindowPlacementState Clamped() => this with
    {
        Width = double.IsFinite(Width) && Width > 0 ? Width : 0,
        Height = double.IsFinite(Height) && Height > 0 ? Height : 0,
        State = State is WindowState.Maximized or WindowState.FullScreen ? State : WindowState.Normal,
    };
}
