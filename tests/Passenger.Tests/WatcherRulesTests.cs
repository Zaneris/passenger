using System.Text.RegularExpressions;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests;

/// <summary>A folder's tree is watched and walked only through Passenger's homes for each: what another program changes on
/// disk through WatchedFile (a file alone) and WatchedTree (a folder), each reading again what its watcher lost as the watcher
/// fails and as it is asked to look again; a link a walk meets told apart only through Core's FileLinks.</summary>
[Collection(Beside.Name)]
[SameOnEverySystem]
public sealed class WatcherRulesTests
{
    [Fact]
    public void A_folders_tree_is_watched_and_its_links_told_apart_only_through_their_homes() => Cases.All(
        ("no FileSystemWatcher is named outside WatchedFile and WatchedTree", No_FileSystemWatcher_is_named_outside_WatchedFile_and_WatchedTree),
        ("no link is told apart by its attributes outside FileLinks", No_link_is_told_apart_by_its_attributes_outside_FileLinks));

    /// <summary>The system's watcher is named in src's C# only in its homes (Workspace/WatchedFile.cs, WatchedTree.cs, and
    /// SharedWatch.cs, which WatchedTree's folders share), and the shared watch only in WatchedTree.cs and its own file: one
    /// made elsewhere has no handler for its error and no look again as a window comes in front, so a change the system
    /// dropped stays unseen. Read from the code as written, comments passed over.</summary>
    private static void No_FileSystemWatcher_is_named_outside_WatchedFile_and_WatchedTree()
    {
        List<string> found = [.. Named("FileSystemWatcher", ["src/Passenger.App/Workspace/WatchedFile.cs", "src/Passenger.App/Workspace/WatchedTree.cs", "src/Passenger.App/Workspace/SharedWatch.cs"]),
            .. Named("SharedWatch", ["src/Passenger.App/Workspace/WatchedTree.cs", "src/Passenger.App/Workspace/SharedWatch.cs"])];
        Assert.True(found.Count == 0, "A system's watch used outside WatchedFile and WatchedTree hears no change it lost, as the system may drop one without "
            + $"a word, and is never looked at again as a window comes in front: watch a file alone through WatchedFile, a folder through WatchedTree:\n{string.Join('\n', found)}");
    }

    /// <summary>A file's reparse point is named in src's C# only in Core's FileLinks, which reads it on Windows alone: off
    /// Windows its attributes are read with whether the file is read-only, which for each one another user owns asks the
    /// directory service whether the person is in its group. Read from the code as written, comments passed over.</summary>
    private static void No_link_is_told_apart_by_its_attributes_outside_FileLinks()
    {
        List<string> found = Named("ReparsePoint", ["src/Passenger.Core/FileLinks.cs"]);
        Assert.True(found.Count == 0, "A link told apart by its attributes, where the system has no reparse points, reads whether each file is read-only "
            + "too, which may ask the directory service of every file another user owns, so a walk of .NET's root-owned packs takes seconds: tell "
            + $"links apart through FileLinks.Is:\n{string.Join('\n', found)}");
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
