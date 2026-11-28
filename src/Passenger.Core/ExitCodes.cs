using System.Globalization;

namespace Passenger.Core;

/// <summary>How a program's exit code is said to a person: on macOS and Linux one above 128 with the signal it stands for
/// where that signal ends a program on both ("exit code 137 (SIGKILL)"), on Windows a negative one with its hex, as its
/// status codes are written ("exit code -1073741819 (0xC0000005)"); <see cref="Words(int)"/> says it in the person's language.</summary>
public static partial class ExitCodes
{
    /// <summary>The signals numbered alike on macOS and Linux; any other is said by its number.</summary>
    private static readonly string?[] Signals =
        [null, "SIGHUP", "SIGINT", "SIGQUIT", "SIGILL", "SIGTRAP", "SIGABRT", null, "SIGFPE", "SIGKILL", null, "SIGSEGV", null, "SIGPIPE", "SIGALRM", "SIGTERM"];

    /// <summary>The signals past those that end a program on both macOS and Linux; a code standing for any other (one that
    /// stops a program or is passed over, 150 for SIGTTOU) is the program's own, as .NET's host exits 150 lacking a runtime.</summary>
    private static readonly int[] Ending = [24, 25, 26, 27, 30, 31];

    /// <summary>The exit code as this system writes it.</summary>
    public static string Said(int code) => Said(code, OperatingSystem.IsWindows());

    /// <summary>The exit code as this system writes it, as words said in each reader's language.</summary>
    public static Words Words(int code) => Words(code, OperatingSystem.IsWindows());

    /// <summary>The exit code as Windows (<paramref name="windows"/>) or macOS and Linux write it, as words.</summary>
    public static Words Words(int code, bool windows)
    {
        string number = code.ToString(CultureInfo.InvariantCulture);
        if (windows)
        {
            return code < 0 ? CodeNoted(number, $"0x{(uint)code:X8}") : Code(number);
        }
        int signal = code - 128;
        if (signal is < 1 || (signal >= Signals.Length && !Ending.Contains(signal)))
        {
            return Code(number);
        }
        return signal < Signals.Length && Signals[signal] is { } name ? CodeNoted(number, name)
            : CodeSignal(number, signal.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The exit code as Windows (<paramref name="windows"/>) or macOS and Linux write it, in English.</summary>
    public static string Said(int code, bool windows) => Words(code, windows).English;
}
