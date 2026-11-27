namespace Passenger.App.Aspire;

/// <summary>The states an AppHost's resource service gives a resource (<see cref="AspireResource.State"/>), each named once,
/// and the one reading of them the Aspire window, its actions and an agent's tools share: ended, stopped (ended or never
/// started, so its Start starts it), between (on its way to running or to stopped) and failed (it could not start, or
/// cannot where it is).</summary>
[IdentifierHome]
public static class AspireStates
{
    public const string NotStarted = "NotStarted";
    public const string Waiting = "Waiting";
    public const string Building = "Building";
    public const string Starting = "Starting";
    public const string Activating = "Activating";
    public const string Running = "Running";
    public const string Stopping = "Stopping";
    public const string Exited = "Exited";
    public const string Finished = "Finished";
    public const string FailedToStart = "FailedToStart";

    /// <summary>The container engine its containers run on is not running or not well: none starts until it is.</summary>
    public const string RuntimeUnhealthy = "RuntimeUnhealthy";

    /// <summary>A parameter its AppHost needs a value of and has none, which what uses it waits for.</summary>
    public const string ValueMissing = "ValueMissing";

    /// <summary>Whether a resource runs no more: it exited, finished, or failed to start.</summary>
    public static bool HasEnded(string? state) => state is Exited or Finished or FailedToStart;

    /// <summary>Whether a resource is stopped where it is, its Start what runs it: ended, or never started.</summary>
    public static bool IsStopped(string? state) => HasEnded(state) || state is NotStarted;

    /// <summary>Whether a resource is on its way to running, or to stopped.</summary>
    public static bool IsBetween(string? state) => state is Waiting or Building or Starting or Activating or Stopping;

    /// <summary>Whether a resource failed: it could not start, or cannot where it is (its container engine unwell).</summary>
    public static bool IsFailed(string? state) => state is FailedToStart or RuntimeUnhealthy;
}
