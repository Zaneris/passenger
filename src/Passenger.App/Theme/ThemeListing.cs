using Passenger.Core;

namespace Passenger.App;

/// <summary>What each theme source lists, as last read in this process: read when a person looks at its themes in Settings
/// (once, until a refresh asks again), never at a start; the built-in source read from the app and the added themes from
/// where they are kept, never from the web. A refresh reads every source again and takes the newer copy of each theme kept
/// from it. UI thread only.</summary>
public static class ThemeListing
{
    private static readonly Dictionary<ThemeSource, SourceRead> reads = [];
    private static readonly Dictionary<ThemeSource, Task<SourceRead>> reading = [];

    /// <summary>A source was read, or began to be.</summary>
    public static event EventHandler? Changed;

    /// <summary>What a source listed when last read in this process; null before it was.</summary>
    public static SourceRead? Read(ThemeSource source) => reads.GetValueOrDefault(source);

    /// <summary>Whether a source is being read.</summary>
    public static bool IsReading(ThemeSource source) => reading.ContainsKey(source);

    /// <summary>Reads each source not read yet in this process (every one, <paramref name="again"/>), beside any read under way.</summary>
    public static void Look(IEnumerable<ThemeSource> sources, bool again = false)
    {
        foreach (ThemeSource source in sources.Where(s => s.Kind is ThemeSourceKind.Folder or ThemeSourceKind.GitHub or ThemeSourceKind.Web))
        {
            if (!reading.ContainsKey(source) && (again || !reads.ContainsKey(source)))
            {
                IdeErrors.Observe(ReadAsync(source));
            }
        }
    }

    /// <summary>A source read now, or the read under way, its answer kept for the lists.</summary>
    public static Task<SourceRead> ReadAsync(ThemeSource source)
    {
        if (reading.TryGetValue(source, out Task<SourceRead>? under))
        {
            return under;
        }
        Task<SourceRead> read = ReadOnceAsync(source);
        if (!read.IsCompleted)
        {
            reading[source] = read;
            Changed?.Invoke(null, EventArgs.Empty);
        }
        return read;
    }

    private static async Task<SourceRead> ReadOnceAsync(ThemeSource source)
    {
        IReadOnlyDictionary<string, ThemeTokenKind> kinds = ThemeResources.Kinds;
        SourceRead read;
        try
        {
            read = await BackgroundWork.Run(() => ThemeSourceReads.ReadAsync(source, kinds, CancellationToken.None));
        }
        finally
        {
            reading.Remove(source);
        }
        reads[source] = read;
        Changed?.Invoke(null, EventArgs.Empty);
        return read;
    }

    /// <summary>Every source read again, each theme or scheme kept from one taken anew where it changed there, and the
    /// themes in force laid again; how many were taken.</summary>
    public static async Task<int> RefreshAsync(IEnumerable<ThemeSource> sources)
    {
        SourceRead[] read = await Task.WhenAll(sources.Where(s => s.Kind is ThemeSourceKind.Folder or ThemeSourceKind.GitHub or ThemeSourceKind.Web).Select(AgainAsync));
        List<(ThemeSource Source, bool Code, string Name, byte[] Bytes)> newer = [];
        foreach (SourceRead source in read.Where(r => r.Refused is null))
        {
            foreach (ListedTheme listed in source.Listed)
            {
                if (listed is { Read.Read: { } file, Bytes: { } bytes } && KeptThemes.Bytes(source.Source, listed.Code, file.Name) is { } kept && !kept.AsSpan().SequenceEqual(bytes))
                {
                    newer.Add((source.Source, listed.Code, file.Name, bytes));
                }
            }
        }
        string?[] refused = await BackgroundWork.Run(() => newer.Select(n => KeptThemes.Keep(n.Source, n.Code, n.Name, n.Bytes)).ToArray());
        foreach (string why in refused.OfType<string>())
        {
            IdeErrors.Write($"Themes: {why}");
        }
        Themes.Reload();
        Changed?.Invoke(null, EventArgs.Empty);
        return refused.Count(r => r is null);

        // A read under way began before the refresh was asked, so the source is read once more after it.
        static async Task<SourceRead> AgainAsync(ThemeSource source)
        {
            if (reading.TryGetValue(source, out Task<SourceRead>? under))
            {
                await under;
            }
            return await ReadAsync(source);
        }
    }

    /// <summary>The themes or schemes kept from a source, each as read, for a list made without reading the source.</summary>
    public static IReadOnlyList<ThemeFile> Kept(ThemeSource source, bool code) =>
        [.. KeptThemes.Of(source, code).Select(bytes => code ? ThemeFiles.ReadCodeColours(source.Shown, bytes, ThemeResources.Kinds) : ThemeFiles.ReadTheme(source.Shown, bytes, ThemeResources.Kinds))
            .Select(r => r.Read).OfType<ThemeFile>()];
}
