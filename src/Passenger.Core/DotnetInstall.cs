using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Passenger.Core;

/// <summary>Installs a .NET SDK into the person's own folder, with no administrator rights: its archive downloaded over
/// HTTPS (http only to this machine) through <see cref="OutboundHttp"/>, its SHA-512 taken as it comes and held to the one
/// Microsoft publishes, unpacked beside the folder's files, moved in (<see cref="DotnetMerge"/>), what the folder holds
/// already kept as it is, and run once. An SDK that does not get that far is taken out again.</summary>
public static class DotnetInstall
{
    /// <summary>How long a download may go without a byte before it is given up.</summary>
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);

    private static readonly HttpClient Http = OutboundHttp.Client(Timeout.InfiniteTimeSpan, most: 64 * 1024 * 1024);

    /// <summary>A test's stand-in for <see cref="Folder"/>; null for the person's own.</summary>
    public static string? FolderStandIn { get; set; }

    /// <summary>Where Passenger installs the SDK, as Microsoft's install scripts do: <c>%LOCALAPPDATA%\Microsoft\dotnet</c>
    /// on Windows, <c>~/.dotnet</c> elsewhere.</summary>
    public static string Folder => FolderStandIn ?? (OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"));

    /// <summary>Downloads, checks, unpacks and moves <paramref name="sdk"/> into <paramref name="into"/> and runs it once,
    /// saying each step as it begins and logging how long it took (<see cref="Steps"/>); what it installed read back. A
    /// <see cref="DotnetInstallFailed"/> saying why when it does not, nothing of it left behind, its folder neither where it
    /// made it, and what could not be taken out named.</summary>
    public static async Task<DotnetInstallation> InstallAsync(SdkDownload sdk, string into, Action<string> stage, CancellationToken cancel)
    {
        if (!WebAddress.Secure(sdk.Url))
        {
            throw new DotnetInstallFailed($"The .NET SDK {sdk.Version} would come over {sdk.Url.Scheme}, not HTTPS ({sdk.Url})");
        }
        bool made = !Directory.Exists(into);
        string staging = Path.Combine(into, $".passenger-install-{Guid.NewGuid():N}");
        Steps steps = new(sdk.Version);
        DotnetMerge merge = new(sdk.Version, stage);
        bool installed = false;
        try
        {
            DotnetInstallation read = await PlaceAsync(sdk, into, staging, merge, stage, steps, cancel).ConfigureAwait(false);
            installed = true;
            return read;
        }
        catch (Exception failed)
        {
            await TakeBackAsync(merge, failed).ConfigureAwait(false);
            throw;
        }
        finally
        {
            Remove(staging);
            // A folder the install made for itself goes with it, where nothing else came into it meanwhile.
            if (!installed && made && Directory.Exists(into) && !Directory.EnumerateFileSystemEntries(into).Any())
            {
                Remove(into);
            }
            steps.Log(installed, merge);
        }
    }

    /// <summary>Whatever ended an install, what it placed taken out again: an SDK short of its last step is no SDK. A
    /// <see cref="DotnetInstallFailed"/> saying why it ended and naming what could not be.</summary>
    private static async Task TakeBackAsync(DotnetMerge merge, Exception ended)
    {
        IReadOnlyList<string> left = await merge.TakeBackAsync().ConfigureAwait(false);
        if (left.Count > 0)
        {
            string named = string.Join(", ", left.Select(path => PathNames.Shown(path, null)));
            throw new DotnetInstallFailed($"{ended.Message}. These could not be taken out again and are yours to delete: {named}", ended);
        }
    }

    /// <summary>The steps of an install, each failing in the words a person is told: the archive read and held to its
    /// SHA-512, unpacked into <paramref name="staging"/>, moved in, read back as a whole SDK, and run.</summary>
    private static async Task<DotnetInstallation> PlaceAsync(SdkDownload sdk, string into, string staging, DotnetMerge merge, Action<string> stage, Steps steps, CancellationToken cancel)
    {
        try
        {
            Directory.CreateDirectory(staging);
            string archive = Path.Combine(staging, sdk.FileName);
            string sha512 = await steps.TimedAsync("download", () => DownloadAsync(sdk, archive, stage, cancel)).ConfigureAwait(false);
            stage($"Checking the .NET SDK {sdk.Version}'s archive against the SHA-512 Microsoft publishes");
            if (!steps.Timed("check", () => string.Equals(sha512, sdk.Sha512, StringComparison.OrdinalIgnoreCase)))
            {
                PassengerLog.Write($"The .NET SDK {sdk.Version}'s archive from {sdk.Url} has the SHA-512 {sha512}, where Microsoft publishes {sdk.Sha512}");
                throw new DotnetInstallFailed($"The .NET SDK {sdk.Version}'s archive is not the one Microsoft publishes (its SHA-512 differs), so nothing was installed");
            }
            stage($"Unpacking the .NET SDK {sdk.Version}");
            string files = Path.Combine(staging, "files");
            await steps.TimedAsync("unpack", () => UnpackAsync(archive, files, cancel)).ConfigureAwait(false);
            stage($"Moving the .NET SDK {sdk.Version} into {PathNames.Shown(into, null)}");
            await steps.TimedAsync("move", () => merge.IntoAsync(files, into, cancel)).ConfigureAwait(false);
            DotnetInstallation read = DotnetInstallation.Read(into);
            if (!read.Sdks.Contains(sdk.Version))
            {
                throw new DotnetInstallFailed($"The .NET SDK {sdk.Version}'s archive gave {PathNames.Shown(into, null)} no sdk/{sdk.Version} folder with the runtime it runs on, so it did not install");
            }
            await steps.TimedAsync("first run", () => FirstRunAsync(sdk, into, stage, cancel)).ConfigureAwait(false);
            return read;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new DotnetInstallFailed($"The .NET SDK {sdk.Version} could not be installed into {PathNames.Shown(into, null)}: {error.Message}", error);
        }
    }

    /// <summary>The archive written to <paramref name="path"/> as it comes, its stage said each megabyte; its SHA-512 in hex.</summary>
    private static async Task<string> DownloadAsync(SdkDownload sdk, string path, Action<string> stage, CancellationToken cancel)
    {
        stage($"Downloading the .NET SDK {sdk.Version}");
        using IncrementalHash sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        try
        {
            using HttpRequestMessage request = OutboundHttp.Request(HttpMethod.Get, sdk.Url);
            using HttpResponseMessage response = await Stalling(cancel, token => Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } landed && !WebAddress.Secure(landed))
            {
                throw new DotnetInstallFailed($"The .NET SDK {sdk.Version} was sent on to {landed}, not over HTTPS");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new DotnetInstallFailed($"The .NET SDK {sdk.Version} did not download: {sdk.Url} answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            long? length = response.Content.Headers.ContentLength;
            await using Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            byte[] buffer = new byte[81920];
            long done = 0;
            long said = -1;
            while (await Stalling(cancel, token => body.ReadAsync(buffer, token).AsTask()).ConfigureAwait(false) is var read and > 0)
            {
                sha512.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                done += read;
                if (done / Megabyte != said)
                {
                    said = done / Megabyte;
                    stage(string.Create(CultureInfo.InvariantCulture, $"Downloading the .NET SDK {sdk.Version}: {said} MB{(length is > 0 ? $" of {length.Value / Megabyte} MB" : string.Empty)}"));
                }
            }
        }
        catch (HttpRequestException error)
        {
            throw new DotnetInstallFailed($"The .NET SDK {sdk.Version} did not download: {error.Message}", error);
        }
        return Convert.ToHexStringLower(sha512.GetHashAndReset());
    }

    private const long Megabyte = 1024 * 1024;

    /// <summary>A step of a download, given up when no byte comes for <see cref="Stall"/>.</summary>
    private static async Task<T> Stalling<T>(CancellationToken cancel, Func<CancellationToken, Task<T>> step)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(Stall);
        try
        {
            return await step(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new DotnetInstallFailed(string.Create(CultureInfo.InvariantCulture, $"The .NET SDK did not download: nothing came for {Stall.TotalSeconds:0} s"));
        }
    }

    /// <summary>A .zip or .tar.gz unpacked into a new folder, each entry kept inside it (the readers refuse one that leaves it).</summary>
    private static async Task UnpackAsync(string archive, string into, CancellationToken cancel)
    {
        Directory.CreateDirectory(into);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ZipFile.ExtractToDirectoryAsync(archive, into, overwriteFiles: false, cancel).ConfigureAwait(false);
            return;
        }
        await using FileStream file = new(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using GZipStream unzipped = new(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(unzipped, into, overwriteFiles: false, cancel).ConfigureAwait(false);
    }

    /// <summary>The installed SDK run once by the dotnet beside it, as its first use would be, whatever global.json is over
    /// the folder (its own dotnet.dll asked its version, its telemetry off): on Windows a virus scanner reads its host and
    /// assemblies as they first load, which the stage names and the log times. A <see cref="DotnetInstallFailed"/> saying what
    /// it said where it did not run; one that outlasts the limit is slow, not broken, and stays.</summary>
    private static async Task FirstRunAsync(SdkDownload sdk, string root, Action<string> stage, CancellationToken cancel)
    {
        stage($"Starting the .NET SDK {sdk.Version} for the first time");
        long began = Stopwatch.GetTimestamp();
        string? answer = null;
        string said;
        string why;
        try
        {
            string cli = Path.Combine(root, "sdk", sdk.Version, "dotnet.dll");
            ChildAnswer run = await ChildProcesses.AnswerAsync(ChildProcesses.DotnetQuestion(Path.Combine(root, Dotnet.HostName), [cli, "--version"], root),
                line => string.Equals(line.Trim(), sdk.Version, StringComparison.OrdinalIgnoreCase), limit: FirstRunLimit, cancel: cancel).ConfigureAwait(false);
            answer = run.Answer?.Trim();
            said = run.Said;
            why = run.Ended;
        }
        catch (TimeoutException slow)
        {
            PassengerLog.Write($"Install .NET SDK {sdk.Version}: its first run gave no answer in time, so nothing says it does not run: {slow.Message}");
            return;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            said = error.Message;
            why = ChildResult.FirstLineOf(error.Message);
        }
        PassengerLog.Write(string.Create(CultureInfo.InvariantCulture,
            $"Install .NET SDK {sdk.Version}: first run {Stopwatch.GetElapsedTime(began).TotalSeconds:0.0} s, answering {answer ?? said}"));
        if (answer is null)
        {
            throw new DotnetInstallFailed($"The .NET SDK {sdk.Version} did not start, so it was not installed: {why}");
        }
    }

    /// <summary>How long the installed SDK's first run may take: a scanner on a slow machine reads for a while.</summary>
    private static readonly TimeSpan FirstRunLimit = TimeSpan.FromMinutes(3);

    /// <summary>An install's steps as they are timed, said in the log as one line once it ends, so where its time went reads
    /// from the log: each step's seconds, how long the moves waited for what held the files, and how many folders came in
    /// entry by entry for it.</summary>
    private sealed class Steps(string version)
    {
        private readonly List<(string Step, TimeSpan Took)> took = [];

        public async Task<T> TimedAsync<T>(string step, Func<Task<T>> work)
        {
            long began = Stopwatch.GetTimestamp();
            try
            {
                return await work().ConfigureAwait(false);
            }
            finally
            {
                took.Add((step, Stopwatch.GetElapsedTime(began)));
            }
        }

        public Task TimedAsync(string step, Func<Task> work) => TimedAsync(step, async () =>
        {
            await work().ConfigureAwait(false);
            return true;
        });

        public T Timed<T>(string step, Func<T> work)
        {
            long began = Stopwatch.GetTimestamp();
            try
            {
                return work();
            }
            finally
            {
                took.Add((step, Stopwatch.GetElapsedTime(began)));
            }
        }

        public void Log(bool installed, DotnetMerge merge)
        {
            string each = string.Join(", ", took.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Step} {t.Took.TotalSeconds:0.0} s")));
            string waits = merge.HeldSteps > 0
                ? string.Create(CultureInfo.InvariantCulture, $"; the moves waited {merge.Held.TotalSeconds:0.0} s in all for Windows to let go of {merge.HeldSteps} held entries")
                : string.Empty;
            string pieces = merge.Piecemeal > 0
                ? string.Create(CultureInfo.InvariantCulture, $"; {merge.Piecemeal} folders came in entry by entry, a file under them being open")
                : string.Empty;
            PassengerLog.Write($"Install .NET SDK {version}: {(installed ? "installed" : "not installed")}; {each}{waits}{pieces}");
        }
    }

    private static void Remove(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            PassengerLog.Write($"The .NET SDK's unpacked files in {folder} could not be removed: {error.Message}");
        }
    }
}
