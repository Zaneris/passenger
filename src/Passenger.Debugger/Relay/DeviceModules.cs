using System.Runtime.InteropServices;

namespace Passenger.Debugger.Relay;

/// <summary>The host's copies of what an app on a device loads: the build's folder of the assemblies it deployed for
/// the device's processor, each with its PDB. A module the app names by a device path is read here by its file name,
/// its metadata opened by the host's own runtime (MetaDataGetDispenser), so no metadata call crosses to the app; one
/// scope a module, which each Hot Reload update the app takes is applied to (<see cref="Updated"/>), as the app's own
/// debugger applies it to its copy.</summary>
internal sealed unsafe class DeviceModules
{
    private static readonly Guid s_dispenserClass = new("E5CB7A31-7512-11D2-89CE-0080C792E5D8");
    private static readonly Guid s_dispenserInterface = new("809C652E-7396-11D2-9771-00A0C9B4D50C");
    private static readonly Guid s_importInterface = new("7DAC8207-D3AE-4C75-9B67-92801A497D44");
    private static readonly Guid s_emitInterface = new("BA3FEE4C-ECB9-4E41-83B7-183FA41CD859");

    /// <summary>ofCopyMemory: a scope opened on memory keeps a copy of its own.</summary>
    private const uint CopyMemory = 0x2;

    /// <summary>Zeros kept past metadata from an app's memory, more than a table's row or a stream's header holds, so a read
    /// that runs past the metadata's end by one of them reads these and nothing of the process's.</summary>
    private const int ZerosPast = 256;

    /// <summary>IMetaDataEmit::ApplyEditAndContinue's slot: cor.h's 44th method of the interface, after IUnknown's three.</summary>
    private const int ApplyEditAndContinueSlot = 3 + 43;

    private readonly Dictionary<string, string> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, nint> _scopes = new(StringComparer.Ordinal);
    private nint _dispenser;

    public DeviceModules(string? folder)
    {
        if (folder is not null && Directory.Exists(folder))
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.dll"))
            {
                _byName[Path.GetFileName(file)] = file;
            }
        }
    }

    public string? HostFileOf(string onDevice)
    {
        int slash = onDevice.LastIndexOfAny(['/', '\\']);
        string name = slash >= 0 ? onDevice[(slash + 1)..] : onDevice;
        // CoreLib, loaded from the runtime's own store, is named without its file's extension.
        return _byName.GetValueOrDefault(name) ?? _byName.GetValueOrDefault(name + ".dll");
    }

    /// <summary>The key a module's scope is kept by: its host file, else its identity in the app, whose memory it was read from.</summary>
    public static string KeyOf(string? file, ulong module) => file ?? $"memory:{module:x}";

    /// <summary>A module's metadata interface, one scope per file kept open for the session, whichever interface is asked
    /// for: pointers into its storage (the evaluator's metadata blocks) stay good as long as the module is loaded, as the
    /// app's own runtime's would.</summary>
    public int OpenMetadata(string file, Guid* iid, nint* result)
    {
        lock (_scopes)
        {
            if (!_scopes.TryGetValue(file, out nint scope))
            {
                int opened = OpenScope(file, &scope);
                if (opened < 0)
                {
                    *result = 0;
                    return opened;
                }
                _scopes[file] = scope;
            }
            return Query(scope, iid, result);
        }
    }

    /// <summary>A scope over metadata read out of the app's memory, kept for the session as a file's is (the evaluator's
    /// metadata blocks point into it); <paramref name="metadata"/> has passed <see cref="DeviceMetadata.Refused"/>.</summary>
    public int OpenMetadataInMemory(ulong module, byte[] metadata, Guid* iid, nint* result)
    {
        string key = KeyOf(null, module);
        lock (_scopes)
        {
            if (!_scopes.TryGetValue(key, out nint scope))
            {
                int made = Dispenser();
                if (made < 0)
                {
                    *result = 0;
                    return made;
                }
                nint copy = (nint)NativeMemory.AllocZeroed((nuint)metadata.Length + ZerosPast);
                metadata.CopyTo(new Span<byte>((void*)copy, metadata.Length));
                nint* vtable = *(nint**)_dispenser;
                Guid import = s_importInterface;
                // IMetaDataDispenser::OpenScopeOnMemory(data, size, ofRead, riid, out scope)
                int opened = ((delegate* unmanaged[Cdecl]<nint, nint, uint, uint, Guid*, nint*, int>)vtable[5])(_dispenser, copy, (uint)metadata.Length, 0, &import, &scope);
                if (opened < 0)
                {
                    NativeMemory.Free((void*)copy);
                    *result = 0;
                    return opened;
                }
                _scopes[key] = scope;
            }
            return Query(scope, iid, result);
        }
    }

    /// <summary>An update the app's runtime took applied to the host's scope of its module (kept by <see cref="KeyOf"/>), as
    /// <paramref name="delta"/> holds it, so what the engine reads of the module after is the module as updated: false
    /// where no scope of it is open (none was read, so the next read is of the module before the update) or the update did
    /// not apply.</summary>
    public bool Updated(string key, ReadOnlySpan<byte> delta)
    {
        lock (_scopes)
        {
            if (!_scopes.TryGetValue(key, out nint scope) || Dispenser() < 0)
            {
                return false;
            }
            nint updates = 0;
            Guid import = s_importInterface;
            fixed (byte* data = delta)
            {
                // IMetaDataDispenser::OpenScopeOnMemory(data, size, ofCopyMemory, riid, out scope)
                if (((delegate* unmanaged[Cdecl]<nint, byte*, uint, uint, Guid*, nint*, int>)(*(nint**)_dispenser)[5])(_dispenser, data, (uint)delta.Length, CopyMemory, &import, &updates) < 0)
                {
                    return false;
                }
            }
            nint emit = 0;
            try
            {
                Guid emitting = s_emitInterface;
                return Query(scope, &emitting, &emit) >= 0
                    && ((delegate* unmanaged[Cdecl]<nint, nint, int>)(*(nint**)emit)[ApplyEditAndContinueSlot])(emit, updates) >= 0;
            }
            finally
            {
                Release(emit);
                Release(updates);
            }
        }
    }

    /// <summary>IUnknown::QueryInterface of a scope, which hands back a reference of the caller's own.</summary>
    private static int Query(nint scope, Guid* iid, nint* result) =>
        ((delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)(*(nint**)scope)[0])(scope, iid, result);

    private static void Release(nint unknown)
    {
        if (unknown != 0)
        {
            ((delegate* unmanaged[Cdecl]<nint, uint>)(*(nint**)unknown)[2])(unknown);
        }
    }

    private int Dispenser()
    {
        if (_dispenser != 0)
        {
            return 0;
        }
        string runtime = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), OperatingSystem.IsWindows() ? "coreclr.dll" : OperatingSystem.IsMacOS() ? "libcoreclr.dylib" : "libcoreclr.so");
        nint library = NativeLibrary.Load(runtime);
        delegate* unmanaged[Cdecl]<Guid*, Guid*, nint*, int> getDispenser = (delegate* unmanaged[Cdecl]<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(library, "MetaDataGetDispenser");
        Guid dispenserClass = s_dispenserClass;
        Guid dispenserInterface = s_dispenserInterface;
        nint dispenser = 0;
        int made = getDispenser(&dispenserClass, &dispenserInterface, &dispenser);
        if (made >= 0)
        {
            _dispenser = dispenser;
        }
        return made;
    }

    private int OpenScope(string file, nint* result)
    {
        int made = Dispenser();
        if (made < 0)
        {
            return made;
        }
        nint* vtable = *(nint**)_dispenser;
        Guid import = s_importInterface;
        fixed (char* path = file)
        {
            // IMetaDataDispenser::OpenScope(name, ofRead, riid, out scope)
            return ((delegate* unmanaged[Cdecl]<nint, char*, uint, Guid*, nint*, int>)vtable[4])(_dispenser, path, 0, &import, result);
        }
    }
}
