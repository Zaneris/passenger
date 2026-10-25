using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Passenger.App.Containers;
using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The window's run configurations (the session's <see cref="RunConfigurations"/>): the one chosen in the toolbar,
/// its menu and Edit Configurations; Run and Debug, which the session's <see cref="BuildRunController"/> launches; and the
/// tab of the Terminal window a configuration that uses one runs in.</summary>
public partial class MainWindow
{
    /// <summary>Whether a program Run started runs, in the Run window or the terminal.</summary>
    public bool IsRunning => buildRun.IsRunning;

    /// <summary>The program Run last started, when it runs in the terminal; null when the last ran in the Run window.</summary>
    public TerminalRun? RunInTerminal => session.InTerminal;

    /// <summary>The end of the program Run last started, in the Run window or the terminal, with its exit code.</summary>
    public Task<int> RunEnd => buildRun.RunEnd;

    /// <summary>Every configuration: the generated ones, then the folder's own.</summary>
    public IReadOnlyList<RunConfiguration> Configurations => session.Configurations.All;

    /// <summary>The configuration chosen in the toolbar, which Run, Debug and Profile launch.</summary>
    public RunConfiguration? Configuration => session.Configurations.Chosen;

    /// <summary>The run configurations dialog open over the window, or null.</summary>
    public RunConfigurationsDialog? RunConfigurationsDialog { get; private set; }

    /// <summary>Chooses the run configuration by name, as the toolbar's menu does; false when there is none.</summary>
    public bool SelectConfiguration(string name) => session.Configurations.Select(name);

    /// <summary>Takes the folder's own configurations as Edit Configurations leaves them, and the one chosen there; kept
    /// with the folder when the window keeps state.</summary>
    public void SetConfigurations(IReadOnlyList<RunConfiguration> mine, string? chosen) => session.Configurations.Set(mine, chosen, Projects);

    /// <summary>Run's Edit Configurations: the dialog over the window, its changes the folder's once applied.</summary>
    public RunConfigurationsDialog ShowRunConfigurations()
    {
        RunConfigurations configurations = session.Configurations;
        RunConfigurationsDialog dialog = new([.. configurations.All.Where(c => c.Generated)], configurations.Own, [.. Projects.Where(p => p.Exists && p.IsRunnable)],
            configurations.Chosen?.Name, (mine, generated, chosen) => configurations.Set(mine, generated, chosen, Projects), buildRun.IsRunningConfiguration)
        {
            Folder = Folder,
            ReadServices = ReadServices(Projects, Engine),
            WriteToProfile = WriteToLaunchProfile,
            ReadAspireServices = ReadAspireConfigurationResourcesAsync,
        };
        dialog.ShowRunningAppHosts(session.Aspire.Apps.Where(app => !app.IsTest && app.Runner.IsRunning).Select(app => app.Configuration.Project));
        RunConfigurationsDialog = dialog;
        dialog.Closed += (_, _) =>
        {
            if (RunConfigurationsDialog == dialog)
            {
                RunConfigurationsDialog = null;
            }
        };
        IdeErrors.Observe(dialog.ShowDialog(this));
        return dialog;
    }

    /// <summary>Edit Configurations' Write to Launch Profile: what of a generated configuration's changes its launch profile
    /// has a place for written into the project's launchSettings.json (saved first if open, taken in after), the rest still
    /// kept with the folder; one command Undo takes back whole, the file and the changes kept here as they were. The
    /// configuration as it then stands, or why not.</summary>
    public (RunConfiguration? Configuration, string? Problem) WriteToLaunchProfile(string name)
    {
        RunConfigurations configurations = session.Configurations;
        if (configurations.All.FirstOrDefault(c => c.Generated && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is not { Source: { } source } edited)
        {
            return (null, $"{name} has no change to write to its launch profile");
        }
        if (Projects.FirstOrDefault(p => PathNames.Same(p.Path, edited.Project)) is not { } project
            || LaunchProfile.Read(project).FirstOrDefault(p => p.Name == edited.LaunchProfile) is not { } profile)
        {
            return (null, $"The launch profile {edited.LaunchProfile} is no longer in its project's launchSettings.json");
        }
        IReadOnlyList<LaunchSettingsEdits.Change> changes = LaunchSettingsEdits.For(project, profile, source, edited);
        if (changes.Count == 0)
        {
            return (null, $"launchSettings.json has no place for {name}'s changes: they stay kept with the folder");
        }
        string path = LaunchProfile.SettingsPath(project);
        if (SaveBeforeWriting([path]) is { } unsaved)
        {
            return (null, $"launchSettings.json could not be changed: {unsaved}");
        }
        JsonObject? before = configurations.EditOf(name);
        using CommandHistory.Recording made = Editors.Commands.Record($"Write {name} to Its Launch Profile");
        made.Watch(path);
        string? problem;
        try
        {
            problem = LaunchSettingsEdits.Write(path, profile.Name, changes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            problem = error.Message;
        }
        finally
        {
            // Open, it takes the text now, inside the command, so the command's undo takes it back too.
            FollowWritten([path]);
        }
        if (problem is not null)
        {
            return (null, $"launchSettings.json could not be changed: {problem}");
        }
        RunConfiguration? now = configurations.Rebase(name, edited);
        JsonObject? after = configurations.EditOf(name);
        made.Also(new CommandHistory.Part($"the run configuration {name}",
            redo => JsonNode.DeepEquals(configurations.EditOf(name), redo ? before : after) ? null : $"{name} has been changed since",
            redo => configurations.SetEdit(name, redo ? after : before)));
        return (now, null);
    }

    /// <summary>How Edit Configurations reads a Compose configuration's services: Compose's model through the window's engine,
    /// asked only once the engine answers as ready.</summary>
    private static Func<RunConfiguration, CancellationToken, Task<IReadOnlyList<(ComposeService Service, string? Project, bool Built)>>> ReadServices(
        IReadOnlyList<ProjectModel> projects, ContainerEngine engine) => async (configuration, cancel) =>
        {
            EngineState state = await engine.StateAsync(cancel: cancel);
            return state.IsReady ? await ContainerPlans.ServicesAsync(configuration, projects, engine, cancel)
                : throw new ContainerCommandException(state.Problem ?? "the container engine is not ready");
        };

    /// <summary>Runs the chosen configuration as Run does: null once it runs (<see cref="RunEnd"/> is its end), else why it
    /// could not, which the status bar says too.</summary>
    public Task<string?> RunAsync(string? arguments = null) => buildRun.RunAsync(arguments);

    /// <summary>Debugs the chosen configuration as Debug does: null once its session is open, else what stopped it.</summary>
    public Task<string?> DebugAsync(string? arguments = null) => buildRun.DebugAsync(arguments);

    /// <summary>Starts in a tab of the Terminal window, named for the configuration being debugged, the program the debugger
    /// asks for (its runInTerminal request): held at its start by the variables the debugger gives it until the debugger
    /// has hold of it.</summary>
    private Task<TerminalRun> RunForDebugger(string name, Dap.RunInTerminalArguments arguments)
    {
        if (arguments.Args is not [string program, ..])
        {
            throw new LaunchException("The debugger asked the terminal to run nothing");
        }
        IEnumerable<string> rest = arguments.Args.Skip(1);
        // A program named without a folder (a .dll's "dotnet") is the one the IDE builds with, or one on the PATH.
        System.Diagnostics.ProcessStartInfo start = ChildProcesses.Command(program is "dotnet" ? Core.Dotnet.Host() : program, rest, arguments.Cwd ?? Folder);
        foreach ((string variable, string value) in arguments.Env ?? new Dictionary<string, string>())
        {
            start.Environment[variable] = value;
        }
        return ShowInTerminal(name, start);
    }

    /// <summary>Runs a program in a tab of the Terminal window and shows the window on it: the tab first, so a window opened
    /// for it starts no shell of its own.</summary>
    private Task<TerminalRun> ShowInTerminal(string name, System.Diagnostics.ProcessStartInfo start)
    {
        Task<TerminalRun> starting = TerminalPanel.RunAsync(name, start);
        ShowToolWindow(ToolWindowIds.Terminal);
        return starting;
    }

    /// <summary>Run, from its key, the toolbar or the Run window: what stopped it, if anything, is in the status bar.</summary>
    private void RunSelected() => IdeErrors.Observe(RunAsync());

    /// <summary>Rerun, of the configuration chosen (<see cref="BuildRunController.RerunAsync"/>): what stopped it, if anything,
    /// is in the status bar.</summary>
    private Task RerunAsync() => buildRun.RerunAsync();

    /// <summary>Debug, from its key or the toolbar: what stopped it, if anything, goes to the status bar.</summary>
    public void DebugSelected()
    {
        IdeErrors.Observe(Debug());

        async Task Debug()
        {
            if (await DebugAsync() is { } problem)
            {
                status.Text = problem;
            }
        }
    }

    /// <summary>Why a configuration cannot launch here (a launch profile Passenger does not run, a WPF app off Windows), as
    /// the toolbar's menu greys it; null when it can.</summary>
    public string? WhyNotHere(RunConfiguration configuration) => session.Configurations.WhyNotHere(configuration);

    /// <summary>A configuration's name as the toolbar and Edit Configurations show it: "(running)" after it while a run of it
    /// is up.</summary>
    public string Shown(RunConfiguration configuration)
    {
        int running = session.Aspire.Apps.Count(app => !app.IsTest && app.Runner.IsRunning
            && (PathNames.Same(app.Configuration.Project, configuration.Project)
                || configuration.LinkedAppHosts.Any(path => PathNames.Same(path, app.Configuration.Project))));
        return running switch
        {
            0 when !buildRun.IsRunningConfiguration(configuration.Name) => configuration.Name,
            0 or 1 => $"{configuration.Name} (running)",
            _ => $"{configuration.Name} ({running} apps running)",
        };
    }

    /// <summary>The toolbar widget names the configuration chosen, greyed with why when it cannot launch here, and says it
    /// runs while a run of it is up.</summary>
    private void ShowChosenConfiguration(RunConfigurations configurations)
    {
        RunConfiguration? chosen = configurations.Chosen;
        string? why = chosen is null ? null : configurations.WhyNotHere(chosen);
        RunConfigName.Text = chosen is null ? "No configurations" : Shown(chosen);
        RunConfigName.Classes.Set("muted", why is not null);
        ToolTip.SetTip(RunConfigButton, why is null ? "Run configuration" : $"{chosen!.Name} cannot run here: {why}");
    }

    /// <summary>The run configurations to choose from, as a menu under the toolbar widget, and Edit Configurations
    /// below them: one that cannot launch here is listed greyed, with why beside it, and can still be chosen, so Run
    /// says why and Edit Configurations shows it.</summary>
    private void OnRunConfigClicked(object? sender, RoutedEventArgs e)
    {
        ContextMenu menu = new();
        foreach (RunConfiguration each in Configurations)
        {
            string name = each.Name;
            string? why = WhyNotHere(each);
            MenuItem item = MenuChoice.Of(why is null ? Shown(each) : Greyed(name, why), session.Configurations.Chosen?.Name == name, () => SelectConfiguration(name));
            if (why is not null)
            {
                ToolTip.SetTip(item, $"{name} cannot run here: {why}");
            }
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No executable projects", IsEnabled = false });
        }
        menu.Items.Add(new Separator());
        MenuItem edit = new() { Header = "Edit Configurations…" };
        edit.Click += (_, _) => Request(AppActions.EditConfigurations);
        menu.Items.Add(edit);
        Open(menu, RunConfigButton);

        static StackPanel Greyed(string name, string why) => new()
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = name, Classes = { "muted" } },
                new TextBlock { Text = why, Classes = { "muted" }, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            },
        };
    }

}
