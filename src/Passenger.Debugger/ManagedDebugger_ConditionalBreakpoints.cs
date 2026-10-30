using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Debugger.ExpressionEvaluator;
using Passenger.Debugger.ExpressionEvaluator.Cil;
using Passenger.Debugger.ExpressionEvaluator.Compiler;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>A breakpoint of any kind (line, function, data, exception) as its hits are tried: what it asks of a hit, its
/// hits so far, and the debugger's word on it, which a condition a hit could not tell is written to.</summary>
internal interface IHitBreakpoint
{
    int Id { get; }
    string? Condition { get; }
    string? HitCondition { get; }
    string? LogMessage { get; }
    bool Suspend { get; }
    int HitCount { get; set; }
    string? Message { get; set; }
}

/// <summary>What every kind of breakpoint shares: one refusal of what the debugger cannot read, one reconciling of those
/// asked for with those set, and one pipeline a hit goes through (hit count, condition, log message, suspend).</summary>
public partial class ManagedDebugger
{
    /// <summary>A data or exception breakpoint's message, by its id, written by a hit whose condition could not be told.</summary>
    public event Action<int, string>? OnBreakpointMessage;

    /// <summary>Why the debugger will not take a breakpoint's condition or hit count as it is, if it will not: such a
    /// breakpoint is refused when set, of every kind, rather than stopping at every hit saying it could not be told.</summary>
    internal static string? Unreadable(string? condition, string? hitCondition)
    {
        if (condition is not null && SyntaxFactory.ParseExpression(condition).DescendantNodesAndSelf().Any(static node => node is AnonymousFunctionExpressionSyntax))
        {
            return "Lambda expressions are not supported in breakpoint conditions.";
        }
        return hitCondition is { } hits && !Passenger.Dap.HitCondition.TryParse(hits, out _, out string? problem) ? problem : null;
    }

    /// <summary>Matches the breakpoints asked for with those set: one asked for again just as it was keeps the one set (each
    /// kept once), else counts on from the hits of one it replaces at its place (<paramref name="samePlace"/>). Those set
    /// that no request keeps are <paramref name="gone"/>.</summary>
    internal static (T? Kept, int Hits)[] Reconcile<T, TRequest>(IReadOnlyList<TRequest> requests, IReadOnlyList<T> set, Func<T, TRequest?> requestOf,
        Func<TRequest, TRequest, bool> samePlace, out List<T> gone) where T : class, IHitBreakpoint where TRequest : class
    {
        T?[] kept = new T?[requests.Count];
        for (int i = 0; i < requests.Count; i++)
        {
            kept[i] = set.FirstOrDefault(b => EqualityComparer<TRequest>.Default.Equals(requestOf(b), requests[i]) && !kept.Contains(b));
        }
        gone = [.. set.Where(b => !kept.Contains(b))];
        (T? Kept, int Hits)[] matched = new (T?, int)[requests.Count];
        for (int i = 0; i < requests.Count; i++)
        {
            TRequest request = requests[i];
            matched[i] = (kept[i], kept[i] is null && gone.FirstOrDefault(b => requestOf(b) is { } was && samePlace(was, request)) is { } replaced ? replaced.HitCount : 0);
        }
        return matched;
    }

    /// <summary>A hit of a breakpoint of any kind, counted, then tried by its hit count and condition (one that cannot be
    /// told goes on, written to the breakpoint as <paramref name="named"/> having stopped); its message is logged, or
    /// <paramref name="reached"/> where it neither logs nor stops. Whether the hit stops the program.</summary>
    private async Task<bool> HitStopsAsync(ICorDebugThread thread, IHitBreakpoint breakpoint, string named, Func<string> reached)
    {
        breakpoint.HitCount++;
        if (breakpoint.HitCondition is { } hits && !EvaluateHitCondition(breakpoint.HitCount, hits))
        {
            _logger?.Invoke($"Breakpoint {breakpoint.Id}: hit count not met: count={breakpoint.HitCount}, condition={hits}");
            return false;
        }
        if (breakpoint.Condition is { } condition)
        {
            (bool holds, string? error) = await EvaluateBreakpointCondition(thread, condition);
            if (error is not null)
            {
                string message = $"{Passenger.Dap.BreakpointConditions.NotEvaluated(condition)}, so {named} stopped: {error}";
                breakpoint.Message = message;
                if (breakpoint is BreakpointManager.BreakpointInfo located)
                {
                    OnBreakpointChanged?.Invoke(located);
                }
                else
                {
                    OnBreakpointMessage?.Invoke(breakpoint.Id, message);
                }
                OnConsole?.Invoke($"{message}\n");
            }
            else if (!holds)
            {
                _logger?.Invoke($"Breakpoint {breakpoint.Id}: condition not met: {condition}");
                return false;
            }
        }
        if (breakpoint.LogMessage is { } logMessage)
        {
            (string text, _) = await RenderDebuggerDisplayAsync(logMessage, null, new ThreadId(thread.Id), new FrameStackDepth(0), 0);
            OnConsole?.Invoke($"{text}\n");
        }
        else if (!breakpoint.Suspend)
        {
            OnConsole?.Invoke($"{reached()}\n");
        }
        return breakpoint.Suspend;
    }

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
