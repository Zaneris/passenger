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

    /// <summary>Puts the work off until the delay from now, in place of the wait under way.</summary>
    public void Restart()
    {
        waiting?.Cancel();
        CancellationTokenSource cancel = new();
        waiting = cancel;
        UnawaitedWork.Observe(WaitAsync(cancel));
    }

    /// <summary>Drops the wait under way; work already begun runs on, and says itself whether it still lands.</summary>
    public void Stop()
    {
        waiting?.Cancel();
        waiting = null;
    }

    private async Task WaitAsync(CancellationTokenSource cancel)
    {
        using (BackgroundWork.Scope(file, line))
        using (hold?.Invoke())
        {
            try
            {
                await Task.Delay(delay, cancel.Token);
            }
            catch (OperationCanceledException)
            {
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
