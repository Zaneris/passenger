using System.Text.RegularExpressions;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests;

/// <summary>What another program changes on disk is watched only through Passenger's two homes for it: a file alone through
/// WatchedFile, a folder through WatchedTree, each reading again what its watcher lost as the watcher fails and as it is
/// asked to look again.</summary>
[Collection(Beside.Name)]
[SameOnEverySystem]
public sealed class WatcherRulesTests
{
    /// <summary>The system's watcher is named in src's C# only in its homes (Workspace/WatchedFile.cs, WatchedTree.cs, and
    /// SharedWatch.cs, which WatchedTree's folders share), and the shared watch only in WatchedTree.cs and its own file: one
    /// made elsewhere has no handler for its error and no look again as a window comes in front, so a change the system
    /// dropped stays unseen. Read from the code as written, comments passed over.</summary>
    [Fact]
    public void No_FileSystemWatcher_is_named_outside_WatchedFile_and_WatchedTree()
    {
        List<string> found = [.. Named("FileSystemWatcher", ["src/Passenger.App/Workspace/WatchedFile.cs", "src/Passenger.App/Workspace/WatchedTree.cs", "src/Passenger.App/Workspace/SharedWatch.cs"]),
            .. Named("SharedWatch", ["src/Passenger.App/Workspace/WatchedTree.cs", "src/Passenger.App/Workspace/SharedWatch.cs"])];
        Assert.True(found.Count == 0, "A system's watch used outside WatchedFile and WatchedTree hears no change it lost, as the system may drop one without "
            + $"a word, and is never looked at again as a window comes in front: watch a file alone through WatchedFile, a folder through WatchedTree:\n{string.Join('\n', found)}");
    }

    /// <summary>Each line of src's C# outside <paramref name="homes"/> naming <paramref name="name"/>, comments passed over.</summary>
    private static List<string> Named(string name, string[] homes)
    {
        string root = Debuggees.Root;
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (homes.Contains(relative) || relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }
            string[] lines = File.ReadAllLines(file);
            for (int line = 0; line < lines.Length; line++)
            {
                if (!lines[line].TrimStart().StartsWith("//", StringComparison.Ordinal) && Regex.IsMatch(lines[line], $@"\b{name}\b"))
                {
                    found.Add($"{relative}:{line + 1}: {lines[line].Trim()}");
                }
            }
        }
        return found;
    }
}
