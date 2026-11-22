using System.Diagnostics;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A child ended while what it started forks (a build step's shell starting its next command): the system's kill
/// of the child's process group passes over a process forked into it at that moment, now and then, which then runs on.
/// Alone, as its children fork hundreds of times a second.</summary>
[Collection(Alone.Name)]
public sealed class ChildGroupKillTests
{
    /// <summary>Each of the child's forks writes a line a fifth of a second after it starts, so one that outlived the kill
    /// is seen writing after it, where what was writing as the child was killed has landed by a twentieth of a second.
    /// Thirty rounds, as one kill of the group passed over such a fork in about one round of twenty.</summary>
    internal async Task A_child_forking_as_it_is_killed_leaves_nothing_of_its_group_to_write_after()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows ends a child with its tree and held family, not a process group");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string folder = TestFolders.Create("group-kill-");
        for (int round = 1; round <= 30; round++)
        {
            string written = Path.Combine(folder, $"written-{round}.txt");
            Process child = ChildProcesses.Start(ChildProcesses.Command("/bin/sh", ["-c",
                $"for i in 1 2 3; do (while true; do (sleep 0.2; echo late >> '{written}') & sleep 0.005; done) & done; wait"]));
            try
            {
                await Waiting.ForAsync(TimeSpan.FromMilliseconds(400), "the child's loops to fork and write", cancel);
                ChildProcesses.Kill(child);
                await Waiting.ForAsync(TimeSpan.FromMilliseconds(50), "what was writing as the child was killed to land", cancel);
                long length = Length(written);
                await Waiting.ForAsync(TimeSpan.FromMilliseconds(600), "a fork that outlived the kill to write", cancel);
                long later = Length(written);
                Assert.True(later == length,
                    $"Round {round}: {later - length} bytes were written after the child was killed: a process its loops forked as it was killed outlived it");
            }
            finally
            {
                ChildProcesses.Kill(child);
                child.Dispose();
            }
        }
    }

    private static long Length(string file) => File.Exists(file) ? new FileInfo(file).Length : 0;
}
