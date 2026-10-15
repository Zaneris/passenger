using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

// Originally based on https://github.com/lordmilko/ClrDebug/blob/5f46218f4b840ab8a94920623dc263b5f2334138/Samples/NetCore/Program.cs,
// Copyright (c) 2022 lordmilko, under the MIT licence THIRD_PARTY_NOTICES.md gives in full.
public static class ClrDebugExtensions
{
    /// <summary>dbgshim's word that a runtime has started (or was running already), on dbgshim's own thread. A starting
    /// runtime waits for this to return, so it returns only once the debugger has taken hold of the process.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe void OnRuntimeStartup(void* pCorDebug, void* parameter, int hr)
    {
        if (GCHandle.FromIntPtr((IntPtr)parameter).Target is RuntimeStartup startup)
        {
            startup.Log($"dbgshim's runtime-start callback entered for process {startup.ProcessId} after {startup.Elapsed.TotalSeconds:F3} s: 0x{hr:X8}");
            ICorDebug? corDebug = ComInterfaceMarshaller<ICorDebug>.ConvertToManaged(pCorDebug);
            startup.Answered.TrySetResult((corDebug, hr));
            // Bounded, so an attach that never returns cannot hold the program at its start for good.
            if (!startup.LetGo.Wait(TimeSpan.FromSeconds(30)))
            {
                startup.Log($"dbgshim's callback for process {startup.ProcessId} was released after 30 s without the debugger taking hold");
            }
        }
    }

    /// <summary>What dbgshim's callback hands over, and what holds a starting runtime in it until the debugger has hold.</summary>
    private sealed class RuntimeStartup(int processId, Action<string>? log)
    {
        private readonly long began = Stopwatch.GetTimestamp();

        public int ProcessId { get; } = processId;

        public TimeSpan Elapsed => Stopwatch.GetElapsedTime(began);

        // Called from unmanaged code: a failed log must never escape the callback.
        public void Log(string message)
        {
            try
            {
                log?.Invoke(message);
            }
            catch
            {
            }
        }

        public TaskCompletionSource<(ICorDebug? CorDebug, int Hr)> Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set once the debugger has taken hold of the process, or given up on it; never disposed, since dbgshim's
        /// thread may still be waiting on it after the attach has failed.</summary>
        public ManualResetEventSlim LetGo { get; } = new();
    }

    /// <summary>A process taken hold of as its runtime starts: registered for, then let go (<paramref name="resume"/>, handed
    /// the runtime's answer so it can stop waiting on a port once the runtime has answered), then <paramref name="attach"/>
    /// run while the starting runtime waits in dbgshim's callback, so none of the program runs before the debugger has it.
    /// A runtime already running answers at once, and is taken hold of where it is.</summary>
    public static async Task<(ICorDebug CorDebug, ICorDebugProcess Process)> Automatic(int pid, Func<Task, Task>? resume, Func<ICorDebug, ICorDebugProcess> attach, Action<string>? log = null, CancellationToken cancellation = default)
    {
        using Process process = Process.GetProcessById(pid);
        IntPtr unregisterToken = IntPtr.Zero;
        RuntimeStartup startup = new(pid, log);
        GCHandle startupHandle = GCHandle.Alloc(startup);
        try
        {
            unsafe
            {
                int registerHr = DbgShim.RegisterForRuntimeStartup(checked((uint)pid), &OnRuntimeStartup, GCHandle.ToIntPtr(startupHandle), out unregisterToken);
                if (registerHr < 0)
                {
                    throw new InvalidOperationException($"dbgshim could not watch for its runtime to start: {Described(registerHr)}", Marshal.GetExceptionForHR(registerHr));
                }
            }
            log?.Invoke($"Registered for the runtime's start in process {pid}");

            if (resume is not null)
            {
                log?.Invoke($"Letting process {pid} start, after {startup.Elapsed.TotalSeconds:F3} s");
                await resume(startup.Answered.Task);
                log?.Invoke($"Process {pid}'s resume returned, after {startup.Elapsed.TotalSeconds:F3} s");
            }

            while (!startup.Answered.Task.IsCompleted)
            {
                cancellation.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    string exit = OperatingSystem.IsWindows() ? $" with exit code {process.ExitCode}" : "";
                    throw new InvalidOperationException($"Process {pid} ended{exit} before its runtime answered dbgshim's startup handshake (after {startup.Elapsed.TotalSeconds:F3} s)");
                }
                await Task.WhenAny(startup.Answered.Task, Task.Delay(100, cancellation)).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            (ICorDebug? CorDebug, int Hr) result = await startup.Answered.Task.ConfigureAwait(false);
            log?.Invoke($"The runtime-start callback for process {pid} is being handled after {startup.Elapsed.TotalSeconds:F3} s: 0x{result.Hr:X8}");
            if (result.CorDebug is not { } corDebug)
            {
                throw new InvalidOperationException($"dbgshim could not take hold of process {pid}'s runtime: {Described(result.Hr)}");
            }
            try
            {
                return (corDebug, attach(corDebug));
            }
            catch (Exception error)
            {
                log?.Invoke($"Taking hold of process {pid} as its runtime started failed: {error}");
                throw new InvalidOperationException(Described(error.HResult), error);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The debug session ended while waiting for process {pid}'s runtime to start (after {startup.Elapsed.TotalSeconds:F3} s)");
        }
        finally
        {
            // Before unregistering, which waits for dbgshim's thread, the one waiting on this.
            startup.LetGo.Set();
            if (unregisterToken != IntPtr.Zero)
            {
                DbgShim.UnregisterForRuntimeStartup(unregisterToken);
            }
            startupHandle.Free();
        }
    }

    /// <summary>An HRESULT as a person reads it: the system's words, and for the one Windows gives a debugger kept from a
    /// process, when it gives it (ICorDebug attaches as Windows' own debugger too, and puts a handle in the process).</summary>
    private static string Described(int hr) => hr switch
    {
        unchecked((int)0x80070005) => "Windows denied the debugger access to the process (E_ACCESSDENIED), as it does to one that is ending or that another debugger has",
        _ => Marshal.GetExceptionForHR(hr)?.Message ?? $"0x{hr:X8}",
    };
}
