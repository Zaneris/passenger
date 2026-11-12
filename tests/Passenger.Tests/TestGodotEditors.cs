using System.Collections.Concurrent;
using System.Diagnostics;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The Godot editors tests start, by process, so the games each plays end with the test: every game Godot's Play
/// starts names its editor by <c>--editor-pid</c>, and may outlive an editor ended as it started one (on macOS it is no child
/// of the editor's then). A test ends its editor's games once it has ended the editor (<see cref="EndGamesOf"/>), and after
/// every test those of an editor that has gone are found, ended and named (<see cref="LeftGames"/>), which fails the test.</summary>
public static class TestGodotEditors
{
    private static readonly ConcurrentDictionary<int, bool> Editors = new();

    /// <summary>An editor a test started, whose games end with the test.</summary>
    public static void Began(int editor) => Editors[editor] = true;

    /// <summary>The games still running that name an editor, ended: each described.</summary>
    public static IReadOnlyList<string> EndGamesOf(int editor)
    {
        List<string> ended = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId && DotnetProcesses.Describe(process.Id) is { } game && NamesEditor(game.CommandLine, editor))
                {
                    ended.Add($"process {game.Id}: {game.CommandLine}");
                    OwnProcess.Of(process).Kill();
                }
            }
        }
        return ended;
    }

    /// <summary>The games of every editor tests started that has gone since, ended and described; an editor gone is forgotten.</summary>
    public static IReadOnlyList<string> LeftGames()
    {
        List<string> left = [];
        foreach (int editor in Editors.Keys.Where(e => !Processes.IsRunning(e)).ToList())
        {
            left.AddRange(EndGamesOf(editor));
            Editors.TryRemove(editor, out _);
        }
        return left;
    }

    /// <summary>Whether a command line names an editor after <c>--editor-pid</c>, as Godot's Play gives every game it starts.</summary>
    public static bool NamesEditor(string commandLine, int editor)
    {
        string[] words = commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string named = editor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return words.Zip(words.Skip(1)).Any(w => w.First == "--editor-pid" && w.Second.Trim('"') == named);
    }
}
