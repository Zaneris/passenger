using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Roslyn.Extension;

namespace Passenger.UnitTests.Roslyn;

/// <summary>The module an "import" call of JS interop names, against Microsoft.JSInterop's overloads as Roslyn binds a call to
/// them: the path is found past a cancellation token or a timeout, however the overload is called, and what is no
/// constant path is none.</summary>
public class JsInteropImportTests
{
    /// <summary>The runtime's interface and extensions, as Microsoft.JSInterop declares them.</summary>
    private const string Interop = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Microsoft.JSInterop
        {
            public interface IJSObjectReference
            {
            }

            public interface IJSRuntime
            {
                ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args);

                ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args);
            }

            public static class JSRuntimeExtensions
            {
                public static ValueTask<TValue> InvokeAsync<TValue>(this IJSRuntime jsRuntime, string identifier, params object?[]? args) => default;

                public static ValueTask<TValue> InvokeAsync<TValue>(this IJSRuntime jsRuntime, string identifier, CancellationToken cancellationToken, params object?[]? args) => default;

                public static ValueTask<TValue> InvokeAsync<TValue>(this IJSRuntime jsRuntime, string identifier, TimeSpan timeout, params object?[]? args) => default;
            }
        }
        """;

    /// <summary>The call's method and the path found in it, the call written in a method that has a runtime and a token.</summary>
    private static string? PathOf(string call)
    {
        string code = $$"""
            using System;
            using System.Threading;
            using Microsoft.JSInterop;

            public class Page
            {
                private const string Path = "./b.js";

                public void Load(IJSRuntime js, CancellationToken cancel, string chosen)
                {
                    _ = {{call}};
                }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(code);
        IEnumerable<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is { } name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard.dll" or "mscorlib.dll"))
            .Select(p => MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create("Calls", [tree, CSharpSyntaxTree.ParseText(Interop)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        SemanticModel model = compilation.GetSemanticModel(tree);
        InvocationExpressionSyntax invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().First();
        IMethodSymbol method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol
            ?? throw new InvalidOperationException($"{call} binds to no method: {string.Join("; ", compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))}");
        return JsInteropImport.ModulePath(invocation, method, model, CancellationToken.None).Path;
    }

    [Theory]
    [InlineData("js.InvokeAsync<IJSObjectReference>(\"import\", \"./b.js\")")]
    [InlineData("js.InvokeAsync<IJSObjectReference>(\"import\", cancel, \"./b.js\")")]
    [InlineData("js.InvokeAsync<IJSObjectReference>(\"import\", TimeSpan.FromSeconds(1), \"./b.js\")")]
    [InlineData("js.InvokeAsync<IJSObjectReference>(\"import\", new object?[] { \"./b.js\" })")]
    [InlineData("js.InvokeAsync<IJSObjectReference>(\"import\", cancel, new object?[] { \"./b.js\" })")]
    [InlineData("js.InvokeAsync<IJSObjectReference>(\"import\", cancel, new[] { \"./b.js\" })")]
    [InlineData("js.InvokeAsync<IJSObjectReference>(\"import\", cancel, [\"./b.js\"])")]
    [InlineData("js.InvokeAsync<IJSObjectReference>(\"import\", args: new object?[] { \"./b.js\" })")]
    [InlineData("js.InvokeAsync<IJSObjectReference>(identifier: \"import\", cancellationToken: cancel, args: new object?[] { \"./b.js\" })")]
    [InlineData("js.InvokeAsync<IJSObjectReference>(\"import\", cancel, Path)")]
    [InlineData("JSRuntimeExtensions.InvokeAsync<IJSObjectReference>(js, \"import\", cancel, \"./b.js\")")]
    public void The_module_is_the_first_of_what_the_called_overload_gives_javascript(string call) =>
        Assert.True(PathOf(call) == "./b.js", $"{call} named {PathOf(call) ?? "no module"}, so the calls on what it imports would go to any script declaring their name");

    [Theory]
    [InlineData("js.InvokeAsync<IJSObjectReference>(\"import\", chosen)")]
    [InlineData("js.InvokeAsync<IJSObjectReference>(\"import\", cancel, chosen)")]
    [InlineData("js.InvokeAsync<IJSObjectReference>(\"import\", cancel)")]
    [InlineData("js.InvokeAsync<IJSObjectReference>(\"import\", new object?[0])")]
    public void A_call_giving_no_constant_path_names_no_module(string call) =>
        Assert.True(PathOf(call) is null, $"{call} named the module {PathOf(call)}");
}
