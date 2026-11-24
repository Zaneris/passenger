using System.Globalization;
using Passenger.Dap;

namespace Passenger.Debugger;

/// <summary>The modules the program loaded as the Modules window lists them, each read from what the debugger kept of it as
/// it loaded (its metadata, symbols and the runtime's flags), never by asking the program again.</summary>
public partial class ManagedDebugger
{
    /// <summary>A module as the protocol tells of it: this load of it by its id, its assembly's version, Just My Code's
    /// verdict, whether the runtime compiles it optimized, where its symbols came from, and where its image lies.</summary>
    internal static DapModule Described(ModuleInfo module) =>
        new(module.Id.ToString(CultureInfo.InvariantCulture), module.ModuleName, module.IsDynamic ? null : module.ModulePath)
        {
            Version = VersionOf(module.MetadataReader),
            IsUserCode = module.IsUserCode,
            IsOptimized = module.CompiledOptimized,
            SymbolStatus = !module.MetadataReader.HasSymbols ? DapSymbolStatus.None
                : module.SymbolsFromDecompiled ? DapSymbolStatus.Decompiled
                : DapSymbolStatus.Loaded,
            SymbolFilePath = module.MetadataReader.SymbolsFrom,
            AddressRange = module.IsDynamic ? null : $"0x{module.BaseAddress.Value:X}",
            IsDynamic = module.IsDynamic ? true : null,
        };

    /// <summary>The assembly's version, which a module holding no assembly (a netmodule) has none of.</summary>
    private static string? VersionOf(ModuleMetadataReader reader) =>
        reader.PeMetadataReader.IsAssembly ? reader.PeMetadataReader.GetAssemblyDefinition().Version.ToString() : null;
}
