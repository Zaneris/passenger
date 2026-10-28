using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.Core;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Run, Debug and Profile of the program at hand: the configuration a gutter icon's menu names, else the program
/// of the Explorer's row, else the open file's. Each chooses that configuration in the toolbar and launches it as Run,
/// Debug and Profile do, under their work, so a program is started one way wherever it is asked for.</summary>
public partial class MainWindow
{
    private void AddRunHere()
    {
        Here(A.RunHere, "Run", A.RunProgram);
        Here(A.DebugHere, "Debug", A.Debug);
        Here(A.ProfileHere, "Profile", A.Profile);

        void Here(string id, string verb, string launch) => actions!.Add(new IdeAction(id, $"{verb} This Program", "Run", c => LaunchHere(c, launch))
        {
            WhyNot = c => ConfigurationHere(c) is { } here
                ? WhyNotHere(here) is { } why ? $"{here.Name} cannot {verb.ToLowerInvariant()} here: {why}" : null
                : WhyNoProgram(c),
            TitleIn = c => ConfigurationHere(c) is { } here ? $"{verb} '{here.Name}'" : $"{verb} This Program",
            Works = launch,
        });
    }

    /// <summary>Chooses the configuration at hand and launches it by the toolbar's own action; what stopped it, if anything,
    /// is in the status bar.</summary>
    private void LaunchHere(ActionContext context, string launch)
    {
        if (ConfigurationHere(context) is not { } here || !SelectConfiguration(here.Name))
        {
            return;
        }
        if (actions!.Run(launch, context) is { } why)
        {
            status.Text = why;
        }
    }

    /// <summary>The configuration Run, Debug and Profile of the program at hand launch: a gutter icon's, else that of the
    /// project of the Explorer's row (its own menu's, or the one chosen while the Explorer has the keys), else, the
    /// Explorer not having the keys, that of the open file's project.</summary>
    private RunConfiguration? ConfigurationHere(ActionContext context)
    {
        if (context.Configuration is { } named)
        {
            return named;
        }
        if (RowOf(context) is { } row)
        {
            return row is { Kind: NodeKind.Project, Path: { } project } ? ProgramConfiguration(project) : null;
        }
        if (ExplorerHasKeys(context))
        {
            return null;
        }
        return context.File is { } file && session.ProjectOf(file.Path) is { } owner ? ProgramConfiguration(owner.Path) : null;
    }

    /// <summary>The configuration that runs a project: the toolbar's when it does, else the first of the project's that can
    /// run here, else its first; null for a project nothing runs.</summary>
    private RunConfiguration? ProgramConfiguration(string project)
    {
        RunConfiguration? first = null;
        foreach (RunConfiguration configuration in Configurations.Where(c => PathNames.Same(c.Project, project)))
        {
            if (ReferenceEquals(configuration, Configuration))
            {
                return configuration;
            }
            if (first is null || WhyNotHere(first) is not null && WhyNotHere(configuration) is null)
            {
                first = configuration;
            }
        }
        return first;
    }

    private string WhyNoProgram(ActionContext context) => RowOf(context) is { } row ? $"{row.Name} is no program"
        : ExplorerHasKeys(context) ? NoRow
        : context.File is { } file ? $"{file.Name} is in no program"
        : Refusals.NoFile;
}
