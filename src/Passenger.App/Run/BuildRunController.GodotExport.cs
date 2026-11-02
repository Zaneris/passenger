using System.Text.RegularExpressions;
using Passenger.App.Godot;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>An export of a Godot project's preset asked for: the project's folder, the preset's name, whether it is a debug
/// export, and where the game goes (from the project's folder, or whole), the preset's own path for none.</summary>
public sealed record GodotExportRequest(string Folder, string Preset, bool Debug = false, string? Path = null);

/// <summary>An export of a Godot project as Publish runs it: the Godot that runs it (the setting's or the one found) headless
/// on the project with <c>--export-release</c> or <c>--export-debug</c>, its arguments, where the game lands, and the
/// templates it lacks; or why it cannot run.</summary>
public sealed record GodotExportPlan(string Program, IReadOnlyList<string> Arguments, string Output, IReadOnlyList<string> Missing, string? Templates, string? Version)
{
    /// <summary>The plan of a request, read off the window's thread: the preset found in export_presets.cfg, the Godot found
    /// running the project, its templates' folder in the user folders given; why not, else.</summary>
    public static async Task<(GodotExportPlan? Plan, string? Why)> OfAsync(GodotExportRequest request, string? setting, bool scripts, IReadOnlyDictionary<string, string>? folders)
    {
        IReadOnlyList<GodotPreset> presets = GodotExports.ReadIn(request.Folder);
        if (presets.FirstOrDefault(p => p.Name == request.Preset) is not { } preset)
        {
            return (null, presets.Count == 0 ? $"{PathNames.Shown(request.Folder, null)} has no {GodotExports.FileName}: make an export preset in Godot's editor (Project, Export)"
                : $"{GodotExports.FileName} has no preset {request.Preset}: it has {Plural.Listed([.. presets.Select(p => p.Name)])}");
        }
        string? path = request.Path ?? preset.ExportPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, $"The preset {preset.Name} names no file to export to: give one");
        }
        if (GodotLaunch.Found(setting) is not { } found)
        {
            return (null, $"{preset.Name} cannot be exported: {GodotLaunch.NotFound}");
        }
        if ((scripts ? found.WhyNotScripts : found.WhyNot) is { } why)
        {
            return (null, $"{preset.Name} cannot be exported: {why}");
        }
        string? answer = await GodotExecutables.AnswerAsync(found);
        string? version = answer is null ? null : GodotExports.TemplatesVersion(answer);
        string? templates = version is null ? null
            : GodotExports.TemplatesFolder(found.Executable, version, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), name => GodotUserFolders.Variable(folders, name), File.Exists);
        string output = System.IO.Path.GetFullPath(System.IO.Path.Combine(request.Folder, path.StartsWith("res://", StringComparison.Ordinal) ? path["res://".Length..] : path));
        List<string> arguments = [.. GodotLaunch.Chosen(setting)?.Arguments ?? []];
        if (!GodotExecutables.RunsHeadless(arguments))
        {
            arguments.Add(GodotExecutables.Headless);
        }
        arguments.AddRange(["--path", request.Folder, request.Debug ? "--export-debug" : "--export-release", preset.Name, output]);
        return (new GodotExportPlan(found.Executable, arguments, output, GodotExports.Missing(preset, request.Debug, templates, File.Exists, request.Folder), templates, version), null);
    }
}

/// <summary>A Godot project's export: Godot's headless export of a preset in the Run window, its errors folded and linked as
/// a game's are (<see cref="GodotConsoleFold"/>), a <see cref="Working"/> saying each stage Godot reports, refused before
/// it starts where the templates it takes are not installed, what its editor writes over of the person's put back once it
/// ends (<see cref="GodotScripts.PuttingBackAsync"/>). Stop ends it.</summary>
public sealed partial class BuildRunController
{
    /// <summary>Exports a preset as Publish of a Godot project does; says how it ended once it has, an export never said to
    /// have succeeded unless Godot ended well and the game is there.</summary>
    public async Task<BuildOutcome> ExportGodotAsync(GodotExportRequest request, CancellationToken cancel = default)
    {
        UiThread.Check();
        if (session.Folder is null || session.IsEnded)
        {
            return Refused(Refusals.NoFolderTo("export"));
        }
        if (IsBuilding)
        {
            return Refused(Refusals.Building);
        }
        if (RunsInConsole)
        {
            return Refused(AlreadyRunning());
        }
        if (parts.Work.Begin(AppActions.Publish, $"Reading how {request.Preset} exports…") is not { } work)
        {
            return Refused(ActionWork.Busy(parts.Work.Of(AppActions.Publish)!));
        }
        using (work)
        using (cancel.Register(work.Cancel))
        using (BackgroundWork.Scope())
        {
            string? setting = parts.Settings().GodotExecutable;
            bool scripts = !session.Projects.Any(p => p.GodotFolder is { } game && PathNames.Same(game, request.Folder));
            IReadOnlyDictionary<string, string>? folders = session.GodotEnvironment ?? GodotUserFolders.OfVariable();
            (GodotExportPlan? plan, string? why) = await BackgroundWork.Run(() => GodotExportPlan.OfAsync(request, setting, scripts, folders));
            if (plan is null)
            {
                return Refused(why!);
            }
            if (plan.Missing.Count > 0)
            {
                return Refused($"{request.Preset} cannot be exported: Godot {plan.Version} has no {Plural.Listed(plan.Missing)} among its export templates"
                    + $"{(plan.Templates is { } folder ? $" ({PathNames.Shown(folder, null)})" : string.Empty)}: install them from a {GodotExports.Archive} in Publish");
            }
            if (await parts.Editors.SaveAllLandedAsync(work, work.Stopping) is { } unsaved)
            {
                return Refused(unsaved);
            }
            if (session.IsEnded || work.IsStopped)
            {
                return Said(new BuildOutcome(BuildStatus.Stopped, "Export stopped"));
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(plan.Output)!);
            }
            catch (Exception unmade) when (unmade is IOException or UnauthorizedAccessException)
            {
                return Said(new BuildOutcome(BuildStatus.NotStarted, $"{PathNames.Shown(Path.GetDirectoryName(plan.Output)!, session.Folder)} could not be made: {unmade.Message}"));
            }
            parts.Views.Run.Clear();
            UnfollowWatched();
            parts.Views.Run.Fold = new GodotConsoleFold();
            session.InTerminal = null;
            ran = $"Export {request.Preset}";
            parts.Views.Run.SetRunning(true);
            parts.Dock.Show(ToolWindowIds.Run);
            string kind = request.Debug ? "a debug export" : "a release export";
            work.Say($"Exporting {request.Preset}, {kind}…");
            parts.Say($"Exporting {request.Preset} with Godot {plan.Version}");
            void OnLine(object? sender, string line)
            {
                if (ExportStage().Match(ControlCodes().Replace(line, string.Empty)) is { Success: true } stage)
                {
                    work.Say($"Exporting {request.Preset}: {stage.Groups["words"].Value.Trim()} ({stage.Groups["done"].Value}%)");
                }
            }
            session.Runner.Line += OnLine;
            try
            {
                Dictionary<string, string> environment = new(GodotUserFolders.Into(new Dictionary<string, string>(), folders), StringComparer.Ordinal);
                (string? notStarted, int code) = await GodotScripts.PuttingBackAsync(request.Folder, async () =>
                {
                    if (await session.Runner.StartAsync(plan.Program, plan.Arguments, request.Folder, environment) is { } unstarted)
                    {
                        return (unstarted, 0);
                    }
                    RunningChanged?.Invoke(this, EventArgs.Empty);
                    using (work.Stopping.Register(session.Runner.Stop))
                    {
                        return ((string?)null, await session.Runner.Ended);
                    }
                });
                return Said(notStarted is null ? Exported(request, plan, code, session.Runner.WasStopped || work.IsStopped) : new BuildOutcome(BuildStatus.NotStarted, notStarted));
            }
            finally
            {
                session.Runner.Line -= OnLine;
            }
        }

        BuildOutcome Refused(string why) => Said(new BuildOutcome(BuildStatus.Refused, why));
    }

    /// <summary>How an export ended: the game there once Godot ended well, else what Godot said first of its failure.</summary>
    private BuildOutcome Exported(GodotExportRequest request, GodotExportPlan plan, int code, bool stopped)
    {
        if (stopped)
        {
            return new BuildOutcome(BuildStatus.Stopped, $"Export of {request.Preset} stopped");
        }
        if (code == 0 && (File.Exists(plan.Output) || Directory.Exists(plan.Output)))
        {
            string landed = PathNames.Canonical(plan.Output);
            parts.Views.Run.Append($"Exported {request.Preset} to {landed}\n");
            return new BuildOutcome(BuildStatus.Succeeded, $"Exported {request.Preset} to {PathNames.Shown(landed, session.Folder)}") { Published = Path.GetDirectoryName(landed) };
        }
        string? first = session.Runner.Shown().ToString().Split('\n').Select(l => l.Trim()).FirstOrDefault(l => GodotConsoleFold.IsHeader(l) && !l.Contains("no default was given", StringComparison.Ordinal));
        string ended = code == 0 ? $"Godot ended without writing {PathNames.Shown(plan.Output, session.Folder)}" : ExitCodes.Said(code);
        return new BuildOutcome(BuildStatus.Failed, $"Export of {request.Preset} failed ({ended}){(first is null ? string.Empty : $": {first}")}");
    }

    /// <summary>Godot's word of a stage of its work ("[  25% ] export | Making PKG"), its share done and its words.</summary>
    [GeneratedRegex(@"^\[\s*(?<done>\d+)% \]\s+\S+\s+\|\s+(?<words>.+)$")]
    private static partial Regex ExportStage();

    /// <summary>A terminal's colour sequences, which Godot writes around its stages' words.</summary>
    [GeneratedRegex(@"\u001b\[[0-9;]*m")]
    private static partial Regex ControlCodes();
}
