using Xunit.v3;

namespace Passenger.Tests;

/// <summary>A test in the release gate, on a test method or a whole class: CI's gate lane runs it on every system and its
/// failure holds a release back, where the report lane's failures are only reported. The lanes select it by its trait,
/// Gate=true (tools/test-shards.py, just test --gate).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class GateAttribute : Attribute, ITraitAttribute
{
    public const string Name = "Gate";

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new(Name, "true")];
}
