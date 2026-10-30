namespace Passenger.Core;

/// <summary>How a program's exit code is said to a person: on macOS and Linux one above 128 with the signal it stands for
/// ("exit code 137 (SIGKILL)"), on Windows a negative one with its hex, as its status codes are written ("exit code
/// -1073741819 (0xC0000005)").</summary>
public static class ExitCodes
{
    /// <summary>The signals numbered alike on macOS and Linux; any other is said by its number.</summary>
    private static readonly string?[] Signals =
        [null, "SIGHUP", "SIGINT", "SIGQUIT", "SIGILL", "SIGTRAP", "SIGABRT", null, "SIGFPE", "SIGKILL", null, "SIGSEGV", null, "SIGPIPE", "SIGALRM", "SIGTERM"];

    /// <summary>The exit code as this system writes it.</summary>
    public static string Said(int code) => Said(code, OperatingSystem.IsWindows());

    /// <summary>The exit code as Windows (<paramref name="windows"/>) or macOS and Linux write it.</summary>
    public static string Said(int code, bool windows)
    {
        if (windows)
        {
            return code < 0 ? $"exit code {code} (0x{(uint)code:X8})" : $"exit code {code}";
        }
        int signal = code - 128;
        if (signal is < 1 or > 64)
        {
            return $"exit code {code}";
        }
        return signal < Signals.Length && Signals[signal] is { } name ? $"exit code {code} ({name})" : $"exit code {code} (signal {signal})";
    }
}
