using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.App.Debugging;
using Passenger.Debugger;
using Passenger.Languages.CSharp;
using Passenger.Text;
using Words = System.Collections.Generic.List<string>;

namespace Passenger.UnitTests.Debugger;

/// <summary>A function breakpoint naming its parameters as C# writes them, matched to the methods of this assembly as its
/// own metadata and symbols name them: aliases, arrays of any rank, jagged arrays and generic arguments alike, ref, out and
/// in, a nullable mark on a reference type or on a value type, type parameters and tuples; constructors, a static
/// constructor and a finalizer by their names in metadata; and each declaration named as Toggle Method Breakpoint names it
/// binding in that member alone.</summary>
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
        }),
        ("constructors a static constructor and a finalizer bind by their names in metadata or as C# writes a finalizer", () => Cases.Each(
        [
            ("Shapes<TItem>..ctor(int)", "Void .ctor(Int32)"),
            ("Shapes<TItem>..ctor(ref string? text)", "Void .ctor(System.String ByRef)"),
            ("Shapes<TItem>..cctor", "Void .cctor()"),
            ("Shapes<TItem>.~Shapes()", "Void Finalize()"),
        ], row => Assert.Equal([row.Item2], Members(row.Item1)))),
        ("ref out in a nullable mark type parameters and tuples are matched as the metadata gives them", () => Cases.Each(
        [
            ("Shapes<TItem>.N(ref string?)", "Int32 N(System.String ByRef)"),
            ("Shapes<TItem>.N(string? text)", "Int32 N(System.String)"),
            ("Shapes<TItem>.N(out int)", "Int32 N(Int32 ByRef)"),
            ("Shapes<TItem>.N(scoped in long value)", "Int32 N(Int64 ByRef)"),
            ("Shapes<TItem>.N(Spot?)", $"Int32 N(System.Nullable`1[{typeof(Spot)}])"),
            ("Shapes<TItem>.N(Spot)", "Int32 N(Spot)"),
            ("Shapes<TItem>.N(Shape?)", "Int32 N(Shape)"),
            ("Shapes<TItem>.N(TItem?)", "Int32 N(TItem)"),
            ("Shapes<TItem>.N<T>(T, List<T>)", "Int32 N[T](T, System.Collections.Generic.List`1[T])"),
            ("Shapes<TItem>.N((int Count, string Name))", "Int32 N(System.ValueTuple`2[System.Int32,System.String])"),
        ], row => Assert.Equal([row.Item2], Members(row.Item1)))),
        ("a primary constructor binds by its parameters", () => Assert.Equal(["Void .ctor(Int32, Int32)"], Members("Pair..ctor(int, int)"))),
        ("an alias another file declares binds as the type the server says it stands for", An_alias_another_file_declares_binds_as_the_type_the_server_says_it_stands_for),
        ("each declaration named as toggle method breakpoint names it binds in that member alone", Each_declaration_named_as_toggle_method_breakpoint_names_it_binds_in_that_member_alone));

    /// <summary>A parameter typed by an alias its file never declares (a project's global using, the SDK's for a Using item) names
    /// a type the metadata has none of, and binds nowhere, until the server's word of what it stands for is read in.</summary>
    private static void An_alias_another_file_declares_binds_as_the_type_the_server_says_it_stands_for()
    {
        const string Source = "static class Overloads\n{\n    public static int M(Number value) => value;\n}\n";
        ParsedText parsed = new(CSharpParsing.Parse(Source), TextSnapshot.From(Source));
        int caret = Source.IndexOf("M(", StringComparison.Ordinal);
        Assert.Empty(Members(MethodBreakpoints.At(parsed, caret)!.Value.Name));
        MethodBreakpoints.Method named = MethodBreakpoints.At(parsed, caret, new Dictionary<string, string> { ["Number"] = "int" })!.Value;
        Assert.Equal("Overloads.M(int)", named.Name);
        Assert.Equal(["Int32 M(Int32)"], Members(named.Name));
        Assert.Equal(["Int32 M(Int32)"], Members(MethodBreakpoints.At(parsed, caret, new Dictionary<string, string> { ["Number"] = "global::System.Int32" })!.Value.Name));
    }

    private static void Each_declaration_named_as_toggle_method_breakpoint_names_it_binds_in_that_member_alone()
    {
        ParsedText parsed = new(CSharpParsing.Parse(ShapesSource), TextSnapshot.From(ShapesSource));
        SyntaxNode[] declarations = [.. parsed.Root.DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or ParameterListSyntax { Parent: TypeDeclarationSyntax })];
        Assert.Equal(17, declarations.Length);
        foreach (SyntaxNode declaration in declarations)
        {
            MethodBreakpoints.Method method = MethodBreakpoints.At(parsed, declaration.Span.End - 2)
                ?? throw new InvalidOperationException($"Toggle Method Breakpoint named nothing at {declaration}");
            int parameters = declaration is BaseMethodDeclarationSyntax own ? own.ParameterList.Parameters.Count : ((ParameterListSyntax)declaration).Parameters.Count;
            List<MethodBase> bound = Resolved(method.Name);
            Assert.True(bound is [{ } member] && member.GetParameters().Length == parameters,
                $"{method.Name}, named at {declaration.ToString().Split('\n')[0].Trim()}, bound in {(bound.Count == 0 ? "no member" : string.Join(" and ", bound))}");
        }
    }

    /// <summary>The parameter types of each method a function breakpoint of this name binds in, joined by ", ".</summary>
    private static List<string> Bound(string name) =>
        [.. Resolved(name).Select(method => string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.ToString())))];

    /// <summary>Each member a function breakpoint of this name binds in, as reflection writes it.</summary>
    private static List<string> Members(string name) => [.. Resolved(name).Select(method => method.ToString()!)];

    private static List<MethodBase> Resolved(string name)
    {
        using ModuleMetadataReader reader = ModuleMetadataReader.TryLoad(typeof(Overloads).Assembly.Location)
            ?? throw new InvalidOperationException("The unit tests' own assembly could not be read");
        Assert.True(reader.HasSymbols, "The unit tests' own assembly was read without its symbols, which bind a function breakpoint");
        return [.. FunctionBreakpointMetadataResolver.Resolve(reader, FunctionBreakpointPattern.Parse(name))
            .Select(resolved => typeof(Overloads).Module.ResolveMethod(resolved.MethodToken) ?? throw new InvalidOperationException($"No method 0x{resolved.MethodToken:X8}"))];
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

    /// <summary><see cref="Shapes{TItem}"/>'s and <see cref="Pair"/>'s declarations as this file writes them, which Toggle
    /// Method Breakpoint names from their parse.</summary>
    private const string ShapesSource = """
        using Words = System.Collections.Generic.List<string>;

        namespace Passenger.UnitTests.Debugger;

        public class FunctionBreakpointResolverTests
        {
            private sealed class Shapes<TItem>
            {
                static Shapes() { }
                public Shapes() { }
                public Shapes(int count) { }
                public Shapes(ref string? text) { }
                ~Shapes() { }
                public static int N(ref string? text) => 0;
                public static int N(string? text) => 0;
                public static int N(out int value) => value = 0;
                public static int N(scoped in long value) => 0;
                public static int N(Spot? at) => 0;
                public static int N(Spot at) => 0;
                public static int N(Shape? shape) => 0;
                public static int N(TItem? item) => 0;
                public static int N<T>(T item, List<T> items) => 0;
                public static int N((int Count, string Name) pair) => 0;
                public static int N(Words words) => 0;
            }

            private sealed class Pair(int left, int right)
            {
                public int Sum { get; } = left + right;
            }
        }
        """;

    private sealed class Shapes<TItem>
    {
        static Shapes() { }
        public Shapes() { }
        public Shapes(int count) { }
        public Shapes(ref string? text) { }
        ~Shapes() { }
        public static int N(ref string? text) => 0;
        public static int N(string? text) => 0;
        public static int N(out int value) => value = 0;
        public static int N(scoped in long value) => 0;
        public static int N(Spot? at) => 0;
        public static int N(Spot at) => 0;
        public static int N(Shape? shape) => 0;
        public static int N(TItem? item) => 0;
        public static int N<T>(T item, List<T> items) => 0;
        public static int N((int Count, string Name) pair) => 0;
        public static int N(Words words) => 0;
    }

    private sealed class Pair(int left, int right)
    {
        public int Sum { get; } = left + right;
    }

    private readonly record struct Spot(int X);

    private sealed class Shape;
}
