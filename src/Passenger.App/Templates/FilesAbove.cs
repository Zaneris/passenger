using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Templates;

/// <summary>The files at the top of the folders above one dotnet new makes things in, up to where its post actions write
/// outside it (a test template's global.json, at the repository's root: the nearest folder holding global.json or .git,
/// else the topmost holding a solution), as they were before it ran, so what it made or changed there is told
/// (<see cref="Wrote"/>) with each file's text before, to be put back. Read off the window's thread.</summary>
public sealed class FilesAbove
{
    /// <summary>The longest file kept to be put back: dotnet new's post actions write small files.</summary>
    private const long Kept = 1 << 20;

    private readonly IReadOnlyList<string> folders;
    private readonly Dictionary<string, (long Length, DateTime Written, FileText? Text)> files;

    private FilesAbove(IReadOnlyList<string> folders, Dictionary<string, (long Length, DateTime Written, FileText? Text)> files)
    {
        this.folders = folders;
        this.files = files;
    }

    /// <summary>The files above a folder (which need not be there yet) as they are now.</summary>
    public static FilesAbove Of(string folder)
    {
        IReadOnlyList<string> above = Above(folder);
        Dictionary<string, (long Length, DateTime Written, FileText? Text)> files = new(PathNames.Comparer);
        foreach (FileInfo file in above.SelectMany(Top))
        {
            files[file.FullName] = (file.Length, file.LastWriteTimeUtc, file.Length <= Kept ? Text(file.FullName) : null);
        }
        return new FilesAbove(above, files);
    }

    /// <summary>The files made or changed since, each with its text before (null for one made); one changed that was not
    /// text, or too long to keep, is left out, as it cannot be put back.</summary>
    public IReadOnlyList<(string Path, FileText? Before)> Wrote()
    {
        List<(string Path, FileText? Before)> wrote = [];
        foreach (FileInfo file in folders.SelectMany(Top))
        {
            if (!files.TryGetValue(file.FullName, out (long Length, DateTime Written, FileText? Text) was))
            {
                wrote.Add((file.FullName, null));
            }
            else if ((was.Length != file.Length || was.Written != file.LastWriteTimeUtc) && was.Text is { } before)
            {
                wrote.Add((file.FullName, before));
            }
        }
        return wrote;
    }

    /// <summary>The folders above a folder where dotnet new's post actions write: up to the nearest holding global.json or
    /// .git, else the topmost holding a solution; none where neither is above it.</summary>
    private static IReadOnlyList<string> Above(string folder)
    {
        List<string> above = [];
        int solved = 0;
        for (DirectoryInfo? at = new DirectoryInfo(folder).Parent; at is not null; at = at.Parent)
        {
            above.Add(at.FullName);
            if (File.Exists(Path.Combine(at.FullName, "global.json")) || Path.Exists(Path.Combine(at.FullName, ".git")))
            {
                return above;
            }
            if (Top(at.FullName).Any(f => Path.GetExtension(f.Name).ToLowerInvariant() is ".sln" or ".slnx"))
            {
                solved = above.Count;
            }
        }
        return above[..solved];
    }

    /// <summary>The files at a folder's top, the system's own passed over; none where it cannot be read.</summary>
    private static IReadOnlyList<FileInfo> Top(string folder)
    {
        try
        {
            return [.. new DirectoryInfo(folder).EnumerateFiles().Where(f => !PathNames.IsSystemFile(f.Name))];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Gone or shut: nothing there is dotnet new's to have written.
            return [];
        }
    }

    /// <summary>A file's text, null for one that is not text or cannot be read.</summary>
    private static FileText? Text(string path)
    {
        try
        {
            return FileText.Load(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Unread, it is not kept: a change of it is left as it is.
            return null;
        }
    }
}
