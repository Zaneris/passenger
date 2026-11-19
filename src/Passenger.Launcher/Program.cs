using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Passenger.Launcher;

/// <summary><c>passenger-ide [folder, solution or file] [flags]</c> on Windows: starts the Passenger.exe beside its
/// assembly with the arguments it was given, each as written, in the folder it was started in, so a relative path is read
/// from there, then ends at once, leaving Passenger running.</summary>
public static partial class Program
{
    public static int Main(string[] args)
    {
        ProcessStartInfo start = new(Path.Combine(AppContext.BaseDirectory, "Passenger.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (string argument in args)
        {
            start.ArgumentList.Add(argument);
        }
        try
        {
            using Process started = Process.Start(start) ?? throw new Win32Exception($"{start.FileName} did not start");
            // Started by the program in front (a terminal), this passes its leave to come forward on to Passenger.
            AllowSetForegroundWindow(started.Id);
            return 0;
        }
        catch (Win32Exception failed)
        {
            // A program of no console says why in the console of the terminal that started it, where there is one.
            if (AttachConsole(ParentsConsole))
            {
                using StreamWriter error = new(Console.OpenStandardError()) { AutoFlush = true };
                error.WriteLine($"passenger-ide: Passenger did not start from {start.FileName}: {failed.Message}");
            }
            return 1;
        }
    }

    private const uint ParentsConsole = unchecked((uint)-1);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);
}
