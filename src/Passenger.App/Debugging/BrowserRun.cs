using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Dap;

namespace Passenger.App.Debugging;

/// <summary>The browser and debug proxy one debugging of a WebAssembly app runs: Chromium started with remote debugging on a
/// profile of Passenger's own (the open folder's, kept, so its sign-ins stay; a profile of the moment when another debugging
/// holds it), its secrets under a key of its own on macOS rather than the person's keychain, never the person's own
/// browser, headless where the IDE is; .NET's proxy pointed at it; and the conversation with the browser's page through
/// the proxy, which adds .NET's code to what the page says. Ending it ends both programs.</summary>
public sealed partial class BrowserRun : IAsyncDisposable
{
    /// <summary>How long the browser and the proxy each have to say where they listen.</summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(60);

    /// <summary>How long a browser that ended as it started has for what it wrote to be read.</summary>
    private static readonly TimeSpan LastWords = TimeSpan.FromSeconds(2);

    private static readonly HttpClient Http = OutboundHttp.Client(TimeSpan.FromSeconds(30), 4 * 1024 * 1024);

    private readonly Process browser;
    private readonly Process proxy;
    private readonly string? apart;

    private BrowserRun(Process browser, Process proxy, CdpConnection page, string? apart)
    {
        this.browser = browser;
        this.proxy = proxy;
        this.apart = apart;
        Page = page;
        Ended = Task.WhenAny(browser.WaitForExitAsync(), page.Completion);
    }

    /// <summary>The conversation with the browser's page, through the proxy.</summary>
    public CdpConnection Page { get; }

    /// <summary>The browser's process id.</summary>
    public int BrowserId => browser.Id;

    /// <summary>Done once the browser has gone (the person closed it) or the page's conversation has ended.</summary>
    public Task Ended { get; }

    /// <summary>Starts the browser on a blank page, then the proxy, and opens the page's conversation through it; what each
    /// writes goes to the IDE's log. Throws <see cref="InvalidOperationException"/> saying which would not start.</summary>
    public static async Task<BrowserRun> StartAsync(BrowserDebugging debugging, string profiles, Action<string> say, CancellationToken cancel)
    {
        string profile = Path.Combine(profiles, "browser");
        (Process browser, int port)? started = await StartBrowserAsync(debugging.Browser, profile, cancel);
        string? apart = null;
        if (started is null)
        {
            // Another debugging holds the kept profile: this one's is its own, gone with it.
            apart = Path.Combine(profiles, $"browser-{Guid.NewGuid():N}");
            try
            {
                started = await StartBrowserAsync(debugging.Browser, apart, cancel)
                    ?? throw new InvalidOperationException($"{debugging.Browser.Name} ended as it started, with no remote debugging");
            }
            catch
            {
                Delete(apart);
                throw;
            }
        }
        (Process browser, int port) = started.Value;
        Process? proxy = null;
        try
        {
            say($"{debugging.Browser.Name} started{(PassengerApp.ShowsNoWindow ? " headless" : string.Empty)} for debugging, on a profile of Passenger's own");
            (proxy, int proxyPort) = await StartProxyAsync(debugging.Proxy, port, cancel);
            string target = await PageTargetAsync(port, cancel);
            CdpConnection page = await CdpConnection.ConnectAsync(new Uri($"ws://127.0.0.1:{proxyPort}/devtools/page/{target}"),
                line => IdeLog.Write(IdeLog.Debugger, $"[browser] {line}"), cancel);
            return new BrowserRun(browser, proxy, page, apart);
        }
        catch
        {
            ChildProcesses.Kill(browser);
            if (proxy is not null)
            {
                ChildProcesses.Kill(proxy);
            }
            Delete(apart);
            throw;
        }
    }

    /// <summary>The proxy every browser Passenger starts sends what is not for this machine through (<c>host:port</c>), a
    /// test run's where nothing listens, so its browsers reach no host but this machine; none unless it names one.</summary>
    [IdentifierHome]
    public const string ProxyVariable = "PASSENGER_BROWSER_PROXY";

    /// <summary>The proxy <see cref="ProxyVariable"/> names, null for none; a value that is no host and port is fatal.</summary>
    public static string? Proxy => Environment.GetEnvironmentVariable(ProxyVariable) is { Length: > 0 } named
        ? Uri.TryCreate($"http://{named}", UriKind.Absolute, out Uri? proxy) && proxy.Host.Length > 0 && !proxy.IsDefaultPort && proxy.AbsolutePath == "/"
            ? named
            : throw new FormatException($"{ProxyVariable} takes a proxy's host and port (127.0.0.1:9), not '{named}'")
        : null;

    /// <summary>What every browser Passenger starts is given beyond its debugging: no first-run pages or questions, its
    /// background networking, component and extension updates, sync, pings, metrics reports, domain reliability, Safe
    /// Browsing's own checks and the features that ask Google's servers left off: as little of its own reaching out as its
    /// flags turn off.</summary>
    public static readonly IReadOnlyList<string> Quiet =
    [
        "--no-first-run", "--no-default-browser-check", "--disable-search-engine-choice-screen", "--disable-background-networking", "--disable-component-update",
        "--disable-sync", "--no-pings", "--metrics-recording-only", "--disable-domain-reliability", "--disable-client-side-phishing-detection",
        "--disable-default-apps", "--disable-component-extensions-with-background-pages",
        "--disable-features=OptimizationHints,MediaRouter,Translate,AutofillServerCommunication,CertificateTransparencyComponentUpdater,InterestFeedContentSuggestions",
    ];

    /// <summary>The browser started on a profile with remote debugging on a port it chooses, once it has written which
    /// (DevToolsActivePort in the profile); null where it ended at once with exit code 0, handing its page to another browser
    /// on that profile. One that ended otherwise throws <see cref="InvalidOperationException"/> with its own last words, and
    /// where the system refused its sandbox, what lets Chromium run. Given up or failed, it leaves no browser running.</summary>
    private static async Task<(Process Browser, int Port)?> StartBrowserAsync(ChromiumFound found, string profile, CancellationToken cancel)
    {
        Containers.OwnFiles.Folder(profile);
        string written = Path.Combine(profile, "DevToolsActivePort");
        File.Delete(written);
        List<string> arguments = ["--remote-debugging-port=0", $"--user-data-dir={profile}", .. Quiet, "--disable-background-timer-throttling", "--disable-renderer-backgrounding"];
        if (Proxy is { } proxy)
        {
            // This machine's addresses go past it as they always do: the app and its proxy are reached directly.
            arguments.Add($"--proxy-server={proxy}");
        }
        if (PassengerApp.ShowsNoWindow)
        {
            arguments.Add("--headless=new");
        }
        if (OperatingSystem.IsMacOS())
        {
            // Under a HOME with no keychain, the browser would wait for the person's keychain for good before its first request.
            arguments.Add("--use-mock-keychain");
        }
        arguments.AddRange(found.Arguments);
        arguments.Add("about:blank");
        ProcessStartInfo start = ChildProcesses.Command(found.Program, arguments);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process browser;
        try
        {
            browser = await ChildProcesses.StartAsync(start);
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            throw new InvalidOperationException($"Could not start {found.Name} ({found.Program}): {error.Message}");
        }
        TaskCompletionSource refused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue<string> last = new();
        void Heard(string line)
        {
            IdeLog.Write(IdeLog.Debugger, $"[{found.Name}] {line}");
            lock (last)
            {
                if (line.Trim().Length > 0)
                {
                    last.Enqueue(line.Trim());
                    while (last.Count > LastLines)
                    {
                        last.Dequeue();
                    }
                }
            }
            if (ChromiumBrowsers.RefusesSandbox(line))
            {
                refused.TrySetResult();
            }
        }
        Task heard = ChildProcesses.Hear(browser, Heard, Heard, longest: 2000);
        IdeErrors.Observe(heard);
        bool listening = false;
        try
        {
            long began = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(began) < Waits.Of(StartLimit))
            {
                cancel.ThrowIfCancellationRequested();
                if (Port(written) is int port)
                {
                    listening = true;
                    return (browser, port);
                }
                if (!DotnetProcesses.Runs(browser.Id))
                {
                    // What it wrote as it ended may still be on its way through its pipes.
                    await Task.WhenAny(refused.Task, heard, Task.Delay(Waits.Of(LastWords), cancel));
                    cancel.ThrowIfCancellationRequested();
                    if (refused.Task.IsCompleted)
                    {
                        throw new InvalidOperationException(ChromiumBrowsers.SandboxRefused(found));
                    }
                    string[] words;
                    lock (last)
                    {
                        words = [.. last];
                    }
                    int? code = await ExitCodeAsync(browser);
                    return code == 0 || (code is null && words.Length == 0) ? null : throw new InvalidOperationException(EndedSaying(found, code, words));
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancel);
            }
            throw new InvalidOperationException($"{found.Name} did not start its remote debugging within {Waits.Of(StartLimit).TotalSeconds:0} s");
        }
        finally
        {
            if (!listening)
            {
                ChildProcesses.Kill(browser);
                browser.Dispose();
            }
        }
    }

    /// <summary>How many of a browser's last lines say why it ended as it started.</summary>
    private const int LastLines = 4;

    /// <summary>Why a browser ended as it started, in its own words: what it last wrote, and its exit code.</summary>
    public static string EndedSaying(ChromiumFound found, int? code, IReadOnlyList<string> words) =>
        $"{found.Name} ({found.Program}) ended as it started{(code is { } exited ? $" with {ExitCodes.Said(exited)}" : string.Empty)}"
        + (words.Count > 0 ? $", saying: {string.Join(" ", words)}" : ", saying nothing");

    /// <summary>A process's exit code once it has ended, within a moment; null where it cannot be read.</summary>
    private static async Task<int?> ExitCodeAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            return process.ExitCode;
        }
        catch (Exception unread) when (unread is TimeoutException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The port a browser's DevToolsActivePort names, once it is whole; null before.</summary>
    private static int? Port(string written)
    {
        try
        {
            string[] lines = File.ReadAllLines(written);
            return lines.Length >= 2 && int.TryParse(lines[0], out int port) && port > 0 ? port : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The proxy started against the browser's port, once it says where it listens.</summary>
    private static async Task<(Process Proxy, int Port)> StartProxyAsync(string assembly, int browserPort, CancellationToken cancel)
    {
        ProcessStartInfo start = ChildProcesses.DotnetCommand(["exec", assembly, "--DevToolsUrl", $"http://127.0.0.1:{browserPort}", "--OwnerPid", $"{Environment.ProcessId}"],
            Path.GetDirectoryName(assembly));
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        TaskCompletionSource<int> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Process proxy = await ChildProcesses.StartAsync(start);
        void Heard(string line)
        {
            IdeLog.Write(IdeLog.Debugger, $"[debug proxy] {line}");
            if (Listening().Match(line) is { Success: true } said && int.TryParse(said.Groups["port"].Value, out int port))
            {
                listening.TrySetResult(port);
            }
        }
        Task heard = ChildProcesses.Hear(proxy, Heard, Heard, longest: 4000);
        IdeErrors.Observe(heard);
        try
        {
            Task first = await Task.WhenAny(listening.Task, heard).WaitAsync(Waits.Of(StartLimit), cancel);
            if (first != listening.Task)
            {
                throw new InvalidOperationException($".NET's browser debug proxy ({assembly}) ended before it listened: the IDE's log has what it said");
            }
            return (proxy, await listening.Task);
        }
        catch (TimeoutException)
        {
            ChildProcesses.Kill(proxy);
            throw new InvalidOperationException($".NET's browser debug proxy did not listen within {Waits.Of(StartLimit).TotalSeconds:0} s");
        }
        catch
        {
            ChildProcesses.Kill(proxy);
            throw;
        }
    }

    /// <summary>The id of the browser's one page, the blank one it started on.</summary>
    private static async Task<string> PageTargetAsync(int port, CancellationToken cancel)
    {
        using HttpRequestMessage asked = OutboundHttp.Request(HttpMethod.Get, new Uri($"http://127.0.0.1:{port}/json/list"));
        using HttpResponseMessage answer = await Http.SendAsync(asked, cancel);
        answer.EnsureSuccessStatusCode();
        using JsonDocument targets = JsonDocument.Parse(await answer.Content.ReadAsStreamAsync(cancel));
        return targets.RootElement.ValueKind == JsonValueKind.Array
            && targets.RootElement.EnumerateArray().FirstOrDefault(t => JsonMembers.Text(t, "type") == "page") is { ValueKind: JsonValueKind.Object } page
            && JsonMembers.Text(page, "id") is { } id ? id
            : throw new InvalidOperationException("The browser has no page to debug");
    }

    public async ValueTask DisposeAsync()
    {
        await Page.DisposeAsync();
        ChildProcesses.Kill(proxy);
        ChildProcesses.Kill(browser);
        // The browser lets go of its profile as it ends.
        await Task.WhenAny(browser.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(5)));
        Delete(apart);
    }

    private static void Delete(string? folder)
    {
        if (folder is null)
        {
            return;
        }
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A browser's helper may hold a file a moment longer; the cache keeps what is left.
        }
    }

    [GeneratedRegex(@"listening on:?\s+https?://[^:\s]+:(?<port>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Listening();
}
