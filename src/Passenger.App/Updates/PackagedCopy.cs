namespace Passenger.App.Updates;

/// <summary>A Windows copy a package manager put in place from the release's zip (Chocolatey's package,
/// tools/chocolatey), told by the marker the package writes beside Passenger.exe: that manager alone updates it, so
/// this copy says a newer release is out, links its page, and downloads nothing.</summary>
public sealed class PackagedCopy(string folder) : Updater
{
    public override string Installed => folder;

    public override async Task<UpdatedElsewhere?> ElsewhereAsync(CancellationToken cancel)
    {
        PackageMarked marked = await MarkedAsync(folder, cancel).ConfigureAwait(false) ?? new PackageMarked("a package manager", null);
        string by = marked.Command is { } command ? $"{marked.Manager} updates it ({command})" : $"{marked.Manager} updates it";
        return new UpdatedElsewhere($"This copy of Passenger is a package {marked.Manager} installed: {by}.", Download: false);
    }

    protected override Task<UpdateOutcome> PrepareAsync(LatestRelease release, Action<int> progress, CancellationToken cancel) =>
        Task.FromResult<UpdateOutcome>(new UpdateOutcome.Unavailable($"Passenger {release.Version} is left to the package manager that installed {folder}"));

    public override void Install(bool restart)
    {
    }

    public override Task DiscardAsync() => Task.CompletedTask;

    protected override IReadOnlyList<int> RunningHere() => [];
}
