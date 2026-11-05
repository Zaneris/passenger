using System.Diagnostics;
using System.Globalization;
using Passenger.Core;

namespace Passenger.App.Godot;

/// <summary>Linux's X server of Passenger's own that draws to memory alone (Xvfb), for the Godot games of an agent's own
/// Passenger: they draw in windows there, which reach no screen, and their frames are read through Godot's debugger. Started
/// through ChildProcesses as a game first needs it, with <c>-displayfd</c>, so it picks a free display and says which; held
/// while a game draws there (<see cref="Hold"/>, <see cref="Release"/>), ended once none has for <see cref="Idle"/>, and with
/// Passenger.</summary>
public static class VirtualDisplays
{
    /// <summary>The variable that names an X display to a program.</summary>
    public const string Variable = "DISPLAY";

    /// <summary>How long the server stays with no game on it: about 20 MB, and a start of a few hundred milliseconds.</summary>
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(3);

    /// <summary>How long the server may take to say its display.</summary>
    private static readonly TimeSpan Starting = TimeSpan.FromSeconds(15);

    /// <summary>The screen's size, larger than a game's window as a rule: what lies past it still renders in Godot's viewport.</summary>
    private const string Screen = "2560x1440x24";

    private static readonly SemaphoreSlim Turn = new(1, 1);
    private static Process? server;
    private static string? display;
    private static int held;
    private static CancellationTokenSource? idle;

    /// <summary>The display of the server, started now where none runs; null where Xvfb is not installed or would not start,
    /// which the log says. Given up as it starts, it leaves no server running.</summary>
    public static async Task<string?> OpenAsync(CancellationToken cancel)
    {
        await Turn.WaitAsync(cancel);
        try
        {
            if (server is { } running && display is { } shown && DotnetProcesses.Runs(running.Id))
            {
                LetGoLater();
                return shown;
            }
            if (Programs.OnPath(GodotExecutables.VirtualDisplay) is not { } program)
            {
                return null;
            }
            ProcessStartInfo start = ChildProcesses.Command(program, ["-displayfd", "1", "-screen", "0", Screen, "-nolisten", "tcp"]);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            Process started = await ChildProcesses.StartAsync(start);
            TextReader said = ChildProcesses.Output(started);
            string? number;
            try
            {
                number = await said.ReadLineAsync(cancel).AsTask().WaitAsync(Starting, cancel);
            }
            catch (TimeoutException)
            {
                number = null;
            }
            catch
            {
                // Given up as it starts: nothing holds it yet, so no idle wait would end it.
                ChildProcesses.Kill(started);
                started.Dispose();
                throw;
            }
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int which))
            {
                ChildProcesses.Kill(started);
                started.Dispose();
                IdeErrors.Write($"Xvfb did not say its display within {Starting.TotalSeconds:0} s ({number ?? "nothing"}): an agent's Godot games run headless, with no frames");
                return null;
            }
            // What it says after its display is its log's, for as long as it runs.
            IdeErrors.Observe(ChildProcesses.Hear(started, _ => { }, line => IdeErrors.Write($"Xvfb: {line}")));
            (server, display) = (started, $":{which.ToString(CultureInfo.InvariantCulture)}");
            IdeErrors.Write($"Xvfb ({program}) draws an agent's Godot games on display {display}");
            LetGoLater();
            return display;
        }
        finally
        {
            Turn.Release();
        }
    }

    /// <summary>Whether a display is this server's, as a plan's environment names it.</summary>
    public static bool Serves(string? named) => named is not null && named == display;

    /// <summary>A game drawing on the server: it stays while one does.</summary>
    public static void Hold()
    {
        Interlocked.Increment(ref held);
        Interlocked.Exchange(ref idle, null)?.Cancel();
    }

    /// <summary>A game no longer drawing there: once none does, the server goes after <see cref="Idle"/>.</summary>
    public static void Release()
    {
        if (Interlocked.Decrement(ref held) <= 0)
        {
            LetGoLater();
        }
    }

    /// <summary>The server ended once <see cref="Idle"/> passes with no game held on it.</summary>
    private static void LetGoLater()
    {
        CancellationTokenSource waiting = new();
        Interlocked.Exchange(ref idle, waiting)?.Cancel();
        // Unheld: no screenshot or settling test waits on a server kept for a game yet to come.
        IdeErrors.Observe(BackgroundWork.Unheld(async () =>
        {
            try
            {
                await Task.Delay(Idle, waiting.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await Turn.WaitAsync();
            try
            {
                if (Volatile.Read(ref held) > 0 || Volatile.Read(ref idle) != waiting || server is not { } ending)
                {
                    return;
                }
                ChildProcesses.Kill(ending);
                ending.Dispose();
                (server, display) = (null, null);
                IdeErrors.Write($"Xvfb let go: no game drew on it for {Idle.TotalMinutes:0} minutes");
            }
            finally
            {
                Turn.Release();
            }
        }));
    }
}
