using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Passenger.Core;

namespace Passenger.Debugger;

/// <summary>The innermost native frames of a program's threads on Windows, for the engine's log to say what a thread
/// standing outside managed code is in: each thread suspended a moment for its registers, its stack walked from them by the
/// system's dbghelp through the program's own unwind data, as x64 code keeps no frame chain, each frame named by its module
/// and its place in it, with the nearest name the module exports where that lies close. Only of a program of this process's
/// own processor, as ICorDebug's programs are; dbghelp is asked one call at a time, and never of a symbol server.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class NativeStacks
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint ThreadSuspendResume = 0x0002;
    private const uint ThreadGetContext = 0x0008;

    /// <summary>SYMOPT_UNDNAME, SYMOPT_DEFERRED_LOADS, SYMOPT_FAIL_CRITICAL_ERRORS, SYMOPT_NO_PROMPTS and SYMOPT_SECURE, which
    /// keeps dbghelp off every symbol server whatever _NT_SYMBOL_PATH says.</summary>
    private const uint SymbolOptions = 0x2 | 0x4 | 0x200 | 0x80000 | 0x40000;

    /// <summary>CONTEXT for x64 and arm64: its size, where its ContextFlags lie and the flags asking for its control and
    /// integer registers, and where its program counter, stack pointer and frame pointer lie.</summary>
    private static readonly (ushort Machine, int Size, int FlagsAt, uint Flags, int PcAt, int SpAt, int FpAt)? Context = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => (0x8664, 0x4D0, 0x30, 0x0010_0003, 0xF8, 0x98, 0xA0),
        Architecture.Arm64 => (0xAA64, 0x390, 0x0, 0x0040_0003, 0x108, 0x100, 0xF0),
        _ => null,
    };

    /// <summary>STACKFRAME64's room, which dbghelp fills past what is set here, and where its program counter, frame and
    /// stack addresses lie, each an ADDRESS64 whose mode follows its offset by 12 bytes.</summary>
    private const int FrameRoom = 512;
    private const int FramePc = 0;
    private const int FrameFrame = 32;
    private const int FrameStack = 48;
    private const uint FlatMode = 3;

    /// <summary>SYMBOL_INFOW: its own size, where its name's length, room and first character lie, and the room given.</summary>
    private const int SymbolSize = 88;
    private const int SymbolNameLength = 76;
    private const int SymbolNameRoom = 80;
    private const int SymbolName = 84;
    private const int SymbolRoom = 256;

    /// <summary>How far past an exported name a frame may lie and still be named by it: further, the module exports no name
    /// for the routine it is in, and only its place says where it is.</summary>
    private const ulong NearExport = 0x1000;

    private static readonly Lock Asking = new();

    /// <summary>dbghelp's routines StackWalk64 is handed to read a program's unwind data and modules, once found.</summary>
    private static (nint Tables, nint Bases)? walking;

    /// <summary>Each thread's innermost native frames, at most <paramref name="frames"/>, innermost first, by its id; a thread
    /// whose frames could not be read is left out.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<string>> Of(int processId, IReadOnlyCollection<int> threads, int frames)
    {
        Dictionary<int, IReadOnlyList<string>> stacks = [];
        if (Context is not { } context || threads.Count == 0)
        {
            return stacks;
        }
        using SafeProcessHandle process = Kernel32.OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)processId);
        if (process.IsInvalid)
        {
            return stacks;
        }
        lock (Asking)
        {
            SymSetOptions(SymbolOptions);
            if (!SymInitialize(process, string.Empty, true))
            {
                return stacks;
            }
            try
            {
                walking ??= Walking();
                (nint tables, nint bases) = walking.Value;
                foreach (int thread in threads)
                {
                    if (Walk(process, thread, context, frames, tables, bases) is { Count: > 0 } returns)
                    {
                        stacks[thread] = [.. returns.Select(at => Named(process, at))];
                    }
                }
            }
            finally
            {
                SymCleanup(process);
            }
        }
        return stacks;
    }

    private static (nint Tables, nint Bases) Walking()
    {
        nint dbghelp = NativeLibrary.Load("dbghelp.dll", typeof(NativeStacks).Assembly, DllImportSearchPath.System32);
        return (NativeLibrary.GetExport(dbghelp, "SymFunctionTableAccess64"), NativeLibrary.GetExport(dbghelp, "SymGetModuleBase64"));
    }

    /// <summary>A thread's innermost frames' addresses, read while it is suspended; null where it could not be.</summary>
    private static List<ulong>? Walk(SafeProcessHandle process, int threadId, (ushort Machine, int Size, int FlagsAt, uint Flags, int PcAt, int SpAt, int FpAt) context,
        int frames, nint tables, nint bases)
    {
        nint thread = Kernel32.OpenThread(ThreadSuspendResume | ThreadGetContext, false, (uint)threadId);
        if (thread == 0)
        {
            return null;
        }
        byte* registers = (byte*)NativeMemory.AlignedAlloc((nuint)context.Size, 16);
        byte* frame = (byte*)NativeMemory.AllocZeroed(FrameRoom);
        try
        {
            NativeMemory.Clear(registers, (nuint)context.Size);
            *(uint*)(registers + context.FlagsAt) = context.Flags;
            if (SuspendThread(thread) == uint.MaxValue)
            {
                return null;
            }
            try
            {
                if (!GetThreadContext(thread, registers))
                {
                    return null;
                }
                Address(frame, FramePc, *(ulong*)(registers + context.PcAt));
                Address(frame, FrameFrame, *(ulong*)(registers + context.FpAt));
                Address(frame, FrameStack, *(ulong*)(registers + context.SpAt));
                List<ulong> returns = [];
                while (returns.Count < frames && StackWalk64(context.Machine, process, thread, frame, registers, 0, tables, bases, 0)
                    && *(ulong*)(frame + FramePc) is var at and not 0)
                {
                    returns.Add(at);
                }
                return returns;
            }
            finally
            {
                Kernel32.ResumeThread(thread);
            }
        }
        finally
        {
            NativeMemory.Free(frame);
            NativeMemory.AlignedFree(registers);
            Kernel32.CloseHandle(thread);
        }
    }

    private static void Address(byte* frame, int at, ulong offset)
    {
        *(ulong*)(frame + at) = offset;
        *(uint*)(frame + at + 12) = FlatMode;
    }

    /// <summary>A frame's address as its module and place in it ("coreclr.dll+0x1a2b3c"), with the name exported nearest
    /// before it where that lies close ("ntdll.dll!NtWaitForAlertByThreadId+0x14"); the bare address outside any module.</summary>
    private static string Named(SafeProcessHandle process, ulong at)
    {
        ulong module = SymGetModuleBase64(process, at);
        if (module == 0)
        {
            return $"0x{at:x}";
        }
        char* name = stackalloc char[260];
        uint length = GetModuleBaseName(process, (nint)module, name, 260);
        string file = length == 0 ? $"0x{module:x}" : new string(name, 0, (int)length);
        byte* symbol = stackalloc byte[SymbolSize + SymbolRoom * sizeof(char)];
        new Span<byte>(symbol, SymbolSize + SymbolRoom * sizeof(char)).Clear();
        *(uint*)symbol = SymbolSize;
        *(uint*)(symbol + SymbolNameRoom) = SymbolRoom;
        if (SymFromAddr(process, at, out ulong past, symbol) && past < NearExport && *(uint*)(symbol + SymbolNameLength) is var named and > 0)
        {
            return $"{file}!{new string((char*)(symbol + SymbolName), 0, (int)Math.Min(named, SymbolRoom - 1))}+0x{past:x}";
        }
        return $"{file}+0x{at - module:x}";
    }

    [LibraryImport("kernel32.dll", EntryPoint = "SuspendThread", SetLastError = true)]
    private static partial uint SuspendThread(nint thread);

    [LibraryImport("kernel32.dll", EntryPoint = "GetThreadContext", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadContext(nint thread, void* context);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetModuleBaseNameW")]
    private static partial uint GetModuleBaseName(SafeProcessHandle process, nint module, char* name, uint size);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymSetOptions")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint SymSetOptions(uint options);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymInitializeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SymInitialize(SafeProcessHandle process, string searchPath, [MarshalAs(UnmanagedType.Bool)] bool invadeProcess);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymCleanup")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SymCleanup(SafeProcessHandle process);

    [LibraryImport("dbghelp.dll", EntryPoint = "StackWalk64", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StackWalk64(uint machine, SafeProcessHandle process, nint thread, void* frame, void* context, nint readMemory,
        nint functionTableAccess, nint getModuleBase, nint translateAddress);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymGetModuleBase64")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial ulong SymGetModuleBase64(SafeProcessHandle process, ulong address);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymFromAddrW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SymFromAddr(SafeProcessHandle process, ulong address, out ulong displacement, void* symbol);
}
