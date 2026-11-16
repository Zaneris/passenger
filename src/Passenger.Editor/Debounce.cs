using System.Runtime.CompilerServices;
using Passenger.Core;

namespace Passenger.Editor;

/// <summary>Work run once what keeps putting it off (typing) pauses for long enough. The wait is background work, so a
/// screenshot or a test settles on what the pause brings, which a DispatcherTimer's wait would not hold them for.
/// <paramref name="hold"/>, when given, is held as long as the background work is. A UI-thread object; where background
/// work is named, its wait is named by the place that made it.</summary>
public sealed class Debounce(TimeSpan delay, Func<Task> work, Func<IDisposable>? hold = null,
    [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
{
    private CancellationTokenSource? waiting;

    /// <summary>How many holds keep the pause open (<see cref="HoldOpen"/>).</summary>
    private int heldOpen;

    /// <summary>Whether a wait ended while the pause was held open, its work put off until let go.</summary>
    private bool endedHeld;

    /// <summary>Whether the work waits for a pause to begin.</summary>
    public bool IsWaiting => waiting is not null;

    /// <summary>Puts the work off until the delay from now, in place of the wait under way.</summary>
    public void Restart()
    {
        endedHeld = false;
        waiting?.Cancel();
        CancellationTokenSource cancel = new();
        waiting = cancel;
        UnawaitedWork.Observe(WaitAsync(cancel));
    }

    /// <summary>Drops the wait under way; work already begun runs on, and says itself whether it still lands.</summary>
    public void Stop()
    {
        endedHeld = false;
        waiting?.Cancel();
        waiting = null;
    }

    /// <summary>The pause held open until let go, for a test of what comes within it however slow its machine: a wait ending
    /// meanwhile runs nothing, and once the last hold is let go its work waits the whole delay again.</summary>
    public IDisposable HoldOpen()
    {
        heldOpen++;
        return new Held(this);
    }

    private sealed class Held(Debounce debounce) : IDisposable
    {
        private bool done;

        public void Dispose()
        {
            if (!done)
            {
                done = true;
                debounce.LetGo();
            }
        }
    }

    private void LetGo()
    {
        if (--heldOpen == 0 && endedHeld)
        {
            Restart();
        }
    }

    private async Task WaitAsync(CancellationTokenSource cancel)
    {
        using (BackgroundWork.Scope(file, line))
        using (hold?.Invoke())
        {
            // Put off again, the wait ends quietly: an exception thrown at every key costs its thread more than the wait.
            await Task.Delay(delay, cancel.Token).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            if (heldOpen > 0)
            {
                endedHeld = true;
                return;
            }
            if (waiting == cancel)
            {
                waiting = null;
            }
            await work();
        }
    }
}
