using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Passenger.App.Profiling;
using Passenger.Profiler;

namespace Passenger.App;

/// <summary>The window's profiler (<see cref="ProfilerCommands"/>), over its session: what agents ask of it, and the
/// toolbar's Profile button.</summary>
public partial class MainWindow
{
    private ProfilerCommands? profiler;

    /// <summary>The Performance Profiler tool window.</summary>
    public ProfilerView ProfilerView => profiler!.View;

    /// <summary>The run being profiled, the open folder's, or null.</summary>
    public ProfilingSession? Profiling => session.Profiling;

    /// <summary>Where the open folder's snapshots are kept; with no folder open, the store's own.</summary>
    public string SnapshotFolder => profiler!.SnapshotFolder;

    private void AddProfiler() => profiler = new ProfilerCommands(new ProfilerParts(() => session, () => buildRun, Dock, actions!, Bottom.Run,
        text => status.Text = text, () => StateFolder, PickSnapshotAsync, NavigateTo, NavigateTo, (process, control) => liveTree?.Reveal(process, control) == true, Editors,
        ChooseProfiledAsync, ProfiledInContainerAsync, ShowInFileManager, (title, text, yes) => Confirm(title, text, yes), ProfileAspireAsync));

    /// <summary>Profiles the chosen configuration as Profile does: null once it runs, else what stopped it.</summary>
    public Task<string?> ProfileAsync(string? arguments = null) => profiler!.ProfileAsync(arguments);

    /// <summary>Profiles a configuration not chosen in the toolbar (a gutter icon's one service) as Profile does.</summary>
    public Task<string?> ProfileAsync(Run.RunConfiguration configuration) => profiler!.ProfileAsync(configuration: configuration);

    /// <summary>Attach Profiler: the program being debugged (else the one Run runs), or a program in a running container
    /// (as Attach to Process lists it), profiled from now; null once it is, else why not.</summary>
    public Task<string?> AttachProfilerAsync(Passenger.Core.DotnetProcess? inContainer = null) => profiler!.AttachAsync(inContainer);

    /// <summary>Get Snapshot: the run ended and its snapshot read, or why there is none.</summary>
    public Task<SnapshotRead> SnapshotAsync() => profiler!.SnapshotAsync();

    /// <summary>Get Memory Snapshot of the program being profiled; one given up by <paramref name="cancel"/> (an agent's) is
    /// said so, and the tools taking it in a container are ended.</summary>
    public Task<MemorySnapshot?> MemorySnapshotAsync(CancellationToken cancel = default) => profiler!.MemorySnapshotAsync(cancel);

    /// <summary>An agent's Get Memory Snapshot, asking no one, with why there is none, or what taking it put into the
    /// program's output (one only the runtime could write).</summary>
    public Task<(MemorySnapshot? Heap, string? Note)> MemorySnapshotUnaskedAsync(CancellationToken cancel) => profiler!.MemorySnapshotAsync(ask: false, cancel);

    /// <summary>Opens a snapshot written before in the profiler window.</summary>
    public Task<Snapshot?> OpenSnapshotAsync(string path) => profiler!.OpenSnapshotAsync(path);

    private Task KeepSnapshotsAsync() => profiler!.KeepSnapshotsAsync();

    /// <summary>The system's panel, for a .nettrace file to open.</summary>
    private async Task<string?> PickSnapshotAsync()
    {
        IReadOnlyList<IStorageFile> picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Snapshot",
            FileTypeFilter = [new FilePickerFileType("EventPipe traces") { Patterns = ["*.nettrace"] }],
        });
        return picked.Count == 1 ? picked[0].TryGetLocalPath() : null;
    }
}
