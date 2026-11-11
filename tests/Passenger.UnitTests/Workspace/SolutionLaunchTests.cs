using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Visual Studio's multi-project launch profiles read as its files write them, and written back so Visual Studio
/// reads them again: the files' names beside a solution, each project's path from the solution's folder, its action and
/// launch profile; a profile left alone keeps every character, a changed one keeps what Passenger does not model, a new one
/// is laid out as Visual Studio lays out the file, and a file that does not read is never written over.</summary>
public sealed class SolutionLaunchTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Two profiles as Visual Studio writes them: two spaces a level, paths with '\', no line break at the end.</summary>
    private const string TwoProfiles = "[\n  {\n    \"Name\": \"Both\",\n    \"Projects\": [\n      {\n        \"Path\": \"src\\\\Api\\\\Api.csproj\",\n"
        + "        \"Action\": \"Start\",\n        \"DebugTarget\": \"http\"\n      },\n      {\n        \"Path\": \"Worker\\\\Worker.csproj\",\n"
        + "        \"Action\": \"StartWithoutDebugging\"\n      }\n    ]\n  },\n  {\n    \"Name\": \"Api alone\",\n    \"Projects\": [\n      {\n"
        + "        \"Path\": \"src\\\\Api\\\\Api.csproj\",\n        \"Action\": \"Start\",\n        \"Extra\": 7\n      }\n    ]\n  }\n]";

    [Theory]
    [InlineData("Shop.sln")]
    [InlineData("Shop.slnx")]
    public void A_solutions_profiles_are_its_name_with_slnLaunch_and_the_persons_own_with_user_after(string solution)
    {
        string path = Path.Combine(root, solution);
        Assert.True(SolutionLaunch.SharedFile(path) == Path.Combine(root, "Shop.slnLaunch"),
            $"{solution}'s profiles are looked for in {Path.GetFileName(SolutionLaunch.SharedFile(path))}, not Shop.slnLaunch, so Visual Studio's are never found");
        Assert.True(SolutionLaunch.UserFile(path) == Path.Combine(root, "Shop.slnLaunch.user"),
            $"{solution}'s own profiles are looked for in {Path.GetFileName(SolutionLaunch.UserFile(path))}, not Shop.slnLaunch.user");
        Assert.True(SolutionLaunch.IsFile(Path.Combine(root, "Shop.slnLaunch")) && SolutionLaunch.IsFile(Path.Combine(root, "Shop.slnLaunch.user")),
            "A change to a profiles file is not heard as one");
    }

    [Fact]
    public void Each_project_is_read_from_the_solutions_folder_with_its_action_and_launch_profile()
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

    [Fact]
    public void An_early_files_Name_for_a_path_an_action_of_any_letter_case_and_comments_are_read_and_an_unknown_action_is_kept()
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

    [Theory]
    [InlineData("{ \"Name\": \"x\" }")]
    [InlineData("[ { \"Name\": ")]
    public void A_file_that_is_no_list_of_profiles_says_why_and_is_never_written_over(string text)
    {
        (IReadOnlyList<SolutionLaunchProfile> profiles, string? problem) = SolutionLaunch.Parse(text, root, shared: true);
        Assert.Empty(profiles);
        Assert.NotNull(problem);
        (string? written, string? refused) = SolutionLaunch.Written(text, [new SolutionLaunchProfile("New", [], true)], root);
        Assert.True(written is null && refused is not null, $"A file that does not read was written over as {written}, losing what the person wrote in it");
    }

    [Fact]
    public void Profiles_written_back_unchanged_keep_every_character()
    {
        (IReadOnlyList<SolutionLaunchProfile> profiles, _) = SolutionLaunch.Parse(TwoProfiles, root, shared: true);
        Assert.Equal(TwoProfiles, SolutionLaunch.Written(TwoProfiles, profiles, root).Text);
        string crlf = "// kept\r\n" + TwoProfiles.Replace("\n", "\r\n", StringComparison.Ordinal);
        (IReadOnlyList<SolutionLaunchProfile> again, _) = SolutionLaunch.Parse(crlf, root, shared: true);
        Assert.Equal(crlf, SolutionLaunch.Written(crlf, again, root).Text);
    }

    [Fact]
    public void A_changed_profile_is_written_in_its_place_keeping_what_Passenger_does_not_model_and_the_others_as_they_were()
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

    [Fact]
    public void A_new_profile_goes_after_the_last_laid_out_as_Visual_Studio_writes_one_and_a_removed_one_goes_with_its_lines()
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

    [Fact]
    public void A_new_file_is_written_as_Visual_Studio_writes_one_and_an_item_that_is_no_profile_is_kept()
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

    [Fact]
    public void A_profile_moved_from_the_persons_file_to_the_teams_keeps_its_members_and_both_files_are_read_beside_the_solution()
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

    private string At(params string[] parts) => PathNames.Canonical(Path.GetFullPath(Path.Combine([root, .. parts])));
}
