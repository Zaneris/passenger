using Avalonia.Controls;
using Avalonia.Interactivity;
using Passenger.App.Run;
using Passenger.App.Run.Devices;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Where the chosen configuration's app runs: for a project whose frameworks run on devices, emulators or
/// simulators, the widget beside the run configuration's names the target chosen, and its action lists what the
/// project's platforms run on as their workloads list it (Android's devices and emulators, iOS's simulators on a Mac,
/// those not running too, which Run and Debug start), with this machine for a platform whose apps run here (Windows')
/// and for a desktop head (a MAUI app's Avalonia head, Linux's too). The choice is the project's, kept with the folder,
/// and decides the framework built and the launch profile tied to its platform; one no longer listed is said.</summary>
public partial class MainWindow
{
    /// <summary>Why the last listing of the project's targets found none, where it could not list them.</summary>
    private string? targetsUnlisted;

    private void AddRunTargets()
    {
        actions!.Add(new IdeAction(A.ChooseRunTarget, ActionTitles.ChooseRunTarget, ActionGroups.Run, _ => IdeErrors.Observe(ChooseRunTargetAsync()))
        {
            // Listing a project's targets runs its workload's targets, which a folder not trusted never runs.
            WhyNot = _ => Untrusted() ?? (TargetProject() is null ? "The run configuration chosen runs no app on a device, an emulator or a simulator" : null),
        });
    }

    /// <summary>The session's choices and listings shown in the widget as they change.</summary>
    private void BindRunTargets(ProjectSession next)
    {
        next.Targets.Changed += (_, _) =>
        {
            if (session == next)
            {
                ShowRunTarget();
            }
        };
        next.ProjectsEvaluated += (_, _) =>
        {
            if (session == next)
            {
                ShowRunTarget();
            }
        };
        ShowRunTarget();
    }

    /// <summary>The chosen configuration's project where its app runs on targets; null for any other.</summary>
    private ProjectModel? TargetProject() => TargetProject(session.Configurations.Chosen);

    /// <summary>A configuration's project where its app runs on targets; null for any other.</summary>
    private ProjectModel? TargetProject(RunConfiguration? configuration) =>
        configuration is { Program.Length: 0, IsContainer: false, IsMultiple: false } chosen
        && session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, chosen.Project)) is { } project
        && DeviceTargets.PlatformsOf(project.TargetFrameworks).Count > 0 ? project : null;

    /// <summary>The widget shown for a project of targets, naming the one chosen, its tip saying why where none is or it has
    /// gone; hidden for any other.</summary>
    private void ShowRunTarget()
    {
        ProjectModel? project = TargetProject();
        RunTargetButton.IsVisible = project is not null;
        if (project is null)
        {
            return;
        }
        KeptTarget? chosen = session.Targets.InForce(project);
        DeviceTarget? found = session.Targets.Found(project);
        string? gone = session.Targets.WasListed(project) ? DeviceTargets.Gone(chosen, session.Targets.Listed(project), project.Name) : null;
        RunTargetName.Text = found?.Shown ?? chosen?.Name ?? "Choose a device";
        string? tied = chosen is not null && session.Configurations.Chosen is { LaunchProfile.Length: 0 }
            ? DeviceTargets.ProfileFor(Run.LaunchProfile.Read(project), chosen.Platform)?.Name : null;
        ToolTip.SetTip(RunTargetButton, gone ?? (chosen is null ? Refusals.NoRunTarget(project.Name)
            : $"{project.Name} runs on {chosen.Name}{(tied is null ? string.Empty : $" with its launch profile {tied}")}"));
    }

    private void OnRunTargetClicked(object? sender, RoutedEventArgs e) => Request(A.ChooseRunTarget);

    /// <summary>The project's targets under the widget, the one chosen checked: listed first where they were not yet,
    /// then listed again behind the menu, which shows what came once it closes and opens again.</summary>
    private async Task ChooseRunTargetAsync()
    {
        UiThread.Check();
        if (TargetProject() is not { } project)
        {
            return;
        }
        if (!session.Targets.WasListed(project))
        {
            using Working? work = actions!.Work.Begin(A.ChooseRunTarget, $"Looking for where {project.Name} runs…");
            await ListRunTargetsAsync(project);
        }
        else
        {
            IdeErrors.Observe(ListRunTargetsAsync(project));
        }
        if (StillTargeted(project) is { } now)
        {
            ShowRunTargets(now);
        }
    }

    /// <summary>The chosen configuration's project of targets, where it is still <paramref name="project"/>'s, as evaluated
    /// now: a listing restores the project, after which it is evaluated again.</summary>
    private ProjectModel? StillTargeted(ProjectModel project) => TargetProject() is { } now && PathNames.Same(now.Path, project.Path) ? now : null;

    /// <summary>The project's targets listed again by each platform's workload (Android's, and on a Mac iOS's simulators),
    /// off the window's thread.</summary>
    private async Task ListRunTargetsAsync(ProjectModel project)
    {
        ProjectSession listing = session;
        List<DeviceTarget> found = DeviceTargets.OfThisMachine is { } here && DeviceTargets.FrameworkFor(project.TargetFrameworks, here) is not null
            ? [DeviceTargets.Here(here)] : [];
        if (DeviceTargets.DesktopHead(project) is not null)
        {
            found.Add(DeviceTargets.Here(TargetPlatform.Desktop));
        }
        List<string> problems = [];
        Dictionary<string, string> globals = ProjectModels.Properties(listing.Workspace, project);
        if (DeviceTargets.FrameworkFor(project.TargetFrameworks, TargetPlatform.Android) is { } android)
        {
            (IReadOnlyList<DeviceTarget> targets, AndroidTools? tools, string? unlisted) =
                await BackgroundWork.Run(() => AndroidDevices.ListAsync(project.Path, android, globals, CancellationToken.None));
            found.AddRange(targets);
            if ((tools is null ? unlisted : tools.Lacks is { } lacks ? Refusals.NoAndroidPlatform(lacks)
                : targets.Count == 0 ? Refusals.NoAndroidTarget(tools.AvdManager) : null) is { } problem)
            {
                problems.Add(problem.Shown);
            }
        }
        // iOS's simulators run only on a Mac: elsewhere an iOS head is passed over unsaid, as its build is.
        if (OperatingSystem.IsMacOS() && DeviceTargets.FrameworkFor(project.TargetFrameworks, TargetPlatform.Ios) is { } ios)
        {
            (IReadOnlyList<DeviceTarget> targets, AppleTools? tools, string? unlisted) =
                await BackgroundWork.Run(() => AppleSimulators.ListAsync(project.Path, ios, globals, CancellationToken.None));
            found.AddRange(targets);
            if (tools is null && unlisted is not null)
            {
                problems.Add(unlisted);
            }
        }
        if (session == listing && !listing.IsEnded)
        {
            targetsUnlisted = problems.Count > 0 ? string.Join("; ", problems) : null;
            listing.Targets.List(project, found);
        }
    }

    /// <summary>The menu of the project's targets as last listed, with Refresh below them.</summary>
    private void ShowRunTargets(ProjectModel project)
    {
        ContextMenu menu = new();
        KeptTarget? chosen = session.Targets.InForce(project);
        foreach (DeviceTarget target in session.Targets.Listed(project))
        {
            menu.Items.Add(MenuChoice.Of(target.Shown, chosen?.Platform == target.Platform && chosen.Key == target.Key, () => ChooseRunTarget(project, target)));
        }
        if (targetsUnlisted is { } unlisted)
        {
            // What a platform lacks (a device, a package its build needs) is said above the targets, which are listed all the same.
            menu.Items.Insert(0, ToolWindows.ListMenus.Notice(unlisted));
        }
        else if (menu.Items.Count == 0)
        {
            menu.Items.Add(ToolWindows.ListMenus.Notice(Refusals.NoTargets));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(ToolWindows.ListMenus.Item("Look Again", () => IdeErrors.Observe(LookAgainAsync())));
        // Gone from a narrow toolbar, the widget leaves its menu to the run configuration's beside it.
        Open(menu, ShrinkingRow.HasGone(RunTargetButton) ? RunConfigButton : RunTargetButton);

        async Task LookAgainAsync()
        {
            using Working? work = actions!.Work.Begin(A.ChooseRunTarget, $"Looking for where {project.Name} runs…");
            await ListRunTargetsAsync(project);
            if (StillTargeted(project) is { } now)
            {
                ShowRunTargets(now);
            }
        }
    }

    /// <summary>A target chosen for a project: kept with the folder, and its platform's framework chosen for the project.</summary>
    public void ChooseRunTarget(ProjectModel project, DeviceTarget target)
    {
        UiThread.Check();
        session.Targets.Choose(project, target);
        if (DeviceTargets.FrameworkFor(project, target.Platform) is { } framework
            && TargetFrameworkChoices.Offered(project).Contains(framework, StringComparer.OrdinalIgnoreCase))
        {
            session.Frameworks.Choose(project, framework);
        }
        Say(target.Running || target.Kind is not (TargetKind.Emulator or TargetKind.Simulator)
            ? $"{project.Name} runs on {target.Name}" : $"{project.Name} runs on {target.Name}, which Run and Debug start");
    }

    /// <summary>Choose Where It Runs for an agent, which no menu reaches: the project's targets listed again, of the chosen
    /// configuration or of the one <paramref name="input"/> names before a colon (Configuration:target), whose choice stays
    /// as it was, and the target it names (by its name, serial or AVD) chosen as the widget chooses it; with none named,
    /// what there is to choose. Why not, thrown.</summary>
    internal async Task<string> ChooseRunTargetForAgentAsync(string? input)
    {
        UiThread.Check();
        if (Untrusted() is { } untrusted)
        {
            throw new InvalidOperationException(untrusted.English);
        }
        RunConfiguration? named = session.Configurations.All.Where(c => input?.StartsWith(c.Name + ":", StringComparison.OrdinalIgnoreCase) == true)
            .MaxBy(c => c.Name.Length);
        if (named is not null)
        {
            input = input![(named.Name.Length + 1)..].Trim();
        }
        if (TargetProject(named ?? session.Configurations.Chosen) is not { } project)
        {
            throw new InvalidOperationException(named is null ? "The run configuration chosen runs no app on a device, an emulator or a simulator"
                : $"{named.Name} runs no app on a device, an emulator or a simulator");
        }
        await ListRunTargetsAsync(project);
        IReadOnlyList<DeviceTarget> listed = session.Targets.Listed(project);
        // What a platform lacks is said after what is listed, as the menu says it with them.
        string offered = (listed.Count == 0 ? targetsUnlisted ?? Refusals.NoTargets
            : Plural.Listed([.. listed.Select(t => t.Shown)]) + (targetsUnlisted is { } lacking ? $"; {lacking}" : string.Empty)).Shown;
        if (input is not { Length: > 0 } wanted)
        {
            return $"{project.Name} runs on {session.Targets.InForce(project)?.Name ?? "nothing chosen yet"}; it can run on {offered}";
        }
        DeviceTarget target = listed.FirstOrDefault(t => t.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) || t.Id.Equals(wanted, StringComparison.Ordinal)
            || t.Avd is { } avd && avd.Equals(wanted, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"{project.Name} cannot run on {wanted}: it can run on {offered}");
        ChooseRunTarget(project, target);
        return $"{project.Name} runs on {target.Name}{(targetsUnlisted is { } unlisted ? $"; {unlisted}" : string.Empty)}";
    }
}
