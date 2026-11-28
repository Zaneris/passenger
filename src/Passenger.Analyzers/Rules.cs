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
        "{0} blocks until the task ends: await it, read it only where it is known to have ended, or stay synchronous through Synchronously.Run (the App) or Blocking.Until (a library), saying why",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A thread held waiting on a task ties up a thread the task may need to end, the UI's or the pool's: by "
            + "its Result, Wait, Task.WaitAll or WaitAny, its awaiter's GetResult, or a wait on its AsyncWaitHandle. A read of "
            + "a task known to have ended is no wait: inside an if, ?:, && or pattern on its IsCompleted, "
            + "IsCompletedSuccessfully, IsFaulted or IsCanceled, after an early exit on its not having ended, after an await "
            + "of it or of a WhenAll naming it, in its ContinueWith's continuation, or the task a WhenAny gave. A caller that "
            + "must stay synchronous runs its work through the App's Synchronously.Run, or, in a library, waits for a task no "
            + "thread it holds ends through Core's Blocking.Until, the one wait this rule passes over, saying why.");

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

    public static readonly DiagnosticDescriptor ProcessEnd = new(
        "PSG0007",
        "Whether a process runs is read past DotnetProcesses",
        "Process.HasExited reads a zombie of another's as running and throws for a process Windows will not open: ask DotnetProcesses.Runs",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Whether a process runs is answered in one place, DotnetProcesses.Runs, for a process Passenger started (by .NET's "
            + "hold on it, which hears of its end however soon its id is taken again) and for one it found: a zombie, which waits "
            + "for another's parent and which HasExited finds running, counts as ended, and a process Windows will not open for "
            + "HasExited (another user's, an elevated one) is asked by its id.");

    public static readonly DiagnosticDescriptor BareJsonRead = new(
        "PSG0008",
        "A JSON member is read past JsonMembers",
        "JsonElement.{0} throws on a value of another kind: read the member through JsonMembers",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "What another program or a person wrote may hold anything anywhere: TryGetProperty throws on a value that is no "
            + "object and GetProperty on a member missing too, where JsonMembers takes an element of any kind, or none, and reads "
            + "a member as the kind it should be (Text, Number, Bool, Object, Items, Texts, Properties, or Value of any kind), "
            + "null for one of another kind. A member a message cannot go without is read so, then thrown on by name.");

    public static readonly DiagnosticDescriptor FileMoveOver = new(
        "PSG0009",
        "A file is moved over another past AtomicFile.Move",
        "{0} over a file is refused on Windows while any program has it open: put the file in place through AtomicFile.Move",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Windows refuses a plain move over a file another program has open, however that program shares it (a "
            + "scanner, an indexer, another Passenger reading it). AtomicFile.Move renames with POSIX semantics, which only a "
            + "program sharing no deletion refuses, and moves plainly where the file system has no such rename (FAT, a share).");

    public static readonly DiagnosticDescriptor BareParallelLoop = new(
        "PSG0010",
        "A Parallel loop runs on the thread pool",
        "{0} runs its loop on the thread pool, every thread of which it may take until the loop ends: run it through ApartScheduler.ForEach, on threads of its own",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A synchronous Parallel loop's workers take the pool's threads for as long as the loop lasts, and as each "
            + "finishes a turn and queues the next the pool sees work done and adds no thread: every other wait of the process "
            + "on the pool (a protocol's reads and answers, a request, an await) waits until the loop ends. ApartScheduler.ForEach "
            + "runs the loop on threads of its own, ended with it.");

    public static readonly DiagnosticDescriptor CultureNumber = new(
        "PSG0011",
        "A number is written in the machine's culture",
        "This {0} is written in the culture of the machine it runs on, so a German one reads 1,5 for 1.5 and 1.234 for 1,234: say it to a person through Numbers (Said, Fixed), or write it for a program with the invariant culture",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A number the IDE writes reads alike on every machine: its digits are the IDE's own, a comma between "
            + "thousands and a point before a fraction, as sizes and whiles are said, whatever culture the system runs in. "
            + "Numbers says a count or a measure to a person; a number written for a program (a command line, a file) is "
            + "formatted with the invariant culture. A whole number with no format sets nothing apart and passes, as does a "
            + "provider the code chose itself (content shown as its own program shows it, a program's values in its culture).");

    public static readonly DiagnosticDescriptor UnextractedWord = new(
        "PSG0012",
        "Words a person reads are written in the code",
        "{0}: say it through a resource's Words (a .resx beside its home class){1}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: false,
        description: "Every word a person reads comes from a resource, so it can be read in their language: a literal given "
            + "where Words go or to a control's words would stay English, and Words joined into a string are said in one "
            + "language and break the sentence a translation keeps whole, as Words said in one language and made Words again keep "
            + "that language for every reader. Turned on folder by folder in .editorconfig as "
            + "each area's words move into resources; a sweep's passenger_words_sweep there (messages, verbatim) asks for an "
            + "exception's message made Words and any other string made Words as it is.");

    public static readonly DiagnosticDescriptor WordsComparedWithString = new(
        "PSG0013",
        "Words are compared with a string",
        "{0} compares Words with a string: it holds only while the Words are verbatim, so compare their English (.English), or Words made as they are",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A string compared with Words (by a comparison's method, or == and !=) is made Words (verbatim) or compared as an object, so the check compiles "
            + "and holds only while the Words compared are verbatim too: once they are a resource's, it fails, and only when "
            + "run. A check of what Words say compares their English; one of which words they are compares Words made the "
            + "same way.");

    public static readonly DiagnosticDescriptor WordsForAgents = new(
        "PSG0014",
        "Words an agent reads are said in the person's language",
        "{0}: an agent reads English, so say their English (.English)",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: false,
        description: "An agent's answer is English whatever language the window is in, so tools and scripts read one language: "
            + "Words made a string (by an interpolation, a +, ToString, a join or as an object) or read as Shown say the person's "
            + "language, and an agent would read it. Turned on in .editorconfig for the code answering agents.");

    public static readonly DiagnosticDescriptor KeysUnread = new(
        "PSG9001",
        "A resource keys file is not read",
        "{0}'s keys are not all in {1}: {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ClassesUnread = new(
        "PSG9002",
        "A style classes file is not read",
        "{0}'s classes are not all in {1}: {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor WordsUnread = new(
        "PSG9003",
        "A words file is not read",
        "{0}'s words are not all read: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor WordsOutOfStep = new(
        "PSG9004",
        "A translation is out of step with its English",
        "{0}: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
