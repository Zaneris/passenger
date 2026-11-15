using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Dap;
using Passenger.Debugger.ExpressionEvaluator;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.ExpressionEvaluator.Compiler;
using Passenger.Debugger.Interop;
using Passenger.Languages.CSharp;

namespace Passenger.Debugger;

/// <summary>What every kind of breakpoint shares: one refusal of what the debugger cannot read, and its hits tried through
/// every debugger's one pipeline (<see cref="BreakpointHits"/>), the condition evaluated in the hit's top frame.</summary>
public partial class ManagedDebugger
{
    /// <summary>What's said of an expression the parser would recurse through past its stack (<see cref="CodeBrackets.Expression"/>),
    /// after what it is.</summary>
    internal const string NestedTooDeep = "nests deeper than the debugger reads";

    /// <summary>A data or exception breakpoint's message, by its id, written by a hit whose condition could not be told.</summary>
    public event Action<int, string>? OnBreakpointMessage;

    /// <summary>Why the debugger will not take a breakpoint's condition or hit count as it is, if it will not: such a
    /// breakpoint is refused when set, of every kind, rather than stopping at every hit saying it could not be told.</summary>
    internal static string? Unreadable(string? condition, string? hitCondition)
    {
        ExpressionSyntax? parsed = condition is null ? null : CodeBrackets.Expression(condition);
        if (condition is not null && parsed is null)
        {
            return $"The condition {NestedTooDeep}.";
        }
        if (parsed?.DescendantNodesAndSelf().Any(static node => node is AnonymousFunctionExpressionSyntax) == true)
        {
            return "Lambda expressions are not supported in breakpoint conditions.";
        }
        return BreakpointHits.Unreadable(hitCondition);
    }

    /// <summary>A hit of a breakpoint of any kind, tried as every debugger tries one (<see cref="BreakpointHits.StopsAsync"/>):
    /// its condition and message read in the thread's top frame, a condition that cannot be told written to the breakpoint as
    /// <paramref name="named"/> having stopped; <paramref name="reached"/> said where it neither logs nor stops. Whether the
    /// hit stops the program.</summary>
    private Task<bool> HitStopsAsync(ICorDebugThread thread, IHitBreakpoint breakpoint, string named, Func<string> reached) => BreakpointHits.StopsAsync(breakpoint, new HitReading
    {
        Named = named,
        Holds = condition => EvaluateBreakpointCondition(thread, condition),
        Logged = async message => (await RenderDebuggerDisplayAsync(message, null, new ThreadId(thread.Id), new FrameStackDepth(0), 0)).Text,
        Reached = reached,
        Say = line => OnConsole?.Invoke($"{line}\n"),
        Told = Told,
        Log = _logger,
    });

    /// <summary>A breakpoint's message changed at a hit, told as its kind tells one.</summary>
    private void Told(IHitBreakpoint breakpoint)
    {
        if (breakpoint is BreakpointManager.BreakpointInfo located)
        {
            OnBreakpointChanged?.Invoke(located);
        }
        else if (breakpoint.Message is { } message)
        {
            OnBreakpointMessage?.Invoke(breakpoint.Id, message);
        }
    }

    /// <summary>Whether a breakpoint's condition holds, or why it could not be told: a condition that does not evaluate
    /// (an unknown name, a throw) or is not a bool. Such a breakpoint stops, with the reason, rather than going missing
    /// unseen.</summary>
    private async Task<ConditionRead> EvaluateBreakpointCondition(ICorDebugThread corThread, string condition)
    {
        try
        {
            ThreadId threadId = new ThreadId(corThread.Id);
            FrameStackDepth frameStackDepth = new FrameStackDepth(0); // Top frame

            CompiledExpressionEvaluationContext evalContext = new CompiledExpressionEvaluationContext(corThread, threadId, frameStackDepth);
            using EvaluationResult result = await _expressionEvaluator.Evaluate(condition, evalContext);

            if (result.Error is not null)
            {
                _logger?.Invoke($"Condition evaluation error for '{condition}': {result.Error}");
                return new ConditionRead(true, result.Error);
            }
            if (result.Value?.UnwrapDebugValue() is not ICorDebugGenericValue { Type: CorElementType.BOOLEAN } holds)
            {
                return new ConditionRead(true, "it is not a bool");
            }
            return new ConditionRead(holds.GetValueAsBytes()[0] != 0);
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Exception evaluating condition '{condition}': {ex}");
            return new ConditionRead(true, ex.Message);
        }
    }
}
