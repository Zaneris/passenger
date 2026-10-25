using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Passenger.App.Discord;

/// <summary>The activity's lines and time: Discord's details line (the solution, or Passenger's own words), its state line
/// (the file, or Passenger's own words) and when the solution opened (its elapsed time), each left out when not shown.</summary>
public sealed record DiscordActivity(string? Details, string? State, DateTimeOffset? Started);

/// <summary>Passenger's Rich Presence in the Discord app on this computer: Passenger's icon and button with what
/// <see cref="Show"/> was last given. One worker off the UI thread does it all, so nothing waits on Discord: it connects
/// when there is something to show, sends the newest at most once a <see cref="DiscordIpc.Pause"/> and clears it before
/// letting go. No Discord, or one gone away, is said in one line, then looked for every <see cref="DiscordIpc.Retry"/> in
/// silence. No UI: <paramref name="log"/> is called on any thread.</summary>
public sealed class DiscordPresence(DiscordIpc ipc, Action<string> log)
{
    /// <summary>The Discord application "Passenger", whose name follows Playing; not a secret.</summary>
    public const string Application = "1559714715847098468";

    /// <summary>The application's art asset of Passenger's 1024-pixel icon.</summary>
    public const string LargeImage = "passenger-1024";

    // Passenger's own words, wherever the activity names nothing of the work.

    /// <summary>The icon's hover text, whatever the activity names.</summary>
    public const string LargeText = "Passenger: an IDE for C# and .NET, open to your coding agent";

    /// <summary>The details line of an activity that names nothing of the work (Passenger only, or idle).</summary>
    public const string OwnDetails = "An IDE for C# and .NET";

    /// <summary>The state line of an activity that names nothing of the work.</summary>
    public const string OwnState = "Open source · Windows, macOS and Linux";

    public const string ButtonLabel = "Get Passenger";
    public const string ButtonUrl = "https://passenger-ide.net";

    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource ending = new();
    private readonly Lock gate = new();
    private readonly HashSet<string> refusals = [];
    private DiscordActivity? wanted;
    private bool closing;
    private Task? worker;

    /// <summary>Shows this activity in Discord, in place of the one before; null clears it and lets go of Discord until
    /// there is one again. Returns at once.</summary>
    public void Show(DiscordActivity? activity)
    {
        lock (gate)
        {
            if (closing || (worker is not null && activity == wanted))
            {
                return;
            }
            wanted = activity;
            // Not BackgroundWork: nothing it does is drawn, and a Discord that never answers must hold no screenshot.
            worker ??= Task.Run(RunAsync);
        }
        wake.Writer.TryWrite(true);
    }

    /// <summary>Clears the activity and lets go of Discord, waiting at most the IPC's timeout for it to take the clear
    /// (closing the connection clears it as well). Never throws.</summary>
    public async Task CloseAsync()
    {
        Task? running;
        lock (gate)
        {
            closing = true;
            wanted = null;
            running = worker;
        }
        wake.Writer.TryWrite(true);
        if (running is not null)
        {
            try
            {
                await running.WaitAsync(ipc.Timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The connection's end clears the activity too.
            }
        }
        await ending.CancelAsync().ConfigureAwait(false);
        if (running is not null)
        {
            await running.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }
    }

    private async Task RunAsync()
    {
        DiscordConnection? connection = null;
        DiscordActivity? sent = null;
        bool quiet = false;
        long lastSent = 0;
        CancellationToken end = ending.Token;
        try
        {
            while (true)
            {
                DiscordActivity? now = Wanted();
                if (connection is { Lost.IsCompleted: true })
                {
                    Say(ref quiet, connection.Lost.Result);
                    await connection.DisposeAsync().ConfigureAwait(false);
                    connection = null;
                    await WaitAsync(ipc.Retry, end).ConfigureAwait(false);
                    continue;
                }
                if (now is null)
                {
                    if (connection is not null)
                    {
                        DiscordConnection clearing = connection;
                        await TryAsync(() => clearing.CommandAsync("SET_ACTIVITY", new JsonObject { ["pid"] = Environment.ProcessId }, end)).ConfigureAwait(false);
                        await connection.DisposeAsync().ConfigureAwait(false);
                        connection = null;
                        log($"Discord: the activity at {clearing.Where} cleared");
                    }
                    if (Closing())
                    {
                        return;
                    }
                    await WaitAsync(Timeout.InfiniteTimeSpan, end).ConfigureAwait(false);
                    continue;
                }
                if (connection is null)
                {
                    (DiscordConnection? opened, string why) = await DiscordConnection.OpenAsync(ipc, Application, Refused, end).ConfigureAwait(false);
                    if (opened is null)
                    {
                        Say(ref quiet, why);
                        await WaitAsync(ipc.Retry, end).ConfigureAwait(false);
                        continue;
                    }
                    connection = opened;
                    sent = null;
                    quiet = false;
                    log($"Discord: connected at {connection.Where}");
                    IdeErrors.Observe(connection.Lost.ContinueWith(_ => wake.Writer.TryWrite(true), TaskScheduler.Default));
                }
                if (now == sent)
                {
                    await WaitAsync(Timeout.InfiniteTimeSpan, end).ConfigureAwait(false);
                    continue;
                }
                TimeSpan settle = ipc.Pause / 4;
                TimeSpan since = lastSent == 0 ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(lastSent);
                if (!await WaitAsync(since < ipc.Pause - settle ? ipc.Pause - since : settle, end).ConfigureAwait(false))
                {
                    continue;
                }
                DiscordConnection sending = connection;
                if (await TryAsync(() => sending.CommandAsync("SET_ACTIVITY", new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = Json(now) }, end)).ConfigureAwait(false))
                {
                    sent = now;
                    lastSent = Stopwatch.GetTimestamp();
                    continue;
                }
                Say(ref quiet, $"the connection to {connection.Where} failed");
                await connection.DisposeAsync().ConfigureAwait(false);
                connection = null;
                await WaitAsync(ipc.Retry, end).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (end.IsCancellationRequested)
        {
            // Closed while waiting on Discord.
        }
        catch (Exception error)
        {
            log($"Discord: stopped by an error: {error}");
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Why Discord cannot be reached, said once until it is reached again; the looking goes on in silence.</summary>
    private void Say(ref bool quiet, string why)
    {
        if (!quiet)
        {
            log($"Discord: {why}; looking for it again every {Seconds(ipc.Retry)} in silence");
            quiet = true;
        }
    }

    private DiscordActivity? Wanted()
    {
        lock (gate)
        {
            return wanted;
        }
    }

    /// <summary>Waits for a change or for the time given; whether the time passed with no change.</summary>
    private async Task<bool> WaitAsync(TimeSpan time, CancellationToken end)
    {
        using CancellationTokenSource timed = CancellationTokenSource.CreateLinkedTokenSource(end);
        if (time != Timeout.InfiniteTimeSpan)
        {
            timed.CancelAfter(time);
        }
        try
        {
            await wake.Reader.WaitToReadAsync(timed.Token).ConfigureAwait(false);
            wake.Reader.TryRead(out _);
            return false;
        }
        catch (OperationCanceledException) when (!end.IsCancellationRequested)
        {
            return true;
        }
    }

    private bool Closing()
    {
        lock (gate)
        {
            return closing;
        }
    }

    /// <summary>A command sent, or false once the connection has failed, which the next turn finds lost.</summary>
    private static async Task<bool> TryAsync(Func<Task> send)
    {
        try
        {
            await send().ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Each refusal Discord gives is said once, however often the same update is refused.</summary>
    private void Refused(string why)
    {
        lock (gate)
        {
            if (!refusals.Add(why))
            {
                return;
            }
        }
        log($"Discord: {why}");
    }

    /// <summary>The activity as Discord's SET_ACTIVITY takes it: Passenger's icon and button always, the solution, the file
    /// and the time since the solution opened as given. Discord takes two to 128 characters on each line.</summary>
    private static JsonObject Json(DiscordActivity activity)
    {
        JsonObject json = [];
        if (activity.Details is { } details)
        {
            json["details"] = Line(details);
        }
        if (activity.State is { } state)
        {
            json["state"] = Line(state);
        }
        if (activity.Started is { } started)
        {
            json["timestamps"] = new JsonObject { ["start"] = started.ToUnixTimeMilliseconds() };
        }
        json["assets"] = new JsonObject { ["large_image"] = LargeImage, ["large_text"] = LargeText };
        json["buttons"] = new JsonArray(new JsonObject { ["label"] = ButtonLabel, ["url"] = ButtonUrl });
        return json;
    }

    private static string Line(string text) =>
        text.Length > 128 ? text[..127] + "…" : text.Length < 2 ? text + "​" : text;

    private static string Seconds(TimeSpan time) => FormattableString.Invariant($"{time.TotalSeconds:0.###} s");
}
