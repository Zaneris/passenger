using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>A function breakpoint naming its parameters as C# writes them, matched to the methods of this assembly as its
/// own metadata and symbols name them: aliases, arrays of any rank, jagged arrays and generic arguments alike.</summary>
public class FunctionBreakpointResolverTests
{
    [Theory]
    [InlineData("Overloads.M(int[])", typeof(int[]))]
    [InlineData("Overloads.M(int[,])", typeof(int[,]))]
    [InlineData("Overloads.M(string[][])", typeof(string[][]))]
    [InlineData("Overloads.M(int[,][])", typeof(int[,][]))]
    [InlineData("Overloads.M(int?[])", typeof(int?[]))]
    [InlineData("Overloads.M(string[]?)", typeof(string[]))]
    [InlineData("Overloads.M(List<int[]>)", typeof(List<int[]>))]
    [InlineData("Overloads.M(Dictionary<int[,], string>)", typeof(Dictionary<int[,], string>))]
    [InlineData("Overloads.M(int)", typeof(int))]
    public void A_function_breakpoint_naming_one_parameter_binds_in_the_overload_taking_it(string name, Type parameter)
    {
        Assert.Equal([parameter.ToString()], Bound(name));
    }

    [Fact]
    public void A_comma_inside_an_arrays_brackets_is_its_rank_never_a_second_parameter()
    {
        Assert.Equal([$"{typeof(int[])}, {typeof(int[,])}"], Bound("Overloads.M(int[], int[,])"));
        Assert.Equal([typeof(int[,]).ToString()], Bound("Overloads.M(int [ , ])"));
    }

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
