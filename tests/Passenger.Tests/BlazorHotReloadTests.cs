using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Hot Reload of a Blazor Server component, as <c>dotnet new blazor</c> makes the app: a markup edit and an
/// <c>@code</c> edit to Counter.razor reach the running app, its page fetched over loopback showing each, and the app's
/// update handlers run after each (a handler of the test's own says so, as Blazor's own re-renders the components then),
/// under Debug, where Passenger's hook calls them, and under Run with Hot Reload, where dotnet watch does. The app needs no
/// package, so it restores offline. Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class BlazorHotReloadTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("blazor-reload-"));

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>The longest dotnet new may take.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    /// <summary>What the app's own update handler writes each time the handlers run.</summary>
    private const string HandlersRan = "the update handlers ran";

    [AvaloniaFact]
    public void Under_debug_a_components_markup_and_code_edits_reach_its_page_and_the_apps_update_handlers_run_after_each()
    {
        int port = Make();
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Choose(window);
            Ui.Launched(window, window.DebugAsync(), "Bell to start under the debugger", TimeSpan.FromMinutes(3));
            Func<string> console = () => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
            Assert.Contains("<h1>Counter</h1>", Page(window, port, console), StringComparison.Ordinal);

            Edit(window, "<h1>Counter</h1>", "<h1>Tally</h1>");
            Apply(window);
            Ui.WaitFor(() => Count(console(), HandlersRan) == 1, () => $"the app's update handlers to run after the markup's edit; the console has:\n{console()}", TimeSpan.FromMinutes(1));
            Assert.Contains("<h1>Tally</h1>", Page(window, port, console), StringComparison.Ordinal);

            Edit(window, "private int currentCount = 0;", "private int currentCount = 5;");
            Apply(window);
            Ui.WaitFor(() => Count(console(), HandlersRan) == 2, () => $"the app's update handlers to run after the code's edit; the console has:\n{console()}", TimeSpan.FromMinutes(1));
            Assert.Contains("Current count: 5", Page(window, port, console), StringComparison.Ordinal);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Under_run_with_hot_reload_a_components_markup_and_code_edits_reach_its_page_as_dotnet_watch_runs_the_update_handlers()
    {
        int port = Make();
        MainWindow window = Ui.OpenFolder(folder);
        window.ApplySettings(window.Settings with { HotReload = true });
        try
        {
            Choose(window);
            window.Perform(AppActions.RunProgram);
            Func<string> output = () => window.BottomPanel.Run.Document.Snapshot.ToString();
            Ui.WaitFor(() => output().Contains("Waiting for changes", StringComparison.Ordinal) || output().Contains("Waiting for a file to change", StringComparison.Ordinal),
                () => $"dotnet watch to watch the app ({window.Status}):\n{output()}", TimeSpan.FromMinutes(3));
            Assert.Contains("<h1>Counter</h1>", Page(window, port, output), StringComparison.Ordinal);

            Edit(window, "<h1>Counter</h1>", "<h1>Tally</h1>");
            window.Perform(AppActions.Save);
            Ui.WaitFor(() => Count(output(), HandlersRan) == 1, () => $"dotnet watch to run the update handlers after the markup's edit:\n{output()}", TimeSpan.FromMinutes(1));
            Assert.Contains("<h1>Tally</h1>", Page(window, port, output), StringComparison.Ordinal);

            Edit(window, "private int currentCount = 0;", "private int currentCount = 5;");
            window.Perform(AppActions.Save);
            Ui.WaitFor(() => Count(output(), HandlersRan) == 2, () => $"dotnet watch to run the update handlers after the code's edit:\n{output()}", TimeSpan.FromMinutes(1));
            Assert.Contains("Current count: 5", Page(window, port, output), StringComparison.Ordinal);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Builds.IsRunning, "the run to end");
            window.Close();
        }
    }

    /// <summary>The app's http configuration chosen, its Counter.razor open.</summary>
    private void Choose(MainWindow window)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Bell: http"), () => $"Bell's http configuration; the window has {string.Join(", ", window.Configurations.Select(c => c.Name))}",
            TimeSpan.FromMinutes(2));
        Assert.True(window.SelectConfiguration("Bell: http"));
        window.OpenFile(Path.Combine(folder, "Components", "Pages", "Counter.razor"));
        Ui.Settle();
    }

    /// <summary>An edit to Counter.razor in the editor, as typed.</summary>
    private static void Edit(MainWindow window, string was, string now)
    {
        TextSnapshot text = window.EditorArea.CodeEditor.Document.Snapshot;
        int at = text.ToString().IndexOf(was, StringComparison.Ordinal);
        Assert.True(at >= 0, $"Counter.razor has no '{was}'");
        window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(at, was.Length, now)]);
    }

    /// <summary>Apply Hot Reload Changes, until it says what it came to: applied.</summary>
    private static void Apply(MainWindow window)
    {
        Assert.True(window.Perform(AppActions.ApplyHotReloadChanges), $"Apply Hot Reload Changes could not run: {window.Status}");
        Ui.WaitFor(() => window.Debugger.LastApplied is { Result: HotReloadResult.Applied } && window.Actions.Work.Of(AppActions.ApplyHotReloadChanges) is null,
            () => $"the edit applied; Hot Reload said: {window.Debugger.LastApplied?.Said} ({window.Status})", TimeSpan.FromMinutes(1));
    }

    /// <summary>The counter's page from the app, once its output (<paramref name="output"/>) says it listens.</summary>
    private static string Page(MainWindow window, int port, Func<string> output)
    {
        Ui.WaitFor(() => output().Contains("Now listening on", StringComparison.Ordinal), () => $"the app to listen ({window.Status}):\n{output()}", TimeSpan.FromMinutes(2));
        using HttpClient http = new();
        return Ui.Await(http.GetStringAsync(new Uri($"http://localhost:{port}/counter")), "the counter's page from the app", TimeSpan.FromMinutes(1));
    }

    private static int Count(string text, string what)
    {
        int count = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    /// <summary>The app, made and restored in the test's folder, listening on a port of its own with no browser opened, with
    /// an update handler of its own that writes <see cref="HandlersRan"/>: the port.</summary>
    private int Make()
    {
        Ui.AwaitPumped(Waiting.Apart(async () =>
        {
            ChildResult made = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(
                ["new", "blazor", "--output", folder, "--name", "Bell", "--framework", TestProjects.Framework, "--interactivity", "Server", "--no-https", "--no-restore"]), limit: Limit);
            Assert.True(made.Succeeded, $"dotnet new blazor did not make the test's app (exit code {made.ExitCode}):\n{made.Output}{made.Error}");
            return made;
        }), "dotnet new blazor to make the test's app", Limit + TestFolders.Spare);
        int port = AspireApp.FreePort();
        string settings = Path.Combine(folder, "Properties", "launchSettings.json");
        JsonNode launch = JsonNode.Parse(File.ReadAllText(settings))!;
        launch["profiles"]!["http"]!["applicationUrl"] = $"http://localhost:{port}";
        launch["profiles"]!["http"]!["launchBrowser"] = false;
        File.WriteAllText(settings, launch.ToJsonString());
        TestFolders.Write(folder, "UpdateProbe.cs", "[assembly: System.Reflection.Metadata.MetadataUpdateHandler(typeof(UpdateProbe))]\n\n"
            + "internal static class UpdateProbe\n{\n    public static void UpdateApplication(Type[]? types) => Console.WriteLine(\"" + HandlersRan + "\");\n}\n");
        TestFolders.Write(folder, "nuget.config", "<configuration>\n  <packageSources><clear /><add key=\"local\" value=\"feed\" /></packageSources>\n</configuration>\n");
        Directory.CreateDirectory(Path.Combine(folder, "feed"));
        Restore.Run(Path.Combine(folder, "Bell.csproj"));
        return port;
    }
}
