using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Passenger.Core;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>The runtime's own threads of one program an evaluation leaves running while it holds the program's: its tiering
/// worker, which runs none of the program's code and, held, stands partway through compiling a method, inside a type's load
/// or the check of a call to inline, holding what an evaluation may need. Nothing ICorDebug says of a thread tells it, so it
/// is told by what the runtime gives it: on Windows and Linux the name it is given; on macOS, which keeps no name one thread
/// gives another, the routine it was started at, a return into it on its own stack, read against the runtime's symbols.</summary>
internal sealed partial class RuntimeThreads(int processId)
{
    /// <summary>The names runtimes give their tiering worker: .NET 11's, and .NET 10's and earlier.</summary>
    private static readonly string[] TieringWorkerNames = [".NET Tiered JIT", ".NET Tiered Compilation Worker"];

    /// <summary>The routine the runtime starts its tiering worker at, as its library's symbol table names it, and that library.</summary>
    private const string TieringWorkerStart = "__ZN24TieredCompilationManager29BackgroundWorkerBootstrapper0EPv";
    private const string RuntimeLibrary = "libcoreclr.dylib";

    /// <summary>How much of a thread's stack above the runtime's frame at its base is read, in pieces of a size.</summary>
    private const int StackRead = 4096;
    private const int StackPiece = 512;

    /// <summary>How much of a thread's name Linux keeps.</summary>
    private const int LinuxNameLength = 15;

    /// <summary>OpenThread's THREAD_QUERY_LIMITED_INFORMATION.</summary>
    private const uint ThreadQueryLimitedInformation = 0x0800;

    /// <summary>proc_pidinfo's PROC_PIDREGIONPATHINFO and its answer, struct proc_regionwithpathinfo: its size, and where in
    /// it a region's protection, offset in its file, address, size and file's path are; and VM_PROT_EXECUTE.</summary>
    private const int RegionPathInfo = 8;
    private const int RegionInfoSize = 1272;
    private const int RegionOffset = 16;
    private const int RegionAddress = 80;
    private const int RegionSize = 88;
    private const int RegionPath = 248;
    private const uint Executes = 4;

    /// <summary>The most regions of a program looked through for the runtime's library.</summary>
    private const int MostRegions = 1 << 20;

    public int ProcessId { get; } = processId;

    /// <summary>On macOS, whether each thread looked at was started at the tiering worker's routine, by its id: a thread's
    /// start never changes, and macOS gives no id to two threads.</summary>
    private readonly Dictionary<int, bool> startedAt = [];

    /// <summary>On macOS, where the tiering worker's start routine lies in the program, once read; null where it can't be.</summary>
    private (ulong Start, ulong End)? workerStart;
    private bool workerStartRead;

    /// <summary>The name the system keeps for a thread of a program, as the runtime or the program gave it: on Windows and
    /// Linux; null elsewhere, or where it can't be read.</summary>
    public static string? SystemName(int processId, int threadId) =>
        OperatingSystem.IsWindows() ? NameOnWindows(processId, threadId) : OperatingSystem.IsLinux() ? NameOnLinux(processId, threadId) : null;

    /// <summary>Whether a thread of the program is the runtime's tiering worker.</summary>
    public bool IsTieringWorker(ICorDebugProcess process, ICorDebugThread thread) =>
        OperatingSystem.IsWindows() ? NameOnWindows(ProcessId, thread.Id) is { } name && TieringWorkerNames.Contains(name)
        : OperatingSystem.IsLinux() ? NameOnLinux(ProcessId, thread.Id) is { } kept && TieringWorkerNames.Any(name => name[..Math.Min(name.Length, LinuxNameLength)] == kept)
        : OperatingSystem.IsMacOS() && StartedAtWorkerOnMac(process, thread);

    /// <summary>Whether a thread was started at the tiering worker's routine: its stack above the runtime's frame at its base
    /// holds a return into that routine. A thread not yet that far is looked at again the next time.</summary>
    [SupportedOSPlatform("macos")]
    private bool StartedAtWorkerOnMac(ICorDebugProcess process, ICorDebugThread thread)
    {
        int id = thread.Id;
        if (startedAt.TryGetValue(id, out bool known))
        {
            return known;
        }
        if (!workerStartRead)
        {
            workerStartRead = true;
            workerStart = WorkerStartOnMac(ProcessId);
        }
        if (workerStart is not { } start || BaseFrameOf(thread) is not { } frame || ReadUp(process, frame) is not { Length: > 0 } stack)
        {
            return false;
        }
        return startedAt[id] = ReturnsInto(stack, frame, start);
    }

    /// <summary>Whether a stack, read from the address given up, holds a frame record whose return lies in a routine: a
    /// saved frame pointer to a record above it, the return address beside it, as arm64's and x64's frames both keep them.</summary>
    internal static bool ReturnsInto(ReadOnlySpan<byte> stack, ulong from, (ulong Start, ulong End) routine)
    {
        for (int at = (int)((8 - (from % 8)) % 8); at + 16 <= stack.Length; at += 8)
        {
            ulong saved = BinaryPrimitives.ReadUInt64LittleEndian(stack[at..]);
            ulong returned = BinaryPrimitives.ReadUInt64LittleEndian(stack[(at + 8)..]);
            if (returned >= routine.Start && returned < routine.End && saved > from + (ulong)at)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Where the runtime's frame at a thread's base stands on its stack: the transition from native code its start
    /// pushes before it runs anything of the runtime's own; null for a thread without one, or one whose frames can't be read.</summary>
    private static ulong? BaseFrameOf(ICorDebugThread thread)
    {
        try
        {
            foreach (ICorDebugChain chain in thread.EnumerateChains())
            {
                if (chain.Reason is not CorDebugChainReason.CHAIN_THREAD_START)
                {
                    continue;
                }
                foreach (ICorDebugFrame frame in chain.EnumerateFrames())
                {
                    if (frame is ICorDebugInternalFrame based && based.TryGetFrameType(out CorDebugInternalFrameType type) is Cor.S_OK
                        && type is CorDebugInternalFrameType.STUBFRAME_U2M && frame.TryGetStackRange(out CORDB_ADDRESS at, out _) is Cor.S_OK && at.Value != 0)
                    {
                        return at.Value;
                    }
                }
            }
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return null;
        }
        return null;
    }

    /// <summary>The program's memory from an address up, as much of <see cref="StackRead"/> as reads.</summary>
    private static byte[] ReadUp(ICorDebugProcess process, ulong from)
    {
        using MemoryStream read = new();
        for (ulong at = from; read.Length < StackRead; at += StackPiece)
        {
            byte[] piece = new byte[StackPiece];
            if (process.TryReadMemory(new CORDB_ADDRESS(at), StackPiece, piece, out nuint got) is not Cor.S_OK || got != StackPiece)
            {
                break;
            }
            read.Write(piece);
        }
        return read.ToArray();
    }

    /// <summary>Where the tiering worker's start routine lies in a program: the runtime's library's start as macOS maps it
    /// there, and the routine's place in it, from the library's symbols; null where either can't be read.</summary>
    [SupportedOSPlatform("macos")]
    private static (ulong Start, ulong End)? WorkerStartOnMac(int processId)
    {
        if (RuntimeImageOnMac(processId) is not { } image)
        {
            return null;
        }
        try
        {
            using FileStream library = File.OpenRead(image.File);
            return MachOFile.Functions(library, [TieringWorkerStart]) is { } functions && functions.TryGetValue(TieringWorkerStart, out (ulong Start, ulong End) routine)
                ? (image.Address + routine.Start, image.Address + routine.End)
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Where a program maps the start of the runtime's library to run it, and the library's file, as macOS lists the
    /// regions of a process of the user's own; null where none does.</summary>
    [SupportedOSPlatform("macos")]
    private static (ulong Address, string File)? RuntimeImageOnMac(int processId)
    {
        byte[] region = new byte[RegionInfoSize];
        ulong at = 0;
        for (int listed = 0; listed < MostRegions; listed++)
        {
            if (ProcPidInfo(processId, RegionPathInfo, at, region, RegionInfoSize) < RegionInfoSize)
            {
                return null;
            }
            ulong address = BinaryPrimitives.ReadUInt64LittleEndian(region.AsSpan(RegionAddress));
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(region.AsSpan(RegionSize));
            if (BinaryPrimitives.ReadUInt64LittleEndian(region.AsSpan(RegionOffset)) == 0 && (BinaryPrimitives.ReadUInt32LittleEndian(region) & Executes) != 0)
            {
                ReadOnlySpan<byte> path = region.AsSpan(RegionPath);
                int end = path.IndexOf((byte)0);
                string file = Encoding.UTF8.GetString(end < 0 ? path : path[..end]);
                if (Path.GetFileName(file) == RuntimeLibrary)
                {
                    return (address, file);
                }
            }
            if (size == 0 || address + size <= at)
            {
                return null;
            }
            at = address + size;
        }
        return null;
    }

    /// <summary>A thread's name as the process gave it, null where the thread is gone, another process's, or unnamed.</summary>
    [SupportedOSPlatform("windows")]
    private static string? NameOnWindows(int processId, int threadId)
    {
        nint thread = Kernel32.OpenThread(ThreadQueryLimitedInformation, false, (uint)threadId);
        if (thread == 0)
        {
            return null;
        }
        try
        {
            if (GetProcessIdOfThread(thread) != (uint)processId || GetThreadDescription(thread, out nint description) < 0 || description == 0)
            {
                return null;
            }
            try
            {
                return Marshal.PtrToStringUni(description);
            }
            finally
            {
                LocalFree(description);
            }
        }
        finally
        {
            Kernel32.CloseHandle(thread);
        }
    }

    /// <summary>A thread's name as Linux keeps it, its first 15 bytes, null where the thread is gone.</summary>
    private static string? NameOnLinux(int processId, int threadId)
    {
        try
        {
            return File.ReadAllText($"/proc/{processId}/task/{threadId}/comm").TrimEnd('\n');
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessIdOfThread")]
    private static partial uint GetProcessIdOfThread(nint thread);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "GetThreadDescription")]
    private static partial int GetThreadDescription(nint thread, out nint description);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial nint LocalFree(nint memory);

    [SupportedOSPlatform("macos")]
    [LibraryImport("libc", EntryPoint = "proc_pidinfo")]
    private static partial int ProcPidInfo(int process, int flavor, ulong argument, byte[] buffer, int size);
}
