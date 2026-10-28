using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>How the IDE compares, shows and walks paths: a folder's files and nothing beside it whose name begins the same, a
/// path shown from the open folder, letter case as the system's file systems take it.</summary>
public class PathNamesTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\x" : "/x";

    /// <summary>Whether two names differing only in letter case are one, as the system's file systems take them.</summary>
    private static readonly bool CaseBlind = !OperatingSystem.IsLinux();

    [Fact]
    public void A_path_is_under_a_folder_only_past_a_separator_after_its_whole_name()
    {
        string app = At("app");
        Assert.True(PathNames.IsUnder(At("app", "src", "A.cs"), app));
        Assert.True(PathNames.IsUnder(At("app", "A.cs"), app + Path.DirectorySeparatorChar));
        Assert.True(PathNames.IsUnder(app + "/A.cs", app));
        Assert.False(PathNames.IsUnder(At("app2", "A.cs"), app));
        Assert.False(PathNames.IsUnder(app, app));
        Assert.False(PathNames.IsUnder(app + Path.DirectorySeparatorChar, app));
        Assert.Equal(CaseBlind, PathNames.IsUnder(At("App", "A.cs"), app));
        Assert.Equal(CaseBlind, PathNames.Same(At("App"), app));
    }

    [Fact]
    public void Below_a_folder_is_the_names_from_it_down_and_nothing_for_the_folder_or_a_path_outside()
    {
        Assert.Equal(Path.Join("src", "A.cs"), PathNames.Below(At("app", "src", "A.cs"), At("app")).ToString());
        Assert.Equal(string.Empty, PathNames.Below(At("app"), At("app")).ToString());
        Assert.Equal(string.Empty, PathNames.Below(At("app2", "A.cs"), At("app")).ToString());
    }

    [Fact]
    public void A_moved_folder_takes_the_paths_in_it_along_and_leaves_one_beside_it_whose_name_begins_the_same()
    {
        Assert.Equal(At("app", "lib", "A.cs"), PathNames.Moved(At("app", "src", "A.cs"), At("app", "src"), At("app", "lib")));
        Assert.Equal(At("app", "lib"), PathNames.Moved(At("app", "src"), At("app", "src"), At("app", "lib")));
        Assert.Equal(At("app", "src2", "A.cs"), PathNames.Moved(At("app", "src2", "A.cs"), At("app", "src"), At("app", "lib")));
        Assert.True(PathNames.Within(At("app"), At("app")));
        Assert.False(PathNames.Within(At("app2"), At("app")));
    }

    [Fact]
    public void A_path_is_shown_from_the_open_folder_the_folder_by_its_name_and_anything_else_whole()
    {
        string app = At("app");
        Assert.Equal(Path.Join("src", "A.cs"), PathNames.Shown(At("app", "src", "A.cs"), app));
        Assert.Equal(Path.Join("src", "A.cs"), PathNames.Shown(At("app", "src", "A.cs"), app + Path.DirectorySeparatorChar));
        Assert.Equal("app", PathNames.Shown(app, app));
        Assert.Equal("app", PathNames.Shown(app + Path.DirectorySeparatorChar, app));
        Assert.Equal(At("app2", "A.cs"), PathNames.Shown(At("app2", "A.cs"), app));
        Assert.Equal(At("app", "A.cs"), PathNames.Shown(At("app", "A.cs"), null));
        Assert.Equal(At("app", "A.cs"), PathNames.Shown(At("app", "A.cs"), string.Empty));
    }

    [Fact]
    public void Extensions_and_the_systems_own_files_are_known_in_any_letter_case()
    {
        Assert.True(PathNames.HasExtension(At("A.CS"), [".csproj", ".cs"]));
        Assert.False(PathNames.HasExtension(At("A.csproj"), [".cs"]));
        Assert.False(PathNames.HasExtension(At("cs"), [".cs"]));
        Assert.True(PathNames.IsSystemFile(".DS_Store"));
        Assert.True(PathNames.IsSystemFile("THUMBS.DB"));
        Assert.False(PathNames.IsSystemFile("DS_Store"));
    }

    [Fact]
    public void A_path_not_there_yet_keeps_its_missing_names_below_the_canonical_folder_that_is()
    {
        using Folder folder = new();
        string canonical = PathNames.Canonical(folder.Path);
        Assert.Equal(canonical, PathNames.Canonical(canonical));
        Assert.Equal(Path.Join(canonical, "not", "There.cs"), PathNames.Canonical(Path.Join(folder.Path, "not", "There.cs")));
        Assert.Equal(Path.Join(canonical, "b"), PathNames.Canonical(Path.Join(folder.Path, "a", "..", "b")));
    }

    [Fact]
    public void A_walk_gives_each_folders_files_by_name_nearest_folder_first_leaving_out_the_systems_files_and_folders_not_entered()
    {
        using Folder folder = new();
        folder.Write("b.txt", "a.txt", ".DS_Store", Path.Join("sub", "c.txt"), Path.Join("sub", "deeper", "d.txt"), Path.Join("skip", "e.txt"),
            Path.Join("also", "f.txt"));
        string root = PathNames.Canonical(folder.Path);
        string Shown(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
        Assert.Equal([".: a.txt b.txt", "also: also/f.txt", "sub: sub/c.txt", "sub/deeper: sub/deeper/d.txt"],
            PathNames.FilesUnder(folder.Path, f => Path.GetFileName(f) != "skip").Select(f => $"{Shown(f.Folder)}: {string.Join(' ', f.Files.Select(Shown))}"));
        Assert.Equal([folder.Path, Path.Join(folder.Path, "also"), Path.Join(folder.Path, "sub"), Path.Join(folder.Path, "sub", "deeper")],
            PathNames.FoldersUnder(folder.Path, f => Path.GetFileName(f) != "skip"));
    }

    private static string At(params string[] names) => Path.Join([Root, .. names]);

    /// <summary>A folder of the test's own under the system's temporary folder, gone when the test is.</summary>
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("passenger-unit-").FullName;

        public void Write(params string[] files)
        {
            foreach (string file in files)
            {
                string full = System.IO.Path.Join(Path, file);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                File.WriteAllText(full, file);
            }
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
