using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>
/// Tracks information about a loaded module including its symbol reader
/// </summary>
public class ModuleInfo : IDisposable
{
    /// <summary>
    /// The ICorDebugModule for this module
    /// </summary>
    public ICorDebugModule Module { get; }

    /// <summary>
    /// Full path to the module on disk (if available)
    /// </summary>
    public string ModulePath { get; }

    /// <summary>
    /// Module name (typically the filename without path)
    /// </summary>
    public string ModuleName { get; }

    /// <summary>
    /// Symbol reader for this module (null if no PDB available)
    /// </summary>
    public ModuleMetadataReader MetadataReader { get; private set; }
    public bool SymbolsFromDecompiled { get; set; }

    /// <summary>
    /// Base address of the module in memory, which keys it; one emitted as the program runs has none, and a key of its own.
    /// </summary>
    public CORDB_ADDRESS BaseAddress { get; }
    public bool IsUserCode { get; }

    /// <summary>Made as the program runs (System.Reflection.Emit): no file, no image, its metadata the runtime's own.</summary>
    public bool IsDynamic { get; }

    public ModuleInfo(ICorDebugModule module, string modulePath, ModuleMetadataReader metadataReader, bool isUserCode, CORDB_ADDRESS? emittedKey = null)
    {
        Module = module;
        ModulePath = modulePath;
        IsUserCode = isUserCode;
        ModuleName = Path.GetFileName(modulePath);
        MetadataReader = metadataReader;
        IsDynamic = emittedKey is not null;
        BaseAddress = emittedKey ?? module.BaseAddress;
    }

    /// <summary>An emitted module the runtime has added to (a type emitted) since its metadata was read.</summary>
    internal bool Stale { get; set; }

    /// <summary>Whether the runtime has Edit and Continue on for the module, so Hot Reload can change its code; and why not
    /// when it has not.</summary>
    internal bool Editable { get; set; }

    internal string? WhyNotEditable { get; set; } = "The module was loaded before the debugger could ask the runtime about it";

    /// <summary>The module's metadata as Hot Reload's updates have made it, in memory that does not move; null before any.</summary>
    internal byte[]? CurrentMetadata { get; set; }

    /// <summary>Copies of an emitted module's metadata read before the current one, kept with the module, as a request may
    /// still be reading one.</summary>
    private readonly List<ModuleMetadataReader> _superseded = [];

    /// <summary>An emitted module's metadata read again, once the runtime has added to it.</summary>
    internal void Reload(ModuleMetadataReader metadataReader)
    {
        _superseded.Add(MetadataReader);
        MetadataReader = metadataReader;
        Stale = false;
    }

    public void Dispose()
    {
        MetadataReader.Dispose();
        foreach (ModuleMetadataReader superseded in _superseded)
        {
            superseded.Dispose();
        }
    }
}
