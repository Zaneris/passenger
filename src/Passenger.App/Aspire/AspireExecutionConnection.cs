using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Passenger.App.Aspire;

internal sealed class AspireExecutionConnection : IAsyncDisposable
{
    private const int MaximumHeaders = 16 * 1024;
    private const int MaximumFields = 32;
    private const int MaximumBody = 1024 * 1024;
    private const int Pending = 0;
    private const int Committing = 1;
    private const int Committed = 2;
    private const int Aborted = 3;

    /// <summary>How long a connection whose answer has gone waits for its peer to close, dropping what it still sends.</summary>
    private static readonly TimeSpan Lingering = TimeSpan.FromSeconds(2);

    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly byte[] buffer = new byte[4096];
    private readonly CancellationTokenSource aborted;
    private readonly Dictionary<string, List<string>> headers = new(StringComparer.OrdinalIgnoreCase);
    private int offset;
    private int available;
    private int headerBytes;
    private int headerFields;
    private int contentLength;
    private bool chunked;
    private bool expectContinue;
    private bool upgraded;
    private CancellationTokenSource? monitoring;
    private Task? monitor;
    // Pending -> committing -> committed. Only a pending create belongs to the HTTP peer's lifetime.
    private int creationState = Pending;

    public AspireExecutionConnection(TcpClient client, CancellationToken shutdown)
    {
        this.client = client;
        stream = client.GetStream();
        aborted = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    }

    public string Method { get; private set; } = "";
    public string Path { get; private set; } = "";
    public string? Version { get; private set; }
    public CancellationToken RequestAborted => aborted.Token;
    public int StatusCode { get; set; } = 200;
    public Dictionary<string, string> ResponseHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasResponded { get; private set; }
    public IReadOnlyList<string> Header(string name) => headers.TryGetValue(name, out List<string>? values) ? values : [];

    public async Task ReadHeadAsync()
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            string line = await ReadLineAsync(4094, timeout.Token, 414).ConfigureAwait(false);
            string[] parts = line.Split(' ');
            if (parts.Length != 3 || parts[0].Length == 0 || !parts[0].All(IsToken) || parts[2] != "HTTP/1.1" ||
                !parts[1].StartsWith('/') || parts[1].Contains('#') || parts[1].Any(character => character is < '!' or > '~'))
            {
                throw new RequestException(400);
            }
            Method = parts[0];
            int query = parts[1].IndexOf('?');
            string path = query < 0 ? parts[1] : parts[1][..query];
            Path = Uri.UnescapeDataString(path.Replace("%2F", "%252F", StringComparison.OrdinalIgnoreCase));
            if (Path.Any(character => character is < ' ' or '\u007f'))
            {
                throw new RequestException(400);
            }
            if (query >= 0)
            {
                string[] versions = parts[1][(query + 1)..].Split('&').Select(value => value.Split('=', 2))
                    .Where(pair => DecodeQuery(pair[0]).Equals("api-version", StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Length == 2 ? DecodeQuery(pair[1]) : "").ToArray();
                Version = versions.Length == 1 ? versions[0] : null;
            }
            await ReadFieldsAsync(false, timeout.Token).ConfigureAwait(false);
            if (Header("Host").Count != 1 || string.IsNullOrWhiteSpace(Header("Host")[0]) || Header("Host")[0].Contains(','))
            {
                throw new RequestException(400);
            }
            IReadOnlyList<string> lengths = Header("Content-Length");
            IReadOnlyList<string> encodings = Header("Transfer-Encoding");
            if (lengths.Count > 1 || (lengths.Count != 0 && encodings.Count != 0))
            {
                throw new RequestException(400);
            }
            if (lengths.Count == 1)
            {
                string length = lengths[0];
                if (length.Length == 0 || !length.All(char.IsAsciiDigit) ||
                    !long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
                {
                    throw new RequestException(400);
                }
                if (parsed > MaximumBody)
                {
                    throw new RequestException(413);
                }
                contentLength = (int)parsed;
            }
            if (encodings.Count != 0)
            {
                if (encodings.Count != 1 || !encodings[0].Equals("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    throw new RequestException(400);
                }
                chunked = true;
            }
            IReadOnlyList<string> expectations = Header("Expect");
            if (expectations.Count != 0)
            {
                if (expectations.Count != 1 || !expectations[0].Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                {
                    throw new RequestException(417);
                }
                expectContinue = true;
            }
        }
        catch (OperationCanceledException) when (!RequestAborted.IsCancellationRequested)
        {
            throw new RequestException(408);
        }
    }

    private static string DecodeQuery(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    public bool HasJsonContentType()
    {
        IReadOnlyList<string> values = Header("Content-Type");
        if (values.Count != 1)
        {
            return false;
        }
        string mediaType = values[0].Split(';', 2)[0].Trim();
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            (mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase) && mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The loopback DCP protocol allows ten seconds total for Expect continuation, body framing, payload and trailers.</summary>
    /// <remarks>This total deadline is not a data-rate limit and ends before pending-create monitoring or session creation.</remarks>
    public async Task<byte[]> ReadBodyAsync()
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await ReadBodyCoreAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !RequestAborted.IsCancellationRequested)
        {
            throw new RequestException(408);
        }
    }

    private async Task<byte[]> ReadBodyCoreAsync(CancellationToken cancellationToken)
    {
        if (expectContinue)
        {
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        if (!chunked)
        {
            byte[] body = new byte[contentLength];
            await ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
            return body;
        }
        using MemoryStream result = new();
        int framingBytes = 0;
        while (true)
        {
            string line = await ReadLineAsync(4096, cancellationToken).ConfigureAwait(false);
            framingBytes += line.Length + 2;
            if (framingBytes > MaximumHeaders)
            {
                throw new RequestException(400);
            }
            int separator = line.IndexOf(';');
            string size = separator < 0 ? line : line[..separator];
            if (size.Length == 0 || !size.All(char.IsAsciiHexDigit) ||
                !ulong.TryParse(size, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong count) ||
                (separator >= 0 && !ValidChunkExtensions(line[separator..])))
            {
                throw new RequestException(400);
            }
            if (count > (ulong)(MaximumBody - result.Length))
            {
                throw new RequestException(413);
            }
            if (count == 0)
            {
                await ReadFieldsAsync(true, cancellationToken).ConfigureAwait(false);
                return result.ToArray();
            }
            byte[] data = new byte[(int)count];
            await ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            result.Write(data);
            byte[] ending = new byte[2];
            await ReadExactlyAsync(ending, cancellationToken).ConfigureAwait(false);
            if (ending[0] != '\r' || ending[1] != '\n')
            {
                throw new RequestException(400);
            }
        }
    }

    private static bool ValidChunkExtensions(string value)
    {
        int index = 0;
        while (index < value.Length)
        {
            if (value[index++] != ';')
            {
                return false;
            }
            int start = index;
            while (index < value.Length && IsToken(value[index]))
            {
                index++;
            }
            if (index == start)
            {
                return false;
            }
            if (index == value.Length || value[index] == ';')
            {
                continue;
            }
            if (value[index++] != '=' || index == value.Length)
            {
                return false;
            }
            if (value[index] != '"')
            {
                start = index;
                while (index < value.Length && IsToken(value[index]))
                {
                    index++;
                }
                if (index == start)
                {
                    return false;
                }
                continue;
            }
            index++;
            bool closed = false;
            while (index < value.Length)
            {
                char character = value[index++];
                if (character == '"')
                {
                    closed = true;
                    break;
                }
                if (character == '\\')
                {
                    if (index == value.Length)
                    {
                        return false;
                    }
                    character = value[index++];
                }
                if (character is < ' ' or > '~')
                {
                    return false;
                }
            }
            if (!closed)
            {
                return false;
            }
        }
        return true;
    }

    private async Task ReadFieldsAsync(bool trailers, CancellationToken cancellationToken)
    {
        while (true)
        {
            string line = await ReadLineAsync(MaximumHeaders, cancellationToken, 431).ConfigureAwait(false);
            headerBytes += line.Length + 2;
            if (headerBytes > MaximumHeaders)
            {
                throw new RequestException(431);
            }
            if (line.Length == 0)
            {
                return;
            }
            if (++headerFields > MaximumFields)
            {
                throw new RequestException(431);
            }
            int colon = line.IndexOf(':');
            if (colon <= 0 || !line[..colon].All(IsToken) || line[(colon + 1)..].Any(character => (character < ' ' && character != '\t') || character == '\u007f'))
            {
                throw new RequestException(400);
            }
            string name = line[..colon];
            if (trailers)
            {
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Microsoft-Developer-DCP-Instance-ID", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Expect", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
                {
                    throw new RequestException(400);
                }
                continue;
            }
            if (!headers.TryGetValue(name, out List<string>? values))
            {
                values = [];
                headers.Add(name, values);
            }
            values.Add(line[(colon + 1)..].Trim(' ', '\t'));
        }
    }

    private static bool IsToken(char character) => char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character);

    private async Task<string> ReadLineAsync(int maximum, CancellationToken cancellationToken, int limitStatus = 400)
    {
        using MemoryStream line = new();
        while (true)
        {
            int value = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (value == '\r')
            {
                if (await ReadByteAsync(cancellationToken).ConfigureAwait(false) != '\n')
                {
                    throw new RequestException(400);
                }
                return Encoding.Latin1.GetString(line.GetBuffer(), 0, (int)line.Length);
            }
            if (value == '\n')
            {
                throw new RequestException(400);
            }
            if (line.Length >= maximum)
            {
                throw new RequestException(limitStatus);
            }
            line.WriteByte((byte)value);
        }
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset == available)
        {
            available = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            offset = 0;
            if (available == 0)
            {
                throw new RequestException(400);
            }
        }
        return buffer[offset++];
    }

    private async Task ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (destination.Length > 0)
        {
            int read = await ReadAsync(destination, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new RequestException(400);
            }
            destination = destination[read..];
        }
    }

    private ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset == available)
        {
            return stream.ReadAsync(destination, cancellationToken);
        }
        int count = Math.Min(destination.Length, available - offset);
        buffer.AsMemory(offset, count).CopyTo(destination);
        offset += count;
        return ValueTask.FromResult(count);
    }

    public void MonitorPendingCreate()
    {
        monitoring = CancellationTokenSource.CreateLinkedTokenSource(RequestAborted);
        monitor = MonitorAsync(monitoring.Token);
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            byte[] next = new byte[1];
            await ReadAsync(next, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        if (Interlocked.CompareExchange(ref creationState, Aborted, Pending) == Pending)
        {
            await aborted.CancelAsync().ConfigureAwait(false);
        }
    }

    public async Task CommitCreateAsync(string location, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref creationState, Committing, Pending) != Pending)
        {
            throw new OperationCanceledException(RequestAborted);
        }
        await StopMonitorAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        StatusCode = 201;
        ResponseHeaders["Location"] = location;
        await WriteResponseAsync([], cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref creationState, Committed);
    }

    private async Task StopMonitorAsync()
    {
        if (monitoring is null)
        {
            return;
        }
        await monitoring.CancelAsync().ConfigureAwait(false);
        if (monitor is not null)
        {
            await monitor.ConfigureAwait(false);
        }
    }

    public bool IsWebSocketRequest()
    {
        IReadOnlyList<string> keys = Header("Sec-WebSocket-Key");
        Span<byte> decoded = stackalloc byte[16];
        return Method == "GET" && !chunked && contentLength == 0 &&
            HasToken("Connection", "Upgrade") && HasToken("Upgrade", "websocket") &&
            Header("Sec-WebSocket-Version").Count == 1 && Header("Sec-WebSocket-Version")[0] == "13" &&
            keys.Count == 1 && keys[0].Length == 24 && Convert.TryFromBase64String(keys[0], decoded, out int count) && count == 16;
    }

    private bool HasToken(string name, string token)
    {
        string[] tokens = Header(name).SelectMany(value => value.Split(',')).Select(value => value.Trim()).ToArray();
        return tokens.All(value => value.Length != 0 && value.All(IsToken)) && tokens.Contains(token, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The connection upgraded to a WebSocket, answering with <paramref name="subProtocol"/> where the peer asked for one.</summary>
    public async Task<WebSocket> AcceptWebSocketAsync(string? subProtocol = null)
    {
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(Header("Sec-WebSocket-Key")[0] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        HasResponded = true;
        upgraded = true;
        string protocol = subProtocol is null ? "" : $"Sec-WebSocket-Protocol: {subProtocol}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n{protocol}\r\n"),
            RequestAborted).ConfigureAwait(false);
        return WebSocket.CreateFromStream(new UpgradeStream(this), true, subProtocol, TimeSpan.FromSeconds(30));
    }

    public Task WriteJsonAsync<T>(T value, JsonSerializerOptions options)
    {
        ResponseHeaders["Content-Type"] = "application/json; charset=utf-8";
        return WriteResponseAsync(JsonSerializer.SerializeToUtf8Bytes(value, options));
    }

    public async Task WriteResponseAsync(byte[] body, CancellationToken cancellationToken = default)
    {
        if (HasResponded)
        {
            return;
        }
        HasResponded = true;
        string reason = StatusCode switch
        {
            200 => "OK", 201 => "Created", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized",
            403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 408 => "Request Timeout", 409 => "Conflict", 413 => "Content Too Large",
            414 => "URI Too Long", 415 => "Unsupported Media Type", 417 => "Expectation Failed",
            429 => "Too Many Requests", 431 => "Request Header Fields Too Large", _ => "Internal Server Error"
        };
        StringBuilder head = new($"HTTP/1.1 {StatusCode} {reason}\r\nConnection: close\r\nContent-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n");
        foreach ((string name, string value) in ResponseHeaders)
        {
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        head.Append("\r\n");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(RequestAborted, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), timeout.Token).ConfigureAwait(false);
        if (body.Length != 0)
        {
            await stream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
        }
    }

    public void Abort() => client.Dispose();

    /// <summary>Ends the connection once its answer has gone: the sending side shut, so the peer reads the answer to its end,
    /// then what the peer still sends read and dropped until it closes, for <see cref="Lingering"/> at most. Closed with its
    /// request unread, the connection would be reset, and Windows then drops the answer the peer has not read yet.</summary>
    public async Task CloseAsync()
    {
        if (!HasResponded || upgraded || RequestAborted.IsCancellationRequested)
        {
            return;
        }
        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(RequestAborted);
            limit.CancelAfter(Lingering);
            int dropped = 0;
            while (dropped < MaximumBody)
            {
                int read = await stream.ReadAsync(buffer, limit.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }
                dropped += read;
            }
        }
        catch (Exception gone) when (gone is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The peer went or kept on past the limit: the close that follows is all that is left.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopMonitorAsync().ConfigureAwait(false);
        }
        finally
        {
            client.Dispose();
            monitoring?.Dispose();
            aborted.Dispose();
        }
    }

    internal sealed class RequestException(int statusCode) : Exception
    {
        public int StatusCode { get; } = statusCode;
    }

    private sealed class UpgradeStream(AspireExecutionConnection connection) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => connection.stream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => connection.stream.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => connection.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => connection.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => connection.stream.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => connection.stream.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => connection.stream.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                connection.Abort();
            }
            base.Dispose(disposing);
        }
    }
}
