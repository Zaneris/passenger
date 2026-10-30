using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Blazor's WebAssembly apps as the SDK's templates make them: a standalone app runs through the development server
/// its project's RunCommand names, answering HTTP, and Debug runs it so too, saying its components in the browser are not
/// debugged; a Blazor Web App's client is no configuration of its own, and Debug of the app says the client is not
/// debugged; New Project offers the standalone app no container or Aspire set-up. Its packages come from the package cache
/// tests/BlazorGraphs fills, never nuget.org. Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class BlazorWebAssemblyTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("blazor-wasm-"));

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>The longest dotnet new may take.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    [AvaloniaFact]
    public void A_standalone_app_runs_and_debugs_through_its_development_server_saying_its_components_in_the_browser_are_not_debugged()
    {
        Make("blazorwasm", "Wasm");
        int port = AspireApp.FreePort();
        string settings = Path.Combine(folder, "Properties", "launchSettings.json");
        JsonNode launch = JsonNode.Parse(File.ReadAllText(settings))!;
        launch["profiles"]!["http"]!["applicationUrl"] = $"http://localhost:{port}";
        File.WriteAllText(settings, launch.ToJsonString());
        TestFolders.Write(folder, "nuget.config", "<configuration>\n  <packageSources><clear /><add key=\"local\" value=\"feed\" /></packageSources>\n</configuration>\n");
        Directory.CreateDirectory(Path.Combine(folder, "feed"));
        Restore.Run(Path.Combine(folder, "Wasm.csproj"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Wasm: http"), () => $"Wasm's http configuration; the window has {string.Join(", ", window.Configurations.Select(c => c.Name))}",
                TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Wasm: http"));
            Ui.Launched(window, window.RunAsync(), "Wasm to build and run through its development server", TimeSpan.FromMinutes(3));
            try
            {
                Answers(window, port, () => window.BottomPanel.Run.Document.Snapshot.ToString());
            }
            finally
            {
                window.Perform(AppActions.Stop);
                Ui.WaitFor(() => !window.Builds.IsRunning, "the run to end");
            }

            Ui.Launched(window, window.DebugAsync(), "Wasm to start under the debugger", TimeSpan.FromMinutes(3));
            try
            {
                Ui.WaitFor(() => window.Debugger.Session?.State is DebugState.Running or DebugState.Ended, "the development server to run under the debugger", TimeSpan.FromMinutes(1));
                const string notDebugged = "Wasm's components run in the browser on .NET's WebAssembly runtime, which Passenger cannot debug yet, so their breakpoints are not hit: "
                    + "only the development server that serves them runs under the debugger";
                Answers(window, port, () => window.BottomPanel.Debug.Console.Document.Snapshot.ToString());
                Assert.Contains(notDebugged, window.BottomPanel.Debug.Console.Document.Snapshot.ToString(), StringComparison.Ordinal);
                Assert.True(window.Debugger.Summary?.EndsWith($"running; {notDebugged}", StringComparison.Ordinal) == true,
                    $"Debug's state did not say what it cannot debug: {window.Debugger.Summary}");
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

    [AvaloniaFact]
    public void A_Blazor_Web_Apps_client_is_no_configuration_and_Debug_of_the_app_says_the_client_is_not_debugged()
    {
        Make("blazor", "Bwa", "--interactivity", "WebAssembly", "--no-https");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Projects.Count == 2 && window.Projects.All(p => p.IsEvaluated) && window.Configurations.Any(c => c.Name == "Bwa: http"),
                () => $"Bwa and Bwa.Client evaluated with Bwa's http configuration; the window has {string.Join(", ", window.Configurations.Select(c => c.Name))}",
                TimeSpan.FromMinutes(2));
            ProjectModel client = window.Projects.Single(p => p.Name == "Bwa.Client");
            Assert.True(client.IsBrowserClient, "Bwa.Client was not taken for a Blazor Web App's client");
            Assert.False(client.IsRunnable, "Bwa.Client, which only Bwa serves, was taken for a program that runs");
            Assert.DoesNotContain(window.Configurations, c => c.Name.StartsWith("Bwa.Client", StringComparison.Ordinal));

            (LaunchPlan? plan, string? problem) = Ui.Await(window.Builds.ResolvePlanAsync(window.Configurations.Single(c => c.Name == "Bwa: http"), null),
                "Bwa's plan", TimeSpan.FromMinutes(1));
            Assert.True(plan is not null, $"Bwa's http configuration has no plan: {problem}");
            Assert.Equal("Bwa.Client's components run in the browser on .NET's WebAssembly runtime, which Passenger cannot debug yet, so their breakpoints are not hit: Bwa's are",
                plan.NotDebugged);

            Ui.Run(window, AppActions.NewProject);
            NewProjectDialog dialog = NewServiceSetupTests.Opened(window);
            try
            {
                NewServiceSetupTests.Choose(dialog, "blazorwasm");
                Assert.False(dialog.SetupChooser.IsEnabled, "The Blazor WebAssembly app, which runs in the browser, was offered a container or Aspire set-up");
                Assert.Equal(ProjectSetup.None, dialog.Setup);
                Assert.Contains("runs in the browser, served by its development server", dialog.SetupNote, StringComparison.Ordinal);
                NewServiceSetupTests.Choose(dialog, "blazor");
                Assert.True(dialog.SetupChooser.IsEnabled, $"The Blazor Web App, a service, was offered no set-up: {dialog.SetupNote}");
            }
            finally
            {
                dialog.Close();
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The app's page from its development server, once the program's output (<paramref name="output"/>) says it
    /// listens.</summary>
    private static void Answers(MainWindow window, int port, Func<string> output)
    {
        Ui.WaitFor(() => output().Contains("Now listening on", StringComparison.Ordinal), () => $"the development server to listen ({window.Status}):\n{output()}",
            TimeSpan.FromMinutes(2));
        using HttpClient http = new();
        string page = Ui.Await(http.GetStringAsync(new Uri($"http://localhost:{port}/")), "the app's page from its development server", TimeSpan.FromMinutes(1));
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
