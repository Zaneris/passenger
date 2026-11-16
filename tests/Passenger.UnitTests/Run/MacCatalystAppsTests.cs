using Passenger.App.Run.Devices;

namespace Passenger.UnitTests.Run;

/// <summary>How a Mac Catalyst app is opened: by the system's open through a shell that carries its output, a new instance
/// waited for to its end, the app's own arguments after open's --args; and, where nobody sees the window that runs it,
/// hidden and never brought in front, so its windows never reach the person's screen.</summary>
public class MacCatalystAppsTests
{
    private static readonly MacCatalystApp App = new("/src/Shop/bin/Debug/net11.0-maccatalyst/maccatalyst-arm64/Shop.app",
        "/src/Shop/bin/Debug/net11.0-maccatalyst/maccatalyst-arm64/Shop.app/Contents/MacOS/Shop");

    [Fact]
    public void An_app_nobody_sees_opens_hidden_behind_every_window_with_its_own_arguments_after_opens()
    {
        IReadOnlyList<string> opening = MacCatalystApps.Opening(App, unseen: true, ["--verbose", "a b"]);

        Assert.Equal(["-c", MacCatalystApps.Opener, "sh", "-n", "-W", "-g", "-j", "-a", App.Bundle, "--args", "--verbose", "a b"], opening);
    }

    [Fact]
    public void An_app_a_person_runs_opens_in_front_as_the_system_opens_an_app()
    {
        IReadOnlyList<string> opening = MacCatalystApps.Opening(App, unseen: false, []);

        Assert.Equal(["-c", MacCatalystApps.Opener, "sh", "-n", "-W", "-a", App.Bundle, "--args"], opening);
        Assert.DoesNotContain("-g", opening);
        Assert.DoesNotContain("-j", opening);
    }
}
