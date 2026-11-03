using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>How a test run starts Godot: the arguments Settings gives each start of it, ahead of the project's, and whether
/// the tests GUT and GoDotTest run in a game show its window (the person asked, and someone sees Passenger), else it runs
/// headless.</summary>
public sealed record GodotStart(IReadOnlyList<string> Arguments, bool Windowed)
{
    /// <summary>A start with no arguments of the setting's, headless.</summary>
    public static readonly GodotStart Headless = new([], false);

    /// <summary>The arguments ahead of the project's for GUT's and GoDotTest's Godot: the setting's, then --headless unless
    /// it shows a window or they run it headless already.</summary>
    public IReadOnlyList<string> Leading => Windowed || GodotExecutables.RunsHeadless(Arguments) ? Arguments : [.. Arguments, GodotExecutables.Headless];
}
