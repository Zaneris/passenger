using Passenger.Core;
using Xunit;

namespace Passenger.Tests;

/// <summary>The disk images tests mount as volumes of their own, on macOS.</summary>
internal static class TestVolumes
{
    /// <summary>Takes a test's volume off, even while something still reads it, each tool run through <paramref name="ran"/>.
    /// The system's own readers of new files (Spotlight, the code-signing checks) can hold a volume just written busy for a
    /// moment, which even a forced detach refuses (exit code 16): tried again for up to 10 s, until the volume is off however
    /// hdiutil answered, then failed naming what holds it.</summary>
    public static void Detach(string volume, Func<string, string[], ChildResult> ran)
    {
        ChildResult detached = null!;
        bool Off()
        {
            detached = ran("/usr/bin/hdiutil", ["detach", volume, "-force"]);
            return detached.Succeeded || !ran("/sbin/mount", []).Output.Contains($" on {PathNames.Canonical(volume)} (", StringComparison.Ordinal);
        }
        if (Waiting.Until(Off, TimeSpan.FromSeconds(10), every: TimeSpan.FromSeconds(1)))
        {
            return;
        }
        ChildResult holders = ran("/usr/sbin/lsof", ["+D", volume]);
        Assert.Fail($"The test's volume {volume} could not be detached (hdiutil exited with code {detached.ExitCode}: {detached.Error.Trim()}); what has files open on it:\n{holders.Output}");
    }
}
