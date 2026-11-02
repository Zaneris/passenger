using System.Diagnostics;
using System.IO.Pipelines;
using Passenger.Core;

namespace Passenger.App.Debugging;

/// <summary>The far end of a session's conversation with its debug adapter: the streams the protocol goes over, the words
/// the adapter writes beside it, its end, and how to end it when it will not go. The adapter is a process of its own
/// (<see cref="ProcessEnd"/>, Passenger's engine) or one inside the IDE (<see cref="InProcessEnd"/>, a Godot game's
/// GDScript, which the game's own connection carries).</summary>
public abstract class AdapterEnd
{
    /// <summary>The adapter's process id, or zero for one inside the IDE.</summary>
    public abstract int Id { get; }

    /// <summary>What the log and the protocol's traces call it: "adapter 4211".</summary>
    public abstract string Name { get; }

    /// <summary>Which it is, as an error of its says: "process 4211", or its name.</summary>
    public virtual string Described => Name;

    /// <summary>What the session writes to the adapter.</summary>
    public abstract Stream ToAdapter { get; }

    /// <summary>What the session reads from the adapter.</summary>
    public abstract Stream FromAdapter { get; }

    /// <summary>The lines the adapter writes beside the protocol (a process's standard error), heard to their end.</summary>
    public abstract Task HearErrors(Action<string> errors);

    /// <summary>Done with the adapter's exit code once it has ended, however soon after its start that was.</summary>
    public abstract Task<int> Exited { get; }

    /// <summary>Whether the adapter has ended within a limit; called off the window's thread.</summary>
    public abstract Task<bool> EndsWithinAsync(TimeSpan limit);

    /// <summary>Ends an adapter that would not go.</summary>
    public abstract void Kill();
}

/// <summary>An adapter in a process of its own, spoken to over its standard input and output.</summary>
public sealed class ProcessEnd : AdapterEnd
{
    private readonly Process process;
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProcessEnd(Process process)
    {
        this.process = process;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exited.TrySetResult(process.ExitCode);
        // Exited is raised once: an adapter that died as it started may have had it before this came to listen.
        if (process.HasExited)
        {
            exited.TrySetResult(process.ExitCode);
        }
    }

    public override int Id => process.Id;

    public override string Name => $"adapter {process.Id}";

    public override string Described => $"process {process.Id}";

    public override Stream ToAdapter => process.StandardInput.BaseStream;

    public override Stream FromAdapter => process.StandardOutput.BaseStream;

    public override Task HearErrors(Action<string> errors) => ChildProcesses.Hear(process, errors: errors);

    public override Task<int> Exited => exited.Task;

    public override Task<bool> EndsWithinAsync(TimeSpan limit) => Task.FromResult(process.WaitForExit(limit));

    public override void Kill() => ChildProcesses.Kill(process);
}

/// <summary>An adapter inside the IDE, the protocol between it and its session carried by two pipes in memory: the adapter
/// reads <see cref="AdapterInput"/> and writes <see cref="AdapterOutput"/>, and says it has ended with <see cref="End"/>.</summary>
public sealed class InProcessEnd : AdapterEnd
{
    private readonly Pipe toAdapter = new();
    private readonly Pipe fromAdapter = new();
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public InProcessEnd(string name)
    {
        Name = name;
        ToAdapter = toAdapter.Writer.AsStream();
        FromAdapter = fromAdapter.Reader.AsStream();
        AdapterInput = toAdapter.Reader.AsStream();
        AdapterOutput = new Written(fromAdapter.Writer.AsStream(), () => ended);
    }

    public override int Id => 0;

    public override string Name { get; }

    public override Stream ToAdapter { get; }

    public override Stream FromAdapter { get; }

    /// <summary>What the adapter reads: the session's requests.</summary>
    public Stream AdapterInput { get; }

    /// <summary>Where the adapter writes its answers and events.</summary>
    public Stream AdapterOutput { get; }

    public override Task HearErrors(Action<string> errors) => Task.CompletedTask;

    public override Task<int> Exited => exited.Task;

    public override async Task<bool> EndsWithinAsync(TimeSpan limit)
    {
        await Task.WhenAny(exited.Task, Task.Delay(limit)).ConfigureAwait(false);
        return exited.Task.IsCompleted;
    }

    /// <summary>Whether the adapter has ended (<see cref="End"/>), from before its output closes: nothing more is to be written.</summary>
    public bool HasEnded => ended;

    private volatile bool ended;

    /// <summary>The adapter has ended: its output closes, and the session hears it gone.</summary>
    public void End(int code = 0)
    {
        ended = true;
        fromAdapter.Writer.Complete();
        toAdapter.Reader.Complete();
        exited.TrySetResult(code);
    }

    public override void Kill() => End(-1);

    /// <summary>What the adapter writes to its session: once the adapter has ended (an answer it finishes after its session's
    /// disconnect), a write fails as one to a process's closed pipe does, with an <see cref="IOException"/>, never the
    /// pipe's own error.</summary>
    private sealed class Written(Stream pipe, Func<bool> ended) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            try
            {
                pipe.Write(buffer);
            }
            catch (InvalidOperationException completed) when (ended())
            {
                throw new IOException("The debug adapter has ended", completed);
            }
        }

        public override void Flush()
        {
            try
            {
                pipe.Flush();
            }
            catch (InvalidOperationException completed) when (ended())
            {
                throw new IOException("The debug adapter has ended", completed);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                pipe.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
