using System.Net.Sockets;
using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>How a launch's program is known to be up before its browser opens: the address's host takes a connection on
/// its port, tried while the program runs, for about seven minutes; a container's service answers HTTP there, as the
/// engine takes a connection on a published port from the moment its container starts. Nothing the program writes is read,
/// so a program that says nothing, or says it in another language, opens it the same, and the page never opens on a
/// refused connection.</summary>
public static class BrowserLaunch
{
    /// <summary>The platform's most tries before the page is given up.</summary>
    private const int Tries = 100;

    /// <summary>How long a try waits for its connection.</summary>
    private static readonly TimeSpan Try = TimeSpan.FromSeconds(1);

    /// <summary>A launch's browser URL as an address the browser opens (http or https), a host on every interface as
    /// <see cref="WebAddress.Callable"/> makes it; null for none or another kind.</summary>
    public static Uri? Address(string? url) =>
        url is not null && Uri.TryCreate(WebAddress.Callable(url), UriKind.Absolute, out Uri? address) && address.Scheme is "http" or "https" ? address : null;

    /// <summary>What is said when the program never listened at the address.</summary>
    public static string NotResponding(Uri address) => $"Unable to open {address} in the browser because the URL does not respond";

    /// <summary>How long a service is given to answer before its page is given up, on a developer's machine.</summary>
    public static readonly TimeSpan AnswerLimit = TimeSpan.FromMinutes(5);

    /// <summary>What is said while the browser waits for a service to answer, after what was said before where something was.</summary>
    public static string Waiting(string? said, string service, Uri address) =>
        $"{(said is { Length: > 0 } ? $"{said}; waiting" : "Waiting")} for {service} to answer at {address} before opening the browser…";

    /// <summary>What is said when a service never answered within the limit.</summary>
    public static string NotAnswered(string service, Uri address, TimeSpan limit) =>
        $"Unable to open {address} in the browser: {service} did not answer there within "
        + (limit.TotalSeconds % 60 == 0 ? Plural.Of((long)limit.TotalMinutes, "minute") : Plural.Of((long)Math.Ceiling(limit.TotalSeconds), "second"));

    /// <summary>The client a wait for an answer asks through, which takes a service's development certificate unchecked
    /// (nothing is sent but the request, and nothing read but that an answer came) and takes a redirect as an answer.</summary>
    private static readonly HttpClient Asking = OutboundHttp.Client(System.Threading.Timeout.InfiniteTimeSpan, 64 * 1024, anyCertificate: true, followsRedirects: false);

    /// <summary>Waits for a service to answer HTTP at the address, any status at all: true once it does, false when it has not
    /// within the limit, null when the wait was ended first (the run ended). A request that the port takes and lets go
    /// unanswered, as an engine does with nothing listening behind it, is tried again; one under way is waited for, never
    /// sent again, so a program held at a breakpoint is asked once.</summary>
    public static async Task<bool?> AnsweringAsync(Uri address, TimeSpan limit, CancellationToken ended)
    {
        using CancellationTokenSource within = CancellationTokenSource.CreateLinkedTokenSource(ended);
        within.CancelAfter(limit);
        try
        {
            for (int tried = 1; ; tried++)
            {
                using HttpRequestMessage request = OutboundHttp.Request(HttpMethod.Head, address);
                request.Headers.ConnectionClose = true;
                try
                {
                    using HttpResponseMessage answer = await Asking.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, within.Token);
                    return true;
                }
                catch (HttpRequestException)
                {
                    // Taken and let go, or refused: nothing answers there yet.
                }
                await Task.Delay(Pause(tried), within.Token);
            }
        }
        catch (OperationCanceledException) when (!ended.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The pause after a try, longer as tries go by.</summary>
    private static TimeSpan Pause(int tried) => tried < 10 ? TimeSpan.FromMilliseconds(500) : tried < 20 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);

    /// <summary>Waits for something to listen at the address's host and port: true once it does, false when the tries run
    /// out, null when the wait was ended first (the program ended).</summary>
    public static async Task<bool?> ListeningAsync(Uri address, CancellationToken ended)
    {
        string host = address.IdnHost;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), ended);
            for (int tried = 1; tried <= Tries; tried++)
            {
                using TcpClient client = new();
                using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(ended);
                attempt.CancelAfter(Try);
                try
                {
                    await client.ConnectAsync(host, address.Port, attempt.Token);
                    return true;
                }
                catch (Exception refused) when (refused is SocketException || (refused is OperationCanceledException && !ended.IsCancellationRequested))
                {
                    // Not listening yet.
                }
                await Task.Delay(Pause(tried), ended);
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
