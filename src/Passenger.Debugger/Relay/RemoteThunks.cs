using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Passenger.Debugger.Relay;

/// <summary>One native entry per vtable slot for the relay's stand-in objects: each passes its slot and its arguments, read
/// as twelve pointer-sized values (the calling conventions pass a COM method's integers and pointers so), to the one
/// dispatcher, which reads them by the interface's plan.</summary>
internal static unsafe class RemoteThunks
{
    public const int Count = 100;

    public static nint At(int slot) => slot switch
    {
        3 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S3,
        4 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S4,
        5 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S5,
        6 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S6,
        7 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S7,
        8 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S8,
        9 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S9,
        10 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S10,
        11 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S11,
        12 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S12,
        13 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S13,
        14 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S14,
        15 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S15,
        16 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S16,
        17 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S17,
        18 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S18,
        19 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S19,
        20 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S20,
        21 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S21,
        22 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S22,
        23 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S23,
        24 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S24,
        25 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S25,
        26 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S26,
        27 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S27,
        28 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S28,
        29 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S29,
        30 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S30,
        31 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S31,
        32 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S32,
        33 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S33,
        34 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S34,
        35 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S35,
        36 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S36,
        37 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S37,
        38 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S38,
        39 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S39,
        40 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S40,
        41 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S41,
        42 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S42,
        43 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S43,
        44 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S44,
        45 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S45,
        46 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S46,
        47 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S47,
        48 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S48,
        49 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S49,
        50 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S50,
        51 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S51,
        52 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S52,
        53 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S53,
        54 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S54,
        55 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S55,
        56 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S56,
        57 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S57,
        58 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S58,
        59 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S59,
        60 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S60,
        61 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S61,
        62 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S62,
        63 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S63,
        64 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S64,
        65 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S65,
        66 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S66,
        67 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S67,
        68 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S68,
        69 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S69,
        70 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S70,
        71 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S71,
        72 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S72,
        73 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S73,
        74 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S74,
        75 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S75,
        76 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S76,
        77 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S77,
        78 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S78,
        79 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S79,
        80 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S80,
        81 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S81,
        82 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S82,
        83 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S83,
        84 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S84,
        85 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S85,
        86 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S86,
        87 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S87,
        88 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S88,
        89 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S89,
        90 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S90,
        91 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S91,
        92 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S92,
        93 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S93,
        94 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S94,
        95 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S95,
        96 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S96,
        97 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S97,
        98 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S98,
        99 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, int>)&S99,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S3(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 3, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S4(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 4, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S5(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 5, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S6(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 6, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S7(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 7, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S8(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 8, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S9(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 9, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S10(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 10, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S11(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 11, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S12(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 12, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S13(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 13, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S14(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 14, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S15(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 15, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S16(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 16, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S17(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 17, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S18(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 18, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S19(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 19, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S20(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 20, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S21(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 21, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S22(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 22, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S23(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 23, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S24(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 24, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S25(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 25, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S26(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 26, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S27(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 27, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S28(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 28, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S29(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 29, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S30(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 30, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S31(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 31, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S32(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 32, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S33(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 33, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S34(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 34, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S35(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 35, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S36(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 36, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S37(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 37, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S38(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 38, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S39(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 39, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S40(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 40, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S41(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 41, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S42(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 42, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S43(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 43, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S44(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 44, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S45(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 45, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S46(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 46, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S47(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 47, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S48(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 48, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S49(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 49, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S50(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 50, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S51(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 51, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S52(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 52, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S53(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 53, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S54(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 54, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S55(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 55, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S56(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 56, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S57(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 57, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S58(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 58, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S59(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 59, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S60(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 60, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S61(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 61, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S62(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 62, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S63(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 63, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S64(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 64, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S65(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 65, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S66(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 66, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S67(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 67, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S68(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 68, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S69(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 69, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S70(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 70, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S71(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 71, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S72(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 72, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S73(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 73, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S74(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 74, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S75(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 75, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S76(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 76, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S77(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 77, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S78(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 78, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S79(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 79, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S80(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 80, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S81(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 81, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S82(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 82, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S83(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 83, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S84(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 84, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S85(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 85, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S86(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 86, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S87(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 87, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S88(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 88, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S89(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 89, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S90(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 90, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S91(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 91, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S92(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 92, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S93(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 93, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S94(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 94, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S95(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 95, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S96(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 96, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S97(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 97, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S98(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 98, a);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int S99(nint self, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9, nint a10, nint a11)
    {
        nint* a = stackalloc nint[12] { a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11 };
        return RemoteObjects.Dispatch(self, 99, a);
    }
}
