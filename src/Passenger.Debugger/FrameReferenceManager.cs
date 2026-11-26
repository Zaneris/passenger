namespace Passenger.Debugger;

/// <summary>The ids of a stop's frames. Each is numbered above every one before it in the session, and a stop's are let go
/// when the program goes on, so a frame from an earlier stop is refused rather than naming the frame now at its depth.</summary>
public partial class FrameReferenceManager
{
    private int _nextFrameId = 1;

    /// <summary>The first frame id of the stop the program is at: any below it was an earlier stop's.</summary>
    private int _firstOfStop = 1;
    private readonly Dictionary<int, (ThreadId threadId, FrameStackDepth frameStackDepth)?> _references = [];
    private readonly Dictionary<(ThreadId threadId, FrameStackDepth frameStackDepth), int?> _referencesByThreadAndFrameStackDepth = [];
    private readonly Lock _lock = new();

    public int GetOrCreateFrameId(ThreadId threadId, FrameStackDepth frameStackDepth)
    {
        lock (_lock)
        {
            int? frameId = _referencesByThreadAndFrameStackDepth.GetValueOrDefault((threadId, frameStackDepth));
            if (frameId is null)
            {
                frameId = _nextFrameId++;
                _references[frameId.Value] = (threadId, frameStackDepth);
                _referencesByThreadAndFrameStackDepth[(threadId, frameStackDepth)] = frameId;
            }
            return frameId.Value;
        }
    }

    public (ThreadId threadId, FrameStackDepth frameStackDepth)? GetFrameInfoById(int frameId)
    {
        lock (_lock)
        {
            return _references.GetValueOrDefault(frameId);
        }
    }

    /// <summary>Why a frame id not held is refused: it was an earlier stop's, or was never given.</summary>
    public string Unknown(int frameId)
    {
        lock (_lock)
        {
            return frameId > 0 && frameId < _firstOfStop
                ? $"Frame {frameId} is from an earlier stop, and the program has run on since"
                : $"Unknown stack frame ID '{frameId}'.";
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _references.Clear();
            _referencesByThreadAndFrameStackDepth.Clear();
            _syntheticAsyncFrameReferences.Clear();
            _syntheticAsyncFrameReferencesByPhysicalFrame.Clear();
            _awaitingFrameReferences.Clear();
            _awaitingFrameIds.Clear();
            _firstOfStop = _nextFrameId;
        }
    }
}
