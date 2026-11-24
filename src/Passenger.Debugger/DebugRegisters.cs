using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Passenger.Core;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>The debug registers of the program's threads, as its data breakpoints set them (x64's, or x86's in a 32-bit
/// adapter debugging a 32-bit program): Dr0 to Dr3 hold the addresses watched, Dr7 has each on, for a write, and how
/// many bytes it covers, and Dr6 says which one a write hit. Each thread is set twice: in the context the runtime resumes
/// it with when it is stopped in managed code (ICorDebugProcess.SetThreadContext), and on the thread itself (Windows'
/// SetThreadContext), which a thread stopped outside managed code, or one the runtime resumes without its saved debug
/// registers, runs on with.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class DebugRegisters
{
    /// <summary>CONTEXT as a program of this process's own processor has it: its size, where its flags lie, how wide its
    /// debug registers are and where Dr0, Dr6 and Dr7 lie, and the flag naming its processor.</summary>
    private readonly record struct Layout(int Size, int FlagsAt, int Width, int Dr0At, int Dr6At, int Dr7At, uint Processor);

    private static readonly Layout Context = RuntimeInformation.ProcessArchitecture == Architecture.X86
        ? new(0x2CC, 0x0, sizeof(uint), 0x4, 0x14, 0x18, 0x0001_0000)
        : new(0x4D0, 0x30, sizeof(ulong), 0x48, 0x68, 0x70, 0x0010_0000);

    private static uint ContextControl => Context.Processor | 0x1;
    private static uint ContextInteger => Context.Processor | 0x2;
    private static uint ContextDebugRegisters => Context.Processor | 0x10;

    private const uint ThreadSuspendResume = 0x0002;
    private const uint ThreadGetContext = 0x0008;
    private const uint ThreadSetContext = 0x0010;

    /// <summary>The slots a write hit, by the context the runtime caught it in: Dr6's low four bits.</summary>
    public static IEnumerable<int> Hit(byte[] context)
    {
        if (context.Length < Context.Dr6At + Context.Width)
        {
            yield break;
        }
        ulong dr6 = Context.Width == sizeof(uint) ? BitConverter.ToUInt32(context, Context.Dr6At) : BitConverter.ToUInt64(context, Context.Dr6At);
        for (int slot = 0; slot < 4; slot++)
        {
            if ((dr6 & (1UL << slot)) != 0)
            {
                yield return slot;
            }
        }
    }

    /// <summary>Sets a thread's registers to watch what each slot holds (null: nothing), clearing what Dr6 says of the last
    /// hit, so a step's single-step trap is never taken for a write. A thread that is gone, or will not be set, is said in
    /// the log and left.</summary>
    public static void Apply(ICorDebugProcess process, int threadId, IReadOnlyList<(ulong Address, int Size)?> slots, Action<string>? log)
    {
        byte* context = (byte*)NativeMemory.AlignedAlloc((nuint)Context.Size, 16);
        try
        {
            new Span<byte>(context, Context.Size).Clear();
            *(uint*)(context + Context.FlagsAt) = ContextControl | ContextInteger | ContextDebugRegisters;
            // Answered only for a thread stopped in managed code; one stopped elsewhere has no context of the runtime's.
            if (process.TryGetThreadContext((uint)threadId, (uint)Context.Size, (nint)context) == Cor.S_OK)
            {
                *(uint*)(context + Context.FlagsAt) |= ContextDebugRegisters;
                Write(context, slots);
                int set = process.TrySetThreadContext((uint)threadId, (uint)Context.Size, (nint)context);
                if (set != Cor.S_OK)
                {
                    log?.Invoke($"Data breakpoints: the runtime's context of thread {threadId} took no debug registers: 0x{set:X8}");
                }
            }
            nint thread = Kernel32.OpenThread(ThreadSuspendResume | ThreadGetContext | ThreadSetContext, false, (uint)threadId);
            if (thread == 0)
            {
                log?.Invoke($"Data breakpoints: thread {threadId} could not be opened: error {Marshal.GetLastPInvokeError()}");
                return;
            }
            try
            {
                if (SuspendThread(thread) == uint.MaxValue)
                {
                    log?.Invoke($"Data breakpoints: thread {threadId} could not be suspended: error {Marshal.GetLastPInvokeError()}");
                    return;
                }
                try
                {
                    new Span<byte>(context, Context.Size).Clear();
                    *(uint*)(context + Context.FlagsAt) = ContextDebugRegisters;
                    if (!GetThreadContext(thread, (nint)context))
                    {
                        log?.Invoke($"Data breakpoints: thread {threadId}'s registers could not be read: error {Marshal.GetLastPInvokeError()}");
                        return;
                    }
                    Write(context, slots);
                    if (!SetThreadContext(thread, (nint)context))
                    {
                        log?.Invoke($"Data breakpoints: thread {threadId}'s registers could not be set: error {Marshal.GetLastPInvokeError()}");
                    }
                }
                finally
                {
                    Kernel32.ResumeThread(thread);
                }
            }
            finally
            {
                Kernel32.CloseHandle(thread);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(context);
        }
    }

    private static void Write(byte* context, IReadOnlyList<(ulong Address, int Size)?> slots)
    {
        ulong dr7 = Register(context, Context.Dr7At);
        for (int slot = 0; slot < 4; slot++)
        {
            // Its local and global enables, then its kind and length.
            dr7 &= ~((0b11UL << (slot * 2)) | (0b1111UL << (16 + (slot * 4))));
            (ulong Address, int Size)? watched = slot < slots.Count ? slots[slot] : null;
            SetRegister(context, Context.Dr0At + (slot * Context.Width), watched?.Address ?? 0);
            if (watched is { } on)
            {
                ulong length = on.Size switch
                {
                    1 => 0b00,
                    2 => 0b01,
                    4 => 0b11,
                    8 => 0b10,
                    _ => throw new ArgumentOutOfRangeException(nameof(slots), on.Size, "A debug register watches 1, 2, 4 or 8 bytes"),
                };
                // On for this thread, stopping at a write (01) of this many bytes.
                dr7 |= (1UL << (slot * 2)) | (0b01UL << (16 + (slot * 4))) | (length << (18 + (slot * 4)));
            }
        }
        SetRegister(context, Context.Dr7At, dr7);
        SetRegister(context, Context.Dr6At, 0);
    }

    private static ulong Register(byte* context, int at) => Context.Width == sizeof(uint) ? *(uint*)(context + at) : *(ulong*)(context + at);

    private static void SetRegister(byte* context, int at, ulong value)
    {
        if (Context.Width == sizeof(uint))
        {
            *(uint*)(context + at) = checked((uint)value);
        }
        else
        {
            *(ulong*)(context + at) = value;
        }
    }

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial uint SuspendThread(nint thread);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadContext(nint thread, nint context);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadContext(nint thread, nint context);
}
