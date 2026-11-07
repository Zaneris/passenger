using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Passenger.App.State;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The askpass a test's ssh-keygen asks through: Passenger's own executable as git's helper, forced (SSH_ASKPASS,
/// SSH_ASKPASS_REQUIRE), so no ssh-keygen reads a passphrase from a console or terminal, answering from a socket this
/// serves, each connection apart, so a failed one leaves no later question unanswered. Each question takes the next of
/// the answers given; one past them, or an exchange that fails, is answered as none and cancels <see cref="Unanswering"/>,
/// so the ssh-keygen, which would ask again or wait for good, is ended at once.</summary>
public sealed class SshKeygenQuestions : IAsyncDisposable
{
    private static int opened;

    private readonly Lock gate = new();
    private readonly Socket listener;
    private readonly string socket;
    private readonly Queue<string> answers;
    private readonly List<string> asked = [];
    private readonly List<Task> serving = [];
    private readonly CancellationTokenSource unanswering = new();
    private readonly CancellationTokenSource ending = new();
    private readonly Task accepting;
    private string? unanswered;

    private SshKeygenQuestions(Socket listener, string socket, IEnumerable<string> answers)
    {
        this.listener = listener;
        this.socket = socket;
        this.answers = new Queue<string>(answers);
        accepting = Waiting.Apart(AcceptAsync);
    }

    /// <summary>A socket of its own, listening, answering in turn with <paramref name="answers"/>.</summary>
    public static SshKeygenQuestions Open(IEnumerable<string> answers)
    {
        string socket = Path.Combine(SocketFolders.Own(StateStore.DefaultRoot), $"{Environment.ProcessId}-keygen-{Interlocked.Increment(ref opened)}");
        File.Delete(socket);
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socket));
        listener.Listen(4);
        return new SshKeygenQuestions(listener, socket, answers);
    }

    /// <summary>Cancelled once ssh-keygen asked what is not answered, or an exchange with its helper failed.</summary>
    public CancellationToken Unanswering => unanswering.Token;

    /// <summary>Why ssh-keygen was left unanswered, or null while every question was answered.</summary>
    public string? Unanswered
    {
        get
        {
            lock (gate)
            {
                return unanswered;
            }
        }
    }

    /// <summary>The questions heard so far, in ssh-keygen's words.</summary>
    public IReadOnlyList<string> Asked
    {
        get
        {
            lock (gate)
            {
                return [.. asked];
            }
        }
    }

    /// <summary>What ssh-keygen asked, as a failure's words name it.</summary>
    public string Said => Asked is [_, ..] heard
        ? $"having asked through the test's askpass {string.Join(", ", heard.Select(question => $"\"{question}\""))}"
        : "having asked nothing through the test's askpass";

    /// <summary>The ssh-keygen's start given the helper, forced, and this socket.</summary>
    public void Give(ProcessStartInfo start)
    {
        start.Environment["SSH_ASKPASS"] = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Passenger.exe" : "Passenger");
        start.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        start.Environment[Passenger.Vcs.GitAskPass.Variable] = socket;
    }

    public async ValueTask DisposeAsync()
    {
        await ending.CancelAsync();
        await accepting;
        listener.Dispose();
        Task[] served;
        lock (gate)
        {
            served = [.. serving];
        }
        await Task.WhenAll(served);
        File.Delete(socket);
        ending.Dispose();
        unanswering.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ending.Token);
            }
            catch (OperationCanceledException) when (ending.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException failed)
            {
                // Winsock refuses an accept whose helper went before it was taken, its question lost with it.
                await UnansweredAsync($"a connection of its askpass helper failed before it was taken ({failed.SocketErrorCode}: {failed.Message})");
                return;
            }
            lock (gate)
            {
                serving.Add(ServeAsync(client));
            }
        }
    }

    private async Task ServeAsync(Socket client)
    {
        try
        {
            await using NetworkStream stream = new(client, ownsSocket: true);
            using MemoryStream prompt = new();
            // Read to its end: a socket closed with input unread resets on Windows, losing the answer.
            await stream.CopyToAsync(prompt, ending.Token);
            string question = Encoding.UTF8.GetString(prompt.GetBuffer(), 0, (int)prompt.Length).Trim();
            string? answer;
            lock (gate)
            {
                asked.Add(question);
                answer = unanswered is null && answers.TryDequeue(out string? next) ? next : null;
            }
            await stream.WriteAsync(answer is null ? "-"u8.ToArray() : Encoding.UTF8.GetBytes("+" + answer), ending.Token);
            client.Shutdown(SocketShutdown.Send);
            if (answer is null)
            {
                await UnansweredAsync($"it asked \"{question}\", which the test does not answer");
            }
        }
        catch (OperationCanceledException) when (ending.IsCancellationRequested)
        {
        }
        catch (Exception failed) when (failed is IOException or SocketException)
        {
            await UnansweredAsync($"an exchange with its askpass helper failed ({failed.Message})");
        }
    }

    private Task UnansweredAsync(string why)
    {
        lock (gate)
        {
            unanswered ??= why;
        }
        return unanswering.CancelAsync();
    }
}

/// <summary>What one ssh-keygen a test ran came to: its result, null where the test ended it; the questions it asked
/// through the test's askpass; and why it was ended at once, where it asked what the test does not answer.</summary>
public sealed record SshKeygenRun(ChildResult? Result, IReadOnlyList<string> Asked, string? Unanswered)
{
    /// <summary>Its result, where it asked nothing left unanswered and succeeded; else the test fails, saying that
    /// ssh-keygen <paramref name="failed"/> and how it ended.</summary>
    public ChildResult Succeeded(string failed)
    {
        Assert.True(Unanswered is null && Result is { Succeeded: true }, $"ssh-keygen {failed}: {this}");
        return Result!;
    }

    public override string ToString()
    {
        string ended = Unanswered is { } why ? $"it was ended at once: {why}"
            : Result is { } result ? $"it exited {result.ExitCode}: {result.Error}{result.Output}".TrimEnd()
            : "it was ended";
        string heard = Asked is [_, ..] ? string.Join(", ", Asked.Select(question => $"\"{question}\"")) : "nothing";
        return $"{ended}; it asked {heard} through the test's askpass";
    }
}
