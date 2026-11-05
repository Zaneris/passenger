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

    /// <summary>The Publish dialog over an AppHost: the step it runs, publish or one of the deploy steps the AppHost declares
    /// (read from it as the dialog opens), and the folder its files land in, the one it published to last chosen; Publish
    /// runs the step there.</summary>
    public AppHostPublishDialog? ShowAppHostPublish(string project)
    {
        if (Folder is not { } open)
        {
            status.Text = Refusals.NoFolder;
            return null;
        }
        Containers.ContainerEngine engine = session.Containers.Engine;
        Func<CancellationToken, Task<Passenger.App.Aspire.DeployOffer>>? readSteps = Projects.FirstOrDefault(p => PathNames.Same(p.Path, project)) is { IsAspireHost: true } host
            ? cancel => ReadDeployStepsAsync(host, engine, cancel)
            : null;
        AppHostPublishDialog dialog = new(project, open, session.Publishing.OutputOf(project), readSteps)
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

    /// <summary>An AppHost's deploy steps read from it, as its build reads the files once their saves have landed; none
    /// while another build runs, whose files the AppHost's own build would take from under it.</summary>
    private async Task<Passenger.App.Aspire.DeployOffer> ReadDeployStepsAsync(ProjectModel host, Containers.ContainerEngine engine, CancellationToken cancel)
    {
        if (IsBuilding)
        {
            return new Passenger.App.Aspire.DeployOffer([], $"{Refusals.Building}: its deploy steps are read as Publish opens once it has ended");
        }
        if (await EditorArea.LandedAsync(null, cancel) is { } unlanded)
        {
            return new Passenger.App.Aspire.DeployOffer([], unlanded);
        }
        return await Passenger.App.Aspire.AppHostSteps.OfferAsync(host, engine, cancel);
    }

    /// <summary>Lands the files a folder holds in another (an AppHost's publishers' output, in the order of their paths), each
    /// byte for byte, whatever it holds: those in the open folder as one command of global undo named by the title
    /// (<see cref="LandInFolderAsync"/>), a file there already written over; any other folder's with
    /// <see cref="AtomicFile.Write"/>. The files written, or why not.</summary>
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
        if (Folder is { } open && (PathNames.IsUnder(to, open) || PathNames.Same(to, open)))
        {
            return await LandInFolderAsync(files, to, title) is { } refused ? ([], refused) : (written, null);
        }
        try
        {
            foreach ((string source, string target) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                AtomicFile.Write(target, await File.ReadAllBytesAsync(source));
            }
            return (written, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ([], error.Message);
        }
    }

    /// <summary>Files landed in the open folder as one command of global undo, written off the window's thread in their turn,
    /// those open with unsaved edits saved first: an open file's text takes what landed as a step of its own, and every other
    /// is kept whole (<see cref="LandedFiles"/>), so Undo and Redo put back each byte. Null once landed, else why not, all of
    /// it taken back.</summary>
    private async Task<string?> LandInFolderAsync(IReadOnlyList<(string From, string To)> files, string to, string title)
    {
        IReadOnlyList<string> paths = [.. files.Select(file => file.To)];
        using CommandHistory.Recording made = Editors.Commands.RecordWrites(title);
        if (await made.ClearAsync(paths) is { } held)
        {
            return held;
        }
        if (SaveBeforeWriting(paths) is { } unsaved)
        {
            return unsaved;
        }
        HashSet<string> shown = new(paths.Where(path => Editors.Find(path) is { HoldsText: true }), PathNames.Comparer);
        foreach (string path in shown)
        {
            made.Watch(path);
        }
        string state = StateFolder;
        string aside = MadeFolders.Aside(state, to);
        LandedFiles landed = new(aside);
        Dictionary<string, bool> stood = Standing(paths);
        string? refused = await Editors.Commands.InTurnAsync(title, paths, wrote =>
        {
            MadeFolders.Sweep(state);
            return landed.Land(files, shown, wrote);
        });
        FollowWritten(paths);
        refused ??= (await UnlandedAsync(paths))?.Message;
        Followed(stood, [], paths);
        if (landed.Paths.Count > 0)
        {
            made.Also(LandedPart(landed, aside, title));
        }
        if (refused is null)
        {
            return null;
        }
        string said = await made.TakenBackAsync(refused);
        MadeFolders.Forget(aside);
        return said;
    }

    /// <summary>What Undo and Redo put of files landed whole, while each is as the command left it and none open holds edits.</summary>
    private CommandHistory.Part LandedPart(LandedFiles landed, string aside, string title) =>
        new(Plural.Listed([.. landed.Paths.Select(Path.GetFileName).OfType<string>()]),
            _ => landed.Paths.Select(Editors.Find).FirstOrDefault(open => open is { IsModified: true }) is { } edited ? $"{edited.Name} has edits not saved" : null,
            redo => PutLandedAsync(landed, title, redo))
        {
            Read = redo => BackgroundWork.Run(() => landed.Unlike(redo)),
            Dropped = () => MadeFolders.Forget(aside),
            First = true,
        };

    /// <summary>Files landed whole put as they were before their command (or after it, given true), off the window's thread
    /// in their turn, then followed: what came or went, a tab taking what it now holds, or closing with a file gone. Null
    /// once put, else why not, none of them changed.</summary>
    private async Task<string?> PutLandedAsync(LandedFiles landed, string title, bool redo)
    {
        IReadOnlyList<string> paths = landed.Paths;
        if (await Editors.ClearAsync(paths) is { } held)
        {
            return held;
        }
        Dictionary<string, bool> stood = Standing(paths);
        string? refused = await Editors.Commands.InTurnAsync($"{(redo ? "Redo" : "Undo")} {title}", paths, wrote => landed.Put(redo, wrote));
        refused ??= (await UnlandedAsync(paths))?.Message;
        FollowWritten(paths);
        foreach (Passenger.App.Workspace.OpenDocument gone in paths.Where(path => !File.Exists(path)).Select(Editors.Find).OfType<Passenger.App.Workspace.OpenDocument>().ToList())
        {
            Editors.CloseDeleted(gone);
        }
        Followed(stood, [], paths);
        return refused;
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
