using System.Security.Cryptography;
using System.Text;

namespace Passenger.Core;

public static partial class Dotnet
{
    /// <summary>What .NET's installers, dotnet-install and the SDK itself put in an installation's folder: the dotnet host
    /// and its dnx, the folders of its SDKs, runtimes, packs, templates and workloads (with an Arm system's x64 installation),
    /// the licence and notices, and what the SDK keeps where the folder is its home too (~/.dotnet: global tools, caches,
    /// telemetry), with Finder's .DS_Store. Nothing of it is a program a shell could run in place of the system's.</summary>
    private static readonly string[] LayoutNames =
    [
        "dotnet", "dotnet.exe", "dnx", "dnx.cmd", "LICENSE.txt", "ThirdPartyNotices.txt",
        "host", "sdk", "shared", "packs", "templates", "sdk-manifests", "metadata", "library-packs", "template-packs", "x64",
        "tools", "toolResolverCache", "TelemetryStorageService", "sdk-advertising", "sdk-vulnerability-cache", ".DS_Store",
    ];

    /// <summary>The endings of the files the SDK writes once for each version where the folder is its home: its first run's
    /// sentinels and its cached answers.</summary>
    private static readonly string[] LayoutEndings = [".dotnetFirstUseSentinel", ".aspNetCertificateSentinel", ".toolpath.sentinel", ".dotnetUserLevelCache"];

    /// <summary>What the SDK's files of workload advertising begin with, where the folder is its home.</summary>
    private const string AdvertisingStart = ".workloadAdvertising";

    /// <summary>Whether the installation's folder holds .NET's own layout alone (<see cref="LayoutNames"/>), nothing of
    /// another name a shell could run in place of a program of the system's; false where it cannot be read.</summary>
    public static bool HoldsLayoutAlone(string root)
    {
        StringComparer names = PathNames.NamesIn(root);
        StringComparison compared = ReferenceEquals(names, StringComparer.OrdinalIgnoreCase) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            return Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).OfType<string>().All(name => LayoutNames.Contains(name, names)
                || LayoutEndings.Any(ending => name.EndsWith(ending, compared)) || name.StartsWith(AdvertisingStart, compared));
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The folder a shell finds the installation in <paramref name="root"/>'s dotnet in, put first on its PATH: the
    /// installation's own where it holds .NET's layout alone (<see cref="HoldsLayoutAlone"/>), else one lending that dotnet
    /// alone (<see cref="HostAlone"/>), so a program of another name beside it is never run in place of the system's. Off
    /// the window's thread.</summary>
    public static string PathFolder(string root, string state) => HoldsLayoutAlone(root) ? root : HostAlone(root, state);

    /// <summary>What lends an installation's dotnet in <see cref="HostAlone"/>'s folder on Windows, where a link to a program
    /// needs Developer Mode and dotnet.exe finds its installation beside its own path.</summary>
    public const string HostCommand = "dotnet.cmd";

    /// <summary>A folder of the user's own (<see cref="UserTemporary.Own"/>), one for each installation and state, holding
    /// only the dotnet of the installation in <paramref name="root"/>: a link to it, or on Windows a
    /// <see cref="HostCommand"/> running it. First on a shell's PATH, it lends the shell that dotnet alone, never the other
    /// programs beside it in the installation's folder. Off the window's thread; an <see cref="IOException"/> saying why
    /// where the folder cannot be the user's own.</summary>
    public static string HostAlone(string root, string state)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..10];
        string folder = UserTemporary.Own(UserTemporary.Of(state, $"dotnet-{hash}"));
        string host = Path.Combine(root, HostName);
        if (OperatingSystem.IsWindows())
        {
            // A batch file reads % as a variable's start wherever it is, quoted or not.
            byte[] command = Encoding.UTF8.GetBytes($"@\"{host.Replace("%", "%%", StringComparison.Ordinal)}\" %*\r\n");
            string file = Path.Combine(folder, HostCommand);
            if (!File.Exists(file) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(command))
            {
                AtomicFile.Write(file, command);
            }
            return folder;
        }
        string link = Path.Combine(folder, HostName);
        if (new FileInfo(link).LinkTarget != host)
        {
            // Made beside and renamed over, so a shell starting meanwhile finds the link whole or not at all.
            string made = Path.Combine(folder, Guid.NewGuid().ToString("N") + AtomicFile.Temporary);
            File.CreateSymbolicLink(made, host);
            try
            {
                AtomicFile.Move(made, link);
            }
            finally
            {
                if (new FileInfo(made).LinkTarget is not null)
                {
                    File.Delete(made);
                }
            }
        }
        return folder;
    }
}
