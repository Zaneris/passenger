using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>What a solution filter leaves out, the one rule both the session and a head start go by.</summary>
public sealed class SolutionFilterTests
{
    private static string Named(string relative) => PathNames.Canonical(Path.Combine(Path.GetTempPath(), "filtered", relative));

    [Fact]
    public void A_filter_leaves_out_the_solutions_projects_it_does_not_name_never_a_program_beside_the_solution()
    {
        SolutionFilter filter = new(Named("App.slnf"), Named("Shop.slnx"), new HashSet<string>(StringComparer.Ordinal) { Named("App/App.csproj") });

        Assert.False(filter.LeavesOut(ProjectModel.Unevaluated(Named("App/App.csproj"))), "The project the filter names is left out");
        Assert.True(filter.LeavesOut(ProjectModel.Unevaluated(Named("Lib/Lib.csproj"))), "A project of the solution the filter does not name stays loaded");
        Assert.False(filter.LeavesOut(ProjectModel.Unevaluated(Named("scripts/tool.cs"))), "A program beside the solution, no project of it, is left out with those the filter leaves out");
    }
}
