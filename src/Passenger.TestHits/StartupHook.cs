using Passenger.Core;
using Passenger.TestHits;

/// <summary>Run before the test program's Main, as DOTNET_STARTUP_HOOKS names this assembly, only in a run Passenger
/// started to map which tests run which lines. Nothing it does can stop the tests: a hook that cannot reach Passenger
/// leaves them to run as they would.</summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        string? port = Environment.GetEnvironmentVariable(Variables.Port);
        // Taken out of what the program starts in turn, which runs no test Passenger asks about.
        OwnEnvironment.Set(Variables.Port, null);
        StartupHooks.TakeOut(typeof(StartupHook).Assembly.Location);
        if (int.TryParse(port, out int number))
        {
            Hits.Serve(number);
        }
    }
}
