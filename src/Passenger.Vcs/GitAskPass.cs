using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>What git, or the ssh it runs, asks while it reaches a remote.</summary>
public enum GitQuestionKind
{
    /// <summary>A user name for a URL, which the login's password follows.</summary>
    User,

    /// <summary>A password: for a URL, or ssh's for a user at a host.</summary>
    Password,

    /// <summary>The passphrase of an ssh key.</summary>
    Passphrase,

    /// <summary>A yes or no: ssh's whether to trust a host it has not met.</summary>
    YesNo,

    /// <summary>Anything else, in git's or ssh's own words.</summary>
    Other,
}

/// <summary>A question git or ssh asks while it reaches a remote, as its askpass helper is given it: its kind, its words,
/// and what it is about (a login's URL, with the user where git names one; a key's file; ssh's host). Read from git's and
/// ssh's English words: a prompt in another language is <see cref="GitQuestionKind.Other"/>, asked in its own words.</summary>
public sealed partial record GitQuestion(GitQuestionKind Kind, string Prompt, string? About = null, string? User = null)
{
    public static GitQuestion Of(string prompt)
    {
        string asked = prompt.Trim();
        if (Login().Match(asked) is { Success: true } login)
        {
            string url = login.Groups["url"].Value;
            return login.Groups["what"].Value == "Username"
                ? new GitQuestion(GitQuestionKind.User, prompt, url)
                : new GitQuestion(GitQuestionKind.Password, prompt, url, UserOf(url));
        }
        if (KeyPassphrase().Match(asked) is { Success: true } key)
        {
            return new GitQuestion(GitQuestionKind.Passphrase, prompt, key.Groups["key"].Value);
        }
        if (SshPassword().Match(asked) is { Success: true } ssh)
        {
            return new GitQuestion(GitQuestionKind.Password, prompt, ssh.Groups["host"].Value, ssh.Groups["user"].Value);
        }
        if (asked.Contains("(yes/no", StringComparison.Ordinal))
        {
            return new GitQuestion(GitQuestionKind.YesNo, prompt, Host().Match(asked) is { Success: true } host ? host.Groups["host"].Value : null);
        }
        return new GitQuestion(GitQuestionKind.Other, prompt);
    }

    /// <summary>Where it asks for, as a person names it: a URL's host and port, a key's file name, ssh's host.</summary>
    public string Place => Kind switch
    {
        GitQuestionKind.Passphrase => Path.GetFileName(About ?? string.Empty),
        _ when About is { } about && about.IndexOf("://", StringComparison.Ordinal) is int scheme and >= 0 => HostOf(about[(scheme + 3)..]),
        _ => About ?? "the remote",
    };

    /// <summary>What it asks for, as a sentence names it ("the login to github.com").</summary>
    public string Asks => Kind switch
    {
        GitQuestionKind.User => $"the login to {Place}",
        GitQuestionKind.Password => User is { } user ? $"the password of {user} at {Place}" : $"the password for {Place}",
        GitQuestionKind.Passphrase => $"the passphrase of key {Place}",
        GitQuestionKind.YesNo => $"whether to trust {Place}",
        _ => $"an answer to \"{Prompt.Trim()}\"",
    };

    /// <summary>Whether its answer is a secret, typed unseen and masked wherever it would be shown.</summary>
    public bool IsSecret => Kind is GitQuestionKind.Password or GitQuestionKind.Passphrase or GitQuestionKind.Other;

    /// <summary>The URL git names in the password it asks for next, once the user named is given for this one's URL; null
    /// where this asks for no user.</summary>
    public string? WithUser(string user) =>
        Kind == GitQuestionKind.User && About is { } url && url.IndexOf("://", StringComparison.Ordinal) is int scheme and >= 0
            ? $"{url[..(scheme + 3)]}{user}@{url[(scheme + 3)..]}"
            : null;

    /// <summary>The user a URL names before its host (git's "http://user@host"), or null.</summary>
    private static string? UserOf(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        string rest = scheme < 0 ? url : url[(scheme + 3)..];
        int path = rest.IndexOf('/', StringComparison.Ordinal);
        int at = (path < 0 ? rest : rest[..path]).LastIndexOf('@');
        return at > 0 ? rest[..at] : null;
    }

    /// <summary>A URL's host and port, from what follows its scheme: its user and path left off.</summary>
    private static string HostOf(string rest)
    {
        int path = rest.IndexOf('/', StringComparison.Ordinal);
        string authority = path < 0 ? rest : rest[..path];
        return authority[(authority.LastIndexOf('@') + 1)..];
    }

    [GeneratedRegex(@"^(?<what>Username|Password) for '(?<url>[^']+)':$")]
    private static partial Regex Login();

    [GeneratedRegex(@"^Enter passphrase for key '(?<key>.+)':$")]
    private static partial Regex KeyPassphrase();

    [GeneratedRegex(@"^(?<user>[^@\s']+)@(?<host>[^@\s']+)'s password:$")]
    private static partial Regex SshPassword();

    [GeneratedRegex(@"host '(?<host>[^' ]+)")]
    private static partial Regex Host();
}

/// <summary>An answer to a question git asked: what git is given, and with a user name the password for the same place,
/// given as git asks for it next; or why there is none (<see cref="Refused"/>), which the operation's failure says.</summary>
public sealed record GitAnswer(string? Text, string? Password = null, string? Refused = null)
{
    public static GitAnswer No(string why) => new(null, null, why);
}

/// <summary>Who answers what git asks while it reaches a remote, and how git reaches them: <see cref="Helper"/>, the
/// program git runs to ask (Passenger's own executable), answers over a socket the operation opens in
/// <see cref="SocketFolder"/> (one only the user reaches), asking <see cref="Ask"/> in turn.</summary>
public sealed record GitAsker(string Helper, string SocketFolder, Func<GitQuestion, CancellationToken, Task<GitAnswer>> Ask);

/// <summary>Git's askpass helper, both ends. Git runs with GIT_ASKPASS and SSH_ASKPASS naming Passenger's executable and
/// <see cref="Variable"/> the operation's socket (<see cref="Prompting.Variables"/>); run with a prompt, the executable sends it
/// there and writes the answer for git (<see cref="Answer"/>), or exits 1, which git takes as no answer. The helper sends
/// the prompt and ends its side; the answer comes back after a byte saying whether there is one. A secret goes only over
/// the socket and git's pipe, never on a command line or into a file.</summary>
public static class GitAskPass
{
    /// <summary>The variable naming the socket of the operation git asks for.</summary>
    [IdentifierHome]
    public const string Variable = "PASSENGER_GIT_ASKPASS";

    internal const byte Given = (byte)'+';
    internal const byte None = (byte)'-';

    /// <summary>The longest prompt read: ssh's question about a host is a few lines.</summary>
    internal const int Longest = 64 * 1024;

    /// <summary>What Main asks before anything starts: where git runs this executable to ask (<see cref="Variable"/> set, one prompt given), the
    /// answer written for git, and the exit code; else null, and Passenger starts as itself.</summary>
    public static int? Answer(string[] args)
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } socket || args.Length != 1)
        {
            return null;
        }
        try
        {
            using Socket asking = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            asking.Connect(new UnixDomainSocketEndPoint(socket));
            asking.Send(Encoding.UTF8.GetBytes(args[0]));
            asking.Shutdown(SocketShutdown.Send);
            using MemoryStream reply = new();
            byte[] buffer = new byte[4096];
            int read;
            while ((read = asking.Receive(buffer)) > 0)
            {
                reply.Write(buffer, 0, read);
            }
            if (reply.Length == 0 || reply.GetBuffer()[0] != Given)
            {
                return 1;
            }
            using Stream output = Console.OpenStandardOutput();
            output.Write(reply.GetBuffer().AsSpan(1, (int)reply.Length - 1));
            output.Write("\n"u8);
            return 0;
        }
        catch (Exception failed) when (failed is SocketException or IOException)
        {
            Console.Error.WriteLine($"Passenger could not ask for git: {failed.Message}");
            return 1;
        }
    }
}

/// <summary>An operation's socket for git's helper while git runs (<see cref="GitAskPass"/>): each question put to
/// <see cref="GitAsker.Ask"/> in turn, a login's password kept for git's next question for the same place and then
/// forgotten, the operation's background work let go while the person answers. The first question left unanswered is
/// <see cref="Refused"/>. Disposed as git ends: a question still asked is taken away, and the socket goes.</summary>
internal sealed class Prompting : IAsyncDisposable
{
    private static int opened;

    private readonly GitAsker asker;
    private readonly Socket listener;
    private readonly string path;
    private readonly Func<IDisposable> apart;
    private readonly CancellationTokenSource ending;
    private readonly Task accepting;

    /// <summary>The password git asks for next, by the URL it will name, once a login's user is given.</summary>
    private (string Url, string Password)? next;

    private volatile string? refused;

    private Prompting(GitAsker asker, Socket listener, string path, Func<IDisposable> apart, CancellationToken cancel)
    {
        this.asker = asker;
        this.listener = listener;
        this.path = path;
        this.apart = apart;
        ending = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        // Not held: it waits for git's questions while git runs, which the operation's own hold covers.
        accepting = BackgroundWork.Unheld(AcceptAsync);
    }

    /// <summary>The socket opened for an operation, letting go of its background work through <paramref name="apart"/>
    /// while a question waits; null where it cannot be, when git asks nobody.</summary>
    public static Prompting? Open(GitAsker asker, Func<IDisposable> apart, CancellationToken cancel)
    {
        if (!File.Exists(asker.Helper))
        {
            return null;
        }
        string path = Path.Combine(asker.SocketFolder, $"{Environment.ProcessId}-git-{Interlocked.Increment(ref opened)}");
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            // Only a dead Passenger that had this process's number leaves one by this name.
            File.Delete(path);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(2);
        }
        catch (Exception unopened) when (unopened is SocketException or IOException or UnauthorizedAccessException)
        {
            listener.Dispose();
            return null;
        }
        return new Prompting(asker, listener, path, apart, cancel);
    }

    /// <summary>What git runs with to ask through the socket: its own questions and ssh's, ssh's even where a terminal
    /// would do.</summary>
    public IReadOnlyDictionary<string, string> Variables => new Dictionary<string, string>
    {
        ["GIT_ASKPASS"] = asker.Helper,
        ["SSH_ASKPASS"] = asker.Helper,
        ["SSH_ASKPASS_REQUIRE"] = "force",
        [GitAskPass.Variable] = path,
    };

    /// <summary>Why the first question left unanswered was, in the asker's words; null while each was answered.</summary>
    public string? Refused => refused;

    public async ValueTask DisposeAsync()
    {
        await ending.CancelAsync();
        listener.Dispose();
        try
        {
            File.Delete(path);
        }
        catch (Exception kept) when (kept is IOException or UnauthorizedAccessException)
        {
            // Left for the next start to take away, as a dead process's socket is.
        }
        await accepting;
        ending.Dispose();
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
            catch (Exception ended) when (ended is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            await ServeAsync(client);
        }
    }

    private async Task ServeAsync(Socket client)
    {
        try
        {
            await using NetworkStream stream = new(client, ownsSocket: true);
            using MemoryStream prompt = new();
            byte[] buffer = new byte[4096];
            int read;
            // Read to its end, past the longest kept: a socket closed with input unread resets on Windows, losing the answer.
            while ((read = await stream.ReadAsync(buffer, ending.Token)) > 0)
            {
                prompt.Write(buffer, 0, (int)Math.Min(read, Math.Max(0, GitAskPass.Longest - prompt.Length)));
            }
            GitAnswer answer = await AnswerAsync(GitQuestion.Of(Encoding.UTF8.GetString(prompt.GetBuffer(), 0, (int)prompt.Length)));
            byte[] reply = answer.Text is { } text ? [GitAskPass.Given, .. Encoding.UTF8.GetBytes(text)] : [GitAskPass.None];
            await stream.WriteAsync(reply, ending.Token);
            client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception gone) when (gone is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The helper went (git ended), or the operation did.
        }
    }

    private async Task<GitAnswer> AnswerAsync(GitQuestion question)
    {
        (string Url, string Password)? kept = next;
        next = null;
        if (question.Kind == GitQuestionKind.Password && kept is { } login && login.Url == question.About)
        {
            return new GitAnswer(login.Password);
        }
        IDisposable away = apart();
        Task<GitAnswer> asking = asker.Ask(question, ending.Token);
        // Taken again on the thread giving the answer, so no wait finds the work quiet before git goes on.
        Task back = asking.ContinueWith(_ => away.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        GitAnswer answer;
        try
        {
            answer = await asking;
        }
        catch (OperationCanceledException) when (ending.IsCancellationRequested)
        {
            answer = GitAnswer.No($"{question.Asks} was not given before git ended");
        }
        await back;
        if (answer.Text is null)
        {
            refused ??= answer.Refused ?? $"{question.Asks} was not given";
        }
        else if (answer.Password is { } password && question.WithUser(answer.Text) is { } url)
        {
            next = (url, password);
        }
        return answer;
    }
}

/// <summary>The background work an operation holds while git runs (<see cref="BackgroundWork.Scope"/>), let go while a
/// question waits on the person, which no screenshot or test waits for, and taken again once it is answered.</summary>
internal sealed class GitHold : IDisposable
{
    private readonly Lock gate = new();
    private readonly string file;
    private readonly int line;
    private IDisposable? held;
    private bool ended;

    /// <summary>Held from now, named by the git call that holds it.</summary>
    public GitHold([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        this.file = file;
        this.line = line;
        held = BackgroundWork.Scope(file, line);
    }

    /// <summary>Lets go until what it returns is disposed.</summary>
    public IDisposable Apart()
    {
        lock (gate)
        {
            held?.Dispose();
            held = null;
        }
        return new Again(this);
    }

    public void Dispose()
    {
        lock (gate)
        {
            ended = true;
            held?.Dispose();
            held = null;
        }
    }

    private void Take()
    {
        lock (gate)
        {
            if (!ended && held is null)
            {
                held = BackgroundWork.Scope(file, line);
            }
        }
    }

    private sealed class Again(GitHold hold) : IDisposable
    {
        public void Dispose() => hold.Take();
    }
}
