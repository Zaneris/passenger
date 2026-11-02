using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Passenger.Tests;

/// <summary>The SDK's browser refresh client as a page runs it, with no browser: from .NET 11's SDK, the settings and the
/// configuration module the app serves (its build's public key, or its WebAssembly client's), then a connection the app forwards, its session begun
/// with the page's secret read back; before 11, the script the middleware serves (Passenger's address and key) and a
/// connection to Passenger. Either way the secret goes encrypted with the key, as the connection's subprotocol.</summary>
internal sealed class RefreshClient : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly List<string> said = [];
    private readonly CancellationTokenSource closing = new();
    private Task listening = Task.CompletedTask;

    /// <summary>Every message heard, a line each.</summary>
    public string Said
    {
        get
        {
            lock (said)
            {
                return string.Join("\n", said);
            }
        }
    }

    public bool Heard(string type, string? path)
    {
        lock (said)
        {
            return said.Any(message =>
            {
                using JsonDocument read = JsonDocument.Parse(message);
                return read.RootElement.GetProperty("type").GetString() == type
                    && (path is null || read.RootElement.TryGetProperty("path", out JsonElement told) && told.GetString() == path);
            });
        }
    }

    public static async Task<RefreshClient> ConnectAsync(int port)
    {
        string origin = $"http://localhost:{port}";
        using HttpClient http = new();
        using HttpResponseMessage settings = await http.GetAsync(new Uri($"{origin}/_framework/dotnet-browser-tools/hot-reload-settings.json"));
        string key;
        Uri address;
        bool forwarded = settings.IsSuccessStatusCode;
        Assert.True(forwarded || settings.StatusCode == System.Net.HttpStatusCode.NotFound,
            $"The app forwarded the settings' request and was answered {(int)settings.StatusCode}: {await settings.Content.ReadAsStringAsync()}");
        if (forwarded)
        {
            using JsonDocument told = JsonDocument.Parse(await settings.Content.ReadAsStringAsync());
            Assert.True(told.RootElement.GetProperty("hotReload").GetBoolean(), $"The app's forwarded settings say no Hot Reload: {told.RootElement}");
            key = Regex.Match(await ConfigurationAsync(http, origin), "publicKey: '([^']+)'").Groups[1].Value;
            address = new Uri($"ws://localhost:{port}/_framework/dotnet-browser-tools/connect");
        }
        else
        {
            string script = await http.GetStringAsync(new Uri($"{origin}/_framework/aspnetcore-browser-refresh.js"));
            address = new Uri(Regex.Match(script, @"'(ws://[^']+)'\.split").Groups[1].Value);
            key = Regex.Match(script, @"getSecret\('([^']+)'\)").Groups[1].Value;
        }
        Assert.False(string.IsNullOrEmpty(key), "The app served no key for the page's secret");
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        using RSA server = RSA.Create();
        server.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
        RefreshClient client = new();
        client.socket.Options.AddSubProtocol(Uri.EscapeDataString(Convert.ToBase64String(server.Encrypt(secret, RSAEncryptionPadding.OaepSHA256))));
        client.socket.Options.SetRequestHeader("Origin", origin);
        await client.socket.ConnectAsync(address, CancellationToken.None);
        if (forwarded)
        {
            using JsonDocument first = JsonDocument.Parse(await client.ReceiveAsync() ?? "{}");
            Assert.Equal("InitializeSession", first.RootElement.GetProperty("type").GetString());
            Assert.True(first.RootElement.GetProperty("sharedSecret").GetString() == Convert.ToBase64String(secret),
                "Passenger did not read back the page's secret: the page would take it for another");
            await client.socket.SendAsync(Encoding.UTF8.GetBytes("{\"success\":true,\"log\":[]}"), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        client.listening = client.ListenAsync();
        return client;
    }

    /// <summary>The configuration module the app serves its pages: a server-rendered app's own, else (a Blazor Web App with
    /// WebAssembly components) its client's, which the page's WebAssembly runtime loads.</summary>
    private static async Task<string> ConfigurationAsync(HttpClient http, string origin)
    {
        foreach (string sdk in (string[])["BlazorWeb", "WebAssembly"])
        {
            using HttpResponseMessage module = await http.GetAsync(new Uri($"{origin}/_framework/Microsoft.NET.Sdk.{sdk}.DotNetWatch.BrowserTools.Config.js"));
            if (module.IsSuccessStatusCode)
            {
                return await module.Content.ReadAsStringAsync();
            }
        }
        Assert.Fail("The app serves no browser tools configuration module, so no page connects");
        return string.Empty;
    }

    private async Task ListenAsync()
    {
        while (await ReceiveAsync() is { } message)
        {
            lock (said)
            {
                said.Add(message);
            }
        }
    }

    private async Task<string?> ReceiveAsync()
    {
        byte[] buffer = new byte[16 * 1024];
        using MemoryStream message = new();
        WebSocketReceiveResult part;
        do
        {
            try
            {
                part = await socket.ReceiveAsync(buffer, closing.Token);
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException)
            {
                return null;
            }
            if (part.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            message.Write(buffer, 0, part.Count);
        }
        while (!part.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await closing.CancelAsync();
        await listening;
        socket.Dispose();
    }
}
