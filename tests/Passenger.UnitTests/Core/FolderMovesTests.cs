using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A folder carried as from one volume to another (an Undo setting a made project aside under the state, Redo
/// bringing it back) arrives as it was: its folder and file links still links to where they led, what builds wrote under
/// bin and obj, hidden files, and each file's last write time.</summary>
public sealed class FolderMovesTests : IDisposable
{
    private static readonly DateTime Written = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_folder_carried_away_and_back_keeps_its_links_build_output_hidden_files_and_times()
    {
        string made = Path.Join(root, "volume-a", "Shop");
        string outside = Write(Path.Join(root, "elsewhere", "shared.txt"), "shared\n");
        Write(Path.Join(made, "Program.cs"), "class Program { }\n");
        Write(Path.Join(made, "Lib", "Code.cs"), "class Code { }\n");
        Write(Path.Join(made, "bin", "Debug", "Shop.dll"), "built\n");
        Write(Path.Join(made, "obj", "project.assets.json"), "{}\n");
        Write(Path.Join(made, ".editorconfig"), "root = true\n");
        Link(() => Directory.CreateSymbolicLink(Path.Join(made, "LibLink"), "Lib"));
        Link(() => File.CreateSymbolicLink(Path.Join(made, "Main.cs"), "Program.cs"));
        Link(() => File.CreateSymbolicLink(Path.Join(made, "shared.txt"), outside));
        Link(() => File.CreateSymbolicLink(Path.Join(made, "gone.txt"), "nothing-there.txt"));
        List<string> before = Listed(made);

        string aside = Path.Join(root, "volume-b", "undone", "Shop");
        FolderMoves.Carry(made, aside);
        Assert.False(Directory.Exists(made), "The folder carried away is still where it was");
        Assert.Equal(before, Listed(aside));

        FolderMoves.Carry(aside, made);
        Assert.False(Directory.Exists(aside), "The folder carried back is still where it was set aside");
        Assert.Equal(before, Listed(made));
        Assert.Equal("shared\n", File.ReadAllText(Path.Join(made, "shared.txt")));
        Assert.Equal(["Shop"], Directory.EnumerateFileSystemEntries(Path.Join(root, "volume-a")).Select(Path.GetFileName));
    }

    [Fact]
    public void A_carry_refused_leaves_the_folder_where_it_was_as_it_was_and_says_why()
    {
        string made = Path.Join(root, "volume-a", "Shop");
        Write(Path.Join(made, "Program.cs"), "class Program { }\n");
        Write(Path.Join(made, "bin", "Shop.dll"), "built\n");
        List<string> before = Listed(made);
        string taken = Write(Path.Join(root, "volume-b", "Shop"), "a file where the folder would go\n");

        IOException refused = Assert.ThrowsAny<IOException>(() => FolderMoves.Carry(made, taken));
        Assert.Contains(taken, refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Listed(made));
        Assert.Equal(["Shop"], Directory.EnumerateFileSystemEntries(Path.Join(root, "volume-a")).Select(Path.GetFileName));
        Assert.Equal("a file where the folder would go\n", File.ReadAllText(taken));
    }

    /// <summary>Every entry under a folder, by its path from there: a link with where it leads, a file with its text and
    /// last write time, a folder by its name.</summary>
    private static List<string> Listed(string folder)
    {
        List<string> listed = [];
        Walk(folder);
        listed.Sort(StringComparer.Ordinal);
        return listed;

        void Walk(string at)
        {
            foreach (FileSystemInfo entry in new DirectoryInfo(at).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                string name = Path.GetRelativePath(folder, entry.FullName).Replace('\\', '/');
                if (entry.LinkTarget is { } target)
                {
                    listed.Add($"{name} -> {target}");
                }
                else if (entry is DirectoryInfo)
                {
                    listed.Add($"{name}/");
                    Walk(entry.FullName);
                }
                else
                {
                    listed.Add($"{name}: {File.ReadAllText(entry.FullName).Trim()} at {entry.LastWriteTimeUtc:O}");
                }
            }
        }
    }

    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, Written);
        return path;
    }

    /// <summary>Makes a link, the test skipped where the system lets this user make none (Windows without Developer Mode).</summary>
    private static void Link(Func<FileSystemInfo> make)
    {
        try
        {
            make();
        }
        catch (Exception refused) when (OperatingSystem.IsWindows() && refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Windows lets this user make no symlink: {refused.Message}");
        }
    }
}
