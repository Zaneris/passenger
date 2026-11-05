using System.Diagnostics;

namespace Passenger.Core;

/// <summary>What was unpacked of an SDK moved into an installation as one change, taken out whole
/// (<see cref="TakeBackAsync"/>) if the install does not end well. What the installation has already (an SDK, a runtime,
/// the dotnet host) stays as it is and what it lacks moves in whole, a folder of the installation being merged into
/// wherever it leads, a link to another disk too. The sdk folder comes last, so what lands short of the end reads as no
/// SDK, whatever stopped it.</summary>
internal sealed class DotnetMerge(string version, Action<string> stage, TimeSpan? heldLimit = null)
{
    /// <summary>How long a move or a removal waits on Windows for what holds the files it reaches, unless told.</summary>
    private static readonly TimeSpan HeldLimit = TimeSpan.FromSeconds(30);

    private readonly TimeSpan limit = heldLimit ?? HeldLimit;

    /// <summary>Everything placed, in the order it came.</summary>
    private readonly List<string> placed = [];

    /// <summary>How long the steps waited in all for what held the files they reached.</summary>
    public TimeSpan Held { get; private set; }

    /// <summary>How many steps waited for what held the files they reached.</summary>
    public int HeldSteps { get; private set; }

    /// <summary>Moves what <paramref name="from"/> holds into <paramref name="into"/>, folder by folder. A
    /// <see cref="DotnetInstallFailed"/> where the installation holds a file for a folder of the SDK's or the other way,
    /// which no merge can make whole.</summary>
    public async Task IntoAsync(string from, string into, CancellationToken cancel)
    {
        Directory.CreateDirectory(into);
        foreach (string entry in Directory.EnumerateFileSystemEntries(from).OrderBy(e => Path.GetFileName(e) == "sdk"))
        {
            cancel.ThrowIfCancellationRequested();
            string target = Path.Combine(into, Path.GetFileName(entry));
            bool folder = Directory.Exists(entry) && new DirectoryInfo(entry).LinkTarget is null;
            if (!File.Exists(target) && !Directory.Exists(target) && new FileInfo(target).LinkTarget is null)
            {
                placed.Add(target);
                await WhileHeldAsync(folder ? () => Land(entry, target) : () => File.Move(entry, target), entry, cancel).ConfigureAwait(false);
            }
            else if (folder && Directory.Exists(target))
            {
                await IntoAsync(entry, target, cancel).ConfigureAwait(false);
            }
            else if (folder || Directory.Exists(target))
            {
                throw new DotnetInstallFailed($"The .NET SDK {version} was not installed: {PathNames.Shown(target, null)} is {(folder ? "no folder, where the SDK has one" : "a folder, where the SDK has a file")}");
            }
        }
    }

    /// <summary>A folder renamed to where it goes, else, on another volume, carried (<see cref="Carried"/>).</summary>
    private void Land(string folder, string target)
    {
        if (!FolderMoves.Renamed(folder, target))
        {
            Carried(folder, target, placed.Add);
        }
    }

    /// <summary>A folder brought over from another volume under a name of its own beside where it goes, then renamed there,
    /// so no part of it ever stands at <paramref name="target"/>; the name beside it given to <paramref name="beside"/> first.</summary>
    internal static void Carried(string folder, string target, Action<string> beside)
    {
        string coming = Path.Combine(Path.GetDirectoryName(target)!, $".passenger-install-{Guid.NewGuid():N}");
        beside(coming);
        FolderMoves.Carry(folder, coming);
        Directory.Move(coming, target);
    }

    /// <summary>What <see cref="IntoAsync"/> placed taken out again, the last first, whatever has been asked of the install
    /// meanwhile: what could not be taken out, each logged with why.</summary>
    public async Task<IReadOnlyList<string>> TakeBackAsync()
    {
        List<string> left = [];
        for (int i = placed.Count - 1; i >= 0; i--)
        {
            string path = placed[i];
            try
            {
                await WhileHeldAsync(() => Delete(path), path, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                left.Add(path);
                PassengerLog.Write($"Install .NET SDK {version}: {path} could not be taken out again: {error.Message}");
            }
        }
        placed.Clear();
        return left;
    }

    /// <summary>A folder and all it holds, a file or a link alone, or nothing where the step placing it had not got that far.</summary>
    private static void Delete(string path)
    {
        if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null)
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }
    }

    /// <summary>A step, tried again on Windows while it is refused for a while: a virus scanner reading the files just
    /// unpacked holds them, and their folder, until it is done, which the stage says while it waits and the log counts.</summary>
    private async Task WhileHeldAsync(Action step, string reaching, CancellationToken cancel)
    {
        long began = Stopwatch.GetTimestamp();
        bool said = false;
        while (true)
        {
            try
            {
                step();
                if (said)
                {
                    Held += Stopwatch.GetElapsedTime(began);
                    HeldSteps++;
                }
                return;
            }
            catch (Exception held) when (OperatingSystem.IsWindows() && (held is IOException or UnauthorizedAccessException) && Stopwatch.GetElapsedTime(began) < limit)
            {
                if (!said)
                {
                    said = true;
                    stage($"Waiting for Windows to let go of the .NET SDK's {Path.GetFileName(reaching)}, which another program (a virus scanner reading the files just unpacked, most likely) holds");
                    PassengerLog.Write($"Install .NET SDK {version}: {reaching} is held ({held.Message}); waiting for it");
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancel).ConfigureAwait(false);
            }
        }
    }
}
