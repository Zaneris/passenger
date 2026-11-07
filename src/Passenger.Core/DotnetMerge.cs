using System.Diagnostics;

namespace Passenger.Core;

/// <summary>What was unpacked of an SDK moved into an installation as one change, taken out whole
/// (<see cref="TakeBackAsync"/>) if the install does not end well. What the installation has already (an SDK, a runtime,
/// the dotnet host) stays as it is and what it lacks moves in whole, a folder of the installation being merged into
/// wherever it leads, a link to another disk too. The sdk folder comes last, so what lands short of the end reads as no
/// SDK, whatever stopped it. Windows refuses to rename a folder while any file under it is open, however the reader shares
/// it, so there a folder whose files another program reads comes in entry by entry (<see cref="PiecesAsync"/>).</summary>
internal sealed class DotnetMerge(string version, Action<string> stage, TimeSpan? heldLimit = null)
{
    /// <summary>How long a step waits for what holds its files: a reader of every file just unpacked (a scanner, an indexer)
    /// may take minutes over them on a slow machine, and an install it outlasts fails for nothing.</summary>
    private static readonly TimeSpan HeldLimit = TimeSpan.FromMinutes(3);

    /// <summary>The host's resolver on Windows, the one system where a folder comes in entry by entry: the dotnet host takes
    /// the newest of host/fxr's folders as it stands, so the folder holding it comes whole.</summary>
    private const string Resolver = "hostfxr.dll";

    private readonly TimeSpan limit = heldLimit ?? HeldLimit;

    /// <summary>Everything placed, in the order it came.</summary>
    private readonly List<string> placed = [];

    /// <summary>How long the steps waited in all for what held the files they reached.</summary>
    public TimeSpan Held { get; private set; }

    /// <summary>How many steps waited for what held the files they reached.</summary>
    public int HeldSteps { get; private set; }

    /// <summary>How many folders came in entry by entry, Windows refusing to move them whole while a file under them was open.</summary>
    public int Piecemeal { get; private set; }

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
                await (folder ? LandAsync(entry, target, cancel) : WhileHeldAsync(() => File.Move(entry, target), entry, cancel)).ConfigureAwait(false);
            }
            else if (folder && IsFolder(target))
            {
                await IntoAsync(entry, target, cancel).ConfigureAwait(false);
            }
            else if (folder || IsFolder(target))
            {
                throw new DotnetInstallFailed($"The .NET SDK {version} was not installed: {PathNames.Shown(target, null)} is {(folder ? "no folder, where the SDK has one" : "a folder, where the SDK has a file")}");
            }
        }
    }

    /// <summary>Whether an entry is what makes its folder count to the dotnet host, which passes over an SDK's folder without
    /// its dotnet.dll and a framework's version folder without its deps.json: in a folder brought in entry by entry it comes
    /// last, so the folder reads as none until all of it is there.</summary>
    internal static bool Counts(string entry)
    {
        string name = Path.GetFileName(entry);
        return name == "dotnet.dll" || name == $"{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(entry)))}.deps.json";
    }

    /// <summary>Whether a folder stands at a path, through a link too: Windows calls a link to nothing a folder.</summary>
    private static bool IsFolder(string path) =>
        Directory.Exists(path) && new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true) is not { Exists: false };

    /// <summary>A folder put where it goes whole (<see cref="Land"/>); on Windows, where that is refused while a file under it
    /// is open, made there and filled entry by entry instead (<see cref="PiecesAsync"/>), but for host/fxr's folder, which
    /// waits to come whole.</summary>
    private async Task LandAsync(string folder, string target, CancellationToken cancel)
    {
        if (!OperatingSystem.IsWindows() || File.Exists(Path.Combine(folder, Resolver)))
        {
            await WhileHeldAsync(() => Land(folder, target), folder, cancel).ConfigureAwait(false);
            return;
        }
        try
        {
            Land(folder, target);
            return;
        }
        catch (Exception refused) when ((refused is IOException or UnauthorizedAccessException) && Directory.Exists(folder) && !Directory.Exists(target))
        {
            // The first is said with what held it; the rest are counted in the install's last line.
            if (Piecemeal++ == 0)
            {
                PassengerLog.Write($"Install .NET SDK {version}: {folder} could not be moved whole ({refused.Message}): {Holding(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories), "hold")} a file under it, so it comes in entry by entry");
            }
        }
        Directory.CreateDirectory(target);
        await PiecesAsync(folder, target, cancel).ConfigureAwait(false);
    }

    /// <summary>A folder renamed to where it goes, else, on another volume, carried (<see cref="Carried"/>).</summary>
    private void Land(string folder, string target)
    {
        if (!FolderMoves.Renamed(folder, target))
        {
            Carried(folder, target, placed.Add);
        }
    }

    /// <summary>What a folder holds moved into the one made for it, each folder whole where it can be (<see cref="LandAsync"/>)
    /// and each file renamed in, which a reader sharing the file's deletion never refuses. A file held otherwise is passed
    /// over until the rest of its folder is in, as a reader moves on, then waited for; what makes the folder count to the
    /// host (<see cref="Counts"/>) comes after all of it.</summary>
    private async Task PiecesAsync(string from, string to, CancellationToken cancel)
    {
        List<string> entries = [.. Directory.EnumerateFileSystemEntries(from)];
        List<string> later = [];
        foreach (string entry in entries.Where(e => !Counts(e)))
        {
            cancel.ThrowIfCancellationRequested();
            string target = Path.Combine(to, Path.GetFileName(entry));
            if (Directory.Exists(entry) && new DirectoryInfo(entry).LinkTarget is null)
            {
                await LandAsync(entry, target, cancel).ConfigureAwait(false);
            }
            else if (!Moved(entry, target))
            {
                later.Add(entry);
            }
        }
        foreach (string file in later.Concat(entries.Where(Counts)))
        {
            cancel.ThrowIfCancellationRequested();
            await WhileHeldAsync(() => File.Move(file, Path.Combine(to, Path.GetFileName(file))), file, cancel).ConfigureAwait(false);
        }
    }

    /// <summary>A file renamed into place at once, or false, nothing moved, where Windows refuses it for now.</summary>
    private static bool Moved(string file, string target)
    {
        try
        {
            File.Move(file, target);
            return true;
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            return false;
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
        else if (IsFolder(Path.GetDirectoryName(path)!))
        {
            File.Delete(path);
        }
    }

    /// <summary>The programs holding any of these files open and <paramref name="verb"/> said of them, as the person is told:
    /// as Windows' Restart Manager names them (<see cref="FileHolders.Named"/>), which it cannot once a quick reader has let
    /// go.</summary>
    private static string Holding(IEnumerable<string> files, string verb)
    {
        IReadOnlyList<string> named = [];
        try
        {
            named = FileHolders.Named(files);
        }
        catch (Exception unlisted) when (unlisted is IOException or UnauthorizedAccessException)
        {
            // A folder gone or shut as it is listed names no one.
        }
        return named.Count switch
        {
            0 => $"a program Windows does not name {verb}s",
            1 => $"{named[0]} {verb}s",
            _ => $"{string.Join(" and ", named)} {verb}",
        };
    }

    /// <summary>A step, tried again on Windows while it is refused for a while, another program holding a file it reaches:
    /// the stage names that program as Windows does while it waits, and the log counts the wait.</summary>
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
                    string holding = Holding(Directory.Exists(reaching) ? Directory.EnumerateFiles(reaching, "*", SearchOption.AllDirectories) : [reaching], "hold");
                    stage($"Waiting for Windows to let go of the .NET SDK's {Path.GetFileName(reaching)}, which {holding}");
                    PassengerLog.Write($"Install .NET SDK {version}: {reaching} is held ({held.Message}): {holding} it; waiting for it");
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancel).ConfigureAwait(false);
            }
        }
    }
}
