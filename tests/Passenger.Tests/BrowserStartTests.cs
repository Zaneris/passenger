using Passenger.App.Debugging;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A browser's start for debugging given up as it waits for the browser to say its port, against a stand-in that never
/// does: no browser is left running, on the kept profile or, another debugging holding that, on a profile of the moment,
/// which goes with it.</summary>
[Collection(Beside.Name)]
public sealed class BrowserStartTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("browser-start-"));

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>A stand-in browser that hands its page over and ends the first time, where a browser holds the profile, and
    /// else waits for good, saying no port.</summary>
    private string Browser(bool handsOver)
    {
        string once = Path.Combine(folder, "handed-over");
        if (!handsOver)
        {
            File.WriteAllText(once, string.Empty);
        }
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(folder, "chromium.cmd");
            File.WriteAllText(script, $"@echo off\r\nif not exist \"{once}\" (\r\n  type nul > \"{once}\"\r\n  exit /b 0\r\n)\r\nping -n 600 127.0.0.1 > nul\r\n");
            return script;
        }
        string program = Path.Combine(folder, "chromium");
        File.WriteAllText(program, $"#!/bin/sh\nif [ ! -e '{once}' ]; then\n  : > '{once}'\n  exit 0\nfi\nexec sleep 600\n");
        File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return program;
    }

    /// <summary>The start given up once so many browsers have begun, the last going on waiting: the process it was.</summary>
    private async Task<int> GivenUpAsync(string program, string profiles, int browsers)
    {
        int begun = 0;
        int last = 0;
        void Heard(object? sender, ChildStart child)
        {
            if (child.Start?.FileName == program)
            {
                Volatile.Write(ref last, child.ProcessId);
                Interlocked.Increment(ref begun);
            }
        }
        ChildProcesses.Started += Heard;
        using CancellationTokenSource giving = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            BrowserDebugging debugging = new(new ChromiumFound(program, "Stand-in"), Path.Combine(folder, "none.dll"), new Uri("http://localhost/"), []);
            Task<BrowserRun> starting = BrowserRun.StartAsync(debugging, profiles, _ => { }, giving.Token);
            Assert.True(await Waiting.UntilAsync(() => Volatile.Read(ref begun) == browsers, TimeSpan.FromSeconds(30), giving.Token), "The stand-in browser never came to wait for good");
            await giving.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
            return last;
        }
        finally
        {
            ChildProcesses.Started -= Heard;
        }
    }

    [Fact]
    public async Task A_start_given_up_as_the_browser_starts_leaves_no_browser_running()
    {
        int browser = await GivenUpAsync(Browser(handsOver: false), Path.Combine(folder, "profiles"), browsers: 1);
        Assert.True(await Waiting.UntilAsync(() => !DotnetProcesses.Runs(browser), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "A browser whose start was given up runs on with nothing holding it");
    }

    [Fact]
    public async Task A_start_given_up_on_a_profile_of_the_moment_leaves_neither_browser_nor_profile()
    {
        string profiles = Path.Combine(folder, "profiles");
        int browser = await GivenUpAsync(Browser(handsOver: true), profiles, browsers: 2);
        Assert.True(await Waiting.UntilAsync(() => !DotnetProcesses.Runs(browser), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "A browser whose start was given up runs on with nothing holding it");
        Assert.True(!Directory.EnumerateDirectories(profiles, "browser-*").Any(), "A profile made for the one start was left behind");
    }
}
