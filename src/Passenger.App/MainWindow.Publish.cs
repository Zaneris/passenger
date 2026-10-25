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
/// published to a folder with a publish profile of its own, the SDK's .pubxml, which the Publish dialog makes and changes
/// as one step of global undo, asking before it changes a file there; the publish itself is the session's
/// <see cref="BuildRunController"/>'s, in the Build window. Publish Again publishes a project with the profile it last
/// published with, kept with the folder.</summary>
public partial class MainWindow
{
    /// <summary>The Publish dialog open over the window, or null.</summary>
    public PublishDialog? PublishDialog { get; private set; }

    /// <summary>The publish the Publish dialog or Publish Again started last, with how it ended once it has; null from the
    /// dialog's opening until one starts.</summary>
    public Task<BuildOutcome>? LastPublish { get; private set; }

    private void AddPublishing()
    {
        actions!.Add(new IdeAction(A.Publish, "Publish…", "Build", c => ShowPublish(PublishTarget(c).Project!))
        {
            WhyNot = c => PublishTarget(c).Why,
            Menus = [new MenuPlace("Build", 2, 0)],
            Words = ["dotnet publish", "Publish Profile", "Deploy"],
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
        if (PublishTarget(context).Project is { } chosen && memory.ProfileOf(chosen) is { } profile)
        {
            return (chosen, profile, null);
        }
        if (memory.Last is { } last && memory.ProfileOf(last) is { } lastProfile && Projects.Any(p => PathNames.Same(p.Path, last)))
        {
            return (last, lastProfile, null);
        }
        return (null, null, $"Nothing has been published here yet: {actions![A.Publish]!.Title} first");
    }

    private async Task PublishAgainAsync(ActionContext context)
    {
        if (AgainTarget(context) is { Project: { } project, Profile: { } profile })
        {
            await (LastPublish = buildRun.PublishAsync(new PublishRequest(project, profile)));
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
            Busy = () => IsBuilding ? "Another build is running: publish once it has ended" : null,
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

    /// <summary>Writes a profile as the Publish dialog asks, as one step of global undo: a new one made, one there changed in
    /// place (what else it says kept, its encoding and line breaks too) once the person agrees, nothing written when nothing
    /// changed. Null once written, else why not.</summary>
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
        try
        {
            using CommandHistory.Recording made = Editors.Commands.Record($"Save Publish Profile {choice.Profile}");
            try
            {
                if (had is null)
                {
                    WriteNew(made, file, text);
                }
                else
                {
                    made.Watch(file);
                    FileText.Save(file, text, had.Encoding, had.HasBom, had.CrBreaks);
                }
            }
            finally
            {
                // Open, it takes the text now, inside the command, so the command's undo takes it back too.
                FollowWritten([file]);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{shownName} could not be written: {error.Message}";
        }
        if (had is null)
        {
            FilesChanged();
        }
        status.Text = $"Saved the publish profile {choice.Profile} of {Path.GetFileNameWithoutExtension(choice.Project)}";
        return null;
    }

    /// <summary>Asks as <see cref="Confirm"/> does, over the dialog that asks.</summary>
    private Task<bool> AskOverAsync(Window owner, string title, string text, string yes) =>
        confirm is { } answer ? answer(title, text, yes) : new ConfirmDialog(title, text, yes).ShowDialog<bool>(owner);
}
