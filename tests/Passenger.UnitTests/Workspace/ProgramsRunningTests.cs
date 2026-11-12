using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Whose configuration a project's code runs with: a program's and a test project's their own, a library's each
/// program's that references it, directly or through other libraries, never through another program or a test; and the
/// code a program runs, the same answer the other way.</summary>
public sealed class ProgramsRunningTests
{
    private static string PathOf(string name) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "solution", name, $"{name}.csproj"));

    private static ProjectModel Project(string name, string? outputType, params string[] references) =>
        new(PathOf(name), name, outputType, ["net10.0"], [], [.. references.Select(PathOf)], true);

    private static ProjectModel Tests(string name, params string[] references) =>
        new(PathOf(name), name, "Exe", ["net10.0"], [], [.. references.Select(PathOf)], true, DeclaredTest: true);

    private static string[] Names(IEnumerable<ProjectModel> projects) => [.. projects.Select(p => p.Name)];

    [Fact]
    public void A_library_is_run_by_each_program_referencing_it_directly_or_through_libraries()
    {
        ProjectModel api = Project("Api", "Exe", "Core");
        ProjectModel core = Project("Core", "Library", "Data");
        ProjectModel data = Project("Data", null);
        ProjectModel worker = Project("Worker", "WinExe", "Data");
        ProjectModel[] all = [api, core, data, worker];

        Assert.Equal(["Api", "Worker"], Names(data.ProgramsRunning(all)));
        Assert.Equal(["Api"], Names(core.ProgramsRunning(all)));
        Assert.Equal(["Api", "Core", "Data"], Names(api.CodeItRuns(all)));
    }

    [Fact]
    public void A_program_and_a_test_project_run_their_own_code_and_a_test_runs_no_library_s()
    {
        ProjectModel api = Project("Api", "Exe");
        ProjectModel core = Project("Core", "Library");
        ProjectModel tests = Tests("Core.Tests", "Core", "Api");
        ProjectModel[] all = [api, core, tests];

        Assert.Equal(["Api"], Names(api.ProgramsRunning(all)));
        Assert.Equal(["Core.Tests"], Names(tests.ProgramsRunning(all)));
        Assert.Empty(core.ProgramsRunning(all));
        Assert.Equal(["Core.Tests"], Names(tests.CodeItRuns(all)));
    }

    [Fact]
    public void An_AppHost_referencing_a_service_runs_none_of_the_service_s_libraries()
    {
        ProjectModel host = Project("AppHost", "Exe", "Api", "Shared");
        ProjectModel api = Project("Api", "Exe", "Core");
        ProjectModel core = Project("Core", "Library");
        ProjectModel shared = Project("Shared", "Library");
        ProjectModel[] all = [host, api, core, shared];

        Assert.Equal(["Api"], Names(core.ProgramsRunning(all)));
        Assert.Equal(["AppHost"], Names(shared.ProgramsRunning(all)));
        Assert.Equal(["AppHost", "Shared"], Names(host.CodeItRuns(all)));
    }

    [Fact]
    public void A_library_no_program_references_is_run_by_none_even_through_a_cycle()
    {
        ProjectModel one = Project("One", "Library", "Two");
        ProjectModel two = Project("Two", "Library", "One");
        ProjectModel api = Project("Api", "Exe");
        ProjectModel[] all = [one, two, api];

        Assert.Empty(one.ProgramsRunning(all));
        Assert.Empty(two.ProgramsRunning(all));
        Assert.Equal(["Api"], Names(api.CodeItRuns(all)));
    }

    /// <summary>Whatever the graph, a project's code is a program's to run exactly where the program runs it, so what a
    /// settings file completes and what checks a read against it never disagree.</summary>
    [Fact]
    public void The_code_a_program_runs_is_what_names_that_program_as_running_it()
    {
        ProjectModel[] all =
        [
            Project("AppHost", "Exe", "Api", "Worker", "Shared"),
            Project("Api", "Exe", "Core", "Shared"),
            Project("Worker", "WinExe", "Data"),
            Project("Core", "Library", "Data", "Api"),
            Project("Data", null, "Core"),
            Project("Shared", "Library"),
            Project("Alone", "Library", "Shared"),
            Tests("Api.Tests", "Api", "Core"),
        ];

        foreach (ProjectModel program in all)
        {
            foreach (ProjectModel project in all)
            {
                bool runs = program.CodeItRuns(all).Contains(project);
                bool named = project.ProgramsRunning(all).Contains(program);
                Assert.True(runs == named, $"{program.Name} {(runs ? "runs" : "does not run")} {project.Name}'s code, which {(named ? "names" : "does not name")} it as running it");
            }
        }
    }
}
