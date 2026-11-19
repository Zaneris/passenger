using Avalonia;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>How a row of parts (<see cref="ShrinkingRow"/>, <see cref="ToolWindows.ToolFlow"/>) measures a part again once
/// it has given it its room, every such row's one way: a part given all it asked is measured with no bound, so growing
/// (words coming to it, a count gaining a digit) it asks more and the row is laid out again, where held to what it asked
/// before its growth would go unheard and it would be drawn past its room; a part given less is measured in that room.</summary>
public static class RoomGiven
{
    /// <summary>Measures a part in <paramref name="room"/>, its row having measured it with no bound before giving it that.</summary>
    public static void Measure(Control part, double room, double height) =>
        part.Measure(new Size(room >= part.DesiredSize.Width ? double.PositiveInfinity : room, height));
}
