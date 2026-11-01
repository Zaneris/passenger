using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Aspire;
using Passenger.App.Debugging;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Hot Reload of a Blazor component in a service of an Aspire AppHost's run, the stand-in AppHost's (<see
/// cref="StandInAppHost"/>), the service a Blazor app (<see cref="BellApp"/>) the IDE starts as DCP asks and attaches to:
/// the app's update handlers run after each update, through Passenger's hook the service started with, under Debug in the
/// service's own tab and under Run with Hot Reload's quiet session, and its page shows the edit. Debugged through the
/// window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class BlazorServiceHotReloadTests(StandInAppHost project) : IClassFixture<StandInAppHost>
{
    [AvaloniaFact]
    public void Under_an_Aspire_services_debug_session_a_components_edit_runs_the_apps_update_handlers_and_reaches_its_page()
    {
        project.Reset();
        (string bell, int port) = Bell();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend);
        MainWindow window = project.Open(project.Program);
        try
        {
            Choose(window);
            Ui.Launched(window, window.DebugAsync(), "the stand-in AppHost to come up debugged");
            (AspireResourceRun service, System.Net.WebSockets.ClientWebSocket notified) = Start(window, bell, "Debug");
            using System.Net.WebSockets.ClientWebSocket subscribed = notified;
            Ui.WaitFor(() => service.DebugTab is { IsLive: true }, () => $"the debugger to attach to the Blazor service ({window.Status})", TimeSpan.FromMinutes(2));
            Func<string> output = () => service.Console.Document.Snapshot.ToString();
            Assert.Contains("<h1>Counter</h1>", BellApp.Page(window, port, output), StringComparison.Ordinal);

            Ui.Await(window.Debugger.ChooseAsync(service.DebugTab!), "the service's tab to be chosen");
            Edit(window, bell);
            Assert.True(window.Perform(AppActions.ApplyHotReloadChanges), $"Apply Hot Reload Changes could not run: {window.Status}");
            Ui.WaitFor(() => window.Debugger.LastApplied is { Result: HotReloadResult.Applied } && window.Actions.Work.Of(AppActions.ApplyHotReloadChanges) is null,
                () => $"the edit applied; Hot Reload said: {window.Debugger.LastApplied?.Said} ({window.Status})", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => BellApp.Count(output(), BellApp.HandlersRan) == 1,
                () => $"the service's update handlers to run after the edit; its output has:\n{output()}\nits tab's console:\n{service.DebugTab?.Console.Document.Snapshot}",
                TimeSpan.FromMinutes(1));
            Assert.Contains("<h1>Tally</h1>", BellApp.Page(window, port, output), StringComparison.Ordinal);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the sessions to end", TimeSpan.FromMinutes(1));
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    [AvaloniaFact]
    public void Under_run_with_hot_reload_an_Aspire_services_quiet_session_runs_the_apps_update_handlers_after_a_components_edit()
    {
        project.Reset();
        (string bell, int port) = Bell();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend);
        MainWindow window = project.Open(project.Program);
        try
        {
            Choose(window);
            window.ApplySettings(window.Settings with { HotReload = true });
            Ui.Run(window, AppActions.RunProgram);
            Ui.WaitFor(() => backend.Connected.Task.IsCompleted && window.AspireRuntime.Apps.Count == 1, $"the stand-in AppHost to come up ({window.Status})");
            (AspireResourceRun service, System.Net.WebSockets.ClientWebSocket notified) = Start(window, bell, "NoDebug");
            using System.Net.WebSockets.ClientWebSocket subscribed = notified;
            Ui.WaitFor(() => window.Debugger.Running.Count == 1, $"the Blazor service to be attached to for Hot Reload ({window.Status})", TimeSpan.FromMinutes(2));
            Func<string> output = () => service.Console.Document.Snapshot.ToString();
            Assert.Contains("<h1>Counter</h1>", BellApp.Page(window, port, output), StringComparison.Ordinal);

            Edit(window, bell);
            Assert.True(window.Perform(AppActions.Save), $"Save could not run: {window.Status}");
            Ui.WaitFor(() => BellApp.Count(output(), BellApp.HandlersRan) == 1,
                () => $"the service's update handlers to run after the saved edit applied ({window.Debugger.LastApplied?.Said}); its output has:\n{output()}", TimeSpan.FromMinutes(1));
            Assert.Contains("<h1>Tally</h1>", BellApp.Page(window, port, output), StringComparison.Ordinal);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Builds.IsRunning && window.Debugger.Running.Count == 0, "the run to end", TimeSpan.FromMinutes(1));
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>The Blazor app made beside the stand-in's projects, in its solution, built by its real path as the IDE starts
    /// it unbuilt (as DCP asks for an AppHost's built service): its project and its port.</summary>
    private (string Project, int Port) Bell()
    {
        string folder = project.PathOf("Bell");
        int port = BellApp.Make(folder);
        string bell = PathNames.Canonical(Path.Combine(folder, "Bell.csproj"));
        project.Write("StandIn.slnx", $"<Solution>\n  <Project Path=\"{StandInAppHost.Name}/{StandInAppHost.Name}.csproj\" />\n"
            + $"  <Project Path=\"{StandInAppHost.Api}/{StandInAppHost.Api}.csproj\" />\n  <Project Path=\"Bell/Bell.csproj\" />\n</Solution>\n");
        ChildResult built = Ui.Await(ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", bell, "-nologo", "-v:q"]), limit: TimeSpan.FromMinutes(4).Scaled()),
            "the Blazor service's build");
        Assert.True(built.Succeeded, $"The Blazor service did not build: {built.Output}{built.Error}");
        return (bell, port);
    }

    private static void Choose(MainWindow window)
    {
        Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration), "the stand-in AppHost's Aspire configuration");
        Assert.True(window.SelectConfiguration(StandInAppHost.Configuration));
    }

    /// <summary>The Blazor app started as the stand-in AppHost's service bell, as DCP asks in <paramref name="mode"/>, and the
    /// sessions' notifications DCP holds while the service runs.</summary>
    private (AspireResourceRun Service, System.Net.WebSockets.ClientWebSocket Notified) Start(MainWindow window, string bell, string mode)
    {
        (System.Net.WebSockets.ClientWebSocket notified, System.Net.HttpStatusCode answer) = project.AskToStartApi("bell", mode, projectPath: bell);
        Assert.True((int)answer is >= 200 and < 300, $"The IDE refused the Blazor service's start: {answer} ({window.Status})");
        Ui.WaitFor(() => window.AspireRuntime.Apps.Single().Services.Any(service => service.ProcessId is > 0), "the Blazor service started as DCP asked");
        return (window.AspireRuntime.Apps.Single().Services.Single(service => PathNames.Same(service.Project, bell)), notified);
    }

    /// <summary>Counter.razor's heading edited in its editor, as typed.</summary>
    private static void Edit(MainWindow window, string bell)
    {
        window.OpenFile(Path.Combine(Path.GetDirectoryName(bell)!, "Components", "Pages", "Counter.razor"));
        Ui.Settle();
        BellApp.Edit(window, "<h1>Counter</h1>", "<h1>Tally</h1>");
    }

    private static async Task<bool> Complete(Task task)
    {
        await task;
        return true;
    }
}
