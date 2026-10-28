using System.Runtime.InteropServices;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>A module the program has loaded, as Hot Reload sees it: whether the runtime lets its code be edited.</summary>
public sealed record HotReloadModule(Guid Mvid, string Name, string? Path, bool Editable, string? WhyNot);

/// <summary>A statement a thread is in, as Roslyn's Edit and Continue asks of the debugger: its method's module, token and
/// version (of the code the frame runs), the IL offset, its sequence point's document and place (from zero), and Roslyn's
/// ActiveStatementFlags.</summary>
public sealed record ActiveStatementInfo(Guid Module, int Token, int Version, int ILOffset, string? Document,
    int StartLine, int StartColumn, int EndLine, int EndColumn, int Flags);

/// <summary>Where an active statement of a method's version went in the new version's source (lines and columns from zero).</summary>
public sealed record StatementUpdate(int Token, int Version, int ILOffset, int StartLine, int StartColumn, int EndLine, int EndColumn);

/// <summary>One module's update as Roslyn's Edit and Continue emitted it, with the first line (from zero) it changed in each
/// document it edits.</summary>
public sealed record ModuleUpdate(Guid Module, string Name, byte[] Metadata, byte[] IL, byte[] Pdb, IReadOnlyList<int> UpdatedMethods,
    IReadOnlyList<int> UpdatedTypes, IReadOnlyList<(string Document, IReadOnlyList<(int Old, int New)> Updates)> Lines,
    IReadOnlyList<StatementUpdate> ActiveStatements, IReadOnlyList<(string Document, int Line)> FirstChanges);

/// <summary>What became of one module's update: applied (or, its module not yet loaded, held for its load), or why not.</summary>
public sealed record ModuleApplied(Guid Module, string Name, bool Applied, string? Message);

/// <summary>Hot Reload under the debugger (Edit and Continue): the user's modules get Edit and Continue in their load
/// callbacks (SetJITCompilerFlags), an update is applied with ICorDebugModule2.ApplyChanges while the program is held (a
/// running one stopped for it), the module's debug information takes the update's (<see cref="ModuleMetadataReader.AddGeneration"/>),
/// and a frame in a method's old code moves to the new code at a statement's start (FunctionRemapOpportunity): one Roslyn
/// mapped, or one the update left as it was. The runtime offers a frame of old code the new at every statement it reaches
/// and has no way to stop offering, so a frame left in the old code runs it a stop at a time. The runtime's capabilities
/// are read from its CoreLib, never assumed.</summary>
public partial class ManagedDebugger
{
    /// <summary>Whether the launch asked for Hot Reload: the user's modules get Edit and Continue as they load.</summary>
    private bool _hotReload;

    /// <summary>Why Hot Reload stopped for this session: an update the runtime refused part way leaves the program holding
    /// part of it, which no later update can be built on.</summary>
    private string? _hotReloadStopped;

    /// <summary>Updates for modules not loaded yet, applied as each loads, in order.</summary>
    private readonly Dictionary<Guid, List<ModuleUpdate>> _heldUpdates = [];

    /// <summary>Where each active statement went in its method's new code, by the old code's place.</summary>
    private readonly Dictionary<(Guid Module, int Token, int Version, int ILOffset), StatementUpdate> _statementUpdates = [];

    /// <summary>The newest code of each method a thread was in when the statements were last read, by module, token and version.</summary>
    private readonly HashSet<(Guid Module, int Token, int Version)> _newestRead = [];

    /// <summary>The old code of each method the last update to its module changed in which Roslyn placed a frame (so the
    /// method's locals and closures carry over to the new code), when it was the method's newest.</summary>
    private readonly HashSet<(Guid Module, int Token, int Version)> _placedFrames = [];

    /// <summary>The first line (from zero) the last update to each module changed in each document it edits.</summary>
    private readonly Dictionary<(Guid Module, string Document), int> _firstChanges = [];

    /// <summary>Whether the program, running, is stopped for an update being made (<see cref="HoldForHotReload"/>).</summary>
    private bool _heldForHotReload;

    /// <summary>The edits the program's runtime can take, as its CoreLib names them (MetadataUpdater.GetCapabilities),
    /// or why they are unknown.</summary>
    public (IReadOnlyList<string>? Capabilities, string? Unknown) HotReloadCapabilities()
    {
        if (_modules.Values.FirstOrDefault(m => m.ModuleName == "System.Private.CoreLib.dll") is not { } coreLib)
        {
            return (null, "The program's runtime has not loaded its CoreLib yet");
        }
        string? said = coreLib.MetadataReader.ConstantString("System.Reflection.Metadata.MetadataUpdater", "GetCapabilities");
        return said is null
            ? (null, "The program's runtime does not say which edits it can take (its CoreLib's MetadataUpdater.GetCapabilities is not a constant)")
            : ([.. said.Split(' ', StringSplitOptions.RemoveEmptyEntries)], null);
    }

    /// <summary>The modules loaded with what Hot Reload may do with each.</summary>
    public IReadOnlyList<HotReloadModule> HotReloadModules() =>
        [.. _modules.Values.Where(m => !m.IsDynamic).Select(m => new HotReloadModule(m.MetadataReader.Mvid, m.ModuleName, m.ModulePath, m.Editable, m.Editable ? null : m.WhyNotEditable))];

    /// <summary>Turns Edit and Continue on for a module of the user's as it loads, when the launch asked for Hot Reload, and
    /// records whether the runtime has it on (DOTNET_MODIFIABLE_ASSEMBLIES=debug turns it on for unoptimized code itself).
    /// Called in the module's load callback, the only time the runtime lets its compiler flags change.</summary>
    private void EnableEditAndContinue(ICorDebugModule module, ModuleInfo info)
    {
        CorDebugJITCompilerFlags flags;
        try
        {
            if (_hotReload && info.IsUserCode && module.JITCompilerFlags != CorDebugJITCompilerFlags.CORDEBUG_JIT_ENABLE_ENC)
            {
                int set = module.Module2.TrySetJITCompilerFlags(CorDebugJITCompilerFlags.CORDEBUG_JIT_ENABLE_ENC);
                if (set != Cor.S_OK)
                {
                    _logger?.Invoke($"  Edit and Continue could not be turned on for {info.ModuleName}: 0x{set:X8}");
                }
            }
            flags = module.JITCompilerFlags;
        }
        catch (COMException error)
        {
            info.WhyNotEditable = $"The runtime would not say whether {info.ModuleName} can be edited: {error.Message}";
            return;
        }
        info.Editable = flags == CorDebugJITCompilerFlags.CORDEBUG_JIT_ENABLE_ENC;
        info.WhyNotEditable = info.Editable ? null
            : !info.IsUserCode ? $"{info.ModuleName} is optimized code, which the runtime does not let be edited"
            : $"{info.ModuleName} was loaded without Edit and Continue: debug it with Hot Reload on";
        if (info.Editable && _heldUpdates.Remove(info.MetadataReader.Mvid, out List<ModuleUpdate>? held))
        {
            foreach (ModuleUpdate update in held)
            {
                ModuleApplied applied = ApplyTo(info, update);
                _logger?.Invoke($"  The update held for {info.ModuleName} {(applied.Applied ? "is applied as it loads" : $"was refused: {applied.Message}")}");
            }
        }
    }

    /// <summary>Stops a running program for an update being made, until <see cref="ReleaseHotReloadHold"/>: its threads'
    /// statements are read while it is held, and the update applied before any of them moves on. False for a program
    /// paused, or held already, which is left as it is.</summary>
    public bool HoldForHotReload()
    {
        if (_process is null || _stopShown || _heldForHotReload)
        {
            return false;
        }
        // A stop counts with any event's: the program stays stopped until both are let go, whichever goes first.
        _process.Stop(0);
        _heldForHotReload = true;
        return true;
    }

    /// <summary>Lets a program held for an update go on, unless a pause asked for meanwhile stops it there.</summary>
    public void ReleaseHotReloadHold()
    {
        if (!_heldForHotReload || _process is null)
        {
            return;
        }
        _heldForHotReload = false;
        if (!TakePendingPause())
        {
            _process.Continue(false);
        }
    }

    /// <summary>The statements the program's threads are in, each once (its flags those of every thread in it), in the
    /// modules the user can edit; none while it runs, but held for an update. Each is told at its statement's start and
    /// as a leaf frame's: a frame moves to new code only where a statement starts, never part way through one, so a frame
    /// returning into it, or stopped part way, will be there as a leaf at that statement's start would.</summary>
    public IReadOnlyList<ActiveStatementInfo> ActiveStatements()
    {
        if (_process is null || !(_stopShown || _heldForHotReload))
        {
            return [];
        }
        _newestRead.Clear();
        Dictionary<(Guid, int, int, int), ActiveStatementInfo> found = [];
        foreach (ThreadInfo thread in _threads.Values)
        {
            int depth = 0;
            IEnumerable<ICorDebugFrame> frames;
            try
            {
                frames = [.. EnumerateFramesForThread(thread.Thread)];
            }
            catch (COMException)
            {
                continue;
            }
            foreach (ICorDebugFrame frame in frames)
            {
                if (frame is not ICorDebugILFrame il)
                {
                    continue;
                }
                bool leaf = depth++ == 0;
                try
                {
                    ICorDebugFunction function = il.Function;
                    if (FindModule(function.Module) is not { Editable: true, MetadataReader.HasSymbols: true } module)
                    {
                        continue;
                    }
                    int token = function.Token;
                    int version = ModuleMetadataReader.VersionOf(il) ?? 1;
                    (int offset, CorDebugMappingResult mapping) = il.IP;
                    int flags = 1;
                    if (leaf && !mapping.HasFlag(CorDebugMappingResult.MAPPING_EXACT) && !mapping.HasFlag(CorDebugMappingResult.MAPPING_APPROXIMATE))
                    {
                        flags |= 2;
                    }
                    if (function.TryGetCurrentVersionNumber(out uint current) is Cor.S_OK && current == version)
                    {
                        flags |= 8;
                        _newestRead.Add((module.MetadataReader.Mvid, token, version));
                    }
                    if (_justMyCode && module.MetadataReader.IsNonUserCode(token))
                    {
                        flags |= 4;
                    }
                    ModuleMetadataReader.Point? statement = module.MetadataReader.StatementAt(token, offset, version);
                    (string? document, int startLine, int startColumn, int endLine, int endColumn) = statement is { Document: { } path } at
                        ? (path, at.StartLine - 1, at.StartColumn - 1, at.EndLine - 1, at.EndColumn - 1)
                        : ((string?)null, 0, 0, 0, 0);
                    (Guid, int, int, int) key = (module.MetadataReader.Mvid, token, version, statement?.Offset ?? offset);
                    if ((flags & 8) == 0 && !_statementUpdates.ContainsKey(key))
                    {
                        // Old code the last update to its method did not place this statement of (one emitted for a running program): stale.
                        flags |= 32;
                    }
                    found[key] = found.TryGetValue(key, out ActiveStatementInfo? same)
                        ? same with { Flags = same.Flags | flags }
                        : new ActiveStatementInfo(key.Item1, token, version, key.Item4, document, startLine, startColumn, endLine, endColumn, flags);
                }
                catch (COMException)
                {
                    // A frame the runtime will not describe (being unwound) has no statement to tell of.
                }
            }
        }
        return [.. found.Values];
    }

    /// <summary>Applies updates, in order, the program held for it: paused, it stays paused; running, it is stopped for the
    /// updates and goes on. The first the runtime refuses stops Hot Reload for the session, as the program may hold part of
    /// it, and the rest are not tried. A module not loaded yet takes its update as it loads.</summary>
    public IReadOnlyList<ModuleApplied> ApplyChanges(IReadOnlyList<ModuleUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(_process);
        if (_hotReloadStopped is { } stopped)
        {
            return [.. updates.Select(u => new ModuleApplied(u.Module, u.Name, false, stopped))];
        }
        bool running = _process.TryIsRunning(out bool isRunning) is Cor.S_OK && isRunning;
        if (running)
        {
            _process.Stop(0);
        }
        List<ModuleApplied> results = [];
        try
        {
            foreach (ModuleUpdate update in updates)
            {
                if (_hotReloadStopped is { } refused)
                {
                    results.Add(new ModuleApplied(update.Module, update.Name, false, refused));
                    continue;
                }
                if (_modules.Values.FirstOrDefault(m => !m.IsDynamic && m.MetadataReader.Mvid == update.Module) is not { } module)
                {
                    if (!_heldUpdates.TryGetValue(update.Module, out List<ModuleUpdate>? held))
                    {
                        _heldUpdates[update.Module] = held = [];
                    }
                    held.Add(update);
                    results.Add(new ModuleApplied(update.Module, update.Name, true, $"{update.Name} is not loaded yet: the change is applied as it loads"));
                    continue;
                }
                results.Add(ApplyTo(module, update));
            }
        }
        finally
        {
            if (running)
            {
                _process.Continue(false);
            }
        }
        return results;
    }

    /// <summary>Applies one update to a loaded module, under the program's hold.</summary>
    private ModuleApplied ApplyTo(ModuleInfo module, ModuleUpdate update)
    {
        if (!module.Editable)
        {
            return new ModuleApplied(update.Module, update.Name, false, module.WhyNotEditable ?? $"{module.ModuleName} cannot be edited");
        }
        // mscordbi updates its own copy of the metadata, made when first asked for, which CordbModule::ApplyChanges assumes.
        if (module.Module.TryGetMetaDataInterface(out IMetaDataImport? _) is not Cor.S_OK)
        {
            return new ModuleApplied(update.Module, update.Name, false, $"The runtime would not give the debugger {module.ModuleName}'s metadata, which an update changes");
        }
        int result = module.Module.Module2.TryApplyChanges((uint)update.Metadata.Length, update.Metadata, (uint)update.IL.Length, update.IL);
        if (result != Cor.S_OK)
        {
            string why = Marshal.GetExceptionForHR(result)?.Message ?? $"0x{result:X8}";
            _hotReloadStopped = $"The runtime refused the update to {module.ModuleName} ({why}), and may hold part of it: restart to apply changes";
            _logger?.Invoke(_hotReloadStopped);
            return new ModuleApplied(update.Module, update.Name, false, _hotReloadStopped);
        }
        try
        {
            module.MetadataReader.AddGeneration(update.Metadata, update.IL, update.Pdb, update.UpdatedMethods, update.Lines);
        }
        catch (Exception error) when (error is BadImageFormatException or InvalidOperationException)
        {
            // The program runs the new code; only its lines are unknown to the debugger, which says so.
            _logger?.Invoke($"The debug information of the update to {module.ModuleName} could not be read: {error.Message}");
        }
        module.CurrentMetadata = SavedMetadata(module);
        if (module.CurrentMetadata is { } saved)
        {
            module.MetadataReader.TakeMetadata(saved);
        }
        // Where frames go is told of this update's code: what an earlier one said of the methods it changes goes with it.
        HashSet<int> changed = [.. update.UpdatedMethods];
        foreach ((Guid, int Token, int, int) stale in _statementUpdates.Keys.Where(k => k.Module == update.Module && changed.Contains(k.Token)).ToList())
        {
            _statementUpdates.Remove(stale);
        }
        _placedFrames.RemoveWhere(placed => placed.Module == update.Module);
        foreach ((Guid, string) stale in _firstChanges.Keys.Where(k => k.Module == update.Module).ToList())
        {
            _firstChanges.Remove(stale);
        }
        foreach (StatementUpdate moved in update.ActiveStatements)
        {
            _statementUpdates[(update.Module, moved.Token, moved.Version, moved.ILOffset)] = moved;
            if (_newestRead.Contains((update.Module, moved.Token, moved.Version)))
            {
                _placedFrames.Add((update.Module, moved.Token, moved.Version));
            }
        }
        foreach ((string document, int line) in update.FirstChanges)
        {
            _firstChanges[(update.Module, document)] = line;
        }
        ModuleSet_Version++;
        foreach (int method in update.UpdatedMethods)
        {
            _logger?.Invoke($"  0x{method:X8}: {string.Join(", ", module.MetadataReader.Points(method).Select(p => $"IL_{p.Offset:X4} {p.StartLine}:{p.StartColumn}"))}");
        }
        Rebind(module, [.. update.UpdatedMethods]);
        _logger?.Invoke($"Applied an update to {module.ModuleName}: {update.UpdatedMethods.Count} methods, {update.UpdatedTypes.Count} types, generation {module.MetadataReader.Generations}");
        return new ModuleApplied(update.Module, update.Name, true, null);
    }

    /// <summary>A module's metadata with every update applied, as the debugger's own copy of it holds it, saved whole into
    /// memory that does not move (the expression compiler reads it by its address); null when it will not save, after which
    /// expressions compile against the image's metadata.</summary>
    private byte[]? SavedMetadata(ModuleInfo module)
    {
        try
        {
            if (module.Module.TryGetMetaDataInterface(out IMetaDataEmit? emit) is not Cor.S_OK || emit is null
                || emit.TryGetSaveSize(0, out uint size) is not Cor.S_OK || size == 0)
            {
                _logger?.Invoke($"  {module.ModuleName}'s updated metadata could not be sized");
                return null;
            }
            byte[] saved = GC.AllocateArray<byte>(checked((int)size), pinned: true);
            int result = emit.TrySaveToMemory(saved, size);
            if (result != Cor.S_OK)
            {
                _logger?.Invoke($"  {module.ModuleName}'s updated metadata could not be saved: 0x{result:X8}");
                return null;
            }
            _logger?.Invoke($"  {module.ModuleName}'s updated metadata saved: {size} bytes");
            return saved;
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            _logger?.Invoke($"  {module.ModuleName}'s updated metadata could not be read: {error.Message}");
            return null;
        }
    }

    /// <summary>After an update, a module's breakpoints follow its new code: a function breakpoint in a method it changed is
    /// bound again in the method's new version, and each line breakpoint is bound again when next asked for (its client
    /// sends its lines again, as they now are), however it was asked before; until then it stops where it stopped.</summary>
    private void Rebind(ModuleInfo module, HashSet<int> methods)
    {
        foreach (BreakpointManager.BreakpointInfo breakpoint in _breakpointManager.GetAllBreakpoints())
        {
            if (!breakpoint.IsFunctionBreakpoint)
            {
                if (breakpoint.ModuleBaseAddress == module.BaseAddress)
                {
                    breakpoint.Request = null;
                }
                continue;
            }
            List<BreakpointManager.FunctionBreakpointBinding> stale = [.. breakpoint.FunctionBindings.Where(b => ReferenceEquals(b.Module, module) && methods.Contains(b.MethodToken))];
            foreach (BreakpointManager.FunctionBreakpointBinding binding in stale)
            {
                binding.CorBreakpoint.TryActivate(false);
                breakpoint.FunctionBindings.Remove(binding);
            }
            if (stale.Count > 0)
            {
                TryBindFunctionBreakpoint(breakpoint, module);
            }
        }
    }

    /// <summary>A frame running a method's old code reaches a point after an update: at a statement's start that Roslyn
    /// mapped (an active statement's), or, the frame not stepping, one the update left as it was, it goes on there in the
    /// new code; otherwise it runs on in the old code, which the runtime offers again at its next statement.</summary>
    private void HandleFunctionRemapOpportunity(FunctionRemapOpportunityCorDebugManagedCallbackEventArgs remap)
    {
        try
        {
            ICorDebugFunction old = remap.OldFunction;
            int token = old.Token;
            if (FindModule(old.Module) is { } module && old is ICorDebugFunction2 oldVersion && oldVersion.TryGetVersionNumber(out uint from) is Cor.S_OK
                && remap.NewFunction is ICorDebugFunction2 newVersion && newVersion.TryGetVersionNumber(out uint to) is Cor.S_OK
                && RemapTarget(module, token, (int)from, (int)to, (int)remap.OldILOffset, stepping: _stepping is (int steppingThread, _) && steppingThread == remap.Thread.Id) is { Document: not null } there
                && remap.Thread.ActiveFrame is ICorDebugILFrame2 frame)
            {
                // A step's stepper is the old frame's: it goes first, and is made again at FunctionRemapComplete.
                if (_stepper is not null && _stepping is (int stepping, _) && stepping == remap.Thread.Id)
                {
                    _stepper.TryDeactivate();
                    _stepper = null;
                    _restepAfterRemap = remap.Thread.Id;
                }
                int remapped = frame.TryRemapFunction((uint)there.Offset);
                _logger?.Invoke($"Remap of 0x{token:X8} v{from} IL_{remap.OldILOffset:X4} to v{to} IL_{there.Offset:X4}: 0x{remapped:X8}");
                if (remapped != Cor.S_OK && _restepAfterRemap == remap.Thread.Id)
                {
                    // Refused, no FunctionRemapComplete comes: the frame runs on in the old code, where its step goes on.
                    _restepAfterRemap = null;
                    SetupStepper(remap.Thread, _lastStep);
                }
            }
            else
            {
                _logger?.Invoke($"  The old code runs on from IL_{remap.OldILOffset:X4}: no statement of the new code is known to start there");
            }
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            _logger?.Invoke($"A remap opportunity could not be taken: {error.Message}");
        }
        Continue(clearVariables: false);
    }

    /// <summary>Where in a method's new code a frame of its old code at an IL offset goes on: the statement Roslyn said its
    /// active statement there became, or, not mid-step, the same statement where the update left the method's text as it
    /// was up to that statement's end (the first line the update changed in its document after it), in old code whose
    /// frames Roslyn placed (so its locals and closures carry over); else none.</summary>
    private ModuleMetadataReader.Point? RemapTarget(ModuleInfo module, int token, int from, int to, int ilOffset, bool stepping)
    {
        Guid mvid = module.MetadataReader.Mvid;
        if (_statementUpdates.TryGetValue((mvid, token, from, ilOffset), out StatementUpdate? went))
        {
            return module.MetadataReader.Points(token, to).FirstOrDefault(p => !p.IsHidden && p.StartLine == went.StartLine + 1 && p.StartColumn == went.StartColumn + 1);
        }
        if (stepping || !_placedFrames.Contains((mvid, token, from))
            || module.MetadataReader.StatementAt(token, ilOffset, from) is not { Document: { } document } statement || statement.Offset != ilOffset
            || !_firstChanges.TryGetValue((mvid, document), out int firstChanged) || statement.EndLine - 1 >= firstChanged)
        {
            return null;
        }
        return module.MetadataReader.Points(token, to).FirstOrDefault(p => !p.IsHidden && p.Document == document
            && p.StartLine == statement.StartLine && p.StartColumn == statement.StartColumn && p.EndLine == statement.EndLine && p.EndColumn == statement.EndColumn);
    }

    /// <summary>The thread whose step a remap ended, stepped again from its statement in the new code once it is there.</summary>
    private int? _restepAfterRemap;

    /// <summary>The step asked for last, which a remap mid-step makes again.</summary>
    private StepType _lastStep;

    /// <summary>A frame runs the new code of its method: a step under way on its thread goes on from its statement there, as
    /// the step asked, through the new code.</summary>
    private void HandleFunctionRemapComplete(FunctionRemapCompleteCorDebugManagedCallbackEventArgs complete)
    {
        if (_restepAfterRemap is int thread && thread == complete.Thread.Id)
        {
            _restepAfterRemap = null;
            try
            {
                _stepper?.TryDeactivate();
                _stepper = null;
                SetupStepper(complete.Thread, _lastStep);
                ICorDebugILFrame? now = complete.Thread.ActiveFrame as ICorDebugILFrame;
                _logger?.Invoke($"The {_lastStep} under way on thread {thread} goes on in the new code, v{(now is null ? null : ModuleMetadataReader.VersionOf(now))} IL_{now?.IP.pnOffset:X4}");
            }
            catch (Exception error) when (error is COMException or InvalidOperationException)
            {
                _logger?.Invoke($"The step could not go on in the new code: {error.Message}");
            }
        }
        Continue(clearVariables: false);
    }
}
