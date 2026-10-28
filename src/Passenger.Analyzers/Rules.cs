using Microsoft.CodeAnalysis;

namespace Passenger.Analyzers;

/// <summary>Every rule's id and words, in one place: the rules of Passenger's code from PSG0001 on, each a warning the build
/// makes an error, and from PSG9001 on what a generator finds wrong in the files it reads. A rule is added here, in
/// AnalyzerReleases.Unshipped.md, and as an analyzer of its own beside the others.</summary>
internal static class Rules
{
    private const string Category = "Passenger";

    public static readonly DiagnosticDescriptor IdentifierLiteral = new(
        "PSG0001",
        "An identifier is written outside its home",
        "\"{0}\" is {1}: name it from there",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An identifier ([IdentifierHome]: an action's id, a tool window's, a theme key, an environment variable, "
            + "an MCP tool's name) is written once, in its home, so a misspelt one is a build error rather than something "
            + "that silently finds nothing.");

    public static readonly DiagnosticDescriptor AsyncVoidMethod = new(
        "PSG0002",
        "An async method returns void",
        "{0} is async void: return a Task and give it to whoever awaits or observes it",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An async void method's fault is thrown on whatever context it resumed on, where nothing can catch or "
            + "report it, and nothing can wait for it to end; an event handler starts a Task-returning method instead.");

    public static readonly DiagnosticDescriptor AsyncVoidLambda = new(
        "PSG0003",
        "An async lambda is given where a void delegate is taken",
        "This async lambda becomes async void as {0}: give it where a Task is taken, or start a Task-returning method from it",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An async lambda given to a delegate that returns void (an event's handler, an Action) is async void: "
            + "its fault reaches nothing that can report it, and nothing waits for it to end.");

    public static readonly DiagnosticDescriptor BlockingWait = new(
        "PSG0004",
        "A thread waits on a task",
        "{0} blocks until the task ends: await it, or read it only where it is known to have ended",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A thread held waiting on a task ties up a thread the task may need to end, the UI's or the pool's. A "
            + "read of a task known to have ended is no wait: inside an if, ?:, && or pattern on its IsCompleted, "
            + "IsCompletedSuccessfully, IsFaulted or IsCanceled, after an early exit on its not having ended, after an await "
            + "of it or of a WhenAll naming it, in its ContinueWith's continuation, or the task a WhenAny gave.");

    public static readonly DiagnosticDescriptor CatchAll = new(
        "PSG0005",
        "A catch takes every exception and passes none on",
        "This catch takes every exception: catch what can be thrown here, or pass what it caught on (report it, rethrow it, or throw it inside another)",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A catch of everything with no filter hides the IDE's own faults where nobody sees them. Catch what the "
            + "code can throw there, or a filter naming it; where everything must be caught (a handler kept apart from "
            + "others, a process's top level, a call into another's code, a loop that outlives one failure), the catch passes "
            + "the exception itself on before it can leave: rethrown, thrown inside another, or given whole to a call, a value "
            + "kept or returned, or a string made of it.");

    public static readonly DiagnosticDescriptor BareTaskRun = new(
        "PSG0006",
        "Work goes to the thread pool past BackgroundWork",
        "{0} starts work that no screenshot or test waits for: start it through BackgroundWork (Run, or Unheld saying why it must hold nothing)",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Work given to the thread pool by Task.Run or StartNew is waited for by nothing waiting for the IDE to be "
            + "quiet, so a screenshot or test can take what it changes half done. BackgroundWork.Run holds them until its answer "
            + "is back; BackgroundWork.Unheld and UnheldThread are for work that must hold nothing, as a loop as long-lived "
            + "as what it serves, each caller saying why.");

    public static readonly DiagnosticDescriptor KeysUnread = new(
        "PSG9001",
        "A resource keys file is not read",
        "{0}'s keys are not all in {1}: {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
