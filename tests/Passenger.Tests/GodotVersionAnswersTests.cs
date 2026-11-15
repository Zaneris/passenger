using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A Godot's answer to <c>--version</c>, asked once per executable as it is written: one that gave none the first
/// time (a first start macOS held to check it, past the ask's limit) is asked again, and one that answered is not. A
/// stand-in Godot answering only once a file beside it says it may.</summary>
[Collection(Beside.Name)]
public sealed class GodotVersionAnswersTests
{
    [Fact]
    public async Task A_Godot_that_gave_no_version_is_asked_again_and_one_that_answered_is_not()
    {
        string folder = PathNames.Canonical(TestFolders.Create("godot-version-"));
        string ready = Path.Combine(folder, "ready");
        string asked = Path.Combine(folder, "asked");
        GodotFound godot = new(StandIn(folder, ready, asked), "a test") { Exists = true };
        DateTime written = File.GetLastWriteTimeUtc(godot.Executable);

        Assert.Null(await GodotExecutables.AnswerAsync(godot, TestContext.Current.CancellationToken));
        File.WriteAllText(ready, string.Empty);
        string? again = await GodotExecutables.AnswerAsync(godot, TestContext.Current.CancellationToken);
        string? kept = await GodotExecutables.AnswerAsync(godot, TestContext.Current.CancellationToken);

        Assert.True(File.GetLastWriteTimeUtc(godot.Executable) == written, "The stand-in was written again, so its answer would be asked again for that alone");
        Assert.True(again == Answer,
            $"After answering nothing once, the Godot was taken to answer {again ?? "nothing"}, so its version stays unsaid and what needs it refused until the Godot is installed again");
        int asks = File.ReadAllLines(asked).Length;
        Assert.True(kept == Answer && asks == 2, $"The Godot was asked {asks} times for three asks, so every run would start it again to read its version");
    }

    private const string Answer = "4.93.0.stable.mono.official.standin";

    /// <summary>A stand-in Godot noting each start, answering <c>--version</c> only once <paramref name="ready"/> is there.</summary>
    private static string StandIn(string folder, string ready, string asked)
    {
        if (OperatingSystem.IsWindows())
        {
            string batch = Path.Combine(folder, "godot.cmd");
            File.WriteAllText(batch, $"@echo off\r\necho.>>\"{asked}\"\r\nif not exist \"{ready}\" (\r\n  echo not ready 1>&2\r\n  exit /b 1\r\n)\r\necho {Answer}\r\n");
            return batch;
        }
        string script = Path.Combine(folder, "godot");
        File.WriteAllText(script, $"#!/bin/sh\necho >> '{asked}'\n[ -e '{ready}' ] || {{ echo 'not ready' >&2; exit 1; }}\necho {Answer}\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }
}
