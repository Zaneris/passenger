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
    public void A_launch_with_its_folder_files_and_caret_reads_back_as_it_was_written()
    {
        HandedLaunch launch = new(Root + "Space", null, [Root + "Space/Player.cs", Root + "Space/Enemy.cs"], new TextPosition(11, 4));

        HandedLaunch? read = Read(launch.Message());

        Assert.True(read is not null && read.Folder == launch.Folder && read.Workspace is null && read.Files.SequenceEqual(launch.Files) && read.Caret == launch.Caret,
            $"The running Passenger would open something other than what the launch named: {launch.Message()} read back as {read}");
    }

    [Fact]
    public void The_caret_goes_on_the_wire_counted_from_one()
    {
        string message = new HandedLaunch(Root + "Space", null, [Root + "Space/Player.cs"], new TextPosition(0, 0)).Message();

        using JsonDocument written = JsonDocument.Parse(message);
        JsonElement caret = written.RootElement.GetProperty("launch").GetProperty("caret");
        Assert.True(caret.GetProperty("line").GetInt32() == 1 && caret.GetProperty("column").GetInt32() == 1,
            $"A running Passenger of another build would put the caret a line off: {message}");
    }

    [Fact]
    public void A_launch_naming_nothing_or_only_a_workspace_reads_back_as_it_was_written()
    {
        HandedLaunch bare = new(null, null, [], null);
        HandedLaunch solution = new(Root + "Space", Root + "Space/Space.sln", [], null);

        HandedLaunch? readBare = Read(bare.Message());
        HandedLaunch? readSolution = Read(solution.Message());

        Assert.True(readBare is { Folder: null, Workspace: null, Files.Count: 0, Caret: null } && readSolution?.Workspace == solution.Workspace,
            $"A launch naming nothing, or a solution, would open something else: {readBare}, {readSolution}");
    }

    [Theory]
    [InlineData("""{"open": "/games/Space"}""")]
    [InlineData("""[1, 2]""")]
    [InlineData("""{"launch": "/games/Space"}""")]
    public void A_line_asking_no_launch_is_no_launch(string line)
    {
        Assert.True(Read(line) is null, $"'{line}' was read as a launch, which would open a window for an agent's or a stranger's line");
    }

    [Theory]
    [InlineData("""{"launch": {"folder": "Space"}}""", "a launch's folder is a whole path, not 'Space'")]
    [InlineData("""{"launch": {"files": ["Player.cs"]}}""", "a launch's file is a whole path, not 'Player.cs'")]
    [InlineData("""{"launch": {"files": [7]}}""", "a launch's files are paths")]
    [InlineData("""{"launch": {"files": ["/games/a.cs"], "caret": {"line": 0, "column": 1}}}""", "a launch's caret is its line and column, each counted from 1")]
    [InlineData("""{"launch": {"files": ["/games/a.cs"], "caret": {"line": "3"}}}""", "a launch's caret is its line and column, each counted from 1")]
    [InlineData("""{"launch": {"caret": {"line": 3, "column": 1}}}""", "a launch's caret needs a file to go in")]
    public void A_malformed_launch_is_refused_saying_why(string line, string why)
    {
        if (OperatingSystem.IsWindows())
        {
            line = line.Replace("/games/", "C:/games/", StringComparison.Ordinal);
        }

        FormatException? refused = Record.Exception(() => Read(line)) as FormatException;

        Assert.True(refused?.Message == why, $"'{line}' was to be refused saying \"{why}\"; it said {refused?.Message ?? "nothing, and was taken"}");
    }

    [Fact]
    public void An_answer_says_the_folder_taken_in_or_why_not()
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
