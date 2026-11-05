using System.Diagnostics;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The solution's helpers (Roslyn's server, the project host) run with the runtime compacting their fragmented
/// heaps, which held a fifth less in Passenger's own solution (#1153), and a setting of the person's own for it stands.</summary>
[Collection(Beside.Name)]
public sealed class HelperMemoryTests
{
    [Fact]
    public void A_helper_runs_conserving_memory_unless_the_person_set_it_themselves()
    {
        ProcessStartInfo start = ChildProcesses.ConservingMemory(ChildProcesses.HelperCommand("Passenger.Host.dll", []));
        string? persons = Environment.GetEnvironmentVariable(RuntimeVariables.ConserveMemory);
        Assert.True(start.Environment.TryGetValue(RuntimeVariables.ConserveMemory, out string? given) && given == (persons ?? ChildProcesses.HelperConservesMemory),
            $"A helper starts with {RuntimeVariables.ConserveMemory} {given ?? "unset"}, where it was to be {persons ?? ChildProcesses.HelperConservesMemory}: "
            + "its heap is left fragmented, or the person's own setting overridden");
    }
}
