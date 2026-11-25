using Passenger.App.Packages;
using Passenger.App.Workspace;
using Passenger.Core;
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
            // The NuGet window's update of one of EF Core's packages moves its set, weighed against the project's provider first.
            view.BeforeInstall = migrations is { } weighs ? weighs.WeighUpdateAsync : null;
            return view;
        }, Docking.ToolAnchor.BottomLeft)
        {
            Serves = ToolWindowServes.EveryFolder,
            Action = AppActions.ShowNuGet,
            Tools = () => nuget?.Tabs,
            Menus = [new MenuPlace("Tools", 0, 0)],
        });
        actions!.Add(new IdeAction(AppActions.NuGetRefresh, "Refresh NuGet", "NuGet", _ => IdeErrors.Observe(NuGet.RefreshAskedAsync())) { WhyNot = NeedsFolder });
        actions.Add(new IdeAction(AppActions.NuGetUpdateAll, "Update All NuGet Packages", "NuGet", _ => IdeErrors.Observe(NuGet.UpdateAllAsync()))
        {
            WhyNot = c => NeedsFolder(c) ?? NuGet.UpdateAllWhyNot,
        });
        // The NuGet window's chosen package, so asked of its view only once built: no package is chosen before.
        actions.Add(new IdeAction(AppActions.NuGetUpdateEverywhere, "Update NuGet Package in All Projects", "NuGet", _ => IdeErrors.Observe(NuGet.UpdateEverywhereAsync()))
        {
            WhyNot = c => NeedsFolder(c) ?? (nuget is { } shown ? shown.UpdateEverywhereWhyNot : Refusals.NoPackageChosen),
            Words = ["Update Package Everywhere", "Update in Every Project"],
        });
        actions.Add(new IdeAction(AppActions.NuGetUpdateSelected, "Update Selected NuGet Packages", "NuGet", _ => IdeErrors.Observe(NuGet.UpdateSelectedAsync()))
        {
            WhyNot = c => NeedsFolder(c) ?? (nuget is { } shown ? shown.UpdateSelectedWhyNot : Refusals.UpdatesNotShown),
        });
    }

    /// <summary>Manage NuGet Packages for Solution: the NuGet window shown (behind, for an agent) on its Packages tab over
    /// every project, a project it was narrowed to let go.</summary>
    private void ManageSolutionPackages(ActionContext context)
    {
        if (context.ByAgent)
        {
            Dock.ShowBehind(ToolWindowIds.NuGet);
        }
        else
        {
            Dock.Show(ToolWindowIds.NuGet);
        }
        NuGet.ShowSolution();
    }

    /// <summary>The session's packages followed while it is the window's: a change's end said in the status bar, its files
    /// taken from disk and the solution read again before whoever asked for the change is answered, the nuget.config files
    /// the Sources tab wrote taken too, wherever they are; and the NuGet window, once built, shows them.</summary>
    private void BindPackages(ProjectSession next)
    {
        next.Packages.EvaluationProperties = project => Passenger.App.Workspace.ProjectModels.Properties(next.Workspace, project);
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

    /// <summary>On a project file's mark of a vulnerable version it gets only through others, the fix Show Context Actions and
    /// an agent's code_actions find: a reference of the project's own at the least version that fixes it, in the frameworks
    /// that get it (<see cref="FolderPackages.TransitiveFixAsync"/>), written as the NuGet window's Fix writes it.</summary>
    private IReadOnlyList<CSharp.EditorFix> PackageFixes(OpenDocument file, int offset)
    {
        if (session.MSBuild.For(file) is not { } document || session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, file.Path)) is not { } project)
        {
            return [];
        }
        ProjectSession at = session;
        return [.. document.ExposedAt(offset).Select(e => new CSharp.EditorFix($"Reference {e.Id}'s fixed version"
            + TransitiveFix.Within(TransitiveFix.FrameworksOf(e.Uses, [.. FolderPackages.References(project).Where(r => r.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Version)], project.TargetFrameworks)),
            $"{Plural.Listed([.. e.Uses.Select(u => u.Version).Distinct(StringComparer.OrdinalIgnoreCase)])} brought in by "
            + $"{Plural.Listed([.. e.Uses.SelectMany(u => u.BroughtBy).Distinct(StringComparer.OrdinalIgnoreCase)])} has a known vulnerability",
            new Text.TextRange(offset, 0), string.Empty)
        {
            Elsewhere = async (_, _) =>
            {
                if (at != session)
                {
                    return CSharp.RefactorOutcome.Refused(Refusals.SolutionClosed);
                }
                TransitiveFix? fix;
                try
                {
                    fix = await at.Packages.TransitiveFixAsync(project, e.Id, CancellationToken.None);
                }
                catch (Exception failed) when (FeedSet.IsFeedFailure(failed))
                {
                    return CSharp.RefactorOutcome.Refused($"The versions of {e.Id} that may fix it could not be read: {failed.Message}");
                }
                if (fix is null)
                {
                    return CSharp.RefactorOutcome.Refused($"No version of {e.Id} without a known vulnerability is offered");
                }
                PackageOutcome outcome = await at.Packages.InstallAsync(project, fix.Id, fix.Version, frameworks: fix.Frameworks);
                return outcome.Done
                    ? new CSharp.RefactorOutcome($"{outcome.Name}: done", null, [.. outcome.Changed.Select(f => new CSharp.RefactoredFile(f, CSharp.RefactoredChange.Changed, []))], [])
                    : CSharp.RefactorOutcome.Refused(outcome.Failure($"{outcome.Name} failed") ?? $"{outcome.Name} failed");
            },
        })];
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
