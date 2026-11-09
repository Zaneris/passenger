using System.Net.Sockets;
using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>How a launch's program is known to be up before its browser opens: the address's host takes a connection on
/// its port, tried while the program runs, for about seven minutes. Nothing it writes is read, so a program that says
/// nothing, or says it in another language, opens it the same, and the page never opens on a refused connection.</summary>
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
                await Task.Delay(tried < 10 ? TimeSpan.FromMilliseconds(500) : tried < 20 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5), ended);
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
