using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A cache kept by last use says it pruned only once the prune is done: what it removed (an entry unused past its
/// limit, a write that never finished) is gone with the folders it left empty, and what is in use or still being written
/// stays. Whoever waits on the prune reads what it left by that word.</summary>
public sealed class UsedCacheTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_prune_says_it_pruned_only_once_what_it_removed_and_the_folders_left_empty_are_gone()
    {
        string stale = Plant("Old.Library.pdb", TimeSpan.FromDays(40));
        string recent = Plant("Recent.Library.pdb", TimeSpan.FromDays(3));
        string abandoned = Plant("Killed.Library.pdb.0a1b.tmp", TimeSpan.FromHours(2));
        string writing = Plant("Writing.Library.pdb.2c3d.tmp", TimeSpan.FromMinutes(1));
        string[] going = [stale, abandoned, .. new[] { stale, abandoned }.SelectMany(Above)];
        List<string> said = [];
        List<string> thereWhenSaid = [];
        UsedCache cache = new(root, UsedEntries.Files(3, ".pdb"), new UsedLimits(TimeSpan.FromDays(30), TimeSpan.FromDays(1), 512L << 20, TimeSpan.FromHours(1)),
            line =>
            {
                said.Add(line);
                thereWhenSaid.AddRange(going.Where(Path.Exists));
            });

        cache.Prune(Now);

        Assert.True(said.Count == 1, $"The prune said {said.Count} things ({string.Join(" | ", said)}), not once that it pruned");
        Assert.True(thereWhenSaid.Count == 0,
            $"The prune said it pruned ({said[0]}) while {string.Join(", ", thereWhenSaid)} was still there, so whoever reads the cache by its word reads it half pruned");
        Assert.True(File.Exists(recent), "An entry used 3 days ago, within its month, was pruned");
        Assert.True(File.Exists(writing), "A temporary file a minute old, which another process may still be writing, was pruned");
        Assert.Equal(["Recent.Library", "Writing.Library"], Directory.EnumerateDirectories(root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>A file in an entry's folder of the cache (NAME/VERSION/FILE), last written as long before <see cref="Now"/> as given.</summary>
    private string Plant(string file, TimeSpan ago)
    {
        string path = Path.Join(root, file[..file.IndexOf(".pdb", StringComparison.Ordinal)], Guid.NewGuid().ToString("N"), file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[1024]);
        File.SetLastWriteTimeUtc(path, Now - ago);
        return path;
    }

    /// <summary>The folders a planted file is in, below the cache's own.</summary>
    private static string[] Above(string file) => [Path.GetDirectoryName(file)!, Path.GetDirectoryName(Path.GetDirectoryName(file))!];
}
