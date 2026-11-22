using System.Collections.Concurrent;
using System.Diagnostics;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The Godot editors tests start, by process, so the games each plays end with the test: every game Godot's Play
/// starts names its editor by <c>--editor-pid</c>, and may outlive an editor ended as it started one (on macOS it is no child
/// of the editor's then). A test ends its editor's games once it has ended the editor (<see cref="EndGamesOf"/>), and after
/// every test those of an editor that has gone are found, ended and named (<see cref="LeftGames"/>), which fails the test.
/// Godot's editor stops a game it plays by killing it, which leaves the game's runtime files: each game's are read while
/// it runs and taken once it has gone (<see cref="Follow"/>).</summary>
public static class TestGodotEditors
{
    private static readonly ConcurrentDictionary<int, bool> Editors = new();

    /// <summary>How often an editor's games are looked for while it runs.</summary>
    private static readonly TimeSpan Looking = TimeSpan.FromMilliseconds(250);

    /// <summary>An editor a test started, whose games end with the test, their runtime files with them.</summary>
    public static void Began(int editor)
    {
        Editors[editor] = true;
        ConcurrentDictionary<int, RuntimeFiles> games = new();
        UnawaitedWork.Dismiss(Waiting.Apart(() => Waiting.UntilAsync(() => Follow(editor, games), TimeSpan.FromHours(1), every: Looking)));
    }

    /// <summary>The runtime files of the editor's games, read while each runs and taken once it has gone; true once the
    /// editor and its games have.</summary>
    private static bool Follow(int editor, ConcurrentDictionary<int, RuntimeFiles> games)
    {
        bool running = Processes.IsRunning(editor);
        if (running)
        {
            foreach (int game in Processes.Descendants(editor).Where(g => !games.ContainsKey(g)))
            {
                if (RuntimeFiles.Of(game) is { } files)
                {
                    games[game] = files;
                }
            }
        }
        foreach ((int game, RuntimeFiles files) in games)
        {
            if (files.Remove())
            {
                games.TryRemove(game, out _);
            }
        }
        return !running && games.IsEmpty;
    }

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
