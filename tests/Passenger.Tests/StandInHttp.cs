using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Passenger.Tests;

/// <summary>An HTTP/1.1 server on a loopback port of its own, for a test that must reach no real host: each request is read
/// whole (its head however long, then the body its Content-Length gives) and handed to the test's handler, which answers it.
/// A connection takes one request and closes, unless the server keeps connections alive, when it takes each one the client
/// sends until the client closes. Disposed, it stops listening, cancels the answers under way and waits for them.</summary>
public sealed class StandInHttp : IDisposable
{
    private static readonly byte[] HeadEnd = "\r\n\r\n"u8.ToArray();

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource end = new();
    private readonly Func<Exchange, Task> handler;
    private readonly bool keepAlive;
    private readonly Task accepting;
    private int connections;
    private int open;

    public StandInHttp(Func<Exchange, Task> handler, bool keepAlive = false)
    {
        this.handler = handler;
        this.keepAlive = keepAlive;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Root = $"http://127.0.0.1:{Port}";
        accepting = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    /// <summary>Its address, with no slash after the port.</summary>
    public string Root { get; }

    /// <summary>The connections clients have made to it.</summary>
    public int Connections => Volatile.Read(ref connections);

    /// <summary>The connections still open.</summary>
    public int Open => Volatile.Read(ref open);

    public void Dispose()
    {
        end.Cancel();
        listener.Stop();
        accepting.Wait(TimeSpan.FromSeconds(10));
        end.Dispose();
    }

    /// <summary>A status's reason phrase, the one table every answer takes it from.</summary>
    public static string Reason(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        302 => "Found",
        307 => "Temporary Redirect",
        401 => "Unauthorized",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "Status",
    };

    private async Task AcceptAsync()
    {
        List<Task> answering = [];
        try
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(end.Token);
                int connection = Interlocked.Increment(ref connections);
                Interlocked.Increment(ref open);
                answering.Add(Task.Run(() => ServeAsync(client, connection)));
            }
        }
        catch (Exception stopped) when (stopped is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Disposed, before the loop first listened too, as a test that skips at once has it.
        }
        await Task.WhenAll(answering);
    }

    private async Task ServeAsync(TcpClient client, int connection)
    {
        try
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                List<byte> read = [];
                do
                {
                    if (await RequestAsync(stream, read, connection) is not { } exchange)
                    {
                        return;
                    }
                    await handler(exchange);
                }
                while (keepAlive);
            }
        }
        catch (Exception gone) when (gone is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The client or the stand-in went first.
        }
        finally
        {
            Interlocked.Decrement(ref open);
        }
    }

    /// <summary>The next request on a connection, from what was read past the last one and what comes; null once the client
    /// closes before a whole head.</summary>
    private async Task<Exchange?> RequestAsync(NetworkStream stream, List<byte> read, int connection)
    {
        byte[] buffer = new byte[16 * 1024];
        int headEnd;
        while ((headEnd = IndexOf(read, HeadEnd)) < 0)
        {
            int more = await stream.ReadAsync(buffer, end.Token);
            if (more == 0)
            {
                return null;
            }
            read.AddRange(buffer.AsSpan(0, more));
        }
        string head = Encoding.ASCII.GetString([.. read.Take(headEnd + HeadEnd.Length)]);
        read.RemoveRange(0, headEnd + HeadEnd.Length);
        string[] lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon]] = line[(colon + 1)..].Trim();
            }
        }
        int length = headers.TryGetValue("Content-Length", out string? given) ? int.Parse(given, System.Globalization.CultureInfo.InvariantCulture) : 0;
        while (read.Count < length)
        {
            int more = await stream.ReadAsync(buffer, end.Token);
            if (more == 0)
            {
                return null;
            }
            read.AddRange(buffer.AsSpan(0, more));
        }
        byte[] body = [.. read.Take(length)];
        read.RemoveRange(0, length);
        string[] request = lines[0].Split(' ');
        return new Exchange(connection, request[0], request[1], head, headers, body, stream, keepAlive, end.Token);
    }

    private static int IndexOf(List<byte> read, byte[] wanted)
    {
        for (int at = 0; at <= read.Count - wanted.Length; at++)
        {
            int matched = 0;
            while (matched < wanted.Length && read[at + matched] == wanted[matched])
            {
                matched++;
            }
            if (matched == wanted.Length)
            {
                return at;
            }
        }
        return -1;
    }

    /// <summary>One request and the connection it came on, for the handler to answer.</summary>
    public sealed class Exchange
    {
        private readonly bool keepAlive;

        internal Exchange(int connection, string method, string path, string head, IReadOnlyDictionary<string, string> headers, byte[] body,
            NetworkStream stream, bool keepAlive, CancellationToken ending)
        {
            Connection = connection;
            Method = method;
            Path = path;
            Head = head;
            Headers = headers;
            Body = body;
            Stream = stream;
            this.keepAlive = keepAlive;
            Ending = ending;
        }

        /// <summary>The connection's number, from one, in the order clients made them.</summary>
        public int Connection { get; }

        public string Method { get; }

        /// <summary>The request's target as sent: its path and query.</summary>
        public string Path { get; }

        /// <summary>The request's method and target.</summary>
        public string Line => $"{Method} {Path}";

        /// <summary>The head as it came, its request line first and the blank line after it.</summary>
        public string Head { get; }

        public IReadOnlyDictionary<string, string> Headers { get; }

        public byte[] Body { get; }

        /// <summary>The connection, for an answer the handler sends in parts.</summary>
        public NetworkStream Stream { get; }

        /// <summary>Cancelled as the stand-in is disposed.</summary>
        public CancellationToken Ending { get; }

        public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;

        /// <summary>An answer whole: its head (no Content-Type when there is no type; the extra header lines given, each ending
        /// in CRLF) and its body.</summary>
        public async Task ReplyAsync(int status, string? type, byte[] body, string headers = "")
        {
            await HeadAsync(status, type, body.Length, headers);
            await Stream.WriteAsync(body, Ending);
        }

        public Task ReplyAsync(int status, string? type, string body, string headers = "") => ReplyAsync(status, type, Encoding.UTF8.GetBytes(body), headers);

        /// <summary>An answer's head alone, for a body the handler writes itself.</summary>
        public async Task HeadAsync(int status, string? type, long length, string headers = "")
        {
            string head = $"HTTP/1.1 {status} {Reason(status)}\r\n" + (type is null ? string.Empty : $"Content-Type: {type}\r\n")
                + $"Content-Length: {length}\r\n{headers}" + (keepAlive ? string.Empty : "Connection: close\r\n") + "\r\n";
            await Stream.WriteAsync(Encoding.ASCII.GetBytes(head), Ending);
        }

        /// <summary>No answer: the request held until the client gives up and closes, or the stand-in goes.</summary>
        public async Task UntilClosedAsync()
        {
            byte[] buffer = new byte[4096];
            while (await Stream.ReadAsync(buffer, Ending) > 0)
            {
            }
        }
    }
}
