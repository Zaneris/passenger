using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Xaml;

/// <summary>A library's own files, read where its build says they are: what a theme's XAML compiled away (Fluent's
/// Button.xaml) for go to declaration into a control's template. Its answer is a local file to open read-only.</summary>
public interface ILibraryFiles
{
    /// <summary>A file of the project an assembly was built from, by its path in that project (<c>/Controls/Button.xaml</c>);
    /// null when it cannot be had, with why in <paramref name="why"/>.</summary>
    Task<string?> FileAsync(string assembly, string assemblyName, string path, IProgress<string>? why, CancellationToken cancel);
}

/// <summary>A library's files through its Source Link: the portable PDB its assembly names, from NuGet's symbol server
/// (<c>PASSENGER_SYMBOLS</c> names another), its Source Link map from the build's paths to the repository's, and the
/// project folder its documents are in; each file downloaded once over HTTPS into MetadataAsSource/SourceLink/, which the
/// editor shows read-only, written whole or not at all and never outside its assembly version's folder.</summary>
public sealed class SourceLink : ILibraryFiles
{
    [IdentifierHome]
    public const string Variable = "PASSENGER_SYMBOLS";

    private static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>The symbol server asked for PDBs.</summary>
    public static string Server => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } set ? set.TrimEnd('/') + "/" : "https://symbols.nuget.org/download/symbols/";

    public async Task<string?> FileAsync(string assembly, string assemblyName, string path, IProgress<string>? why, CancellationToken cancel)
    {
        // The path comes from the library's own XAML (an include's Source), so it must not lead out of the version's folder.
        (string? folder, string? local) = await BackgroundWork.Run(() => LibrarySources.Entry("SourceLink", assemblyName, assembly) is { } entry
            ? (entry, LibrarySources.Inside(entry, path.Replace('/', Path.DirectorySeparatorChar)))
            : (null, null));
        if (folder is null || local is null)
        {
            why?.Report($"{assemblyName}'s {path} leads outside its sources' folder, so it was not downloaded");
            return null;
        }
        if (File.Exists(local))
        {
            return local;
        }
        try
        {
            if (await PdbAsync(assembly, folder, cancel) is not { } pdb)
            {
                why?.Report($"{assemblyName} names no PDB on the symbol server");
                return null;
            }
            (Dictionary<string, string> map, string? project) = Read(pdb, assemblyName);
            string relative = Path.GetRelativePath(PathNames.Canonical(folder), local).Replace('\\', '/');
            if (project is null || Map(map, project + relative) is not { } url)
            {
                why?.Report($"{assemblyName}'s PDB has no Source Link for {path}");
                return null;
            }
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                why?.Report($"{assemblyName}'s Source Link is not HTTPS: {url}");
                return null;
            }
            byte[] bytes = await Http.GetByteArrayAsync(url, cancel);
            await BackgroundWork.Run(() => LibrarySources.WriteWhole(local, bytes));
            return local;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or BadImageFormatException or JsonException
            or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            why?.Report($"{assemblyName}'s sources could not be downloaded: {error.Message}");
            return null;
        }
    }

    /// <summary>The portable PDB an assembly names in its CodeView entry, downloaded once; null for one with none.</summary>
    private static async Task<string?> PdbAsync(string assembly, string folder, CancellationToken cancel)
    {
        CodeViewDebugDirectoryData codeView;
        List<string> checksums = [];
        using (FileStream stream = File.OpenRead(assembly))
        using (PEReader pe = new(stream))
        {
            IReadOnlyList<DebugDirectoryEntry> entries = pe.ReadDebugDirectory();
            DebugDirectoryEntry? entry = entries.Where(e => e.Type == DebugDirectoryEntryType.CodeView).Cast<DebugDirectoryEntry?>().FirstOrDefault();
            if (entry is not { } found)
            {
                return null;
            }
            codeView = pe.ReadCodeViewDebugDirectoryData(found);
            foreach (DebugDirectoryEntry checksum in entries.Where(e => e.Type == DebugDirectoryEntryType.PdbChecksum))
            {
                PdbChecksumDebugDirectoryData data = pe.ReadPdbChecksumDebugDirectoryData(checksum);
                checksums.Add($"{data.AlgorithmName}:{Convert.ToHexString(data.Checksum.AsSpan())}");
            }
        }
        string name = Path.GetFileName(codeView.Path.Replace('\\', '/'));
        if (LibrarySources.Inside(folder, name) is not { } pdb)
        {
            return null;
        }
        if (File.Exists(pdb))
        {
            return pdb;
        }
        // A portable PDB's key on a symbol server is its id with FFFFFFFF where a Windows PDB's age would be.
        string url = $"{Server}{Uri.EscapeDataString(name)}/{codeView.Guid:N}FFFFFFFF/{Uri.EscapeDataString(name)}";
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        // NuGet's symbol server gives a portable PDB only to a request naming the checksum the assembly holds of it.
        if (checksums.Count > 0)
        {
            request.Headers.Add("SymbolChecksum", string.Join(';', checksums));
        }
        using HttpResponseMessage response = await Http.SendAsync(request, cancel);
        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancel);
        await BackgroundWork.Run(() => LibrarySources.WriteWhole(pdb, bytes));
        return pdb;
    }

    /// <summary>A PDB's Source Link map, and the folder of the project the assembly was built from: the one of its
    /// documents' folders named after the assembly, else their common folder.</summary>
    private static (Dictionary<string, string> Map, string? Project) Read(string pdb, string assemblyName)
    {
        using FileStream stream = File.OpenRead(pdb);
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        MetadataReader reader = provider.GetMetadataReader();
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (CustomDebugInformationHandle handle in reader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
        {
            CustomDebugInformation information = reader.GetCustomDebugInformation(handle);
            if (reader.GetGuid(information.Kind) != SourceLinkKind)
            {
                continue;
            }
            using JsonDocument json = JsonDocument.Parse(reader.GetBlobBytes(information.Value));
            if (json.RootElement.TryGetProperty("documents", out JsonElement documents))
            {
                foreach (JsonProperty document in documents.EnumerateObject())
                {
                    map[document.Name.Replace('\\', '/')] = document.Value.GetString() ?? string.Empty;
                }
            }
        }
        List<string> folders = [.. reader.Documents.Select(d => reader.GetString(reader.GetDocument(d).Name).Replace('\\', '/'))
            .Select(p => p[..(p.LastIndexOf('/') + 1)])];
        string? project = folders.Where(f => f.EndsWith($"/{assemblyName}/", StringComparison.Ordinal)).OrderBy(f => f.Length).FirstOrDefault()
            ?? Common(folders);
        return (map, project);
    }

    private static string? Common(List<string> folders)
    {
        if (folders.Count == 0)
        {
            return null;
        }
        string common = folders[0];
        foreach (string folder in folders)
        {
            while (!folder.StartsWith(common, StringComparison.Ordinal))
            {
                common = common[..(common.TrimEnd('/').LastIndexOf('/') + 1)];
            }
        }
        return common.Length > 0 ? common : null;
    }

    /// <summary>A build path's URL by a Source Link map: an exact entry, else the longest pattern ending in * it starts
    /// with, the rest put in the *'s place.</summary>
    private static string? Map(Dictionary<string, string> map, string path)
    {
        if (map.TryGetValue(path, out string? exact))
        {
            return exact;
        }
        foreach ((string pattern, string url) in map.OrderByDescending(m => m.Key.Length))
        {
            if (pattern.EndsWith('*') && path.StartsWith(pattern[..^1], StringComparison.Ordinal) && url.Contains('*', StringComparison.Ordinal))
            {
                return url.Replace("*", path[(pattern.Length - 1)..], StringComparison.Ordinal);
            }
        }
        return null;
    }
}
