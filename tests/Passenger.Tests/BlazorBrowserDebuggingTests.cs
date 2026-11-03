using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Blazor app's WebAssembly components debugged in a headless Chromium browser through .NET's browser debug proxy:
/// a Blazor Web App debugs its server on Passenger's engine and its client in a WebAssembly tab beside it, a standalone app
/// in its WebAssembly tab alone; a component's breakpoint stops as the page renders it, Step Over goes to the next line
/// and Variables shows its field; a server component's breakpoint stops in the same run. With no browser where Settings
/// names one, Debug says so and how to get one, and of one the system refuses its sandbox, why and how to run one. The apps
/// are the SDK's templates, restored from the package cache tests/BlazorGraphs fills. Skipped without a Chromium browser.
/// Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class BlazorBrowserDebuggingTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("blazor-browser-"));

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>The longest dotnet new may take.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    /// <summary>The component the browser renders: its OnInitialized sets its fields a line at a time.</summary>
    private const string Counter = """
        @page "/counter"
        @rendermode @(new InteractiveWebAssemblyRenderMode(prerender: false))

        <p role="status">Current count: @currentCount</p>

        @code {
            private int currentCount;
            private string greeting = "hello";

            protected override void OnInitialized()
            {
                currentCount = 41;
                currentCount++;
                greeting = greeting + " world";
            }
        }

        """;

    private static void SkipWithoutBrowser() =>
        Assert.SkipWhen(ChromiumBrowsers.Find(null) is null, "No Chromium browser (Chrome, Edge, Chromium or Brave) is on this machine to debug WebAssembly in");

    /// <summary>A Blazor Web App's server stops at its layout's breakpoint as the browser asks for the page, and once it goes
    /// on, that breakpoint gone, the page's WebAssembly component stops at its own in the WebAssembly tab beside the
    /// server's; Step Over goes to the next line, and Variables shows the field the line set.</summary>
    [AvaloniaFact]
    public void A_Blazor_Web_Apps_server_and_its_WebAssembly_component_both_stop_in_one_run_each_in_its_own_tab()
    {
        SkipWithoutBrowser();
        Make("blazor", "Bwa", "--interactivity", "WebAssembly", "--no-https");
        string layout = Path.Combine(folder, "Bwa", "Components", "Layout", "MainLayout.razor");
        File.AppendAllText(layout, "\n@code {\n    private string served = \"on the server\";\n\n    protected override void OnInitialized()\n    {\n        served = served.ToUpperInvariant();\n    }\n}\n");
        string counter = Path.Combine(folder, "Bwa.Client", "Pages", "Counter.razor");
        File.WriteAllText(counter, Counter);
        Profile(Path.Combine(folder, "Bwa", "Properties", "launchSettings.json"));
        Restore.Run(Path.Combine(folder, "Bwa", "Bwa.csproj"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Projects.Count == 2 && window.Projects.All(p => p.IsEvaluated) && window.Configurations.Any(c => c.Name == "Bwa: http"),
                () => $"Bwa and Bwa.Client evaluated with Bwa's http configuration; the window has {string.Join(", ", window.Configurations.Select(c => c.Name))}",
                TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Bwa: http"));
            int served = LineOf(layout, "served = served.ToUpperInvariant();");
            int set = LineOf(counter, "currentCount = 41;");
            LineBreakpoint onServer = window.Debugger.Breakpoints.Set(layout, served, null);
            window.Debugger.Breakpoints.Set(counter, set, null);

            Ui.Launched(window, window.DebugAsync(), "Bwa to build and start under the debugger", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.Debugger.Session is { Engine: DebugEngine.Managed, State: DebugState.Paused } && Stopped(window, layout) == served || BrowserRefused(window),
                () => $"the server to stop in its layout as the browser asks for the page ({Sessions(window)}; {Ui.Said(window)}; {Consoles(window)})", TimeSpan.FromMinutes(3));
            Assert.False(BrowserRefused(window), $"No browser asks for the page, as none started: {Ui.Said(window)}");
            // The layout renders again for each page the browser asks for (its icon's not-found page too), which would hold the page's own files.
            window.Debugger.Breakpoints.Remove(onServer);
            Ui.Run(window, AppActions.Resume);

            string tab = DebugController.BrowserTab("Bwa: http");
            Ui.WaitFor(() => window.Debugger.Session is { Engine: DebugEngine.WebAssembly, State: DebugState.Paused } && Stopped(window, counter) == set,
                () => $"the page's component to stop in the browser ({Sessions(window)}; {Ui.Said(window)}; {Consoles(window)})", TimeSpan.FromMinutes(3));
            Assert.Contains(window.Debugger.Tabs, t => t.Name == tab && t.Companion?.Name == "Bwa: http");
            SteppedWithItsField(window, counter, set);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A standalone Blazor WebAssembly app debugs in its WebAssembly tab alone, which runs its development server:
    /// its component stops as the page renders it, Step Over goes to the next line, and Variables shows its field.</summary>
    [AvaloniaFact]
    public void A_standalone_WebAssembly_apps_component_stops_in_the_browser_steps_and_shows_its_field()
    {
        SkipWithoutBrowser();
        Make("blazorwasm", "Wasm");
        string counter = Path.Combine(folder, "Pages", "Counter.razor");
        File.WriteAllText(counter, Counter.Replace("@rendermode @(new InteractiveWebAssemblyRenderMode(prerender: false))\n", string.Empty, StringComparison.Ordinal));
        Profile(Path.Combine(folder, "Properties", "launchSettings.json"));
        Restore.Run(Path.Combine(folder, "Wasm.csproj"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        // The browser found, told to keep its own log of every request it makes.
        string netLog = Path.Combine(folder, "net-log.json");
        window.ApplySettings(window.Settings with { DebugBrowser = $"\"{ChromiumBrowsers.Find(null)!.Program}\" --log-net-log={netLog}" });
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Wasm: http"), () => $"Wasm's http configuration; the window has {string.Join(", ", window.Configurations.Select(c => c.Name))}",
                TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Wasm: http"));
            int set = LineOf(counter, "currentCount = 41;");
            LineBreakpoint breakpoint = window.Debugger.Breakpoints.Set(counter, set, null);

            Ui.Launched(window, window.DebugAsync(), "Wasm to build and start in the browser", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.Debugger.Session is { Engine: DebugEngine.WebAssembly, State: DebugState.Paused } && Stopped(window, counter) == set,
                () => $"the page's component to stop in the browser ({Sessions(window)}; {Ui.Said(window)}; {Consoles(window)})", TimeSpan.FromMinutes(3));
            Assert.Equal(["Wasm: http"], window.Debugger.Tabs.Select(t => t.Name));
            Assert.Equal("bound", BreakpointStates.StateOf(breakpoint, window.Debugger.IsDebugging, muted: false));
            SteppedWithItsField(window, counter, set);
        }
        finally
        {
            End(window);
        }
        // Its own log of every connection it made: to this machine alone (the app, the debug proxy, the test run's dead proxy).
        Ui.WaitFor(() => File.Exists(netLog), "the browser's network log");
        string[] reached = Reached(netLog);
        Assert.Contains(reached, address => address.StartsWith("127.0.0.1:", StringComparison.Ordinal) || address.StartsWith("[::1]:", StringComparison.Ordinal));
        string[] elsewhere = [.. reached.Where(address => !address.StartsWith("127.0.0.1:", StringComparison.Ordinal) && !address.StartsWith("[::1]:", StringComparison.Ordinal))];
        Assert.True(elsewhere.Length == 0, $"The browser Passenger started for debugging connected beyond this machine: {string.Join(", ", elsewhere)}");
    }

    /// <summary>The addresses a browser's network log says it sent to: each TCP connect's attempt, and each UDP socket's
    /// that sent bytes (a UDP connect alone sends nothing: the browser's check that IPv6 has a route makes one). Read an
    /// event a line, as a browser ended before its log's close leaves its last line cut.</summary>
    private static string[] Reached(string netLog)
    {
        string[] lines = Ui.Shared(netLog).Split('\n');
        JsonNode constants = JsonNode.Parse(lines[0].TrimEnd().TrimEnd(',') + "}")!["constants"]!["logEventTypes"]!;
        int attempt = constants["TCP_CONNECT_ATTEMPT"]!.GetValue<int>(), connect = constants["UDP_CONNECT"]!.GetValue<int>(),
            sent = constants["UDP_BYTES_SENT"]!.GetValue<int>();
        HashSet<string> reached = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<long, string> sockets = [];
        HashSet<long> sending = [];
        foreach (string line in lines.Skip(1).Select(l => l.TrimEnd().TrimEnd(',', ']')).Where(l => l.StartsWith('{') && l.EndsWith('}')))
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }
            int type = node!["type"]!.GetValue<int>();
            long source = node["source"]!["id"]!.GetValue<long>();
            string? address = node["params"]?["address"]?.GetValue<string>();
            if (type == attempt && address is not null)
            {
                reached.Add(address);
            }
            else if (type == connect && address is not null)
            {
                sockets[source] = address;
            }
            else if (type == sent)
            {
                sending.Add(source);
            }
        }
        reached.UnionWith(sockets.Where(s => sending.Contains(s.Key)).Select(s => s.Value));
        return [.. reached];
    }

    /// <summary>With no browser where Settings names one, Debug of a standalone app runs its development server under the
    /// debugger as before, saying its components in the browser are not debugged, why, and how to get a browser.</summary>
    [AvaloniaFact]
    public void Debug_with_no_browser_where_Settings_names_one_says_so_and_how_to_get_one()
    {
        Make("blazorwasm", "Wasm");
        Profile(Path.Combine(folder, "Properties", "launchSettings.json"));
        Restore.Run(Path.Combine(folder, "Wasm.csproj"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            string nowhere = Path.Combine(folder, "no-browser", "chrome");
            window.ApplySettings(window.Settings with { DebugBrowser = nowhere });
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Wasm: http"), () => $"Wasm's http configuration; the window has {string.Join(", ", window.Configurations.Select(c => c.Name))}",
                TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Wasm: http"));
            Ui.Launched(window, window.DebugAsync(), "Wasm's development server to start under the debugger", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => window.Debugger.Session?.State is DebugState.Running or DebugState.Ended, "the development server to run under the debugger", TimeSpan.FromMinutes(1));
            string said = window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
            Assert.Contains($"names {nowhere} as the browser to debug them in, and there is no program there", said, StringComparison.Ordinal);
            Assert.Contains("install Google Chrome (google.com/chrome) or Microsoft Edge (microsoft.com/edge)", said, StringComparison.Ordinal);
            Assert.Contains("and their breakpoints are not hit: only the development server that serves them runs under the debugger", said, StringComparison.Ordinal);
            Assert.Equal(DebugEngine.Managed, window.Debugger.Session?.Engine);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A browser that ends as it starts with Chromium's word that the system refused its sandbox (Ubuntu's AppArmor
    /// for a Chromium with no profile of its own): Debug says so plainly, naming the browser, the packages that carry their
    /// profile and Chromium's page on giving it one, and never passes it --no-sandbox.</summary>
    [AvaloniaFact]
    public void Debug_in_a_browser_the_system_refuses_its_sandbox_says_so_naming_the_browser_and_the_fixes()
    {
        Make("blazorwasm", "Wasm");
        Profile(Path.Combine(folder, "Properties", "launchSettings.json"));
        Restore.Run(Path.Combine(folder, "Wasm.csproj"));
        string started = Path.Combine(folder, "browser-started");
        string browser = SandboxRefusingBrowser(started);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ApplySettings(window.Settings with { DebugBrowser = browser });
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Wasm: http"), () => $"Wasm's http configuration; the window has {string.Join(", ", window.Configurations.Select(c => c.Name))}",
                TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Wasm: http"));
            Task<string?> launch = window.DebugAsync();
            string Words() => $"{Ui.Said(window)}\n{Consoles(window)}";
            Ui.WaitFor(() => launch.IsCompleted && Words().Contains("the system refused it its sandbox", StringComparison.Ordinal),
                () => $"Debug to say the system refused the browser its sandbox ({Sessions(window)}; it said: {Words()})", TimeSpan.FromMinutes(3));
            string said = Words();
            Assert.Contains($"({browser}) ended as it started: the system refused it its sandbox", said, StringComparison.Ordinal);
            Assert.Contains("Install Google Chrome or Microsoft Edge from their own packages", said, StringComparison.Ordinal);
            Assert.Contains($"an AppArmor profile that allows it user namespaces: {ChromiumBrowsers.SandboxPage}", said, StringComparison.Ordinal);
            string[] arguments = File.ReadAllLines(started);
            Assert.NotEmpty(arguments);
            Assert.DoesNotContain(arguments, a => a.Contains("--no-sandbox", StringComparison.Ordinal));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A browser that ends as it starts with words of its own (here, that it has no display): Debug ends its session
    /// within seconds, saying it in the browser's own words and its exit code, rather than waiting for a page.</summary>
    [AvaloniaFact]
    public void Debug_in_a_browser_that_ends_as_it_starts_ends_its_session_within_seconds_in_the_browsers_own_words()
    {
        Make("blazorwasm", "Wasm");
        Profile(Path.Combine(folder, "Properties", "launchSettings.json"));
        Restore.Run(Path.Combine(folder, "Wasm.csproj"));
        const string Words = "[4242:4242:1102/120000.000000:ERROR:ozone_platform_x11.cc(240)] Missing X server or $DISPLAY";
        string browser = ExitingBrowser(Words);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ApplySettings(window.Settings with { DebugBrowser = browser });
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Wasm: http"), () => $"Wasm's http configuration; the window has {string.Join(", ", window.Configurations.Select(c => c.Name))}",
                TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Wasm: http"));
            Task<string?> launch = window.DebugAsync();
            string Said() => $"{Ui.Said(window)}\n{Consoles(window)}";
            // Its build and its development server's start first; the browser's end then ends the session at once.
            Ui.WaitFor(() => launch.IsCompleted && Said().Contains("ended as it started", StringComparison.Ordinal),
                () => $"Debug to end, saying the browser ended as it started ({Sessions(window)}; it said: {Said()})", TimeSpan.FromMinutes(3));
            Assert.Contains($"({browser}) ended as it started with exit code 3, saying: {Words}", Said(), StringComparison.Ordinal);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, () => $"the session to end with its browser ({Sessions(window)})", TimeSpan.FromSeconds(20));
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>A stand-in browser that writes <paramref name="words"/> to its standard error and ends with exit code 3.</summary>
    private string ExitingBrowser(string words)
    {
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(folder, "ending.cmd");
            File.WriteAllText(script, $"@echo off\r\necho {words} 1>&2\r\nexit /b 3\r\n");
            return script;
        }
        string program = Path.Combine(folder, "ending");
        File.WriteAllText(program, $"#!/bin/sh\necho '{words}' >&2\nexit 3\n");
        File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return program;
    }

    /// <summary>A stand-in Chromium that writes each start's arguments to <paramref name="started"/>, then Chromium's fatal
    /// word that it has no usable sandbox, and ends as Chromium's abort does.</summary>
    private string SandboxRefusingBrowser(string started)
    {
        const string Fatal = "[4242:4242:1102/120000.000000:FATAL:zygote_host_impl_linux.cc(128)] No usable sandbox! If you are running on Ubuntu 23.10+ "
            + "or another Linux distro that has disabled unprivileged user namespaces with AppArmor, see "
            + "https://chromium.googlesource.com/chromium/src/+/main/docs/security/apparmor-userns-restrictions.md.";
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(folder, "chromium.cmd");
            File.WriteAllText(script, $"@echo off\r\necho %* >> \"{started}\"\r\necho {Fatal} 1>&2\r\nexit /b 133\r\n");
            return script;
        }
        string program = Path.Combine(folder, "chromium");
        File.WriteAllText(program, $"#!/bin/sh\nprintf '%s\\n' \"$*\" >> '{started}'\necho '{Fatal}' >&2\nexit 133\n");
        File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return program;
    }

    /// <summary>Step Over from the component's first line goes to the next, and Variables shows the field it set under this.</summary>
    private static void SteppedWithItsField(MainWindow window, string counter, int set)
    {
        Ui.Run(window, AppActions.StepOver);
        Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Paused } && Stopped(window, counter) == set + 1,
            () => $"Step Over to go to the next line, {set + 2} ({Sessions(window)}, the editor at {Stopped(window, counter) + 1})", TimeSpan.FromMinutes(1));
        DebugModel view = window.BottomPanel.Debug;
        Ui.WaitFor(() => view.Tree.Rows.Any(r => r.Depth == 0 && r.Name == "this"), () => $"the component as this in Variables ({Rows(view)})");
        VariableNode self = view.Tree.Rows.First(r => r.Depth == 0 && r.Name == "this");
        Task opened = view.Tree.Expand(self);
        Ui.WaitFor(() => opened.IsCompleted, "this's rows");
        Assert.True((self.Children ?? []).Any(c => c.Name == "currentCount" && c.Value == "41" && c.Type == "int"),
            $"Variables showed the component's field set by the line stepped over as {string.Join("; ", (self.Children ?? []).Select(c => $"{c.Name} = {c.Value} ({c.Type})"))}");
        Assert.Contains(self.Children!, c => c.Name == "greeting" && c.Value == "\"hello\"");
    }

    /// <summary>The app's http profile at a free port, opening the counter's page.</summary>
    private static void Profile(string settings)
    {
        JsonNode launch = JsonNode.Parse(File.ReadAllText(settings))!;
        JsonNode http = launch["profiles"]!["http"]!;
        http["applicationUrl"] = $"http://localhost:{AspireApp.FreePort()}";
        http["launchUrl"] = "counter";
        http["launchBrowser"] = false;
        File.WriteAllText(settings, launch.ToJsonString());
    }

    /// <summary>The line (from zero) of a file's text.</summary>
    private static int LineOf(string path, string code) => File.ReadAllLines(path).ToList().FindIndex(l => l.Contains(code, StringComparison.Ordinal)) is int line and >= 0 ? line
        : throw new InvalidOperationException($"{Path.GetFileName(path)} has no line with {code}");

    /// <summary>The line (from zero) the window shows the program stopped at in a file, or -1.</summary>
    private static int Stopped(MainWindow window, string path) =>
        window.EditorArea.Active is { } active && PathNames.Same(active.Path, path)
        && window.EditorArea.CodeEditor.LineMarks.FirstOrDefault(m => m.Kind == LineMarkKind.ExecutionPoint) is { } mark ? mark.Line : -1;

    /// <summary>Whether the window said a Blazor Web App's components in the browser are not debugged, as its browser could not start.</summary>
    private static bool BrowserRefused(MainWindow window) => window.Status?.Contains("components in the browser are not debugged", StringComparison.Ordinal) == true;

    private static string Sessions(MainWindow window) =>
        string.Join(", ", window.Debugger.Tabs.Select(t => $"{t.Name}: {t.Session?.State.ToString() ?? "none"}"));

    private static string Consoles(MainWindow window) =>
        string.Join("\n---\n", window.Debugger.Tabs.Select(t => $"{t.Name}:\n{t.Console.Document.Snapshot}"));

    private static string Rows(DebugModel view) => string.Join("; ", view.Tree.Rows.Where(r => r.Depth == 0).Select(r => $"{r.Name} = {r.Value} ({r.Type})"));

    private static void End(MainWindow window)
    {
        if (window.Debugger.IsDebugging || window.IsRunning)
        {
            Assert.Null(window.Actions.Run(AppActions.StopAll));
            Ui.WaitFor(() => !window.Debugger.IsDebugging && !window.IsRunning, () => $"the app to end ({Sessions(window)})", TimeSpan.FromMinutes(1));
        }
        window.Close();
    }

    /// <summary>The app of an SDK template, made in the test's folder without its restore, restoring from the package cache alone.</summary>
    private void Make(string template, string name, params string[] options)
    {
        Ui.AwaitPumped(Waiting.Apart(async () =>
        {
            ChildResult made = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(
                ["new", template, "--output", folder, "--name", name, "--framework", TestProjects.Framework, "--no-restore", .. options]), limit: Limit);
            Assert.True(made.Succeeded, $"dotnet new {template} did not make the test's app (exit code {made.ExitCode}):\n{made.Output}{made.Error}");
            return made;
        }), $"dotnet new {template} to make the test's app", Limit + TestFolders.Spare);
        TestFolders.Write(folder, "nuget.config", "<configuration>\n  <packageSources><clear /><add key=\"local\" value=\"feed\" /></packageSources>\n</configuration>\n");
        Directory.CreateDirectory(Path.Combine(folder, "feed"));
    }
}
