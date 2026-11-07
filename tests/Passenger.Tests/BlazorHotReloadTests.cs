using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.Core;

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

    private const string HandlersRan = BellApp.HandlersRan;

    [AvaloniaFact]
    public void Under_debug_a_components_markup_and_code_edits_reach_its_page_and_the_apps_update_handlers_run_after_each()
    {
        Make();
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Choose(window);
            Ui.Launched(window, window.DebugAsync(), "Bell to start under the debugger", TimeSpan.FromMinutes(3));
            Func<string> console = () => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
            Assert.Contains("<h1>Counter</h1>", Page(window, console), StringComparison.Ordinal);

            Edit(window, "<h1>Counter</h1>", "<h1>Tally</h1>");
            Apply(window);
            Ui.WaitFor(() => Count(console(), HandlersRan) == 1, () => $"the app's update handlers to run after the markup's edit; the console has:\n{console()}", TimeSpan.FromMinutes(1));
            Assert.Contains("<h1>Tally</h1>", Page(window, console), StringComparison.Ordinal);

            Edit(window, "private int currentCount = 0;", "private int currentCount = 5;");
            Apply(window);
            Ui.WaitFor(() => Count(console(), HandlersRan) == 2, () => $"the app's update handlers to run after the code's edit; the console has:\n{console()}", TimeSpan.FromMinutes(1));
            Assert.Contains("Current count: 5", Page(window, console), StringComparison.Ordinal);
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
        Make();
        MainWindow window = Ui.OpenFolder(folder);
        window.ApplySettings(window.Settings with { HotReload = true });
        try
        {
            Choose(window);
            window.Perform(AppActions.RunProgram);
            Func<string> output = () => window.BottomPanel.Run.Document.Snapshot.ToString();
            Ui.WaitFor(() => output().Contains("Waiting for changes", StringComparison.Ordinal) || output().Contains("Waiting for a file to change", StringComparison.Ordinal),
                () => $"dotnet watch to watch the app ({window.Status}):\n{output()}", TimeSpan.FromMinutes(3));
            Assert.Contains("<h1>Counter</h1>", Page(window, output), StringComparison.Ordinal);

            Edit(window, "<h1>Counter</h1>", "<h1>Tally</h1>");
            window.Perform(AppActions.Save);
            Ui.WaitFor(() => Count(output(), HandlersRan) == 1, () => $"dotnet watch to run the update handlers after the markup's edit:\n{output()}", TimeSpan.FromMinutes(1));
            Assert.Contains("<h1>Tally</h1>", Page(window, output), StringComparison.Ordinal);

            Edit(window, "private int currentCount = 0;", "private int currentCount = 5;");
            window.Perform(AppActions.Save);
            Ui.WaitFor(() => Count(output(), HandlersRan) == 2, () => $"dotnet watch to run the update handlers after the code's edit:\n{output()}", TimeSpan.FromMinutes(1));
            Assert.Contains("Current count: 5", Page(window, output), StringComparison.Ordinal);
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

    /// <summary>Apply Hot Reload Changes, until it says what it came to: applied.</summary>
    private static void Apply(MainWindow window)
    {
        Assert.True(window.Perform(AppActions.ApplyHotReloadChanges), $"Apply Hot Reload Changes could not run: {window.Status}");
        Ui.WaitFor(() => window.Debugger.LastApplied is { Result: HotReloadResult.Applied } && window.Actions.Work.Of(AppActions.ApplyHotReloadChanges) is null,
            () => $"the edit applied; Hot Reload said: {window.Debugger.LastApplied?.Said} ({window.Status})", TimeSpan.FromMinutes(1));
    }

    /// <summary>The app, made and restored in the test's folder (<see cref="BellApp"/>).</summary>
    private void Make() => BellApp.Make(folder);

    private static string Page(MainWindow window, Func<string> output) => BellApp.Page(window, output);

    private static void Edit(MainWindow window, string was, string now) => BellApp.Edit(window, was, now);

    private static int Count(string text, string what) => BellApp.Count(text, what);
}
