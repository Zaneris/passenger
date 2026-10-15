using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using Porta.Pty;

namespace Passenger.Terminal;

/// <summary>A shell in a pseudo-terminal, as Rider's terminal runs one: the user's login shell in the folder, its
/// output read off the window's thread and fed to the <see cref="TerminalScreen"/> on it a slice at a time, keys and
/// pastes written to it off the window's thread, its size kept to the view's. Porta.Pty makes the pseudo-terminal
/// (forkpty in a native shim on macOS and Linux, ConPTY on Windows). Its reads and writes block, so each has a thread
/// of its own: on the thread pool, a pool every thread of which a build or a debugger holds would hold the shell too.</summary>
public sealed class TerminalSession : IAsyncDisposable
{
    /// <summary>Output read but not yet on the screen past which reading waits, so a program writing faster than the
    /// window draws is held in its own write, as on a slow terminal, instead of its output piling up in memory.</summary>
    private const int MostHeld = 256 * 1024;

    /// <summary>How much the screen takes between looks at the clock.</summary>
    private const int Piece = 4096;

    /// <summary>The longest the window's thread parses output in one go before it takes keys and draws again.</summary>
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(10);

    private readonly ConcurrentQueue<byte[]> arrived = new();
    private readonly SemaphoreSlim room = new(0);
    private readonly BlockingCollection<byte[]> outgoing = new(new ConcurrentQueue<byte[]>());
    private IPtyConnection? pty;
    private int? processId;
    private byte[]? feeding;
    private int fed;
    private int held;
    private int posted;
    private volatile bool closed;
    private bool ended;
    private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool startAttempted;

    /// <summary>When output was last read (a <see cref="Stopwatch"/> timestamp), and whether reading has ended: what tells
    /// that the shell's last output is in once it has exited.</summary>
    private long lastRead;
    private volatile bool readEnded;

    /// <summary>How long output must have stopped, after the shell has exited, for its end to be told.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(150);

    /// <summary>The longest the end is told after the shell has exited, its output still coming.</summary>
    private static readonly TimeSpan LastOutputLimit = TimeSpan.FromSeconds(2);

    public TerminalSession(int columns = 80, int rows = 24)
    {
        Screen = new TerminalScreen(columns, rows);
        Screen.Reply += Write;
    }

    public TerminalScreen Screen { get; }

    /// <summary>Raised on the window's thread when output has been taken onto the screen.</summary>
    public event EventHandler? Output;

    /// <summary>Raised once on the window's thread when the shell has ended and its last output is on the screen (or
    /// discarded after close), with its exit code: Porta.Pty's, which is 0 for one a signal ended on macOS and Linux.</summary>
    public event EventHandler<int>? Exited;

    public bool IsRunning { get; private set; }

    /// <summary>The shell's process id, once it runs.</summary>
    public int? ProcessId => processId;

    /// <summary>Starts the shell in a folder. <paramref name="shell"/> names one other than the user's own, or a program
    /// to run in the terminal in place of a shell; <paramref name="variables"/> are set over the IDE's environment, an
    /// empty value taking one away.</summary>
    public async Task StartAsync(string folder, string? shell = null, IReadOnlyList<string>? arguments = null, IReadOnlyDictionary<string, string>? variables = null)
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (startAttempted)
        {
            throw new InvalidOperationException("This terminal session has already started");
        }
        startAttempted = true;
        try
        {
            await SpawnAsync(folder, shell, arguments, variables);
        }
        finally
        {
            // A close racing startup owns the eventual connection too, including preparation or spawn failure.
            started.TrySetResult();
        }
    }

    private async Task SpawnAsync(string folder, string? shell, IReadOnlyList<string>? arguments, IReadOnlyDictionary<string, string>? variables)
    {
        (string app, string[] args) = shell is null ? DefaultShell() : (shell, [.. arguments ?? []]);
        Dictionary<string, string> environment = new(variables ?? new Dictionary<string, string>())
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["TERM_PROGRAM"] = "Passenger",
        };
        // An app opened from the Finder has no LANG, and a shell without one misreads every UTF-8 character.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANG")) && !OperatingSystem.IsWindows() && !environment.ContainsKey("LANG"))
        {
            environment["LANG"] = "en_US.UTF-8";
        }
        PtyOptions options = new()
        {
            Name = "Passenger",
            Cols = Screen.Columns,
            Rows = Screen.Rows,
            Cwd = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            App = app,
            CommandLine = args,
            Environment = environment,
        };
        pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        processId = pty.Pid;
        IsRunning = true;
        pty.ProcessExited += (_, e) => Dispatcher.UIThread.Post(() => AfterLastOutput(e.ExitCode));
        Stream reader = pty.ReaderStream;
        Stream writer = pty.WriterStream;
        new Thread(() => Read(reader)) { IsBackground = true, Name = "Terminal output" }.Start();
        new Thread(() => Send(writer)) { IsBackground = true, Name = "Terminal input" }.Start();
    }

    /// <summary>Puts the IDE's own words on the screen, as if the shell had written them: why a program did not start, or
    /// how it ended. Window's thread.</summary>
    public void Say(string text)
    {
        Screen.Feed(Encoding.UTF8.GetBytes(text));
        Output?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Text for the shell, as typed.</summary>
    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    /// <summary>Bytes for the shell, in order with everything written before. They go from another thread: a program
    /// that reads nothing (a paste during sleep 30) leaves that write waiting in the kernel, never the window.</summary>
    public void Write(byte[] bytes)
    {
        if (pty is null || !IsRunning || bytes.Length == 0 || outgoing.IsAddingCompleted)
        {
            return;
        }
        try
        {
            outgoing.Add(bytes);
        }
        catch (InvalidOperationException)
        {
            // The session closed between the look and the add.
        }
    }

    /// <summary>A paste: line breaks as Enter sends them, every other control character left out, as xterm and Windows
    /// Terminal filter one (so no ESC in it can end its own bracket, nor a Ctrl+C or an escape sequence act as keys),
    /// and bracketed when the program asked (so a shell takes it as text and runs none of its lines).</summary>
    public void Paste(string text)
    {
        StringBuilder kept = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                continue;
            }
            if (c is '\r' or '\n')
            {
                kept.Append('\r');
            }
            else if (c == '\t' || !char.IsControl(c))
            {
                kept.Append(c);
            }
        }
        if (kept.Length == 0)
        {
            return;
        }
        Write(Screen.BracketedPaste ? $"\x1b[200~{kept}\x1b[201~" : kept.ToString());
    }

    public void Resize(int columns, int rows)
    {
        if (columns == Screen.Columns && rows == Screen.Rows)
        {
            return;
        }
        Screen.Resize(columns, rows);
        try
        {
            pty?.Resize(columns, rows);
        }
        catch (Exception failed) when (failed is IOException or InvalidOperationException)
        {
            // A shell that has just ended has no size to set.
        }
    }

    public void Kill()
    {
        try
        {
            pty?.Kill();
        }
        catch (Exception failed) when (failed is IOException or InvalidOperationException)
        {
            // Already gone.
        }
    }

    /// <summary>Ends the process and drains its closing output while native teardown runs away from the window's thread.
    /// Completes once its actual exit has been published and its pseudo-terminal released; repeated closes share it.</summary>
    public ValueTask DisposeAsync()
    {
        if (closed)
        {
            return new ValueTask(disposed.Task);
        }
        closed = true;
        outgoing.CompleteAdding();
        room.Release();
        _ = CloseAsync();
        return new ValueTask(disposed.Task);
    }

    private async Task CloseAsync()
    {
        try
        {
            if (startAttempted)
            {
                await started.Task;
            }
            if (pty is { } connection)
            {
                if (IsRunning)
                {
                    Kill();
                }
                // Disposing Porta's connection can suppress ProcessExited. Keep its process handle until the real
                // exit is available, without blocking the window's thread, and retain its code across native teardown.
                try
                {
                    int exitCode = await Task.Run(() =>
                    {
                        try
                        {
                            connection.WaitForExit(-1);
                            return connection.ExitCode;
                        }
                        finally
                        {
                            // Windows' ClosePseudoConsole may wait for inherited output handles. The reader keeps
                            // draining while this runs; neither that wait nor the process wait holds the UI thread.
                            connection.Dispose();
                        }
                    });
                    await Dispatcher.UIThread.InvokeAsync(() => AfterLastOutput(exitCode));
                }
                finally
                {
                    pty = null;
                }
            }
            disposed.TrySetResult();
        }
        catch (Exception failed)
        {
            disposed.TrySetException(failed);
        }
    }

    /// <summary>The user's shell as a login shell, as Rider starts it: $SHELL (zsh where unset on macOS, bash on
    /// Linux); PowerShell on Windows, the newer one when it is installed, keeping its history in HISTFILE when that is
    /// set, as sh does.</summary>
    public static (string App, string[] Arguments) DefaultShell()
    {
        if (OperatingSystem.IsWindows())
        {
            string? pwsh = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(folder => Path.Combine(folder, "pwsh.exe"))
                .FirstOrDefault(File.Exists);
            string powershell = pwsh ?? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            // PSReadLine reads no variable for its history, which a screenshot and a test run move (HISTFILE): it is told
            // before the first prompt, and so before it reads or writes the person's own.
            return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HISTFILE"))
                ? (powershell, ["-NoLogo"])
                : (powershell, ["-NoLogo", "-NoExit", "-Command", "try { Set-PSReadLineOption -HistorySavePath $env:HISTFILE } catch { }"]);
        }
        string shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } own && File.Exists(own)
            ? own
            : OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";
        return (shell, ["-l"]);
    }

    /// <summary>Tells of the shell's end once what it wrote last is on the screen: when the pseudo-terminal closes, or,
    /// where it stays open (ConPTY keeps it until it is closed, and a program the shell left behind may hold it), once
    /// nothing more has come for a moment, two seconds at the most.</summary>
    private void AfterLastOutput(int exitCode)
    {
        if (ended)
        {
            return;
        }
        long exited = Stopwatch.GetTimestamp();
        if (Done())
        {
            End();
            return;
        }
        DispatcherTimer waiting = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
        waiting.Tick += (_, _) =>
        {
            if (Done())
            {
                waiting.Stop();
                End();
            }
        };
        waiting.Start();

        bool Done() => closed || readEnded || Stopwatch.GetElapsedTime(exited) >= LastOutputLimit
            || Stopwatch.GetElapsedTime(Math.Max(exited, Interlocked.Read(ref lastRead))) >= Quiet;

        void End()
        {
            if (ended)
            {
                return;
            }
            ended = true;
            TakeAll();
            IsRunning = false;
            Exited?.Invoke(this, exitCode);
        }
    }

    /// <summary>Puts all the output read so far on the screen at once, as the shell ends.</summary>
    private void TakeAll()
    {
        if (closed)
        {
            return;
        }
        int taken = 0;
        if (feeding is not null && fed < feeding.Length)
        {
            Screen.Feed(feeding.AsSpan(fed));
            taken += feeding.Length - fed;
        }
        feeding = null;
        fed = 0;
        while (arrived.TryDequeue(out byte[]? piece))
        {
            Screen.Feed(piece);
            taken += piece.Length;
        }
        if (taken > 0)
        {
            if (Interlocked.Add(ref held, -taken) < MostHeld && room.CurrentCount == 0)
            {
                room.Release();
            }
            Output?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Reads the shell's output as it comes and hands it to the window's thread, waiting while the screen has
    /// <see cref="MostHeld"/> still to take.</summary>
    private void Read(Stream reader)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int read = reader.Read(buffer);
                if (read <= 0)
                {
                    break;
                }
                if (closed)
                {
                    // A closing pseudo-console can still produce output until its clients detach. Discard it, but
                    // continue draining so native disposal cannot deadlock waiting for a full output pipe.
                    continue;
                }
                Interlocked.Exchange(ref lastRead, Stopwatch.GetTimestamp());
                arrived.Enqueue(buffer[..read]);
                Interlocked.Add(ref held, read);
                if (Interlocked.Exchange(ref posted, 1) == 0)
                {
                    Dispatcher.UIThread.Post(TakeOutput, DispatcherPriority.Input);
                }
                while (Volatile.Read(ref held) >= MostHeld && !closed)
                {
                    room.Wait();
                }
            }
        }
        catch (Exception failed) when (failed is IOException or ObjectDisposedException)
        {
            // The pseudo-terminal closed with the shell.
        }
        finally
        {
            readEnded = true;
        }
    }

    /// <summary>Writes what was typed and pasted, in order, for as long as the shell runs.</summary>
    private void Send(Stream writer)
    {
        try
        {
            foreach (byte[] bytes in outgoing.GetConsumingEnumerable())
            {
                writer.Write(bytes);
                writer.Flush();
            }
        }
        catch (Exception failed) when (failed is IOException or ObjectDisposedException)
        {
            // The shell has gone; its exit says so.
        }
    }

    /// <summary>Puts output on the screen for at most a <see cref="Slice"/>, then, while more comes, lets the window take
    /// its keys and draw before the next, as xterm.js parses: a flood of output never holds the window nor keeps Ctrl+C
    /// from the program. The next waits on a timer, since one posted would run ahead of the keys.</summary>
    private void TakeOutput()
    {
        if (closed)
        {
            return;
        }
        long started = Stopwatch.GetTimestamp();
        int taken = 0;
        while (Stopwatch.GetElapsedTime(started) < Slice)
        {
            if (feeding is null || fed == feeding.Length)
            {
                if (!arrived.TryDequeue(out feeding))
                {
                    break;
                }
                fed = 0;
            }
            int piece = Math.Min(Piece, feeding.Length - fed);
            Screen.Feed(feeding.AsSpan(fed, piece));
            fed += piece;
            taken += piece;
        }
        if (taken > 0)
        {
            // Only this thread releases, so the reader is never owed more than one wake.
            if (Interlocked.Add(ref held, -taken) < MostHeld && room.CurrentCount == 0)
            {
                room.Release();
            }
            Output?.Invoke(this, EventArgs.Empty);
            // Even when this slice emptied the queue, the reader's next post would run straight after it.
            DispatcherTimer.RunOnce(TakeOutput, TimeSpan.FromMilliseconds(1), DispatcherPriority.Input);
            return;
        }
        Interlocked.Exchange(ref posted, 0);
        // Output that came after the queue was found empty, whose reader saw this still posted.
        if (!arrived.IsEmpty && Interlocked.Exchange(ref posted, 1) == 0)
        {
            Dispatcher.UIThread.Post(TakeOutput, DispatcherPriority.Input);
        }
    }
}
