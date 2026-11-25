using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Passenger.Tests;

/// <summary>AWS's services as Passenger's sign-ins ask them, on a loopback port (Passenger's PASSENGER_AWS_ENDPOINT): Secrets
/// Manager's GetSecretValue of the secrets a test gives, and IAM Identity Center's OIDC device sign-in (its token pending
/// once, as a person approving it takes a moment) and portal's role credentials. Each request's access key is heard.</summary>
public sealed class StandInAws : IDisposable
{
    /// <summary>The role credentials the portal gives a signed-in profile.</summary>
    public const string SsoAccessKey = "AKIDSTANDINSSO";

    public const string SsoSecretKey = "stand-in-sso-secret";

    /// <summary>The code the device sign-in's page shows.</summary>
    public const string UserCode = "PASS-ENGR";

    private readonly StandInHttp http;
    private int pending = 1;

    public StandInAws() => http = new StandInHttp(AnswerAsync, keepAlive: true);

    public string Root => http.Root;

    /// <summary>The secrets it holds, by name, each as its SecretString.</summary>
    public ConcurrentDictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

    /// <summary>Each secret read, with the access key its request was signed with.</summary>
    public ConcurrentQueue<(string Secret, string AccessKey)> Read { get; } = new();

    /// <summary>The page the person approves the device sign-in on.</summary>
    public string Verification => $"{Root}/verify?user_code={UserCode}";

    public void Dispose() => http.Dispose();

    private async Task AnswerAsync(StandInHttp.Exchange asked)
    {
        string body = Encoding.UTF8.GetString(asked.Body);
        if (asked.Header("X-Amz-Target") == "secretsmanager.GetSecretValue")
        {
            string id = JsonNode.Parse(body)?["SecretId"]?.GetValue<string>() ?? string.Empty;
            Read.Enqueue((id, AccessKeyOf(asked.Header("Authorization"))));
            await (Secrets.TryGetValue(id, out string? secret)
                ? asked.ReplyAsync(200, "application/x-amz-json-1.1", new JsonObject { ["ARN"] = $"arn:aws:secretsmanager:us-east-1:111122223333:secret:{id}", ["Name"] = id,
                    ["SecretString"] = secret, ["VersionId"] = "1" }.ToJsonString())
                : asked.ReplyAsync(400, "application/x-amz-json-1.1", new JsonObject { ["__type"] = "ResourceNotFoundException", ["message"] = $"No secret {id}" }.ToJsonString()));
            return;
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        switch (asked.Path.Split('?')[0])
        {
            case "/client/register":
                await asked.ReplyAsync(200, "application/json", new JsonObject { ["clientId"] = "stand-in-client", ["clientSecret"] = "stand-in-client-secret",
                    ["clientIdIssuedAt"] = now, ["clientSecretExpiresAt"] = now + 90 * 86400 }.ToJsonString());
                return;
            case "/device_authorization":
                await asked.ReplyAsync(200, "application/json", new JsonObject { ["deviceCode"] = "stand-in-device", ["userCode"] = UserCode,
                    ["verificationUri"] = $"{Root}/verify", ["verificationUriComplete"] = Verification, ["expiresIn"] = 600, ["interval"] = 1 }.ToJsonString());
                return;
            case "/token" when Interlocked.Decrement(ref pending) >= 0:
                await asked.ReplyAsync(400, "application/json", new JsonObject { ["error"] = "authorization_pending", ["error_description"] = "Not yet approved" }.ToJsonString(),
                    "x-amzn-ErrorType: AuthorizationPendingException\r\n");
                return;
            case "/token":
                await asked.ReplyAsync(200, "application/json", new JsonObject { ["accessToken"] = "stand-in-access-token", ["tokenType"] = "Bearer", ["expiresIn"] = 3600,
                    ["refreshToken"] = "stand-in-refresh-token" }.ToJsonString());
                return;
            case "/federation/credentials" when asked.Header("x-amz-sso_bearer_token") == "stand-in-access-token":
                await asked.ReplyAsync(200, "application/json", new JsonObject { ["roleCredentials"] = new JsonObject { ["accessKeyId"] = SsoAccessKey,
                    ["secretAccessKey"] = SsoSecretKey, ["sessionToken"] = "stand-in-session", ["expiration"] = (now + 3600) * 1000 } }.ToJsonString());
                return;
            case "/federation/credentials":
                await asked.ReplyAsync(401, "application/json", new JsonObject { ["message"] = "Session token not found or invalid" }.ToJsonString(),
                    "x-amzn-ErrorType: UnauthorizedException\r\n");
                return;
            default:
                await asked.ReplyAsync(404, "application/json", new JsonObject { ["message"] = $"The stand-in answers no {asked.Line}" }.ToJsonString());
                return;
        }
    }

    /// <summary>The access key a SigV4 Authorization header signs with.</summary>
    private static string AccessKeyOf(string? authorization) =>
        authorization?.Split("Credential=", 2) is [_, var credential] ? credential.Split('/')[0] : string.Empty;
}

/// <summary>A PostgreSQL server that takes an RDS IAM authentication token as a password, as RDS does: on a loopback port of
/// its own, it asks each client for its password in the clear, checks it as a SigV4-signed connect for the user it signs
/// in as, its host and port this proxy's, by the secret key given, and only then hands the connection to the server behind
/// it (which trusts that user), passing everything between them. Each token heard is kept, with whether it held.</summary>
public sealed class PostgresIamProxy : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource end = new();
    private readonly int server;
    private readonly string secretKey;
    private readonly Task accepting;

    public PostgresIamProxy(int server, string secretKey)
    {
        this.server = server;
        this.secretKey = secretKey;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = Waiting.Apart(() => AcceptAsync());
    }

    public int Port { get; }

    /// <summary>Each token a client gave, with what was wrong with it (null for one that held).</summary>
    public ConcurrentQueue<(string Token, string? Wrong)> Tokens { get; } = new();

    public void Dispose()
    {
        end.Cancel();
        listener.Stop();
        Ui.Pumped(() => accepting.IsCompleted, TimeSpan.FromSeconds(10), scaled: false);
        end.Dispose();
    }

    private async Task AcceptAsync()
    {
        List<Task> serving = [];
        try
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(end.Token);
                serving.Add(ServeAsync(client));
            }
        }
        catch (Exception stopped) when (stopped is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            await Waiting.Within(Task.WhenAll(serving), TimeSpan.FromSeconds(5), scaled: false);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream near = client.GetStream();
                byte[] startup;
                while (true)
                {
                    startup = await MessageAsync(near, typed: false);
                    // SSL's and GSS's requests are refused, so the client starts over in the clear.
                    if (startup.Length == 8 && BinaryPrimitives.ReadInt32BigEndian(startup.AsSpan(4)) is 80877103 or 80877104)
                    {
                        await near.WriteAsync("N"u8.ToArray(), end.Token);
                        continue;
                    }
                    break;
                }
                string user = Parameters(startup).GetValueOrDefault("user") ?? string.Empty;
                await near.WriteAsync((byte[])[(byte)'R', 0, 0, 0, 8, 0, 0, 0, 3], end.Token);
                byte[] password = await MessageAsync(near, typed: true);
                string token = Encoding.UTF8.GetString(password, 5, password.Length - 6);
                string? wrong = Wrong(token, user);
                Tokens.Enqueue((token, wrong));
                if (wrong is not null)
                {
                    byte[] said = Encoding.UTF8.GetBytes($"SFATAL\0C28P01\0Mpassword authentication failed for user \"{user}\": {wrong}\0\0");
                    byte[] error = new byte[5 + said.Length];
                    error[0] = (byte)'E';
                    BinaryPrimitives.WriteInt32BigEndian(error.AsSpan(1), 4 + said.Length);
                    said.CopyTo(error, 5);
                    await near.WriteAsync(error, end.Token);
                    return;
                }
                using TcpClient behind = new();
                await behind.ConnectAsync(IPAddress.Loopback, server, end.Token);
                NetworkStream far = behind.GetStream();
                await far.WriteAsync(startup, end.Token);
                using CancellationTokenSource either = CancellationTokenSource.CreateLinkedTokenSource(end.Token);
                await Task.WhenAny(near.CopyToAsync(far, either.Token), far.CopyToAsync(near, either.Token));
                await either.CancelAsync();
            }
            catch (Exception ended) when (ended is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>A message of PostgreSQL's protocol whole: a typed one (its type byte first) or the startup's.</summary>
    private async Task<byte[]> MessageAsync(NetworkStream stream, bool typed)
    {
        int head = typed ? 5 : 4;
        byte[] start = new byte[head];
        await stream.ReadExactlyAsync(start, end.Token);
        int length = BinaryPrimitives.ReadInt32BigEndian(start.AsSpan(head - 4));
        byte[] whole = new byte[head - 4 + length];
        start.CopyTo(whole, 0);
        await stream.ReadExactlyAsync(whole.AsMemory(head), end.Token);
        return whole;
    }

    private static Dictionary<string, string> Parameters(byte[] startup)
    {
        string[] words = Encoding.UTF8.GetString(startup, 8, startup.Length - 8).Split('\0');
        Dictionary<string, string> named = new(StringComparer.Ordinal);
        for (int at = 0; at + 1 < words.Length && words[at].Length > 0; at += 2)
        {
            named[words[at]] = words[at + 1];
        }
        return named;
    }

    /// <summary>What is wrong with a token as RDS would check it, null for nothing: a presigned GET of this host and port
    /// connecting as the user, its scope rds-db's, signed by the secret key with SigV4.</summary>
    private string? Wrong(string token, string user)
    {
        string expectedHost = $"127.0.0.1:{Port}";
        if (!token.StartsWith(expectedHost + "/?", StringComparison.Ordinal))
        {
            return $"it is not for {expectedHost}";
        }
        List<(string Name, string Value)> query = [.. token[(expectedHost.Length + 2)..].Split('&').Select(p => p.Split('=', 2))
            .Select(p => (Uri.UnescapeDataString(p[0]), Uri.UnescapeDataString(p.Length > 1 ? p[1] : string.Empty)))];
        string Of(string name) => query.FirstOrDefault(q => q.Name == name).Value ?? string.Empty;
        if (Of("Action") != "connect" || Of("DBUser") != user)
        {
            return $"it connects {Of("DBUser")}, not {user}";
        }
        string[] scope = Of("X-Amz-Credential").Split('/');
        if (scope is not [_, var day, var region, "rds-db", "aws4_request"])
        {
            return $"its scope is {Of("X-Amz-Credential")}";
        }
        string canonicalQuery = string.Join('&', query.Where(q => q.Name != "X-Amz-Signature").OrderBy(q => q.Name, StringComparer.Ordinal)
            .Select(q => $"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(q.Value)}"));
        byte[] key = Encoding.UTF8.GetBytes("AWS4" + secretKey);
        foreach (string part in (string[])[day, region, "rds-db", "aws4_request"])
        {
            key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(part));
        }
        foreach (string payload in (string[])[Convert.ToHexStringLower(SHA256.HashData([])), "UNSIGNED-PAYLOAD"])
        {
            string canonical = $"GET\n/\n{canonicalQuery}\nhost:{expectedHost}\n\nhost\n{payload}";
            string toSign = $"AWS4-HMAC-SHA256\n{Of("X-Amz-Date")}\n{day}/{region}/rds-db/aws4_request\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
            if (Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(toSign))) == Of("X-Amz-Signature"))
            {
                return null;
            }
        }
        return "its signature is not the secret key's";
    }
}
