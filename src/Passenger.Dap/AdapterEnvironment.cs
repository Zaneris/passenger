using Passenger.Core;

namespace Passenger.Dap;

/// <summary>What the IDE and the debug adapter read from the environment, named once for both ends: how long the
/// adapter lets an evaluation run, and the file the engine's log is kept in.</summary>
public static class AdapterEnvironment
{
    /// <summary>The variable that gives <see cref="EvaluationTimeout"/> in milliseconds.</summary>
    [IdentifierHome]
    public const string EvaluationTimeoutVariable = "PASSENGER_EVAL_TIMEOUT_MS";

    /// <summary>The variable naming a file the engine's own log of every session is kept in, for a stop or an end that
    /// goes wrong.</summary>
    [IdentifierHome]
    public const string EngineLogVariable = "PASSENGER_ENGINE_LOG";

    /// <summary>The variable that, set to 1, has the engine hand over a stepping thread's leaving at an await only with its
    /// step's end, once the thread has run on, as the runtime once handed both under load: a test's hold.</summary>
    [IdentifierHome]
    public const string LateYieldVariable = "PASSENGER_ENGINE_LATE_YIELD";

    /// <summary>The variable naming a module, by its file's name, whose load the engine takes as the runtime's word that
    /// its debugging of the program failed for good, as the runtime once said under load: a test's hold.</summary>
    [IdentifierHome]
    public const string FailAtModuleVariable = "PASSENGER_ENGINE_FAIL_AT_MODULE";

    /// <summary>The variable that, set to 1, has the engine's evaluations hold no other thread, as the runtime's hold once
    /// missed one: a test's hold.</summary>
    [IdentifierHome]
    public const string HoldNoneVariable = "PASSENGER_ENGINE_HOLD_NONE";

    /// <summary>The variable that, set to 1, has the engine's box of each value a call returned wait first for the program's
    /// input, so its evaluation is held unfinished as one that never ends is: a test's hold.</summary>
    [IdentifierHome]
    public const string HoldBoxVariable = "PASSENGER_ENGINE_HOLD_BOX";

    /// <summary>The variable that, set to 1, has the async stepper wait for the program's input on a thread before it asks
    /// there where a step goes (a step out's builder, to tell of its end; which run goes on after an await), so an
    /// evaluation is held unfinished there as one that never ends is: a test's hold.</summary>
    [IdentifierHome]
    public const string HoldAsyncStepVariable = "PASSENGER_ENGINE_HOLD_ASYNC_STEP";

    /// <summary>The variable naming a kind of the runtime's events, as its callback is named (Breakpoint,
    /// FunctionRemapComplete, LoadModule), whose handling the engine fails as it begins, as an error inside its handler
    /// would: a test's hold.</summary>
    [IdentifierHome]
    public const string FailHandlingVariable = "PASSENGER_ENGINE_FAIL_HANDLING";

    /// <summary>The variable naming a thread, by the name the program gives it, that the engine holds where the runtime
    /// announces it, at its first instruction, until an evaluation lets it past, as a stop once caught a thread on its way
    /// out of its announcement: a test's hold.</summary>
    [IdentifierHome]
    public const string HoldStartVariable = "PASSENGER_ENGINE_HOLD_START";

    /// <summary>The variable that, set to 1, has each thread of a decompile wait after its first file until the session
    /// ends, so the decompile lasts as CoreLib's does on a slow machine, with every thread it runs on taken: a test's hold.</summary>
    [IdentifierHome]
    public const string HoldDecompileVariable = "PASSENGER_ENGINE_HOLD_DECOMPILE";

    /// <summary>The variable that, set to 1, has the engine wait as long as an expression may take before it makes a session's
    /// first lambda in the program, as that making once took on a busy machine: a test's hold.</summary>
    [IdentifierHome]
    public const string HoldLambdaVariable = "PASSENGER_ENGINE_HOLD_LAMBDA";

    /// <summary>The variable that, set to 1, has a let-go wait, the program running, until each evaluation given up still to
    /// end has ended before it holds the program, so that end comes just before the let-go listens for it, as one once
    /// did: a test's hold.</summary>
    [IdentifierHome]
    public const string HoldLetGoVariable = "PASSENGER_ENGINE_HOLD_LET_GO";

    /// <summary>How long an evaluation runs when nothing says otherwise.</summary>
    public static readonly TimeSpan DefaultEvaluationTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long code the debugger runs in the program may take: <see cref="DefaultEvaluationTimeout"/>, or
    /// what <see cref="EvaluationTimeoutVariable"/> gives; a value there that is no whole number of milliseconds above
    /// 0 is fatal, as a malformed flag is.</summary>
    public static TimeSpan EvaluationTimeout => EnvironmentValues.Milliseconds(EvaluationTimeoutVariable, DefaultEvaluationTimeout);

    /// <summary>Whether <see cref="LateYieldVariable"/> holds the engine so; a value other than 1 is fatal, as a malformed
    /// flag is.</summary>
    public static bool LateYield => Flag(LateYieldVariable);

    /// <summary>Whether <see cref="HoldNoneVariable"/> holds the engine so; a value other than 1 is fatal, as a malformed
    /// flag is.</summary>
    public static bool HoldNone => Flag(HoldNoneVariable);

    /// <summary>Whether <see cref="HoldBoxVariable"/> holds the engine so; a value other than 1 is fatal, as a malformed
    /// flag is.</summary>
    public static bool HoldBox => Flag(HoldBoxVariable);

    /// <summary>Whether <see cref="HoldAsyncStepVariable"/> holds the engine so; a value other than 1 is fatal, as a
    /// malformed flag is.</summary>
    public static bool HoldAsyncStep => Flag(HoldAsyncStepVariable);

    /// <summary>Whether <see cref="HoldLambdaVariable"/> holds the engine so; a value other than 1 is fatal, as a malformed
    /// flag is.</summary>
    public static bool HoldLambda => Flag(HoldLambdaVariable);

    /// <summary>Whether <see cref="HoldLetGoVariable"/> holds the engine so; a value other than 1 is fatal, as a malformed
    /// flag is.</summary>
    public static bool HoldLetGo => Flag(HoldLetGoVariable);

    /// <summary>The kind of event <see cref="FailHandlingVariable"/> names, or null for none; the engine refuses, as fatal,
    /// a name that is no kind of its events.</summary>
    public static string? FailHandling => Environment.GetEnvironmentVariable(FailHandlingVariable) is { Length: > 0 } kind ? kind : null;

    /// <summary>Whether <see cref="HoldDecompileVariable"/> holds decompiles so; a value other than 1 is fatal, as a
    /// malformed flag is.</summary>
    public static bool HoldDecompile => Flag(HoldDecompileVariable);

    /// <summary>The thread's name <see cref="HoldStartVariable"/> gives, or null for none.</summary>
    public static string? HoldStart => Environment.GetEnvironmentVariable(HoldStartVariable) is { Length: > 0 } thread ? thread : null;

    /// <summary>The module's file name <see cref="FailAtModuleVariable"/> gives, or null for none.</summary>
    public static string? FailAtModule => Environment.GetEnvironmentVariable(FailAtModuleVariable) is { Length: > 0 } module ? module : null;

    /// <summary>The file <see cref="EngineLogVariable"/> names, or null when it names none.</summary>
    public static string? EngineLog => Environment.GetEnvironmentVariable(EngineLogVariable) is { Length: > 0 } file ? file : null;

    /// <summary>A variable that is 1 or unset; any other value is fatal.</summary>
    private static bool Flag(string variable) => Environment.GetEnvironmentVariable(variable) switch
    {
        null or "" => false,
        "1" => true,
        { } other => throw new FormatException($"{variable} is \"{other}\": it must be 1, or unset"),
    };
}
