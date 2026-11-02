using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Dap;

namespace Passenger.App.Debugging;

/// <summary>The browser and debug proxy one debugging of a WebAssembly app runs: Chromium started with remote debugging on a
/// profile of Passenger's own (the open folder's, kept, so its sign-ins stay; a profile of the moment when another debugging
/// holds it), never the person's own browser, headless where the IDE is; .NET's proxy pointed at it; and the conversation
/// with the browser's page through the proxy, which adds .NET's code to what the page says. Ending it ends both programs.</summary>
public sealed partial class BrowserRun : IAsyncDisposable
{
    /// <summary>How long the browser and the proxy each have to say where they listen.</summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(60);

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

    /// <summary>Whether the browser runs headless: in a headless IDE (a test's, a screenshot's or an agent's own), which shows
    /// no window to anyone.</summary>
    public static bool Headless => Mcp.McpHost.IsHeadless || Mcp.McpProxy.StartsHeadless;

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
            started = await StartBrowserAsync(debugging.Browser, apart, cancel)
                ?? throw new InvalidOperationException($"{debugging.Browser.Name} ended as it started, with no remote debugging");
        }
        (Process browser, int port) = started.Value;
        Process? proxy = null;
        try
        {
            say($"{debugging.Browser.Name} started{(Headless ? " headless" : string.Empty)} for debugging, on a profile of Passenger's own");
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

    /// <summary>The browser started on a profile with remote debugging on a port it chooses, once it has written which
    /// (DevToolsActivePort in the profile); null where it ended first, handing its page to another browser on that profile.</summary>
    private static async Task<(Process Browser, int Port)?> StartBrowserAsync(ChromiumFound found, string profile, CancellationToken cancel)
    {
        Containers.OwnFiles.Folder(profile);
        string written = Path.Combine(profile, "DevToolsActivePort");
        File.Delete(written);
        List<string> arguments = ["--remote-debugging-port=0", $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check",
            "--disable-search-engine-choice-screen", "--disable-background-timer-throttling", "--disable-renderer-backgrounding"];
        if (Headless)
        {
            arguments.Add("--headless=new");
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
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new InvalidOperationException($"Could not start {found.Name} ({found.Program}): {error.Message}");
        }
        IdeErrors.Observe(ChildProcesses.Hear(browser, line => IdeLog.Write(IdeLog.Debugger, $"[{found.Name}] {line}"),
            line => IdeLog.Write(IdeLog.Debugger, $"[{found.Name}] {line}"), longest: 2000));
        long began = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(began) < Waits.Of(StartLimit))
        {
            cancel.ThrowIfCancellationRequested();
            if (Port(written) is int port)
            {
                return (browser, port);
            }
            if (!DotnetProcesses.Runs(browser.Id))
            {
                return null;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancel);
        }
        ChildProcesses.Kill(browser);
        throw new InvalidOperationException($"{found.Name} did not start its remote debugging within {Waits.Of(StartLimit).TotalSeconds:0} s");
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
