using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Templates;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Blazor's WebAssembly apps as the SDK's templates make them: a standalone app runs through the development
/// server its project's RunCommand names, answering HTTP, and with no browser to debug it in Debug runs it so too,
/// saying its components in the browser are not debugged, a component's breakpoint saying it is never hit and Problems
/// listing it; Its packages come from the package cache tests/BlazorGraphs fills, never nuget.org. Debugged through the
/// window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class BlazorWebAssemblyTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("blazor-wasm-"));

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>The longest dotnet new may take.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    /// <summary>A standalone WebAssembly app runs, and with no browser debugs through its development server.</summary>
    [AvaloniaFact]
    public void A_standalone_webassembly_app_runs_and_debugs_through_its_development_server()
    {
        Flow.StepApart<BlazorWebAssemblyTests>(nameof(A_standalone_app_runs_and_with_no_browser_debugs_through_its_development_server_saying_its_components_in_the_browser_are_not_debugged),
            test => test.A_standalone_app_runs_and_with_no_browser_debugs_through_its_development_server_saying_its_components_in_the_browser_are_not_debugged());
    }

    private void A_standalone_app_runs_and_with_no_browser_debugs_through_its_development_server_saying_its_components_in_the_browser_are_not_debugged()
    {
        Make("blazorwasm", "Wasm");
        string settings = Path.Combine(folder, "Properties", "launchSettings.json");
        JsonNode launch = JsonNode.Parse(File.ReadAllText(settings))!;
        // A port of the system's choosing, which its development server says as it listens; no page opened at it.
        launch["profiles"]!["http"]!["applicationUrl"] = "http://127.0.0.1:0";
        launch["profiles"]!["http"]!["launchBrowser"] = false;
        File.WriteAllText(settings, launch.ToJsonString());
        TestFolders.Write(folder, "nuget.config", "<configuration>\n  <packageSources><clear /><add key=\"local\" value=\"feed\" /></packageSources>\n</configuration>\n");
        Directory.CreateDirectory(Path.Combine(folder, "feed"));
        Restore.Run(Path.Combine(folder, "Wasm.csproj"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            string nowhere = Path.Combine(folder, "no-browser", "chrome");
            window.ApplySettings(window.Settings with { Browser = nowhere });
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Wasm: http"), () => $"Wasm's http configuration; the window has {string.Join(", ", window.Configurations.Select(c => c.Name))}",
                TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Wasm: http"));
            Ui.Launched(window, window.RunAsync(), "Wasm to build and run through its development server", TimeSpan.FromMinutes(3));
            try
            {
                Answers(window, () => window.BottomPanel.Run.Document.Snapshot.ToString());
            }
            finally
            {
                window.Perform(AppActions.Stop);
                Ui.WaitFor(() => !window.Builds.IsRunning, "the run to end");
            }

            string counter = Path.Combine(folder, "Pages", "Counter.razor");
            int increment = File.ReadAllLines(counter).ToList().FindIndex(l => l.Contains("currentCount++;", StringComparison.Ordinal));
            LineBreakpoint inBrowser = window.Debugger.Breakpoints.Set(counter, increment, null);
            Ui.Launched(window, window.DebugAsync(), "Wasm to start under the debugger", TimeSpan.FromMinutes(3));
            try
            {
                Ui.WaitFor(() => window.Debugger.Session?.State is DebugState.Running or DebugState.Ended, "the development server to run under the debugger", TimeSpan.FromMinutes(1));
                string notDebugged = "Wasm's components run in the browser on .NET's WebAssembly runtime, and their breakpoints are not hit: "
                    + $"only the development server that serves them runs under the debugger. Settings | Build, Run and Debug | Run | Browser names {nowhere} as the browser to debug "
                    + "them in, and there is no program there: name a Chromium browser that is, or install Google Chrome (google.com/chrome) or Microsoft Edge (microsoft.com/edge)";
                Answers(window, () => window.BottomPanel.Debug.Console.Document.Snapshot.ToString());
                Assert.Contains(notDebugged, window.BottomPanel.Debug.Console.Document.Snapshot.ToString(), StringComparison.Ordinal);
                Assert.True(window.Debugger.Summary?.EndsWith($"running; {notDebugged}", StringComparison.Ordinal) == true,
                    $"Debug's state did not say what it cannot debug: {window.Debugger.Summary}");

                // The component's breakpoint says it is never hit, and Problems lists it at its line.
                const string neverHit = "Wasm's components run in the browser on .NET's WebAssembly runtime, and no browser debugs them this time: only the development "
                    + "server that serves them runs under the debugger, so this breakpoint is never hit";
                Ui.WaitFor(() => inBrowser.Status == BreakpointStatus.Invalid, () => $"the component's breakpoint refused; it is {inBrowser.Status}: {inBrowser.Message}");
                Assert.Equal(neverHit, inBrowser.Message);
                Assert.Equal(["Breakpoint, not bound: it will not stop the program", neverHit],
                    BreakpointStates.Words(inBrowser, window.Debugger.IsDebugging, window.Debugger.Breakpoints.Muted));
                Ui.WaitFor(() => window.BottomPanel.Problems.BreakpointProblems.Any(p => p.Position.Line == increment), "Problems' Breakpoints listing the component's breakpoint");
                Assert.Equal($"Breakpoint not bound: {neverHit}", window.BottomPanel.Problems.BreakpointProblems.Single().Message);
            }
            finally
            {
                window.Perform(AppActions.Stop);
                Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The app's page from its development server, at the port the program's output (<paramref name="output"/>) says
    /// it listens on.</summary>
    private static void Answers(MainWindow window, Func<string> output)
    {
        int port = HeardPorts.Of(output, $"the development server ({window.Status})");
        using HttpClient http = new();
        string page = Ui.Await(http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/")), "the app's page from its development server", TimeSpan.FromMinutes(1));
        Assert.Contains("<div id=\"app\">", page, StringComparison.Ordinal);
    }

    /// <summary>The app of an SDK template, made in the test's folder without its restore.</summary>
    private void Make(string template, string name, params string[] options) =>
        Ui.AwaitPumped(Waiting.Apart(async () =>
        {
            ChildResult made = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(
                ["new", template, "--output", folder, "--name", name, "--framework", TestProjects.Framework, "--no-restore", .. options]), limit: Limit);
            Assert.True(made.Succeeded, $"dotnet new {template} did not make the test's app (exit code {made.ExitCode}):\n{made.Output}{made.Error}");
            return made;
        }), $"dotnet new {template} to make the test's app", Limit + TestFolders.Spare);
}
