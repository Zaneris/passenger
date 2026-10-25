using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Passenger run as it is installed, where the C# server may not write beside its own assemblies, as it does
/// its MEF caches: inside a macOS app bundle, whose seal a file added there breaks, after which Gatekeeper calls a
/// downloaded app damaged, or a system-wide Linux install its user cannot write. It runs from a copy in the state's
/// cache, and the app's folder is left as it was.</summary>
[Collection(Beside.Name)]
public sealed class InstalledAppTests : McpRig, IClassFixture<RestoredFolders>
{
    public InstalledAppTests(RestoredFolders folders)
        : base(folders)
    {
    }

    [Fact]
    public async Task The_csharp_server_of_an_app_bundle_writes_nothing_inside_the_bundle()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "An app bundle, whose seal a file added inside breaks, is macOS's");
        string bundle = TestFolders.Create("bundle-");
        DeleteAtEnd(bundle);
        string app = Path.Combine(Directory.CreateDirectory(Path.Combine(bundle, "Passenger.app", "Contents")).FullName, "MacOS");
        await CopyBuild(app);
        // Through dotnet: macOS takes a program started inside a bundle whose seal fails for a damaged app.
        await RunsFromCopy(app, "wrote inside the app bundle, breaking its seal", throughDotnet: true);
    }

    [Fact]
    public async Task The_csharp_server_of_an_install_its_user_cannot_write_runs_from_a_copy()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A folder's read-only mode keeps no one from writing in it on Windows, where Passenger installs per user");
            return;
        }
        string install = TestFolders.Create("install-");
        DeleteAtEnd(install);
        string app = Path.Combine(install, "passenger");
        await CopyBuild(app);
        // The server's folder as a root-owned install's is to its user (install.sh --system).
        string roslyn = Path.Combine(app, "roslyn");
        File.SetUnixFileMode(roslyn, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        try
        {
            Assert.SkipWhen(Writable(roslyn), "This user (root) writes in a folder whatever its mode");
            await RunsFromCopy(app, "wrote inside the install");
        }
        finally
        {
            File.SetUnixFileMode(roslyn, File.GetUnixFileMode(roslyn) | UnixFileMode.UserWrite);
        }

        static bool Writable(string folder)
        {
            try
            {
                File.WriteAllText(Path.Combine(folder, "probe"), string.Empty);
                File.Delete(Path.Combine(folder, "probe"));
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>The app's build copied to a folder, as an APFS clone on macOS and hard links on Linux (seconds, and no space),
    /// else (another file system, as a tmpfs /tmp is) its large folders a C# server's run never reads linked and the rest
    /// copied. Without what a run of the build in place wrote in its server's folder, which no install carries, where a
    /// server writing there again would find its caches already made.</summary>
    private static async Task CopyBuild(string to)
    {
        string build = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Passenger.App", new DirectoryInfo(AppContext.BaseDirectory).Name));
        Assert.True(File.Exists(Path.Combine(build, "Passenger")), $"The app's build is not at {build}");
        ChildResult copied = await ChildProcesses.RunAsync(ChildProcesses.Command("/bin/cp", [OperatingSystem.IsMacOS() ? "-c" : "-l", "-R", build, to]),
            limit: TimeSpan.FromMinutes(2).Scaled(), cancel: Cancel);
        if (!copied.Succeeded)
        {
            if (Directory.Exists(to))
            {
                Directory.Delete(to, recursive: true);
            }
            Directory.CreateDirectory(to);
            foreach (string entry in Directory.EnumerateFileSystemEntries(build))
            {
                string there = Path.Combine(to, Path.GetFileName(entry));
                if (Path.GetFileName(entry) is "runtimes" or "web" or "debugger" or "inspector")
                {
                    Directory.CreateSymbolicLink(there, entry);
                }
                else if (Directory.Exists(entry))
                {
                    foreach (string file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(there, Path.GetRelativePath(entry, file)))!);
                        File.Copy(file, Path.Combine(there, Path.GetRelativePath(entry, file)));
                    }
                }
                else
                {
                    File.Copy(entry, there);
                }
            }
        }
        if (Directory.Exists(Path.Combine(to, "roslyn", "cache")))
        {
            Directory.Delete(Path.Combine(to, "roslyn", "cache"), recursive: true);
        }
    }

    /// <summary>Has an agent ask the C# server of the app in a folder about a file, then checks that the server wrote its
    /// caches in its copy in the state's cache and nothing in the app's own server folder, and that another build's copy
    /// no server has run from for two days was removed.</summary>
    private async Task RunsFromCopy(string app, string wrote, bool throughDotnet = false)
    {
        string roslyn = Path.Combine(app, "roslyn");
        string[] before = Listing(roslyn);
        string stale = Path.Combine(OwnState, "cache", "roslyn", "5.0.0-0123456789ab");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, ".used"), string.Empty);
        File.SetLastWriteTimeUtc(Path.Combine(stale, ".used"), DateTime.UtcNow - TimeSpan.FromDays(2));
        await using (McpClient client = throughDotnet
            ? await Start([Path.Combine(app, "Passenger.dll"), "--mcp", Folder], executable: Dotnet.Host())
            : await Start(["--mcp", Folder], executable: Path.Combine(app, "Passenger")))
        {
            Assert.Contains((await Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" })).EnumerateArray(),
                d => d.GetProperty("code").GetString() == "CS0219");
            // Its MEF caches are written unawaited as it starts, wherever it runs from.
            Assert.True(await Waiting.UntilAsync(() => Copies().Append(roslyn).SelectMany(Caches).Any(), TimeSpan.FromMinutes(1), Cancel),
                $"The server wrote no MEF cache, in the state's cache or beside itself in the app.{Said()}");
        }
        Assert.True(LastPassenger().WaitForExit(TimeSpan.FromMinutes(1).Scaled()), $"Passenger did not end once its agent went.{Said()}");

        string[] after = Listing(roslyn);
        Assert.True(before.SequenceEqual(after), $"The C# server {wrote}: "
            + string.Join(", ", after.Except(before).Concat(before.Except(after).Select(gone => $"{gone} (changed or gone)"))));
        Assert.False(Directory.Exists(stale), $"Another build's copy of the C# server, unused for two days, was kept: {stale}");
        string copy = Assert.Single(Copies());
        Assert.True(File.Exists(Path.Combine(copy, "Microsoft.CodeAnalysis.LanguageServer.dll")) && Caches(copy).Any(),
            $"The server did not run from its copy in the state's cache, {copy}");

        IEnumerable<string> Copies() =>
            Directory.Exists(Path.Combine(OwnState, "cache", "roslyn"))
                ? Directory.GetDirectories(Path.Combine(OwnState, "cache", "roslyn")).Where(folder => !Path.GetFileName(folder).StartsWith('.'))
                : [];
    }

    /// <summary>The MEF caches a server wrote beside itself, in the folder it ran from.</summary>
    private static IEnumerable<string> Caches(string server) =>
        Directory.Exists(Path.Combine(server, "cache")) ? Directory.EnumerateFiles(Path.Combine(server, "cache"), "*.cache") : [];

    /// <summary>A folder and everything under it, each with its length and time, so a file added, changed or taken away
    /// shows, even one made and deleted again.</summary>
    private static string[] Listing(string folder) =>
        [.. new DirectoryInfo(folder).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Prepend(new DirectoryInfo(folder))
            .Select(each => $"{Path.GetRelativePath(folder, each.FullName)} {(each is FileInfo file ? file.Length : -1)} {each.LastWriteTimeUtc.Ticks}")
            .Order(StringComparer.Ordinal)];
}
