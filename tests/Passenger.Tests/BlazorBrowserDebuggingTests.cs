using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Blazor app's WebAssembly components debugged in a headless Chromium browser through .NET's browser debug
/// proxy: a Blazor Web App debugs its server on Passenger's engine and its client in a WebAssembly tab beside it, a
/// standalone app in its WebAssembly tab alone; a component's breakpoint stops as the page renders it, Step Over goes
/// to the next line and Variables shows its field; a server component's breakpoint stops in the same run; a page whose
/// browser's network service ended as it loaded is loaded again. A browser that ends as it starts ends its session
/// within seconds. The apps are the SDK's templates, restored from the package cache tests/BlazorGraphs fills. Skipped
/// without a Chromium browser. Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class BlazorBrowserDebuggingTests(StandInAppHost appHost) : IDisposable, IClassFixture<StandInAppHost>
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
            private string Loud => greeting.ToUpperInvariant();

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

    /// <summary>Debug in a browser that ends as it starts ends its session within seconds, in the browser's own
    /// words.</summary>
    [AvaloniaFact]
    public void Debug_in_a_browser_that_ends_as_it_starts_ends_its_session()
    {
        Flow.StepApart(nameof(Debug_in_a_browser_that_ends_as_it_starts_ends_its_session_within_seconds_in_the_browsers_own_words), () => new BlazorBrowserDebuggingTests(appHost),
            test => test.Debug_in_a_browser_that_ends_as_it_starts_ends_its_session_within_seconds_in_the_browsers_own_words());
    }

    /// <summary>A Blazor app's WebAssembly component stops in the browser beside its server, standalone or started by
    /// an AppHost, the browser waking no updater.</summary>
    [AvaloniaFact]
    public void A_blazor_apps_webassembly_stops_in_the_browser()
    {
        Flow.StepApart(nameof(A_Blazor_Web_Apps_server_and_its_WebAssembly_component_both_stop_in_one_run_each_in_its_own_tab), () => new BlazorBrowserDebuggingTests(appHost),
            test => test.A_Blazor_Web_Apps_server_and_its_WebAssembly_component_both_stop_in_one_run_each_in_its_own_tab());
        OpenWindows.CloseAll();
        Flow.StepApart(nameof(A_standalone_WebAssembly_apps_component_stops_in_the_browser_steps_and_shows_its_field), () => new BlazorBrowserDebuggingTests(appHost),
            test => test.A_standalone_WebAssembly_apps_component_stops_in_the_browser_steps_and_shows_its_field());
        OpenWindows.CloseAll();
        Flow.StepApart(nameof(The_browser_a_page_is_debugged_in_starts_no_updater_as_it_runs), () => new BlazorBrowserDebuggingTests(appHost),
            test => test.The_browser_a_page_is_debugged_in_starts_no_updater_as_it_runs());
        OpenWindows.CloseAll();
        Flow.StepApart(nameof(A_page_whose_browsers_network_service_ends_as_it_fetches_its_dotnet_code_is_loaded_again_and_its_component_stops), () => new BlazorBrowserDebuggingTests(appHost),
            test => test.A_page_whose_browsers_network_service_ends_as_it_fetches_its_dotnet_code_is_loaded_again_and_its_component_stops());
        OpenWindows.CloseAll();
        Flow.StepApart(nameof(A_Blazor_Web_App_an_AppHost_starts_debugged_has_its_WebAssembly_component_stop_in_the_browser), () => new BlazorBrowserDebuggingTests(appHost),
            test => test.A_Blazor_Web_App_an_AppHost_starts_debugged_has_its_WebAssembly_component_stop_in_the_browser());
    }

    /// <summary>A Blazor Web App's server stops at its layout's breakpoint as the browser asks for the page, and once it goes
    /// on, that breakpoint gone, the page's WebAssembly component stops at its own in the WebAssembly tab beside the
    /// server's; Step Over goes to the next line, and Variables shows the field the line set.</summary>
    private void A_Blazor_Web_Apps_server_and_its_WebAssembly_component_both_stop_in_one_run_each_in_its_own_tab()
    {
        SkipWithoutBrowser();
        Make("blazor", "Bwa", "--interactivity", "WebAssembly", "--no-https");
        string layout = Path.Combine(folder, "Bwa", "Components", "Layout", "MainLayout.razor");
        File.AppendAllText(layout, "\n@code {\n    private string served = \"on the server\";\n\n    protected override void OnInitialized()\n    {\n        served = served.ToUpperInvariant();\n    }\n}\n");
        string counter = Path.Combine(folder, "Bwa.Client", "Pages", "Counter.razor");
        File.WriteAllText(counter, Counter);
        using HeldPort port = new();
        Profile(Path.Combine(folder, "Bwa", "Properties", "launchSettings.json"), port.Port, HeldPort.GateIn(folder));
        Gate(Path.Combine(folder, "Bwa"));
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
            port.LetGo(HeldPort.GateIn(folder));
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

    /// <summary>A Blazor Web App's page whose browser's network service ends as the page fetches its .NET code (killed here, as
    /// a crash ends it), which leaves the page failing to fetch it for good: its WebAssembly session says so and loads the
    /// page again, and the component stops at its breakpoint. The server holds the page's .NET code until the test lets it
    /// go, so the service ends while the page waits for it.</summary>
    private void A_page_whose_browsers_network_service_ends_as_it_fetches_its_dotnet_code_is_loaded_again_and_its_component_stops()
    {
        SkipWithoutBrowser();
        Make("blazor", "Bwa", "--interactivity", "WebAssembly", "--no-https");
        string counter = Path.Combine(folder, "Bwa.Client", "Pages", "Counter.razor");
        File.WriteAllText(counter, Counter);
        using HeldPort port = new();
        string settings = Path.Combine(folder, "Bwa", "Properties", "launchSettings.json");
        Profile(settings, port.Port, HeldPort.GateIn(folder));
        Gate(Path.Combine(folder, "Bwa"));
        string held = Path.Combine(folder, "code-held");
        HoldCode(Path.Combine(folder, "Bwa"), settings, held);
        Restore.Run(Path.Combine(folder, "Bwa", "Bwa.csproj"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Projects.Count == 2 && window.Projects.All(p => p.IsEvaluated) && window.Configurations.Any(c => c.Name == "Bwa: http"),
                () => $"Bwa and Bwa.Client evaluated with Bwa's http configuration; the window has {string.Join(", ", window.Configurations.Select(c => c.Name))}",
                TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Bwa: http"));
            int set = LineOf(counter, "currentCount = 41;");
            window.Debugger.Breakpoints.Set(counter, set, null);

            Ui.Launched(window, window.DebugAsync(), "Bwa to build and start under the debugger", TimeSpan.FromMinutes(3));
            port.LetGo(HeldPort.GateIn(folder));
            Ui.WaitFor(() => File.Exists(held + Asked) || BrowserRefused(window),
                () => $"the page to ask for its .NET code ({Sessions(window)}; {Ui.Said(window)}; {Consoles(window)}; the browser said: {BrowserSaid()})", TimeSpan.FromMinutes(3));
            Assert.False(BrowserRefused(window), $"No browser asks for the page, as none started: {Ui.Said(window)}");
            int ended = Ui.Await(Waiting.Apart(EndNetworkService), "the browser's network service to be ended");
            Assert.True(ended > 0, $"The browser Passenger started has no network service to end: {BrowserSaid()}");

            string browser = ChromiumBrowsers.Find(null)!.Name;
            Ui.WaitFor(() => Consoles(window).Contains($"{browser}'s network service ended as the page loaded its .NET code, which it could then not fetch: the page is loaded again",
                StringComparison.Ordinal), () => $"the page to be loaded again ({Sessions(window)}; {Consoles(window)}; the browser said: {BrowserSaid()})", TimeSpan.FromMinutes(1));
            File.WriteAllText(held, string.Empty);
            Ui.WaitFor(() => window.Debugger.Session is { Engine: DebugEngine.WebAssembly, State: DebugState.Paused } && Stopped(window, counter) == set,
                () => $"the page's component to stop in the browser once loaded again ({Sessions(window)}; {Ui.Said(window)}; {Consoles(window)}; the browser said: {BrowserSaid()})",
                TimeSpan.FromMinutes(3));
        }
        finally
        {
            File.WriteAllText(held, string.Empty);
            End(window);
        }
    }

    /// <summary>A Blazor Web App an AppHost starts debugged, as DCP asks for it, has its WebAssembly half debugged in the
    /// browser beside its server's tab, at the page its run serves (the address DCP gave it on its launch profile's path):
    /// the stand-in AppHost's service, its page's component stopping at its breakpoint in its WebAssembly tab.</summary>
    private void A_Blazor_Web_App_an_AppHost_starts_debugged_has_its_WebAssembly_component_stop_in_the_browser()
    {
        SkipWithoutBrowser();
        appHost.Reset();
        appHost.BuildApi();
        string made = PathNames.Canonical(Directory.CreateDirectory(appHost.PathOf("Bwa")).FullName);
        MakeIn(made, "blazor", "Bwa", "--interactivity", "WebAssembly", "--no-https");
        string counter = Path.Combine(made, "Bwa.Client", "Pages", "Counter.razor");
        File.WriteAllText(counter, Counter);
        // Held from every other program until the service, started, waits at its gate.
        using HeldPort port = new();
        string gate = HeldPort.GateIn(made);
        Profile(Path.Combine(made, "Bwa", "Properties", "launchSettings.json"), port.Port);
        Gate(Path.Combine(made, "Bwa"));
        string server = PathNames.Canonical(Path.Combine(made, "Bwa", "Bwa.csproj"));
        Restore.Run(server);
        ChildResult built = Ui.Await(ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", server, "-nologo", "-v:q"]), limit: Limit.Scaled()), "Bwa's build", Limit + TestFolders.Spare);
        Assert.True(built.Succeeded, $"Bwa did not build: {built.Output}{built.Error}");
        TestFolders.Write(appHost.Folder, "StandIn.slnx", $"<Solution>\n  <Project Path=\"{StandInAppHost.Name}/{StandInAppHost.Name}.csproj\" />\n  <Project Path=\"{StandInAppHost.Api}/{StandInAppHost.Api}.csproj\" />\n"
            + "  <Project Path=\"Bwa/Bwa/Bwa.csproj\" />\n  <Project Path=\"Bwa/Bwa.Client/Bwa.Client.csproj\" />\n</Solution>\n");
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(appHost.KeyFile), "the stand-in resource service");
        appHost.Serve(backend);
        MainWindow window = appHost.Open(appHost.Program, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == StandInAppHost.Configuration) && window.Projects.Any(p => p.Name == "Bwa.Client" && p.IsEvaluated),
                "the stand-in AppHost's configuration and Bwa's projects evaluated", TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration(StandInAppHost.Configuration));
            int set = LineOf(counter, "currentCount = 41;");
            window.Debugger.Breakpoints.Set(counter, set, null);
            Ui.Launched(window, window.DebugAsync(), "the stand-in AppHost to come up debugged", TimeSpan.FromMinutes(3));
            string address = $"http://localhost:{port.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            (System.Net.WebSockets.ClientWebSocket notified, System.Net.HttpStatusCode answer) = appHost.AskToStartApi("bwa", "Debug",
                new Dictionary<string, string> { [AspNetCoreVariables.Urls] = address, [HeldPort.GateVariable] = gate }, projectPath: server);
            using System.Net.WebSockets.ClientWebSocket subscribed = notified;
            Assert.True((int)answer is >= 200 and < 300, $"The IDE refused Bwa's start: {answer} ({window.Status})");
            Ui.WaitFor(() => window.AspireRuntime.Apps.Single().Services.Any(service => service.ProcessId is > 0), "Bwa started as DCP asked");
            port.LetGo(gate);

            Ui.WaitFor(() => window.Debugger.Session is { Engine: DebugEngine.WebAssembly, State: DebugState.Paused } && Stopped(window, counter) == set || BrowserRefused(window),
                () => $"the service's page component to stop in the browser ({Sessions(window)}; {Ui.Said(window)}; {Consoles(window)}; the service said: "
                    + string.Join("\n", window.AspireRuntime.Apps.SelectMany(a => a.Services).Select(service => service.Console.Document.Snapshot.ToString()))
                    + $"; the browser said: {BrowserSaid()})", TimeSpan.FromMinutes(3));
            Assert.False(BrowserRefused(window), $"Bwa's components in the browser were not debugged: {Ui.Said(window)}");
            DebugTab browser = window.Debugger.Tabs.Single(t => t.Name.EndsWith(" (WebAssembly)", StringComparison.Ordinal));
            Assert.True(browser.Companion is { } beside && beside.Name.Contains("bwa", StringComparison.OrdinalIgnoreCase),
                $"The WebAssembly tab {browser.Name} is beside {browser.Companion?.Name}, not the service's tab");
        }
        finally
        {
            End(window);
            Ui.Await(backend.DisposeAsync().AsTask(), "the stand-in resource service to stop");
        }
    }

    /// <summary>A Blazor Web App whose page is served over HTTPS on a development certificate this machine does not trust has
    /// its components in the browser left undebugged, saying so and how to trust it (dotnet dev-certs https --trust, the
    /// person's to run): the certificate is only checked, never made or trusted, through a stand-in for dev-certs.</summary>
    [AvaloniaFact]
    public void A_Blazor_Web_Apps_https_page_on_an_untrusted_development_certificate_says_so_and_how_to_trust_it()
    {
        SkipWithoutBrowser();
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in for dotnet dev-certs is a shell script");
        Make("blazor", "Bwa", "--interactivity", "WebAssembly", "--no-https");
        string settings = Path.Combine(folder, "Bwa", "Properties", "launchSettings.json");
        // Its server's port of the system's choosing: the page refused, nothing asks it.
        Profile(settings, 0);
        // Its page over HTTPS; the server listening over HTTP, so it starts whatever certificate this machine has.
        JsonNode launch = JsonNode.Parse(File.ReadAllText(settings))!;
        int port = AspireApp.FreePort();
        launch["profiles"]!["http"]!["launchUrl"] = $"https://localhost:{port}/counter";
        File.WriteAllText(settings, launch.ToJsonString());
        Restore.Run(Path.Combine(folder, "Bwa", "Bwa.csproj"));
        string certificates = TestFolders.Create("dev-certs-");
        File.WriteAllText(Path.Combine(certificates, "check"), "7");
        MainWindow window = ContainerRunsTests.Open(folder, devCertificates: DevCerts.In(certificates));
        try
        {
            Ui.WaitFor(() => window.Projects.Count == 2 && window.Projects.All(p => p.IsEvaluated) && window.Configurations.Any(c => c.Name == "Bwa: http"),
                () => $"Bwa's http configuration; the window has {string.Join(", ", window.Configurations.Select(c => c.Name))}", TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Bwa: http"));
            Ui.Launched(window, window.DebugAsync(), "Bwa to build and start under the debugger", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => BrowserRefused(window), () => $"the untrusted certificate said ({Sessions(window)}; {Ui.Said(window)})", TimeSpan.FromMinutes(2));
            Assert.Equal($"Bwa: http's components in the browser are not debugged: the development certificate its page localhost:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                + "is served with is not trusted on this machine, so the browser refuses the page: run dotnet dev-certs https --trust in a terminal, then debug again", Ui.Said(window));
            Assert.Equal("dev-certs https --check --trust", DevCerts.Asked(certificates).Trim());
            Assert.DoesNotContain(window.Debugger.Tabs, t => t.Name.EndsWith(" (WebAssembly)", StringComparison.Ordinal));
        }
        finally
        {
            End(window);
            TestFolders.Delete(certificates);
        }
    }

    /// <summary>A standalone Blazor WebAssembly app debugs in its WebAssembly tab alone, which runs its development server:
    /// its component stops as the page renders it, Step Over goes to the next line, and Variables shows its field, and its
    /// property read by its getter through the proxy once its row is opened.</summary>
    private void A_standalone_WebAssembly_apps_component_stops_in_the_browser_steps_and_shows_its_field()
    {
        SkipWithoutBrowser();
        Make("blazorwasm", "Wasm");
        string counter = Path.Combine(folder, "Pages", "Counter.razor");
        File.WriteAllText(counter, Counter.Replace("@rendermode @(new InteractiveWebAssemblyRenderMode(prerender: false))\n", string.Empty, StringComparison.Ordinal));
        // The development server has no code of the test's to wait at a gate: its port is held until Debug begins its build.
        using HeldPort port = new();
        Profile(Path.Combine(folder, "Properties", "launchSettings.json"), port.Port);
        Restore.Run(Path.Combine(folder, "Wasm.csproj"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        // The browser found, told to keep its own log of every request it makes.
        string netLog = Path.Combine(folder, "net-log.json");
        window.ApplySettings(window.Settings with { Browser = $"\"{ChromiumBrowsers.Find(null)!.Program}\" --log-net-log={netLog}" });
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Wasm: http"), () => $"Wasm's http configuration; the window has {string.Join(", ", window.Configurations.Select(c => c.Name))}",
                TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Wasm: http"));
            int set = LineOf(counter, "currentCount = 41;");
            LineBreakpoint breakpoint = window.Debugger.Breakpoints.Set(counter, set, null);

            port.LetGo();
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

    /// <summary>The browser a standalone WebAssembly app is debugged in starts no updater while the app runs, past the 19 s
    /// after its start at which Chrome begins its updater's work: no process it starts, nor any those start, is an updater,
    /// and nothing it writes is an updater's.</summary>
    private void The_browser_a_page_is_debugged_in_starts_no_updater_as_it_runs()
    {
        SkipWithoutBrowser();
        Make("blazorwasm", "Wasm");
        using HeldPort port = new();
        Profile(Path.Combine(folder, "Properties", "launchSettings.json"), port.Port);
        Restore.Run(Path.Combine(folder, "Wasm.csproj"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        string tag = $"[{ChromiumBrowsers.Find(null)!.Name}] ";
        int logged = Ui.Shared(IdeLog.PathOf(IdeLog.Debugger)).Length;
        List<string> updaters = [];
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Wasm: http"), () => $"Wasm's http configuration; the window has {string.Join(", ", window.Configurations.Select(c => c.Name))}",
                TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Wasm: http"));
            port.LetGo();
            Ui.Launched(window, window.DebugAsync(), "Wasm to build and start in the browser", TimeSpan.FromMinutes(3));
            int browser = 0;
            Ui.WaitFor(() => (browser = DebuggingBrowser()) > 0, () => $"the browser to start ({Sessions(window)}; {Ui.Said(window)})", TimeSpan.FromMinutes(3));
            for (int second = 0; second < 30; second++)
            {
                Ui.Hold(TimeSpan.FromSeconds(1), "the browser's run to go on past the moment Chrome wakes its updater");
                updaters.AddRange(Under(browser).Select(DotnetProcesses.Describe).OfType<DotnetProcess>()
                    .Where(p => p.Name.Contains("Updater", StringComparison.OrdinalIgnoreCase)).Select(p => $"{p.Id} {p.CommandLine}"));
            }
            Assert.True(DotnetProcesses.Runs(browser), $"The browser ended within the 30 s it was watched:\n{BrowserSaid()}");
        }
        finally
        {
            End(window);
        }
        Assert.True(updaters.Count == 0, $"The browser Passenger started for debugging started an updater: {string.Join("; ", updaters.Distinct())}");
        string log = Ui.Shared(IdeLog.PathOf(IdeLog.Debugger));
        string[] said = [.. log[Math.Min(logged, log.Length)..].Split('\n')
            .Where(l => l.Contains(tag, StringComparison.Ordinal) && l.Contains("updater", StringComparison.OrdinalIgnoreCase))];
        Assert.True(said.Length == 0, $"The browser Passenger started for debugging ran an updater, which wrote:\n{string.Join('\n', said.Take(6))}");

        static IEnumerable<int> Under(int id) => DotnetProcesses.StartedBy(id).SelectMany(child => (int[])[child, .. Under(child)]);
    }

    /// <summary>The browser this process started for debugging, by its remote debugging switch; 0 while there is none.</summary>
    private static int DebuggingBrowser() => DotnetProcesses.StartedBy(Environment.ProcessId)
        .FirstOrDefault(id => DotnetProcesses.Describe(id)?.CommandLine.Contains("--remote-debugging-port=", StringComparison.Ordinal) == true);

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

    /// <summary>A browser that ends as it starts with words of its own (here, that it has no display): Debug ends its session
    /// within seconds, saying it in the browser's own words and its exit code, rather than waiting for a page.</summary>
    private void Debug_in_a_browser_that_ends_as_it_starts_ends_its_session_within_seconds_in_the_browsers_own_words()
    {
        Make("blazorwasm", "Wasm");
        // The browser starts once the page listens; the development server has no code of the test's to wait at a gate, so its
        // port is held until Debug begins its build.
        using HeldPort port = new();
        Profile(Path.Combine(folder, "Properties", "launchSettings.json"), port.Port);
        Restore.Run(Path.Combine(folder, "Wasm.csproj"));
        const string Words = "[4242:4242:1102/120000.000000:ERROR:ozone_platform_x11.cc(240)] Missing X server or $DISPLAY";
        string browser = ExitingBrowser(Words);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ApplySettings(window.Settings with { Browser = browser });
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Wasm: http"), () => $"Wasm's http configuration; the window has {string.Join(", ", window.Configurations.Select(c => c.Name))}",
                TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Wasm: http"));
            port.LetGo();
            Task<Words?> launch = window.DebugAsync();
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

    /// <summary>Step Over from the component's first line goes to the next, and Variables shows the field it set under this,
    /// and a property read by its getter as its row is opened.</summary>
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
        // A property the proxy reads only by its getter, read once its row is opened.
        VariableNode loud = self.Children!.First(c => c.Name == "Loud");
        Assert.True(loud.IsLazy, $"Variables read Loud before its row was opened, as {loud.Value}");
        Task read = view.Tree.Expand(loud);
        Ui.WaitFor(() => read.IsCompleted && loud.ReadAs is not null, "Loud's getter to be read");
        Assert.True(loud.ReadAs is { Value: "\"HELLO\"", Type: "string" },
            $"Loud's getter read as {loud.ReadAs!.Value} ({loud.ReadAs.Type}), where it returns \"HELLO\"");
    }

    /// <summary>The app's http profile at the port given (0: one of the system's choosing), opening the counter's page, its
    /// program told of the gate it waits at before it listens where one is given (<see cref="HeldPort"/>).</summary>
    private static void Profile(string settings, int port, string? gate = null)
    {
        JsonNode launch = JsonNode.Parse(File.ReadAllText(settings))!;
        JsonNode http = launch["profiles"]!["http"]!;
        http["applicationUrl"] = port == 0 ? "http://127.0.0.1:0" : $"http://localhost:{port}";
        http["launchUrl"] = "counter";
        http["launchBrowser"] = false;
        if (gate is not null)
        {
            ((http["environmentVariables"] ??= new JsonObject()).AsObject())[HeldPort.GateVariable] = gate;
        }
        File.WriteAllText(settings, launch.ToJsonString());
    }

    /// <summary>The server project's Program.cs made to wait at its gate before it listens (<see cref="HeldPort.Gated"/>).</summary>
    private static void Gate(string project)
    {
        string program = Path.Combine(project, "Program.cs");
        File.WriteAllText(program, HeldPort.Gated(File.ReadAllText(program)));
    }

    /// <summary>What a server of <see cref="HoldCode"/> writes after its held file's name once the page first asks for its code.</summary>
    private const string Asked = ".asked";

    /// <summary>The server project's Program.cs made to hold each request for the page's .NET code (a .wasm file) until the
    /// file <paramref name="held"/> is there, writing <see cref="Asked"/> after its name at the first; its http profile in
    /// <paramref name="settings"/> names the file to the program.</summary>
    private static void HoldCode(string project, string settings, string held)
    {
        string program = Path.Combine(project, "Program.cs");
        string text = File.ReadAllText(program);
        const string Built = "var app = builder.Build();";
        Assert.Contains(Built, text, StringComparison.Ordinal);
        File.WriteAllText(program, text.Replace(Built, Built + "\n" + $$"""
            if (System.Environment.GetEnvironmentVariable("CODE_HELD") is { Length: > 0 } codeHeld)
            {
                int asked = 0;
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.Value?.EndsWith(".wasm", System.StringComparison.Ordinal) == true)
                    {
                        if (System.Threading.Interlocked.Exchange(ref asked, 1) == 0)
                        {
                            System.IO.File.WriteAllText(codeHeld + "{{Asked}}", string.Empty);
                        }
                        while (!System.IO.File.Exists(codeHeld))
                        {
                            await System.Threading.Tasks.Task.Delay(50);
                        }
                    }
                    await next();
                });
            }

            """, StringComparison.Ordinal));
        JsonNode launch = JsonNode.Parse(File.ReadAllText(settings))!;
        ((launch["profiles"]!["http"]!["environmentVariables"] ??= new JsonObject()).AsObject())["CODE_HELD"] = held;
        File.WriteAllText(settings, launch.ToJsonString());
    }

    /// <summary>Ends the network service of the browser this process started for debugging, alone, as a crash ends it: each
    /// of the browser's processes whose command line names the service. How many it ended.</summary>
    private static int EndNetworkService()
    {
        static IEnumerable<int> Under(int id) => DotnetProcesses.StartedBy(id).SelectMany(child => (int[])[child, .. Under(child)]);
        int[] services = [.. DotnetProcesses.StartedBy(Environment.ProcessId)
            .Where(id => DotnetProcesses.Describe(id)?.CommandLine.Contains("--remote-debugging-port=", StringComparison.Ordinal) == true)
            .SelectMany(Under).Where(id => DotnetProcesses.Describe(id)?.CommandLine.Contains("network.mojom.NetworkService", StringComparison.Ordinal) == true)];
        foreach (int service in services)
        {
            OwnProcess.Of(service).Kill(alone: true, waited: true);
        }
        return services.Length;
    }

    /// <summary>The last lines the debugging browser wrote, as the IDE's log keeps them, but for its verbose ones (the system
    /// updater's it starts) and its headless display's.</summary>
    private static string BrowserSaid()
    {
        string tag = $"[{ChromiumBrowsers.Find(null)?.Name}] ";
        return string.Join("\n", Ui.Shared(IdeLog.PathOf(IdeLog.Debugger)).Split('\n')
            .Where(l => l.Contains(tag, StringComparison.Ordinal) && !l.Contains(":VERBOSE", StringComparison.Ordinal) && !l.Contains("CVDisplayLink", StringComparison.Ordinal))
            .TakeLast(12));
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
            Assert.Null(window.Actions.Run(AppActions.StopAll)?.Shown);
            Ui.WaitFor(() => !window.Debugger.IsDebugging && !window.IsRunning, () => $"the app to end ({Sessions(window)})", TimeSpan.FromMinutes(1));
        }
        window.Close();
    }

    /// <summary>The app of an SDK template, made in the test's folder without its restore, restoring from the package cache alone.</summary>
    private void Make(string template, string name, params string[] options) => MakeIn(folder, template, name, options);

    /// <summary>The app of an SDK template made in a folder of the test's (<see cref="Make(string, string, string[])"/>).</summary>
    private static void MakeIn(string folder, string template, string name, params string[] options)
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
