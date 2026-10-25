using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>Where profiled methods are written, from their modules' portable PDBs (beside the module or embedded in
/// it): a method's file and the line of its first sequence point. Each module is read once, and let go with
/// this.</summary>
public sealed class MethodSources : IDisposable
{
    private readonly Dictionary<string, (PEReader Module, MetadataReaderProvider? Pdb)?> read = new(StringComparer.Ordinal);

    /// <summary>The method's source file, as its PDB names it, and line; null where the module has no PDB that matches it
    /// (a framework's or a package's, or one built since) or the method no sequence point (a generated one).</summary>
    public (string File, int Line)? Find(MethodCode code)
    {
        if ((code.Token >>> 24) != 0x06)
        {
            return null;
        }
        if (!read.TryGetValue(code.ModuleFile, out (PEReader Module, MetadataReaderProvider? Pdb)? opened))
        {
            read[code.ModuleFile] = opened = Open(code.ModuleFile);
        }
        if (opened?.Pdb is not { } pdb)
        {
            return null;
        }
        try
        {
            MetadataReader reader = pdb.GetMetadataReader();
            MethodDefinitionHandle method = MetadataTokens.MethodDefinitionHandle(code.Token & 0xFFFFFF);
            if (MetadataTokens.GetRowNumber(method) > reader.MethodDebugInformation.Count)
            {
                return null;
            }
            foreach (SequencePoint point in reader.GetMethodDebugInformation(method).GetSequencePoints())
            {
                if (!point.IsHidden)
                {
                    return (reader.GetString(reader.GetDocument(point.Document).Name), point.StartLine);
                }
            }
            return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>Where some of a snapshot's methods are written, those found: from their modules' PDBs here, or, for a
    /// run of a program in a container (<see cref="Snapshot.Place"/>), there by Passenger's tools (<c>sources</c>),
    /// each document then made this machine's by the place's map. Off the caller's thread; a container gone, or tools
    /// that fail, find none.</summary>
    public static async Task<IReadOnlyDictionary<MethodCode, (string File, int Line)>> FindAsync(Snapshot snapshot, IEnumerable<MethodCode> codes,
        CancellationToken cancel = default)
    {
        MethodCode[] asked = [.. codes.Distinct()];
        Dictionary<MethodCode, (string File, int Line)> found = [];
        if (asked.Length == 0)
        {
            return found;
        }
        if (snapshot.Place is not { } place)
        {
            // Not held, and back on no thread of the caller's: whoever asked waits for what it finds.
            return await BackgroundWork.Unheld(() =>
            {
                using MethodSources sources = new();
                foreach (MethodCode code in asked)
                {
                    if (sources.Find(code) is { } at)
                    {
                        found[code] = at;
                    }
                }
                return found;
            }, cancel).ConfigureAwait(false);
        }
        string input = string.Concat(asked.Select(c => $"{c.Token.ToString(CultureInfo.InvariantCulture)}\t{c.ModuleFile}\n"));
        ChildResult answered;
        try
        {
            // A line a method, its document's path and line: what a container sends beyond that is no answer, and is not read.
            answered = await ChildProcesses.RunAsync(place.Tools(["sources"]), input, TimeSpan.FromSeconds(30), cancel,
                mostOutput: MostSources(asked.Length)).ConfigureAwait(false);
        }
        catch (Exception failed) when (failed is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return found;
        }
        if (!answered.Succeeded)
        {
            return found;
        }
        string[] lines = answered.Output.Split('\n');
        for (int i = 0; i < asked.Length && i < lines.Length; i++)
        {
            string[] parts = lines[i].TrimEnd('\r').Split('\t');
            if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int line))
            {
                found[asked[i]] = (place.ToHost(parts[0]), line);
            }
        }
        return found;
    }

    /// <summary>The most read of the tools' <c>sources</c> answer, and of their errors, for so many methods: a path of a few
    /// thousand characters and its line each; past it the run is ended and nothing is found.</summary>
    private static int MostSources(int methods) => 64 * 1024 + methods * 4096;

    public void Dispose()
    {
        foreach ((PEReader Module, MetadataReaderProvider? Pdb)? opened in read.Values)
        {
            opened?.Pdb?.Dispose();
            opened?.Module.Dispose();
        }
        read.Clear();
    }

    private static (PEReader, MetadataReaderProvider?)? Open(string named)
    {
        if (OnDisk(named) is not { } module)
        {
            return null;
        }
        PEReader? pe = null;
        try
        {
            pe = new PEReader(File.OpenRead(module));
            pe.TryOpenAssociatedPortablePdb(module, path => File.Exists(path) ? File.OpenRead(path) : null, out MetadataReaderProvider? pdb, out _);
            return (pe, pdb);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            pe?.Dispose();
            return null;
        }
    }

    /// <summary>A module's file as it is on disk: the trace can name it in lower case (a container's /passenger/app/web.dll for
    /// Web.dll), which a file system that tells case apart (Linux's, a container's over it) does not find as named, so each
    /// part of the path is matched ignoring case where it is not there as written. Null when there is no such file.</summary>
    private static string? OnDisk(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }
        string at = root;
        try
        {
            foreach (string part in path[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
            {
                string exact = Path.Combine(at, part);
                at = Directory.Exists(exact) || File.Exists(exact) ? exact
                    : Directory.EnumerateFileSystemEntries(at).FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase)) is { } found
                        ? found : string.Empty;
                if (at.Length == 0)
                {
                    return null;
                }
            }
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return File.Exists(at) ? at : null;
    }
}
