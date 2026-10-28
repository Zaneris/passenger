using System.Runtime.InteropServices;

namespace Passenger.App;

/// <summary>The process's open files as macOS and Linux number them.</summary>
internal static partial class Descriptors
{
    /// <summary>FIOCLEX: macOS's _IO('f', 1), Linux's 0x5451.</summary>
    private static readonly nuint CloseOnExecRequest = OperatingSystem.IsMacOS() ? 0x20006601u : 0x5451u;

    /// <summary>Marks a descriptor to close as the process becomes another program, through ioctl, which takes no third
    /// argument here: fcntl's is variadic, which arm64 macOS passes on the stack, where a fixed signature never puts it.</summary>
    public static bool CloseOnExec(int descriptor) => Control(descriptor, CloseOnExecRequest) == 0;

    /// <summary>Marks every descriptor but standard input, output and error to close as the process becomes another program.</summary>
    public static void CloseAllOnExec()
    {
        foreach (string open in Directory.EnumerateFileSystemEntries("/dev/fd"))
        {
            if (int.TryParse(Path.GetFileName(open), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int descriptor)
                && descriptor > 2)
            {
                CloseOnExec(descriptor);
            }
        }
    }

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Control(int descriptor, nuint request);
}
