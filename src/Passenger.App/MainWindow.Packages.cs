using Passenger.App.Packages;
using Passenger.App.Workspace;
using Passenger.Packages;

namespace Passenger.App;

/// <summary>The window's NuGet: its tool window over the session's packages (<see cref="ProjectSession.Packages"/>), the
/// solution read again after a project's packages change, and the sources again after a nuget.config does.</summary>
public partial class MainWindow
{
    private ToolWindows.NuGetView? nuget;

    /// <summary>The NuGet tool window, built as it is first shown, by a person's action, or as a test asks for it.</summary>
    public ToolWindows.NuGetView NuGet => nuget ?? (ToolWindows.NuGetView)Dock[ToolWindowIds.NuGet].Content;

    private void AddPackages()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.NuGet, "NuGet", IconKeys.Package, () =>
        {
            ToolWindows.NuGetView view = nuget = new ToolWindows.NuGetView(session.Packages);
            view.LinkClicked += (_, link) => Browse(link);
            view.ActionRequested += (_, action) => Request(action);
            // The NuGet window's update of one of EF Core's packages is weighed against the project's provider first.
            view.BeforeInstall = migrations is { } weighs ? weighs.WeighUpdateAsync : null;
            return view;
        }, Docking.ToolAnchor.BottomLeft)
        {
            Action = AppActions.ShowNuGet,
            Tools = () => nuget?.Tabs,
            Menus = [new MenuPlace("Tools", 0, 0)],
        });
        actions!.Add(new IdeAction(AppActions.NuGetRefresh, "Refresh NuGet", "NuGet", _ => IdeErrors.Observe(NuGet.RefreshAskedAsync())) { WhyNot = NeedsFolder });
    }

    /// <summary>The session's packages followed while it is the window's: a change's end said in the status bar, its files
    /// taken from disk and the solution read again before whoever asked for the change is answered, the nuget.config files
    /// the Sources tab wrote taken too, wherever they are; and the NuGet window, once built, shows them.</summary>
    private void BindPackages(ProjectSession next)
    {
        next.Packages.Changed += (_, outcome) =>
        {
            if (session != next)
            {
                return;
            }
            SayChanged(outcome);
            if (outcome.Refused is null)
            {
                // dotnet add package wrote the project, its lock file, and Directory.Packages.props under central management.
                FollowWritten(outcome.Written);
                ReloadSolution();
            }
        };
        next.Packages.ConfigsWritten += (_, configs) =>
        {
            if (session == next)
            {
                FollowWritten(configs);
            }
        };
        nuget?.Bind(next.Packages);
    }

    /// <summary>How a package change ended, in the status bar in place of its stage: one that failed with what became of
    /// its files, a click opening the NuGet window.</summary>
    private void SayChanged(PackageOutcome outcome)
    {
        if (outcome.Done)
        {
            Say($"{outcome.Name}: done");
        }
        else if (outcome.Refused is { } why)
        {
            Say($"{outcome.Name}: {why}");
        }
        else
        {
            Say($"{outcome.Name} failed{(outcome.Left is { } left ? $", {left}" : string.Empty)}; the NuGet window's log says why",
                StatusPlace.Window(Dock, ToolWindowIds.NuGet));
        }
    }

    /// <summary>A nuget.config written, made or deleted on disk: the folder's sources read again, once they have been, and
    /// listed again in the NuGet window when it is built.</summary>
    private void PackageSourcesChanged(FolderChanges changes)
    {
        if (session.Packages.Sources != PackageSourceSet.None
            && changes.Paths.Any(p => Path.GetFileName(p).Equals("nuget.config", StringComparison.OrdinalIgnoreCase)))
        {
            IdeErrors.Observe(nuget is { } view ? view.ReloadSourcesAsync() : session.Packages.ReloadSourcesAsync());
        }
    }
}
