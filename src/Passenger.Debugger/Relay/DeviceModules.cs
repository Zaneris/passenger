using System.Runtime.InteropServices;

namespace Passenger.Debugger.Relay;

/// <summary>The host's copies of what an app on a device loads: the build's folder of the assemblies it deployed for
/// the device's processor, each with its PDB. A module the app names by a device path is read here by its file name,
/// its metadata opened by the host's own runtime (MetaDataGetDispenser), so no metadata call crosses to the app.</summary>
internal sealed unsafe class DeviceModules
{
    private static readonly Guid s_dispenserClass = new("E5CB7A31-7512-11D2-89CE-0080C792E5D8");
    private static readonly Guid s_dispenserInterface = new("809C652E-7396-11D2-9771-00A0C9B4D50C");
    private readonly Dictionary<string, string> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<(string File, Guid Iid), nint> _scopes = [];
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

    /// <summary>A module's metadata interface, one scope per file kept open for the session: pointers into its storage
    /// (the evaluator's metadata blocks) stay good as long as the module is loaded, as the app's own runtime's would.</summary>
    public int OpenMetadata(string file, Guid* iid, nint* result)
    {
        lock (_scopes)
        {
            if (!_scopes.TryGetValue((file, *iid), out nint scope))
            {
                int opened = OpenScope(file, iid, &scope);
                if (opened < 0)
                {
                    *result = 0;
                    return opened;
                }
                _scopes[(file, *iid)] = scope;
            }
            ((delegate* unmanaged[Cdecl]<nint, uint>)(*(nint**)scope)[1])(scope);
            *result = scope;
            return 0;
        }
    }

    /// <summary>A scope over metadata read out of the app's memory, kept for the session as a file's is (the evaluator's
    /// metadata blocks point into it).</summary>
    public int OpenMetadataInMemory(ulong module, byte[] metadata, Guid* iid, nint* result)
    {
        string key = $"memory:{module:x}";
        lock (_scopes)
        {
            if (!_scopes.TryGetValue((key, *iid), out nint scope))
            {
                int made = Dispenser();
                if (made < 0)
                {
                    *result = 0;
                    return made;
                }
                nint copy = (nint)NativeMemory.Alloc((nuint)metadata.Length);
                metadata.CopyTo(new Span<byte>((void*)copy, metadata.Length));
                nint* vtable = *(nint**)_dispenser;
                // IMetaDataDispenser::OpenScopeOnMemory(data, size, ofRead, riid, out scope)
                int opened = ((delegate* unmanaged[Cdecl]<nint, nint, uint, uint, Guid*, nint*, int>)vtable[5])(_dispenser, copy, (uint)metadata.Length, 0, iid, &scope);
                if (opened < 0)
                {
                    NativeMemory.Free((void*)copy);
                    *result = 0;
                    return opened;
                }
                _scopes[(key, *iid)] = scope;
            }
            ((delegate* unmanaged[Cdecl]<nint, uint>)(*(nint**)scope)[1])(scope);
            *result = scope;
            return 0;
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

    private int OpenScope(string file, Guid* iid, nint* result)
    {
        int made = Dispenser();
        if (made < 0)
        {
            return made;
        }
        nint* vtable = *(nint**)_dispenser;
        fixed (char* path = file)
        {
            // IMetaDataDispenser::OpenScope(name, ofRead, riid, out scope)
            return ((delegate* unmanaged[Cdecl]<nint, char*, uint, Guid*, nint*, int>)vtable[4])(_dispenser, path, 0, iid, result);
        }
    }
}
