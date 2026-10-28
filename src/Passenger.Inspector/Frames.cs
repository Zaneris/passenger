using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Passenger.Inspector;

/// <summary>The app's frames, told by Passenger's own EventSource, whose events Passenger.Profiler's UiFrames reads by
/// their numbers and their payloads' names (<see cref="FrameWire"/>, each parameter's name here). A session enabling it turns the recording on and its end turns it off
/// (<see cref="FrameRecorder.Follow"/>), so an app nobody profiles pays nothing. A control is told once a recording,
/// before its first layout.</summary>
[EventSource(Name = FrameWire.Provider)]
internal sealed class FrameEvents : EventSource
{
    public static FrameEvents? Log { get; private set; }

    /// <summary>Made once, by the startup hook, before the app's Main.</summary>
    public static void Make() => Log ??= new FrameEvents();

    protected override void OnEventCommand(EventCommandEventArgs command) => FrameRecorder.Follow(IsEnabled());

    /// <summary>A control laid out: the inspector's id for it (the Live Visual Tree's), its type without and with its namespace,
    /// its name, the control whose template made it, and where its XAML is (an empty file and line 0 when unknown).</summary>
    [Event(FrameWire.ControlEvent, Level = EventLevel.Informational)]
    public unsafe void Control(long id, string type, string fullType, string name, string templatedBy, string file, int line)
    {
        fixed (char* typed = type, full = fullType, named = name, by = templatedBy, at = file)
        {
            EventData* data = stackalloc EventData[7];
            data[0] = Data(&id, sizeof(long));
            data[1] = Text(typed, type);
            data[2] = Text(full, fullType);
            data[3] = Text(named, name);
            data[4] = Text(by, templatedBy);
            data[5] = Text(at, file);
            data[6] = Data(&line, sizeof(int));
            WriteEventCore(FrameWire.ControlEvent, 7, data);
        }
    }

    /// <summary>A control's measures and arranges in a frame: how many, their time, and their own time (less the controls they
    /// measured or arranged in turn), in milliseconds.</summary>
    [Event(FrameWire.LayoutEvent, Level = EventLevel.Informational)]
    public unsafe void Layout(int frame, long control, int measures, double measureMs, double measureSelfMs, int arranges, double arrangeMs, double arrangeSelfMs)
    {
        EventData* data = stackalloc EventData[8];
        data[0] = Data(&frame, sizeof(int));
        data[1] = Data(&control, sizeof(long));
        data[2] = Data(&measures, sizeof(int));
        data[3] = Data(&measureMs, sizeof(double));
        data[4] = Data(&measureSelfMs, sizeof(double));
        data[5] = Data(&arranges, sizeof(int));
        data[6] = Data(&arrangeMs, sizeof(double));
        data[7] = Data(&arrangeSelfMs, sizeof(double));
        WriteEventCore(FrameWire.LayoutEvent, 8, data);
    }

    /// <summary>A frame's work on the UI thread, told as it ends: from its first layout or render to its last, how long before
    /// this event that last ended, Avalonia's measure and arrange passes, the time measuring and arranging (the outermost
    /// measures and arranges), the time recording what to render, and how many measures and arranges it did.</summary>
    [Event(FrameWire.FrameEvent, Level = EventLevel.Informational)]
    public unsafe void Frame(int frame, double durationMs, double endedMsAgo, int measurePasses, int arrangePasses, double measureMs, double arrangeMs,
        double renderMs, int measures, int arranges)
    {
        EventData* data = stackalloc EventData[10];
        data[0] = Data(&frame, sizeof(int));
        data[1] = Data(&durationMs, sizeof(double));
        data[2] = Data(&endedMsAgo, sizeof(double));
        data[3] = Data(&measurePasses, sizeof(int));
        data[4] = Data(&arrangePasses, sizeof(int));
        data[5] = Data(&measureMs, sizeof(double));
        data[6] = Data(&arrangeMs, sizeof(double));
        data[7] = Data(&renderMs, sizeof(double));
        data[8] = Data(&measures, sizeof(int));
        data[9] = Data(&arranges, sizeof(int));
        WriteEventCore(FrameWire.FrameEvent, 10, data);
    }

    /// <summary>The compositor drew a window: its update pass and its render pass, in milliseconds, told as the render ends.</summary>
    [Event(FrameWire.CompositedEvent, Level = EventLevel.Informational)]
    public unsafe void Composited(double updateMs, double renderMs)
    {
        EventData* data = stackalloc EventData[2];
        data[0] = Data(&updateMs, sizeof(double));
        data[1] = Data(&renderMs, sizeof(double));
        WriteEventCore(FrameWire.CompositedEvent, 2, data);
    }

    private static unsafe EventData Data(void* at, int size) => new() { DataPointer = (IntPtr)at, Size = size };

    private static unsafe EventData Text(char* at, string text) => new() { DataPointer = (IntPtr)at, Size = (text.Length + 1) * sizeof(char) };
}

/// <summary>Records the app's frames while a session has <see cref="FrameEvents"/> on, from Avalonia's own diagnostics, which
/// the startup hook turned on (its Avalonia.Diagnostics.Diagnostic.IsEnabled switch, read once as Avalonia starts): the
/// Avalonia.Diagnostic.Source activities around each control's Measure and Arrange, tagged with the control, give each
/// control's count and time, its own time being what its children's do not take; the Avalonia.Diagnostic.Meter histograms
/// give the measure and arrange passes, the time recording what to render, and the compositor's passes. A frame is the UI
/// thread's layout and render work in one go: it opens at the first and is told once the dispatcher has finished what
/// it was running then. Nothing listens to Avalonia while no session has the events on.</summary>
internal static class FrameRecorder
{
    private const string Activities = "Avalonia.Diagnostic.Source";
    private const string Meter = "Avalonia.Diagnostic.Meter";
    private const string Measuring = "Avalonia.MeasuringLayoutable";
    private const string Arranging = "Avalonia.ArrangingLayoutable";

    private static readonly object Gate = new();
    private static ActivityListener? listener;
    private static MeterListener? meters;

    /// <summary>Bumped by each start and each end, so what the UI thread kept of a recording before is let go there: a measure
    /// left open by an end mid-layout would otherwise hold its frame, re-posting its close, for good.</summary>
    private static int recording;

    // The app's UI thread's: the recording these are of, the measures and arranges under way, the frame open, the frames told
    // and the controls described.
    private static int seen;
    private static readonly List<Open> Stack = [];
    private static Frame? frame;
    private static int frames;
    private static readonly HashSet<long> Described = [];

    /// <summary>The compositor's last update pass on this thread, until the render pass after it.</summary>
    [ThreadStatic]
    private static double updated;

    /// <summary>From the startup hook, before the app's Main: the events made, recording at once if a session has them on.</summary>
    public static void Start() => FrameEvents.Make();

    /// <summary>A session turned the events on or off: Avalonia's activities and meters listened to, or no longer.</summary>
    public static void Follow(bool on)
    {
        lock (Gate)
        {
            if (on == listener is not null)
            {
                return;
            }
            Interlocked.Increment(ref recording);
            if (!on)
            {
                listener!.Dispose();
                meters!.Dispose();
                listener = null;
                meters = null;
                return;
            }
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == Activities,
                // Only layout's: the source's style, resource, hit test and routed event activities are never made.
                Sample = (ref ActivityCreationOptions<ActivityContext> asked) => asked.Name is Measuring or Arranging ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
                ActivityStarted = Started,
                ActivityStopped = Stopped,
            };
            ActivitySource.AddActivityListener(listener);
            meters = new MeterListener
            {
                InstrumentPublished = (instrument, to) =>
                {
                    if (instrument.Meter.Name == Meter && instrument is Histogram<double>)
                    {
                        to.EnableMeasurementEvents(instrument);
                    }
                },
            };
            meters.SetMeasurementEventCallback<double>(Measured);
            meters.Start();
        }
    }

    private static bool OnUiThread() => Dispatcher.UIThread.CheckAccess();

    /// <summary>What the UI thread kept of a recording before this one let go.</summary>
    private static void Sync()
    {
        int now = Volatile.Read(ref recording);
        if (seen != now)
        {
            seen = now;
            Stack.Clear();
            frame = null;
            frames = 0;
            Described.Clear();
        }
    }

    private static void Started(Activity activity)
    {
        // Another listener of the app's may have the source's other activities made, which come here too.
        if (activity.OperationName is not (Measuring or Arranging) || !OnUiThread())
        {
            return;
        }
        Sync();
        long now = Stopwatch.GetTimestamp();
        Opened(now);
        Stack.Add(new Open(activity, now, 0, activity.OperationName == Measuring));
    }

    private static void Stopped(Activity activity)
    {
        if (activity.OperationName is not (Measuring or Arranging) || !OnUiThread())
        {
            return;
        }
        Sync();
        int at = Stack.Count - 1;
        while (at >= 0 && Stack[at].Activity != activity)
        {
            at--;
        }
        if (at < 0)
        {
            return;
        }
        long now = Stopwatch.GetTimestamp();
        Open done = Stack[at];
        Stack.RemoveRange(at, Stack.Count - at);
        long total = now - done.Started;
        long own = Math.Max(0, total - done.Children);
        if (at > 0)
        {
            Stack[at - 1] = Stack[at - 1] with { Children = Stack[at - 1].Children + total };
        }
        Frame open = Opened(done.Started);
        open.Ended = Math.Max(open.Ended, now);
        if (at == 0)
        {
            if (done.Measuring)
            {
                open.Measuring += total;
            }
            else
            {
                open.Arranging += total;
            }
        }
        if (activity.GetTagItem("Control") is not Layoutable control)
        {
            return;
        }
        if (!open.Controls.TryGetValue(control, out Work? work))
        {
            open.Controls[control] = work = new Work();
        }
        if (done.Measuring)
        {
            open.Measures++;
            work.Measures++;
            work.Measure += total;
            work.MeasureOwn += own;
        }
        else
        {
            open.Arranges++;
            work.Arranges++;
            work.Arrange += total;
            work.ArrangeOwn += own;
        }
    }

    private static void Measured(Instrument instrument, double ms, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        switch (instrument.Name)
        {
            case "avalonia.comp.update.time":
                updated = ms;
                return;
            case "avalonia.comp.render.time":
                FrameEvents.Log?.Composited(updated, ms);
                updated = 0;
                return;
            case "avalonia.ui.measure.time" or "avalonia.ui.arrange.time" or "avalonia.ui.render.time" when OnUiThread():
                Sync();
                long now = Stopwatch.GetTimestamp();
                Frame open = Opened(now - (long)(ms * Stopwatch.Frequency / 1000));
                open.Ended = Math.Max(open.Ended, now);
                if (instrument.Name == "avalonia.ui.measure.time")
                {
                    open.MeasurePasses++;
                }
                else if (instrument.Name == "avalonia.ui.arrange.time")
                {
                    open.ArrangePasses++;
                }
                else
                {
                    open.RenderMs += ms;
                }
                return;
        }
    }

    /// <summary>The frame open, opened if none is, beginning no later than <paramref name="from"/>.</summary>
    private static Frame Opened(long from)
    {
        if (frame is { } open)
        {
            open.Started = Math.Min(open.Started, from);
            return open;
        }
        frame = new Frame(++frames, from);
        // Run once the dispatcher has finished what it runs now, the frame's render job, whatever its priority.
        Dispatcher.UIThread.Post(Close, DispatcherPriority.Send);
        return frame;
    }

    /// <summary>The frame told: each control's layout (described first if new), then the frame itself.</summary>
    private static void Close()
    {
        Sync();
        if (frame is not { } done)
        {
            return;
        }
        if (Stack.Count > 0)
        {
            // A dispatcher frame pushed inside a measure: the layout under way is still this frame's.
            Dispatcher.UIThread.Post(Close, DispatcherPriority.Send);
            return;
        }
        frame = null;
        if (FrameEvents.Log is not { } log || !log.IsEnabled())
        {
            return;
        }
        foreach ((Layoutable control, Work work) in done.Controls)
        {
            long id = ElementIds<Visual>.Of(control);
            if (Described.Add(id))
            {
                Type type = control.GetType();
                SourcePlace? place = Details.Place(control);
                log.Control(id, Words.TypeName(type), type.FullName ?? type.Name, (control as StyledElement)?.Name ?? string.Empty,
                    control is StyledElement { TemplatedParent: Visual parent } ? Shown.Called(parent) : string.Empty, place?.File ?? string.Empty, place?.Line ?? 0);
            }
            log.Layout(done.Number, id, work.Measures, Ms(work.Measure), Ms(work.MeasureOwn), work.Arranges, Ms(work.Arrange), Ms(work.ArrangeOwn));
        }
        log.Frame(done.Number, Ms(done.Ended - done.Started), Ms(Stopwatch.GetTimestamp() - done.Ended), done.MeasurePasses, done.ArrangePasses,
            Ms(done.Measuring), Ms(done.Arranging), done.RenderMs, done.Measures, done.Arranges);
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>A measure or arrange under way: its activity, when it began, and the time of those it made in turn.</summary>
    private readonly record struct Open(Activity Activity, long Started, long Children, bool Measuring);

    /// <summary>A frame open on the UI thread, in Stopwatch ticks but for the passes' milliseconds.</summary>
    private sealed class Frame(int number, long started)
    {
        public int Number { get; } = number;
        public long Started { get; set; } = started;
        public long Ended { get; set; } = started;
        public int MeasurePasses { get; set; }
        public int ArrangePasses { get; set; }
        public long Measuring { get; set; }
        public long Arranging { get; set; }
        public double RenderMs { get; set; }
        public int Measures { get; set; }
        public int Arranges { get; set; }
        public Dictionary<Layoutable, Work> Controls { get; } = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>A control's measures and arranges in a frame, in Stopwatch ticks.</summary>
    private sealed class Work
    {
        public int Measures { get; set; }
        public long Measure { get; set; }
        public long MeasureOwn { get; set; }
        public int Arranges { get; set; }
        public long Arrange { get; set; }
        public long ArrangeOwn { get; set; }
    }
}
