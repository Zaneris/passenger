using System.Text.Json;
using Passenger.App;
using Passenger.Text;

namespace Passenger.UnitTests.Launch;

/// <summary>The line a launch hands a running Passenger, and the answers it is told: the folder, workspace, files and the
/// caret (counted from 1 on the wire, from 0 as the editor holds it) come back as they went, and a line that is malformed
/// is refused saying why, never opened as something else.</summary>
public class HandedLaunchTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\Games\" : "/games/";

    [Fact]
    public void A_launch_handed_to_a_running_Passenger_reads_back_as_it_was_written() => Cases.All(
        ("a launch with its folder files and caret reads back as it was written", A_launch_with_its_folder_files_and_caret_reads_back_as_it_was_written),
        ("the caret goes on the wire counted from one", The_caret_goes_on_the_wire_counted_from_one),
        ("a launch naming nothing or only a workspace reads back as it was written", A_launch_naming_nothing_or_only_a_workspace_reads_back_as_it_was_written),
        ("a launch asking for a new window reads back as one and a plain launch never does", A_launch_asking_for_a_new_window_reads_back_as_one_and_a_plain_launch_never_does),
        ("an answer says the folder taken in or why not", An_answer_says_the_folder_taken_in_or_why_not));

    private void A_launch_with_its_folder_files_and_caret_reads_back_as_it_was_written()
    {
        HandedLaunch launch = new(Root + "Space", null, [Root + "Space/Player.cs", Root + "Space/Enemy.cs"], new TextPosition(11, 4));

        HandedLaunch? read = Read(launch.Message());

        Assert.True(read is not null && read.Folder == launch.Folder && read.Workspace is null && read.Files.SequenceEqual(launch.Files) && read.Caret == launch.Caret,
            $"The running Passenger would open something other than what the launch named: {launch.Message()} read back as {read}");
    }

    private void The_caret_goes_on_the_wire_counted_from_one()
    {
        string message = new HandedLaunch(Root + "Space", null, [Root + "Space/Player.cs"], new TextPosition(0, 0)).Message();

        using JsonDocument written = JsonDocument.Parse(message);
        JsonElement caret = written.RootElement.GetProperty("launch").GetProperty("caret");
        Assert.True(caret.GetProperty("line").GetInt32() == 1 && caret.GetProperty("column").GetInt32() == 1,
            $"A running Passenger of another build would put the caret a line off: {message}");
    }

    private void A_launch_naming_nothing_or_only_a_workspace_reads_back_as_it_was_written()
    {
        HandedLaunch bare = new(null, null, [], null);
        HandedLaunch solution = new(Root + "Space", Root + "Space/Space.sln", [], null);

        HandedLaunch? readBare = Read(bare.Message());
        HandedLaunch? readSolution = Read(solution.Message());

        Assert.True(readBare is { Folder: null, Workspace: null, Files.Count: 0, Caret: null } && readSolution?.Workspace == solution.Workspace,
            $"A launch naming nothing, or a solution, would open something else: {readBare}, {readSolution}");
    }

    private void A_launch_asking_for_a_new_window_reads_back_as_one_and_a_plain_launch_never_does()
    {
        HandedLaunch? asked = Read(new HandedLaunch(null, null, [], null) { NewWindow = true }.Message());
        HandedLaunch? plain = Read(new HandedLaunch(Root + "Space", null, [], null).Message());

        Assert.True(asked is { NewWindow: true, Folder: null, Files.Count: 0 } && plain is { NewWindow: false },
            $"The taskbar's New Window would bring a window forward rather than make one, or a folder's launch would make an empty window: {asked}, {plain}");
    }

    [Fact]
    public void A_line_that_is_no_launch_or_a_malformed_one_opens_nothing() => Cases.All(
        ("a line asking no launch is no launch", () => Cases.Each<string>(
        [
            """{"open": "/games/Space"}""",
            """{"launch": "/games/Space"}""",
        ], A_line_asking_no_launch_is_no_launch)),
        ("a malformed launch is refused saying why", () => Cases.Each<(string, string)>(
        [
            ("""{"launch": {"folder": "Space"}}""", "a launch's folder is a whole path, not 'Space'"),
            ("""{"launch": {"files": [7]}}""", "a launch's files are paths"),
            ("""{"launch": {"files": ["/games/a.cs"], "caret": {"line": 0, "column": 1}}}""", "a launch's caret is its line and column, each counted from 1"),
            ("""{"launch": {"caret": {"line": 3, "column": 1}}}""", "a launch's caret needs a file to go in"),
            ("""{"launch": {"folder": "/games/Space", "newWindow": true}}""", "a launch asking for a new window names nothing to open in it"),
        ], row => A_malformed_launch_is_refused_saying_why(row.Item1, row.Item2))));

    private void A_line_asking_no_launch_is_no_launch(string line)
    {
        Assert.True(Read(line) is null, $"'{line}' was read as a launch, which would open a window for an agent's or a stranger's line");
    }

    private void A_malformed_launch_is_refused_saying_why(string line, string why)
    {
        if (OperatingSystem.IsWindows())
        {
            line = line.Replace("/games/", "C:/games/", StringComparison.Ordinal);
        }

        FormatException? refused = Record.Exception(() => Read(line)) as FormatException;

        Assert.True(refused?.Message == why, $"'{line}' was to be refused saying \"{why}\"; it said {refused?.Message ?? "nothing, and was taken"}");
    }

    private void An_answer_says_the_folder_taken_in_or_why_not()
    {
        using JsonDocument taken = JsonDocument.Parse(HandedLaunch.Answer(Root + "Space", null));
        using JsonDocument none = JsonDocument.Parse(HandedLaunch.Answer(null, null));
        using JsonDocument refused = JsonDocument.Parse(HandedLaunch.Answer(null, "Passenger has no window ready to take it"));

        Assert.True(taken.RootElement.GetProperty("taken").GetString() == Root + "Space" && none.RootElement.GetProperty("taken").GetString() == string.Empty
            && refused.RootElement.GetProperty("refused").GetString() == "Passenger has no window ready to take it" && !refused.RootElement.TryGetProperty("taken", out _),
            $"The launch would misread its answer: {taken.RootElement}, {none.RootElement}, {refused.RootElement}");
    }

    private static HandedLaunch? Read(string line)
    {
        using JsonDocument message = JsonDocument.Parse(line);
        return HandedLaunch.Read(message.RootElement);
    }
}
