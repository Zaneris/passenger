using Porta.Pty;

namespace Passenger.Terminal;

/// <summary>A program in a pseudo-terminal, as a <see cref="TerminalSession"/> runs it: on macOS one of Passenger's own
/// (<see cref="MacPseudoTerminal"/>), elsewhere Porta.Pty's.</summary>
internal interface IPseudoTerminal : IDisposable
{
    int Pid { get; }

    /// <summary>The program's output; its read ends once every other end of the pseudo-terminal has closed.</summary>
    Stream ReaderStream { get; }

    Stream WriterStream { get; }

    /// <summary>Raised once, on any thread, as the program ends, with its exit code: 0 for one a signal ended.</summary>
    event EventHandler<int>? Exited;

    bool HasExited { get; }

    int ExitCode { get; }

    /// <summary>Lets the program begin, once something reads its output: a program started held waits until then.</summary>
    void LetRun();

    void Resize(int columns, int rows);

    void Kill();
}

/// <summary>The pseudo-terminals a <see cref="TerminalSession"/> starts its program in.</summary>
internal static class PseudoTerminals
{
    public static async Task<IPseudoTerminal> StartAsync(PtyOptions options) =>
        OperatingSystem.IsMacOS() ? MacPseudoTerminal.Start(options) : new Ported(await PtyProvider.SpawnAsync(options, CancellationToken.None));

    /// <summary>Porta.Pty's, which lets its program run from its start.</summary>
    private sealed class Ported : IPseudoTerminal
    {
        private readonly IPtyConnection connection;

        public Ported(IPtyConnection connection)
        {
            this.connection = connection;
            connection.ProcessExited += (_, e) => Exited?.Invoke(this, e.ExitCode);
        }

        public event EventHandler<int>? Exited;

        public int Pid => connection.Pid;

        public Stream ReaderStream => connection.ReaderStream;

        public Stream WriterStream => connection.WriterStream;

        public bool HasExited => connection.WaitForExit(0);

        public int ExitCode => connection.ExitCode;

        public void LetRun()
        {
        }

        public void Resize(int columns, int rows) => connection.Resize(columns, rows);

        public void Kill() => connection.Kill();

        public void Dispose() => connection.Dispose();
    }
}
