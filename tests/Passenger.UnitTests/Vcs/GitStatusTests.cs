using System.Text;
using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>A status read from git's bytes where they lie: every kind of entry git's porcelain writes read as the files,
/// conflicts and stages it names, and a status of thousands of files costing no more for each than its path.</summary>
public class GitStatusTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "repo");

    [Fact]
    public void Each_kind_of_entry_is_read_as_the_file_change_git_means()
    {
        string written = string.Join('\0',
            "# branch.oid 0123456789abcdef0123456789abcdef01234567",
            "# branch.head main",
            "1 .M N... 100644 100644 100644 aaaa bbbb src/Shop.cs",
            "1 A. N... 000000 100644 100644 0000 cccc src/New File.cs",
            "1 D. N... 100644 000000 000000 dddd 0000 src/Gone.cs",
            "1 .M N... 100644 100644 100755 eeee eeee tools/run.sh",
            "1 .M SCMU 160000 160000 160000 ffff ffff lib/sub",
            "2 R. N... 100644 100644 100644 1111 1111 R100 src/Renamed.cs", "src/Old.cs",
            "u UU N... 100644 100644 100644 100644 b1 b2 b3 src/Both.cs",
            "u DU N... 100644 000000 100644 100644 c1 0000 c3 src/Theirs.cs",
            "? notes/todo café.txt",
            "? src/Shop.cs.passenger-save.user",
            string.Empty);

        Git.StatusRead read = Git.StatusOf(Encoding.UTF8.GetBytes(written), new Git.DiskNames(Root));

        Assert.Equal(("main", "0123456789abcdef0123456789abcdef01234567"), (read.Branch, read.Head));
        Assert.Equal(
        [
            new FileChange(Full("src/Shop.cs"), ChangeKind.Modified),
            new FileChange(Full("src/New File.cs"), ChangeKind.Added),
            new FileChange(Full("src/Gone.cs"), ChangeKind.Deleted),
            new FileChange(Full("tools/run.sh"), ChangeKind.Modified, Mode: new ModeChange("100644", "100755")),
            new FileChange(Full("lib/sub"), ChangeKind.Modified,
                Submodule: SubmoduleChange.Submodule | SubmoduleChange.Commit | SubmoduleChange.Modified | SubmoduleChange.Untracked),
            new FileChange(Full("src/Renamed.cs"), ChangeKind.Renamed, Full("src/Old.cs")),
            new FileChange(Full("src/Both.cs"), ChangeKind.Conflicted),
            new FileChange(Full("src/Theirs.cs"), ChangeKind.Conflicted),
            new FileChange(Full("notes/todo café.txt"), ChangeKind.Unversioned),
        ], read.Changes);
        Assert.Equal([new GitConflict(Full("src/Both.cs"), true, true, true), new GitConflict(Full("src/Theirs.cs"), true, false, true)], read.Conflicts);
        string[][] stages = [["b1", "b2", "b3"], ["c1", "c3"]];
        Assert.Equal(stages, read.Stages);
    }

    [Fact]
    public void A_first_commit_to_come_and_a_detached_head_name_no_commit_and_no_branch()
    {
        Git.StatusRead read = Git.StatusOf("# branch.oid (initial)\0# branch.head (detached)\0"u8, new Git.DiskNames(Root));
        Assert.Equal((null, null), (read.Branch, read.Head));
        Assert.Empty(read.Changes);
    }

    [Fact]
    public void A_branchs_upstream_is_read_with_whether_HEAD_is_its_commit_and_none_said_where_it_is_gone()
    {
        Assert.Equal(("origin/main", true), Upstream("# branch.head main\0# branch.upstream origin/main\0# branch.ab +0 -0\0"u8));
        Assert.Equal(("origin/main", false), Upstream("# branch.head main\0# branch.upstream origin/main\0# branch.ab +? -?\0"u8));
        Assert.Equal(("upstream/feature", false), Upstream("# branch.head main\0# branch.upstream upstream/feature\0# branch.ab +2 -0\0"u8));
        Assert.Equal(("origin/main", null), Upstream("# branch.head main\0# branch.upstream origin/main\0"u8));
        Assert.Equal((null, null), Upstream("# branch.head main\0"u8));

        static (string?, bool?) Upstream(ReadOnlySpan<byte> written)
        {
            Git.StatusRead read = Git.StatusOf(written, new Git.DiskNames(Root));
            return (read.Upstream, read.Even);
        }
    }

    [Fact]
    public void A_status_of_thousands_of_files_costs_each_file_no_more_than_its_path()
    {
        byte[] few = Untracked(200);
        byte[] many = Untracked(2_200);
        Git.StatusOf(few, new Git.DiskNames(Root));
        long fewBytes = Measured(few);
        long manyBytes = Measured(many);

        long each = (manyBytes - fewBytes) / 2_000;
        int path = 22 + 2 * Full("new/levels/forest/more/Untracked1000.txt").Length;
        Assert.True(each <= path + 128,
            $"Each of 2,000 more untracked files cost a status read {each:N0} bytes, past its {path:N0}-byte path and its change: a large untracked folder costs every status read its output again");
    }

    private static long Measured(byte[] output)
    {
        Git.DiskNames names = new(Root);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Git.StatusRead read = Git.StatusOf(output, names);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(read);
        return allocated;
    }

    /// <summary>A status of this many untracked files, each named as long as the others.</summary>
    private static byte[] Untracked(int count) =>
        Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1_000, count).Select(i => $"? new/levels/forest/more/Untracked{i % 10_000:D4}.txt\0")));

    private static string Full(string relative) => Path.GetFullPath(Path.Combine(Root, relative));
}
