using System.Diagnostics;
using System.Globalization;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A test that leaves running a game of a Godot editor it ended fails as it ends, the game ended then: a stand-in
/// game (a shell that sleeps, its command line naming the editor as Godot's Play names it) of a stand-in editor that has
/// gone.</summary>
public sealed class TestGodotEditorsTests
{
    [Fact]
    public void A_test_that_leaves_a_game_of_an_editor_it_ended_running_fails_and_the_game_ends()
    {
        bool windows = OperatingSystem.IsWindows();
        using Process editor = ChildProcesses.Start(windows ? ChildProcesses.Command("cmd.exe", ["/c", "exit", "0"]) : ChildProcesses.Command("/bin/sh", ["-c", "exit 0"]));
        editor.WaitForExit();
        string named = editor.Id.ToString(CultureInfo.InvariantCulture);
        using Process game = ChildProcesses.Start(windows
            ? ChildProcesses.Command("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep", "-Seconds", "60;", "#", "--editor-pid", named])
            : ChildProcesses.Command("/bin/sh", ["-c", "sleep 60; :", "sh", "--editor-pid", named]));
        try
        {
            Assert.True(Waiting.Until(() => DotnetProcesses.Describe(game.Id) is { } running && TestGodotEditors.NamesEditor(running.CommandLine, editor.Id), TimeSpan.FromSeconds(10)),
                $"The stand-in game's command line does not name its editor: {DotnetProcesses.Describe(game.Id)?.CommandLine}");
            TestGodotEditors.Began(editor.Id);

            InvalidOperationException left = Assert.Throws<InvalidOperationException>(() => new TestsLeaveNothingBehindAttribute().After(
                typeof(TestGodotEditorsTests).GetMethod(nameof(A_test_that_leaves_a_game_of_an_editor_it_ended_running_fails_and_the_game_ends))!,
                (Xunit.v3.IXunitTest)TestContext.Current.Test!));

            Assert.Contains($"--editor-pid {named}", left.Message, StringComparison.Ordinal);
            Assert.True(Processes.Gone(game.Id), $"The game the test left (process {game.Id}) still runs after the test's end found it");
        }
        finally
        {
            Processes.Kill(game.Id);
        }
    }
}
