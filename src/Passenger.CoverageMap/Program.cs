using System.Runtime.Loader;

namespace Passenger.CoverageMap;

/// <summary>Instruments a test run's own copy of the modules beside its test assembly: <c>Passenger.CoverageMap TEST-ASSEMBLY
/// MAP [MODULE...]</c>, those named alone where any are (<see cref="Instrumenting"/>). Exit 0 once MAP is written, 2 for
/// arguments it cannot take, 1 where a module could not be instrumented, said on standard error.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // Coverlet and what it uses are beside this helper, as Coverlet's console carries them; its .deps.json names none.
        AssemblyLoadContext.Default.Resolving += (context, wanted) =>
            Path.Combine(AppContext.BaseDirectory, $"{wanted.Name}.dll") is { } path && File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        return Instrumenting.Run(args);
    }
}
