using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Passenger.Core;

namespace Passenger.Debugger.Decompilation;

/// <summary>Symbols decompiled for assemblies that ship none, kept in the folder the client names: a portable PDB per
/// assembly version at <c>NAME/MVID/NAME.STAMP.pdb</c>, STAMP the decompiler's version and
/// <see cref="PortablePdbWriter2"/>'s, either of which moves the lines. Written whole (<see cref="UsedCache"/>), so an
/// adapter killed mid-write leaves no entry; one that does not load is deleted. Pruned as the adapter starts.</summary>
public sealed class SymbolCache
{
    /// <summary>An entry unused this long is deleted: .NET's patches come monthly, each runtime's a new CoreLib, so the
    /// version a machine has moved on from goes a month later.</summary>
    public static readonly TimeSpan UnusedFor = TimeSpan.FromDays(30);

    /// <summary>The most the entries may take, past which the least recently used go: CoreLib's, the largest a program
    /// loads, is about 5 MB, so a hundred of them, for libraries without symbols that are larger or many.</summary>
    public const long MostBytes = 512L << 20;

    /// <summary>An entry used this recently is never deleted: another session may be reading it.</summary>
    private static readonly TimeSpan InUse = TimeSpan.FromDays(1);

    /// <summary>A temporary file this old is a write that never finished, its adapter killed; a younger one may still be written.</summary>
    private static readonly TimeSpan Abandoned = TimeSpan.FromHours(1);

    /// <summary>The version of what Passenger's PDB writer makes, raised whenever a change to it moves what it maps.</summary>
    private const int Format = 1;

    /// <summary>What names each entry beside its assembly: another decompiler's lines, or another writer's, are not these.</summary>
    private static readonly string Stamp = $"{DecompilerVersionInfo.Version}-{Format}";

    private readonly UsedCache cache;
    private readonly Action<string>? log;

    /// <summary>The prune begun as the cache was opened, which every write waits for.</summary>
    private readonly Task pruned;

    /// <summary>Opened as the prune ends, for every read to wait on: a read comes from the frames' synchronous reads under the
    /// engine's lock, which cannot await the prune's task.</summary>
    private readonly ManualResetEventSlim prunedGate = new();

    public SymbolCache(string folder, Action<string>? log = null)
    {
        this.log = log;
        cache = new UsedCache(folder, UsedEntries.Files(3, ".pdb"), new UsedLimits(UnusedFor, InUse, MostBytes, Abandoned),
            log is null ? null : line => log($"Symbol cache: {line}"));
        // Not held: the cache's own chore, which the first write waits for.
        pruned = Core.BackgroundWork.Unheld(() =>
        {
            try
            {
                cache.Prune(DateTime.UtcNow);
            }
            finally
            {
                prunedGate.Set();
            }
        });
    }

    public string Folder => cache.Folder;

    /// <summary>Where an assembly version's decompiled PDB is kept.</summary>
    public string PathOf(string name, Guid mvid) => Path.Combine(Folder, name, mvid.ToString("N"), $"{name}.{Stamp}.pdb");

    /// <summary>An assembly version's decompiled PDB, marked as used now, or null when the cache has none.</summary>
    public string? Find(string name, Guid mvid)
    {
        prunedGate.Wait();
        string pdb = PathOf(name, mvid);
        if (!File.Exists(pdb))
        {
            return null;
        }
        // Pruned or held a moment ago, the load that follows says which.
        cache.Touch(pdb);
        return pdb;
    }

    /// <summary>Deletes a PDB that did not load, so the next session asking makes it again rather than refusing it for good.</summary>
    public void Discard(string pdb)
    {
        if (!cache.Remove(pdb))
        {
            log?.Invoke($"Symbol cache: {pdb} did not load and could not be deleted");
        }
    }

    /// <summary>Decompiles an assembly into its entry once the prune has ended, on a thread of its own (CoreLib's takes many
    /// seconds), written whole as it is made. False when it could not be decompiled or the session ended first
    /// (<paramref name="cancel"/>), its temporary file then deleted.</summary>
    public async Task<bool> WriteAsync(string assemblyPath, string name, Guid mvid, IReadOnlyList<string> references, CancellationToken cancel)
    {
        await pruned.ConfigureAwait(false);
        // Not held, on a thread of its own: a decompile blocks it for seconds.
        return await Core.BackgroundWork.UnheldThread(() => Write(assemblyPath, name, mvid, references, cancel)).ConfigureAwait(false);
    }

    private bool Write(string assemblyPath, string name, Guid mvid, IReadOnlyList<string> references, CancellationToken cancel)
    {
        string pdb = PathOf(name, mvid);
        try
        {
            using PEFile file = new(assemblyPath, PEStreamOptions.PrefetchEntireImage);
            DecompilerSettings settings = new();
            DecompilerTypeSystem types = new(file, new DebuggingAssemblyResolver([.. references]), settings);
            cache.Write(pdb, stream => new PortablePdbWriter2 { NoLogo = true }.WritePdb(file, types, settings, stream, cancellationToken: cancel));
            return true;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            log?.Invoke($"Symbol cache: decompiling {name} was given up as the session ended");
        }
        catch (Exception error)
        {
            log?.Invoke($"Symbol cache: {name} ({assemblyPath}) could not be decompiled: {error}");
        }
        return false;
    }
}
