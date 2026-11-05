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
/// as a startup hook and hosting startup, has its browser client, the SDK's too, connect to Passenger. A stylesheet the app
/// serves changed (its own, or a referenced project's, a Razor class library's) is applied in the page with no reload (a
/// component's scoped CSS bundled again by its project first), any other static file changed reloads the page, and an update
/// applied refreshes it. Before .NET 11 the page connects to Passenger's own WebSocket and encrypts its secret with
/// Passenger's key; from 11 the app forwards it, and Passenger proves itself with the key the app's build made. Never waited
/// on by the program.</summary>
public sealed class BrowserRefresh : IDisposable
{
    /// <summary>The middleware's file, under the SDK's dotnet-watch tool.</summary>
    private const string Middleware = "Microsoft.AspNetCore.Watch.BrowserRefresh.dll";

    /// <summary>The private half of the key a build makes beside the browser tools it serves.</summary>
    private const string PrivateKey = "browser-tools-key.private.json";

    /// <summary>Where the app forwards the page's requests from .NET 11, and what it asks there.</summary>
    private const string Routes = "/_framework/dotnet-browser-tools";
    private const string Settings = Routes + "/hot-reload-settings.json";
    private const string ClearCache = Routes + "/clear-cache";
    private const string Connect = Routes + "/connect";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource closing = new();
    private readonly List<Page> pages = [];
    private readonly SemaphoreSlim telling = new(1, 1);
    private readonly ToldFiles told = new();
    private ((DateTime Written, long Length) Stamp, DevelopmentAssets? Assets)? served;
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
    /// browser tools or lost their key, an app on a .NET older than the middleware's 6); null and nothing said for any other
    /// program.</summary>
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
            (string? tools, bool keyed) = await BackgroundWork.Run(() => BrowserToolsOf(web.Intermediate));
            if (tools is null)
            {
                return (null, plan, $"Hot Reload: the browser is not kept up: {name}'s build made no browser tools for its pages, which the SDK makes in a build "
                    + "whose EnableHotReloadInRuntimeConfigDevFile is true, as a Debug build's is unless the project sets it to false");
            }
            keyFile = Path.Combine(tools, PrivateKey);
            if (!keyed)
            {
                return (null, plan, $"Hot Reload: the browser is not kept up: the key {name}'s build made for its browser tools is gone "
                    + $"({PathNames.Shown(keyFile, Path.GetDirectoryName(project))}): build {name} again");
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

    /// <summary>Where the browser tools the app's build made for its pages are, as the build's own map of what the app serves
    /// says (a Blazor Web App's are its WebAssembly client's), and whether the key made with them is there; no folder where
    /// the build made none, or wrote no map.</summary>
    private static (string? Folder, bool Keyed) BrowserToolsOf(string intermediate)
    {
        byte[] manifest;
        try
        {
            manifest = File.ReadAllBytes(Path.Combine(intermediate, DevelopmentAssets.FileName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, false);
        }
        return DevelopmentAssets.Read(manifest)?.BrowserTools is { } folder ? (folder, File.Exists(Path.Combine(folder, PrivateKey))) : (null, false);
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

    /// <summary>Files changed on disk, told to the pages off the window's thread, one change at a time: each file the app serves,
    /// as its build's map of what it serves names it, a stylesheet applied in place and anything else reloading them, and a
    /// component's scoped CSS, written or deleted, bundled again by its project first, the app's or one it references; a file
    /// unchanged since it was told is passed over.</summary>
    public void Changed(IEnumerable<string> paths)
    {
        UiThread.Check();
        string[] changed = [.. paths];
        if (changed.Length > 0)
        {
            IdeErrors.Observe(BackgroundWork.Run(() => TellAsync(changed)));
        }
    }

    /// <summary>Whether a file is a component's or a page's scoped CSS, which its build bundles.</summary>
    internal static bool IsScopedCss(string path) =>
        path.EndsWith(".razor.css", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cshtml.css", StringComparison.OrdinalIgnoreCase);

    /// <summary>An update applied to the app: its pages refreshed as the SDK refreshes them (a Blazor page renders again,
    /// any other reloads).</summary>
    public void Updated()
    {
        UiThread.Check();
        IdeErrors.Observe(BackgroundWork.Run(() => SendAsync(new { Type = "RefreshBrowser" })));
    }

    private async Task TellAsync(IReadOnlyList<string> changed)
    {
        try
        {
            await telling.WaitAsync(closing.Token);
            try
            {
                await TellEachAsync(changed);
            }
            finally
            {
                telling.Release();
            }
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested)
        {
            // The session ended: no page is left to tell.
        }
    }

    private async Task TellEachAsync(IReadOnlyList<string> changed)
    {
        DevelopmentAssets? assets = Served();
        string app = Path.GetFileNameWithoutExtension(project);
        List<string> urls = [];
        List<(string Project, string? Framework)> scoped = [];
        HashSet<string> lostSheet = new(PathNames.Comparer);
        foreach (string path in changed)
        {
            if (IsScopedCss(path))
            {
                if (ProjectOf(path) is { } owner && told.WrittenOrGone(path))
                {
                    if (!scoped.Contains(owner))
                    {
                        scoped.Add(owner);
                    }
                    if (!File.Exists(path))
                    {
                        lostSheet.Add(owner.Project);
                    }
                }
            }
            else if (UrlOf(assets, path) is { } url && told.Written(path) && !urls.Contains(url))
            {
                urls.Add(url);
            }
        }
        foreach ((string bundled, string? framework) in scoped)
        {
            string name = Path.GetFileNameWithoutExtension(bundled);
            bool own = PathNames.Same(bundled, project);
            if ((own ? $"{name}.styles.css" : assets?.BundleUrlOf(bundled)) is not { } url)
            {
                Say($"Hot Reload: {name}'s scoped CSS is not in {app}'s pages, as {app}'s build took in none of it: build {app} again to have it", true);
            }
            else if (await BundleAsync(bundled, own ? null : framework, own, lostSheet.Contains(bundled) ? assets?.BundleFileOf(bundled, own) : null) is { } failed)
            {
                Say($"Hot Reload: {name}'s scoped CSS could not be bundled again: {failed}", true);
            }
            else
            {
                urls.Add(url);
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

    /// <summary>The app's map of what it serves as its build last wrote it, read again once a build has written it since;
    /// null where its build wrote none.</summary>
    private DevelopmentAssets? Served()
    {
        FileInfo manifest = new(Path.Combine(build.Intermediate, DevelopmentAssets.FileName));
        try
        {
            if (!manifest.Exists)
            {
                return null;
            }
            (DateTime, long) stamp = (manifest.LastWriteTimeUtc, manifest.Length);
            if (served is { } last && last.Stamp == stamp)
            {
                return last.Assets;
            }
            DevelopmentAssets? read = DevelopmentAssets.Read(File.ReadAllBytes(manifest.FullName));
            served = (stamp, read);
            return read;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return served?.Assets;
        }
    }

    /// <summary>The URL the app serves a file at, as its build's map says, else a file come since in its own wwwroot by its
    /// place there; null for a file it does not serve.</summary>
    private string? UrlOf(DevelopmentAssets? assets, string path)
    {
        if (assets?.UrlOf(path) is { } url)
        {
            return url;
        }
        string root = Path.Combine(Path.GetDirectoryName(project)!, "wwwroot");
        return PathNames.IsUnder(path, root) ? Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/') : null;
    }

    /// <summary>The project a file is in, of the app's own and those it references, the innermost; none for a file of none,
    /// or of a project's build output.</summary>
    private (string Project, string? Framework)? ProjectOf(string path)
    {
        (string Project, string? Framework)? found = null;
        foreach ((string Project, string? Framework) each in (IEnumerable<(string, string?)>)[(project, null), .. build.Referenced])
        {
            string folder = Path.GetDirectoryName(each.Project)!;
            if (PathNames.IsUnder(path, folder) && !PathNames.IsUnder(path, Path.Combine(folder, "bin")) && !PathNames.IsUnder(path, Path.Combine(folder, "obj"))
                && (found is not { } inner || each.Project.Length > inner.Project.Length))
            {
                found = each;
            }
        }
        return found;
    }

    /// <summary>A project's scoped CSS bundled again by its build's own targets, with no compile, the app's as its build is
    /// given, one it references for its own framework: null once done, else why not. Where a sheet of it went, the bundle the
    /// app serves is emptied first: those targets write it whole from what is left, and with no sheet left they write none,
    /// which would keep the rules of the last. What it held is put back unless the targets ran.</summary>
    private async Task<string?> BundleAsync(string bundled, string? framework, bool own, string? emptied)
    {
        IEnumerable<KeyValuePair<string, string>> properties = own ? build.Properties
            : build.Properties.Where(p => !p.Key.Equals(MSBuildNames.TargetFramework, StringComparison.OrdinalIgnoreCase));
        if (framework is not null)
        {
            properties = properties.Append(new(MSBuildNames.TargetFramework, framework));
        }
        List<string> arguments = ["msbuild", bundled, "-nologo", "-verbosity:quiet",
            $"-target:{MSBuildNames.GenerateComputedBuildStaticWebAssets};{MSBuildNames.ResolveReferencedProjectsStaticWebAssets}",
            .. properties.Select(p => $"-property:{p.Key}={p.Value}")];
        byte[]? held = emptied is not null ? Emptied(emptied) : null;
        bool ran = false;
        try
        {
            ChildResult made = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(arguments, Path.GetDirectoryName(bundled)), cancel: closing.Token);
            ran = made.Succeeded;
            return ran ? null : $"{made.Output}{made.Error}".Trim() is { Length: > 0 } said ? said : ExitCodes.Said(made.ExitCode);
        }
        finally
        {
            if (!ran && held is not null)
            {
                Filled(emptied!, held);
            }
        }
    }

    /// <summary>A bundle emptied, and what it held; null where it could not be, which leaves it as it was.</summary>
    private static byte[]? Emptied(string bundle)
    {
        try
        {
            byte[] held = File.ReadAllBytes(bundle);
            AtomicFile.Write(bundle, []);
            return held;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What a bundle held put back.</summary>
    private static void Filled(string bundle, byte[] held)
    {
        try
        {
            AtomicFile.Write(bundle, held);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The app holds it: the next bundling writes it whole.
        }
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
