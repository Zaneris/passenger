using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Passenger.Debugger;

namespace Passenger.UnitTests.Debugger;

/// <summary>A frame's method named as its code names it: a lambda by the method it is written in, then "lambda", and a local
/// function by that method, then its own name, whichever type the compiler keeps its closure in, with the type arguments
/// the frame runs with given to the type, the method and the local function each declaring them.</summary>
public class FrameNamesTests
{
    [Theory]
    [InlineData("var app = new Router();\napp.MapGet(\"/add/{a}/{b}\", (int a, int b) => a + b);\nclass Router { public void MapGet(string route, System.Func<int, int, int> handler) { } }",
        "", "Program.<Main>$.lambda(int a, int b)")]
    [InlineData("namespace Shop;\nclass Basket { int Total(int[] prices) { int tax = 2; return prices.Sum(p => p + tax); } }", "", "Shop.Basket.Total.lambda(int p)")]
    [InlineData("class Basket { int rate; System.Func<int, int> Rated() => p => p * rate; }", "", "Basket.Rated.lambda(int p)")]
    [InlineData("class Basket { System.Func<System.Func<int>> Nested() => () => () => 1; }", "", "Basket.Nested.lambda()|Basket.Nested.lambda()")]
    [InlineData("class Basket { System.Func<int, Task<int>> Later() => async p => { await Task.Yield(); return p; }; }", "", "Basket.Later.lambda(int p)")]
    [InlineData("class Basket { int Twice(int x) { return Double(x); static int Double(int y) => y * 2; } }", "", "Basket.Twice.Double(int y)")]
    [InlineData("class Basket { int Sum(int x) { int k = 1; return Add(x); int Add(int y) => y + k; } }", "", "Basket.Sum.Add(int y)")]
    [InlineData("class Box<T> { public List<U> Paired<U>(T[] items, U with) => items.Select(i => with).ToList(); }", "int,string", "Box<int>.Paired<string>.lambda(int i)")]
    [InlineData("class Box<T> { public T Kept(T item) { return Same(item); static V Same<V>(V v) => v; } }", "int,int", "Box<int>.Kept.Same<int>(int v)")]
    [InlineData("class Box<T> { public U Made<U>(T item) where U : new() { return New<int>(1); static U New<V>(V v) => new U(); } }", "int,string,long", "Box<int>.Made<string>.New<long>(long v)")]
    [InlineData("class Box<T> { public void Made<U>() { System.Action a = () => { }; } public void Made<U, V>() { } }", "int,string", "Box<int>.Made.lambda()")]
    public void A_lambda_and_a_local_function_are_named_by_the_method_they_are_written_in(string code, string arguments, string expected)
    {
        using PEReader image = Built(code);
        MetadataReader reader = image.GetMetadataReader();
        string[] given = arguments.Length == 0 ? [] : arguments.Split(',');
        List<string> named = [.. reader.MethodDefinitions
            .Where(handle => reader.GetString(reader.GetMethodDefinition(handle).Name) is ['<', ..] name && (name.Contains(">b__", StringComparison.Ordinal) || name.Contains(">g__", StringComparison.Ordinal)))
            .Select(handle => Named(reader, handle, given))
            .Order(StringComparer.Ordinal)];
        Assert.True(string.Join('|', named) == expected, $"The compiler's methods of\n{code}\nare named [{string.Join(", ", named)}], where the code names them [{expected.Replace('|', ',')}]");
    }

    [Theory]
    [InlineData("namespace Shop;\nclass Basket { int Total(int[] prices) => prices.Length; }", "Total", "", "Shop.Basket.Total(int[] prices)")]
    [InlineData("class Box<T> { public U Map<U>(T value) => default!; }", "Map", "int,string", "Box<int>.Map<string>(int value)")]
    [InlineData("class Outer<T> { public class Inner { public void Run(T value) { } } }", "Run", "int", "Outer<int>.Inner.Run(int value)")]
    public void A_method_of_the_codes_own_is_named_by_its_type_and_its_name(string code, string method, string arguments, string expected)
    {
        using PEReader image = Built(code);
        MetadataReader reader = image.GetMetadataReader();
        MethodDefinitionHandle handle = reader.MethodDefinitions.Single(h => reader.StringComparer.Equals(reader.GetMethodDefinition(h).Name, method));
        Assert.Equal(expected, Named(reader, handle, arguments.Length == 0 ? [] : arguments.Split(',')));
    }

    /// <summary>A method named with the type arguments given, the first its type's and the rest its own.</summary>
    private static string Named(MetadataReader reader, MethodDefinitionHandle handle, string[] arguments)
    {
        MethodDefinition method = reader.GetMethodDefinition(handle);
        int ofType = reader.GetTypeDefinition(method.GetDeclaringType()).GetGenericParameters().Count;
        int ofMethod = method.GetGenericParameters().Count;
        Assert.True(ofType + ofMethod <= arguments.Length, $"{reader.GetString(method.Name)} takes {ofType + ofMethod} type arguments, more than the {arguments.Length} given");
        return FrameNames.Of(reader, handle, arguments[..ofType], arguments[ofType..(ofType + ofMethod)]);
    }

    /// <summary>The code compiled in memory, as a program where it has top-level statements, else a library.</summary>
    private static PEReader Built(string code)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(code);
        SyntaxTree usings = CSharpSyntaxTree.ParseText("global using System.Linq;\nglobal using System.Collections.Generic;\nglobal using System.Threading.Tasks;\n");
        bool program = tree.GetRoot().ChildNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax>().Any();
        IEnumerable<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is { } name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard.dll" or "mscorlib.dll"))
            .Select(p => MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create("Framed", [tree, usings], references,
            new CSharpCompilationOptions(program ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary));
        MemoryStream built = new();
        EmitResult emitted = compilation.Emit(built);
        Assert.True(emitted.Success, string.Join('\n', emitted.Diagnostics));
        built.Position = 0;
        return new PEReader(built);
    }
}
