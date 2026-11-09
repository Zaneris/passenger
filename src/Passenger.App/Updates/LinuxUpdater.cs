using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Passenger.Core;
using Passenger.Terminal;

namespace Passenger.App.Updates;

/// <summary>Linux's update, of a Passenger the archive's install.sh installed (Passenger-linux-x64.tar.gz or -arm64):
/// downloaded into the state folder's <c>updates/</c>, its SHA-256 checked against the release's SHA256SUMS, unpacked and
/// checked again (its layout and version). One person's install (~/.local/opt/passenger) has it unpacked beside it
/// (<see cref="UpdateFolders"/>); as the process ends the two folders trade places in one step, the release replaced kept
/// beside until the new one starts, and the new install.sh refreshes the menu entry, icons and command as it installs
/// them. An install for everyone (/opt/passenger) is put in place by its own install.sh, as root through pkexec or by a
/// command the person is given, which keeps the release replaced in the update's folder in the state until the new one
/// starts, as a person's does; a copy a package manager installed is left to it.</summary>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxUpdater : Updater
{
    /// <summary>Where install.sh installs for everyone, with <c>--system</c>.</summary>
    public const string SystemFolder = "/opt/passenger";

    /// <summary>The file a package puts in the app's folder to say a package manager installed it, naming that manager.</summary>
    public const string PackageMarker = "package-manager";

    /// <summary>How long install.sh may take, an administrator's password asked for included.</summary>
    private static readonly TimeSpan ScriptLimit = TimeSpan.FromMinutes(10);

    /// <summary>How long a package manager may take to say whether it owns the app.</summary>
    private static readonly TimeSpan AskLimit = TimeSpan.FromSeconds(20);

    private readonly InstallKind kind;
    private readonly bool mayAsk;
    private readonly string running;
    private readonly IReadOnlyDictionary<string, string?> environment;
    private readonly Func<string, Task> start;
    private Task<UpdatedElsewhere?>? elsewhere;

    /// <summary>pkexec, found as the update was made ready, by which Restart to Update installs it as root; or null.</summary>
    private string? asker;

    /// <summary>The updater of the installed passenger/ folder <paramref name="install"/>, of the <paramref name="running"/>
    /// version (this build's by default); <paramref name="mayAsk"/> whether one this user cannot write asks for a password
    /// through pkexec, <paramref name="environment"/> what install.sh is given beside this process's own variables (a
    /// test's HOME), <paramref name="start"/> how Restart to Update starts it (its launcher, apart, by default).</summary>
    public LinuxUpdater(string install, string staging, InstallKind kind, bool mayAsk = true, string? running = null,
        IReadOnlyDictionary<string, string?>? environment = null, Func<string, Task>? start = null)
    {
        Folder = install;
        Staging = staging;
        this.kind = kind;
        this.mayAsk = mayAsk;
        this.running = running ?? Product.Version;
        this.environment = environment ?? new Dictionary<string, string?>();
        this.start = start ?? Launch;
    }

    /// <summary>Whose install a copy is, by where install.sh put it.</summary>
    public enum InstallKind
    {
        /// <summary>Run where its archive was unpacked, or anywhere else install.sh does not install.</summary>
        Unpacked,

        /// <summary>One person's, in ~/.local/opt/passenger.</summary>
        PerUser,

        /// <summary>Everyone's, in /opt/passenger, with <c>--system</c>.</summary>
        System,
    }

    /// <summary>The installed passenger/ folder this copy runs from.</summary>
    public string Folder { get; }

    /// <summary>The folder downloads are made ready in.</summary>
    public string Staging { get; }

    public override string Installed => Folder;

    public override string? Asks => kind == InstallKind.System && asker is not null && !Writable()
        ? $"Installing it in {Folder} asks for an administrator's password" : null;

    /// <summary>The running copy's updater: the archive's passenger/ folder its executable's app/ folder is in.</summary>
    internal static LinuxUpdater? OfThisInstall(string staging, out string why)
    {
        string? executable = Environment.ProcessPath;
        string? app = Path.GetDirectoryName(executable);
        if (executable is null || app is null || Path.GetFileName(app) != "app" || Path.GetDirectoryName(app) is not { } install)
        {
            why = $"{executable} is not in the app folder of an unpacked Passenger-linux archive, as a build from source is not";
            return null;
        }
        string? home = Environment.GetEnvironmentVariable("HOME");
        InstallKind kind = PathNames.Same(install, SystemFolder) ? InstallKind.System
            : home is { Length: > 0 } && PathNames.Same(install, PathNames.Canonical(Path.Combine(home, ".local", "opt", "passenger"))) ? InstallKind.PerUser
            : InstallKind.Unpacked;
        why = string.Empty;
        return new LinuxUpdater(install, staging, kind);
    }

    public override Task<UpdatedElsewhere?> ElsewhereAsync(CancellationToken cancel)
    {
        // Found once, unless a window closing cut the finding short.
        if (elsewhere is null || elsewhere.IsCanceled || elsewhere.IsFaulted)
        {
            elsewhere = FindElsewhereAsync(cancel);
        }
        return elsewhere;
    }

    private async Task<UpdatedElsewhere?> FindElsewhereAsync(CancellationToken cancel)
    {
        string executable = Path.Combine(Folder, "app", "Passenger");
        if (await PackageOfAsync(executable, cancel).ConfigureAwait(false) is { } package)
        {
            return new UpdatedElsewhere($"This copy of Passenger is {package}: the system's package manager updates it.", Download: false);
        }
        if (kind == InstallKind.Unpacked || !File.Exists(Path.Combine(Folder, "install.sh")))
        {
            return new UpdatedElsewhere($"This copy of Passenger runs from {Folder}, where its archive was unpacked, so it is updated by hand: "
                + "a newer archive unpacked in its place, or this one installed by its install.sh, after which it updates itself.", Download: true);
        }
        return null;
    }

    /// <summary>The package a package manager installed the app from, as the person would name it, or null: the marker a
    /// package writes beside the app, else the answer of each package database on the PATH for the app's path.</summary>
    private static async Task<string?> PackageOfAsync(string executable, CancellationToken cancel)
    {
        string marker = Path.Combine(Path.GetDirectoryName(executable)!, PackageMarker);
        if (File.Exists(marker))
        {
            string manager = (await File.ReadAllTextAsync(marker, cancel).ConfigureAwait(false)).Split('\n')[0].Trim();
            return $"a package {(manager.Length > 0 ? manager : "a package manager")} installed";
        }
        (string Tool, string Manager, string[] Arguments)[] databases =
        [
            ("dpkg-query", "dpkg", ["-S", executable]),
            ("rpm", "rpm", ["-qf", "--queryformat", "%{NAME}", executable]),
            ("pacman", "pacman", ["-Qqo", executable]),
        ];
        foreach ((string tool, string manager, string[] arguments) in databases)
        {
            if (Programs.OnPath(tool) is not { } found)
            {
                continue;
            }
            ChildResult owner = await ToolAsync(ChildProcesses.Command(found, arguments), AskLimit, cancel).ConfigureAwait(false);
            // dpkg-query says "package: path", rpm and pacman the package alone.
            if (owner.Succeeded && owner.Output.Split('\n')[0].Trim() is { Length: > 0 } line)
            {
                string name = tool == "dpkg-query" ? line.Split(':')[0].Trim() : line;
                return $"{manager}'s package {name}";
            }
        }
        return null;
    }

    protected override async Task<UpdateOutcome> PrepareAsync(LatestRelease release, Action<int> progress, CancellationToken cancel)
    {
        asker = null;
        if (PackageOf(release) is not { } package)
        {
            return Announced(release, "archive for this processor");
        }
        string unpacked = UnpackedFolder(release.Version);
        string passenger = Path.Combine(unpacked, "passenger");
        // Not held: removing old copies is the disk's work, which nothing drawn waits for.
        await BackgroundWork.Unheld(() => Clean(keep: release.Version), cancel).ConfigureAwait(false);
        if (Directory.Exists(passenger))
        {
            // Made ready by an earlier start: checked again rather than downloaded again.
            try
            {
                Check(passenger, release.Version);
                return await MadeAsync(release.Version, passenger, $"Passenger {release.Version} was unpacked before, to {passenger}, and checked again", cancel).ConfigureAwait(false);
            }
            catch (UpdateRefusedException)
            {
                Remove(unpacked);
            }
        }
        string folder = Directory.CreateDirectory(Path.Combine(Staging, release.Version)).FullName;
        (string saved, string expected) = await DownloadCheckedAsync(package, folder, progress, cancel).ConfigureAwait(false);
        await UnpackAsync(saved, unpacked, cancel).ConfigureAwait(false);
        File.Delete(saved);
        Check(passenger, release.Version);
        return await MadeAsync(release.Version, passenger,
            $"Passenger {release.Version} downloaded to {saved} and unpacked to {passenger}: {package.Download.Name}'s SHA-256 is {ReleaseSums.Name}' {expected}", cancel).ConfigureAwait(false);
    }

    /// <summary>A release unpacked and checked: ready to install as Passenger ends, unless this user cannot write the
    /// install and nobody can be asked for an administrator's password, which leaves it to a command the person runs.</summary>
    private async Task<UpdateOutcome> MadeAsync(string version, string passenger, string said, CancellationToken cancel)
    {
        if (kind != InstallKind.System || Writable())
        {
            return new UpdateOutcome.Ready(said);
        }
        if (mayAsk && await BackgroundWork.Unheld(Pkexec, cancel).ConfigureAwait(false) is { } pkexec)
        {
            asker = pkexec;
            return new UpdateOutcome.Ready($"{said}; Restart to Update installs it in {Folder} as root through {pkexec}");
        }
        string command = Command(passenger);
        return new UpdateOutcome.ByHand(command, $"{said}; this user cannot write {Folder}, and no one can be asked for an administrator's password here, "
            + $"so it is installed by hand: {command}");
    }

    /// <summary>The command that installs a release unpacked here for everyone, as a person types it, the release it
    /// replaces kept for the new one's start to remove.</summary>
    public string Command(string passenger) =>
        $"sudo {ShellWords.Quoted(Path.Combine(Folder, "install.sh"), ShellKind.Posix)} --system --from {ShellWords.Quoted(passenger, ShellKind.Posix)} --keep-replaced";

    /// <summary>pkexec, where it is on the PATH and a desktop session is there to show its question; else null.</summary>
    private static string? Pkexec() =>
        (Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } || Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 })
            ? Programs.OnPath("pkexec")
            : null;

    /// <summary>Where a release is unpacked: beside one person's install, so the two can trade places in one step, else in
    /// the state's own folder for it, from which install.sh copies it.</summary>
    private string UnpackedFolder(string version) =>
        kind == InstallKind.System ? Path.Combine(Staging, version) : UpdateFolders.Beside(Folder, version);

    /// <summary>Unpacks the archive into a folder of its own, its passenger/ folder put in place only once all of it came
    /// out, so a folder an interrupted unpacking left is never taken for a release.</summary>
    private static async Task UnpackAsync(string archive, string unpacked, CancellationToken cancel)
    {
        // The folder may hold the archive itself, which stays.
        Remove(Path.Combine(unpacked, "passenger"));
        Remove(Path.Combine(unpacked, "partial"));
        string partial = Directory.CreateDirectory(Path.Combine(unpacked, "partial")).FullName;
        try
        {
            await using FileStream file = File.OpenRead(archive);
            await using GZipStream gzip = new(file, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, partial, overwriteFiles: false, cancel).ConfigureAwait(false);
        }
        catch (InvalidDataException error)
        {
            throw new UpdateRefusedException($"{Path.GetFileName(archive)} does not unpack as a gzipped tar archive", error.Message);
        }
        string[] top = [.. Directory.EnumerateFileSystemEntries(partial).Select(Path.GetFileName).OfType<string>()];
        if (!top.SequenceEqual(["passenger"]))
        {
            throw new UpdateRefusedException($"{Path.GetFileName(archive)} holds {(top.Length == 0 ? "nothing" : string.Join(", ", top))}, not one passenger folder");
        }
        Directory.Move(Path.Combine(partial, "passenger"), Path.Combine(unpacked, "passenger"));
        Directory.Delete(partial);
    }

    /// <summary>The release unpacked is the release's: install.sh's layout, its launcher, script and app executable, and
    /// the version its app's deps.json gives Passenger.</summary>
    private static void Check(string passenger, string version)
    {
        foreach (string part in (string[])["passenger", "install.sh", "app/Passenger"])
        {
            string path = Path.Combine(passenger, part);
            if (!File.Exists(path) || !File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute))
            {
                throw new UpdateRefusedException($"the archive's passenger folder has no executable {part}");
            }
        }
        if (!File.Exists(Path.Combine(passenger, "io.github.zaneris.passenger.desktop")))
        {
            throw new UpdateRefusedException("the archive's passenger folder has no desktop entry");
        }
        string deps = Path.Combine(passenger, "app", "Passenger.deps.json");
        string? named = File.Exists(deps) ? UpdateFolders.AppVersion(File.ReadAllText(deps)) : null;
        if (named != version)
        {
            throw new UpdateRefusedException($"the archive's Passenger is {(named is null ? "of no version" : named)}, not {version}");
        }
    }

    public override void Install(bool restart)
    {
        try
        {
            InstallReady(restart);
        }
        finally
        {
            LetRelaysGo();
        }
    }

    private void InstallReady(bool restart)
    {
        NotInstalled = null;
        if (Ready is not { } version)
        {
            return;
        }
        if (Asks is not null && !restart)
        {
            IdeErrors.Write($"Updates: Passenger {version} stays ready: installing it asks for an administrator's password, which only Restart to Update asks for");
            return;
        }
        string passenger = Path.Combine(UnpackedFolder(version), "passenger");
        if (!Directory.Exists(passenger))
        {
            NotInstalled = $"{passenger} is gone";
            IdeErrors.Write($"Updates: Passenger {version} was not installed: {NotInstalled}");
            return;
        }
        IReadOnlyList<OtherCopy> others = Others();
        if (WaitsFor(others, version, Folder) is { } named)
        {
            NotInstalled = $"another Passenger ran from {Folder} ({named})";
            return;
        }
        FollowRelays(others, version);
        string? why = kind == InstallKind.System ? InstallForEveryone(passenger) : Swap(passenger);
        // The release is in place, or the old one still is: either way the relays start again from it.
        LetRelaysGo();
        if (why is not null)
        {
            NotInstalled = why;
            IdeErrors.Write($"Updates: Passenger {version} was not installed in {Folder}: {why}");
            Restart(restart, start);
            IdeErrors.Flush();
            return;
        }
        if (kind != InstallKind.System)
        {
            Refresh();
        }
        IdeErrors.Write($"Updates: Passenger {version} installed in {Folder}{(restart ? ", and started" : string.Empty)}");
        Restart(restart, start);
        Ready = null;
        // An install for everyone left the release it replaced there, for the new one's start to remove (TidyAsync).
        if (kind != InstallKind.System)
        {
            Remove(Path.Combine(Staging, version));
        }
        IdeErrors.Flush();
    }

    /// <summary>The release unpacked beside the install and the install trade places in one step, where the file system
    /// can exchange two folders (renameat2's RENAME_EXCHANGE), else in two renames, the install put back should the second
    /// fail; the release replaced is left beside, where the unpacked one was. Gives why not.</summary>
    private string? Swap(string passenger)
    {
        int failed;
        try
        {
            failed = RenameAt(WorkingFolder, passenger, WorkingFolder, Folder, RenameExchange) == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        catch (EntryPointNotFoundException)
        {
            failed = NotImplemented;
        }
        if (failed == 0)
        {
            return null;
        }
        if (failed is not (InvalidArgument or NotImplemented))
        {
            return Marshal.GetPInvokeErrorMessage(failed);
        }
        string replaced = Path.Combine(Path.GetDirectoryName(passenger)!, "replaced");
        try
        {
            Directory.Move(Folder, replaced);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
        try
        {
            Directory.Move(passenger, Folder);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                Directory.Move(replaced, Folder);
                return error.Message;
            }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException)
            {
                return $"{error.Message}, and the installed release could not be put back ({back.Message}): it is in {replaced}";
            }
        }
    }

    /// <summary>The install for everyone put in place by its own install.sh, from the release unpacked here: as root
    /// through pkexec, or itself where this user can write the install (root), the release it replaces kept beside the
    /// unpacked one, given to this user, for the new one's start to remove. Gives why not.</summary>
    private string? InstallForEveryone(string passenger)
    {
        string script = Path.Combine(Folder, "install.sh");
        string[] arguments = ["--system", "--from", passenger, "--keep-replaced"];
        ProcessStartInfo command = Writable() || asker is null
            ? ChildProcesses.Command("/bin/sh", [script, .. arguments])
            : ChildProcesses.Command(asker, [script, .. arguments]);
        ChildResult ran = Run(command);
        IdeErrors.Write($"Updates: {ChildProcesses.Display(command)} said: {ran.Said}");
        return ran.Succeeded ? null
            : ran.ExitCode == 126 && asker is not null ? $"no administrator's password was given to pkexec. To install it, run {Command(passenger)}"
            : ran.ExitCode == 127 && asker is not null ? $"pkexec could not ask for an administrator's password ({ran.Said}). To install it, run {Command(passenger)}"
            : $"install.sh failed ({ran.Said}). To install it, run {Command(passenger)}";
    }

    /// <summary>The menu entry, its icons and file types and the command's link, refreshed by the installed install.sh,
    /// the new release's, as it installs them; a failure is logged, the release being in place all the same.</summary>
    private void Refresh()
    {
        ProcessStartInfo command = ChildProcesses.Command("/bin/sh", [Path.Combine(Folder, "install.sh")]);
        command.Environment.Remove("SUDO_USER");
        ChildResult ran = Run(command);
        IdeErrors.Write(ran.Succeeded
            ? $"Updates: install.sh refreshed the menu entry, icons and command: {ran.Said}"
            : $"Updates: install.sh did not refresh the menu entry, icons and command: {ran.Said}");
    }

    public override Task DiscardAsync()
    {
        Ready = null;
        asker = null;
        // Every one: no version is named so.
        Clean(keep: string.Empty);
        return Task.CompletedTask;
    }

    public override Task TidyAsync()
    {
        Clean(keep: null);
        return Task.CompletedTask;
    }

    /// <summary>Every download and unpacked release but <paramref name="keep"/>'s; with none kept, only those not newer than
    /// the running version (<see cref="UpdateFolders.Stale"/>), so a release unpacked and not yet in place stays.</summary>
    private void Clean(string? keep)
    {
        List<string> stale = [];
        if (Directory.Exists(Staging))
        {
            stale.AddRange(Directory.EnumerateDirectories(Staging).Where(folder => UpdateFolders.Stale(Path.GetFileName(folder), running, keep)));
        }
        if (Path.GetDirectoryName(Folder) is { } parent && Directory.Exists(parent))
        {
            stale.AddRange(Directory.EnumerateDirectories(parent, UpdateFolders.Prefix + "*")
                .Where(folder => UpdateFolders.VersionOf(folder) is { } version && UpdateFolders.Stale(version, running, keep)));
        }
        foreach (string folder in stale)
        {
            Remove(folder);
        }
    }

    protected override IReadOnlyList<int> RunningHere() => RunningAs(Path.Combine(Folder, "app", "Passenger"));

    /// <summary>Whether this user can write the install and the folder it is in, as replacing it needs.</summary>
    private bool Writable() =>
        Path.GetDirectoryName(Folder) is { } parent && Libc.Access(parent, Libc.MayWrite) == 0 && Libc.Access(Folder, Libc.MayWrite) == 0;

    /// <summary>The install's launcher, started apart from this process, which it outlives, with this process's
    /// environment (its state folder, release feed and Discord as they were given).</summary>
    private static Task Launch(string install)
    {
        ChildProcesses.StartApart(ChildProcesses.Command(Path.Combine(install, "passenger"), [], Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        return Task.CompletedTask;
    }

    /// <summary>A script run where nothing awaits it, as the process ends, with the variables given for it.</summary>
    private ChildResult Run(ProcessStartInfo command)
    {
        foreach ((string name, string? value) in environment)
        {
            command.Environment[name] = value;
        }
        return Synchronously.Run(() => ToolAsync(command, ScriptLimit, CancellationToken.None));
    }

    /// <summary>AT_FDCWD: a path relative to the working folder, or absolute.</summary>
    private const int WorkingFolder = -100;

    private const uint RenameExchange = 0x2;

    /// <summary>EINVAL: the file system cannot exchange two folders.</summary>
    private const int InvalidArgument = 22;

    /// <summary>ENOSYS: the kernel has no renameat2.</summary>
    private const int NotImplemented = 38;

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt(int fromFolder, string from, int toFolder, string to, uint flags);
}
