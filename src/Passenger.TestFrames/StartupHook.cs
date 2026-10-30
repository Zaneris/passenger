using System.Reflection;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.TestFrames;

/// <summary>Run before the test program's Main, as DOTNET_STARTUP_HOOKS names this assembly, only in a test run
/// Passenger started. Nothing here touches Avalonia, which the tests have not set up yet, and nothing it does can stop
/// the tests.</summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable(Variables.Frames) != "1")
        {
            Unlist();
            return;
        }
        // The tests run in the project's own program, which a test host starts: that program takes the hook from here.
        if (Assembly.GetEntryAssembly()?.GetName().Name is { } entry && entry.StartsWith("testhost", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        Unlist();
        try
        {
            if (!AppAvaloniaContext.ProgramAvalonia().Served)
            {
                return;
            }
            string self = typeof(StartupHook).Assembly.Location;
            Assembly inside = new AppAvaloniaContext("Passenger test frames", self).LoadFromAssemblyPath(self);
            inside.GetType("Passenger.TestFrames.Frames", throwOnError: true)!
                .GetMethod("Watch", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, null);
        }
        catch (Exception error) when (error is IOException or BadImageFormatException or TypeLoadException or MissingMethodException or TargetInvocationException)
        {
            // A program the hook cannot serve runs its tests as it would without it.
        }
    }

    /// <summary>This hook and its variable, out of the environment the tests' own programs inherit; other hooks stay.</summary>
    private static void Unlist()
    {
        OwnEnvironment.Set(Variables.Frames, null);
        StartupHooks.TakeOut(typeof(StartupHook).Assembly.Location);
    }
}
