using System.Net.Sockets;
using System.Text;
using Passenger.App.Containers;
using Passenger.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Passenger.App.Database;

/// <summary>A host key the person trusted for a tunnel's host, as ssh writes its fingerprint ("SHA256:…").</summary>
public sealed record TrustedHostKey(string Host, int Port, string Fingerprint);

/// <summary>An SSH tunnel a connection reaches its server through: the host (a name ssh's configuration may know), its port
/// and user where given (else the configuration's, else 22 and the person's own name), a private key file, and the host
/// keys the person trusted for it and the hosts it jumps through.</summary>
public sealed record SshTunnel(string Host, int Port, string User)
{
    public string? KeyFile { get; init; }
    public IReadOnlyList<TrustedHostKey> HostKeys { get; init; } = [];

    public bool Equals(SshTunnel? other) =>
        other is not null && Host == other.Host && Port == other.Port && User == other.User && KeyFile == other.KeyFile && HostKeys.SequenceEqual(other.HostKeys);

    public override int GetHashCode() => HashCode.Combine(Host, Port, User, KeyFile, HostKeys.Count);
}

/// <summary>A host's key no one trusted yet, or one other than the key trusted or known for it.</summary>
public sealed class SshHostKeyException(string host, int port, string kind, string fingerprint, bool changed)
    : IOException(changed ? Refusals.SshHostKeyChanged(host, fingerprint) : Refusals.SshHostKeyUnknown(host))
{
    public string Host { get; } = host;
    public int Port { get; } = port;
    public string Kind { get; } = kind;
    public string Fingerprint { get; } = fingerprint;
    public bool Changed { get; } = changed;
}

/// <summary>Where ssh's files are read from: the person's home, or the folder <see cref="HomeVariable"/> names in its place
/// (a test run's), so a test never reads the person's configuration, keys or known hosts.</summary>
public static class SshFiles
{
    [IdentifierHome]
    public const string HomeVariable = "PASSENGER_SSH_HOME";

    /// <summary>Whether the person's own home is read, whose agent on Windows is OpenSSH's pipe.</summary>
    public static bool PersonsHome => Environment.GetEnvironmentVariable(HomeVariable) is not { Length: > 0 };

    public static string Home => Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home
        ? Path.GetFullPath(home) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>What ssh's configuration says of a host, read now.</summary>
    public static SshHost Resolve(string alias)
    {
        string home = Home;
        return SshConfig.Resolve(alias, Path.Combine(home, ".ssh", "config"), home, Environment.UserName, Read, Matching);
    }

    /// <summary>A file's text, or null where it cannot be read.</summary>
    public static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The files an Include's pattern names, its last part's * and ? matched, in order.</summary>
    private static IReadOnlyList<string> Matching(string pattern)
    {
        string? folder = Path.GetDirectoryName(pattern);
        string name = Path.GetFileName(pattern);
        if (folder is null || !Directory.Exists(folder))
        {
            return [];
        }
        return [.. Directory.EnumerateFiles(folder).Where(f => SshConfig.Wildcard(Path.GetFileName(f), name)).Order(StringComparer.Ordinal)];
    }
}

/// <summary>The SSH tunnels connections reach their servers through: one forward a connection and server, from a port of
/// loopback's to the server as the last host reaches it, through each host ProxyJump names first, opened as a connection
/// first needs it and kept until the window lets the connection go. Each host is trusted by the key the person trusted for
/// it or the key ssh's known hosts know, and is signed in to with the agent's keys, key files and, on the tunnel's own
/// host, its password. A tunnel that cannot open is an <see cref="IOException"/> saying why, as a connection's own failure is.</summary>
internal static class SshTunnels
{
    private sealed record Key(Guid Connection, SshTunnel Tunnel, string Host, int Port);

    /// <summary>A forward open: its clients, each through the one before, and their forwards, the last to the server.</summary>
    private sealed record Forward(List<SshClient> Clients, List<ForwardedPortLocal> Ports)
    {
        public bool Live => Clients.All(c => c.IsConnected) && Ports.All(p => p.IsStarted);

        public uint Bound => Ports[^1].BoundPort;

        public void Close()
        {
            for (int i = Clients.Count - 1; i >= 0; i--)
            {
                Clients[i].Dispose();
            }
            Ports.ForEach(p => p.Dispose());
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
                    return (int)kept.Bound;
                }
                Open.Remove(key);
            }
            kept?.Close();
            string? secret = SystemCredentials.Read(DatabaseController.CredentialService, source.TunnelAccount);
            Forward opened = await RouteAsync(tunnel, secret, (host, port), cancel);
            lock (Open)
            {
                Open[key] = opened;
            }
            return (int)opened.Bound;
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

    /// <summary>Opens a tunnel's hosts and signs in to each, then lets them go: an <see cref="SshHostKeyException"/> names
    /// the first host whose key is not trusted, for the person to trust before the tunnel is kept.</summary>
    public static async Task CheckAsync(SshTunnel tunnel, string? secret, CancellationToken cancel) =>
        (await RouteAsync(tunnel, secret, null, cancel)).Close();

    /// <summary>The hosts a tunnel goes through, each as ssh's configuration names it: ProxyJump's in order, then its own.</summary>
    public static IReadOnlyList<SshHost> Hops(SshTunnel tunnel)
    {
        SshHost last = SshFiles.Resolve(tunnel.Host);
        last = last with { Port = tunnel.Port > 0 ? tunnel.Port : last.Port, User = tunnel.User is { Length: > 0 } named ? named : last.User };
        List<SshHost> hops = [];
        foreach (string written in last.Jumps)
        {
            (string host, string? user, int? port) = SshConfig.Jump(written);
            SshHost jump = SshFiles.Resolve(host);
            hops.Add(jump with { User = user ?? jump.User, Port = port ?? jump.Port });
        }
        hops.Add(last);
        return hops;
    }

    /// <summary>Signs in to each host in turn, each reached through a forward of the one before; the last forwards to
    /// <paramref name="server"/> where one is given.</summary>
    private static async Task<Forward> RouteAsync(SshTunnel tunnel, string? secret, (string Host, int Port)? server, CancellationToken cancel)
    {
        if (secret is { Length: > 0 })
        {
            ContainerSecrets.Mark(secret);
        }
        IReadOnlyList<SshHost> hops = await BackgroundWork.Run(() => Hops(tunnel), cancel);
        Forward route = new([], []);
        try
        {
            string address = hops[0].HostName;
            int port = hops[0].Port;
            for (int i = 0; i < hops.Count; i++)
            {
                SshHost hop = hops[i];
                bool last = i == hops.Count - 1;
                SshClient client = new(new ConnectionInfo(address, port, hop.User, await SignInAsync(hop, last ? tunnel.KeyFile : null, secret, last, cancel))
                {
                    Timeout = TimeSpan.FromSeconds(30),
                })
                {
                    // A keep-alive holds an idle tunnel open through a host that ends quiet clients.
                    KeepAliveInterval = TimeSpan.FromSeconds(30),
                };
                SshHostKeyException? refused = null;
                client.HostKeyReceived += (_, key) =>
                {
                    refused = Judged(hop, key, tunnel.HostKeys);
                    key.CanTrust = refused is null;
                };
                try
                {
                    await client.ConnectAsync(cancel);
                }
                catch (Exception failed) when (failed is SshException or SocketException or IOException or InvalidOperationException)
                {
                    client.Dispose();
                    throw refused ?? new IOException(Refusals.SshUnreachable(hop.HostName, ContainerSecrets.InSaid(failed.Message)), failed);
                }
                route.Clients.Add(client);
                (string Host, int Port)? next = last ? server : (hops[i + 1].HostName, hops[i + 1].Port);
                if (next is { } onward)
                {
                    ForwardedPortLocal forward = new("127.0.0.1", 0, onward.Host, (uint)onward.Port);
                    client.AddForwardedPort(forward);
                    route.Ports.Add(forward);
                    forward.Start();
                    address = "127.0.0.1";
                    port = (int)forward.BoundPort;
                }
            }
            return route;
        }
        catch (Exception failed) when (failed is not (IOException or OperationCanceledException))
        {
            route.Close();
            throw new IOException(Refusals.SshUnreachable(tunnel.Host, ContainerSecrets.InSaid(failed.Message)), failed);
        }
        catch (Exception)
        {
            route.Close();
            throw;
        }
    }

    /// <summary>Why a host's key is refused, or null where the person trusted it or ssh's known hosts know it.</summary>
    private static SshHostKeyException? Judged(SshHost hop, HostKeyEventArgs key, IReadOnlyList<TrustedHostKey> trusted)
    {
        string fingerprint = "SHA256:" + key.FingerPrintSHA256;
        string kind = KindOf(key.HostKey);
        TrustedHostKey? kept = trusted.FirstOrDefault(k => k.Host == hop.HostName && k.Port == hop.Port);
        if (kept?.Fingerprint == fingerprint)
        {
            return null;
        }
        List<KnownHostKey> said = [.. hop.KnownHostsFiles.Select(SshFiles.Read).OfType<string>().Select(text => KnownHosts.Judge(text, hop.KeyAlias, hop.Port, kind, key.HostKey))];
        // As ssh: a key revoked anywhere is refused, one known anywhere taken, one known otherwise refused as changed.
        return !said.Contains(KnownHostKey.Revoked) && said.Contains(KnownHostKey.Known) ? null
            : new SshHostKeyException(hop.HostName, hop.Port, kind, fingerprint, kept is not null || said.Contains(KnownHostKey.Changed) || said.Contains(KnownHostKey.Revoked));
    }

    /// <summary>A key blob's kind, its first field ("ssh-ed25519").</summary>
    private static string KindOf(byte[] blob) =>
        blob.Length >= 4 && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(blob) is var length and > 0 && length <= blob.Length - 4
            ? Encoding.ASCII.GetString(blob, 4, length) : "unknown";

    /// <summary>How a host is signed in to: the agent's keys (unless IdentitiesOnly), the tunnel's key file and the
    /// configuration's (or ssh's usual ones), the secret as each encrypted key's passphrase, and on the tunnel's own host
    /// alone the secret as its password, so no host it jumps through is given it.</summary>
    private static async Task<AuthenticationMethod[]> SignInAsync(SshHost hop, string? keyFile, string? secret, bool own, CancellationToken cancel)
    {
        List<IPrivateKeySource> keys = [];
        if (!hop.IdentitiesOnly && SshAgent.For(hop, SshFiles.PersonsHome) is { } agent)
        {
            await agent.ReadAsync(cancel);
            if (agent.HostKeyAlgorithms.Count > 0)
            {
                keys.Add(agent);
            }
        }
        foreach (string file in (keyFile is null ? hop.IdentityFiles : [keyFile, .. hop.IdentityFiles]).Distinct(PathNames.Comparer))
        {
            bool named = file == keyFile;
            if (!named && !File.Exists(file))
            {
                continue;
            }
            try
            {
                keys.Add(new PrivateKeyFile(file, secret is { Length: > 0 } ? secret : null));
            }
            catch (Exception unread) when (unread is SshException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                if (named)
                {
                    throw new IOException(Refusals.SshUnreachable(hop.HostName, $"{Path.GetFileName(file)}: {unread.Message}"), unread);
                }
                IdeLog.Write(IdeLog.Ide, $"SSH: {file} passed over for {hop.HostName}: {unread.Message}");
            }
        }
        List<AuthenticationMethod> methods = [];
        if (keys.Count > 0)
        {
            methods.Add(new PrivateKeyAuthenticationMethod(hop.User, [.. keys]));
        }
        if (own && secret is { Length: > 0 })
        {
            methods.Add(new PasswordAuthenticationMethod(hop.User, secret));
        }
        return methods.Count > 0 ? [.. methods] : throw new IOException(Refusals.SshUnreachable(hop.HostName, "there is no agent, key file or password to sign in with"));
    }
}
