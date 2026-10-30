using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using Passenger.Core;
using Porta.Pty;

namespace Passenger.Terminal;

/// <summary>A shell in a pseudo-terminal (Porta.Pty's): the user's login shell in the folder, its output read off the
/// window's thread and fed to the <see cref="TerminalScreen"/> on it a slice at a time, keys and pastes written to it
/// off the window's thread, its size kept to the view's. Its reads and writes block, so each has a thread of its own:
/// on the thread pool, a pool every thread of which a build or a debugger holds would hold the shell too.</summary>
public sealed class TerminalSession : IAsyncDisposable
{
    /// <summary>Output read but not yet on the screen past which reading waits, so a program writing faster than the
    /// window draws is held in its own write, as on a slow terminal, instead of its output piling up in memory.</summary>
    private const int MostHeld = 256 * 1024;

    /// <summary>How much the screen takes between looks at the clock.</summary>
    private const int PieceSize = 4096;

    /// <summary>The longest the window's thread parses output in one go before it takes keys and draws again.</summary>
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(10);

    private readonly ConcurrentQueue<Piece> arrived = new();
    private readonly SemaphoreSlim room = new(0);
    private readonly BlockingCollection<byte[]> outgoing = new(new ConcurrentQueue<byte[]>());
    private IPtyConnection? pty;

    /// <summary>The piece the screen is taking, and how much of it it has taken.</summary>
    private Piece feeding;
    private int fed;

    /// <summary>What takes the next slice of output once the window has drawn and taken its keys.</summary>
    private DispatcherTimer? next;

    /// <summary><see cref="TakeOutput"/>, made once for every post of it.</summary>
    private readonly Action take;
    private int held;
    private int posted;
    private volatile bool closed;

    /// <summary>When output was last read (a <see cref="Stopwatch"/> timestamp), and whether reading has ended: what tells
    /// that the shell's last output is in once it has exited.</summary>
    private long lastRead;
    private volatile bool readEnded;
    private readonly ManualResetEventSlim readDone = new();

    /// <summary>The shell's end and its pseudo-terminal's close, once asked for (<see cref="End"/>).</summary>
    private Task? ending;

    /// <summary>Whether the shell's end has been told: Porta.Pty's watcher and the start's own look may both find it.</summary>
    private int endTold;

    /// <summary>How long output must have stopped, after the shell has exited, for its end to be told.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(150);

    /// <summary>The longest the end is told after the shell has exited, its output still coming.</summary>
    private static readonly TimeSpan LastOutputLimit = TimeSpan.FromSeconds(2);

    /// <summary>How long a shell killed has to go, what it wrote read out, before its pseudo-terminal closes on it all the
    /// same.</summary>
    private static readonly TimeSpan EndingLimit = TimeSpan.FromSeconds(2);

    /// <summary>What describes the terminal Passenger itself was started in: a tmux pane (whose tmux commands would act on
    /// the person's own session), a screen window, an X terminal's window, its terminal description and its size.</summary>
    private static readonly string[] OuterTerminal = ["TMUX", "TMUX_PANE", "STY", "WINDOW", "WINDOWID", "TERMCAP", "COLUMNS", "LINES"];

    /// <summary>What begins the name of each variable of macOS's loader, which it takes out of the environment of a
    /// program System Integrity Protection guards (env and sh among them), and so out of whatever that program runs.</summary>
    private const string LoaderPrefix = "DYLD_";

    /// <summary>What a loader variable is carried under past env and sh (<see cref="Unsetting"/>), its own name after it.</summary>
    [IdentifierHome]
    public const string KeptPrefix = "PASSENGER_KEPT_";

    public TerminalSession(int columns = 80, int rows = 24)
    {
        Screen = new TerminalScreen(columns, rows);
        Screen.Reply += Write;
        take = TakeOutput;
    }

    public TerminalScreen Screen { get; }

    /// <summary>Raised on the window's thread when output has been taken onto the screen.</summary>
    public event EventHandler? Output;

    /// <summary>Raised on the window's thread when the shell has ended and its last output is on the screen, with its exit
    /// code: Porta.Pty's, which is 0 for one a signal ended on macOS and Linux.</summary>
    public event EventHandler<int>? Exited;

    public bool IsRunning { get; private set; }

    /// <summary>The shell's process id, once it runs.</summary>
    public int? ProcessId => pty?.Pid;

    /// <summary>How the program run reads a quoted word, once it is started.</summary>
    public ShellKind Shell { get; private set; }

    /// <summary>Starts the shell in a folder. <paramref name="shell"/> names one other than the user's own, or a program
    /// to run in the terminal in place of a shell; <paramref name="variables"/> are set over the IDE's environment, an
    /// empty value taking one away, and the outer terminal's are taken out (<see cref="WithoutOuterTerminal"/>).</summary>
    public async Task StartAsync(string folder, string? shell = null, IReadOnlyList<string>? arguments = null, IReadOnlyDictionary<string, string>? variables = null)
    {
        (string app, string[] args) = shell is null ? DefaultShell() : (shell, [.. arguments ?? []]);
        Shell = ShellWords.KindOf(app);
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
        WithoutOuterTerminal(environment, Environment.GetEnvironmentVariable);
        string cwd = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!OperatingSystem.IsWindows())
        {
            // A shell keeps an inherited PWD naming its folder through a link, and a build there by it undoes the IDE's.
            environment.TryAdd("PWD", cwd);
            IReadOnlyDictionary<string, string> loader = OperatingSystem.IsMacOS() ? LoaderVariables(environment, Inherited()) : new Dictionary<string, string>();
            (app, args) = Unsetting([.. environment.Where(v => v.Value.Length == 0).Select(v => v.Key)], app, args, environment, loader);
        }
        PtyOptions options = new()
        {
            Name = "Passenger",
            Cols = Screen.Columns,
            Rows = Screen.Rows,
            Cwd = cwd,
            App = app,
            // On Windows, quoted as the C runtime splits a command line back, and given whole.
            CommandLine = OperatingSystem.IsWindows() ? [WindowsCommandLine.Of(args)] : args,
            VerbatimCommandLine = OperatingSystem.IsWindows(),
            Environment = environment,
        };
        pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        IsRunning = true;
        IPtyConnection started = pty;
        started.ProcessExited += (_, e) => Ended(e.ExitCode);
        // Porta.Pty watches from the spawn on, so a program that ends at once may have ended unheard before the line above.
        if (started.WaitForExit(0))
        {
            Ended(started.ExitCode);
        }
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

    /// <summary>A paste: line breaks as Enter sends them, every other control character left out (so no ESC in it can
    /// end its own bracket, nor a Ctrl+C or an escape sequence act as keys), and bracketed when the program asked (so a
    /// shell takes it as text and runs none of its lines).</summary>
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

    /// <summary>Ends the shell, killed if it runs, and closes its pseudo-terminal, off the window's thread, since both may
    /// wait on the shell (<see cref="End"/>); what it writes from now on is read and left off the screen.</summary>
    public ValueTask DisposeAsync()
    {
        if (ending is null)
        {
            closed = true;
            outgoing.CompleteAdding();
            room.Release();
            IPtyConnection? connection = pty;
            pty = null;
            bool running = IsRunning;
            // Unheld: a shell that will not go holds it for EndingLimit, and nothing the window shows waits for it.
            ending = connection is null ? Task.CompletedTask : BackgroundWork.UnheldThread(() => End(connection, running));
        }
        return new ValueTask(ending);
    }

    /// <summary>Kills the shell and closes its pseudo-terminal once the shell has gone: a shell leading its session waits as
    /// it exits for the terminal to take what it wrote, and the close waits for a write still going (a paste the program
    /// never read), which only the shell's going ends. ConPTY keeps its output open until it closes, so Windows waits for
    /// none of it.</summary>
    private void End(IPtyConnection connection, bool running)
    {
        if (running)
        {
            try
            {
                connection.Kill();
            }
            catch (Exception failed) when (failed is IOException or InvalidOperationException)
            {
                // Already gone.
            }
        }
        if (!OperatingSystem.IsWindows())
        {
            readDone.Wait(Waits.Of(EndingLimit));
        }
        connection.Dispose();
    }

    /// <summary>Takes out of a program's environment the variables of the terminal Passenger was started in, which the
    /// program is not in (<see cref="OuterTerminal"/>): each one Passenger inherited, unless the caller gives it a value
    /// of its own, since a run hands over the inherited value with the rest of its environment.</summary>
    public static void WithoutOuterTerminal(Dictionary<string, string> environment, Func<string, string?> inherited)
    {
        foreach (string variable in OuterTerminal)
        {
            if (inherited(variable) is { } outer && (!environment.TryGetValue(variable, out string? given) || given == outer))
            {
                environment[variable] = string.Empty;
            }
        }
    }

    /// <summary>A program run on macOS or Linux with these variables taken out of what it inherits: its pseudo-terminal
    /// starts it from the process's own environment, setting over it only what has a value, so env unsets them before it
    /// runs the program. The <paramref name="loader"/>'s variables, which env would lose, go into the environment under
    /// <see cref="KeptPrefix"/> for sh to set again before its exec, their values on no command line; a program whose name
    /// holds '=', which env would take for a variable, is run by sh's exec too.</summary>
    public static (string App, string[] Arguments) Unsetting(IReadOnlyList<string> variables, string app, string[] arguments,
        IDictionary<string, string> environment, IReadOnlyDictionary<string, string> loader)
    {
        // No environment holds a name with '=' in it, and env refuses to unset one.
        string[] names = [.. variables.Where(name => name.Length > 0 && !name.Contains('=', StringComparison.Ordinal))];
        if (names.Length == 0)
        {
            return (app, arguments);
        }
        List<string> words = [.. names.SelectMany(name => (string[])["-u", name]), "--"];
        // sh sets only a name that is an identifier, as each the loader reads is.
        string[] kept = [.. loader.Keys.Where(IsIdentifier).Order(StringComparer.Ordinal)];
        if (kept.Length > 0 || app.Contains('=', StringComparison.Ordinal))
        {
            foreach (string name in kept)
            {
                environment[KeptPrefix + name] = loader[name];
            }
            string setAgain = string.Concat(kept.Select(name => $"{name}=\"${KeptPrefix}{name}\"; export {name}; unset {KeptPrefix}{name}; "));
            words.AddRange(["/bin/sh", "-c", setAgain + "exec \"$0\" \"$@\""]);
        }
        return ("/usr/bin/env", [.. words, app, .. arguments]);
    }

    /// <summary>The variables of macOS's loader a program started with this environment is to have: each one Passenger
    /// inherited, set over by the start's own, less each the start takes away (an empty value).</summary>
    public static Dictionary<string, string> LoaderVariables(IReadOnlyDictionary<string, string> environment, IEnumerable<KeyValuePair<string, string>> inherited)
    {
        Dictionary<string, string> loader = new(StringComparer.Ordinal);
        foreach ((string name, string value) in inherited.Concat(environment))
        {
            if (!name.StartsWith(LoaderPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            if (value.Length == 0)
            {
                loader.Remove(name);
            }
            else
            {
                loader[name] = value;
            }
        }
        return loader;
    }

    /// <summary>The process's environment as a program it starts inherits it.</summary>
    private static IEnumerable<KeyValuePair<string, string>> Inherited() =>
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(variable => new KeyValuePair<string, string>((string)variable.Key, variable.Value as string ?? string.Empty));

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>The user's shell as a login shell: $SHELL (zsh where unset on macOS, bash on Linux); PowerShell on
    /// Windows, the newer one when it is installed, keeping its history in HISTFILE when that is set.</summary>
    public static (string App, string[] Arguments) DefaultShell()
    {
        if (OperatingSystem.IsWindows())
        {
            string? pwsh = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(folder => Path.Combine(folder, "pwsh.exe"))
                .FirstOrDefault(File.Exists);
            string powershell = pwsh ?? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            // PSReadLine reads no variable for its history, so it is told before the first prompt reads the person's own.
            return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HISTFILE"))
                ? (powershell, ["-NoLogo"])
                : (powershell, ["-NoLogo", "-NoExit", "-Command", "try { Set-PSReadLineOption -HistorySavePath $env:HISTFILE } catch { }"]);
        }
        string shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } own && File.Exists(own)
            ? own
            : OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";
        return (shell, ["-l"]);
    }

    /// <summary>The shell has ended, as Porta.Pty tells or as its start found: told on the window's thread once.</summary>
    private void Ended(int exitCode)
    {
        if (Interlocked.Exchange(ref endTold, 1) == 0)
        {
            Dispatcher.UIThread.Post(() => AfterLastOutput(exitCode));
        }
    }

    /// <summary>Tells of the shell's end once what it wrote last is on the screen: when the pseudo-terminal closes, or,
    /// where it stays open (ConPTY keeps it until it is closed, and a program the shell left behind may hold it), once
    /// nothing more has come for a moment, two seconds at the most.</summary>
    private void AfterLastOutput(int exitCode)
    {
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
        if (feeding.Bytes is not null)
        {
            Screen.Feed(feeding.Bytes.AsSpan(fed, feeding.Length - fed));
            taken += feeding.Length - fed;
            ArrayPool<byte>.Shared.Return(feeding.Bytes);
        }
        feeding = default;
        fed = 0;
        while (arrived.TryDequeue(out Piece piece))
        {
            Screen.Feed(piece.Bytes.AsSpan(0, piece.Length));
            taken += piece.Length;
            ArrayPool<byte>.Shared.Return(piece.Bytes);
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

    /// <summary>Reads the shell's output as it comes and hands it to the window's thread, each read in an array of the
    /// pool's about its size, waiting while the screen has <see cref="MostHeld"/> still to take; once the session is
    /// closed, to the end, the shell's end waiting for it (<see cref="End"/>).</summary>
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
                    continue;
                }
                Interlocked.Exchange(ref lastRead, Stopwatch.GetTimestamp());
                byte[] piece = ArrayPool<byte>.Shared.Rent(read);
                buffer.AsSpan(0, read).CopyTo(piece);
                arrived.Enqueue(new Piece(piece, read));
                Interlocked.Add(ref held, read);
                if (Interlocked.Exchange(ref posted, 1) == 0)
                {
                    Dispatcher.UIThread.Post(take, DispatcherPriority.Input);
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
            readDone.Set();
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
    /// its keys and draw before the next: a flood of output never holds the window nor keeps Ctrl+C
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
            if (feeding.Bytes is null)
            {
                if (!arrived.TryDequeue(out feeding))
                {
                    break;
                }
                fed = 0;
            }
            int piece = Math.Min(PieceSize, feeding.Length - fed);
            Screen.Feed(feeding.Bytes.AsSpan(fed, piece));
            fed += piece;
            taken += piece;
            if (fed == feeding.Length)
            {
                ArrayPool<byte>.Shared.Return(feeding.Bytes);
                feeding = default;
            }
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
            if (next is null)
            {
                next = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(1) };
                next.Tick += (_, _) =>
                {
                    next.Stop();
                    TakeOutput();
                };
            }
            next.Start();
            return;
        }
        Interlocked.Exchange(ref posted, 0);
        // Output that came after the queue was found empty, whose reader saw this still posted.
        if (!arrived.IsEmpty && Interlocked.Exchange(ref posted, 1) == 0)
        {
            Dispatcher.UIThread.Post(take, DispatcherPriority.Input);
        }
    }

    /// <summary>Output as read: an array of the pool's, and how much of it the read filled.</summary>
    private readonly record struct Piece(byte[] Bytes, int Length);
}
