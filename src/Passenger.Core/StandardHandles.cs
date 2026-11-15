namespace Passenger.Core;

/// <summary>This process's standard handles, or another handle of its, made ones a program it starts does not inherit,
/// on Windows, where a child started with its own pipes inherits every inheritable handle beside them (CreateProcess's
/// bInheritHandles): what that child leaves running (an MSYS gpg's gpg-agent) would hold them, and whoever reads this
/// process's output (git, reading its gpg's signature) would never see it end.</summary>
public static class StandardHandles
{
    public static void NotInherited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        foreach (int standard in (int[])[Kernel32.StandardInput, Kernel32.StandardOutput, Kernel32.StandardError])
        {
            // A handle that is none (a process started with no console) is left as it is.
            _ = NotInherited(Kernel32.GetStdHandle(standard));
        }
    }

    /// <summary>One handle of this process's made one a program it starts does not inherit, on Windows: whether it was.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool NotInherited(nint handle) => Kernel32.SetHandleInformation(handle, Kernel32.Inherited, 0);
}
