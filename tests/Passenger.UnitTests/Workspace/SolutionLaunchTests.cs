using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Runs of several parts as their files hold them, read and written back: Visual Studio's multi-project launch
/// profiles (the files' names beside a solution, each project's path from the solution's folder, its action and launch
/// profile) and a folder's passenger-runs.json (each part's kind, readiness and variables); a profile or run left alone keeps
/// every character, a changed one keeps what Passenger does not model, a new one is laid out as its file lays them out, and
/// a file that does not read is never written over.</summary>
public sealed class SolutionLaunchTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Two profiles as Visual Studio writes them: two spaces a level, paths with '\', no line break at the end.</summary>
    private const string TwoProfiles = "[\n  {\n    \"Name\": \"Both\",\n    \"Projects\": [\n      {\n        \"Path\": \"src\\\\Api\\\\Api.csproj\",\n"
        + "        \"Action\": \"Start\",\n        \"DebugTarget\": \"http\"\n      },\n      {\n        \"Path\": \"Worker\\\\Worker.csproj\",\n"
        + "        \"Action\": \"StartWithoutDebugging\"\n      }\n    ]\n  },\n  {\n    \"Name\": \"Api alone\",\n    \"Projects\": [\n      {\n"
        + "        \"Path\": \"src\\\\Api\\\\Api.csproj\",\n        \"Action\": \"Start\",\n        \"Extra\": 7\n      }\n    ]\n  }\n]";

    [Fact]
    public void A_solutions_launch_profiles_are_read_from_the_files_beside_it() => Cases.AllApart<SolutionLaunchTests>(
        ("a solutions profiles are its name with slnLaunch and the persons own with user after", _ => Cases.EachApart<SolutionLaunchTests, string>(
        [
            "Shop.slnx",
        ], (test, row) => test.A_solutions_profiles_are_its_name_with_slnLaunch_and_the_persons_own_with_user_after(row))),
        ("each project is read from the solutions folder with its action and launch profile", test => test.Each_project_is_read_from_the_solutions_folder_with_its_action_and_launch_profile()),
        ("an early files Name for a path an action of any letter case and comments are read and an unknown action is kept", test => test.An_early_files_Name_for_a_path_an_action_of_any_letter_case_and_comments_are_read_and_an_unknown_action_is_kept()),
        ("a file that is no list of profiles says why and is never written over", _ => Cases.EachApart<SolutionLaunchTests, string>(
        [
            "{ \"Name\": \"x\" }",
            "[ { \"Name\": ",
        ], (test, row) => test.A_file_that_is_no_list_of_profiles_says_why_and_is_never_written_over(row))),
        ("a folders runs are read with each parts kind readiness and variables and a part or format it cannot read says why", test => test.A_folders_runs_are_read_with_each_parts_kind_readiness_and_variables_and_a_part_or_format_it_cannot_read_says_why()));

    private void A_solutions_profiles_are_its_name_with_slnLaunch_and_the_persons_own_with_user_after(string solution)
    {
        string path = Path.Combine(root, solution);
        Assert.True(SolutionLaunch.SharedFile(path) == Path.Combine(root, "Shop.slnLaunch"),
            $"{solution}'s profiles are looked for in {Path.GetFileName(SolutionLaunch.SharedFile(path))}, not Shop.slnLaunch, so Visual Studio's are never found");
        Assert.True(SolutionLaunch.UserFile(path) == Path.Combine(root, "Shop.slnLaunch.user"),
            $"{solution}'s own profiles are looked for in {Path.GetFileName(SolutionLaunch.UserFile(path))}, not Shop.slnLaunch.user");
        Assert.True(SolutionLaunch.IsFile(Path.Combine(root, "Shop.slnLaunch")) && SolutionLaunch.IsFile(Path.Combine(root, "Shop.slnLaunch.user")),
            "A change to a profiles file is not heard as one");
    }

    private void Each_project_is_read_from_the_solutions_folder_with_its_action_and_launch_profile()
    {
        (IReadOnlyList<SolutionLaunchProfile> profiles, string? problem) = SolutionLaunch.Parse(TwoProfiles, root, shared: true);
        Assert.Null(problem);
        Assert.Equal(["Both", "Api alone"], profiles.Select(p => p.Name));
        SolutionLaunchProfile both = profiles[0];
        Assert.True(both.Projects.Select(p => p.Project).SequenceEqual([At("src", "Api", "Api.csproj"), At("Worker", "Worker.csproj")]),
            $"The projects read as {string.Join(", ", both.Projects.Select(p => p.Project))}, so the profile starts what its file does not name");
        Assert.Equal([ProjectLaunchAction.Start, ProjectLaunchAction.StartWithoutDebugging], both.Projects.Select(p => p.Action));
        Assert.Equal(["http", null], both.Projects.Select(p => p.DebugTarget));
        Assert.True(both.Shared && both.Index == 0 && profiles[1].Index == 1, "A profile's place in its file is not kept, so writing it back would move it");
    }

    private void An_early_files_Name_for_a_path_an_action_of_any_letter_case_and_comments_are_read_and_an_unknown_action_is_kept()
    {
        const string Early = "// written by hand\n[\n  { \"Name\": \"Early\", \"Projects\": [\n    { \"Name\": \"Api\\\\Api.csproj\", \"Action\": \"startwithoutdebugging\", \"DebugTarget\": \"\" },\n"
            + "    { \"Path\": \"Tool\\\\Tool.csproj\", \"Action\": \"Profile\" },\n  ], },\n  { \"Projects\": [] },\n  42,\n]";
        (IReadOnlyList<SolutionLaunchProfile> profiles, string? problem) = SolutionLaunch.Parse(Early, root, shared: false);
        Assert.Null(problem);
        SolutionLaunchProfile early = Assert.Single(profiles);
        Assert.False(early.Shared);
        Assert.True(early.Projects[0].Project == At("Api", "Api.csproj"), "A project an early file names by Name is not read, so the profile starts nothing of it");
        Assert.Equal(ProjectLaunchAction.StartWithoutDebugging, early.Projects[0].Action);
        Assert.True(early.Projects[1] is { Action: null, ActionWritten: "Profile" }, "An action Passenger does not know is read as one it does, so it would start what the file does not say");
    }

    private void A_file_that_is_no_list_of_profiles_says_why_and_is_never_written_over(string text)
    {
        (IReadOnlyList<SolutionLaunchProfile> profiles, string? problem) = SolutionLaunch.Parse(text, root, shared: true);
        Assert.Empty(profiles);
        Assert.NotNull(problem);
        (string? written, string? refused) = SolutionLaunch.Written(text, [new SolutionLaunchProfile("New", [], true)], root);
        Assert.True(written is null && refused is not null, $"A file that does not read was written over as {written}, losing what the person wrote in it");
    }

    [Fact]
    public void Profiles_written_back_keep_every_character_Passenger_did_not_change() => Cases.AllApart<SolutionLaunchTests>(
        ("profiles written back unchanged keep every character", test => test.Profiles_written_back_unchanged_keep_every_character()),
        ("a changed profile is written in its place keeping what Passenger does not model and the others as they were", test => test.A_changed_profile_is_written_in_its_place_keeping_what_Passenger_does_not_model_and_the_others_as_they_were()),
        ("a new profile goes after the last laid out as Visual Studio writes one and a removed one goes with its lines", test => test.A_new_profile_goes_after_the_last_laid_out_as_Visual_Studio_writes_one_and_a_removed_one_goes_with_its_lines()),
        ("a new file is written as Visual Studio writes one and an item that is no profile is kept", test => test.A_new_file_is_written_as_Visual_Studio_writes_one_and_an_item_that_is_no_profile_is_kept()),
        ("a profile moved from the persons file to the teams keeps its members and both files are read beside the solution", test => test.A_profile_moved_from_the_persons_file_to_the_teams_keeps_its_members_and_both_files_are_read_beside_the_solution()),
        ("a project renamed or moved is named where it went in the separator it was written with every other character kept", test => test.A_project_renamed_or_moved_is_named_where_it_went_in_the_separator_it_was_written_with_every_other_character_kept()),
        ("the solution moved names each project from its new folder and a file that does not read is left alone", test => test.The_solution_moved_names_each_project_from_its_new_folder_and_a_file_that_does_not_read_is_left_alone()),
        ("runs written back keep what is unchanged a new file is format 1 and a moved package is followed", test => test.Runs_written_back_keep_what_is_unchanged_a_new_file_is_format_1_and_a_moved_package_is_followed()));

    private void Profiles_written_back_unchanged_keep_every_character()
    {
        (IReadOnlyList<SolutionLaunchProfile> profiles, _) = SolutionLaunch.Parse(TwoProfiles, root, shared: true);
        Assert.Equal(TwoProfiles, SolutionLaunch.Written(TwoProfiles, profiles, root).Text);
        string crlf = "// kept\r\n" + TwoProfiles.Replace("\n", "\r\n", StringComparison.Ordinal);
        (IReadOnlyList<SolutionLaunchProfile> again, _) = SolutionLaunch.Parse(crlf, root, shared: true);
        Assert.Equal(crlf, SolutionLaunch.Written(crlf, again, root).Text);
    }

    private void A_changed_profile_is_written_in_its_place_keeping_what_Passenger_does_not_model_and_the_others_as_they_were()
    {
        (IReadOnlyList<SolutionLaunchProfile> profiles, _) = SolutionLaunch.Parse(TwoProfiles, root, shared: true);
        SolutionLaunchProfile alone = profiles[1];
        SolutionLaunchProfile changed = alone with
        {
            Name = "Api only",
            Projects = [alone.Projects[0] with { Action = ProjectLaunchAction.StartWithoutDebugging, DebugTarget = "https" }],
        };
        string? text = SolutionLaunch.Written(TwoProfiles, [profiles[0], changed], root).Text;
        string expected = TwoProfiles.Replace("\"Name\": \"Api alone\"", "\"Name\": \"Api only\"", StringComparison.Ordinal)
            .Replace("\"Action\": \"Start\",\n        \"Extra\": 7", "\"Action\": \"StartWithoutDebugging\",\n        \"Extra\": 7,\n        \"DebugTarget\": \"https\"", StringComparison.Ordinal);
        Assert.Equal(expected, text);
    }

    private void A_new_profile_goes_after_the_last_laid_out_as_Visual_Studio_writes_one_and_a_removed_one_goes_with_its_lines()
    {
        (IReadOnlyList<SolutionLaunchProfile> profiles, _) = SolutionLaunch.Parse(TwoProfiles, root, shared: true);
        SolutionLaunchProfile added = new("Worker", [new SolutionLaunchProject(At("Worker", "Worker.csproj"), ProjectLaunchAction.Start, null)], true);
        string? text = SolutionLaunch.Written(TwoProfiles, [profiles[1], added], root).Text;
        string expected = "[\n  {\n    \"Name\": \"Api alone\",\n    \"Projects\": [\n      {\n        \"Path\": \"src\\\\Api\\\\Api.csproj\",\n"
            + "        \"Action\": \"Start\",\n        \"Extra\": 7\n      }\n    ]\n  },\n  {\n    \"Name\": \"Worker\",\n    \"Projects\": [\n      {\n"
            + "        \"Path\": \"Worker\\\\Worker.csproj\",\n        \"Action\": \"Start\"\n      }\n    ]\n  }\n]";
        Assert.Equal(expected, text);
        (IReadOnlyList<SolutionLaunchProfile> reread, _) = SolutionLaunch.Parse(text!, root, shared: true);
        Assert.True(reread.Select(p => p.Name).SequenceEqual(["Api alone", "Worker"]) && reread[1].Projects[0].Project == At("Worker", "Worker.csproj"),
            "What was written does not read back as the profiles written");
    }

    private void A_new_file_is_written_as_Visual_Studio_writes_one_and_an_item_that_is_no_profile_is_kept()
    {
        SolutionLaunchProfile made = new("Up", [
            new SolutionLaunchProject(At("src", "Api", "Api.csproj"), ProjectLaunchAction.Start, "http"),
            new SolutionLaunchProject(At("..", "Shared", "Tool.csproj"), ProjectLaunchAction.None, null),
        ], true);
        string expected = "[\n  {\n    \"Name\": \"Up\",\n    \"Projects\": [\n      {\n        \"Path\": \"src\\\\Api\\\\Api.csproj\",\n        \"Action\": \"Start\",\n"
            + "        \"DebugTarget\": \"http\"\n      },\n      {\n        \"Path\": \"..\\\\Shared\\\\Tool.csproj\",\n        \"Action\": \"None\"\n      }\n    ]\n  }\n]";
        Assert.Equal(expected, SolutionLaunch.Written(null, [made], root).Text);

        const string WithNote = "[\n  { \"Note\": \"kept\" },\n  {\n    \"Name\": \"Gone\",\n    \"Projects\": []\n  }\n]";
        Assert.Equal("[\n  { \"Note\": \"kept\" }\n]", SolutionLaunch.Written(WithNote, [], root).Text);
    }

    private void A_profile_moved_from_the_persons_file_to_the_teams_keeps_its_members_and_both_files_are_read_beside_the_solution()
    {
        string solution = Path.Combine(root, "Shop.slnx");
        File.WriteAllText(SolutionLaunch.SharedFile(solution), TwoProfiles);
        File.WriteAllText(SolutionLaunch.UserFile(solution), "[ { \"Name\": \"Mine\", \"Projects\": [ { \"Path\": \"Worker\\\\Worker.csproj\", \"Action\": \"Start\", \"Own\": true } ] } ]");
        (IReadOnlyList<SolutionLaunchProfile> profiles, IReadOnlyList<string> problems) = SolutionLaunch.Read(solution);
        Assert.Empty(problems);
        Assert.Equal(["Both", "Api alone", "Mine"], profiles.Select(p => p.Name));
        Assert.Equal([true, true, false], profiles.Select(p => p.Shared));

        SolutionLaunchProfile moved = profiles[2] with { Shared = true, Index = -1 };
        string? text = SolutionLaunch.Written(TwoProfiles, [profiles[0], profiles[1], moved], root).Text;
        Assert.True(text is not null && text.StartsWith(TwoProfiles[..^2], StringComparison.Ordinal) && text.Contains("\"Own\": true", StringComparison.Ordinal),
            $"The profile moved to the team's file lost what it held or changed the others: {text}");
    }

    private void A_project_renamed_or_moved_is_named_where_it_went_in_the_separator_it_was_written_with_every_other_character_kept()
    {
        const string Written = "// ours\n[\n  {\n    \"Name\": \"Both\",\n    \"Projects\": [\n      { \"Path\": \"src\\\\Api\\\\Api.csproj\", \"Action\": \"Start\", \"Kept\": 1 },\n"
            + "      { \"Name\": \"Worker/Worker.csproj\", \"Action\": \"None\" },\n      { \"Path\": \"Tool\\\\Tool.csproj\", \"Action\": \"Start\" },\n    ],\n  },\n  42\n]";
        IReadOnlyList<(string From, string To)> moves = [(At("src", "Api", "Api.csproj"), At("src", "Api", "Gateway.csproj")), (At("Worker"), At("apps", "Worker"))];
        string? moved = SolutionLaunch.Moved(Written, root, root, moves);
        string expected = Written.Replace("src\\\\Api\\\\Api.csproj", "src\\\\Api\\\\Gateway.csproj", StringComparison.Ordinal)
            .Replace("Worker/Worker.csproj", "apps/Worker/Worker.csproj", StringComparison.Ordinal);
        Assert.True(moved == expected, $"The profiles did not follow the projects moved as written, so Visual Studio and Run start what is gone:\n{moved}");
        Assert.Equal(Written, SolutionLaunch.Moved(Written, root, root, [(At("docs"), At("notes"))]));
    }

    private void The_solution_moved_names_each_project_from_its_new_folder_and_a_file_that_does_not_read_is_left_alone()
    {
        const string Written = "[ { \"Name\": \"Both\", \"Projects\": [ { \"Path\": \"Api\\\\Api.csproj\", \"Action\": \"Start\" } ] } ]";
        string? moved = SolutionLaunch.Moved(Written, root, At("solution"), []);
        Assert.Equal(Written.Replace("Api\\\\Api.csproj", "..\\\\Api\\\\Api.csproj", StringComparison.Ordinal), moved);
        Assert.Null(SolutionLaunch.Moved("[ { \"Name\": ", root, root, [(At("Api"), At("Gateway"))]));
        Assert.Null(SolutionLaunch.Moved("{ \"Name\": \"x\" }", root, root, [(At("Api"), At("Gateway"))]));
    }

    /// <summary>A folder's runs as a person writes them: a comment, a step, a script waiting for a line, a project and a
    /// command with its folder and variables, and a member Passenger does not model.</summary>
    private const string Runs = "{\n  // ours\n  \"passengerRuns\": 1,\n  \"runs\": [\n    {\n      \"name\": \"Web\",\n      \"parts\": [\n"
        + "        { \"install\": \"web/package.json\", \"readyWhen\": \"exited\" },\n"
        + "        { \"script\": \"build:watch\", \"package\": \"web/package.json\", \"readyWhen\": { \"line\": \"built in\" } },\n"
        + "        { \"project\": \"Api/Api.csproj\", \"launchProfile\": \"https\", \"action\": \"startWithoutDebugging\", \"readyWhen\": \"listening\", \"kept\": 1 },\n"
        + "        { \"command\": \"redis-server --port 6380\", \"folder\": \"tools\", \"environment\": { \"A\": \"1\", \"B\": 2 }, \"readyWhen\": { \"listening\": \"localhost:6380\" } }\n"
        + "      ]\n    },\n    {\n      \"name\": \"Odd\",\n      \"parts\": [ { \"lambda\": \"x\" }, { \"command\": \"x\", \"readyWhen\": \"soon\" } ]\n    }\n  ]\n}\n";

    private void A_folders_runs_are_read_with_each_parts_kind_readiness_and_variables_and_a_part_or_format_it_cannot_read_says_why()
    {
        Assert.True(RunsFile.PathIn(root) == Path.Combine(root, "passenger-runs.json") && RunsFile.IsFile(RunsFile.PathIn(root)),
            "A folder's runs are not looked for in passenger-runs.json at its top, so the team's are never found");
        (IReadOnlyList<RunsFileRun> runs, string? problem) = RunsFile.Parse(Runs, root);
        Assert.Null(problem);
        Assert.Equal(["Web", "Odd"], runs.Select(r => r.Name));
        IReadOnlyList<RunPart> web = runs[0].Parts;
        Assert.Equal([RunPartKind.Install, RunPartKind.Script, RunPartKind.Project, RunPartKind.Command], web.Select(p => p.Kind));
        Assert.Equal([At("web", "package.json"), At("web", "package.json"), At("Api", "Api.csproj"), string.Empty], web.Select(p => p.Project));
        Assert.Equal([ReadyWhen.Exited, ReadyWhen.Line, ReadyWhen.Listening, ReadyWhen.Listening], web.Select(p => p.Ready));
        Assert.Equal([string.Empty, "built in", string.Empty, "localhost:6380"], web.Select(p => p.ReadyText));
        Assert.True(web[2] is { Action: ProjectLaunchAction.StartWithoutDebugging, LaunchProfile: "https" } && web[1].Script == "build:watch",
            "A part's action, launch profile or script is not read as written, so the run starts what its file does not say");
        Assert.True(web[3].Command == "redis-server --port 6380" && web[3].Folder == At("tools") && web[3].Environment.Count == 2 && web[3].Environment["B"] == "2",
            "A command's folder or variables are not read as written");
        Assert.Equal(["web: install", "web: build:watch", "Api: https", "redis-server"], web.Select(RunsFile.NameOf));
        Assert.True(web.All(p => p.Unread is null) && runs[1].Parts.All(p => p.Unread is not null),
            "A part of a kind or readiness Passenger does not know is not said to be one, so its run would start something else");
        Assert.False(web.All(p => p.FitsSolutionLaunch), "A run of a script and a command is taken for one Visual Studio's file can hold");
        (IReadOnlyList<RunsFileRun> later, string? refused) = RunsFile.Parse("{ \"passengerRuns\": 2, \"runs\": [] }", root);
        Assert.True(later.Count == 0 && refused is not null, "A file of a later format is read as this one");
        Assert.True(RunsFile.Parse("{ \"runs\": [", root).Problem is not null && RunsFile.Written("[ 1 ]", [], root).Problem is not null,
            "A file that does not read as runs is not said to, or is written over");
    }

    private void Runs_written_back_keep_what_is_unchanged_a_new_file_is_format_1_and_a_moved_package_is_followed()
    {
        (IReadOnlyList<RunsFileRun> runs, _) = RunsFile.Parse(Runs, root);
        Assert.Equal(Runs, RunsFile.Written(Runs, runs, root).Text);
        RunsFileRun web = runs[0] with { Parts = [.. runs[0].Parts.Select((p, i) => i == 2 ? p with { Action = ProjectLaunchAction.Start, Environment = new() { ["X"] = "y" } } : p)] };
        string? changed = RunsFile.Written(Runs, [web, runs[1]], root).Text;
        Assert.True(changed is not null && changed.Contains("\"kept\": 1", StringComparison.Ordinal) && !changed.Contains("startWithoutDebugging", StringComparison.Ordinal)
            && changed.Contains("\"X\": \"y\"", StringComparison.Ordinal) && changed.Contains("// ours", StringComparison.Ordinal)
            && changed.Contains("{ \"lambda\": \"x\" }", StringComparison.Ordinal),
            $"A changed run lost what Passenger does not model, a comment or a run it left alone:\n{changed}");
        Assert.Equal(runs[0].Parts.Select(p => p.Kind), RunsFile.Parse(changed!, root).Runs[0].Parts.Select(p => p.Kind));

        RunsFileRun made = new("Up", [new RunPart(At("Api", "Api.csproj"), ProjectLaunchAction.Start, string.Empty),
            new RunPart(string.Empty, ProjectLaunchAction.Start, string.Empty) { Kind = RunPartKind.Command, Command = "make watch", Ready = ReadyWhen.Line, ReadyText = "ready" }]);
        Assert.Equal("{\n  \"passengerRuns\": 1,\n  \"runs\": [\n    {\n      \"name\": \"Up\",\n      \"parts\": [\n        {\n          \"project\": \"Api/Api.csproj\"\n        },\n"
            + "        {\n          \"command\": \"make watch\",\n          \"readyWhen\": {\n            \"line\": \"ready\"\n          }\n        }\n      ]\n    }\n  ]\n}\n",
            RunsFile.Written(null, [made], root).Text);

        string? moved = RunsFile.Moved(Runs, root, [(At("web"), At("apps", "web")), (At("Api", "Api.csproj"), At("Api", "Gateway.csproj"))]);
        Assert.Equal(Runs.Replace("\"web/package.json\"", "\"apps/web/package.json\"", StringComparison.Ordinal).Replace("Api/Api.csproj", "Api/Gateway.csproj", StringComparison.Ordinal), moved);
    }

    private string At(params string[] parts) => PathNames.Canonical(Path.GetFullPath(Path.Combine([root, .. parts])));
}
