using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Testing;

/// <summary>The map of which tests run which lines as a document of the folder's state.</summary>
public sealed partial record TestReachState : IStateDocument<TestReachState>
{
    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];
}
