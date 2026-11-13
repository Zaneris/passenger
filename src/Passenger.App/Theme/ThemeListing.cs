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
        List<(ListedTheme Listed, string Name, byte[] Bytes)> held = [];
        foreach (SourceRead source in read.Where(r => r.Refused is null))
        {
            foreach (ListedTheme listed in source.Listed)
            {
                if (listed is { Read.Read: { } file, Bytes: { } bytes } && KeptThemes.Holds(source.Source, listed.Code, file.Name))
                {
                    held.Add((listed, file.Name, bytes));
                }
            }
        }
        ThemeSource[] keeping = [.. sources, ThemeSource.Added, ThemeSource.OpenVsx];
        List<(ThemeSource Source, string Name, VsCodeBase Base)> bases = [.. keeping.SelectMany(source => Kept(source, false)
            .Where(file => file.Base is not null).Select(file => (source, file.Name, file.Base!)))];
        string?[] refused = await BackgroundWork.Run(async () =>
        {
            List<string?> said = [];
            // A base pinned to its version is taken again only where another is kept; the rest take Open VSX's newest.
            foreach ((ThemeSource source, string name, VsCodeBase wanted) in bases)
            {
                string? kept = VsCodeBases.KeptVersion(source, false, name);
                if (wanted.Version is { } pinned && kept == pinned)
                {
                    continue;
                }
                (OpenVsxEntry? newest, string? missing) = await OpenVsx.ExtensionAsync(wanted.Id, wanted.Version, CancellationToken.None);
                if (newest is null || newest.Version == kept)
                {
                    said.AddRange(newest is null ? [$"{name}'s VS Code theme could not be refreshed: {missing}"] : []);
                    continue;
                }
                (VsCodeTaken? taken, string? why) = await VsCodeBases.FetchAsync(wanted with { Version = newest.Version }, CancellationToken.None);
                said.Add(taken is null ? why : KeptThemes.KeepBase(source, false, name, taken.Files));
            }
            foreach ((ListedTheme listed, string name, byte[] bytes) in held)
            {
                (Dictionary<string, byte[]> images, string? unseen) = await ThemeSourceReads.ImagesAsync(listed, listed.Source.Reached, CancellationToken.None);
                bool changed = unseen is null && (KeptThemes.Bytes(listed.Source, listed.Code, name) is not { } kept || !kept.AsSpan().SequenceEqual(bytes)
                    || images.Any(i => KeptThemes.Image(listed.Source, listed.Code, name, i.Key) is not { } picture || !picture.AsSpan().SequenceEqual(i.Value)));
                if (unseen is not null || changed)
                {
                    said.Add(unseen ?? KeptThemes.Keep(listed.Source, listed.Code, name, bytes, images));
                }
            }
            return said.ToArray();
        });
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
