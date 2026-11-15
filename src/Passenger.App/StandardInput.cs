using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Passenger.Core;

namespace Passenger.App;

/// <summary>The process's standard input, read by the process alone: an agent writes its requests there, which a program
/// the process starts with its input left as it was (vstest.console and the test hosts it starts) would read in its place.
/// So what was given is read through a descriptor (on Windows a handle) of the process's own, which nothing started
/// inherits, and the standard input becomes the null device, whose end a program started reads at once.</summary>
internal static partial class StandardInput
{
    /// <summary>DUPLICATE_SAME_ACCESS.</summary>
    private const uint SameAccess = 2;

    /// <summary>On Windows, the null device made the standard input, held as long as the process runs.</summary>
    private static SafeFileHandle? nothing;

    /// <summary>The standard input given, as a stream of the process's own (empty where none was given); the standard
    /// input is the null device after.</summary>
    public static Stream Take()
    {
        SafeFileHandle? given = OperatingSystem.IsWindows() ? TakeOnWindows() : TakeOnUnix();
        return given is null ? Stream.Null : new FileStream(given, FileAccess.Read, 1);
    }

    private static SafeFileHandle? TakeOnUnix()
    {
        // The console's own stream reads descriptor 0 whatever it is now, so the input is read through a copy.
        int kept = Libc.Duplicate(0);
        SafeFileHandle? given = kept >= 0 ? new SafeFileHandle(kept, ownsHandle: true) : null;
        if (kept >= 0)
        {
            Descriptors.CloseOnExec(kept);
        }
        using SafeFileHandle none = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read);
        if (Libc.DuplicateTo((int)none.DangerousGetHandle(), 0) != 0)
        {
            Unmade();
        }
        return given;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static SafeFileHandle? TakeOnWindows()
    {
        SafeFileHandle? given = null;
        nint handle = Kernel32.GetStdHandle(Kernel32.StdInputHandle);
        if (handle is not (0 or -1))
        {
            // The handle given stays open, but no program started inherits it.
            Kernel32.SetHandleInformation(handle, Kernel32.Inherited, 0);
            if (DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), out nint copy, 0, false, SameAccess))
            {
                given = new SafeFileHandle(copy, ownsHandle: true);
            }
        }
        nothing = File.OpenHandle("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (!Kernel32.SetHandleInformation(nothing.DangerousGetHandle(), Kernel32.Inherited, Kernel32.Inherited) || !Kernel32.SetStdHandle(Kernel32.StdInputHandle, nothing.DangerousGetHandle()))
        {
            Unmade();
        }
        return given;
    }

    private static void Unmade() =>
        IdeErrors.Write($"The standard input could not be made the null device (error {Marshal.GetLastPInvokeError()}): a program started may read what was given there");

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}
