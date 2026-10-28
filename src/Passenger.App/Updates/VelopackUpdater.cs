using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Passenger.Core;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace Passenger.App.Updates;

/// <summary>Windows' update, through Velopack as the installer is: the full package the release's feed names for this
/// install's channel (<c>releases.win-x64.json</c> or <c>-arm64</c>), downloaded into <c>packages\</c> and checked
/// against its size and SHA-256. As Passenger ends, Velopack's Update.exe replaces the install and, for Restart to
/// Update, starts the new version; it outlives Passenger, so it is not ChildProcesses' to start.</summary>
public sealed class VelopackUpdater(IVelopackLocator locator) : Updater
{
    private UpdateManager? manager;
    private VelopackAsset? package;

    public override string Installed => locator.RootAppDir ?? AppContext.BaseDirectory;

    protected override async Task<UpdateOutcome> PrepareAsync(LatestRelease release, Action<int> progress, CancellationToken cancel)
    {
        manager = null;
        package = null;
        // tools/package-win.sh packs each system as a channel of its own, named by its runtime.
        string feed = $"releases.{locator.Channel ?? "win"}.json";
        if (release.File(feed) is null)
        {
            return new UpdateOutcome.Unavailable($"Passenger {release.Version} is only announced: its release carries no {feed}");
        }
        UpdateManager updates = new(new ReleaseSource(release, feed, cancel), new UpdateOptions(), locator);
        UpdateInfo? found = await updates.CheckForUpdatesAsync().ConfigureAwait(false);
        if (found is null)
        {
            return new UpdateOutcome.Unavailable($"Passenger {release.Version} is only announced: its {feed} offers nothing newer than {locator.CurrentlyInstalledVersion}");
        }
        VelopackAsset target = found.TargetFullRelease;
        if (target.Version.ToString() != release.Version)
        {
            throw new UpdateRefusedException($"the release's {feed} offers {target.Version}, not {release.Version} as its tag says");
        }
        // Velopack takes a package already there as downloaded; one left from before is checked again instead.
        string local = Path.Combine(locator.PackagesDir!, target.FileName);
        if (File.Exists(local) && !string.Equals(await Sha256Async(local, cancel).ConfigureAwait(false), target.SHA256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(local);
        }
        try
        {
            await updates.DownloadUpdatesAsync(found, progress, cancel).ConfigureAwait(false);
        }
        catch (ChecksumFailedException failed)
        {
            string why = failed.Message.EndsWith($" ({failed.FilePath})", StringComparison.Ordinal) ? failed.Message[..^(failed.FilePath.Length + 3)] : failed.Message;
            throw new UpdateRefusedException($"{target.FileName} does not match the size and SHA-256 the release's {feed} gives", why);
        }
        manager = updates;
        package = target;
        return new UpdateOutcome.Ready($"Passenger {release.Version} downloaded to {local}: its size and SHA-256 are {feed}'s {target.SHA256}");
    }

    public override void Install(bool restart)
    {
        if (manager is null || package is null || Ready is not { } version)
        {
            return;
        }
        IReadOnlyList<OtherCopy> others = Others();
        if (others.Any(other => !other.Relay))
        {
            IdeErrors.Write($"Updates: Passenger {version} waits to install: another Passenger runs from {locator.AppContentDir}: {Listed(others.Where(other => !other.Relay))}");
            return;
        }
        // Update.exe would find their files in use.
        EndRelays(others, version);
        try
        {
            // Quietly when Passenger is only closing; with Velopack's progress while a person waits for it to start again.
            manager.WaitExitThenApplyUpdates(package, silent: !restart, restart: restart);
            IdeErrors.Write($"Updates: Update.exe installs Passenger {version} in {Installed} once Passenger has ended{(restart ? ", and starts it" : string.Empty)}");
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException)
        {
            IdeErrors.Write($"Updates: Passenger {version} was not installed: Update.exe did not start: {error.Message}");
        }
        IdeErrors.Flush();
    }

    public override Task DiscardAsync()
    {
        Ready = null;
        manager = null;
        package = null;
        if (locator.PackagesDir is not { } packages || !Directory.Exists(packages))
        {
            return Task.CompletedTask;
        }
        // The package the installed version came with stays, as Velopack keeps it.
        foreach (VelopackAsset newer in locator.GetLocalPackages().Where(p => p.Version > locator.CurrentlyInstalledVersion))
        {
            Delete(Path.Combine(packages, newer.FileName));
        }
        foreach (string partial in Directory.EnumerateFiles(packages, "*.partial"))
        {
            Delete(partial);
        }
        return Task.CompletedTask;
    }

    protected override IReadOnlyList<int> RunningHere()
    {
        if (locator.AppContentDir is not { } content)
        {
            return [];
        }
        List<int> here = [];
        foreach (Process running in Process.GetProcessesByName("Passenger"))
        {
            using (running)
            {
                try
                {
                    if (running.Id != Environment.ProcessId && running.MainModule?.FileName is { } path && PathNames.IsUnder(path, content))
                    {
                        here.Add(running.Id);
                    }
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                {
                    // Another user's, or ended meanwhile.
                }
            }
        }
        return here;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancel)
    {
        await using FileStream file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancel).ConfigureAwait(false));
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Updates: {path} could not be removed: {error.Message}");
        }
    }

    /// <summary>The release the check found, as Velopack's source of updates: its feed for this channel and the full
    /// packages that feed names, each downloaded from the release by its name over HTTPS, with nothing in the request
    /// but Passenger's name and version. A feed that gives a package no SHA-256 is refused, so Velopack never settles
    /// for its SHA-1.</summary>
    private sealed class ReleaseSource(LatestRelease release, string feed, CancellationToken cancel) : IUpdateSource
    {
        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            string json = await Releases.ReadTextAsync(release.File(feed)!, Product.Version, cancel).ConfigureAwait(false);
            VelopackAssetFeed parsed;
            try
            {
                parsed = VelopackAssetFeed.FromJson(json);
            }
            catch (System.Text.Json.JsonException error)
            {
                throw new UpdateRefusedException($"the release's {feed} is not an update feed", error.Message);
            }
            // Full packages alone: a package Velopack patches together from deltas is not checked against a SHA-256 of its own.
            VelopackAsset[] full = [.. parsed.Assets.Where(a => a.Type == VelopackAssetType.Full)];
            if (full.FirstOrDefault(a => string.IsNullOrWhiteSpace(a.SHA256)) is { } unhashed)
            {
                throw new UpdateRefusedException($"the release's {feed} gives no SHA-256 for {unhashed.FileName}");
            }
            return parsed with { Assets = full };
        }

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
        {
            ReleaseFile file = release.File(releaseEntry.FileName)
                ?? throw new UpdateRefusedException($"the release's {feed} names {releaseEntry.FileName}, which the release does not carry");
            return Releases.DownloadAsync(file, localFile, Product.Version, progress, cancelToken);
        }
    }
}
