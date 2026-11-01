using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>A Godot project's game and editor as a launch starts them: on the Godot Settings names (its command line's
/// program, its arguments given every start), else the one found (<see cref="GodotExecutables"/>), with <c>--path</c>
/// naming the project's folder, and <c>--editor</c> for its editor. Godot loads the project's C# itself, from the
/// assembly its build makes there.</summary>
public static class GodotLaunch
{
    /// <summary>The Godot to run, as the setting's command line names it or as found without it; null when none is.</summary>
    public static GodotFound? Found(string? setting) => Chosen(setting) is ({ } program, _)
        ? GodotExecutables.At(program, SettingsDialog.PlaceOfPage(SettingsDialog.GodotPage), OperatingSystem.IsWindows())
        : GodotExecutables.Find(null);

    /// <summary>Why Run takes no Hot Reload for a Godot game: dotnet watch's agent comes into a program as a startup hook,
    /// which .NET runs only for a program it starts at its Main, and Godot starts it as a host of its own.</summary>
    public const string WithoutHotReload = "Godot starts .NET itself and runs no startup hook, which is how dotnet watch applies changes: "
        + "Debug the game, and Hot Reload applies them through the debugger";

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

    /// <summary>The plan of a Godot project's configuration that names no program of its own: its game, or its editor
    /// (<see cref="RunConfiguration.GodotEditor"/>), on the Godot found, the configuration's arguments after those that
    /// start it, in its working folder or else the Godot project's. Throws a <see cref="LaunchException"/> saying why it
    /// cannot: no project.godot, no Godot found or one that cannot run C#, no scene for the game to start in, or a
    /// project.godot that loads another assembly than the project builds.</summary>
    internal static LaunchPlan Plan(RunConfiguration configuration, ProjectModel project, string folder, string target, string? framework, Func<string, string> expand,
        IReadOnlyDictionary<string, string> environment, string? setting)
    {
        GodotProject game = GodotProject.Read(folder)
            ?? throw new LaunchException($"{configuration.Name} cannot run: {folder} holds no {GodotProject.FileName}, which makes it the Godot project Godot runs");
        GodotFound found = Found(setting) ?? throw new LaunchException($"{configuration.Name} cannot run: {NotFound}");
        if (found.WhyNot is { } why)
        {
            throw new LaunchException($"{configuration.Name} cannot run: {why}");
        }
        string built = Path.GetFileNameWithoutExtension(target);
        if (game.AssemblyName is { Length: > 0 } loaded && !string.Equals(loaded, built, StringComparison.OrdinalIgnoreCase))
        {
            throw new LaunchException($"{configuration.Name} cannot run: Godot loads the game's C# from {loaded}.dll, as its {GodotProject.FileName}'s dotnet/project/assembly_name "
                + $"says, but {project.Name} builds {Path.GetFileName(target)}: name the one assembly in both");
        }
        IReadOnlyList<string> own = CommandLine.Split(expand(configuration.Arguments));
        if (!configuration.GodotEditor && string.IsNullOrEmpty(game.MainScene) && !own.Any(IsScene))
        {
            throw new LaunchException($"{configuration.Name} cannot run: its {GodotProject.FileName} names no main scene for the game to start in: choose one in Godot's editor "
                + "(Project Settings, Application, Run, Main Scene), or give one in the configuration's arguments (res://main.tscn)");
        }
        List<string> arguments = [.. Chosen(setting)?.Arguments ?? [], "--path", folder];
        if (configuration.GodotEditor)
        {
            arguments.Add("--editor");
        }
        int leading = arguments.Count;
        arguments.AddRange(own);
        string working = configuration.WorkingDirectory is { Length: > 0 } given
            ? Path.TrimEndingDirectorySeparator(PathNames.Canonical(Path.Combine(project.Folder, expand(given))))
            : folder;
        return new LaunchPlan
        {
            Name = configuration.Name,
            Program = found.Executable,
            Arguments = arguments,
            Leading = leading,
            WorkingDirectory = working,
            Environment = environment,
            Project = project.Path,
            Framework = framework,
            Build = configuration.BuildBeforeLaunch ? new BuildStep(project.Path, framework) : null,
            Built = target,
            Console = configuration.Console,
            Godot = folder,
            GodotVersion = found.Version,
        };
    }

    /// <summary>Whether an argument names a scene for Godot to start in.</summary>
    private static bool IsScene(string argument) =>
        argument.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) || argument.EndsWith(".scn", StringComparison.OrdinalIgnoreCase);
}
