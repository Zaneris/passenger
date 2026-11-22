using Passenger.Core;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests;

/// <summary>A helper Passenger runs on its own runtime through its helper host (Roslyn's server, the project host, the
/// debug adapter) is given its runtimeconfig.json's properties as the program's own host would give them.</summary>
[Collection(Beside.Name)]
public sealed class HelperHostTests
{
    internal async Task A_helpers_properties_of_every_kind_reach_it_as_its_own_host_gives_them()
    {
        TimeSpan limit = TimeSpan.FromSeconds(60).Scaled();
        ChildResult own = await ChildProcesses.RunAsync(ChildProcesses.Command(Debuggees.Apphost("PassengerCases"), ["appcontext"]), limit: limit, cancel: TestContext.Current.CancellationToken);
        ChildResult helped = await ChildProcesses.RunAsync(ChildProcesses.HelperCommand(Debuggees.Program("PassengerCases"), ["appcontext"]), limit: limit, cancel: TestContext.Current.CancellationToken);

        Assert.True(own.Succeeded && own.Output.Contains("Passenger.Cases.Object={", StringComparison.Ordinal), $"The program's own host ran it to exit code {own.ExitCode}: {own.Output}{own.Error}");
        Assert.True(helped.Succeeded && helped.Output == own.Output,
            $"Run by the helper host, the program was given\n{helped.Output}{helped.Error}\nwhere its own host gave it\n{own.Output}: a helper reads its configuration otherwise than it was built to");
    }
}
