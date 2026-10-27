using Passenger.App.CSharp;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Where what the status bar says has more to see: a project's load problem in Problems' Toolset, Environment, a
/// build's problems in Problems' Build, Hot Reload's edits in its tab, a binding error stopped at in Bindings, a test run in
/// Unit Tests, an internal error in IDE Internal Errors, a state file set aside in the file manager, the log where the
/// words say it says why, and a C# language server left stopped started again. A click on the words, or Enter on them,
/// goes there.</summary>
public partial class MainWindow
{
    /// <summary>Where a click on the status bar's words goes now, null when nowhere.</summary>
    public StatusPlace? StatusPlace => status.Place;

    /// <summary>Says something in the status bar, with where a click on it goes; the same words said again keep the place
    /// they had (a failed build's words, said again as the reason Run or a test run did not start).</summary>
    private void Say(string said, StatusPlace? more = null)
    {
        if (more is null || (status.Text == said && status.Place is not null))
        {
            status.Text = said;
            return;
        }
        status.Say(said, null, more);
    }

    /// <summary>A project's load problem in Problems' Toolset, Environment, its row chosen.</summary>
    private StatusPlace LoadProblemPlace(ProjectLoadProblem problem) => StatusPlace.Problems(Dock, Bottom.Problems, ProblemsTab.Toolset,
        p => p.Path is { } path && PathNames.Same(path, problem.Project) && p.Message == problem.Message);

    /// <summary>A failed binding in Problems' Bindings, its row chosen.</summary>
    private StatusPlace BindingPlace(Run.Problem failed) => StatusPlace.Problems(Dock, Bottom.Problems, ProblemsTab.Bindings,
        p => p.Path == failed.Path && p.Position == failed.Position && p.Message == failed.Message);

    /// <summary>IDE Internal Errors, the latest (the one said) chosen, as the status bar's red mark opens them.</summary>
    private StatusPlace ErrorsPlace => new("see it in IDE Internal Errors", () => ShowErrors());

    /// <summary>A state file set aside, chosen in the file manager.</summary>
    private StatusPlace SetAsidePlace(string kept) =>
        new($"show {Path.GetFileName(kept)} in {FileManager.Name}", () => ShowInFileManager(kept));

    /// <summary>Help, Show Log: the log chosen in the file manager.</summary>
    private StatusPlace LogPlace => new($"show the log in {FileManager.Name}", ShowLog);

    /// <summary>Restart C# Language Server, run as its menu item runs it.</summary>
    private StatusPlace RestartCSharpPlace => new("start it again", () => Request(AppActions.RestartCSharpServer));
}
