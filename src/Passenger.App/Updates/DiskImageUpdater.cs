using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App.Updates;

/// <summary>macOS's update, from the disk image a person downloads (Passenger-macos-arm64.dmg or -x64), into the state
/// folder's <c>updates/</c>: its SHA-256 checked against the release's SHA256SUMS, its Passenger.app copied out and
/// checked again (bundle, version, signature, and the running app's team once there is one). As the process ends the
/// two apps trade places in one step where the volume allows (renamex_np's swap), and Restart to Update starts the new
/// one through LaunchServices: nothing of the update runs after Passenger has gone.</summary>
[SupportedOSPlatform("macos")]
public sealed partial class DiskImageUpdater : Updater
{
    /// <summary>The app at the image's top, as tools/package-mac.sh makes it.</summary>
    private const string AppName = "Passenger.app";

    /// <summary>The app made ready, named so LaunchServices never takes it for an app to open until it is in place.</summary>
    private const string Staged = "Passenger.update";

    private const string Sums = "SHA256SUMS";

    private static readonly TimeSpan ToolLimit = TimeSpan.FromMinutes(10);

    private readonly Func<string, Task> start;

    /// <param name="bundle">The installed Passenger.app.</param>
    /// <param name="staging">Where downloads are made ready: the state folder's <c>updates/</c>.</param>
    /// <param name="start">How the installed app is started again by Restart to Update: LaunchServices' open by
    /// default.</param>
    public DiskImageUpdater(string bundle, string staging, Func<string, Task>? start = null)
    {
        Bundle = bundle;
        Staging = staging;
        this.start = start ?? Open;
    }

    /// <summary>The installed Passenger.app this copy runs from.</summary>
    public string Bundle { get; }

    /// <summary>The folder downloads are made ready in.</summary>
    public string Staging { get; }

    public override string Installed => Bundle;

    /// <summary>The running copy's updater: the app bundle its executable is in, when this user can replace it.</summary>
    internal static DiskImageUpdater? OfThisBundle(string staging, out string why)
    {
        string? executable = Environment.ProcessPath;
        string? macOS = Path.GetDirectoryName(executable);
        string? contents = Path.GetDirectoryName(macOS);
        string? bundle = Path.GetDirectoryName(contents);
        if (bundle is null || Path.GetFileName(macOS) != "MacOS" || Path.GetFileName(contents) != "Contents"
            || !bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            why = $"{executable} is not in an app bundle, as a build from source is not";
            return null;
        }
        if (bundle.Contains("/AppTranslocation/", StringComparison.Ordinal))
        {
            why = $"macOS runs it from a copy of its own ({bundle}): moved to Applications, it updates itself";
            return null;
        }
        string parent = Path.GetDirectoryName(bundle)!;
        if (Access(parent, Writable) != 0 || Access(bundle, Writable) != 0)
        {
            why = $"this user cannot replace {bundle}: its folder is read-only (a disk image) or another user's";
            return null;
        }
        why = string.Empty;
        return new DiskImageUpdater(bundle, staging);
    }

    protected override async Task<UpdateOutcome> PrepareAsync(LatestRelease release, Action<int> progress, CancellationToken cancel)
    {
        string? image = Releases.ThisSystemsAsset();
        ReleaseFile? download = image is null ? null : release.File(image);
        ReleaseFile? sums = release.File(Sums);
        if (download is null || sums is null)
        {
            string missing = download is null ? image ?? "disk image for this processor" : Sums;
            return new UpdateOutcome.Unavailable($"Passenger {release.Version} is only announced: its release carries no {missing}");
        }
        string folder = Path.Combine(Staging, release.Version);
        string app = Path.Combine(folder, Staged);
        // Not held: removing old copies is the disk's work, which nothing drawn waits for.
        await BackgroundWork.Unheld(() => CleanAsync(keep: release.Version), cancel).ConfigureAwait(false);
        string identifier = await PlistAsync(Bundle, "CFBundleIdentifier", cancel).ConfigureAwait(false)
            ?? throw new UpdateFailedException($"{Bundle} names no bundle identifier");
        string? team = await TeamAsync(Bundle, cancel).ConfigureAwait(false);
        if (Directory.Exists(app))
        {
            // Made ready by an earlier start: checked again rather than downloaded again.
            try
            {
                await CheckAsync(app, identifier, release.Version, team, cancel).ConfigureAwait(false);
                return new UpdateOutcome.Ready($"Passenger {release.Version} was downloaded before, to {app}, and its app checked again{Signed(team)}");
            }
            catch (UpdateRefusedException)
            {
                // Not held: removing old copies is the disk's work, which nothing drawn waits for.
                await BackgroundWork.Unheld(() => CleanAsync(keep: null), cancel).ConfigureAwait(false);
            }
        }
        Directory.CreateDirectory(folder);
        string expected = Expected(await Releases.ReadTextAsync(sums, Product.Version, cancel).ConfigureAwait(false), download.Name)
            ?? throw new UpdateRefusedException($"the release's {Sums} gives no SHA-256 for {download.Name}");
        string saved = Path.Combine(folder, download.Name);
        string actual = await Releases.DownloadAsync(download, saved, Product.Version, progress, cancel).ConfigureAwait(false);
        if (actual != expected)
        {
            throw new UpdateRefusedException($"{download.Name} does not match the SHA-256 the release's {Sums} gives", $"it is {actual}; {Sums} says {expected}");
        }
        await CopyOutAsync(saved, folder, app, cancel).ConfigureAwait(false);
        File.Delete(saved);
        await CheckAsync(app, identifier, release.Version, team, cancel).ConfigureAwait(false);
        return new UpdateOutcome.Ready($"Passenger {release.Version} downloaded to {app}: {download.Name}'s SHA-256 is {Sums}' {expected}, and the app's signature holds{Signed(team)}");
    }

    public override void Install(bool restart)
    {
        if (Ready is not { } version)
        {
            return;
        }
        string app = Path.Combine(Staging, version, Staged);
        if (!Directory.Exists(app))
        {
            IdeErrors.Write($"Updates: Passenger {version} was not installed: {app} is gone");
            return;
        }
        IReadOnlyList<OtherCopy> others = Others();
        if (others.Any(other => !other.Relay))
        {
            IdeErrors.Write($"Updates: Passenger {version} waits to install: another Passenger runs from {Bundle}: {Listed(others.Where(other => !other.Relay))}");
            return;
        }
        EndRelays(others, version);
        (string? why, string? old) = Replace(app, version);
        if (why is not null)
        {
            IdeErrors.Write($"Updates: Passenger {version} was not installed: {app} could not take the place of {Bundle}: {why}");
            Restart(restart);
            IdeErrors.Flush();
            return;
        }
        // LaunchServices notices a bundle that changed by its date.
        Directory.SetLastWriteTimeUtc(Bundle, DateTime.UtcNow);
        IdeErrors.Write($"Updates: Passenger {version} installed in {Bundle}{(restart ? ", and started" : string.Empty)}");
        Restart(restart);
        Remove(old);
        Ready = null;
        // As the process ends, after its last window: nothing is left to await it.
        Synchronously.Run(() => CleanAsync(keep: null));
        IdeErrors.Flush();
    }

    /// <summary>The new app takes the installed one's place: the two trade places in one step where the volume can swap
    /// folders (APFS), the app copied beside the installed one first when the state folder is on another volume, and on a
    /// volume that cannot swap (HFS+) in two renames, the old app put back should the second fail. Gives why not, or where
    /// the old app now is.</summary>
    private (string? Why, string? Old) Replace(string app, string version)
    {
        int failed = Swap(app, Bundle);
        if (failed == 0)
        {
            return (null, app);
        }
        if (failed is not (CrossDevice or NotSupported))
        {
            return (Marshal.GetPInvokeErrorMessage(failed), null);
        }
        string beside = Path.Combine(Path.GetDirectoryName(Bundle)!, $".{Path.GetFileName(Bundle)}.{version}");
        Remove(beside);
        ChildResult copied = Run("ditto", [app, beside]);
        if (!copied.Succeeded)
        {
            Remove(beside);
            return ($"it could not be copied beside it: {Said(copied)}", null);
        }
        failed = Swap(beside, Bundle);
        if (failed == 0)
        {
            return (null, beside);
        }
        if (failed != NotSupported)
        {
            Remove(beside);
            return (Marshal.GetPInvokeErrorMessage(failed), null);
        }
        string old = beside + ".old";
        try
        {
            Directory.Move(Bundle, old);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Remove(beside);
            return (error.Message, null);
        }
        try
        {
            Directory.Move(beside, Bundle);
            return (null, old);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Directory.Move(old, Bundle);
            Remove(beside);
            return (error.Message, null);
        }
    }

    public override async Task DiscardAsync()
    {
        Ready = null;
        await CleanAsync(keep: null).ConfigureAwait(false);
    }

    protected override IReadOnlyList<int> RunningHere()
    {
        // As the system names a process's executable, links resolved.
        string executable = PathNames.Canonical(Path.Combine(Bundle, "Contents", "MacOS", "Passenger"));
        List<int> here = [];
        foreach (Process running in Process.GetProcessesByName("Passenger"))
        {
            using (running)
            {
                if (running.Id != Environment.ProcessId && ExecutableOf(running.Id) is { } path && PathNames.Same(path, executable))
                {
                    here.Add(running.Id);
                }
            }
        }
        return here;
    }

    /// <summary>The app at the image's top, copied out of it mounted read-only (hdiutil checks the image's own checksums
    /// as it mounts it), and the image let go of, a failure included.</summary>
    private static async Task CopyOutAsync(string image, string folder, string app, CancellationToken cancel)
    {
        string mount = Path.Combine(folder, "mount");
        Directory.CreateDirectory(mount);
        ChildResult attached = await RunAsync("hdiutil", ["attach", image, "-readonly", "-nobrowse", "-noautoopen", "-mountpoint", mount], cancel).ConfigureAwait(false);
        if (!attached.Succeeded)
        {
            throw new UpdateRefusedException($"{Path.GetFileName(image)} does not open as a disk image", Said(attached));
        }
        try
        {
            string inside = Path.Combine(mount, AppName);
            if (!Directory.Exists(inside))
            {
                throw new UpdateRefusedException($"{Path.GetFileName(image)} holds no {AppName}");
            }
            ChildResult copied = await RunAsync("ditto", [inside, app], cancel).ConfigureAwait(false);
            if (!copied.Succeeded)
            {
                throw new UpdateFailedException($"{AppName} could not be copied out of {Path.GetFileName(image)}: {Said(copied)}");
            }
        }
        finally
        {
            await DetachAsync(mount).ConfigureAwait(false);
        }
    }

    /// <summary>The app made ready is the release's: the installed app's bundle, the release's version, its signature
    /// whole, and the installed app's team's when that has one.</summary>
    private static async Task CheckAsync(string app, string identifier, string version, string? team, CancellationToken cancel)
    {
        string? id = await PlistAsync(app, "CFBundleIdentifier", cancel).ConfigureAwait(false);
        string? named = await PlistAsync(app, "CFBundleShortVersionString", cancel).ConfigureAwait(false);
        if (id != identifier || named != version)
        {
            throw new UpdateRefusedException($"the disk image's {AppName} is {id ?? "no bundle"} {named ?? "of no version"}, not {identifier} {version}");
        }
        List<string> verify = ["--verify", "--deep", "--strict"];
        if (team is not null)
        {
            verify.AddRange(["-R", $"=anchor apple generic and certificate leaf[subject.OU] = \"{team}\""]);
        }
        ChildResult verified = await RunAsync("codesign", [.. verify, app], cancel).ConfigureAwait(false);
        if (!verified.Succeeded)
        {
            throw new UpdateRefusedException($"the signature of the disk image's {AppName} does not hold{(team is null ? string.Empty : $" for {team}")}", Said(verified));
        }
    }

    /// <summary>The team a Developer ID signed the app for, or null when it is signed ad hoc.</summary>
    private static async Task<string?> TeamAsync(string app, CancellationToken cancel)
    {
        ChildResult shown = await RunAsync("codesign", ["-dv", app], cancel).ConfigureAwait(false);
        return Team().Match(shown.Error) is { Success: true } match && match.Groups[1].Value != "not set" ? match.Groups[1].Value : null;
    }

    private static async Task<string?> PlistAsync(string app, string key, CancellationToken cancel)
    {
        ChildResult read = await RunAsync("plutil", ["-extract", key, "raw", "-o", "-", Path.Combine(app, "Contents", "Info.plist")], cancel).ConfigureAwait(false);
        return read.Succeeded ? read.Output.Trim() : null;
    }

    /// <summary>The SHA-256 SHA256SUMS gives a file, in lowercase hex: sha256sum's lines, text or binary mode.</summary>
    private static string? Expected(string sums, string name) =>
        sums.Split('\n').Select(line => SumLine().Match(line.TrimEnd('\r'))).FirstOrDefault(m => m.Success && m.Groups[2].Value == name)
            ?.Groups[1].Value.ToLowerInvariant();

    /// <summary>Every download made ready but <paramref name="keep"/>'s, an image still mounted let go of first.</summary>
    private async Task CleanAsync(string? keep)
    {
        if (!Directory.Exists(Staging))
        {
            return;
        }
        foreach (string folder in Directory.EnumerateDirectories(Staging))
        {
            if (Path.GetFileName(folder) == keep)
            {
                continue;
            }
            if (Directory.Exists(Path.Combine(folder, "mount")))
            {
                await DetachAsync(Path.Combine(folder, "mount")).ConfigureAwait(false);
            }
            Remove(folder);
        }
    }

    private static async Task DetachAsync(string mount)
    {
        if (!(await ToolAsync("hdiutil", ["detach", mount, "-quiet"]).ConfigureAwait(false)).Succeeded)
        {
            await ToolAsync("hdiutil", ["detach", mount, "-force", "-quiet"]).ConfigureAwait(false);
        }
        try
        {
            Directory.Delete(mount);
        }
        catch (IOException)
        {
            // Still mounted, or holding something: the next clean tries again.
        }
    }

    private static void Remove(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Updates: {folder} could not be removed: {error.Message}");
        }
    }

    private void Restart(bool restart)
    {
        if (!restart)
        {
            return;
        }
        try
        {
            // As the process ends, after its last window: nothing is left to await it.
            Synchronously.Run(() => start(Bundle));
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            IdeErrors.Write($"Updates: {Bundle} could not be started again: {error.Message}");
        }
    }

    /// <summary>LaunchServices starts the app as a new instance (this one is still ending), with the state folder, release
    /// feed and Discord this one was given, so a Passenger run on its own settings stays on them. The app is launchd's
    /// child, not Passenger's; open itself ends once it has asked.</summary>
    private static async Task Open(string bundle)
    {
        List<string> arguments = ["-n"];
        foreach (string variable in (string[])[State.StateStore.Variable, ReleaseFeed.Variable, Discord.DiscordIpc.Variable])
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                arguments.AddRange(["--env", $"{variable}={value}"]);
            }
        }
        ChildResult opened = await ChildProcesses.RunAsync(ChildProcesses.Command("/usr/bin/open", [.. arguments, bundle]), limit: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (!opened.Succeeded)
        {
            throw new InvalidOperationException($"open {bundle}: {Said(opened)}");
        }
    }

    private static Task<ChildResult> RunAsync(string tool, IEnumerable<string> arguments, CancellationToken cancel) =>
        ChildProcesses.RunAsync(ChildProcesses.Command(Path.Combine("/usr/bin", tool), arguments), limit: ToolLimit, cancel: cancel);

    /// <summary>A tool waited for where nothing awaits: the install, as the process ends.</summary>
    private static ChildResult Run(string tool, IEnumerable<string> arguments) => Synchronously.Run(() => ToolAsync(tool, arguments));

    /// <summary>A tool run where nothing can be given up: one that could not start or finish is an exit code of -1 saying why.</summary>
    private static async Task<ChildResult> ToolAsync(string tool, IEnumerable<string> arguments)
    {
        try
        {
            return await RunAsync(tool, arguments, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or System.ComponentModel.Win32Exception)
        {
            return new ChildResult(-1, string.Empty, error.Message);
        }
    }

    private static string Said(ChildResult result) =>
        (result.Error.Trim() is { Length: > 0 } error ? error : result.Output.Trim()) is { Length: > 0 } said ? said : $"exit code {result.ExitCode}";

    private static string Signed(string? team) => team is null ? " (signed ad hoc, as the installed app is)" : $" and is {team}'s";

    /// <summary>The two bundles trade places; 0, or the error.</summary>
    private static int Swap(string from, string to) => RenameEx(from, to, RenameSwap) == 0 ? 0 : Marshal.GetLastPInvokeError();

    private static string? ExecutableOf(int process)
    {
        byte[] path = new byte[4096];
        int length = ProcessPath(process, path, (uint)path.Length);
        return length > 0 ? System.Text.Encoding.UTF8.GetString(path, 0, length) : null;
    }

    private const uint RenameSwap = 0x2;

    /// <summary>EXDEV: the two are on different volumes.</summary>
    private const int CrossDevice = 18;

    /// <summary>ENOTSUP: the volume cannot swap two folders (HFS+), or not across volumes.</summary>
    private const int NotSupported = 45;

    private const int Writable = 2;

    [LibraryImport("libc", EntryPoint = "renamex_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameEx(string from, string to, uint flags);

    [LibraryImport("libc", EntryPoint = "access", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Access(string path, int mode);

    [LibraryImport("libc", EntryPoint = "proc_pidpath")]
    private static partial int ProcessPath(int process, [Out] byte[] buffer, uint size);

    [GeneratedRegex(@"^TeamIdentifier=(.+)$", RegexOptions.Multiline)]
    private static partial Regex Team();

    [GeneratedRegex(@"^([0-9A-Fa-f]{64}) [ *](.+)$")]
    private static partial Regex SumLine();
}
