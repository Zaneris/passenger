using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Paths alike but for letter case, compared as the folders holding the names that differ tell case: here a volume
/// mounted at cs tells case apart, as a case-sensitive APFS volume does, gone is not there, and every other folder ignores
/// case, as the system volumes of macOS and Windows do.</summary>
public class PathComparerTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\v" : "/v";

    /// <summary>Each folder the comparer asked, in order.</summary>
    private readonly List<string> asked = [];

    private readonly PathComparer comparer;

    public PathComparerTests() => comparer = new(folder =>
    {
        asked.Add(folder);
        return Within(folder, At("gone")) ? null : !Within(folder, At("cs"));
    });

    [Fact]
    public void Names_differing_only_in_case_are_two_files_where_their_folder_tells_case_and_one_where_it_ignores_it()
    {
        Assert.False(comparer.Equals(At("cs", "README.md"), At("cs", "readme.md")), "README.md and readme.md on a case-sensitive volume compared as one file");
        Assert.True(comparer.Equals(At("plain", "README.md"), At("plain", "readme.md")), "README.md and readme.md where case is ignored compared as two files");
        Assert.True(comparer.Equals(At("CS", "README.md"), At("cs", "README.md")), "The volume's own name, in a folder that ignores case, was told apart by case");
        Assert.False(comparer.Equals(At("CS", "README.md"), At("cs", "readme.md")), "A name differing in case inside the volume was passed over once its mount's name differed too");
        Assert.True(comparer.Equals("README.md", "readme.md"), "A name with no folder before it was told apart by case");
    }

    [Fact]
    public void Paths_differing_otherwise_or_not_at_all_ask_no_folder()
    {
        Assert.True(comparer.Equals(At("cs", "a.md"), new string(At("cs", "a.md").AsSpan())));
        Assert.False(comparer.Equals(At("cs", "a.md"), At("cs", "b.md")));
        Assert.False(comparer.Equals(At("cs", "a.md"), At("cs", "a.mdx")));
        Assert.True(asked.Count == 0, $"Paths alike or differing past case asked {string.Join(", ", asked)}");
    }

    [Fact]
    public void A_folder_is_asked_once_and_one_not_there_is_answered_by_the_nearest_folder_above_that_is()
    {
        comparer.Equals(At("cs", "docs", "A.md"), At("cs", "docs", "a.md"));
        comparer.Equals(At("cs", "docs", "B.md"), At("cs", "docs", "b.md"));
        Assert.Equal([At("cs", "docs")], asked);

        asked.Clear();
        Assert.True(comparer.Equals(At("gone", "sub", "A.md"), At("gone", "sub", "a.md")), "A folder not there was not answered as the folder above it ignores case");
        Assert.Equal([At("gone", "sub"), At("gone"), Root], asked);
    }

    [Fact]
    public void A_set_keyed_by_paths_holds_both_names_where_case_is_told_and_finds_either_spelling_where_it_is_ignored()
    {
        HashSet<string> paths = new(comparer) { At("cs", "README.md"), At("cs", "readme.md"), At("plain", "README.md") };
        Assert.Equal(3, paths.Count);
        Assert.True(paths.Contains(At("plain", "readme.md")), "A path where case is ignored was not found by another spelling");
        Assert.False(paths.Add(At("plain", "Readme.md")), "A path where case is ignored was added again by another spelling");
        Assert.True(comparer.Compare(At("cs", "README.md"), At("cs", "readme.md")) != 0 && comparer.Compare(At("plain", "README.md"), At("plain", "readme.md")) == 0,
            "Sorting took two files for one, or one for two");
    }

    private static string At(params string[] names) => Path.Join([Root, .. names]);

    /// <summary>Whether a folder is another or inside it, the names down to it compared ignoring case as the folders holding
    /// them do.</summary>
    private static bool Within(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
