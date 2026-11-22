using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>A function breakpoint naming its parameters as C# writes them, matched to the methods of this assembly as its
/// own metadata and symbols name them: aliases, arrays of any rank, jagged arrays and generic arguments alike.</summary>
public class FunctionBreakpointResolverTests
{
    [Fact]
    public void A_function_breakpoint_binds_in_the_overload_taking_the_parameters_it_names() => Cases.All(
        ("a function breakpoint naming one parameter binds in the overload taking it", () => Cases.Each(
        [
            ("Overloads.M(int[,][])", typeof(int[,][])),
            ("Overloads.M(int?[])", typeof(int?[])),
            ("Overloads.M(string[]?)", typeof(string[])),
            ("Overloads.M(Dictionary<int[,], string>)", typeof(Dictionary<int[,], string>)),
        ], row => Assert.Equal([row.Item2.ToString()], Bound(row.Item1)))),
        ("a comma inside an array's brackets is its rank, never a second parameter", () =>
        {
            Assert.Equal([$"{typeof(int[])}, {typeof(int[,])}"], Bound("Overloads.M(int[], int[,])"));
            Assert.Equal([typeof(int[,]).ToString()], Bound("Overloads.M(int [ , ])"));
        }));

    /// <summary>The parameter types of each method a function breakpoint of this name binds in, joined by ", ".</summary>
    private static List<string> Bound(string name)
    {
        using ModuleMetadataReader reader = ModuleMetadataReader.TryLoad(typeof(Overloads).Assembly.Location)
            ?? throw new InvalidOperationException("The unit tests' own assembly could not be read");
        Assert.True(reader.HasSymbols, "The unit tests' own assembly was read without its symbols, which bind a function breakpoint");
        return [.. FunctionBreakpointMetadataResolver.Resolve(reader, FunctionBreakpointPattern.Parse(name))
            .Select(resolved => typeof(Overloads).Module.ResolveMethod(resolved.MethodToken) ?? throw new InvalidOperationException($"No method 0x{resolved.MethodToken:X8}"))
            .Select(method => string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.ToString())))];
    }

    private static class Overloads
    {
        public static int M(int value) => value;

        public static int M(int[] values) => values.Length;

        public static int M(int[,] grid) => grid.Length;

        public static int M(string[][] rows) => rows.Length;

        public static int M(int[,][] jagged) => jagged.Length;

        public static int M(int?[] maybe) => maybe.Length;

        public static int M(string[]? words) => words?.Length ?? 0;

        public static int M(List<int[]> lists) => lists.Count;

        public static int M(Dictionary<int[,], string> named) => named.Count;

        public static int M(int[] values, int[,] grid) => values.Length + grid.Length;
    }
}
