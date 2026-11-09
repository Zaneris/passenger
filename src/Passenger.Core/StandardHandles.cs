using System.Runtime.InteropServices;

namespace Passenger.Core;

/// <summary>This process's standard handles, or another handle of its, made ones a program it starts does not inherit,
/// on Windows, where a child started with its own pipes inherits every inheritable handle beside them (CreateProcess's
/// bInheritHandles): what that child leaves running (an MSYS gpg's gpg-agent) would hold them, and whoever reads this
/// process's output (git, reading its gpg's signature) would never see it end.</summary>
public static partial class StandardHandles
{
    private const int Input = -10;
    private const int Output = -11;
    private const int Error = -12;

    /// <summary>HANDLE_FLAG_INHERIT.</summary>
    private const uint Inherit = 1;

    public static void NotInherited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        foreach (int standard in (int[])[Input, Output, Error])
        {
            // A handle that is none (a process started with no console) is left as it is.
            _ = NotInherited(GetStdHandle(standard));
        }
    }

    /// <summary>One handle of this process's made one a program it starts does not inherit, on Windows: whether it was.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool NotInherited(nint handle) => SetHandleInformation(handle, Inherit, 0);

    [LibraryImport("kernel32.dll", EntryPoint = "GetStdHandle", SetLastError = true)]
    private static partial nint GetStdHandle(int which);

    [LibraryImport("kernel32.dll", EntryPoint = "SetHandleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);
}
