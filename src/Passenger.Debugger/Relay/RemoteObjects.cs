using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger.Relay;

/// <summary>ICorDebug in an app on a device, as the engine's own interfaces: each object the relay hands out is a native
/// stand-in here, a vtable of <see cref="RemoteThunks"/>, so the engine's generated COM calls reach
/// <see cref="Dispatch"/>, which hands them to the stand-in's <see cref="RelaySession"/>. A stand-in's AddRef and Release
/// are the engine's references to the app's object, so the app's object is let go once the engine lets go of it.</summary>
internal static unsafe class RemoteObjects
{
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_NOTIMPL = unchecked((int)0x80004001);

    private static readonly ConcurrentDictionary<nint, StandIn> s_standIns = new();
    private static readonly ConcurrentDictionary<InterfacePlan, nint> s_vtables = new();

    /// <summary>The app's ICorDebug as the engine holds one, its callbacks handed on as they come.</summary>
    public static ICorDebug Start(RelayConnection relay, DeviceModules modules, Action<string>? log)
    {
        RelaySession session = new(relay, modules, log);
        nint corDebug = session.Take(new RemotePointer(relay.CorDebug, 0), RemoteInterfaces.Of(typeof(ICorDebug)));
        session.HandOnCallbacks();
        return ComInterfaceMarshaller<ICorDebug>.ConvertToManaged((void*)corDebug)!;
    }

    /// <summary>A new stand-in's block: its vtable, then nothing of the host's (the table finds its stand-in).</summary>
    internal static nint MakeBlock(StandIn standIn)
    {
        nint* block = (nint*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(nint)));
        block[0] = s_vtables.GetOrAdd(standIn.Plan, VtableOf);
        s_standIns[(nint)block] = standIn;
        return (nint)block;
    }

    internal static void FreeBlock(nint block)
    {
        if (s_standIns.TryRemove(block, out _))
        {
            NativeMemory.Free((void*)block);
        }
    }

    internal static StandIn? Of(nint block) => block != 0 && s_standIns.TryGetValue(block, out StandIn? standIn) ? standIn : null;

    private static nint VtableOf(InterfacePlan plan)
    {
        int slots = 3 + plan.Methods.Length;
        nint* vtable = (nint*)NativeMemory.Alloc((nuint)(slots * sizeof(nint)));
        vtable[0] = (nint)(delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)&QueryInterface;
        vtable[1] = (nint)(delegate* unmanaged[Cdecl]<nint, uint>)&AddRef;
        vtable[2] = (nint)(delegate* unmanaged[Cdecl]<nint, uint>)&Release;
        for (int slot = 3; slot < slots; slot++)
        {
            vtable[slot] = RemoteThunks.At(slot);
        }
        return (nint)vtable;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QueryInterface(nint self, Guid* iid, nint* result)
    {
        *result = 0;
        return Of(self) is { } standIn ? standIn.Session.QueryInterface(standIn, *iid, result) : E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint AddRef(nint self) => Of(self) is { } standIn ? standIn.Session.AddRef(standIn) : 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Release(nint self) => Of(self) is { } standIn ? standIn.Session.Release(standIn) : 1;

    /// <summary>A call the engine made on a stand-in, by its vtable slot.</summary>
    public static int Dispatch(nint self, int slot, nint* args) =>
        Of(self) is { } standIn ? standIn.Session.Dispatch(standIn, slot, args) : E_NOTIMPL;
}

/// <summary>One object in the app, however many of its interfaces the engine holds: its identity (its IUnknown in the
/// app), the engine's references across its stand-ins, and what the stand-ins may answer without asking again.</summary>
internal sealed class RemoteObject(ulong identity)
{
    public ulong Identity { get; } = identity;

    /// <summary>Held for the session: the app's debugger keeps the object as long as its module, so letting it go
    /// would free nothing there and lose its answers here.</summary>
    public bool Kept { get; set; }

    public int References { get; set; }

    public Dictionary<InterfacePlan, StandIn> StandIns { get; } = [];

    /// <summary>The interfaces the app said the object lacks.</summary>
    public HashSet<InterfacePlan> Lacks { get; } = [];

    public Dictionary<AnswerKey, RememberedAnswer> Answers { get; } = [];

    public EnumBuffer<nint>? Enumeration { get; set; }

    /// <summary>Held while an enumerator's elements are read and handed out, one caller at a time.</summary>
    public Lock Gate { get; } = new();
}

/// <summary>A stand-in for one interface of an object in the app: its native block, and the app's pointer for that
/// interface, which holds one reference in the app (none for an IUnknown made here).</summary>
internal sealed class StandIn(RelaySession session, RemoteObject owner, InterfacePlan plan, ulong pointer)
{
    public RelaySession Session { get; } = session;

    public RemoteObject Owner { get; } = owner;

    public InterfacePlan Plan { get; } = plan;

    public ulong Pointer { get; set; } = pointer;

    public nint Block { get; set; }
}

/// <summary>A call's inputs, by which its answer is remembered: the method, by the interface that declares it, and up to
/// three numbers.</summary>
internal readonly record struct AnswerKey(Type Declaring, string Method, ulong First, ulong Second, ulong Third);

/// <summary>An answer kept: the result, the bytes of each value written, the stand-ins handed back (each holding a
/// reference of the answer's own while it is kept), and the stop it was given in (-1 for one that lasts).</summary>
internal sealed record RememberedAnswer(int Hr, byte[][] Outs, nint[][] Objects, int Stop);
