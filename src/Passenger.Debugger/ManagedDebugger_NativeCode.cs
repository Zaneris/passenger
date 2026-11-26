using Passenger.Core;
using Passenger.Dap;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>One frame's native code, in the runtime's order, with each region's actual address.</summary>
public sealed record FrameNativeCode(byte[] Bytes, CodeChunk[] Chunks, uint? InstructionOffset, IReadOnlyList<DapNativeSource> Sources);

public partial class ManagedDebugger
{
    /// <summary>Copies the current frame's code version while the engine holds the stop.</summary>
    public FrameNativeCode? NativeCodeAt(int frameId)
    {
        RequireStop();
        if (_frameReferenceManager.GetFrameInfoById(frameId) is not { } at
            || GetFrameForThreadIdAndStackDepth(at.threadId, at.frameStackDepth) is not ICorDebugNativeFrame frame
            || frame.TryGetCode(out ICorDebugCode? code) != Cor.S_OK || code is not ICorDebugCode2 regions
            || code.TryIsIL(out bool il) != Cor.S_OK || il
            || code.TryGetSize(out uint size) != Cor.S_OK || size is 0 or > 256 * 1024
            || regions.TryGetCodeChunks(0, out uint count, null) != Cor.S_OK || count is 0 || count > size)
        {
            return null;
        }

        CodeChunk[] chunks = new CodeChunk[count];
        byte[] bytes = new byte[size];
        if (regions.TryGetCodeChunks(count, out uint fetched, chunks) != Cor.S_OK || fetched != count
            || chunks.Sum(chunk => (long)chunk.Length) != size
            || code.TryGetCode(0, size, size, bytes, out uint read) != Cor.S_OK || read != size)
        {
            return null;
        }

        uint? instruction = frame.TryGetIP(out uint offset) == Cor.S_OK ? offset : null;
        return new FrameNativeCode(bytes, chunks, instruction, NativeSources(frame, code, size));
    }

    private IReadOnlyList<DapNativeSource> NativeSources(ICorDebugNativeFrame frame, ICorDebugCode code, uint size)
    {
        if (frame is not ICorDebugILFrame ilFrame || FindModule(ilFrame.Function.Module) is not { } module
            || code.TryGetILToNativeMapping(0, out uint count, null) != Cor.S_OK || count is 0 || count > size)
        {
            return [];
        }
        IReadOnlyList<ModuleMetadataReader.Point> points = module.MetadataReader.Points(ilFrame.Function.Token, ModuleMetadataReader.VersionOf(ilFrame));
        COR_DEBUG_IL_TO_NATIVE_MAP[] map = new COR_DEBUG_IL_TO_NATIVE_MAP[count];
        if (points.Count == 0 || code.TryGetILToNativeMapping(count, out uint fetched, map) != Cor.S_OK || fetched > count)
        {
            return [];
        }

        List<DapNativeSource> sources = [];
        foreach (COR_DEBUG_IL_TO_NATIVE_MAP entry in map.AsSpan(0, (int)fetched))
        {
            if ((int)entry.ilOffset < 0 || entry.nativeStartOffset >= entry.nativeEndOffset || entry.nativeEndOffset > size)
            {
                continue;
            }
            int index = Sorted.FirstAfter(points, static point => point.Offset, (int)entry.ilOffset) - 1;
            if (index >= 0 && points[index] is { IsHidden: false, Rewritten: false, Document: { } path } point)
            {
                sources.Add(new DapNativeSource(entry.nativeStartOffset, entry.nativeEndOffset, path,
                    point.StartLine, point.StartColumn, point.EndLine, point.EndColumn));
            }
        }
        return sources;
    }
}
