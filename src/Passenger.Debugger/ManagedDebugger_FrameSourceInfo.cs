using System.Diagnostics;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public readonly record struct SourceInfo(string FilePath, int StartLine, int EndLine, int StartColumn, int EndColumn, DecompiledSourceInfo? DecompiledSourceInfo);
public class DecompiledSourceInfo
{
    public required string TypeFullName { get; init; }
    public required AssemblyPathAndMvid Assembly { get; init; }
    public required string CallingUserCodeAssemblyPath { get; init; }
}
public record struct AssemblyPathAndMvid(string AssemblyPath, Guid Mvid);

/// <summary>An assembly to decompile symbols for: read under the engine's lock, decompiled off it.</summary>
public sealed record SymbolsWanted(string AssemblyPath, string Name, Guid Mvid, IReadOnlyList<string> References);

public partial class ManagedDebugger
{
    /// <summary>A frame's source and place, as its PDB counts them, from 1. With <paramref name="decompiled"/>, a
    /// module with no symbols of its own takes the decompiled ones the cache has.</summary>
    private SourceInfo? GetSourceInfoAtFrame(ICorDebugFrame frame, bool decompiled)
    {
        if (frame is not ICorDebugILFrame ilFrame)
        {
            throw new InvalidOperationException("Active frame is not an IL frame");
        }

        ICorDebugFunction function = ilFrame.Function;
        if (FindModule(function.Module) is not { } module)
        {
            return null;
        }
        if (decompiled)
        {
            LoadDecompiledSymbols(module);
        }

        if (module.MetadataReader.HasSymbols && StoppedShortAt(frame) is { } placed)
        {
            return new SourceInfo(placed.DocumentPath, placed.StartLine, placed.EndLine, placed.StartColumn, placed.EndColumn, null);
        }
        if (module.MetadataReader.HasSymbols)
        {
            int ilOffset = WrittenAt(frame) ?? ilFrame.IP.pnOffset;
            mdMethodDef methodToken = function.Token;
            int? version = ModuleMetadataReader.VersionOf(ilFrame);
            (string sourceFilePath, int startLine, int endLine, int startColumn, int endColumn)? sourceInfo = module.MetadataReader.GetSourceLocationForOffset(methodToken, ilOffset, version);
            // A frame still in a method's old code, at a statement Hot Reload moved: shown where the statement now is.
            if (sourceInfo is { } old && version is int at && _statementUpdates.TryGetValue((module.MetadataReader.Mvid, methodToken, at, ilOffset), out StatementUpdate? moved))
            {
                sourceInfo = (old.sourceFilePath, moved.StartLine + 1, moved.EndLine + 1, moved.StartColumn + 1, moved.EndColumn + 1);
            }
            if (sourceInfo is not null)
            {
                DecompiledSourceInfo? decompiledSourceInfo = null;
                if (module.SymbolsFromDecompiled)
                {
                    string? callingUserCodeAssemblyPath = FindCallingUserCodeAssemblyPath(frame.Caller);
                    decompiledSourceInfo = CreateDecompiledSourceInfo(module, methodToken, callingUserCodeAssemblyPath);
                }

                return new SourceInfo(sourceInfo.Value.sourceFilePath, sourceInfo.Value.startLine, sourceInfo.Value.endLine, sourceInfo.Value.startColumn, sourceInfo.Value.endColumn, decompiledSourceInfo);
            }
        }

        return null;
    }

    /// Walks the physical caller chain looking for the closest frame from a user code assembly
    private string? FindCallingUserCodeAssemblyPath(ICorDebugFrame? startFrame)
    {
        for (ICorDebugFrame? frame = startFrame; frame is not null; frame = frame.Caller)
        {
            if (frame is not ICorDebugILFrame ilFrame)
            {
                continue;
            }

            if (FindModule(ilFrame.Function.Module) is { IsUserCode: true } callerModule)
            {
                return callerModule.ModulePath;
            }
        }
        return null;
    }

    private static DecompiledSourceInfo? CreateDecompiledSourceInfo(ModuleInfo module, int methodToken, string? callingUserCodeAssemblyPath)
    {
        if (callingUserCodeAssemblyPath is null)
        {
            return null;
        }

        IMetaDataImport metadataImport = module.Module.GetMetaDataInterface<IMetaDataImport>();
        Guid mvid = metadataImport.ScopeProps.pmvid;
        mdTypeDef containingTypeDef = metadataImport.GetMethodProps(methodToken).pClass;
        return new DecompiledSourceInfo
        {
            TypeFullName = GetFullMetadataTypeName(metadataImport, containingTypeDef),
            Assembly = new AssemblyPathAndMvid(module.ModulePath, mvid),
            CallingUserCodeAssemblyPath = callingUserCodeAssemblyPath
        };
    }

    private static string GetFullMetadataTypeName(IMetaDataImport metadataImport, mdTypeDef typeDef)
    {
        (string szTypeDef, CorTypeAttr pdwTypeDefFlags, mdToken ptkExtends) typeProps = metadataImport.GetTypeDefProps(typeDef);
        if (typeProps.pdwTypeDefFlags.IsTdNested() is false)
        {
            return typeProps.szTypeDef;
        }

        mdTypeDef declaringType = metadataImport.GetNestedClassProps(typeDef);
        return $"{GetFullMetadataTypeName(metadataImport, declaringType)}+{typeProps.szTypeDef}";
    }

    /// <summary>A module with no symbols of its own given its decompiled ones from the cache, read once; false while the cache
    /// has none, which only <see cref="DecompileAsync"/> makes, off the engine's lock. One that does not load is deleted, to be
    /// made again once.</summary>
    private bool LoadDecompiledSymbols(ModuleInfo module)
    {
        if (module.SymbolsFromDecompiled)
        {
            return true;
        }
        if (_symbols is null || module.IsDynamic || module.MetadataReader.HasSymbols)
        {
            return false;
        }
        string name = Path.GetFileNameWithoutExtension(module.ModuleName);
        if (_symbols.Find(name, module.MetadataReader.Mvid) is not { } pdb)
        {
            return false;
        }
        if (module.MetadataReader.TryLoadSymbols(pdb))
        {
            module.SymbolsFromDecompiled = true;
            return true;
        }
        _logger?.Invoke($"Decompiled symbols {pdb} did not load: deleted, to be made again");
        _symbols.Discard(pdb);
        return false;
    }

    /// <summary>What a module needs decompiled before its frames can show source: null when it has symbols (its own, or
    /// decompiled ones the cache has), when it cannot be decompiled, or when this session has decompiled it already (once
    /// is enough: a second that would not load either leaves it without source). Under the engine's lock.</summary>
    private SymbolsWanted? WantedFor(ModuleInfo module)
    {
        if (_symbols is null || module.IsDynamic || module.MetadataReader.HasSymbols || string.IsNullOrEmpty(module.ModulePath)
            || LoadDecompiledSymbols(module) || !File.Exists(module.ModulePath))
        {
            return null;
        }
        Guid mvid = module.MetadataReader.Mvid;
        lock (_decompiling)
        {
            if (_decompiling.TryGetValue(mvid, out Task<bool>? tried) && tried.IsCompleted)
            {
                return null;
            }
        }
        List<string> references = [.. _modules.Values.Select(m => m.ModulePath).Where(path => !string.IsNullOrEmpty(path))];
        return new SymbolsWanted(module.ModulePath, Path.GetFileNameWithoutExtension(module.ModuleName), mvid, references);
    }

    /// <summary>Decompiles an assembly's symbols into the cache, never under the engine's lock (CoreLib's takes many seconds,
    /// and a disconnect meanwhile must be answered): one decompile per assembly a session, whoever asks, given up as the
    /// session ends. True when the cache has them.</summary>
    public Task<bool> DecompileAsync(SymbolsWanted wanted)
    {
        lock (_decompiling)
        {
            if (!_decompiling.TryGetValue(wanted.Mvid, out Task<bool>? decompiling))
            {
                decompiling = DecompileOffTheLockAsync(wanted);
                _decompiling[wanted.Mvid] = decompiling;
            }
            return decompiling;
        }
    }

    private async Task<bool> DecompileOffTheLockAsync(SymbolsWanted wanted)
    {
        _logger?.Invoke($"Decompiling {wanted.Name} ({wanted.Mvid}) for its symbols, off the engine's lock");
        long began = Stopwatch.GetTimestamp();
        bool made = await _symbols!.WriteAsync(wanted.AssemblyPath, wanted.Name, wanted.Mvid, wanted.References, _ending.Token).ConfigureAwait(false);
        _logger?.Invoke($"Decompiling {wanted.Name} {(made ? "made its symbols" : "made none")} in {Stopwatch.GetElapsedTime(began).TotalSeconds:0.0} s");
        return made;
    }
}
