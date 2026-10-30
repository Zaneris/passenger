using System.Security.Cryptography;
using System.Text;
using Passenger.Core;

namespace Passenger.App.Updates;

/// <summary>A release's files as an update downloads them: over HTTPS (http only to a loopback address), each a GET as
/// <see cref="OutboundHttp"/> makes it, each file's SHA-256 taken as it comes.</summary>
public static partial class Releases
{
    /// <summary>How long a download may go without a byte before it is given up: a connection that stalled, not one that
    /// is slow.</summary>
    public static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);

    /// <summary>The most a text file of the release (SHA256SUMS, an update feed) may be: some hundred bytes a line.</summary>
    private const int TextLimit = 1024 * 1024;

    /// <summary>A small text file of the release, whole. Throws <see cref="UpdateFailedException"/> when it does not come and
    /// <see cref="UpdateRefusedException"/> when it would not come over HTTPS or is far larger than such a file is.</summary>
    public static async Task<string> ReadTextAsync(ReleaseFile file, CancellationToken cancel)
    {
        using MemoryStream text = new();
        await DownloadAsync(file, text, TextLimit, progress: null, cancel).ConfigureAwait(false);
        return Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length);
    }

    /// <summary>Downloads a release's file to a path, reporting the percent done, and gives its SHA-256 in lowercase hex. The
    /// file is the caller's to remove when this throws.</summary>
    public static async Task<string> DownloadAsync(ReleaseFile file, string path, Action<int>? progress, CancellationToken cancel)
    {
        await using FileStream into = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        return await DownloadAsync(file, into, long.MaxValue, progress, cancel).ConfigureAwait(false);
    }

    private static async Task<string> DownloadAsync(ReleaseFile file, Stream into, long limit, Action<int>? progress, CancellationToken cancel)
    {
        if (!Secure(file.Url))
        {
            throw new UpdateRefusedException($"{file.Name} would come over {file.Url.Scheme}, not HTTPS ({file.Url})");
        }
        using IncrementalHash sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            using HttpRequestMessage request = OutboundHttp.Request(HttpMethod.Get, file.Url);
            using HttpResponseMessage response = await Stalling(cancel,
                token => Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)).ConfigureAwait(false);
            // A redirect GitHub gives is followed, but never from HTTPS to http, which the handler refuses.
            if (!Secure(response.RequestMessage?.RequestUri ?? file.Url))
            {
                throw new UpdateRefusedException($"{file.Name} was sent on to {response.RequestMessage?.RequestUri}, not over HTTPS");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateFailedException($"{file.Name}: {file.Url} answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            long length = response.Content.Headers.ContentLength ?? file.Size;
            if (length > limit)
            {
                throw new UpdateRefusedException($"{file.Name} is {length} bytes, far more than such a file is");
            }
            await using Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            byte[] buffer = new byte[81920];
            long done = 0;
            int shown = -1;
            while (true)
            {
                int read = await Stalling(cancel, token => body.ReadAsync(buffer, token).AsTask()).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                done += read;
                if (done > limit)
                {
                    throw new UpdateRefusedException($"{file.Name} goes on past {limit} bytes, far more than such a file is");
                }
                sha256.AppendData(buffer, 0, read);
                await into.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                int percent = length > 0 ? (int)Math.Min(99, done * 100 / length) : 0;
                if (percent != shown)
                {
                    shown = percent;
                    progress?.Invoke(percent);
                }
            }
        }
        catch (HttpRequestException error)
        {
            throw new UpdateFailedException($"{file.Name}: {error.Message}", error);
        }
        catch (IOException error)
        {
            throw new UpdateFailedException($"{file.Name}: {error.Message}", error);
        }
        progress?.Invoke(100);
        return Convert.ToHexStringLower(sha256.GetHashAndReset());
    }

    /// <summary>HTTPS, or http to this machine alone.</summary>
    private static bool Secure(Uri url) => url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback);

    /// <summary>A step of a download, given up when no byte comes for <see cref="Stall"/>.</summary>
    private static async Task<T> Stalling<T>(CancellationToken cancel, Func<CancellationToken, Task<T>> step)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(Stall);
        try
        {
            return await step(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new UpdateFailedException(FormattableString.Invariant($"nothing came for {Stall.TotalSeconds:0} s"));
        }
    }
}

/// <summary>An update not installed because what came is not what the release says it is: the person is told why in a
/// line, and the log has the detail (the two checksums, what a tool said).</summary>
public sealed class UpdateRefusedException(string reason, string? detail = null) : Exception(reason)
{
    public string? Detail { get; } = detail;
}

/// <summary>An update that could not be downloaded or made ready: no network, an error, a full disk.</summary>
public sealed class UpdateFailedException : Exception
{
    public UpdateFailedException(string reason)
        : base(reason)
    {
    }

    public UpdateFailedException(string reason, Exception inner)
        : base(reason, inner)
    {
    }
}
