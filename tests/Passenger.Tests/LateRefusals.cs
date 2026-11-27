using System.Net;
using System.Net.Sockets;
using Passenger.App.LiveTree;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A stand-in between Passenger and adb's forward to an app's inspector on a device, as adb on a loaded machine is:
/// each connection the app's socket does not take ends only a second after adb ends it, nothing said back; one it takes
/// passes through as it is. Put in place of where Passenger reaches the inspector (<see cref="LiveApp.ReachedThrough"/>)
/// until disposed.</summary>
internal sealed class LateRefusals : IDisposable
{
    private static readonly TimeSpan Late = TimeSpan.FromSeconds(1);

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource ending = new();
    private readonly List<Task> passing = [];
    private int late;

    public LateRefusals()
    {
        listener.Start();
        LiveApp.ReachedThrough = Through;
    }

    /// <summary>How many connections adb ended with nothing said back, each ended here a second later.</summary>
    public int Refused => Volatile.Read(ref late);

    private EndPoint Through(EndPoint at)
    {
        lock (passing)
        {
            passing.Add(Waiting.Apart(() => AcceptAsync(at)));
        }
        return listener.LocalEndpoint;
    }

    private async Task AcceptAsync(EndPoint at)
    {
        while (!ending.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptSocketAsync(ending.Token);
            }
            catch (Exception gone) when (gone is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            lock (passing)
            {
                passing.Add(Waiting.Apart(() => PassAsync(client, at)));
            }
        }
    }

    /// <summary>One connection passed through to adb's, its end there passed back at once where adb said something, else a
    /// second later.</summary>
    private async Task PassAsync(Socket client, EndPoint at)
    {
        using Socket held = client;
        using Socket adb = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await adb.ConnectAsync(at, ending.Token);
            Task up = PumpAsync(client, adb, passEnd: true);
            if (await PumpAsync(adb, client, passEnd: false) == 0)
            {
                Interlocked.Increment(ref late);
                await Waiting.ForAsync(Late, "adb's end of a connection the app's socket did not take, passed on as late as a loaded machine passes it", ending.Token);
            }
            client.Shutdown(SocketShutdown.Both);
            await up;
        }
        catch (Exception gone) when (gone is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Ended with the stand-in, or by either end.
        }
    }

    /// <summary>What one end says written to the other until it ends, its end passed on with <paramref name="passEnd"/>; how
    /// many bytes it said.</summary>
    private async Task<long> PumpAsync(Socket from, Socket to, bool passEnd)
    {
        byte[] buffer = new byte[4096];
        long said = 0;
        try
        {
            int read;
            while ((read = await from.ReceiveAsync(buffer, SocketFlags.None, ending.Token)) > 0)
            {
                said += read;
                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, ending.Token);
            }
            if (passEnd)
            {
                to.Shutdown(SocketShutdown.Send);
            }
        }
        catch (Exception gone) when (gone is SocketException or ObjectDisposedException)
        {
            // The other end went first.
        }
        return said;
    }

    public void Dispose()
    {
        LiveApp.ReachedThrough = null;
        ending.Cancel();
        listener.Stop();
        Task[] left;
        lock (passing)
        {
            left = [.. passing];
        }
        Ui.Await(Task.WhenAll(left).Ended(), "the stand-in's connections to end", TimeSpan.FromSeconds(30));
        ending.Dispose();
    }
}
