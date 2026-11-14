// Window tests run one at a time on Avalonia's one session thread whatever xunit does; as the assembly's one collection
// they also run one class after another, so no class's restores and projects are made beside another class's test.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace Passenger.Tests;

/// <summary>What runs beside nothing: a class with a test that holds what it tests to a time (a key's, a frame's, a diff's,
/// how soon the window answered, a profile's samples, how long the debugger gives an evaluation), that debugs a program
/// through the window, its debuggee waiting on timings a busy machine stretches, or that counts what every thread
/// allocates, where a class beside it would count as its own. These run after the rest, one class at a time. Beside the
/// window tests run only the debug adapter's protocol tests (DebugSession.Collection) and <see cref="Beside"/>'s
/// classes.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Alone
{
    public const string Name = "Alone";
}

/// <summary>What runs beside the window tests, one class after another: a class that opens no window and waits on
/// nothing of the headless session's, changes nothing the process shares (its PATH, its environment, a setting), and
/// holds nothing to a time, a call's included, so a machine busy with the window tests only lengthens it; its time then
/// hides beside theirs.</summary>
[CollectionDefinition(Name)]
public sealed class Beside
{
    public const string Name = "Beside";
}
