using System.Diagnostics;
using System.Globalization;
using Passenger.Core;

namespace Passenger.App.Editors;

/// <summary>A folder a command made whole, kept for its Undo as each file's length and last write time, never its bytes:
/// Undo moves it aside under the state's folder once it is as made, and Redo moves it back from there. What a build or a
/// restore writes (bin, obj) is no part of it. <see cref="Stamps"/>, <see cref="Unlike"/> and <see cref="Sweep"/> read the
/// disk at length, so they run off the window's thread; <see cref="Move"/> is a rename on one volume.</summary>
public static class MadeFolders
{
    private const string Undone = "undone";

    private static readonly HashSet<string> Swept = new(PathNames.Comparer);

    /// <summary>Each file under a folder by its path from there, with its length and last write time, but what builds
    /// write and the system's own files.</summary>
    public static Dictionary<string, (long Length, DateTime Written)> Stamps(string folder)
    {
        Dictionary<string, (long Length, DateTime Written)> stamps = new(PathNames.Comparer);
        string root = PathNames.Canonical(folder);
        foreach ((_, IReadOnlyList<string> files) in PathNames.FilesUnder(root, OutsideBuilds))
        {
            foreach (string file in files)
            {
                FileInfo info = new(file);
                if (info.Exists)
                {
                    stamps[Path.GetRelativePath(root, file)] = (info.Length, info.LastWriteTimeUtc);
                }
            }
        }
        return stamps;
    }

    /// <summary>Why a folder is no longer as it was stamped, naming the first file changed, gone or made in it since; null
    /// when it is.</summary>
    public static string? Unlike(string folder, IReadOnlyDictionary<string, (long Length, DateTime Written)> made)
    {
        string name = Path.GetFileName(folder);
        if (!Directory.Exists(folder))
        {
            return $"{name} is no longer there";
        }
        Dictionary<string, (long Length, DateTime Written)> now = Stamps(folder);
        foreach ((string file, (long Length, DateTime Written) stamp) in made)
        {
            if (!now.TryGetValue(file, out (long Length, DateTime Written) found))
            {
                return $"{Path.Join(name, file)} is no longer there";
            }
            if (found != stamp)
            {
                return $"{Path.Join(name, file)} has been changed since";
            }
        }
        return now.Keys.FirstOrDefault(f => !made.ContainsKey(f)) is { } added ? $"{Path.Join(name, added)} has been made since" : null;
    }

    /// <summary>Where a folder Undo takes away is set aside: a place of its own under the state's folder, named for this
    /// process so what one that ended left can be told from what a running one keeps.</summary>
    public static string Aside(string state, string folder) =>
        Path.Combine(state, Undone, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), Guid.NewGuid().ToString("N"), Path.GetFileName(folder));

    /// <summary>Moves a folder aside, or back: at once on one volume, else copied whole, its files' times kept and what
    /// builds write left out, before the folder goes.</summary>
    public static void Move(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        try
        {
            Directory.Move(from, to);
            return;
        }
        catch (IOException) when (Directory.Exists(from) && !Directory.Exists(to))
        {
            // A rename moves nothing when it fails, so the folder is copied, as it must be from one volume to another.
        }
        try
        {
            Copy(from, to);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Delete(to);
            throw;
        }
        Directory.Delete(from, recursive: true);
    }

    /// <summary>Lets go of a folder set aside, with the place made for it, off the window's thread.</summary>
    public static void Forget(string aside) =>
        IdeErrors.Observe(BackgroundWork.Run(() => Delete(Path.GetDirectoryName(aside)!)));

    /// <summary>What processes no longer running set aside under a state's folder (one ended before its Undo history let
    /// go), deleted once a process: the first time it keeps a folder there.</summary>
    public static void Sweep(string state)
    {
        lock (Swept)
        {
            if (!Swept.Add(state))
            {
                return;
            }
        }
        string undone = Path.Combine(state, Undone);
        if (!Directory.Exists(undone))
        {
            return;
        }
        foreach (string kept in Directory.EnumerateDirectories(undone))
        {
            if (int.TryParse(Path.GetFileName(kept), NumberStyles.None, CultureInfo.InvariantCulture, out int process) && process != Environment.ProcessId && !Running(process))
            {
                Delete(kept);
            }
        }
    }

    private static bool OutsideBuilds(string folder) => Path.GetFileName(folder) is not ("bin" or "obj");

    private static void Copy(string from, string to)
    {
        string root = PathNames.Canonical(from);
        foreach ((string folder, IReadOnlyList<string> files) in PathNames.FilesUnder(root, OutsideBuilds))
        {
            string into = Path.Join(to, Path.GetRelativePath(root, folder));
            Directory.CreateDirectory(into);
            foreach (string file in files)
            {
                string copy = Path.Join(into, Path.GetFileName(file));
                File.Copy(file, copy);
                File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(file));
            }
        }
    }

    private static void Delete(string folder)
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
            // Under the state's folder alone, where a later process's sweep takes it.
            IdeLog.Write(IdeLog.Ide, $"A folder set aside for Redo could not be deleted, {folder}: {error.Message}");
        }
    }

    private static bool Running(int process)
    {
        try
        {
            using Process found = Process.GetProcessById(process);
            return !found.HasExited;
        }
        catch (Exception missing) when (missing is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
