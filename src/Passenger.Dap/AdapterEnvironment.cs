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

    /// <summary>The variable that, set to 1, has each thread of a decompile wait after its first file until the session
    /// ends, so the decompile lasts as CoreLib's does on a slow machine, with every thread it runs on taken: a test's hold.</summary>
    [IdentifierHome]
    public const string HoldDecompileVariable = "PASSENGER_ENGINE_HOLD_DECOMPILE";

    /// <summary>How long an evaluation runs when nothing says otherwise.</summary>
    public static readonly TimeSpan DefaultEvaluationTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long code the debugger runs in the program may take: <see cref="DefaultEvaluationTimeout"/>, or
    /// what <see cref="EvaluationTimeoutVariable"/> gives; a value there that is no whole number of milliseconds above
    /// 0 is fatal, as a malformed flag is.</summary>
    public static TimeSpan EvaluationTimeout => EnvironmentValues.Milliseconds(EvaluationTimeoutVariable, DefaultEvaluationTimeout);

    /// <summary>Whether <see cref="LateYieldVariable"/> holds the engine so; a value other than 1 is fatal, as a malformed
    /// flag is.</summary>
    public static bool LateYield => Flag(LateYieldVariable);

    /// <summary>Whether <see cref="HoldDecompileVariable"/> holds decompiles so; a value other than 1 is fatal, as a
    /// malformed flag is.</summary>
    public static bool HoldDecompile => Flag(HoldDecompileVariable);

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
