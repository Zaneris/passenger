using System.Xml;
using Avalonia.Controls;
using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Host;
using Passenger.Text;
using Passenger.Workspace;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Publish… and Publish Again: the chosen project (the Explorer's, the file shown's, or the solution's one app)
/// published to a folder or as its container image with a publish profile of its own, the SDK's .pubxml, which the Publish
/// dialog makes and changes as one step of global undo, asking before it changes a file there; an AppHost through its own
/// publishers, their files landed in its folder as one more step of it; the publish itself is the session's
/// <see cref="BuildRunController"/>'s, in the Build window. Publish Again publishes a project with the profile (an AppHost
/// to the folder) it last published with, kept with the folder.</summary>
public partial class MainWindow
{
    /// <summary>The Publish dialog open over the window, or null.</summary>
    public PublishDialog? PublishDialog { get; private set; }

    /// <summary>The Publish dialog of an AppHost open over the window, or null.</summary>
    public AppHostPublishDialog? AppHostPublishDialog { get; private set; }

    /// <summary>The publish the Publish dialog or Publish Again started last, with how it ended once it has; null from the
    /// dialog's opening until one starts.</summary>
    public Task<BuildOutcome>? LastPublish { get; private set; }

    private void AddPublishing()
    {
        // A Godot project's game is exported through its presets, any other project published through its profiles.
        actions!.Add(new IdeAction(A.Publish, "Publish…", "Build", c =>
        {
            if (GodotExportOf(c) is { } game)
            {
                ShowGodotExport(game, null);
            }
            else
            {
                string project = PublishTarget(c).Project!;
                if (Projects.FirstOrDefault(p => PathNames.Same(p.Path, project)) is { IsAspireHost: true })
                {
                    ShowAppHostPublish(project);
                }
                else
                {
                    ShowPublish(project);
                }
            }
        })
        {
            WhyNot = c => GodotExportOf(c) is not null ? null : PublishTarget(c).Why,
            Menus = [new MenuPlace("Build", 2, 0)],
            Words = ["dotnet publish", "Publish Profile", "Deploy", "Export", "Godot Export Presets", "Container Image", MSBuildNames.PublishContainer, "aspire publish", "Docker Compose"],
            Works = A.Publish,
        });
        actions.Add(new IdeAction(A.PublishAgain, "Publish Again", "Build", c => IdeErrors.Observe(PublishAgainAsync(c)))
        {
            WhyNot = c => AgainTarget(c).Why,
            Menus = [new MenuPlace("Build", 2, 1)],
            Words = ["Republish", "dotnet publish"],
            Works = A.Publish,
        });
    }

    /// <summary>The project Publish publishes: the one chosen in the Explorer or holding the file shown, else the solution's
    /// one project that publishes; or why there is none, or it does not publish.</summary>
    private (string? Project, string? Why) PublishTarget(ActionContext context)
    {
        if (Folder is null)
        {
            return (null, Refusals.NoFolder);
        }
        IReadOnlyList<string> chosen = ChosenProjects(context) ?? SelectedProjects();
        if (chosen.Count > 1)
        {
            return (null, "Choose one project to publish");
        }
        if (chosen.Count == 0)
        {
            List<ProjectModel> publishable = [.. Projects.Where(p => PublishProfiles.WhyNot(p) is null)];
            return publishable.Count == 1 ? (publishable[0].Path, null)
                : (null, publishable.Count == 0 ? "No project here publishes" : "No project is chosen in the Explorer or open in the editor");
        }
        return Projects.FirstOrDefault(p => PathNames.Same(p.Path, chosen[0])) is not { } project ? (null, $"{Path.GetFileNameWithoutExtension(chosen[0])} is not one of the folder's projects")
            : PublishProfiles.WhyNot(project) is { } why ? (null, why) : (project.Path, null);
    }

    /// <summary>What Publish Again publishes: the project Publish would, with the profile it last published with, else the
    /// project published last; or why there is nothing to publish again.</summary>
    private (string? Project, string? Profile, string? Why) AgainTarget(ActionContext context)
    {
        if (Folder is null)
        {
            return (null, null, Refusals.NoFolder);
        }
        PublishMemory memory = session.Publishing;
        if (PublishTarget(context).Project is { } chosen && (memory.ProfileOf(chosen) is not null || memory.OutputOf(chosen) is not null))
        {
            return (chosen, memory.ProfileOf(chosen), null);
        }
        if (memory.Last is { } last && (memory.ProfileOf(last) is not null || memory.OutputOf(last) is not null) && Projects.Any(p => PathNames.Same(p.Path, last)))
        {
            return (last, memory.ProfileOf(last), null);
        }
        return (null, null, $"Nothing has been published here yet: {actions![A.Publish]!.Title} first");
    }

    private async Task PublishAgainAsync(ActionContext context)
    {
        if (AgainTarget(context) is { Project: { } project, Why: null } again)
        {
            await (LastPublish = buildRun.PublishAsync(new PublishRequest(project, again.Profile)));
        }
    }

    /// <summary>The Publish dialog over a project: its profiles, the one it last published with chosen; Publish saves the
    /// profile and publishes with it. Null, saying why, for a project that does not publish.</summary>
    public PublishDialog? ShowPublish(string project)
    {
        if (Folder is not { } open || Projects.FirstOrDefault(p => PathNames.Same(p.Path, project)) is not { } model)
        {
            status.Text = $"{Path.GetFileNameWithoutExtension(project)} is not one of the folder's projects";
            return null;
        }
        if (PublishProfiles.WhyNot(model) is { } why)
        {
            status.Text = why;
            return null;
        }
        WorkspaceModel? workspace = session.Workspace;
        PublishDialog? shown = null;
        PublishDialog dialog = shown = new PublishDialog(model, open, session.Publishing.ProfileOf(model.Path),
            (profile, configuration, framework, runtime, cancel) =>
                PublishProfiles.EvaluateAsync(open, model.Path, PublishProfiles.Globals(workspace, model, profile, configuration, framework, runtime), cancel),
            choice => SaveProfileAsync(choice, shown!))
        {
            Busy = () => IsBuilding ? $"{Refusals.Building}: publish once it has ended" : null,
        };
        PublishDialog = dialog;
        LastPublish = null;
        dialog.Closed += (_, _) =>
        {
            if (PublishDialog == dialog)
            {
                PublishDialog = null;
            }
        };
        BuildRunController publishing = buildRun;
        IdeErrors.Observe(PublishAfterAsync());
        return dialog;

        async Task PublishAfterAsync()
        {
            if (await dialog.ShowDialog<PublishRequest?>(this) is { } request && !session.IsEnded)
            {
                await (LastPublish = publishing.PublishAsync(request));
            }
        }
    }

    /// <summary>The Publish dialog over an AppHost: the folder its publishers' files land in, the one it published to last
    /// chosen; Publish publishes it there.</summary>
    public AppHostPublishDialog? ShowAppHostPublish(string project)
    {
        if (Folder is not { } open)
        {
            status.Text = Refusals.NoFolder;
            return null;
        }
        AppHostPublishDialog dialog = new(project, open, session.Publishing.OutputOf(project))
        {
            Busy = () => IsBuilding ? $"{Refusals.Building}: publish once it has ended" : null,
        };
        AppHostPublishDialog = dialog;
        LastPublish = null;
        dialog.Closed += (_, _) =>
        {
            if (AppHostPublishDialog == dialog)
            {
                AppHostPublishDialog = null;
            }
        };
        BuildRunController publishing = buildRun;
        IdeErrors.Observe(PublishAfterAsync());
        return dialog;

        async Task PublishAfterAsync()
        {
            if (await dialog.ShowDialog<PublishRequest?>(this) is { } request && !session.IsEnded)
            {
                await (LastPublish = publishing.PublishAsync(request));
            }
        }
    }

    /// <summary>Lands the files a folder holds in another (an AppHost's publishers' output, in the order of their paths):
    /// those in the open folder written as the window writes its own, one command of global undo named by the title, a file
    /// there already written over; any other folder's with <see cref="AtomicFile.Write"/>. The files written, or why not.</summary>
    private async Task<(IReadOnlyList<string> Written, string? Problem)> LandFilesAsync(string from, string to, string title)
    {
        List<(string From, string To)> files;
        try
        {
            files = [.. Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => (file, Path.Combine(to, Path.GetRelativePath(from, file))))];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ([], error.Message);
        }
        List<string> written = [.. files.Select(file => file.To)];
        if (files.Count == 0)
        {
            return ([], null);
        }
        try
        {
            if (Folder is not { } open || !PathNames.IsUnder(to, open) && !PathNames.Same(to, open))
            {
                foreach ((string source, string target) in files)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    AtomicFile.Write(target, await File.ReadAllBytesAsync(source));
                }
                return (written, null);
            }
            List<(string Target, string Text)> texts = [];
            foreach ((string source, string target) in files)
            {
                texts.Add((target, await File.ReadAllTextAsync(source)));
            }
            string? refused = await ChangeFileAsync(title, written, () =>
            {
                foreach ((string target, string text) in texts)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    FileText? had = File.Exists(target) ? FileText.Load(target) : null;
                    _ = had is null ? FileText.SaveNew(target, text) : FileText.SaveOver(target, had, text);
                }
                return null;
            });
            return refused is null ? (written, null) : ([], refused);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ([], error.Message);
        }
    }

    /// <summary>Writes a profile as the Publish dialog asks, as one step of global undo: a new one made, one there changed in
    /// place (what else it says kept, its encoding and line breaks too) once the person agrees, nothing written when nothing
    /// changed. Null once written and landed, else why not.</summary>
    private async Task<string?> SaveProfileAsync(PublishChoice choice, Window owner)
    {
        string file = choice.File;
        string shownName = Path.GetFileName(file);
        if (SaveBeforeWriting([file]) is { } unsaved)
        {
            return $"{shownName} could not be saved: {unsaved}";
        }
        FileText? had = File.Exists(file) ? FileText.Load(file) : null;
        if (had is not null && !choice.Changed && !choice.Replacing)
        {
            return null;
        }
        string text;
        try
        {
            bool fresh = choice.Replacing || had is null;
            text = PubXml.Apply(fresh ? PubXml.Empty : had!.Text,
                PublishProfiles.Properties(choice.Options, choice.Before, fresh ? new Dictionary<string, string>() : choice.Raw, choice.MultiTargeted, fresh));
        }
        catch (XmlException error)
        {
            return $"{shownName} does not read: {error.Message}";
        }
        if (had is not null && had.Text == text)
        {
            return null;
        }
        if (had is not null && !await AskOverAsync(owner, "Publish Profile",
            choice.Replacing ? $"{shownName} is there already. Replace it with the new profile {choice.Profile}?"
                : $"Save the changes to {shownName}? What else it says stays as it is.",
            choice.Replacing ? "Replace" : "Save"))
        {
            return $"{shownName} was left as it was";
        }
        // What was asked about is what is written over: a change made while the question was open is never lost.
        if (had is not null && SaveBeforeWriting([file]) is { } unsavedSince)
        {
            return $"{shownName} could not be saved: {unsavedSince}";
        }
        if (had is not null && (File.Exists(file) ? FileText.Load(file)?.Text : null) != had.Text)
        {
            return $"{shownName} changed while the question was open, so it was left as it is now: save again to change it";
        }
        if (await ChangeFileAsync($"Save Publish Profile {choice.Profile}", [file], () =>
        {
            _ = had is null ? FileText.SaveNew(file, text) : FileText.SaveOver(file, had, text);
            return null;
        }) is { } refused)
        {
            return $"{shownName} could not be written: {refused}";
        }
        status.Text = $"Saved the publish profile {choice.Profile} of {Path.GetFileNameWithoutExtension(choice.Project)}";
        return null;
    }

    /// <summary>Asks as <see cref="Confirm"/> does, over the dialog that asks.</summary>
    private Task<bool> AskOverAsync(Window owner, string title, string text, string yes) =>
        confirm is { } answer ? answer(title, text, yes) : new ConfirmDialog(title, text, yes).ShowDialog<bool>(owner);
}
