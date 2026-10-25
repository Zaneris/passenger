using Passenger.Dap;

namespace Passenger.DebugAdapter;

/// <summary>The adapter's process: the Debug Adapter Protocol on standard input and output, until the client
/// disconnects or goes away. Takes netcoredbg's flags, so a client written for it runs this.</summary>
public static class AdapterProgram
{
    /// <summary>Runs the adapter on the flags given; a flag it does not know is fatal (exit code 2), so a typo never runs
    /// something other than what was asked. <paramref name="usage"/> is how the help names the command.</summary>
    public static async Task<int> RunAsync(string[] args, string usage)
    {
        // Every value shown reads as C# reads it, whatever the machine's culture, so Set Value takes it back.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        string? logPath = null;
        string? symbolCache = null;
        bool remote = false;
        foreach (string arg in args)
        {
            if (arg is "--help" or "-h")
            {
                Console.WriteLine($"{usage} {AdapterFlags.Interpreter} [{AdapterFlags.EngineLogging}FILE] [{AdapterFlags.SymbolCache}FOLDER] [{AdapterFlags.Remote}]");
                Console.WriteLine("  The Debug Adapter Protocol on standard input and output; --engineLogging writes the engine's log to FILE.");
                Console.WriteLine("  --symbolCache keeps the symbols decompiled for assemblies that ship none in FOLDER, pruned as the adapter");
                Console.WriteLine("  starts; without it their frames have no source.");
                Console.WriteLine("  --remote: the client is on another machine (this adapter in a container), so the processes it tells of");
                Console.WriteLine("  are not local to the client (isLocalProcess false).");
                return 0;
            }
            else if (arg == AdapterFlags.Remote)
            {
                remote = true;
            }
            else if (arg.StartsWith(AdapterFlags.SymbolCache, StringComparison.Ordinal))
            {
                if (arg.Length == AdapterFlags.SymbolCache.Length)
                {
                    Console.Error.WriteLine("--symbolCache needs a folder");
                    return 2;
                }
                symbolCache = arg[AdapterFlags.SymbolCache.Length..];
            }
            else if (arg.StartsWith(AdapterFlags.AnyInterpreter, StringComparison.Ordinal))
            {
                if (arg != AdapterFlags.Interpreter)
                {
                    Console.Error.WriteLine($"Only --interpreter=vscode (the Debug Adapter Protocol) is supported, not '{arg}'");
                    return 2;
                }
            }
            else if (arg.StartsWith(AdapterFlags.EngineLogging, StringComparison.Ordinal) && arg.Length > AdapterFlags.EngineLogging.Length)
            {
                logPath = arg[AdapterFlags.EngineLogging.Length..];
            }
            else
            {
                Console.Error.WriteLine($"Unknown flag '{arg}'; --help lists them");
                return 2;
            }
        }

        // How long an evaluation may run before it is stopped; a malformed value is fatal, as a flag's is.
        try
        {
            Passenger.Debugger.Extensions.EvaluationTimeout = AdapterEnvironment.EvaluationTimeout;
        }
        catch (FormatException error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }

        StreamWriter? logWriter = null;
        if (logPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
            logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
        }
        Lock logGate = new();
        void Log(string message)
        {
            if (logWriter is not null)
            {
                lock (logGate)
                {
                    logWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
                }
            }
        }

        // A fault in work nothing awaits, as it happens: on the error stream, and in the engine's log when it is kept.
        Passenger.Core.UnawaitedWork.Install(error =>
        {
            Console.Error.WriteLine($"Error: {error}");
            Log($"Error: {error}");
        });
        Log("Starting");
        // No program the adapter launches may hold the protocol: on macOS and Linux it is given descriptors 0, 1 and 2 as its own.
        (Stream input, Stream output) = Passenger.Debugger.SuspendedLaunch.DetachProtocol();
        DapConnection connection = new(input, output, Log);
        DebugAdapter adapter = new(connection, Log, symbolCache is null ? null : new Passenger.Debugger.Decompilation.SymbolCache(symbolCache, Log)) { Remote = remote };
        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.ShutdownRequested += () => disconnected.TrySetResult();
        connection.Start();
        // Ends at a disconnect, or as standard input ends, which a client gone without disconnecting leaves.
        await Task.WhenAny(disconnected.Task, connection.Completion);
        // A client that closed its end without disconnecting leaves the debuggee to be let go here.
        await adapter.AbortAsync();
        Log("Exiting");
        logWriter?.Dispose();
        // A read of standard input cannot be cancelled, and the client may hold its end open: exit rather than wait.
        Environment.Exit(0);
        return 0;
    }
}
