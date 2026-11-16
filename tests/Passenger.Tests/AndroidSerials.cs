using Passenger.App;
using Passenger.App.Run.Devices;

namespace Passenger.Tests;

/// <summary>The serial of the emulator or device an Android window test runs on, which each of its adb calls names, so
/// with several connected each reaches the one the window started the app on.</summary>
internal static class AndroidSerials
{
    /// <summary>The target's own serial where it ran when listed, else the one the window's Run or Debug booted it as.</summary>
    public static string Of(MainWindow window, DeviceTarget target) =>
        target.Running ? target.Id
        : ((window.Session.RunPlan?.Device ?? window.Debugger.Tabs.Select(t => t.Plan?.Device).LastOrDefault(d => d?.App is not null))?.App as AndroidApp)?.Serial
            ?? throw new InvalidOperationException($"{target.Name} was not started by the window, so the emulator it runs as is unknown");
}
