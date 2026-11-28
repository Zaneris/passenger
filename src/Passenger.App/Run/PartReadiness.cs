using System.Text.RegularExpressions;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>Whether a part of a run of several parts is ready for the parts after it, as it says (<see cref="RunPart.Ready"/>):
/// once it ends with exit code 0, once a line its program writes matches its pattern, or once something listens at its
/// address (a .NET project naming none: once its host says it serves, <see cref="ListeningLines"/>). Its lines are heard
/// from its console from the start (<see cref="Hear"/>), a debugged program's from its Debug tab's placing
/// (<see cref="HearPlaced"/>), or read off its terminal's screen as it changes (<see cref="Watch"/>). A UI-thread object,
/// disposed with its wait.</summary>
public sealed class PartReadiness(RunPart part, string name, bool dotnet) : IDisposable
{
    private readonly TaskCompletionSource<string?> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<IDisposable> hearing = [];
    private readonly HashSet<OutputView> heard = [];
    private readonly ListeningLines serving = new();
    private Regex? pattern;
    private string? unread;

    /// <summary>What the start says while it waits.</summary>
    public string Waiting => part.Ready switch
    {
        ReadyWhen.Exited => $"Waiting for {name} to end…",
        ReadyWhen.Line => $"Waiting for {name} to write a line matching {part.ReadyText}…",
        _ => part.ReadyText.Length > 0 ? $"Waiting for {name} to listen at {part.ReadyText}…" : $"Waiting for {name}'s host to start…",
    };

    /// <summary>Hears each line its program writes to a console from now, a console heard already passed over.</summary>
    public void Hear(OutputView console)
    {
        if (HearsLines && heard.Add(console))
        {
            hearing.Add(new ConsoleLines(console, Heard, programs: true));
        }
    }

    /// <summary>Hears the console of the Debug tab <paramref name="debug"/> places for its program (named
    /// <paramref name="tab"/>) from the tab's placing, so what its program writes before its launch returns is heard too.</summary>
    public void HearPlaced(Debugging.DebugController debug, string tab)
    {
        if (!HearsLines)
        {
            return;
        }
        HashSet<DebugTab> before = [.. debug.Tabs];
        void Placed(object? sender, EventArgs e)
        {
            if (debug.Tabs.FirstOrDefault(t => t.Name == tab && !before.Contains(t)) is { } placed)
            {
                debug.TabsChanged -= Placed;
                Hear(placed.Console);
            }
        }
        debug.TabsChanged += Placed;
        hearing.Add(new Unhook(() => debug.TabsChanged -= Placed));
    }

    /// <summary>Whether its wait reads its program's lines: a line's, or a .NET host's start.</summary>
    private bool HearsLines => part.Ready == ReadyWhen.Line || part.Ready == ReadyWhen.Listening && part.ReadyText.Length == 0;

    /// <summary>Reads each line of a terminal's screen as it changes, the lines it held as it started among them.</summary>
    public void Watch(TerminalRun run)
    {
        if (!HearsLines)
        {
            return;
        }
        int read = 0;
        void Look(object? sender, EventArgs e)
        {
            Terminal.TerminalScreen screen = run.Session.Screen;
            List<string> lines = [.. screen.Scrollback.Select(l => l.Text()).Concat(screen.Lines.Select(l => l.Text()))];
            // A line the screen still writes may be heard again once whole: matching it twice changes nothing.
            for (int at = Math.Max(0, Math.Min(read, lines.Count) - 1); at < lines.Count; at++)
            {
                Heard(lines[at]);
            }
            read = lines.Count;
        }
        run.Session.Output += Look;
        hearing.Add(new Unhook(() => run.Session.Output -= Look));
        Look(null, EventArgs.Empty);
    }

    /// <summary>Waits until it is ready: null once it is, else why not (it ended first, or with another exit code, or its
    /// pattern did not read, or nothing listened); a stop ends the wait as an <see cref="OperationCanceledException"/>.</summary>
    public async Task<string?> ReadyAsync(Task<int>? exit, Task ended, CancellationToken stop)
    {
        if (part.Ready == ReadyWhen.Exited)
        {
            if (exit is null)
            {
                return $"{name} is waited for until it ends, which a program under the debugger is not";
            }
            int code = await exit.WaitAsync(stop);
            return code == 0 ? null : $"{name} ended with {ExitCodes.Said(code)}";
        }
        if (part.Ready == ReadyWhen.Line && (pattern ??= Written()) is null)
        {
            return $"{name}'s pattern {part.ReadyText} does not read: {unread}";
        }
        using CancellationTokenSource listening = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (part.Ready == ReadyWhen.Listening && part.ReadyText.Length > 0)
        {
            if (AddressOf(part.ReadyText) is not { } address)
            {
                return $"{name}'s address {part.ReadyText} is no host and port";
            }
            IdeErrors.Observe(ListenAsync(address, listening.Token));
        }
        Task done;
        try
        {
            // A stop ends the wait itself: a part that neither readies nor ends would hold it for good.
            done = await Task.WhenAny(ready.Task, ended).WaitAsync(stop);
        }
        finally
        {
            await listening.CancelAsync();
        }
        stop.ThrowIfCancellationRequested();
        // Its last line, which no line break ended, is heard as its end lands.
        foreach (ConsoleLines lines in hearing.OfType<ConsoleLines>())
        {
            lines.Rest();
        }
        if (done == ready.Task || ready.Task.IsCompleted)
        {
            return await ready.Task;
        }
        return exit is { IsCompletedSuccessfully: true } ? $"{name} ended with {ExitCodes.Said(await exit)} before it was ready" : $"{name} ended before it was ready";
    }

    /// <summary>A host and port as an address listened at: a URL's, or one written alone (localhost:6380).</summary>
    public static Uri? AddressOf(string written) =>
        Uri.TryCreate(written.Contains("://", StringComparison.Ordinal) ? written : $"tcp://{written}", UriKind.Absolute, out Uri? address) && address.Port > 0 ? address : null;

    /// <summary>Tries the address until something listens there, saying so where nothing does.</summary>
    private async Task ListenAsync(Uri address, CancellationToken stop)
    {
        // Not background work: it lasts as long as the part takes to listen, which no settling test waits for.
        bool? up = await BackgroundWork.Unheld(() => BrowserLaunch.ListeningAsync(address, stop));
        if (up is { } listened)
        {
            ready.TrySetResult(listened ? null : $"nothing listened at {part.ReadyText} while {name} started");
        }
    }

    private void Heard(string line)
    {
        if (ready.Task.IsCompleted)
        {
            return;
        }
        if (part.Ready == ReadyWhen.Line)
        {
            try
            {
                if ((pattern ??= Written()) is { } written && written.IsMatch(line))
                {
                    ready.TrySetResult(null);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                ready.TrySetResult($"{TextSearch.RegexStoppedIn($"{name}'s pattern")}, so it is never heard as ready");
            }
            return;
        }
        serving.Hear(line);
        if (dotnet && (serving.HostStarted || serving.Addresses.Count > 0))
        {
            ready.TrySetResult(null);
        }
    }

    /// <summary>The part's pattern, or null where it does not read (said once the wait asks for it).</summary>
    private Regex? Written()
    {
        try
        {
            return TextSearch.Written(part.ReadyText, RegexOptions.None, TextSearch.AtOnce);
        }
        catch (ArgumentException misread)
        {
            unread = misread.Message;
            return null;
        }
    }

    public void Dispose()
    {
        foreach (IDisposable each in hearing)
        {
            each.Dispose();
        }
        hearing.Clear();
        heard.Clear();
    }

    private sealed class Unhook(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
