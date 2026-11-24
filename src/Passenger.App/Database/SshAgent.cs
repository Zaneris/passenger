using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using Passenger.Core;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace Passenger.App.Database;

/// <summary>The person's ssh-agent, asked as ssh asks it (draft-miller-ssh-agent): the keys it holds, and a signature of
/// one of them, its private part never leaving the agent. It is reached at a Unix socket, or on Windows at OpenSSH's
/// named pipe; a connection is made for each question.</summary>
internal sealed class SshAgent(string where) : IPrivateKeySource
{
    private const byte Failure = 5;
    private const byte AskKeys = 11;
    private const byte Keys = 12;
    private const byte AskSignature = 13;
    private const byte Signature = 14;

    /// <summary>The signature flags asking an RSA key for SHA-256 and SHA-512 signatures.</summary>
    private const uint RsaSha256 = 2;
    private const uint RsaSha512 = 4;

    private IReadOnlyCollection<HostAlgorithm> held = [];

    /// <summary>The agent a host's configuration names (IdentityAgent), else SSH_AUTH_SOCK's, else on Windows OpenSSH's
    /// own pipe where the person's own home is read; null for none.</summary>
    public static SshAgent? For(SshHost host, bool personsHome)
    {
        string? named = host.IdentityAgent switch
        {
            "none" => null,
            null or "SSH_AUTH_SOCK" => Environment.GetEnvironmentVariable("SSH_AUTH_SOCK"),
            ['$', .. var variable] => Environment.GetEnvironmentVariable(variable),
            var path => path,
        };
        if (named is { Length: > 0 })
        {
            return new SshAgent(named);
        }
        return host.IdentityAgent is null && personsHome && OperatingSystem.IsWindows() ? new SshAgent(@"\\.\pipe\openssh-ssh-agent") : null;
    }

    /// <summary>Its keys as signing algorithms, once <see cref="ReadAsync"/> asked for them.</summary>
    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms => held;

    /// <summary>Asks the agent for its keys; none where it cannot be reached.</summary>
    public async Task ReadAsync(CancellationToken cancel)
    {
        try
        {
            byte[] answer = await BackgroundWork.Run(() => Ask([AskKeys]), cancel);
            if (answer is not [Keys, ..])
            {
                return;
            }
            List<HostAlgorithm> keys = [];
            int at = 1;
            uint count = BinaryPrimitives.ReadUInt32BigEndian(answer.AsSpan(at));
            at += 4;
            for (uint i = 0; i < count; i++)
            {
                byte[] blob = Field(answer, ref at);
                Field(answer, ref at);
                string kind = Encoding.ASCII.GetString(Field(blob, 0));
                if (kind == "ssh-rsa")
                {
                    keys.Add(new AgentKey(this, "rsa-sha2-512", blob, RsaSha512));
                    keys.Add(new AgentKey(this, "rsa-sha2-256", blob, RsaSha256));
                }
                keys.Add(new AgentKey(this, kind, blob, 0));
            }
            held = keys;
        }
        catch (Exception unreached) when (unreached is IOException or SocketException or UnauthorizedAccessException or TimeoutException or ArgumentException)
        {
            IdeLog.Write(IdeLog.Ide, $"SSH: the agent at {where} gave no keys: {unreached.Message}");
        }
    }

    /// <summary>The agent's signature of <paramref name="data"/> by the key <paramref name="blob"/> names, as SSH's
    /// signature blob; asked on the SSH session's own thread as it signs in.</summary>
    private byte[] Sign(byte[] blob, byte[] data, uint flags)
    {
        using MemoryStream request = new();
        request.WriteByte(AskSignature);
        Write(request, blob);
        Write(request, data);
        Span<byte> written = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(written, flags);
        request.Write(written);
        byte[] answer = Ask(request.ToArray());
        if (answer is not [Signature, ..])
        {
            throw new IOException(answer is [Failure, ..] ? "The SSH agent refused to sign" : "The SSH agent's answer was not a signature");
        }
        int at = 1;
        return Field(answer, ref at);
    }

    /// <summary>One question and its answer, each a length and a message, on a connection of its own.</summary>
    private byte[] Ask(byte[] message)
    {
        using Stream stream = Open();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)message.Length);
        stream.Write(length);
        stream.Write(message);
        stream.Flush();
        stream.ReadExactly(length);
        uint size = BinaryPrimitives.ReadUInt32BigEndian(length);
        if (size is 0 or > 256 * 1024)
        {
            throw new IOException("The SSH agent's answer has no length a message holds");
        }
        byte[] answer = new byte[size];
        stream.ReadExactly(answer);
        return answer;
    }

    private Stream Open()
    {
        if (OperatingSystem.IsWindows() && where.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            NamedPipeClientStream pipe = new(".", where[@"\\.\pipe\".Length..], PipeDirection.InOut);
            pipe.Connect(5000);
            return pipe;
        }
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { ReceiveTimeout = 30000, SendTimeout = 30000 };
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(where));
        }
        catch (SocketException)
        {
            socket.Dispose();
            throw;
        }
        return new NetworkStream(socket, ownsSocket: true);
    }

    private static void Write(Stream stream, byte[] field)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)field.Length);
        stream.Write(length);
        stream.Write(field);
    }

    /// <summary>The string field at <paramref name="at"/>, moving past it.</summary>
    private static byte[] Field(byte[] message, ref int at)
    {
        if (message.Length - at < 4 || BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(at)) is var size && size > message.Length - at - 4)
        {
            throw new IOException("The SSH agent's answer was cut short");
        }
        byte[] field = message.AsSpan(at + 4, (int)size).ToArray();
        at += 4 + (int)size;
        return field;
    }

    private static byte[] Field(byte[] message, int at) => Field(message, ref at);

    /// <summary>A key the agent holds, signing by one of its algorithms (RSA's as SHA-2's).</summary>
    private sealed class AgentKey(SshAgent agent, string name, byte[] blob, uint flags) : HostAlgorithm(name)
    {
        public override byte[] Data => blob;

        public override byte[] Sign(byte[] data) => agent.Sign(blob, data, flags);

        /// <summary>A client's key verifies no server's signature.</summary>
        public override bool VerifySignature(byte[] data, byte[] signature) => false;
    }
}
