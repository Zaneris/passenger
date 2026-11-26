namespace Passenger.Debugger;

public partial class FrameReferenceManager
{
    private readonly Dictionary<int, (ThreadId threadId, FrameStackDepth physicalFrameStackDepth, SyntheticAsyncCallerFrame frame)?> _syntheticAsyncFrameReferences = [];
    private readonly Dictionary<(ThreadId threadId, FrameStackDepth physicalFrameStackDepth, int syntheticFrameIndex), int> _syntheticAsyncFrameReferencesByPhysicalFrame = [];

    internal int GetOrCreateSyntheticAsyncFrameId(ThreadId threadId, FrameStackDepth physicalFrameStackDepth, int syntheticFrameIndex, SyntheticAsyncCallerFrame frame)
    {
        lock (_lock)
        {
            (ThreadId threadId, FrameStackDepth physicalFrameStackDepth, int syntheticFrameIndex) key = (threadId, physicalFrameStackDepth, syntheticFrameIndex);
            if (_syntheticAsyncFrameReferencesByPhysicalFrame.TryGetValue(key, out int existing))
            {
                return existing;
            }

            int frameId = _nextFrameId++;
            _syntheticAsyncFrameReferences[frameId] = (threadId, physicalFrameStackDepth, frame);
            _syntheticAsyncFrameReferencesByPhysicalFrame[key] = frameId;
            return frameId;
        }
    }

    internal (ThreadId threadId, FrameStackDepth physicalFrameStackDepth, SyntheticAsyncCallerFrame frame)? GetSyntheticAsyncFrameById(int frameId)
    {
        lock (_lock)
        {
            return _syntheticAsyncFrameReferences.GetValueOrDefault(frameId);
        }
    }

    /// <summary>The frames of the methods awaiting a resumed run (runtime async), by their place in the thread's chain.</summary>
    private readonly Dictionary<int, (ThreadId threadId, int index)> _awaitingFrameReferences = [];
    private readonly Dictionary<(ThreadId threadId, int index), int> _awaitingFrameIds = [];

    /// <summary>The id of the method a thread's resumed run is awaited by at a place in its chain (0 the innermost).</summary>
    internal int GetOrCreateAwaitingFrameId(ThreadId threadId, int index)
    {
        lock (_lock)
        {
            if (_awaitingFrameIds.TryGetValue((threadId, index), out int existing))
            {
                return existing;
            }
            int frameId = _nextFrameId++;
            _awaitingFrameReferences[frameId] = (threadId, index);
            _awaitingFrameIds[(threadId, index)] = frameId;
            return frameId;
        }
    }

    internal (ThreadId threadId, int index)? GetAwaitingFrameById(int frameId)
    {
        lock (_lock)
        {
            return _awaitingFrameReferences.TryGetValue(frameId, out (ThreadId threadId, int index) found) ? found : null;
        }
    }
}
