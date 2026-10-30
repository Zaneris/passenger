using Microsoft.Diagnostics.Tracing;
using Passenger.Inspector;

namespace Passenger.Profiler;

/// <summary>A control an Avalonia app laid out, as Passenger's inspector in it names it: its id (the Live Visual Tree's for
/// the same control), its type without and with its namespace, its name, the control whose template made it, and where its
/// XAML is (the file and line, from one), when the app's build recorded it.</summary>
public sealed record UiControl(long Id, string Type, string FullType, string? Name, string? TemplatedBy, string? File, int Line)
{
    /// <summary>As the profiler lists it: Type#Name, or the type in the template of the control that made it.</summary>
    public string Label => Name is { Length: > 0 } ? $"{Type}#{Name}" : TemplatedBy is { Length: > 0 } by ? $"{Type} in {by}'s template" : Type;
}

/// <summary>What measuring and arranging cost: how many of each, their time with what they measured or arranged in turn, and
/// their own time without, in milliseconds.</summary>
public readonly record struct LayoutWork(int Measures, double MeasureMs, double MeasureSelfMs, int Arranges, double ArrangeMs, double ArrangeSelfMs)
{
    /// <summary>The time measuring and arranging took in the control itself: what tells the heaviest control.</summary>
    public double SelfMs => MeasureSelfMs + ArrangeSelfMs;

    public static LayoutWork operator +(LayoutWork a, LayoutWork b) => new(a.Measures + b.Measures, a.MeasureMs + b.MeasureMs, a.MeasureSelfMs + b.MeasureSelfMs,
        a.Arranges + b.Arranges, a.ArrangeMs + b.ArrangeMs, a.ArrangeSelfMs + b.ArrangeSelfMs);
}

/// <summary>A control's layout in a frame.</summary>
public readonly record struct ControlWork(UiControl Control, LayoutWork Work);

/// <summary>An Avalonia frame: the UI thread's layout and render work in one go, when it began (milliseconds from the trace's
/// start) and how long it took, Avalonia's measure and arrange passes in it, the time measuring and arranging, the time
/// recording what to render, how many measures and arranges, and each control's layout; then the compositor's passes that
/// drew it, after it.</summary>
public sealed record UiFrame(int Number, double StartMs, double DurationMs, int MeasurePasses, int ArrangePasses, double MeasureMs, double ArrangeMs,
    double RecordMs, int Measures, int Arranges, IReadOnlyList<ControlWork> Controls)
{
    /// <summary>The compositor's update and render passes drawing what the frame recorded, in milliseconds.</summary>
    public double CompositeMs { get; init; }

    public double LayoutMs => MeasureMs + ArrangeMs;

    public double EndMs => StartMs + DurationMs;
}

/// <summary>A row of the layout table: a control type, its work in all its controls and those controls under it, or one control.</summary>
public sealed record LayoutCost(string Name, UiControl? Control, LayoutWork Work, IReadOnlyList<LayoutCost> Controls);

/// <summary>An Avalonia app's frames as its inspector tells them through Passenger's EventSource (the inspector's Frames.cs):
/// read from a trace with the profiler's other events, and the layout table worked out of any of them.</summary>
public static class UiFrames
{
    /// <summary>The inspector's EventSource, which a session enables to have the app's frames recorded.</summary>
    public const string Provider = FrameWire.Provider;

    /// <summary>How soon after one window's drawing ends another's begins in the same tick of the render loop.</summary>
    private const double SameTick = 2;

    /// <summary>The frames' median time on the UI thread: of an even count, halfway between the two in the middle.</summary>
    public static double MedianMs(IEnumerable<UiFrame> frames) => Percentile([.. frames.Select(f => f.DurationMs).Order()], 0.5);

    /// <summary>The value a fraction of the way through some sorted values, between the two either side of it in
    /// proportion (linear interpolation), so a small count's percentile is never simply its next value up.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        if (sorted.Count == 0)
        {
            throw new ArgumentException("No values to take a percentile of", nameof(sorted));
        }
        double at = Math.Clamp(fraction, 0, 1) * (sorted.Count - 1);
        int below = (int)Math.Floor(at);
        int above = Math.Min(below + 1, sorted.Count - 1);
        return sorted[below] + ((sorted[above] - sorted[below]) * (at - below));
    }

    /// <summary>The layout of some frames by control type, the heaviest by its own time first, each with its controls, the
    /// heaviest first.</summary>
    public static IReadOnlyList<LayoutCost> ByType(IEnumerable<UiFrame> frames)
    {
        Dictionary<long, (UiControl Control, LayoutWork Work)> controls = [];
        foreach (UiFrame frame in frames)
        {
            foreach (ControlWork laid in frame.Controls)
            {
                controls[laid.Control.Id] = controls.TryGetValue(laid.Control.Id, out (UiControl Control, LayoutWork Work) so) ? (so.Control, so.Work + laid.Work) : (laid.Control, laid.Work);
            }
        }
        return [.. controls.Values
            .GroupBy(c => c.Control.FullType, StringComparer.Ordinal)
            .Select(type =>
            {
                List<LayoutCost> each = [.. type.Select(c => new LayoutCost(c.Control.Label, c.Control, c.Work, [])).OrderByDescending(c => c.Work.SelfMs).ThenBy(c => c.Name, StringComparer.Ordinal)];
                LayoutWork all = each.Aggregate(default(LayoutWork), (sum, c) => sum + c.Work);
                return new LayoutCost(type.First().Control.Type, null, all, each);
            })
            .OrderByDescending(t => t.Work.SelfMs)
            .ThenBy(t => t.Name, StringComparer.Ordinal)];
    }

    /// <summary>Frames in order, each with the compositor's first drawing after it and every drawing that adjoins the one
    /// before (every window's, which a tick of the render loop draws together, one beginning as the last ended) before
    /// the next frame: later drawings with no frame between (an animation, the FPS overlay's counter) are the
    /// compositor's alone. A drawing is when it began and how long it took, in milliseconds.</summary>
    internal static IReadOnlyList<UiFrame> Composited(IEnumerable<UiFrame> frames, IEnumerable<(double At, double Ms)> drawings)
    {
        List<UiFrame> ordered = [.. frames.OrderBy(f => f.StartMs)];
        List<(double At, double Ms)> composited = [.. drawings.OrderBy(d => d.At)];
        int next = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            UiFrame frame = ordered[i];
            double before = i + 1 < ordered.Count ? ordered[i + 1].StartMs : double.MaxValue;
            while (next < composited.Count && composited[next].At < frame.EndMs - 1)
            {
                next++;
            }
            double drawn = 0;
            double? tick = null;
            while (next < composited.Count && composited[next].At < before && (tick is not { } latest || composited[next].At <= latest))
            {
                // The same tick while each drawing begins by the time the latest so far has ended.
                double ended = composited[next].At + composited[next].Ms + SameTick;
                tick = Math.Max(tick ?? ended, ended);
                drawn += composited[next].Ms;
                next++;
            }
            ordered[i] = frame with { CompositeMs = drawn };
        }
        return ordered;
    }

    /// <summary>Gathers the inspector's events from a trace as it is read, in order.</summary>
    internal sealed class Reader
    {
        private readonly Dictionary<long, UiControl> controls = [];
        private readonly Dictionary<int, List<ControlWork>> laidOut = [];
        private readonly List<UiFrame> frames = [];
        private readonly List<(double At, double Ms)> composited = [];

        /// <summary>Takes the event if it is the inspector's.</summary>
        public bool Take(TraceEvent data)
        {
            if (data.ProviderName != Provider)
            {
                return false;
            }
            switch ((int)data.ID)
            {
                case FrameWire.ControlEvent:
                    long id = Long(data, FrameWire.Id);
                    controls[id] = new UiControl(id, Text(data, FrameWire.Type) ?? "?", Text(data, FrameWire.FullType) ?? "?", Text(data, FrameWire.Name), Text(data, FrameWire.TemplatedBy), Text(data, FrameWire.File), Int(data, FrameWire.Line));
                    break;
                case FrameWire.LayoutEvent:
                    int number = Int(data, FrameWire.FrameNumber);
                    long control = Long(data, FrameWire.ControlId);
                    if (!laidOut.TryGetValue(number, out List<ControlWork>? works))
                    {
                        laidOut[number] = works = [];
                    }
                    UiControl named = controls.TryGetValue(control, out UiControl? known) ? known : new UiControl(control, "?", "?", null, null, null, 0);
                    works.Add(new ControlWork(named, new LayoutWork(Int(data, FrameWire.Measures), Double(data, FrameWire.MeasureMs), Double(data, FrameWire.MeasureSelfMs),
                        Int(data, FrameWire.Arranges), Double(data, FrameWire.ArrangeMs), Double(data, FrameWire.ArrangeSelfMs))));
                    break;
                case FrameWire.FrameEvent:
                    int frame = Int(data, FrameWire.FrameNumber);
                    double duration = Double(data, FrameWire.DurationMs);
                    double start = data.TimeStampRelativeMSec - Double(data, FrameWire.EndedMsAgo) - duration;
                    laidOut.Remove(frame, out List<ControlWork>? laid);
                    frames.Add(new UiFrame(frame, start, duration, Int(data, FrameWire.MeasurePasses), Int(data, FrameWire.ArrangePasses), Double(data, FrameWire.MeasureMs),
                        Double(data, FrameWire.ArrangeMs), Double(data, FrameWire.RenderMs), Int(data, FrameWire.Measures), Int(data, FrameWire.Arranges),
                        [.. (laid ?? []).OrderByDescending(c => c.Work.SelfMs)]));
                    break;
                case FrameWire.CompositedEvent:
                    double ms = Double(data, FrameWire.UpdateMs) + Double(data, FrameWire.RenderMs);
                    composited.Add((data.TimeStampRelativeMSec - ms, ms));
                    break;
            }
            return true;
        }

        /// <summary>The frames in order, each with the compositor's drawings after it (<see cref="Composited"/>).</summary>
        public IReadOnlyList<UiFrame> Frames() => Composited(frames, composited);

        private static string? Text(TraceEvent data, string name) => data.PayloadByName(name) as string is { Length: > 0 } text ? text : null;

        private static int Int(TraceEvent data, string name) => Convert.ToInt32(data.PayloadByName(name) ?? 0, System.Globalization.CultureInfo.InvariantCulture);

        private static long Long(TraceEvent data, string name) => Convert.ToInt64(data.PayloadByName(name) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);

        private static double Double(TraceEvent data, string name) => Convert.ToDouble(data.PayloadByName(name) ?? 0.0, System.Globalization.CultureInfo.InvariantCulture);
    }
}
