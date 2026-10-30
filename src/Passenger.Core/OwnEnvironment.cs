using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Passenger.Core;

/// <summary>This process's environment as every program it starts inherits it: .NET's copy and, on macOS and Linux, the
/// process's own as well, which a program forked from native code (a pseudo-terminal's shell) starts from, given only what
/// is set over it. Framework-free, since it is linked into what Passenger puts inside another program.</summary>
public static partial class OwnEnvironment
{
    /// <summary>Sets a variable in both, or takes it out of both when null or empty, as .NET does.</summary>
    public static void Set(string variable, string? value)
    {
        Environment.SetEnvironmentVariable(variable, value);
        // Windows' copy is the process's own.
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        if ((string.IsNullOrEmpty(value) ? UnsetEnv(variable) : SetEnv(variable, value, 1)) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"{variable} could not be changed in the process's environment");
        }
    }

    [LibraryImport("libc", EntryPoint = "unsetenv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnsetEnv(string name);

    [LibraryImport("libc", EntryPoint = "setenv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnv(string name, string value, int overwrite);
}
