using Passenger.App.Workspace;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A build's churn under obj/ and bin/, which no registration of the language server's matches, costs the
/// watchers no allocation for each change: a registration tells a build output's name it can never match without making a
/// string, and a watcher counts such a change toward a burst without one once its folder has been heard of; what the
/// registrations ask for under obj/ still matches, and is matched without a string made; and the watchers over a change, or
/// over a folder, are found among a thousand registrations elsewhere at what they cost among none.</summary>
public sealed class WatcherChurnTests
{
    private const string Project = "/game/App";

    internal void A_change_under_build_output_no_registration_matches_costs_no_allocation()
    {
        FileWatch sources = new(Project, "**/*.{cs,csx}", WatchKinds.Create | WatchKinds.Change | WatchKinds.Delete);
        FileWatch assets = new($"{Project}/obj", "project.assets.json", WatchKinds.Create | WatchKinds.Change | WatchKinds.Delete);
        string[] built = [.. Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? $"{Project}/obj/Debug/net10.0/App{i}.dll" : $"{Project}/bin/Debug/net10.0/App{i}.pdb")];
        // Each registration reads its pattern once, at the first name under its folder: that is set aside.
        Assert.DoesNotContain(built, path => sources.Matches(path) || assets.Matches(path));
        long before = GC.GetAllocatedBytesForCurrentThread();
        int matched = 0;
        foreach (string path in built)
        {
            matched += sources.Matches(path) || assets.Matches(path) ? 1 : 0;
        }
        long told = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, matched);
        Assert.True(told == 0, $"Telling 1,000 build outputs from what the registrations ask for allocated {told:N0} bytes");
        Assert.True(sources.Matches($"{Project}/obj/Debug/net10.0/App.AssemblyInfo.cs"), "A source the build writes under obj/ no longer matches the registration of sources");
        Assert.True(assets.Matches($"{Project}/obj/project.assets.json"), "The restore's assets no longer match their registration");
        string[] written = [.. Enumerable.Range(0, 1000).Select(i => $"{Project}/Folder{i % 10}/Source{i}.cs")];
        before = GC.GetAllocatedBytesForCurrentThread();
        foreach (string path in written)
        {
            matched += sources.Matches(path) ? 1 : 0;
        }
        long sourcesMatched = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(written.Length, matched);
        Assert.True(sourcesMatched == 0, $"Matching 1,000 sources the registration asks for allocated {sourcesMatched:N0} bytes");

        // The same two registrations alone and among a thousand of folders elsewhere, as a solution's packages register them.
        WatchIndex few = new([sources, assets]);
        WatchIndex many = new([.. Enumerable.Range(0, 1000).Select(i => new FileWatch($"/packages/library{i}/lib", "**/*.dll", WatchKinds.All)), sources, assets]);
        (long Told, long Concerned) Costs(WatchIndex index)
        {
            // Each look made once first, so what a first use sets up once (a static lambda's delegate) is not counted.
            Assert.True(index.Covers(new FileEvent(written[0], FileChangeType.Changed)) && !index.Matches(built[0]) && index.Concerning($"{Project}/obj").Count == 2,
                "The index did not find what the registrations ask for");
            long start = GC.GetAllocatedBytesForCurrentThread();
            int covered = 0;
            for (int i = 0; i < written.Length; i++)
            {
                covered += index.Covers(new FileEvent(written[i], FileChangeType.Changed)) ? 1 : 0;
                covered += index.Matches(built[i]) ? 1 : 0;
            }
            long told = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal(written.Length, covered);
            start = GC.GetAllocatedBytesForCurrentThread();
            List<FileWatch> concerning = index.Concerning($"{Project}/obj");
            long concerned = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal([sources, assets], concerning.OrderBy(w => w.Pattern, StringComparer.Ordinal));
            return (told, concerned);
        }
        (long fewTold, long fewConcerned) = Costs(few);
        (long manyTold, long manyConcerned) = Costs(many);
        Assert.True(fewTold == 0 && manyTold == 0, $"Telling 2,000 changes from the watchers over each allocated {fewTold:N0} bytes among two registrations, {manyTold:N0} among a thousand more");
        Assert.True(manyConcerned == fewConcerned, $"The registrations over a folder cost {manyConcerned:N0} bytes to find among a thousand registrations elsewhere, against {fewConcerned:N0} among none: "
            + "every refresh of the server's watchers costs the square of its registrations");

        using Churn watcher = new(Project);
        watcher.Hear(built[0]);
        watcher.Hear(built[1]);
        before = GC.GetAllocatedBytesForCurrentThread();
        foreach (string path in built)
        {
            watcher.Hear(path);
        }
        long counted = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(counted == 0, $"Counting 1,000 changes under obj/ and bin/ toward a burst allocated {counted:N0} bytes");
    }

    /// <summary>A watcher that counts every change it hears toward a burst and takes none, as the server's does a change
    /// no registration matches; it watches nothing, its changes heard as told.</summary>
    private sealed class Churn(string folder) : WatchedTree(folder, FolderWatching.Platform, eachFolder: false)
    {
        public void Hear(string path) => Note(path, created: false);

        protected override bool Kept(string path) => true;

        protected override Notice Noticed(string path, bool created) => new(Takes: false, Counts: true, Canonical: null);

        protected override void Take(string path, bool created, Notice notice)
        {
        }

        protected override FolderChanges? Gathered(bool lost, bool overflowed) => null;

        protected override void Hand(FolderChanges changes)
        {
        }
    }
}
