namespace Passenger.App.Godot;

/// <summary>When Passenger connects again to the Godot editor a project's IDE messaging meta file names
/// (<see cref="GodotIdeLink"/>): one connected is kept while open, one that did not answer is passed over a while, and a
/// changed meta file is connected to at once. Pure.</summary>
public static class GodotIdeRetry
{
    /// <summary>How long a meta file whose editor did not answer is passed over before it is tried again: an editor busy
    /// importing as it opens shakes no hands until it is done.</summary>
    public static readonly TimeSpan Again = TimeSpan.FromSeconds(10);

    /// <summary>Whether the editor as last found stands for the meta file as read now, so it is not connected to again: the
    /// same file's, connected still; or one that did not answer when tried <paramref name="since"/> ago, within <see cref="Again"/>.</summary>
    public static bool Stands(string? found, bool answered, bool open, TimeSpan since, string? meta) => found == meta && (answered ? open : since < Again);
}
