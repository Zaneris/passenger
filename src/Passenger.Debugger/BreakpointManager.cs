using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>
/// Manages breakpoint tracking and mapping
/// </summary>
public class BreakpointManager
{
    private int _nextBreakpointId = 1;
    private readonly Dictionary<int, BreakpointInfo> _breakpoints = new();
    private readonly Dictionary<string, List<int>> _breakpointsByFile = new();
    private readonly Lock _lock = new();

    public class BreakpointInfo
    {
        public int Id { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public int Line { get; set; }
        public int? Column { get; set; }
        public int EndLine { get; set; }
        public int? EndColumn { get; set; }
        public bool Verified { get; set; }
        public ICorDebugFunctionBreakpoint? CorBreakpoint { get; set; }
        public List<FunctionBreakpointBinding> FunctionBindings { get; } = [];
        public string? FunctionName { get; set; }
        public bool IsFunctionBreakpoint => FunctionName is not null;
        public string? Message { get; set; }
        public ModuleMetadataReader.ResolvedBreakpoint? ResolvedBreakpointFromPdb { get; set; }

        /// <summary>The module a line breakpoint is bound in.</summary>
        public ModuleInfo? Module { get; set; }

        /// <summary>Conditional expression to evaluate when breakpoint is hit</summary>
        public string? Condition { get; set; }

        /// <summary>Hit count condition (e.g., ">=10", "==5", "%3")</summary>
        public string? HitCondition { get; set; }

        /// <summary>Current hit count for this breakpoint</summary>
        public int HitCount { get; set; }

        /// <summary>A tracepoint's message, its {expressions} evaluated where it is hit: written to the console in
        /// place of a stop.</summary>
        public string? LogMessage { get; set; }

        /// <summary>Whether a hit stops the program; a tracepoint's does not.</summary>
        public bool Suspend { get; set; } = true;

        /// <summary>A source breakpoint's request as the client made it: one made again just so keeps this breakpoint.</summary>
        public BreakpointRequest? Request { get; set; }

        /// <summary>A function breakpoint's request as the client made it: one made again just so keeps this breakpoint.</summary>
        public FunctionBreakpointRequest? FunctionRequest { get; set; }

        /// <summary>The debugger will not take it as it is (a condition or hit count it cannot read), as its message says.</summary>
        public bool Refused { get; set; }
    }

    /// <summary>A function breakpoint bound in one module. Its module is told apart from another of the same base address:
    /// one assembly loaded into two load contexts (the Avalonia inspector, a startup hook that loads itself again) is two
    /// modules over one image, and code runs in either.</summary>
    public record FunctionBreakpointBinding(
        ICorDebugFunctionBreakpoint CorBreakpoint,
        int MethodToken,
        ModuleMetadataReader.ResolvedBreakpoint Source,
        ModuleInfo Module);

    /// <summary>
    /// Create a new breakpoint
    /// </summary>
    public BreakpointInfo CreateBreakpoint(string filePath, int line, int? column = null, string? condition = null, string? hitCondition = null, string? logMessage = null, bool? suspend = null)
    {
        lock (_lock)
        {
            int id = _nextBreakpointId++;
            if (string.IsNullOrWhiteSpace(condition))
            {
                condition = null;
            }

            if (string.IsNullOrWhiteSpace(hitCondition))
            {
                hitCondition = null;
            }

            BreakpointInfo bp = new BreakpointInfo
            {
                Id = id,
                FilePath = filePath,
                Line = line,
                Column = column,
                Verified = false,
                Condition = condition,
                HitCondition = hitCondition,
                HitCount = 0,
                LogMessage = string.IsNullOrEmpty(logMessage) ? null : logMessage,
                Suspend = suspend ?? string.IsNullOrEmpty(logMessage),
            };

            _breakpoints[id] = bp;

            if (!_breakpointsByFile.ContainsKey(filePath))
            {
                _breakpointsByFile[filePath] = [];
            }
            _breakpointsByFile[filePath].Add(id);

            return bp;
        }
    }

    /// <summary>An id for a breakpoint kept elsewhere (an exception breakpoint), never one a line or function breakpoint has.</summary>
    public int NextId()
    {
        lock (_lock)
        {
            return _nextBreakpointId++;
        }
    }

    public BreakpointInfo CreateFunctionBreakpoint(string functionName, string? condition = null, string? hitCondition = null, string? logMessage = null, bool? suspend = null)
    {
        lock (_lock)
        {
            BreakpointInfo bp = new BreakpointInfo
            {
                Id = _nextBreakpointId++,
                FunctionName = functionName,
                Condition = string.IsNullOrWhiteSpace(condition) ? null : condition,
                HitCondition = string.IsNullOrWhiteSpace(hitCondition) ? null : hitCondition,
                LogMessage = string.IsNullOrEmpty(logMessage) ? null : logMessage,
                Suspend = suspend ?? string.IsNullOrEmpty(logMessage),
            };
            _breakpoints[bp.Id] = bp;
            return bp;
        }
    }

    public List<BreakpointInfo> GetFunctionBreakpoints()
    {
        lock (_lock)
        {
            return _breakpoints.Values.Where(bp => bp.IsFunctionBreakpoint).ToList();
        }
    }

    /// <summary>
    /// Get breakpoint by ID
    /// </summary>
    public BreakpointInfo? GetBreakpoint(int id)
    {
        lock (_lock)
        {
            return _breakpoints.TryGetValue(id, out BreakpointInfo? bp) ? bp : null;
        }
    }

    /// <summary>
    /// Get all breakpoints for a file
    /// </summary>
    public List<BreakpointInfo> GetBreakpointsForFile(string filePath)
    {
        lock (_lock)
        {
            if (_breakpointsByFile.TryGetValue(filePath, out List<int>? ids))
            {
                return ids.Select(id => _breakpoints[id]).ToList();
            }
            return [];
        }
    }

    /// <summary>
    /// Find breakpoint by ClrDebug breakpoint
    /// </summary>
    public BreakpointInfo? FindByCorBreakpoint(ICorDebugFunctionBreakpoint corBreakpoint)
    {
        lock (_lock)
        {
            return _breakpoints.Values.FirstOrDefault(bp =>
                bp.CorBreakpoint == corBreakpoint ||
                bp.FunctionBindings.Any(binding => binding.CorBreakpoint == corBreakpoint));
        }
    }

    /// <summary>The breakpoints not yet bound that may be once their code loads: not those refused.</summary>
    public List<BreakpointInfo> GetPendingBreakpoints()
    {
        lock (_lock)
        {
            return _breakpoints.Values.Where(bp => !bp.Verified && !bp.Refused).ToList();
        }
    }

    /// <summary>
    /// Get all breakpoints
    /// </summary>
    public List<BreakpointInfo> GetAllBreakpoints()
    {
        lock (_lock)
        {
            return _breakpoints.Values.ToList();
        }
    }

    /// <summary>
    /// Remove a breakpoint by id
    /// </summary>
    public bool RemoveBreakpoint(int id)
    {
        lock (_lock)
        {
            if (!_breakpoints.TryGetValue(id, out BreakpointInfo? bp))
            {
                return false;
            }

            _breakpoints.Remove(id);
            if (_breakpointsByFile.TryGetValue(bp.FilePath, out List<int>? ids))
            {
                ids.Remove(id);
                if (ids.Count == 0)
                {
                    _breakpointsByFile.Remove(bp.FilePath);
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Clear all breakpoints
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _breakpoints.Clear();
            _breakpointsByFile.Clear();
            _nextBreakpointId = 1;
        }
    }
}
