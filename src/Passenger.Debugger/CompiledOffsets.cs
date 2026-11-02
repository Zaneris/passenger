using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>The IL offset compiled code is at, as the runtime's stack traces read it from the code's map: the lowest IL
/// offset mapped where the native offset's run of code begins, 0 in the prolog, in code of no IL or before any run, and
/// the method's last IL offset in the epilog.</summary>
internal static class CompiledOffsets
{
    private const int NoMapping = -1;
    private const int Prolog = -2;
    private const int Epilog = -3;

    /// <summary>The IL offset of a native offset in compiled code; null where its map cannot be read.</summary>
    public static int? ILAt(ICorDebugCode code, uint nativeOffset)
    {
        if (code.TryGetILToNativeMapping(0, out uint count, null) is not Cor.S_OK || count == 0)
        {
            return null;
        }
        COR_DEBUG_IL_TO_NATIVE_MAP[] map = new COR_DEBUG_IL_TO_NATIVE_MAP[count];
        return code.TryGetILToNativeMapping(count, out count, map) is Cor.S_OK ? ILAt(map.AsSpan(0, (int)Math.Min(count, (uint)map.Length)), nativeOffset) : null;
    }

    public static int ILAt(ReadOnlySpan<COR_DEBUG_IL_TO_NATIVE_MAP> map, uint nativeOffset)
    {
        uint? start = null;
        int greatest = 0;
        foreach (COR_DEBUG_IL_TO_NATIVE_MAP entry in map)
        {
            greatest = Math.Max(greatest, (int)entry.ilOffset);
            if (entry.nativeStartOffset <= nativeOffset && (start is not { } found || entry.nativeStartOffset > found))
            {
                start = entry.nativeStartOffset;
            }
        }
        if (start is not { } run)
        {
            return 0;
        }
        int? lowest = null;
        foreach (COR_DEBUG_IL_TO_NATIVE_MAP entry in map)
        {
            if (entry.nativeStartOffset == run && (int)entry.ilOffset is var il && il != NoMapping && (lowest is not { } low || il < low))
            {
                lowest = il;
            }
        }
        return lowest switch
        {
            null or Prolog => 0,
            Epilog => greatest,
            < 0 => 0,
            { } il => il,
        };
    }
}
