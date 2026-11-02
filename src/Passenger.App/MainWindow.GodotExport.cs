using Avalonia.Platform.Storage;
using Passenger.App.Godot;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Publish of a Godot project: its export presets in the Export dialog (<see cref="GodotExportDialog"/>), the one
/// chosen exported by Godot headless in the Run window (<see cref="BuildRunController.ExportGodotAsync"/>), and the export
/// templates of the Godot found installed from a .tpz the person picks, into the user folders the window's Godots keep,
/// only those the project's presets take, once asked; nothing is downloaded.</summary>
public partial class MainWindow
{
    /// <summary>The Export dialog of a Godot project open over the window, or null.</summary>
    public GodotExportDialog? GodotExportDialog { get; private set; }

    private void AddGodotExport()
    {
        actions!.Add(new IdeAction(AppActions.InstallGodotTemplates, "Install Godot Export Templates…", "Godot",
            c => IdeErrors.Observe(InstallGodotTemplatesAsync(GodotProjectOf(c)!, GodotExports.ReadIn(GodotProjectOf(c)!))))
        {
            WhyNot = c => Folder is null ? Refusals.NoFolder : GodotProjectOf(c) is null ? Refusals.NoGodotProject : null,
            Words = ["Export Templates", ".tpz", "Godot"],
            Works = AppActions.InstallGodotTemplates,
        });
    }

    /// <summary>The Godot project Publish exports: the project chosen where it builds a game, else, nothing chosen and no
    /// other project to publish, the Godot project at hand; null where Publish is a .NET project's.</summary>
    private string? GodotExportOf(ActionContext context)
    {
        if (Folder is null)
        {
            return null;
        }
        if (PublishTarget(context).Project is { } project)
        {
            return Projects.FirstOrDefault(p => PathNames.Same(p.Path, project))?.GodotFolder;
        }
        if (RowOf(context) is { } row && GodotProjectOfRow(row) is { } game)
        {
            return game;
        }
        IReadOnlyList<string> chosen = ChosenProjects(context) ?? SelectedProjects();
        return chosen.Count switch
        {
            1 => Projects.FirstOrDefault(p => PathNames.Same(p.Path, chosen[0]))?.GodotFolder,
            0 => GodotProjectOf(context),
            _ => null,
        };
    }

    /// <summary>The Godot project an agent's export is of: the folder's, as Publish finds it with nothing chosen.</summary>
    internal string? GodotExportFolder() => GodotFolder();

    /// <summary>The Export dialog over a Godot project, a preset chosen (its first for none); Export runs the export it
    /// closes with.</summary>
    public GodotExportDialog ShowGodotExport(string folder, string? preset)
    {
        UiThread.Check();
        GodotExportDialog? asking = GodotExportDialog;
        asking?.Close();
        string name = GodotProject.Read(folder)?.Name ?? Path.GetFileName(folder);
        GodotExportDialog dialog = new(folder, name, GodotExports.ReadIn(folder), preset, () => ExportGodotAsync(folder),
            presets => InstallGodotTemplatesAsync(folder, presets), Browse);
        GodotExportDialog = dialog;
        LastPublish = null;
        dialog.Closed += (_, _) =>
        {
            if (GodotExportDialog == dialog)
            {
                GodotExportDialog = null;
            }
        };
        BuildRunController exporting = buildRun;
        IdeErrors.Observe(ExportAfterAsync());
        return dialog;

        async Task ExportAfterAsync()
        {
            if (await dialog.ShowDialog<GodotExportRequest?>(this) is { } request && !session.IsEnded)
            {
                await (LastPublish = exporting.ExportGodotAsync(request));
                Editors.GodotPages?.Refresh();
            }
        }
    }

    /// <summary>The Godot found as a project's export takes it: its templates' version and folder in the window's Godot user
    /// folders, or why none runs the project.</summary>
    private async Task<GodotExportGodot> ExportGodotAsync(string folder)
    {
        string? setting = settings.GodotExecutable;
        bool scripts = !Projects.Any(p => p.GodotFolder is { } game && PathNames.Same(game, folder));
        IReadOnlyDictionary<string, string>? folders = GodotFolders;
        return await BackgroundWork.Run(async () =>
        {
            if (GodotLaunch.Found(setting) is not { } found)
            {
                return new GodotExportGodot(null, null, $"It cannot be exported: {GodotLaunch.NotFound}");
            }
            if ((scripts ? found.WhyNotScripts : found.WhyNot) is { } why)
            {
                return new GodotExportGodot(null, null, $"It cannot be exported: {why}");
            }
            if (await GodotExecutables.AnswerAsync(found) is not { } answer || GodotExports.TemplatesVersion(answer) is not { } version)
            {
                return new GodotExportGodot(null, null, $"The Godot at {found.Executable} did not say its version, which names its export templates");
            }
            string? templates = GodotExports.TemplatesFolder(found.Executable, version, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(),
                name => GodotUserFolders.Variable(folders, name), File.Exists);
            return new GodotExportGodot(version, templates, templates is null ? "No folder for Godot's export templates is known: HOME (APPDATA on Windows) is not set" : null);
        });
    }

    /// <summary>The export templates a project's presets take (all of the archive's for a project of none) installed from a
    /// .tpz the person picks, into the Godot found's templates folder of its version, once they agree to what it writes
    /// where; true once installed.</summary>
    private async Task<bool> InstallGodotTemplatesAsync(string folder, IReadOnlyList<GodotPreset> presets)
    {
        UiThread.Check();
        IReadOnlyList<string> picked = await PickFiles("Godot Export Templates", [new FilePickerFileType("Godot export templates") { Patterns = [$"*{GodotExports.Archive}"] }], false);
        if (picked is not [{ } archive])
        {
            return false;
        }
        if (this.work.Begin(AppActions.InstallGodotTemplates, $"Reading {Path.GetFileName(archive)}…") is not { } work)
        {
            status.Text = ActionWork.Busy(this.work.Of(AppActions.InstallGodotTemplates)!);
            return false;
        }
        using (work)
        {
            GodotExportGodot godot = await ExportGodotAsync(folder);
            if (godot is not { Problem: null, Version: { } version, Templates: { } templates })
            {
                return Failed($"The templates cannot be installed: {godot.Problem}");
            }
            IReadOnlySet<string>? needed = GodotExports.NeededBy(presets);
            (string? holds, IReadOnlyList<(string Name, long Size)> chosen, string? unread) = await BackgroundWork.Run(() => Listed(archive, needed));
            if (unread is not null)
            {
                return Failed($"{Path.GetFileName(archive)} could not be read: {unread}");
            }
            if (holds != version)
            {
                return Failed(holds is null ? $"{Path.GetFileName(archive)} is not an archive of Godot's export templates"
                    : $"{Path.GetFileName(archive)} holds the templates of Godot {holds}, and the Godot found is {version}");
            }
            string[] names = [.. chosen.Select(c => c.Name).Where(n => n != GodotExports.VersionFile)];
            work.Say("Asking to install the templates");
            if (!await ConfirmAsync("Install Export Templates?",
                $"Passenger installs {Plural.Listed(names)} ({GodotExports.Megabytes(chosen.Sum(c => c.Size))}) from {Path.GetFileName(archive)} into {PathNames.Shown(templates, null)}, "
                + $"where Godot {version} finds its export templates{(needed is null ? string.Empty : ": those the project's presets take")}.",
                "Install", null))
            {
                return Failed("The export templates were left as they were");
            }
            string? failed;
            bool ended = false;
            try
            {
                // Each stage said on the window's thread, none once the install has ended.
                failed = await BackgroundWork.Run(() => GodotExports.Install(archive, templates, version, needed, stage => UiThread.Post(() =>
                {
                    if (!ended)
                    {
                        work.Say(stage);
                    }
                }), work.Stopping));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
            {
                failed = error is OperationCanceledException ? "stopped" : error.Message;
            }
            ended = true;
            if (failed is not null)
            {
                return Failed($"The export templates were not installed: {failed}");
            }
            Editors.GodotPages?.Refresh();
            Told(work, true, $"Installed {Plural.Of(names.Length, "export template")} of Godot {version} into {PathNames.Shown(templates, null)}");
            return true;
        }

        bool Failed(string said)
        {
            Told(work!, false, said);
            return false;
        }

        static (string?, IReadOnlyList<(string, long)>, string?) Listed(string archive, IReadOnlySet<string>? needed)
        {
            try
            {
                using System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(archive);
                return (GodotExports.ArchiveVersion(zip), GodotExports.Chosen(zip, needed), null);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return (null, [], error.Message);
            }
        }
    }
}
