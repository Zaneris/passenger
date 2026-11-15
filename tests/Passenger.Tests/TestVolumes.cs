using Passenger.Core;
using Xunit;

namespace Passenger.Tests;

/// <summary>The disk images tests mount as volumes of their own, on macOS.</summary>
internal static class TestVolumes
{
    /// <summary>The waits before each try again to let a test's image go: half a minute in all, scaled.</summary>
    private static readonly double[] Pauses = [0.5, 1, 1, 2, 2, 4, 8, 12];

    /// <summary>Takes a test's volume off, its image let go of by its disks as an update lets go of its own
    /// (<see cref="AttachedImages.DetachAsync"/>): the system's own readers of new files (Spotlight, the code-signing checks)
    /// can hold a disk just written open for a moment, which even a forced detach refuses, and an unmounted volume whose
    /// disk is held stays attached, its image locked. Tried again, forced from the fourth try, until hdiutil lists the
    /// image no more; then failed naming what held its disk.</summary>
    public static void Detach(string volume, string image)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("A test's volume is a disk image, on macOS alone");
        }
        string canonical = PathNames.Canonical(image);
        LetGo(attachment => PathNames.Same(PathNames.Canonical(attachment.Image), canonical), $"The test's volume {volume}");
    }

    /// <summary>Every image under a test's folder let go of as <see cref="Detach"/> lets one go: what a test that failed
    /// partway left attached.</summary>
    public static void DetachUnder(string folder)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("A test's disk image is macOS's");
        }
        string under = PathNames.Canonical(folder);
        LetGo(attachment => PathNames.IsUnder(PathNames.Canonical(attachment.Image), under), $"The images under {folder}");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void LetGo(Func<AttachedImages.Attachment, bool> ofImage, string what)
    {
        TimeSpan[] pauses = [.. Pauses.Select(seconds => TimeSpan.FromSeconds(seconds).Scaled())];
        AttachedImages.LetGo letGo = Ui.AwaitPumped(Waiting.Apart(() => AttachedImages.DetachAsync(ofImage,
            (disk, attempt) => ChildProcesses.Command("/usr/bin/hdiutil", attempt >= 4 ? ["detach", disk, "-force"] : ["detach", disk]), pauses)),
            $"{what} to be detached", TimeSpan.FromSeconds(Pauses.Sum()).Scaled() + TimeSpan.FromMinutes(2).Scaled());
        Assert.True(letGo.Detached,
            $"{what} could not be detached: hdiutil still lists them after {letGo.Tries} tries, which said: {string.Join(" ", letGo.Said.Select(words => $"[{words}]"))}");
    }
}
