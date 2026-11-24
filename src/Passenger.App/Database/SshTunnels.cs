using System.Net.Sockets;
using Passenger.App.Containers;
using Passenger.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Passenger.App.Database;

/// <summary>An SSH tunnel a connection reaches its server through: the bastion's host, port and user, a private key file
/// (else a password), and the host key the person trusted, as ssh writes its fingerprint ("SHA256:…").</summary>
public sealed record SshTunnel(string Host, int Port, string User)
{
    public string? KeyFile { get; init; }
    public string? HostKey { get; init; }
}

/// <summary>The SSH tunnels connections reach their servers through: one forward a connection and server, from a port of
/// loopback's to the server as the bastion reaches it, opened as a connection first needs it and kept until the window
/// lets the connection go. The bastion is trusted only by the host key the person trusted, and its password or key's
/// passphrase is read from the system's credential store. A tunnel that cannot open is an <see cref="IOException"/>
/// saying why, as a connection's own failure is.</summary>
internal static class SshTunnels
{
    private sealed record Key(Guid Connection, SshTunnel Tunnel, string Host, int Port);

    /// <summary>A forward open, its client and port.</summary>
    private sealed record Forward(SshClient Client, ForwardedPortLocal Port)
    {
        public bool Live => Client.IsConnected && Port.IsStarted;

        public void Close()
        {
            Port.Dispose();
            Client.Dispose();
        }
    }

    /// <summary>One tunnel opened at a time, so two reads of a connection at once share one.</summary>
    private static readonly SemaphoreSlim Opening = new(1, 1);
    private static readonly Dictionary<Key, Forward> Open = [];

    /// <summary>The loopback port a connection's server is reached at through its tunnel, opening the tunnel where none is open.</summary>
    public static async Task<int> PortAsync(DatabaseSource source, SshTunnel tunnel, string host, int port, CancellationToken cancel)
    {
        Key key = new(source.Id, tunnel, host, port);
        await Opening.WaitAsync(cancel);
        try
        {
            Forward? kept;
            lock (Open)
            {
                if (Open.TryGetValue(key, out kept) && kept.Live)
                {
                    return (int)kept.Port.BoundPort;
                }
                Open.Remove(key);
            }
            kept?.Close();
            Forward opened = await ForwardAsync(source, tunnel, host, port, cancel);
            lock (Open)
            {
                Open[key] = opened;
            }
            return (int)opened.Port.BoundPort;
        }
        finally
        {
            Opening.Release();
        }
    }

    /// <summary>Closes a connection's tunnels, as its window lets it go or it changes.</summary>
    public static void Close(IReadOnlyCollection<Guid> connections)
    {
        List<Forward> closing = [];
        lock (Open)
        {
            foreach (Key key in Open.Keys.Where(k => connections.Contains(k.Connection)).ToList())
            {
                closing.Add(Open[key]);
                Open.Remove(key);
            }
        }
        if (closing.Count > 0)
        {
            // A client's close says goodbye over the network, off the window's thread.
            UnawaitedWork.Dismiss(BackgroundWork.Run(() => closing.ForEach(f => f.Close())));
        }
    }

    /// <summary>A bastion's host key, its kind and fingerprint as ssh writes them, read as it greets a client, which
    /// signs in as nobody: for the person to trust before a tunnel uses it.</summary>
    public static async Task<(string Kind, string Fingerprint)> HostKeyAsync(string host, int port, CancellationToken cancel)
    {
        (string Kind, string Fingerprint)? seen = null;
        using SshClient client = new(new ConnectionInfo(host, port, "passenger", new NoneAuthenticationMethod("passenger")) { Timeout = TimeSpan.FromSeconds(30) });
        client.HostKeyReceived += (_, key) =>
        {
            seen = (key.HostKeyName, "SHA256:" + key.FingerPrintSHA256);
            key.CanTrust = false;
        };
        try
        {
            await client.ConnectAsync(cancel);
        }
        catch (Exception refused) when (seen is not null && refused is SshException or SocketException or IOException)
        {
            IdeLog.Write(IdeLog.Ide, $"SSH: {host}:{port}'s host key read; the client left as it greeted.");
        }
        catch (Exception failed) when (failed is SshException or SocketException or IOException or InvalidOperationException)
        {
            throw new IOException(Refusals.SshUnreachable(host, failed.Message), failed);
        }
        return seen ?? throw new IOException(Refusals.SshUnreachable(host, "it gave no host key"));
    }

    private static async Task<Forward> ForwardAsync(DatabaseSource source, SshTunnel tunnel, string host, int port, CancellationToken cancel)
    {
        string? secret = SystemCredentials.Read(DatabaseController.CredentialService, source.TunnelAccount);
        if (secret is { Length: > 0 })
        {
            ContainerSecrets.Mark(secret);
        }
        SshClient client;
        try
        {
            AuthenticationMethod signIn = tunnel.KeyFile is { Length: > 0 } file
                ? new PrivateKeyAuthenticationMethod(tunnel.User, new PrivateKeyFile(file, secret is { Length: > 0 } ? secret : null))
                : new PasswordAuthenticationMethod(tunnel.User, secret ?? string.Empty);
            // A keep-alive holds an idle tunnel open through a bastion that ends quiet clients.
            client = new SshClient(new ConnectionInfo(tunnel.Host, tunnel.Port, tunnel.User, signIn) { Timeout = TimeSpan.FromSeconds(30) }) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        }
        catch (Exception unread) when (unread is SshException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new IOException(Refusals.SshUnreachable(tunnel.Host, unread.Message), unread);
        }
        string? seen = null;
        client.HostKeyReceived += (_, key) =>
        {
            seen = "SHA256:" + key.FingerPrintSHA256;
            key.CanTrust = string.Equals(seen, tunnel.HostKey, StringComparison.Ordinal);
        };
        try
        {
            await client.ConnectAsync(cancel);
            ForwardedPortLocal forward = new("127.0.0.1", 0, host, (uint)port);
            client.AddForwardedPort(forward);
            forward.Start();
            return new Forward(client, forward);
        }
        catch (Exception failed) when (failed is SshException or SocketException or IOException or InvalidOperationException)
        {
            client.Dispose();
            throw new IOException(seen is not null && !string.Equals(seen, tunnel.HostKey, StringComparison.Ordinal)
                ? tunnel.HostKey is null ? Refusals.SshHostKeyUnknown(tunnel.Host) : Refusals.SshHostKeyChanged(tunnel.Host, seen)
                : Refusals.SshUnreachable(tunnel.Host, ContainerSecrets.InSaid(failed.Message)), failed);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
    }
}
