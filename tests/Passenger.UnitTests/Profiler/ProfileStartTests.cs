using Passenger.Core;
using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>A profile whose program is named by a path that is not there is refused before anything of its run is made,
/// whether it samples the program or records a game's frames alone: it says so, starts nothing and leaves no snapshot.</summary>
public sealed class ProfileStartTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_program_named_by_a_path_not_there_is_refused_saying_so_and_leaves_no_snapshot(bool framesAlone)
    {
        string program = Path.Combine(root, "gone", "godot");
        string snapshots = Path.Combine(root, "snapshots");
        ProfileTarget target = new(program, ["--path", root], root, new Dictionary<string, string>()) { Called = "Lobby" };
        int pumped = 0;
        ProgramPump pump = (_, _, _) =>
        {
            Interlocked.Increment(ref pumped);
            return Task.CompletedTask;
        };

        ProfilingException refused = await Assert.ThrowsAsync<ProfilingException>(() => framesAlone
            ? ProfilingSession.StartFramesAsync(target, snapshots, pump)
            : ProfilingSession.StartAsync(target, snapshots, Path.Combine(root, "sockets"), pump, TestContext.Current.CancellationToken));

        Assert.Equal($"Could not profile Lobby: {program} does not exist", refused.Message);
        Assert.True(Snapshots.List(snapshots).Count == 0,
            $"A profile of a program not there left {string.Join(", ", Snapshots.List(snapshots).Select(s => s.Name))} among the snapshots, where it recorded nothing");
        Assert.True(pumped == 0, "A profile of a program not there read a program's output, where none started");
    }
}
