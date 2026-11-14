using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Passenger.App.Containers;
using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
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

    /// <summary>The copies of a Godot game Run runs, each in its tab of the Terminal window.</summary>
    public IReadOnlyList<TerminalRun> RunCopies => buildRun.CopyRuns;

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
        RunConfigurationsDialog dialog = new([.. configurations.All.Where(c => c.Generated)], configurations.Own, [.. session.RunProjects.Where(p => p.Exists && (p.IsRunnable || p.GodotFolder is not null))],
            configurations.Chosen?.Name, (mine, generated, chosen) => configurations.Set(mine, generated, chosen, Projects), buildRun.IsRunningConfiguration,
            configurations.Profiles, configurations.Solution, configurations.ProfileProblems, session.BuildChoices.Offered, session.BuildChoices.Chosen)
        {
            Folder = Folder,
            WriteProfiles = WriteLaunchProfilesAsync,
            GodotSetting = settings.GodotExecutable,
            ReadServices = ReadServices(Projects, Engine),
            WriteToProfile = WriteToLaunchProfileAsync,
            Actions = Actions,
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
    /// configuration as it then stands once the file has landed, or why not.</summary>
    public async Task<(RunConfiguration? Configuration, string? Problem)> WriteToLaunchProfileAsync(string name)
    {
        RunConfigurations configurations = session.Configurations;
        if (configurations.All.FirstOrDefault(c => c.Generated && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is not { Source: { } source } edited)
        {
            return (null, $"{name} has no change to write to its launch profile");
        }
        if (Projects.FirstOrDefault(p => PathNames.Same(p.Path, edited.Project)) is not { } project
            || LaunchProfile.Read(project).FirstOrDefault(p => p.Name == edited.LaunchProfile) is not { } profile)
        {
            return (null, $"The launch profile {edited.LaunchProfile} is no longer in its project's {LaunchProfile.SettingsName(edited.Project)}");
        }
        IReadOnlyList<LaunchSettingsEdits.Change> changes = LaunchSettingsEdits.For(project, profile, source, edited);
        if (changes.Count == 0)
        {
            return (null, $"{LaunchProfile.SettingsName(project.Path)} has no place for {name}'s changes: they stay kept with the folder");
        }
        string path = LaunchProfile.SettingsPath(project);
        JsonObject? before = configurations.EditOf(name);
        using CommandHistory.Recording made = Editors.Commands.Record($"Write {name} to Its Launch Profile");
        if (await ChangeFileAsync([path], () => LaunchSettingsEdits.Write(path, profile.Name, changes), made) is { } problem)
        {
            return (null, $"{Path.GetFileName(path)} could not be changed: {await made.TakenBackAsync(problem)}");
        }
        RunConfiguration? now = configurations.Rebase(name, edited);
        JsonObject? after = configurations.EditOf(name);
        made.Also(new CommandHistory.Part($"the run configuration {name}",
            redo => JsonNode.DeepEquals(configurations.EditOf(name), redo ? before : after) ? null : $"{name} has been changed since",
            redo =>
            {
                configurations.SetEdit(name, redo ? after : before);
                return Task.FromResult<string?>(null);
            }));
        return (now, null);
    }

    /// <summary>Edit Configurations' multi-project configurations as it leaves them written into the solution's launch profile
    /// files (the team's <c>.slnLaunch</c> and the person's <c>.slnLaunch.user</c>, saved first if open, taken in after), in
    /// Visual Studio's format: each profile unchanged kept as written, one changed written in its place, one removed taken out,
    /// one new put last, what Passenger does not model kept; one command Undo takes back whole. The configurations as read
    /// again once the files have landed, or why not.</summary>
    public async Task<(IReadOnlyList<RunConfiguration>? Profiles, string? Problem)> WriteLaunchProfilesAsync(IReadOnlyList<RunConfiguration> profiles)
    {
        RunConfigurations configurations = session.Configurations;
        if (configurations.Solution is not { } solution)
        {
            return (null, Refusals.NoSolutionForProfiles);
        }
        IReadOnlyList<RunConfiguration> before = configurations.Profiles;
        string folder = Path.GetDirectoryName(solution)!;
        string[] files = [SolutionLaunch.SharedFile(solution), SolutionLaunch.UserFile(solution)];
        // Each file's text as written now, or why it cannot be; null for one left as it is.
        (string? Text, string? Problem) Written(string file, bool shared)
        {
            List<SolutionLaunchProfile> kept = [.. profiles.Where(p => p.Shared == shared)
                .Select(p => RunConfigurations.ToProfile(p, before.FirstOrDefault(b => b.Launch is not null && ReferenceEquals(b.Launch, p.Launch))?.Name))];
            FileText? had = File.Exists(file) ? FileText.Load(file) ?? throw new IOException($"{Path.GetFileName(file)} is not a text file") : null;
            if (had is null && kept.Count == 0)
            {
                return (null, null);
            }
            (string? text, string? problem) = SolutionLaunch.Written(had?.Text, kept, folder);
            return problem is not null ? (null, $"{Path.GetFileName(file)} {problem}") : (text == had?.Text ? null : text, null);
        }
        List<string> changing = [];
        try
        {
            for (int i = 0; i < files.Length; i++)
            {
                (string? text, string? problem) = Written(files[i], i == 0);
                if (problem is not null)
                {
                    return (null, problem);
                }
                if (text is not null)
                {
                    changing.Add(files[i]);
                }
            }
        }
        catch (IOException unread)
        {
            return (null, unread.Message);
        }
        if (changing.Count == 0)
        {
            return (configurations.Profiles, null);
        }
        using CommandHistory.Recording made = Editors.Commands.Record("Change Launch Profiles");
        string? refused = await ChangeFileAsync(changing, () =>
        {
            // Read again as the change writes: what was open was saved first.
            foreach (string file in changing)
            {
                (string? text, string? problem) = Written(file, file == files[0]);
                if (problem is not null)
                {
                    return problem;
                }
                if (text is null)
                {
                    continue;
                }
                if (File.Exists(file))
                {
                    FileText.SaveOver(file, FileText.Load(file) ?? throw new IOException($"{Path.GetFileName(file)} is not a text file"), text);
                }
                else
                {
                    FileText.SaveNew(file, text);
                }
            }
            return null;
        }, made);
        if (refused is not null)
        {
            return (null, $"The launch profiles could not be written: {await made.TakenBackAsync(refused)}");
        }
        configurations.Configure(Projects);
        return (configurations.Profiles, null);
    }

    /// <summary>Why Save as Launch Profile cannot save the configuration it would (the one chosen in Edit Configurations
    /// while it is open, else the toolbar's), among those listed there; null when it can.</summary>
    private string? WhyNotShare() => RunConfigurationsDialog is { } dialog
        ? WhyNotShare(dialog.Chosen, dialog.Rows)
        : WhyNotShare(Configuration, Configurations);

    /// <summary>Why a configuration cannot be saved as a launch profile: none is chosen, it is generated, it runs containers,
    /// its project is not loaded, or its name, less its project's, is a profile's already or would make the name of another
    /// listed; null when it can.</summary>
    private string? WhyNotShare(RunConfiguration? shared, IReadOnlyList<RunConfiguration> listed)
    {
        if (Folder is null)
        {
            return Refusals.NoFolder;
        }
        if (shared is null)
        {
            return "No run configuration is chosen";
        }
        if (shared.Generated)
        {
            return shared.LaunchProfile.Length > 0 && !shared.IsContainer
                ? $"{shared.Name} is generated from its launch profile already: Write to Launch Profile writes its changes there"
                : $"{shared.Name} is generated, not one of the folder's own: a copy of it is";
        }
        if (shared.IsContainer)
        {
            return $"{shared.Name} runs containers: a launch profile saved here runs a project on this machine";
        }
        if (shared.IsMultiple)
        {
            return $"{shared.Name} is a launch profile of the solution's already, kept beside it where Visual Studio reads it too";
        }
        if (shared.IsScript)
        {
            return $"{shared.Name} runs a package.json script: a launch profile saved here runs a .NET project";
        }
        if (Projects.FirstOrDefault(p => p.Exists && p.IsRunnable && PathNames.Same(p.Path, shared.Project)) is not { } project)
        {
            return shared.Project.Length == 0 ? $"{shared.Name} has no project to run" : $"{shared.Name}'s project, {Path.GetFileNameWithoutExtension(shared.Project)}, is not loaded";
        }
        string profile = LaunchSettingsEdits.ProfileName(project, shared);
        if (profile.Length == 0)
        {
            return "A configuration needs a name";
        }
        if (LaunchProfile.Read(project).FirstOrDefault(p => string.Equals(p.Name, profile, StringComparison.OrdinalIgnoreCase)) is { } taken)
        {
            return $"{project.Name}'s {LaunchProfile.SettingsName(project.Path)} has a profile named {taken.Name} already";
        }
        string generated = RunConfiguration.NameOf(project, profile);
        return listed.FirstOrDefault(c => !ReferenceEquals(c, shared) && string.Equals(c.Name, generated, StringComparison.OrdinalIgnoreCase)) is { } named
            ? $"Saved as the profile {profile}, it would be {generated}, as {named.Name} is named already"
            : null;
    }

    /// <summary>Save as Launch Profile, from Edit Configurations (the dialog applied first, then shown as it then stands) or
    /// Find Action (the toolbar's configuration, what was done said in the status bar).</summary>
    private async Task ShareChosenAsync()
    {
        if (RunConfigurationsDialog is not { } dialog)
        {
            if (Configuration is { } chosen)
            {
                (RunConfiguration? shared, string said) = await SaveAsLaunchProfileAsync(chosen.Name);
                status.Text = shared is null ? said : said + KeptOf(shared);
            }
            return;
        }
        if (dialog.Chosen is not { } listed || !dialog.Applied())
        {
            return;
        }
        (RunConfiguration? now, string done) = await SaveAsLaunchProfileAsync(listed.Name);
        RunConfigurations configurations = session.Configurations;
        dialog.Took([.. configurations.All.Where(c => c.Generated)], configurations.Own, now, done);
    }

    /// <summary>What a configuration saved as a launch profile keeps with the folder, by the store's names of its fields,
    /// as the status bar and an agent are told it; empty for nothing.</summary>
    private static string KeptOf(RunConfiguration shared) => shared.Source is { } source && ConfigurationEdit.Changed(source, shared) is { Count: > 0 } left
        ? $"; kept here, as launchSettings.json has no place for it: {string.Join(", ", left)}"
        : string.Empty;

    /// <summary>An agent's Save as Launch Profile (ide_action) of one of the folder's own configurations by name, else the
    /// one chosen: what was done, or why not as an <see cref="InvalidOperationException"/>.</summary>
    public async Task<string> SaveAsLaunchProfileForAgentAsync(string? name)
    {
        string? why = RunConfigurationsDialog is not null ? "Run/Debug Configurations is open in the window, its changes not yet applied"
            : name is null && Configuration is null ? "No run configuration is chosen"
            : null;
        if (why is not null)
        {
            throw new InvalidOperationException($"{AppActions.SaveAsLaunchProfile} did not run: {why}");
        }
        (RunConfiguration? shared, string said) = await SaveAsLaunchProfileAsync(name ?? Configuration!.Name);
        return shared is null ? throw new InvalidOperationException($"{AppActions.SaveAsLaunchProfile} did not run: {said}") : said + KeptOf(shared);
    }

    /// <summary>Save as Launch Profile: one of the folder's own configurations, by name, written as a new profile of its
    /// project's launchSettings.json (saved first if open, taken in after; made in dotnet new's shape if the project has
    /// none), and from then on the configuration that profile generates, what the profile has no place for kept with the
    /// folder as its change; one command Undo takes back whole. The configuration as it then stands, with what was done,
    /// or null with why not, once the file has landed.</summary>
    public async Task<(RunConfiguration? Configuration, string Said)> SaveAsLaunchProfileAsync(string name)
    {
        RunConfigurations configurations = session.Configurations;
        if (configurations.Own.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } mine)
        {
            return (null, configurations.All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { } other
                ? WhyNotShare(other, configurations.All) ?? $"{other.Name} is not one of the folder's own"
                : $"No run configuration is named {name}");
        }
        if (WhyNotShare(mine, configurations.All) is { } why)
        {
            return (null, why);
        }
        ProjectModel project = Projects.First(p => PathNames.Same(p.Path, mine.Project));
        string profile = LaunchSettingsEdits.ProfileName(project, mine);
        string path = LaunchProfile.SettingsPath(project);
        string properties = Path.GetDirectoryName(path)!;
        bool madeFolder = !Directory.Exists(properties);
        bool madeFile = !File.Exists(path);
        int at = configurations.Own.ToList().IndexOf(mine);
        bool chosen = configurations.Chosen == mine;
        JsonObject? before = configurations.EditOf(RunConfiguration.NameOf(project, profile));
        using CommandHistory.Recording made = Editors.Commands.Record($"Save {mine.Name} as a Launch Profile");
        if (await ChangeFileAsync([path], () => LaunchSettingsEdits.Add(path, profile, LaunchSettingsEdits.ProfileOf(mine)), made) is { } problem)
        {
            return (null, $"{Path.GetFileName(path)} could not be changed: {await made.TakenBackAsync(problem)}");
        }
        if (madeFolder)
        {
            // Redo makes it again with the file; Undo, the file gone, takes it away if nothing else is in it.
            made.Also(new CommandHistory.Part("the Properties folder", _ => null, redo =>
            {
                try
                {
                    if (!redo && Directory.Exists(properties) && !Directory.EnumerateFileSystemEntries(properties).Any())
                    {
                        Directory.Delete(properties);
                    }
                    return Task.FromResult<string?>(null);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return Task.FromResult<string?>($"the Properties folder could not be deleted: {error.Message}");
                }
            }));
        }
        if (configurations.Share(mine.Name, project.Path, profile) is not { } now)
        {
            return (null, $"The launch profile {profile} was written, but makes no run configuration");
        }
        JsonObject? after = configurations.EditOf(now.Name);
        made.Also(new CommandHistory.Part($"the run configuration {mine.Name}",
            redo => redo
                ? configurations.Own.FirstOrDefault(c => string.Equals(c.Name, mine.Name, StringComparison.OrdinalIgnoreCase)) is not { } back || !ConfigurationEdit.Same(back, mine)
                    ? $"{mine.Name} has been changed since" : null
                : !JsonNode.DeepEquals(configurations.EditOf(now.Name), after) ? $"{now.Name} has been changed since"
                : configurations.Own.Any(c => string.Equals(c.Name, mine.Name, StringComparison.OrdinalIgnoreCase)) ? $"A configuration is named {mine.Name} again"
                : null,
            redo =>
            {
                if (redo)
                {
                    return Task.FromResult(configurations.Share(mine.Name, project.Path, profile) is null ? $"The launch profile {profile} makes no run configuration" : null);
                }
                configurations.Unshare(mine, at, now.Name, before, chosen);
                return Task.FromResult<string?>(null);
            }));
        return (now, $"Saved as the launch profile {profile} in {project.Name}'s {LaunchProfile.SettingsShown(project.Path)}{(madeFile ? ", made for it" : string.Empty)}: "
            + $"it is now {now.Name}, generated from that profile");
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

    /// <summary>What the command line's <c>--run</c> or <c>--debug</c> came to (<see cref="StartConfigurationAsync"/>) in
    /// the window that opened its folder: null once its action ran, or with neither given; else why not, which ends
    /// Passenger at its start as a malformed flag does.</summary>
    public Task<string?> StartedRun { get; private set; } = Task.FromResult<string?>(null);

    /// <summary>Why the start the command line's <c>--run</c> or <c>--debug</c> handed over came to nothing (its build failed,
    /// its program did not start), which the log and standard error say beside what a person's Run says; the window stays
    /// open on it. Null where it runs, or with neither given.</summary>
    public string? NotStarted { get; private set; }

    private static async Task<string?> StartOnceBegunAsync(Task<MainWindow> begun, ConfigurationStart start) =>
        await (await begun).StartConfigurationAsync(start);

    /// <summary>Runs or debugs a configuration through Run's or Debug's action, as the person's key runs it, once the
    /// folder's run configurations are made from its projects as MSBuild evaluated them: the one named chosen first, as the
    /// toolbar's menu chooses it. Null once the action ran and handed its start over, its build, console and Game pane the
    /// person's Run's, one that came to nothing said as theirs is and in <see cref="NotStarted"/>; else why not: no folder
    /// open, a name none of the folder's configurations has (those it has listed), or why the action is greyed.</summary>
    public async Task<string?> StartConfigurationAsync(ConfigurationStart start)
    {
        UiThread.Check();
        ProjectSession reading = session;
        if (reading.Folder is not { } folder)
        {
            return start.NoFolder;
        }
        string shown = PathNames.Shown(folder, folder);
        // As an agent's open waits: the walk for its projects, its single-file AppHosts and Node packages, then the evaluations those started.
        await reading.WorkspaceRead;
        await reading.AspireFilesRead;
        await reading.NodeFilesRead;
        await Task.Yield();
        await reading.Models.Idle;
        if (reading.IsEnded)
        {
            return $"{start.Flag}: {shown} closed before its run configurations were read";
        }
        RunConfigurations configurations = reading.Configurations;
        if (start.WhyNot(shown, [.. configurations.All.Select(c => c.Name)]) is { } unknown)
        {
            return unknown;
        }
        if (start.Name is { } name)
        {
            configurations.Select(name);
        }
        Hearing heard = ActionWork.Hear();
        string? why;
        try
        {
            why = actions!.Run(start.Debug ? AppActions.Debug : AppActions.RunProgram);
        }
        finally
        {
            heard.Ran();
        }
        if (why is not null)
        {
            return start.Cannot(configurations.Chosen?.Name ?? shown, why);
        }
        await heard.DoneAsync(CancellationToken.None);
        if (heard.Outcome is { Failed: true, Said: { } failed })
        {
            NotStarted = start.Cannot(configurations.Chosen?.Name ?? shown, failed, reading.Problems.Find(p => p.Severity == ProblemSeverity.Error) is { } first ? $"{first.Where}: {first.Message}" : null);
            Program.SayNotStarted(NotStarted);
        }
        return null;
    }

    /// <summary>Debug Another Copy (<see cref="BuildRunController.DebugAnotherCopyAsync"/>): what stopped it, if anything, goes to
    /// the status bar; null once the copy's session is open.</summary>
    public async Task<string?> DebugAnotherCopyAsync()
    {
        string? problem = await buildRun.DebugAnotherCopyAsync();
        if (problem is not null)
        {
            status.Text = problem;
        }
        return problem;
    }

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
        System.Diagnostics.ProcessStartInfo start = ChildProcesses.Command(program is "dotnet" ? Core.Dotnet.Host(arguments.Cwd ?? Folder) : program, rest, arguments.Cwd ?? Folder, arguments.Env);
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
        RunConfigName.Classes.Set(StyleClasses.Muted, why is not null);
        ToolTip.SetTip(RunConfigButton, why is null ? "Run configuration" : $"{chosen!.Name} cannot run here: {why}");
    }

    /// <summary>The run configurations to choose from, as a menu under the toolbar widget, and Edit Configurations
    /// below them: one that cannot launch here is listed greyed, with why beside it, and can still be chosen, so Run
    /// says why and Edit Configurations shows it.</summary>
    private void OnRunConfigClicked(object? sender, RoutedEventArgs e)
    {
        ContextMenu menu = new();
        foreach (RunConfiguration each in Configurations.Where(c => !c.IsScript || !c.Generated))
        {
            menu.Items.Add(Choice(each));
        }
        // A package's scripts after the rest, each package's under its name where there are several.
        List<IGrouping<string, RunConfiguration>> scripts = [.. Configurations.Where(c => c is { IsScript: true, Generated: true }).GroupBy(c => c.Project, PathNames.Comparer)];
        if (scripts.Count > 0 && menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }
        foreach (IGrouping<string, RunConfiguration> package in scripts)
        {
            if (scripts.Count == 1)
            {
                foreach (RunConfiguration each in package)
                {
                    menu.Items.Add(Choice(each));
                }
                continue;
            }
            NodePackage? read = session.NodeFiles.Packages.FirstOrDefault(p => PathNames.Same(p.Manifest, package.Key));
            string header = read is null ? PathNames.Shown(Path.GetDirectoryName(package.Key)!, session.Folder) : $"{read.Name} ({NodeProjects.ProgramOf(read.Manager)})";
            MenuItem holding = new() { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = package.Any(c => c.Name == session.Configurations.Chosen?.Name) };
            holding.ItemsSource = (Control[])[.. package.Select(Choice)];
            menu.Items.Add(holding);
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

        MenuItem Choice(RunConfiguration each)
        {
            string name = each.Name;
            string? why = WhyNotHere(each);
            MenuItem item = MenuChoice.Of(why is null ? Shown(each) : Greyed(name, why), session.Configurations.Chosen?.Name == name, () => SelectConfiguration(name));
            if (why is not null)
            {
                ToolTip.SetTip(item, $"{name} cannot run here: {why}");
            }
            return item;
        }

        static StackPanel Greyed(string name, string why) => new()
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = name, Classes = { StyleClasses.Muted } },
                new TextBlock { Text = why, Classes = { StyleClasses.Muted }, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            },
        };
    }

}
