using Passenger.App.Workspace;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A build's churn under obj/ and bin/, which no registration of the language server's matches, costs the
/// watchers no allocation for each change: a registration tells a build output's name it can never match without making a
/// string, and a watcher counts such a change toward a burst without one once its folder has been heard of; what the
/// registrations ask for under obj/ still matches.</summary>
public sealed class WatcherChurnTests
{
    private const string Project = "/game/App";

    [Fact]
    public void A_change_under_build_output_no_registration_matches_costs_no_allocation()
    {
        FileWatch sources = new(Project, "**/*.{cs,csx}", WatchKinds.Create | WatchKinds.Change | WatchKinds.Delete);
        FileWatch assets = new($"{Project}/obj", "project.assets.json", WatchKinds.Create | WatchKinds.Change | WatchKinds.Delete);
        string[] built = [.. Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? $"{Project}/obj/Debug/net10.0/App{i}.dll" : $"{Project}/bin/Debug/net10.0/App{i}.pdb")];
        Assert.False(sources.Matches(built[0]) || assets.Matches(built[1]));
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
