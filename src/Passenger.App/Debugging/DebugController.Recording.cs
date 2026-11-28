using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Dap;

namespace Passenger.App.Debugging;

/// <summary>Step back debugging: while Record Calls is on, each program debugged records the calls of its own code, from its
/// start or from then on; at a stop, Step Back and Step Forward go through the moments recorded on the stopped thread,
/// each shown as a past stop as a snapshot is, the live stop one step past the newest.</summary>
public sealed partial class DebugController
{
    /// <summary>Whether the programs debugged record their calls: the window's choice, off as Passenger starts.</summary>
    public bool RecordsCalls { get; private set; }

    /// <summary>The stopped thread's timeline as last read, for the session and stop it was read at.</summary>
    private (DebugSession Session, int Stop, TimelineResponse Timeline)? timeline;

    /// <summary>The recorded moment shown as a past stop, by its number; null while the live stop or a snapshot is shown.</summary>
    public DapMoment? MomentShown { get; private set; }

    /// <summary>The past stop the frames and values show: a snapshot or a recorded moment; null for none.</summary>
    public TakenSnapshot? ShownPast => view.ShownSnapshot;

    /// <summary>What a timeline keeps, let go and cost, as the Timeline tab says it.</summary>
    public string TimelineWords(TimelineResponse read) => DebugModel.Said(read);

    /// <summary>Turns recording on or off for every program debugged now and those started after.</summary>
    public void SetRecording(bool on)
    {
        UiThread.Check();
        RecordsCalls = on;
        view.ShowRecording(on);
        foreach (DebugTab tab in view.Sessions)
        {
            if (tab.Session is { Engine: DebugEngine.Managed, State: not DebugState.Ended } session)
            {
                Send(session.RecordAsync(on));
            }
        }
        Message?.Invoke(this, on ? "Recording the calls of the programs debugged: Step Back goes back through them at a stop"
            : "Calls are no longer recorded; what was recorded stays");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Says why a step back or forward went nowhere.</summary>
    private async Task SaidAsync(Task<string?> stepping)
    {
        if (await stepping is { } why)
        {
            Message?.Invoke(this, why);
        }
    }

    /// <summary>A session as it begins records where Record Calls is on.</summary>
    private void RecordFromStart(DebugSession session)
    {
        if (RecordsCalls && session.Engine == DebugEngine.Managed)
        {
            Send(session.RecordAsync(true));
        }
    }

    /// <summary>The stopped thread's timeline, read once a stop.</summary>
    public async Task<TimelineResponse> TimelineAsync(DebugSession session)
    {
        UiThread.Check();
        if (timeline is { } read && read.Session == session && read.Stop == session.Stops)
        {
            return read.Timeline;
        }
        int stop = session.Stops;
        TimelineResponse now = await session.TimelineAsync(session.StoppedThread);
        timeline = (session, stop, now);
        if (session == Session)
        {
            view.ShowTimeline(now, MomentShown);
        }
        return now;
    }

    /// <summary>Why Step Back goes nowhere now; null where it goes.</summary>
    private Words? WhyNotStepBack() => NotPaused() ?? (Session!.Engine != DebugEngine.Managed ? Refusals.RecordsDotnet
        : timeline is { } read && read.Session == Session && read.Stop == Session.Stops && read.Timeline.Moments.Count == 0 && !RecordsCalls ? Refusals.NothingRecorded
        : null);

    /// <summary>Shows the moment recorded before the one shown (before the live stop, the newest); refused, says why.</summary>
    public async Task<string?> StepBackAsync()
    {
        UiThread.Check();
        if (WhyNotStepBack() is { } why)
        {
            return why.Shown;
        }
        DebugSession session = Session!;
        IReadOnlyList<DapMoment> moments = (await TimelineAsync(session)).Moments;
        int at = MomentShown is { } shownNow ? IndexOf(moments, shownNow) - 1 : moments.Count - 1;
        if (at < 0)
        {
            return (moments.Count == 0 ? Refusals.NothingRecorded : Refusals.EarliestMoment).Shown;
        }
        await ShowMomentAsync(session, moments[at]);
        return null;
    }

    /// <summary>Shows the moment recorded after the one shown, or the live stop after the newest; refused, says why.</summary>
    public async Task<string?> StepForwardAsync()
    {
        UiThread.Check();
        if (NotPaused() is { } why)
        {
            return why.Shown;
        }
        if (MomentShown is not { } shownNow)
        {
            return Refusals.LiveStop.Shown;
        }
        DebugSession session = Session!;
        IReadOnlyList<DapMoment> moments = (await TimelineAsync(session)).Moments;
        int at = IndexOf(moments, shownNow) + 1;
        if (at >= moments.Count)
        {
            await BackToLiveAsync();
            return null;
        }
        await ShowMomentAsync(session, moments[at]);
        return null;
    }

    private static int IndexOf(IReadOnlyList<DapMoment> moments, DapMoment moment)
    {
        for (int i = 0; i < moments.Count; i++)
        {
            if (moments[i].Index == moment.Index)
            {
                return i;
            }
        }
        return moments.Count;
    }

    /// <summary>A recorded moment shown as a past stop: its frames, its values as they were, its place gone to.</summary>
    public async Task ShowMomentAsync(DebugSession session, DapMoment moment)
    {
        UiThread.Check();
        SnapshotEvent read = await session.MomentAsync(moment.Index);
        if (session != Session || session.State != DebugState.Paused)
        {
            return;
        }
        string place = moment.Place.Source?.Path is { } path ? $"{Path.GetFileName(path)}:{moment.Place.Line}" : moment.Place.Name;
        ShowSnapshot(new TakenSnapshot(read, place, TabOf(session)?.Name ?? session.Program, moment.Index, moment));
        MomentShown = moment;
        view.ShowTimeline(null, moment);
        Message?.Invoke(this, view.ShownSnapshot?.Said ?? string.Empty);
    }

    /// <summary>The live stop shown again in place of a recorded moment.</summary>
    public async Task BackToLiveAsync()
    {
        UiThread.Check();
        if (Session is { State: DebugState.Paused } session)
        {
            await DescribeStop(session);
        }
    }

    /// <summary>A stop shown live: no recorded moment is.</summary>
    private void LeaveMoment()
    {
        MomentShown = null;
        view.ShowTimeline(null, null);
    }
}
