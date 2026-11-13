using Passenger.App.State;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run.Devices;

/// <summary>Where each project of the folder runs, for a project whose frameworks run on devices, emulators or simulators
/// (<see cref="DeviceTargets.PlatformsOf"/>): one target per project, kept with the folder
/// (<see cref="WorkspaceState.RunTargets"/>), and each project's targets as last listed. A target chosen is found again by
/// its key in a later listing; one no longer listed is said (<see cref="DeviceTargets.Gone"/>). A UI-thread object.</summary>
public sealed class DeviceChoices
{
    private readonly Dictionary<string, KeptTarget> chosen = new(PathNames.Comparer);
    private readonly Dictionary<string, IReadOnlyList<DeviceTarget>> listed = new(PathNames.Comparer);
    private StateDocument<WorkspaceState>? kept;

    /// <summary>A project's choice or listing changed, by its path.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>The target chosen for a project, as kept; null where none was chosen.</summary>
    public KeptTarget? Of(ProjectModel project) => chosen.GetValueOrDefault(project.Path);

    /// <summary>The project's targets as last listed; none before its first listing.</summary>
    public IReadOnlyList<DeviceTarget> Listed(ProjectModel project) => listed.GetValueOrDefault(project.Path) ?? [];

    /// <summary>Whether the project's targets have been listed since the window opened.</summary>
    public bool WasListed(ProjectModel project) => listed.ContainsKey(project.Path);

    /// <summary>The chosen target in the project's last listing; null where none is chosen, or it is not listed now.</summary>
    public DeviceTarget? Found(ProjectModel project) => DeviceTargets.Find(Of(project), Listed(project));

    /// <summary>A project's targets as just listed, which a choice is found in again.</summary>
    public void List(ProjectModel project, IReadOnlyList<DeviceTarget> targets)
    {
        UiThread.Check();
        listed[project.Path] = targets;
        Changed?.Invoke(this, project.Path);
    }

    /// <summary>The choices as the folder's state keeps them, which a whole rewrite of that state carries on.</summary>
    public Dictionary<string, KeptTarget> Kept => new(chosen, StringComparer.Ordinal);

    /// <summary>Chooses where a project runs, kept with the folder.</summary>
    public void Choose(ProjectModel project, DeviceTarget target)
    {
        UiThread.Check();
        KeptTarget choice = KeptTarget.Of(target);
        if (chosen.GetValueOrDefault(project.Path) == choice)
        {
            return;
        }
        chosen[project.Path] = choice;
        if (kept is { } document)
        {
            document.Set(document.Value with { RunTargets = Kept });
        }
        Changed?.Invoke(this, project.Path);
    }

    /// <summary>The folder's documents from now on: the choices it kept taken first.</summary>
    public void Keep(StateDocument<WorkspaceState> document)
    {
        UiThread.Check();
        kept = document;
        foreach ((string project, KeptTarget choice) in document.Value.RunTargets)
        {
            chosen[project] = choice;
        }
    }
}
