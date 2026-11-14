using Avalonia.Controls;
using Avalonia.Interactivity;
using Passenger.Core;
using Passenger.Workspace;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The build configuration and platform the folder builds as, chosen beside the run configuration: the widget
/// names the one chosen, and its action lists those the solution declares, or with none those its projects declare. The
/// choice is the folder's, kept with it, and every build, run, test and evaluation takes it; choosing builds nothing, but
/// says what the next build uses.</summary>
public partial class MainWindow
{
    private void AddBuildConfigurations()
    {
        actions!.Add(new IdeAction(A.ChooseBuildConfiguration, "Choose Build Configuration…", "Build", _ => ShowBuildConfigurations())
        {
            WhyNot = _ => session.Folder is null ? Refusals.NoFolderTo("build") : session.BuildChoices.Offered.Count == 0 ? Refusals.NoBuildConfigurations : null,
        });
    }

    /// <summary>The session's choice and offer shown in the widget as they change.</summary>
    private void BindBuildConfigurations(ProjectSession next)
    {
        next.BuildChoices.Listed += (_, _) =>
        {
            if (session == next)
            {
                ShowBuildConfiguration();
            }
        };
        next.BuildChosen += (_, _) =>
        {
            if (session == next)
            {
                ShowBuildConfiguration();
            }
        };
        ShowBuildConfiguration();
    }

    /// <summary>The widget naming the configuration chosen, shown once the folder offers any.</summary>
    private void ShowBuildConfiguration()
    {
        IReadOnlyList<BuildConfiguration> offered = session.BuildChoices.Offered;
        BuildConfigButton.IsVisible = session.Folder is not null && offered.Count > 0;
        if (session.BuildChoices.Chosen is not { } chosen)
        {
            return;
        }
        BuildConfigName.Text = chosen.Shown(offered);
        ToolTip.SetTip(BuildConfigButton, $"Build configuration: {Named(chosen)}");
    }

    /// <summary>A configuration as a choice and the widget's tip say it: Release (Any CPU).</summary>
    private static string Named(BuildConfiguration configuration) => $"{configuration.Configuration} ({configuration.Platform})";

    private void OnBuildConfigClicked(object? sender, RoutedEventArgs e) => Request(A.ChooseBuildConfiguration);

    /// <summary>The configurations offered under the widget, the one chosen checked.</summary>
    private void ShowBuildConfigurations()
    {
        UiThread.Check();
        IReadOnlyList<BuildConfiguration> offered = session.BuildChoices.Offered;
        BuildConfiguration? chosen = session.BuildChoices.Chosen;
        ContextMenu menu = new();
        foreach (BuildConfiguration each in offered)
        {
            menu.Items.Add(MenuChoice.Of(each.Shown(offered), chosen is not null && chosen.Is(each), () => IdeErrors.Observe(ChooseBuildConfigurationAsync(each))));
        }
        // Gone from a narrow toolbar, the widget leaves its menu to the Build button beside it.
        Open(menu, ShrinkingRow.HasGone(BuildConfigButton) ? BuildButton : BuildConfigButton);
    }

    /// <summary>A configuration chosen: kept with the folder, and said with what it changes; nothing is built.</summary>
    private async Task<string> ChooseBuildConfigurationAsync(BuildConfiguration configuration)
    {
        UiThread.Check();
        if (session.Folder is not { } folder || session.Workspace is not { } workspace)
        {
            throw new InvalidOperationException(Refusals.NoFolderTo("build"));
        }
        using Working? work = actions!.Work.Begin(A.ChooseBuildConfiguration, $"Reading what {Path.GetFileName(workspace.Solution?.Path ?? folder)} builds as {configuration.Shown(session.BuildChoices.Offered)}…");
        if (await session.BuildChoices.ChooseAsync(folder, workspace, configuration) is { } why)
        {
            work?.Tell(false, why);
            Say(why);
            throw new InvalidOperationException(why);
        }
        string said = $"Builds, runs and tests build as {Named(configuration)} from the next build on; the editor and Problems follow it as the projects are read again";
        work?.Tell(true, said);
        Say(said);
        return said;
    }

    /// <summary>Choose Build Configuration for an agent, which no menu reaches: <paramref name="input"/> names a configuration
    /// offered (Release, or Release|x64 where several platforms are), chosen as the widget chooses it; with none, what is
    /// chosen and what there is to choose. Why not, thrown.</summary>
    internal async Task<string> ChooseBuildConfigurationForAgentAsync(string? input)
    {
        UiThread.Check();
        IReadOnlyList<BuildConfiguration> offered = session.BuildChoices.Offered;
        if (offered.Count == 0)
        {
            throw new InvalidOperationException(session.Folder is null ? Refusals.NoFolderTo("build") : Refusals.NoBuildConfigurations);
        }
        string listed = Plural.Listed([.. offered.Select(o => o.Key)]);
        BuildConfiguration chosen = session.BuildChoices.Chosen!;
        if (input is not { Length: > 0 } named)
        {
            return $"The folder builds as {chosen.Key}; it can build as {listed}";
        }
        BuildConfiguration? asked = BuildConfiguration.Of(named) is { } both ? offered.FirstOrDefault(o => o.Is(both))
            : offered.FirstOrDefault(o => o.Configuration.Equals(named.Trim(), StringComparison.OrdinalIgnoreCase) && o.Platform.Equals(chosen.Platform, StringComparison.OrdinalIgnoreCase))
              ?? offered.FirstOrDefault(o => o.Configuration.Equals(named.Trim(), StringComparison.OrdinalIgnoreCase));
        if (asked is null)
        {
            throw new InvalidOperationException($"The folder cannot build as {named}: it can build as {listed}");
        }
        return await ChooseBuildConfigurationAsync(asked);
    }
}
