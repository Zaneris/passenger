using Passenger.App.Debugging;
using Passenger.Core;

namespace Passenger.App.Run.Devices;

/// <summary>What one start of an app on a device puts there, held from before its first step until all of it is out again
/// (<see cref="Out"/>): the start puts its relay or inspector in (<see cref="PutRelayAsync"/>, <see cref="PutInspectorAsync"/>),
/// then hands it to its run's or session's end (<see cref="EndsWith"/>) or takes it out itself (<see cref="TakeOutAsync"/>).
/// Each step on the device runs off the window's thread, so the folder's end takes it all out (<see cref="EndNowAsync"/>)
/// once the window's loop has stopped too. Stop waits for a start until it has settled (<see cref="Settled"/>).</summary>
public sealed class DeviceStart(string name, AndroidApp app) : IDisposable
{
    private readonly Lock gate = new();

    /// <summary>Canceled by the folder's end, so a put under way stops there and takes out what it had put in.</summary>
    private readonly CancellationTokenSource ending = new();

    private readonly TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource taken = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The last put under way or done, off the window's thread: what it answers is what is in the app.</summary>
    private Task<(DevicePut? Put, RelaySession? Relay, string? Problem)>? putting;

    /// <summary>The take-out under way or done, off the window's thread, which every end of the start shares.</summary>
    private Task? takingOut;

    /// <summary>Whether the app was started, what the start put in handed to its run's or session's end.</summary>
    private bool handed;

    /// <summary>The program whose app it starts, as Run and Debug name it.</summary>
    public string Name { get; } = name;

    public AndroidApp App { get; } = app;

    /// <summary>Done once the start's own part is over: what it put in taken out again, or handed to its run's or session's end.</summary>
    public Task Settled => settled.Task;

    /// <summary>Done once all the start put in is out again and its app ended there, however its start or its end went.</summary>
    public Task Out => taken.Task;

    /// <summary>Passenger's relay put in the installed Debug app as <see cref="DeviceLaunches"/> puts it (the inspector and
    /// the update handlers' hook beside it where given): the relay's session, else why not. Stopped partway, by
    /// <paramref name="cancel"/> or the folder's end, what it put in is out again before the cancel is thrown.</summary>
    public async Task<(RelaySession? Relay, string? Problem)> PutRelayAsync(DeviceInspector? inspector, UpdateHandlerHook? handlers, CancellationToken cancel)
    {
        (_, RelaySession? relay, string? problem) = await PutAsync(relayed: true, inspector, handlers?.Device, cancel);
        return (relay, problem);
    }

    /// <summary>Passenger's inspector put in the installed Debug app Run starts, as Debug puts it in beside the relay: why
    /// not, or null.</summary>
    public async Task<string?> PutInspectorAsync(DeviceInspector inspector, CancellationToken cancel) =>
        (await PutAsync(relayed: false, inspector, null, cancel)).Problem;

    private async Task<(DevicePut? Put, RelaySession? Relay, string? Problem)> PutAsync(bool relayed, DeviceInspector? inspector, UpdateHandlerHook.DeviceReach? handlers,
        CancellationToken cancel)
    {
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(cancel, ending.Token);
        // Unheld: the folder's end waits for it where the window's loop may have stopped; the start holds the window meanwhile.
        Task<(DevicePut?, RelaySession?, string?)> under = BackgroundWork.Unheld(() => DeviceLaunches.PutInAsync(App, relayed, inspector, handlers, stopping.Token));
        lock (gate)
        {
            putting = under;
        }
        try
        {
            return await under;
        }
        catch (OperationCanceledException)
        {
            // Stopped partway, the put has taken out what it had put in.
            Over();
            throw;
        }
    }

    /// <summary>What the start put in is handed to its run's or session's end, <paramref name="ending"/>, which takes it out
    /// (<see cref="TakeOutAsync"/>) once the app's run here ends: the start has settled, and is out once that end is.</summary>
    public void EndsWith(Task ending)
    {
        lock (gate)
        {
            handed = true;
        }
        settled.TrySetResult();
        IdeErrors.Observe(ending);
        IdeErrors.Observe(OutOnceEndedAsync(ending));
    }

    private async Task OutOnceEndedAsync(Task ending)
    {
        await ending.Ended();
        taken.TrySetResult();
    }

    /// <summary>The app ended on its device and what the start put in taken out, off the window's thread, once for every end
    /// that asks: the start's own where it gives up, its run's or session's, the folder's.</summary>
    public Task TakeOutAsync()
    {
        lock (gate)
        {
            DevicePut? placed = Placed();
            // Unheld: the folder's end waits for it where the window's loop may have stopped.
            takingOut ??= BackgroundWork.Unheld(async () =>
            {
                try
                {
                    await DeviceLaunches.EndAsync(App, placed);
                }
                finally
                {
                    Over();
                }
            });
            return takingOut;
        }
    }

    /// <summary>The folder's end: a put under way stopped (taking out what it had put in), then, where the app was started or
    /// something is in it, the app ended and all taken out at once, whether or not its run or session has ended. Run off
    /// the window's thread, which may run no more.</summary>
    public async Task EndNowAsync()
    {
        Task? under;
        bool started;
        lock (gate)
        {
            under = putting;
            started = handed;
        }
        await ending.CancelAsync();
        if (under is not null)
        {
            await under.Ended();
        }
        bool held;
        lock (gate)
        {
            held = Placed() is not null;
        }
        if (started || held)
        {
            await TakeOutAsync();
        }
        Over();
    }

    /// <summary>The start is over: what it neither handed to an end nor took out (an exception on its way) is taken out now.</summary>
    public void Dispose()
    {
        bool open;
        bool held;
        lock (gate)
        {
            open = !handed && takingOut is null;
            held = Placed() is not null;
        }
        if (open && held)
        {
            IdeErrors.Observe(TakeOutAsync());
        }
        else if (open)
        {
            Over();
        }
    }

    /// <summary>What is in the app, as the last put answered: null before it is in, or where it put nothing. Read under the gate.</summary>
    private DevicePut? Placed() => putting is { IsCompletedSuccessfully: true } put ? put.Result.Put : null;

    private void Over()
    {
        settled.TrySetResult();
        taken.TrySetResult();
    }
}
