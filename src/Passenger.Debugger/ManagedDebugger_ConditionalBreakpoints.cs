using Passenger.Debugger.ExpressionEvaluator;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.ExpressionEvaluator.Compiler;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    /// <summary>Whether a breakpoint's condition holds, or why it could not be told: a condition that does not evaluate
    /// (an unknown name, a throw) or is not a bool. Such a breakpoint stops, with the reason, rather than going missing
    /// unseen.</summary>
    private async Task<(bool Holds, string? Error)> EvaluateBreakpointCondition(ICorDebugThread corThread, string condition)
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
                return (true, result.Error);
            }
            if (result.Value?.UnwrapDebugValue() is not ICorDebugGenericValue { Type: CorElementType.BOOLEAN } holds)
            {
                return (true, "it is not a bool");
            }
            return (holds.GetValueAsBytes()[0] != 0, null);
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Exception evaluating condition '{condition}': {ex}");
            return (true, ex.Message);
        }
    }

    /// <summary>Whether a breakpoint stops on this hit of it. One whose hit count cannot be read was refused when set, so
    /// it is never bound to be hit.</summary>
    private static bool EvaluateHitCondition(int hitCount, string hitCondition) =>
        Passenger.Dap.HitCondition.TryParse(hitCondition, out Passenger.Dap.HitCondition condition, out _) && condition.IsMet(hitCount);
}
