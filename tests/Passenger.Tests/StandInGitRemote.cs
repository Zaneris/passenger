using System.Diagnostics;
using System.Globalization;
using System.Text;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A Git remote over HTTP that asks for a login, for a push that must reach no real host: the bare repository
/// <see cref="Repository"/> (the test makes it) served by git's own http-backend, behind a stand-in (<see cref="StandInHttp"/>)
/// that answers 401 to every request without its one user and password. Disposed as the stand-in is.</summary>
public sealed class StandInGitRemote : IDisposable, IAsyncDisposable
{
    public const string User = "tester";

    public const string Password = "stand-in-secret-7";

    private static readonly string Login = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));

    private readonly StandInHttp http;
    private readonly string root;
    private int signedIn;

    /// <param name="root">The folder <see cref="Repository"/> is in.</param>
    public StandInGitRemote(string root)
    {
        this.root = root;
        http = new StandInHttp(ServeAsync);
    }

    /// <summary>The bare repository served, repo.git in the folder given.</summary>
    public string Repository => Path.Combine(root, "repo.git");

    public string Url => $"{http.Root}/repo.git";

    /// <summary>Its host and port, as a question about it names it.</summary>
    public string Place => $"127.0.0.1:{http.Port}";

    /// <summary>The requests that came with the user and password.</summary>
    public int SignedIn => Volatile.Read(ref signedIn);

    public void Dispose() => http.Dispose();

    public ValueTask DisposeAsync() => http.DisposeAsync();

    private async Task ServeAsync(StandInHttp.Exchange exchange)
    {
        if (exchange.Header("Authorization") != Login)
        {
            await exchange.ReplyAsync(401, "text/plain", "A login is needed", "WWW-Authenticate: Basic realm=\"stand-in\"\r\n");
            return;
        }
        Interlocked.Increment(ref signedIn);
        int query = exchange.Path.IndexOf('?', StringComparison.Ordinal);
        ProcessStartInfo start = ChildProcesses.Command("git", ["http-backend"], root);
        start.Environment["GIT_PROJECT_ROOT"] = root;
        start.Environment["GIT_HTTP_EXPORT_ALL"] = "1";
        start.Environment["GATEWAY_INTERFACE"] = "CGI/1.1";
        start.Environment["SERVER_PROTOCOL"] = "HTTP/1.1";
        start.Environment["REQUEST_METHOD"] = exchange.Method;
        start.Environment["PATH_INFO"] = query < 0 ? exchange.Path : exchange.Path[..query];
        start.Environment["QUERY_STRING"] = query < 0 ? string.Empty : exchange.Path[(query + 1)..];
        start.Environment["CONTENT_TYPE"] = exchange.Header("Content-Type") ?? string.Empty;
        start.Environment["CONTENT_LENGTH"] = exchange.Body.Length.ToString(CultureInfo.InvariantCulture);
        foreach ((string header, string variable) in (ReadOnlySpan<(string, string)>)[("Content-Encoding", "HTTP_CONTENT_ENCODING"), ("Git-Protocol", "HTTP_GIT_PROTOCOL")])
        {
            if (exchange.Header(header) is { } value)
            {
                start.Environment[variable] = value;
            }
        }
        // A push is taken only from a user the server signed in.
        start.Environment["REMOTE_USER"] = User;
        start.Environment["REMOTE_ADDR"] = "127.0.0.1";
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using Process backend = await ChildProcesses.StartAsync(start);
        using MemoryStream output = new();
        Task copied = backend.StandardOutput.BaseStream.CopyToAsync(output, exchange.Ending);
        Task<string> errors = backend.StandardError.ReadToEndAsync(exchange.Ending);
        await backend.StandardInput.BaseStream.WriteAsync(exchange.Body, exchange.Ending);
        backend.StandardInput.Close();
        await backend.WaitForExitAsync(exchange.Ending);
        await copied;
        string said = await errors;
        byte[] answer = output.ToArray();
        int end = answer.AsSpan().IndexOf("\r\n\r\n"u8);
        Assert.True(end >= 0, $"git http-backend answered {exchange.Line} with no CGI head: {said}");
        int status = 200;
        string? type = null;
        StringBuilder headers = new();
        foreach (string line in Encoding.ASCII.GetString(answer, 0, end).Split("\r\n"))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            string name = line[..colon];
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("Status", StringComparison.OrdinalIgnoreCase))
            {
                status = int.Parse(value.Split(' ')[0], CultureInfo.InvariantCulture);
            }
            else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                type = value;
            }
            else
            {
                headers.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
            }
        }
        await exchange.ReplyAsync(status, type, answer[(end + 4)..], headers.ToString());
    }
}
