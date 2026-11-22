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
    [Fact]
    public void A_frames_method_is_named_as_its_code_names_it() => Cases.All(
        ("a lambda and a local function are named by the method they are written in", () => Cases.Each(
        [
            ("var app = new Router();\napp.MapGet(\"/add/{a}/{b}\", (int a, int b) => a + b);\nclass Router { public void MapGet(string route, System.Func<int, int, int> handler) { } }",
                "", "Program.<Main>$.lambda(int a, int b)"),
            ("namespace Shop;\nclass Basket { int Total(int[] prices) { int tax = 2; return prices.Sum(p => p + tax); } }", "", "Shop.Basket.Total.lambda(int p)"),
            ("class Basket { System.Func<System.Func<int>> Nested() => () => () => 1; }", "", "Basket.Nested.lambda()|Basket.Nested.lambda()"),
            ("class Basket { int Sum(int x) { int k = 1; return Add(x); int Add(int y) => y + k; } }", "", "Basket.Sum.Add(int y)"),
            ("class Box<T> { public U Made<U>(T item) where U : new() { return New<int>(1); static U New<V>(V v) => new U(); } }", "int,string,long", "Box<int>.Made<string>.New<long>(long v)"),
        ], row =>
        {
            (string code, string arguments, string expected) = row;
            using PEReader image = Built(code);
            MetadataReader reader = image.GetMetadataReader();
            string[] given = arguments.Length == 0 ? [] : arguments.Split(',');
            List<string> named = [.. reader.MethodDefinitions
                .Where(handle => reader.GetString(reader.GetMethodDefinition(handle).Name) is ['<', ..] name && (name.Contains(">b__", StringComparison.Ordinal) || name.Contains(">g__", StringComparison.Ordinal)))
                .Select(handle => Named(reader, handle, given))
                .Order(StringComparer.Ordinal)];
            Assert.True(string.Join('|', named) == expected, $"The compiler's methods of\n{code}\nare named [{string.Join(", ", named)}], where the code names them [{expected.Replace('|', ',')}]");
        }, row => row.Item3)),
        ("a method of the code's own is named by its type and its name, a by-reference parameter as declared", () => Cases.Each(
        [
            ("class Outer<T> { public class Inner { public void Run(T value) { } } }", "Run", "int", "Outer<int>.Inner.Run(int value)"),
            ("struct Point { public int X; }\nclass Reader { int Read(in Point point, ref int count, out int sum, ref readonly int limit) { sum = 0; return 0; } }",
                "Read", "", "Reader.Read(in Point point, ref int count, out int sum, ref readonly int limit)"),
        ], row =>
        {
            (string code, string method, string arguments, string expected) = row;
            using PEReader image = Built(code);
            MetadataReader reader = image.GetMetadataReader();
            MethodDefinitionHandle handle = reader.MethodDefinitions.Single(h => reader.StringComparer.Equals(reader.GetMethodDefinition(h).Name, method));
            Assert.Equal(expected, Named(reader, handle, arguments.Length == 0 ? [] : arguments.Split(',')));
        }, row => row.Item4)));

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
