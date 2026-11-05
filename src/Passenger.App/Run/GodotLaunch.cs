using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>A Godot project's game and editor as a launch starts them: on the Godot Settings names (its command line's
/// program, its arguments given every start), else the one found (<see cref="GodotExecutables"/>), with <c>--path</c>
/// naming the project's folder, and <c>--editor</c> for its editor; a launch profile running Godot on the project
/// (%GODOT%) runs its game so too, its own arguments after. Godot loads the project's C# itself, from the assembly its
/// build makes there.</summary>
public static class GodotLaunch
{
    /// <summary>The Godot to run, as the setting's command line names it or as found without it; null when none is.</summary>
    public static GodotFound? Found(string? setting) => Chosen(setting) is ({ } program, _)
        ? GodotExecutables.At(program, SettingsDialog.PlaceOfPage(SettingsDialog.GodotPage), OperatingSystem.IsWindows())
        : GodotExecutables.Find(null);

    /// <summary>What Hot Reload is to Godot's editor, which loads the project's C# (its tool scripts) into a context it can
    /// unload, and loads it again from each new build by itself (its assembly watcher): a build applies an edit there.</summary>
    public const string EditorReloads = "Godot's editor loads the project's C# into a context it unloads, and loads it again from each new build: "
        + "Build the project to apply an edit to its tool scripts, which start again with the new build";

    /// <summary>What a setting's line runs, as Settings says it under the box: the Godot found (or chosen) with its version as
    /// it answers, and that it is a .NET build, which runs C#; or why none would run a C# game.</summary>
    public static async Task<string> DescribedAsync(string? setting)
    {
        GodotFound? found = await BackgroundWork.Run(() => Found(setting));
        if (found is null)
        {
            return $"{char.ToUpperInvariant(NotFound[0])}{NotFound[1..]}";
        }
        if (found.WhyNot is { } why)
        {
            return $"{char.ToUpperInvariant(why[0])}{why[1..]}";
        }
        string version = await GodotExecutables.VersionAsync(found) is { } said ? $"Godot {said}" : "Godot, its version unsaid";
        return $"{version}, a .NET build, which runs C#{(setting is null ? $", from {found.From}" : string.Empty)}: {found.Executable}";
    }

    /// <summary>Why no Godot runs a Godot project's game, in words for the person.</summary>
    public static string NotFound =>
        $"no Godot was found to run it: install Godot's .NET build, then choose its executable in {SettingsDialog.PlaceOfPage(SettingsDialog.GodotPage)}, set {GodotExecutables.Variable} to it, or put it on the PATH";

    /// <summary>The program and the arguments a setting's command line gives: the whole line when it is a path that is there
    /// (one with spaces needs no quotes), else its first word and those after it; null for none.</summary>
    internal static (string Program, IReadOnlyList<string> Arguments)? Chosen(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
        {
            return null;
        }
        string line = setting.Trim();
        if (File.Exists(line) || Directory.Exists(line))
        {
            return (line, []);
        }
        IReadOnlyList<string> words = CommandLine.Split(line);
        return words is [{ Length: > 0 } program, ..] ? (program, [.. words.Skip(1)]) : null;
    }

    /// <summary>The plan of a Godot project's configuration that names no program of its own, or Godot: its game, or its editor
    /// on the scene of the file at hand (<see cref="GodotProject.SceneOf"/>), on the Godot found, its arguments after those that
    /// start it, in its working folder or the Godot project's, headless where nobody sees it (<paramref name="unseen"/>); of
    /// GDScript alone (no <paramref name="target"/>) on any Godot 4, nothing built. A <see cref="LaunchException"/> says why not:
    /// no project.godot or Godot, a Godot.NET.Sdk (<paramref name="sdk"/>) newer than it, no scene, another assembly or folder.</summary>
    internal static async Task<LaunchPlan> PlanAsync(RunConfiguration configuration, ProjectModel project, string folder, string? target, string? framework,
        Func<string, string> expand, IReadOnlyDictionary<string, string> environment, string? setting, string? sdk, bool unseen, CancellationToken cancel, string? atHand = null)
    {
        GodotProject game = GodotProject.Read(folder)
            ?? throw new LaunchException($"{configuration.Name} cannot run: {folder} holds no {GodotProject.FileName}, which makes it the Godot project Godot runs");
        GodotFound found = Found(setting) ?? throw new LaunchException($"{configuration.Name} cannot run: {NotFound}");
        if ((project.IsScriptGame ? found.WhyNotScripts : found.WhyNot) is { } why)
        {
            throw new LaunchException($"{configuration.Name} cannot run: {why}");
        }
        string? version = await GodotExecutables.VersionAsync(found, cancel);
        if (sdk is { Length: > 0 } && version is not null && GodotExecutables.Mismatch(sdk, version) is { } mismatch)
        {
            throw new LaunchException($"{configuration.Name} cannot run: {mismatch}");
        }
        if (target is not null && game.AssemblyName is { Length: > 0 } loaded && !string.Equals(loaded, Path.GetFileNameWithoutExtension(target), StringComparison.OrdinalIgnoreCase))
        {
            throw new LaunchException($"{configuration.Name} cannot run: Godot loads the game's C# from {loaded}.dll, as its {GodotProject.FileName}'s dotnet/project/assembly_name "
                + $"says, but {project.Name} builds {Path.GetFileName(target)}: name the one assembly in both");
        }
        string working = configuration.WorkingDirectory is { Length: > 0 } given
            ? Path.TrimEndingDirectorySeparator(PathNames.Canonical(Path.Combine(project.Folder, expand(given))))
            : folder;
        IReadOnlyList<string> own = CommandLine.Split(expand(configuration.Arguments));
        if (configuration.NamesGodot)
        {
            // A launch profile running Godot names the project itself, which the launch names once below.
            own = GodotExecutables.WithoutPath(own, working, folder) ?? throw new LaunchException($"{configuration.Name} cannot run: it runs Godot with a --path that is not "
                + $"{project.Name}'s Godot project ({folder}): make its --path name that folder, or leave it out");
            own = configuration.GodotEditor ? GodotExecutables.WithoutEditorFlags(own) : own;
            if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(GodotExecutables.Variable))
                && !environment.Keys.Any(k => string.Equals(k, GodotExecutables.Variable, StringComparison.OrdinalIgnoreCase)))
            {
                // What the profile named, for the game and the tools it starts.
                environment = new Dictionary<string, string>(environment, StringComparer.Ordinal) { [GodotExecutables.Variable] = found.Executable };
            }
        }
        string? starts = configuration.GodotEditor ? null : configuration.GodotScene;
        if (starts == RunConfiguration.CurrentScene)
        {
            // The scene at hand, or one its script is attached in, as the editor's configuration opens it.
            starts = atHand is not null && await BackgroundWork.Run(() => GodotProject.SceneOf(folder, atHand, game.MainScene)) is { } open
                ? GodotProject.ResourcePath(folder, open)
                : throw new LaunchException($"{configuration.Name} cannot run: it starts in the scene open in the editor, and none of {game.Name ?? project.Name} is "
                    + "(a .tscn, or a script a scene's node runs)");
        }
        if (!configuration.GodotEditor && starts is null && string.IsNullOrEmpty(game.MainScene) && !NamesScene(own))
        {
            throw new LaunchException($"{configuration.Name} cannot run: its {GodotProject.FileName} names no main scene for the game to start in: choose one in Godot's editor "
                + "(Project Settings, Application, Run, Main Scene), or give one in the configuration's arguments (res://main.tscn)");
        }
        if (!configuration.GodotEditor && await BackgroundWork.Run(() => Godot.GodotScripts.Unimported(folder), cancel))
        {
            // A game loads only what Godot's editor imported, which a project never opened in it has not.
            ChildResult imported = await Godot.GodotScripts.ImportAsync(found.Executable, Chosen(setting)?.Arguments ?? [], folder, environment, Godot.GodotScripts.MadeFiles.Uids, cancel);
            if (!imported.Succeeded)
            {
                throw new LaunchException($"{configuration.Name} cannot run: Godot could not import {PathNames.Shown(folder, null)}'s assets, which the game loads as Godot's editor "
                    + $"imports them (exit code {imported.ExitCode}): {ChildResult.FirstLineOf(imported.Error.Length > 0 ? imported.Error : imported.Output)}");
            }
        }
        List<string> arguments = [.. Chosen(setting)?.Arguments ?? []];
        bool ownDesktop = false;
        // Nobody would see its window: Godot runs headless, but an agent's own game draws where the system can with none, for its frames.
        if (unseen && !GodotExecutables.RunsHeadless([.. arguments, .. own]))
        {
            // Windows' desktop of its own takes a start of Passenger's own, which the terminal's (its console, its copies) is not.
            bool drawn = PassengerApp.ForAgent && !configuration.GodotEditor && GodotExecutables.DrawsWithoutWindow(version)
                && (!OperatingSystem.IsWindows() || (configuration.Console == ConsoleKind.Internal && configuration.GodotCopies <= 1));
            string? display = drawn && OperatingSystem.IsLinux() ? await Godot.VirtualDisplays.OpenAsync(cancel) : null;
            drawn &= !OperatingSystem.IsLinux() || display is not null;
            arguments.AddRange(drawn ? GodotExecutables.UnseenArguments(game.WindowSize.Width, game.WindowSize.Height) : [GodotExecutables.Headless]);
            ownDesktop = drawn && OperatingSystem.IsWindows();
            if (display is not null && drawn)
            {
                environment = new Dictionary<string, string>(environment, StringComparer.Ordinal) { [Godot.VirtualDisplays.Variable] = display };
            }
        }
        arguments.AddRange(["--path", folder]);
        if (configuration.GodotEditor)
        {
            arguments.Add("--editor");
            // The scene at hand, or one its script is attached in, opens in the editor, unless the configuration names one.
            if (atHand is not null && !NamesScene(own) && await BackgroundWork.Run(() => GodotProject.SceneOf(folder, atHand, game.MainScene)) is { } scene)
            {
                arguments.Add(GodotProject.ResourcePath(folder, scene));
            }
        }
        if (starts is not null)
        {
            arguments.Add(starts);
        }
        int leading = arguments.Count;
        arguments.AddRange(own);
        return new LaunchPlan
        {
            Name = configuration.Name,
            Program = found.Executable,
            Arguments = arguments,
            Leading = leading,
            WorkingDirectory = working,
            Environment = GodotExecutables.EnvironmentOf(arguments, environment),
            Project = project.Path,
            Framework = framework,
            Build = configuration.BuildBeforeLaunch && target is not null ? new BuildStep(project.Path, framework) : null,
            Built = target,
            Console = configuration.Console,
            Godot = folder,
            GodotVersion = version,
            GodotScene = starts,
            GodotUnseen = unseen,
            OwnDesktop = ownDesktop,
            GodotInPane = configuration.GodotInPane && !configuration.GodotEditor && !configuration.GodotInEditor,
            GodotSize = game.WindowSize,
            GodotStretches = game.Stretches,
            ScriptsAlone = project.IsScriptGame,
            GameName = project.IsScriptGame ? project.Name : null,
        };
    }

    /// <summary>Whether arguments name a scene for Godot to start in: one of Godot's own, never one the game is given.</summary>
    private static bool NamesScene(IReadOnlyList<string> arguments) => GodotExecutables.EngineArguments(arguments).Any(GodotProject.IsScene);
}
