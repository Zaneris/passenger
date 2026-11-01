using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Passenger.App.Aspire;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.App.Debugging;

/// <summary>The SDK's own browser refresh for a web app debugged with Hot Reload, keeping its open pages up as dotnet watch
/// does: the SDK's middleware (Microsoft.AspNetCore.Watch.BrowserRefresh) of the SDK the app builds with, loaded into the app
/// as a startup hook and hosting startup, has its browser client, the SDK's too, connect to Passenger. A stylesheet changed is
/// applied in the page with no reload (a component's scoped CSS bundled again first), any other static file changed reloads
/// the page, and an update applied refreshes it. Before .NET 11 the page connects to Passenger's own WebSocket and encrypts
/// its secret with Passenger's key; from 11 the app forwards it, and Passenger proves itself with the key the app's build
/// made. Never waited on by the program.</summary>
public sealed class BrowserRefresh : IDisposable
{
    /// <summary>The middleware's file, under the SDK's dotnet-watch tool.</summary>
    private const string Middleware = "Microsoft.AspNetCore.Watch.BrowserRefresh.dll";

    /// <summary>Where the app forwards the page's requests from .NET 11, and what it asks there.</summary>
    private const string Routes = "/_framework/dotnet-browser-tools";
    private const string Settings = Routes + "/hot-reload-settings.json";
    private const string ClearCache = Routes + "/clear-cache";
    private const string Connect = Routes + "/connect";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource closing = new();
    private readonly List<Page> pages = [];
    private readonly SemaphoreSlim bundling = new(1, 1);
    private readonly Dictionary<string, (DateTime Written, long Length)> told = new(StringComparer.Ordinal);
    private readonly RSA? own;
    private readonly string? keyFile;
    private readonly string project;
    private readonly WebBuild build;

    private BrowserRefresh(string project, WebBuild build, RSA? own, string? keyFile)
    {
        this.project = project;
        this.build = build;
        this.own = own;
        this.keyFile = keyFile;
        listener.Start();
        // Not held: it serves as long as the program runs, which nothing drawn waits for.
        IdeErrors.Observe(BackgroundWork.Unheld(AcceptAsync));
    }

    /// <summary>Says a line where the program's output is: what each change came to in the browser; set by the session's tab.</summary>
    public Action<string, bool>? Says { get; set; }

    /// <summary>The refresh for a web app's plan and the plan that loads the SDK's middleware into it, read off the window's
    /// thread; or, where the browser cannot be kept up, null and why (the SDK with no browser refresh, a build that made no
    /// key, an app on a .NET older than the middleware's 6); null and nothing said for any other program.</summary>
    public static async Task<(BrowserRefresh? Refresh, LaunchPlan Plan, string? Said)> ForAsync(LaunchPlan plan)
    {
        if (plan.Web is not { } web || plan.Project is not { } project)
        {
            return (null, plan, null);
        }
        string name = Path.GetFileNameWithoutExtension(project);
        if (plan.RunsOn is { } framework && UpdateHandlerHook.MajorOf(framework) is > 0 and < 6)
        {
            return (null, plan, $"Hot Reload: the browser is not kept up: {name} runs on {framework}, older than the SDK's browser refresh (.NET 6)");
        }
        (string? middleware, bool? forwarded) = await BackgroundWork.Run(() => MiddlewareOf(web.Sdk));
        if (middleware is null || forwarded is null)
        {
            return (null, plan, $"Hot Reload: the browser is not kept up: the SDK {Path.GetFileName(web.Sdk)} {name} builds with has "
                + (middleware is null ? "no browser refresh" : "a browser refresh Passenger does not speak"));
        }
        string? keyFile = null;
        if (forwarded == true)
        {
            keyFile = Path.Combine(web.Intermediate, "dotnet-watch", "browser-tools-key.private.json");
            if (!await BackgroundWork.Run(() => File.Exists(keyFile)))
            {
                return (null, plan, $"Hot Reload: the browser is not kept up: {name}'s build made no browser tools key ({PathNames.Shown(keyFile, Path.GetDirectoryName(project))}), which a Debug build makes");
            }
        }
        BrowserRefresh refresh = new(project, web, forwarded == true ? null : RSA.Create(2048), keyFile);
        string address = $"127.0.0.1:{((IPEndPoint)refresh.listener.LocalEndpoint).Port}";
        Dictionary<string, string> environment = new(plan.Environment)
        {
            [RuntimeVariables.StartupHooks] = StartupHooks.First(plan.Environment, middleware),
            [AspNetCoreVariables.HostingStartupAssemblies] = First(plan.Environment, AspNetCoreVariables.HostingStartupAssemblies, Path.GetFileNameWithoutExtension(Middleware), ';'),
        };
        if (refresh.own is { } key)
        {
            environment[AspNetCoreVariables.AutoReloadEndpoint] = $"ws://{address}";
            environment[AspNetCoreVariables.AutoReloadKey] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
        else
        {
            environment[AspNetCoreVariables.AutoReloadProvider] = $"http://{address}/";
        }
        return (refresh, plan with { Environment = environment }, null);
    }

    /// <summary>A list variable with <paramref name="first"/> first, the values the program's environment gives (else
    /// Passenger's) after it.</summary>
    private static string First(IReadOnlyDictionary<string, string> environment, string variable, string first, char separator)
    {
        string? others = environment.TryGetValue(variable, out string? given) ? given : Environment.GetEnvironmentVariable(variable);
        return others is { Length: > 0 } ? $"{first}{separator}{others}" : first;
    }

    /// <summary>The SDK's middleware and whether it is the one the app forwards to Passenger (.NET 11's), by the variable it
    /// reads; neither where the SDK has none, and no way where it reads neither.</summary>
    internal static (string? Path, bool? Forwarded) MiddlewareOf(string sdk)
    {
        string tools = Path.Combine(sdk, "DotnetTools", "dotnet-watch");
        string? middleware;
        byte[] read;
        try
        {
            middleware = Directory.Exists(tools) ? Directory.EnumerateFiles(tools, Middleware, SearchOption.AllDirectories).FirstOrDefault() : null;
            if (middleware is null)
            {
                return (null, null);
            }
            read = File.ReadAllBytes(middleware);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
        return (middleware, Reads(read, AspNetCoreVariables.AutoReloadProvider) ? true : Reads(read, AspNetCoreVariables.AutoReloadEndpoint) ? false : null);
    }

    /// <summary>Whether an assembly's strings hold a name, as its user strings keep it (UTF-16).</summary>
    internal static bool Reads(ReadOnlySpan<byte> assembly, string name) => assembly.IndexOf(Encoding.Unicode.GetBytes(name)) >= 0;

    /// <summary>Files changed on disk: each of the app's static web assets the pages are told of, a stylesheet applied in
    /// place and anything else reloading them, a component's scoped CSS bundled again first; a file unchanged since it was
    /// told is passed over.</summary>
    public void Changed(IEnumerable<string> paths)
    {
        UiThread.Check();
        string folder = Path.GetDirectoryName(project)!;
        string root = Path.Combine(folder, "wwwroot");
        List<string> assets = [];
        bool scoped = false;
        foreach (string path in paths)
        {
            if (PathNames.IsUnder(path, root) && New(path))
            {
                assets.Add(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'));
            }
            else if (IsScopedCss(path) && PathNames.IsUnder(path, folder) && !PathNames.IsUnder(path, Path.Combine(folder, "bin")) && !PathNames.IsUnder(path, Path.Combine(folder, "obj"))
                && New(path))
            {
                scoped = true;
            }
        }
        if (assets.Count > 0 || scoped)
        {
            IdeErrors.Observe(BackgroundWork.Run(() => TellAsync(assets, scoped)));
        }
    }

    /// <summary>Whether a file is a component's or a page's scoped CSS, which its build bundles.</summary>
    internal static bool IsScopedCss(string path) =>
        path.EndsWith(".razor.css", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cshtml.css", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a file there now was written since the pages were last told of it.</summary>
    private bool New(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists)
        {
            return false;
        }
        (DateTime, long) now = (file.LastWriteTimeUtc, file.Length);
        if (told.TryGetValue(path, out (DateTime, long) last) && last == now)
        {
            return false;
        }
        told[path] = now;
        return true;
    }

    /// <summary>An update applied to the app: its pages refreshed as the SDK refreshes them (a Blazor page renders again,
    /// any other reloads).</summary>
    public void Updated()
    {
        UiThread.Check();
        IdeErrors.Observe(BackgroundWork.Run(() => SendAsync(new { Type = "RefreshBrowser" })));
    }

    private async Task TellAsync(IReadOnlyList<string> assets, bool scoped)
    {
        try
        {
            await TellEachAsync(assets, scoped);
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested)
        {
            // The session ended: no page is left to tell.
        }
    }

    private async Task TellEachAsync(IReadOnlyList<string> assets, bool scoped)
    {
        List<string> urls = [.. assets];
        if (scoped)
        {
            await bundling.WaitAsync(closing.Token);
            try
            {
                if (await BundleAsync() is { } failed)
                {
                    Say($"Hot Reload: {Path.GetFileNameWithoutExtension(project)}'s scoped CSS could not be bundled again: {failed}", true);
                }
                else
                {
                    urls.Add($"{Path.GetFileNameWithoutExtension(project)}.styles.css");
                }
            }
            finally
            {
                bundling.Release();
            }
        }
        foreach (string url in urls)
        {
            int reached = await SendAsync(new { Type = "UpdateStaticFile", Path = url });
            bool css = url.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
            Say(reached == 0 ? $"Hot Reload: {url} changed, with no page open to {(css ? "apply it in" : "reload")}"
                : css ? $"Hot Reload: {url} applied in {Plural.Of(reached, "page")}" : $"Hot Reload: {url} changed: {Plural.Of(reached, "page")} reloaded", false);
        }
    }

    /// <summary>The app's scoped CSS bundled again by its build's own targets, with no compile: null once done, else why not.</summary>
    private async Task<string?> BundleAsync()
    {
        List<string> arguments = ["msbuild", project, "-nologo", "-verbosity:quiet",
            $"-target:{MSBuildNames.GenerateComputedBuildStaticWebAssets};{MSBuildNames.ResolveReferencedProjectsStaticWebAssets}",
            .. build.Properties.Select(p => $"-property:{p.Key}={p.Value}")];
        ChildResult made = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(arguments, Path.GetDirectoryName(project)), cancel: closing.Token);
        return made.Succeeded ? null : $"{made.Output}{made.Error}".Trim() is { Length: > 0 } said ? said : ExitCodes.Said(made.ExitCode);
    }

    private void Say(string line, bool error) => UiThread.Post(() => Says?.Invoke(line, error));

    /// <summary>A message sent to every page connected: how many it reached.</summary>
    private async Task<int> SendAsync<T>(T message)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        Page[] open;
        lock (pages)
        {
            open = [.. pages];
        }
        int reached = 0;
        foreach (Page page in open)
        {
            reached += await page.SendAsync(bytes, closing.Token) ? 1 : 0;
        }
        return reached;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(closing.Token);
                // Not held: a page's connection lasts as long as the page, which nothing drawn waits for.
                IdeErrors.Observe(BackgroundWork.Unheld(() => ServeAsync(client)));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The session ended: nothing more connects.
        }
    }

    /// <summary>One request: from .NET 11 the app's forwarded settings, cache clearing and connection; before, the page's
    /// connection alone.</summary>
    private async Task ServeAsync(TcpClient client)
    {
        await using AspireExecutionConnection connection = new(client, closing.Token);
        try
        {
            await connection.ReadHeadAsync();
            connection.ResponseHeaders["Cache-Control"] = "no-store";
            if (connection.Method != "GET")
            {
                connection.StatusCode = 405;
            }
            else if (keyFile is null || connection.Path == Connect)
            {
                await ConnectAsync(connection);
            }
            else if (connection.Path == Settings)
            {
                connection.ResponseHeaders["Content-Type"] = "application/json";
                await connection.WriteResponseAsync("{ \"hotReload\": true }"u8.ToArray());
            }
            else if (connection.Path == ClearCache)
            {
                connection.ResponseHeaders["Clear-Site-Data"] = "\"cache\"";
                connection.StatusCode = 204;
            }
            else
            {
                connection.StatusCode = 404;
            }
            await connection.WriteResponseAsync([]);
            await connection.CloseAsync();
        }
        catch (AspireExecutionConnection.RequestException refused)
        {
            connection.StatusCode = refused.StatusCode;
            await Answered(connection.WriteResponseAsync([]));
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException or WebSocketException)
        {
            // The page, the app or the session went.
        }
    }

    private static async Task Answered(Task answer)
    {
        try
        {
            await answer;
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The peer went before its refusal.
        }
    }

    /// <summary>A page's connection, from this machine's pages alone: its secret, encrypted with the key it was given, read
    /// back for it (from .NET 11 sent first, the page answering before it hears anything else), then held until it goes.</summary>
    private async Task ConnectAsync(AspireExecutionConnection connection)
    {
        if (!connection.IsWebSocketRequest())
        {
            connection.StatusCode = 400;
            return;
        }
        if (!Uri.TryCreate(connection.Header("Origin").FirstOrDefault(), UriKind.Absolute, out Uri? origin) || origin.Host is not ("localhost" or "127.0.0.1" or "[::1]"))
        {
            connection.StatusCode = 403;
            return;
        }
        string[] asked = [.. connection.Header("Sec-WebSocket-Protocol").SelectMany(p => p.Split(',')).Select(p => p.Trim()).Where(p => p.Length > 0)];
        string? secret = asked is [{ } encrypted] ? Decrypted(WebUtility.UrlDecode(encrypted)) : null;
        if (secret is null && (keyFile is not null || asked.Length > 0))
        {
            connection.StatusCode = 400;
            return;
        }
        using WebSocket socket = await connection.AcceptWebSocketAsync(asked is [{ } protocol] ? protocol : null);
        Page page = new(socket);
        if (keyFile is not null && !await page.InitializedAsync(secret!, closing.Token))
        {
            return;
        }
        lock (pages)
        {
            pages.Add(page);
        }
        try
        {
            await page.HeldAsync(closing.Token);
        }
        finally
        {
            lock (pages)
            {
                pages.Remove(page);
            }
        }
    }

    /// <summary>A page's secret, decrypted with Passenger's key, else the key the app's build made, read as each page comes
    /// (a build since may have made another): base64, as the page keeps it; null when it does not decrypt.</summary>
    private string? Decrypted(string encrypted)
    {
        try
        {
            using RSA? built = own is null ? BuiltKey() : null;
            return (own ?? built) is { } key ? Convert.ToBase64String(key.Decrypt(Convert.FromBase64String(encrypted), RSAEncryptionPadding.OaepSHA256)) : null;
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The private half of the key the app's build made (<c>browser-tools-key.private.json</c>: its RSA parameters,
    /// base64); null where it is not there or does not read.</summary>
    private RSA? BuiltKey()
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(File.ReadAllBytes(keyFile!));
            if (JsonMembers.Object(read.RootElement, "parameters") is not { } parameters)
            {
                return null;
            }
            byte[] Part(string name) => Convert.FromBase64String(JsonMembers.Text(parameters, name) ?? throw new FormatException($"{name} is not there"));
            RSA key = RSA.Create();
            key.ImportParameters(new RSAParameters
            {
                Modulus = Part("modulus"), Exponent = Part("exponent"), D = Part("d"), P = Part("p"), Q = Part("q"), DP = Part("dp"), DQ = Part("dq"), InverseQ = Part("inverseQ"),
            });
            return key;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        closing.Cancel();
        listener.Dispose();
        own?.Dispose();
    }

    /// <summary>A page connected, its sends one at a time.</summary>
    private sealed class Page(WebSocket socket)
    {
        private readonly SemaphoreSlim sending = new(1, 1);

        /// <summary>From .NET 11, the session begun as the SDK begins it: the page's secret given back, with no updates, and
        /// the page's answer that it took it.</summary>
        public async Task<bool> InitializedAsync(string secret, CancellationToken cancel)
        {
            if (!await SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { Type = "InitializeSession", SharedSecret = secret, Updates = Array.Empty<object>() }, Json), cancel))
            {
                return false;
            }
            byte[] buffer = new byte[16 * 1024];
            using MemoryStream answer = new();
            WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(buffer, cancel);
                if (part.MessageType == WebSocketMessageType.Close || answer.Length + part.Count > 1024 * 1024)
                {
                    return false;
                }
                answer.Write(buffer, 0, part.Count);
            }
            while (!part.EndOfMessage);
            try
            {
                using JsonDocument read = JsonDocument.Parse(answer.ToArray());
                return JsonMembers.Bool(read.RootElement, "success") == true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>Whether a message reached the page.</summary>
        public async Task<bool> SendAsync(byte[] message, CancellationToken cancel)
        {
            try
            {
                await sending.WaitAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            try
            {
                await socket.SendAsync(message, WebSocketMessageType.Text, true, cancel);
                return true;
            }
            catch (Exception error) when (error is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                return false;
            }
            finally
            {
                sending.Release();
            }
        }

        /// <summary>What the page says read and passed over until it goes.</summary>
        public async Task HeldAsync(CancellationToken cancel)
        {
            byte[] buffer = new byte[4096];
            bool open = true;
            while (open && socket.State == WebSocketState.Open)
            {
                open = (await socket.ReceiveAsync(buffer, cancel)).MessageType != WebSocketMessageType.Close;
            }
        }
    }
}
