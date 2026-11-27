using Passenger.Core;

namespace Passenger.App;

/// <summary>What each theme source lists, as last read in this process: read when a person looks at its themes in Settings
/// (once, until a refresh asks again), never at a start; the built-in source read from the app and the added themes from
/// where they are kept, never from the web, each kept one read off the window's thread and again only once its file's stamp
/// moved. A refresh reads every source again and takes the newer copy of each theme kept from it. UI thread only.</summary>
public static class ThemeListing
{
    private static readonly Dictionary<ThemeSource, SourceRead> reads = [];
    private static readonly Dictionary<ThemeSource, Task<SourceRead>> reading = [];

    /// <summary>The themes and schemes kept from each source as last read, by source and kind, with the stamps of their files then.</summary>
    private static readonly Dictionary<(ThemeSource Source, bool Code), KeptRead> kept = [];

    /// <summary>The sources whose kept themes are to be looked at by the look under way (<see cref="keptLook"/>), once it is
    /// done with those it took.</summary>
    private static readonly HashSet<ThemeSource> keptWanted = [];

    private static Task keptLook = Task.CompletedTask;
    private static bool keptLooking;

    /// <summary>A source's kept themes or schemes as read, and the stamps of the files they were read from.</summary>
    private sealed record KeptRead(IReadOnlyList<ThemeFile> Files, IReadOnlyList<(string Name, FileStamp? Stamp)> Stamps);

    /// <summary>A source was read, or began to be, or what is kept from one came, went or changed.</summary>
    public static event EventHandler? Changed;

    /// <summary>How many times <see cref="Changed"/> was raised: a list made at one count shows what the next would.</summary>
    public static int Version { get; private set; }

    private static void Change()
    {
        Version++;
        Changed?.Invoke(null, EventArgs.Empty);
    }

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
            Change();
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
        Change();
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
        await LookKeptAsync(keeping);
        List<(ThemeSource Source, bool Code, string Name, VsCodeBase Base)> bases = [.. keeping.SelectMany(source => ((bool[])[false, true])
            .SelectMany(code => Kept(source, code).Where(file => file.Base is { Id: not null }).Select(file => (source, code, file.Name, file.Base!))))];
        List<(ThemeSource Source, string Name, VsCodeBase IconTheme)> iconSets = [.. keeping.SelectMany(source => Kept(source, false)
            .SelectMany(file => file.IconThemes.Where(i => i.Id is not null).Select(i => (source, file.Name, i))))];
        string?[] refused = await BackgroundWork.Run(async () =>
        {
            List<string?> said = [];
            // A base pinned to its version is taken again only where another is kept; the rest take Open VSX's newest.
            foreach ((ThemeSource source, bool code, string name, VsCodeBase wanted) in bases)
            {
                string? kept = VsCodeBases.KeptVersion(source, code, name);
                if (wanted.Version is { } pinned && kept == pinned)
                {
                    continue;
                }
                (OpenVsxEntry? newest, string? missing) = await OpenVsx.ExtensionAsync(wanted.Id!, wanted.Version, CancellationToken.None);
                if (newest is null || newest.Version == kept)
                {
                    said.AddRange(newest is null ? [$"{name}'s VS Code theme could not be refreshed: {missing}"] : []);
                    continue;
                }
                (VsCodeTaken? taken, string? why) = await VsCodeBases.FetchAsync(wanted with { Version = newest.Version }, CancellationToken.None);
                said.Add(taken is null ? why : KeptThemes.KeepBase(source, code, name, taken.Files));
            }
            // A file icon theme by its extension's id, as a base.
            foreach ((ThemeSource source, string name, VsCodeBase wanted) in iconSets)
            {
                string? kept = VsCodeIconSets.KeptVersion(wanted, path => KeptThemes.Beside(source, false, name, path));
                if (wanted.Version is { } pinned && kept == pinned)
                {
                    continue;
                }
                (OpenVsxEntry? newest, string? missing) = await OpenVsx.ExtensionAsync(wanted.Id!, wanted.Version, CancellationToken.None);
                if (newest is null || newest.Version == kept)
                {
                    said.AddRange(newest is null ? [$"{name}'s file icon theme could not be refreshed: {missing}"] : []);
                    continue;
                }
                (VsCodeIconTaken? taken, string? why) = await VsCodeIconSets.FetchAsync(wanted with { Version = newest.Version }, CancellationToken.None);
                said.Add(taken is null ? why : KeptThemes.KeepUnder(source, false, name, VsCodeIconSets.KeptUnder(wanted), taken.Files));
            }
            foreach ((ListedTheme listed, string name, byte[] bytes) in held)
            {
                (Dictionary<string, byte[]> images, string? unseen) = await ThemeSourceReads.ImagesAsync(listed, listed.Source.Reached, CancellationToken.None);
                bool changed = unseen is null && (KeptThemes.Bytes(listed.Source, listed.Code, name) is not { } kept || !kept.AsSpan().SequenceEqual(bytes)
                    || images.Any(i => KeptThemes.Beside(listed.Source, listed.Code, name, i.Key) is not { } beside || !beside.AsSpan().SequenceEqual(i.Value)));
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
        await LookKeptAsync(keeping);
        Change();
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

    /// <summary>A theme or scheme named by its source and name made ready to wear, as choosing it in Settings does: one carried
    /// or kept as it is, another found in its source's listing (read again where the last read lacks it) and kept; why not,
    /// naming what the source lists, where it is no source Passenger reads, lists none of that name or cannot be read.</summary>
    public static async Task<string?> TakeAsync(SourcedTheme theme, bool code)
    {
        string kind = code ? "code colour scheme" : "theme";
        if (theme.From is not { } source)
        {
            return $"{theme.Source} names no folder, repository or address a {kind} comes from";
        }
        if (source == ThemeSource.BuiltIn ? BuiltInThemes.Bytes(code, theme.Name) is not null : KeptThemes.Holds(source, code, theme.Name))
        {
            return null;
        }
        IEnumerable<string?> names;
        if (source.Kind is ThemeSourceKind.Folder or ThemeSourceKind.GitHub or ThemeSourceKind.Web)
        {
            SourceRead read = Read(source) is { } last && Find(last) is not null ? last : await ReadAsync(source);
            if (read.Refused is { } refused)
            {
                return $"{source.Shown} could not be read: {refused}";
            }
            if (Find(read) is { } found)
            {
                return await KeepAsync(found, theme.Name);
            }
            names = read.Listed.Where(l => l.Code == code).Select(l => l.Read.Read?.Name);
        }
        else
        {
            await LookKeptAsync([source]);
            names = source == ThemeSource.BuiltIn ? BuiltInThemes.Listed(ThemeResources.Kinds).Listed.Where(l => l.Code == code).Select(l => l.Read.Read?.Name)
                : Kept(source, code).Select(f => f.Name);
        }
        string[] listed = [.. names.OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return $"{source.Shown} lists no {kind} named {theme.Name}" + (listed.Length == 0 ? string.Empty : $"; it lists {Plural.Listed(listed)}");

        ListedTheme? Find(SourceRead read) => read.Listed.FirstOrDefault(l => l.Code == code && l.Read.Read?.Name == theme.Name && l.Bytes is not null);
    }

    /// <summary>A theme or scheme a source listed, kept from what was read of it with its pictures and its VS Code base, so
    /// it is worn at once, and what is kept from the source looked at again; why not where they cannot be had.</summary>
    public static async Task<string?> KeepAsync(ListedTheme listed, string name)
    {
        string? why = await BackgroundWork.Run(async () =>
        {
            (Dictionary<string, byte[]> images, string? unseen) = await ThemeSourceReads.ImagesAsync(listed, listed.Source.Reached, CancellationToken.None);
            return unseen ?? (listed.Read.Read is { } file ? await VsCodeBases.KeepAsync(listed.Source, listed.Code, name, file, CancellationToken.None)
                ?? await VsCodeIconSets.KeepAsync(listed.Source, listed.Code, name, file, CancellationToken.None) : null)
                ?? KeptThemes.Keep(listed.Source, listed.Code, name, listed.Bytes!, images);
        });
        await LookKeptAsync([listed.Source]);
        return why;
    }

    /// <summary>The themes or schemes kept from a source as last read (<see cref="LookKeptAsync"/>), for a list made without
    /// reading the source or the disk: none before the first look at it has landed.</summary>
    public static IReadOnlyList<ThemeFile> Kept(ThemeSource source, bool code) => kept.GetValueOrDefault((source, code))?.Files ?? [];

    /// <summary>Whether what is kept from a source has been read in this process (<see cref="Kept"/>).</summary>
    public static bool KnowsKept(ThemeSource source) => kept.ContainsKey((source, false));

    /// <summary>The themes and schemes kept from each source read again off the window's thread where the stamps of their
    /// files moved since the last read, <see cref="Changed"/> raised once as the look lands where any came, went or changed;
    /// one look at a time, the sources asked for meanwhile looked at once it is done with its own. Done with them all.</summary>
    public static Task LookKeptAsync(IEnumerable<ThemeSource> sources)
    {
        keptWanted.UnionWith(sources);
        if (!keptLooking)
        {
            keptLooking = true;
            keptLook = LookKeptAgainAsync();
        }
        return keptLook;
    }

    private static async Task LookKeptAgainAsync()
    {
        try
        {
            bool changed = false;
            while (keptWanted.Count > 0)
            {
                IReadOnlyDictionary<string, ThemeTokenKind> kinds = ThemeResources.Kinds;
                (ThemeSource Source, bool Code, KeptRead? Was)[] looked = [.. keptWanted.SelectMany(source => ((bool[])[false, true]).Select(code => (source, code, kept.GetValueOrDefault((source, code)))))];
                keptWanted.Clear();
                KeptRead[] read = await BackgroundWork.Run(() => looked.Select(l => ReadKept(l.Source, l.Code, l.Was, kinds)).ToArray());
                for (int i = 0; i < looked.Length; i++)
                {
                    if (!ReferenceEquals(read[i], looked[i].Was))
                    {
                        kept[(looked[i].Source, looked[i].Code)] = read[i];
                        changed = true;
                    }
                }
            }
            if (changed)
            {
                Change();
            }
        }
        finally
        {
            keptLooking = false;
        }
    }

    /// <summary>A source's kept themes or schemes as they stand: <paramref name="was"/> where no file's stamp moved since it
    /// was read, else each read anew, the stamps taken first, so a file written meanwhile is read again by the next look.</summary>
    private static KeptRead ReadKept(ThemeSource source, bool code, KeptRead? was, IReadOnlyDictionary<string, ThemeTokenKind> kinds)
    {
        IReadOnlyList<(string Name, FileStamp? Stamp)> stamps = KeptThemes.Stamps(source, code);
        if (was is not null && was.Stamps.SequenceEqual(stamps))
        {
            return was;
        }
        return new KeptRead([.. KeptThemes.Of(source, code).Select(bytes => code ? ThemeFiles.ReadCodeColours(source.Shown, bytes, kinds) : ThemeFiles.ReadTheme(source.Shown, bytes, kinds))
            .Select(r => r.Read).OfType<ThemeFile>()], stamps);
    }
}
