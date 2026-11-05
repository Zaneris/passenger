using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.LiveTree;

/// <summary>The Live Visual Tree's split between the tree and the chosen element's details, as the person dragged it for
/// each kind of source, in the store's liveTree.json, every folder alike: the tree's share of the window, or null where the
/// split follows what is shown (<see cref="LiveTreeView.DraggedSplit"/>).</summary>
public sealed record LiveTreeState : IStateDocument<LiveTreeState>
{
    /// <summary>The least share either side of the split is kept at: a drag past it reads as a slip of the pointer.</summary>
    private const double Least = 0.05;

    public double? Avalonia { get; init; }

    public double? Wpf { get; init; }

    public double? Godot { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    /// <summary>The share dragged for a source's kind; null where none was, or for no source.</summary>
    public double? SplitOf(ILiveTreeSource? source) => source switch
    {
        LiveApp { Kind: LiveAppKind.Wpf } => Wpf,
        LiveApp => Avalonia,
        Passenger.App.Godot.GodotGame => Godot,
        _ => null,
    };

    /// <summary>The split dragged for a source's kind kept; as it was for no source.</summary>
    public LiveTreeState WithSplit(ILiveTreeSource? source, double share) => source switch
    {
        LiveApp { Kind: LiveAppKind.Wpf } => this with { Wpf = Share(share) },
        LiveApp => this with { Avalonia = Share(share) },
        Passenger.App.Godot.GodotGame => this with { Godot = Share(share) },
        _ => this,
    };

    public LiveTreeState Clamped() => this with { Avalonia = Share(Avalonia), Wpf = Share(Wpf), Godot = Share(Godot) };

    private static double? Share(double? share) => share is double kept && double.IsFinite(kept) ? Math.Clamp(kept, Least, 1 - Least) : null;
}
