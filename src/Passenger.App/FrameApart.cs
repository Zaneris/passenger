using Avalonia;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace Passenger.App;

/// <summary>Work whose change draws in a frame of its own: run once the compositor has taken the frame holding what the
/// window changed before it was asked, so what it changes lands in the next. The compositor draws one rectangle a frame
/// around all that changed, so a change far from another drawn in its frame draws again all that lies between them (the
/// status bar's caret words with the caret's row: every row below it). Asked again before it runs, it runs once, as things
/// then stand. A UI-thread object of a window.</summary>
public sealed class FrameApart
{
    private readonly Visual within;
    private readonly Action run;
    private bool asked;

    /// <param name="within">A visual of the window whose frames the work's change keeps apart from.</param>
    public FrameApart(Visual within, Action work)
    {
        this.within = within;
        run = () =>
        {
            asked = false;
            work();
        };
    }

    /// <summary>Runs the work as the frame after the one holding what changed so far is made; at once while the window
    /// draws nothing.</summary>
    public void Ask()
    {
        UiThread.Check();
        if (asked)
        {
            return;
        }
        if (ElementComposition.GetElementVisual(within)?.Compositor is not { } compositor)
        {
            run();
            return;
        }
        asked = true;
        IdeErrors.Observe(compositor.RequestCompositionBatchCommitAsync().Processed.ContinueWith(Taken, run, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
    }

    /// <summary>The frame taken, on the compositor's thread: the work goes to the window's, ahead of all but drawing.</summary>
    private static void Taken(Task frame, object? run) => UiThread.Post((Action)run!, DispatcherPriority.Render);
}
