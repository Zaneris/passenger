using Passenger.Core;

namespace Passenger.App;

/// <summary>A theme or code colour scheme a source lists, as read: its path in the source, whether it colours the code alone,
/// and what reading it came to, with the bytes read where it reads.</summary>
public sealed record ListedTheme(ThemeSource Source, string Path, bool Code, ThemeRead<ThemeFile> Read, byte[]? Bytes)
{
    /// <summary>Its name as settings keep it, where it reads.</summary>
    public SourcedTheme? Named => Read.Read is { } file ? new SourcedTheme(Source.Name, file.Name) : null;
}

/// <summary>What reading a source came to: what it lists, or why it could not be read (its index refused or out of reach),
/// and what its index passed over.</summary>
public sealed record SourceRead(ThemeSource Source, string? Refused, IReadOnlyList<ListedTheme> Listed, IReadOnlyList<string> Problems);

/// <summary>Reads a theme source: its index, then every file it lists, each through <see cref="ThemeFiles"/> and held to the
/// most a file may be before it is read whole, from a folder or over <see cref="OutboundHttp"/> (a GitHub repository's raw
/// files at its default branch, or an address's), never a clone. Asked only when a person looks at a source's themes, picks
/// one or asks for a refresh. Called on any thread.</summary>
public static class ThemeSourceReads
{
    /// <summary>How long a source's host is waited for, for each file.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>The most files of one source read at once.</summary>
    private const int AtOnce = 4;

    private static readonly HttpClient Http = OutboundHttp.Client(Limit, most: ThemeFiles.MostBytes);

    /// <summary>A source's index and every file it lists, its tokens read against <paramref name="kinds"/>.</summary>
    public static async Task<SourceRead> ReadAsync(ThemeSource source, IReadOnlyDictionary<string, ThemeTokenKind> kinds, CancellationToken cancel)
    {
        ThemeSource reached;
        try
        {
            reached = source.Reached;
        }
        catch (FormatException error)
        {
            return new SourceRead(source, error.Message, [], []);
        }
        (byte[]? bytes, string? why) = await BytesAsync(reached, ThemeFiles.IndexFile, ThemeFiles.MostBytes, cancel);
        if (bytes is null)
        {
            return new SourceRead(source, why, [], []);
        }
        ThemeRead<ThemeIndex> index = ThemeFiles.ReadIndex($"{source.Shown}'s {ThemeFiles.IndexFile}", bytes);
        if (index.Read is not { } listed)
        {
            return new SourceRead(source, index.Refused, [], index.Problems);
        }
        using SemaphoreSlim turns = new(AtOnce);
        ListedTheme[] read = await Task.WhenAll(listed.Themes.Select(p => (p, false)).Concat(listed.CodeColours.Select(p => (p, true)))
            .Select(async entry =>
            {
                await turns.WaitAsync(cancel);
                try
                {
                    return await FileAsync(source, reached, entry.p, entry.Item2, kinds, cancel);
                }
                finally
                {
                    turns.Release();
                }
            }));
        return new SourceRead(source, null, read, index.Problems);
    }

    /// <summary>One file a source lists, as <see cref="ReadAsync"/> reads it.</summary>
    public static async Task<ListedTheme> FileAsync(ThemeSource source, ThemeSource reached, string path, bool code,
        IReadOnlyDictionary<string, ThemeTokenKind> kinds, CancellationToken cancel)
    {
        (byte[]? bytes, string? why) = await BytesAsync(reached, path, ThemeFiles.MostBytes, cancel);
        string named = $"{source.Shown}'s {path}";
        ThemeRead<ThemeFile> read = bytes is null ? new ThemeRead<ThemeFile>(null, why, [])
            : code ? ThemeFiles.ReadCodeColours(named, bytes, kinds) : ThemeFiles.ReadTheme(named, bytes, kinds);
        return new ListedTheme(source, path, code, read, read.Read is null ? null : bytes);
    }

    /// <summary>A file of a source by its path there, at most <paramref name="most"/> bytes (a theme file's, or later an
    /// image's own limit), or why not, naming it.</summary>
    public static async Task<(byte[]? Bytes, string? Why)> BytesAsync(ThemeSource source, string path, long most, CancellationToken cancel)
    {
        if (source.Kind == ThemeSourceKind.Folder)
        {
            return await BackgroundWork.Run(() => FromFolder(source.Name, path, most), cancel);
        }
        if (source.Place is not { } place)
        {
            return (null, $"{source.Shown} is read where Passenger keeps it");
        }
        Uri address = new(new Uri(place), path);
        string missing = $"{source.Shown} has no {path}{(source.Kind == ThemeSourceKind.GitHub ? " at its default branch, or is no public repository" : string.Empty)} ({address} answers 404)";
        return await OutboundHttp.ReadAsync(Http, address, most, $"{source.Shown}'s {path}", missing, cancel);
    }

    private static (byte[]? Bytes, string? Why) FromFolder(string folder, string path, long most)
    {
        string file = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, path.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!PathNames.IsUnder(file, folder))
        {
            return (null, $"{path} is not inside {folder}");
        }
        try
        {
            FileInfo info = new(file);
            if (!Directory.Exists(folder))
            {
                return (null, $"{folder} is no folder here");
            }
            if (!info.Exists)
            {
                return (null, $"{folder} has no {path}");
            }
            if (info.Length > most)
            {
                return (null, $"{file} is {Sizes.Said(info.Length)}, more than the {Sizes.Said(most)} it may hold");
            }
            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Streams.Bounded(stream, most) is { } bytes ? (bytes, null) : (null, $"{file} is more than the {Sizes.Said(most)} it may hold");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, $"{file} could not be read: {error.Message}");
        }
    }

    /// <summary>Every picture a theme a source lists names, each read beside its file there and checked, and the VS Code theme
    /// it names as a file beside it with the files that includes, or why not.</summary>
    public static async Task<(Dictionary<string, byte[]> Images, string? Why)> ImagesAsync(ListedTheme listed, ThemeSource reached, CancellationToken cancel)
    {
        if (listed.Read.Read is not { } theme)
        {
            return ([], null);
        }
        (Dictionary<string, byte[]> images, string? why) = await ThemeImages.ReadAsync(theme, image => BytesAsync(reached, ThemeImages.Beside(listed.Path, image), ThemeFiles.MostImageBytes, cancel));
        // A VS Code theme or file icon theme named as a file beside the theme's comes with it, as its pictures do.
        (Dictionary<string, byte[]> beside, string? unread) = await VsCodeBases.BesideAsync(theme, file => BytesAsync(reached, ThemeImages.Beside(listed.Path, file), ThemeFiles.MostBytes, cancel));
        (Dictionary<string, byte[]> icons, string? undrawn) = await VsCodeIconSets.BesideAsync(theme, file => BytesAsync(reached, ThemeImages.Beside(listed.Path, file), ThemeFiles.MostBytes, cancel));
        foreach ((string path, byte[] bytes) in beside.Concat(icons))
        {
            images[path] = bytes;
        }
        return (images, why ?? unread ?? undrawn);
    }

    /// <summary>A picture beside a file named by the person, by its path there.</summary>
    public static (byte[]? Bytes, string? Why) BesideFile(string file, string image) =>
        System.IO.Path.GetDirectoryName(file) is { } folder ? FromFolder(folder, image, ThemeFiles.MostImageBytes) : (null, $"{file} is no file");

    /// <summary>A file named by the person, as <see cref="FromFolder"/> reads one a source lists: at most a theme file's
    /// bytes, or <paramref name="most"/>.</summary>
    public static (byte[]? Bytes, string? Why) FromFile(string file, long most = ThemeFiles.MostBytes) =>
        System.IO.Path.GetDirectoryName(file) is { } folder ? FromFolder(folder, System.IO.Path.GetFileName(file), most) : (null, $"{file} is no file");
}
