using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Passenger.Core;

/// <summary>On Windows, the console every child shares, its code pages UTF-8 for output and input alike, the encoding
/// Passenger reads a child's output and writes its input in: a .NET program writes its redirected output and reads its
/// input in its console's code page, which a console made for one child alone takes from the system (437 on an English
/// Windows). It is the process's own console where it has one, else one made for it with no window, whose Ctrl+C or
/// Ctrl+Break sent to all it holds the process passes over; its code pages are set again before each start, so a code
/// page one child set is never the next one's.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class SharedConsole
{
    private const uint Utf8CodePage = 65001;

    private const uint CtrlC = 0;
    private const uint CtrlBreak = 1;

    private const int StandardInput = -10;
    private const int StandardOutput = -11;
    private const int StandardError = -12;

    /// <summary>How long the holder of a console being made has to write its first words, the console then made.</summary>
    private static readonly TimeSpan MakeLimit = TimeSpan.FromSeconds(10);

    private static readonly Lock Gate = new();

    /// <summary>Whether the process has a console to share, once looked for and made where it had none.</summary>
    private static bool? attached;

    /// <summary>Whether a child started now shares the process's console, its code pages set to UTF-8 first; false where
    /// there is none and none could be made, the child then given one of its own in the system's code page.</summary>
    public static bool Ready()
    {
        lock (Gate)
        {
            uint process;
            attached ??= GetConsoleProcessList(&process, 1) > 0 || Make();
            return attached.Value && SetConsoleOutputCP(Utf8CodePage) && SetConsoleCP(Utf8CodePage);
        }
    }

    /// <summary>Attaches the process to a console with no window: a holder's (<c>cmd /c pause</c>, started with none), taken
    /// once it has written, then ended. The process's standard handles stay as they were. False where none could be made.</summary>
    private static bool Make()
    {
        nint input = GetStdHandle(StandardInput);
        nint output = GetStdHandle(StandardOutput);
        nint error = GetStdHandle(StandardError);
        ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", "pause"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Lifeline.Prepare(start);
        using Process holder = new() { StartInfo = start };
        string? unmade = null;
        try
        {
            holder.Start();
            // Its words come once its console is there; a holder silent past the limit is ended, which ends the read.
            using (CancellationTokenSource limit = new(MakeLimit))
            using (limit.Token.Register(() => End(holder)))
            {
                unmade = holder.StandardOutput.BaseStream.ReadByte() < 0 ? $"{start.FileName} wrote nothing within {MakeLimit.TotalSeconds:0} s"
                    : !AttachConsole((uint)holder.Id) ? $"error {Marshal.GetLastPInvokeError()} attaching to its console"
                    : !SetConsoleCtrlHandler(&PassOver, add: true) ? $"error {Marshal.GetLastPInvokeError()} passing over its Ctrl+C"
                    : null;
            }
        }
        catch (Exception failed) when (failed is Win32Exception or IOException or InvalidOperationException)
        {
            unmade = failed.Message;
        }
        finally
        {
            End(holder);
            SetStdHandle(StandardInput, input);
            SetStdHandle(StandardOutput, output);
            SetStdHandle(StandardError, error);
        }
        if (unmade is not null)
        {
            PassengerLog.Write($"No console was made for the programs Passenger starts to share ({unmade}): each has one of its own, in the system's code page");
        }
        return unmade is null;
    }

    /// <summary>Ends the holder alone: the console it made lives on with the process attached to it.</summary>
    private static void End(Process holder)
    {
        try
        {
            holder.Kill();
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // It had ended, or never started.
        }
    }

    /// <summary>A Ctrl+C or Ctrl+Break a child sends to the whole console, which would end the process, passed over.</summary>
    [UnmanagedCallersOnly]
    private static int PassOver(uint signal) => signal is CtrlC or CtrlBreak ? 1 : 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetConsoleProcessList(uint* processes, uint count);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleOutputCP(uint codePage);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCP(uint codePage);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(delegate* unmanaged<uint, int> handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int which);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int which, nint handle);
}
