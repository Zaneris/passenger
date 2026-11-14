using Passenger.App.Run;

namespace Passenger.App.Debugging;

/// <summary>What an Apply Changes came to.</summary>
public enum HotReloadResult
{
    /// <summary>The program runs the edits now.</summary>
    Applied,

    /// <summary>The edits change no code the program runs (comments, whitespace on a line).</summary>
    NothingToApply,

    /// <summary>An edit the runtime cannot take while the program runs (Roslyn's rude edits): restarting applies it.</summary>
    RestartNeeded,

    /// <summary>The code does not compile: nothing is applied until it does.</summary>
    Errors,

    /// <summary>Hot Reload could not run or the runtime refused the update; the reason says which.</summary>
    Failed,
}

/// <summary>What an Apply Changes came to, in words for the status bar and the Debug console, with the edits that could not
/// be applied (each at its line, as Problems' Hot Reload tab lists them), the files and modules the update changed, and
/// how long it took.</summary>
public sealed record HotReloadOutcome(HotReloadResult Result, string Said)
{
    public IReadOnlyList<Problem> Problems { get; init; } = [];

    /// <summary>The files whose edits the program now runs (or, with nothing to apply, whose edits needed nothing).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>The modules updated, by name.</summary>
    public IReadOnlyList<string> Modules { get; init; } = [];

    /// <summary>The changed type tokens, keyed by module identity, for the program's update handlers.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<int>> UpdatedTypes { get; init; } = new Dictionary<Guid, IReadOnlyList<int>>();

    /// <summary>How many methods the update replaced or added.</summary>
    public int Methods { get; init; }

    public TimeSpan Took { get; init; }

    /// <summary>Of several programs' apply (a container run's services), those whose edits need them restarted, by name.</summary>
    public IReadOnlyList<string> Restart { get; init; } = [];

    /// <summary>What a program's C# came to, its XAML said with it or not: what the debugger's lines, breakpoints and update
    /// handlers follow, as XAML that failed after it takes nothing back from the program.</summary>
    public HotReloadOutcome Code
    {
        get => code ?? this;
        private init => code = value;
    }

    private readonly HotReloadOutcome? code;

    /// <summary>A program's C# apply and its XAML's said as one, C#'s first, the worse result standing; null when neither ran.</summary>
    public static HotReloadOutcome? Both(HotReloadOutcome? code, HotReloadOutcome? xaml)
    {
        if (code is null || xaml is null)
        {
            return code ?? xaml;
        }
        List<string> said = [.. new[] { code, xaml }.Where(o => o.Result != HotReloadResult.NothingToApply || o.Problems.Count > 0).Select(o => o.Said)];
        return new HotReloadOutcome(Worse(code.Result, xaml.Result), said.Count == 0 ? code.Said : string.Join("; ", said))
        {
            Problems = [.. code.Problems, .. xaml.Problems],
            Files = [.. code.Files, .. xaml.Files],
            Modules = code.Modules,
            UpdatedTypes = code.UpdatedTypes,
            Methods = code.Methods,
            Took = code.Took + xaml.Took,
            Code = code,
        };
    }

    /// <summary>Of two results, the one to act on: errors first, then a restart, a failure, an apply, and nothing applied.</summary>
    internal static HotReloadResult Worse(HotReloadResult one, HotReloadResult other) => Rank(one) <= Rank(other) ? one : other;

    private static int Rank(HotReloadResult result) => result switch
    {
        HotReloadResult.Errors => 0,
        HotReloadResult.RestartNeeded => 1,
        HotReloadResult.Failed => 2,
        HotReloadResult.Applied => 3,
        _ => 4,
    };
}
