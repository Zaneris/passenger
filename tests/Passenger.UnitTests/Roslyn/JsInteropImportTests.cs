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

    /// <summary>A module is the first of what the called overload gives JavaScript, else none.</summary>
    [Fact]
    public void A_modules_import_is_the_path_the_overload_gives() => Cases.All(
        (nameof(The_module_is_the_first_of_what_the_called_overload_gives_javascript),
            The_module_is_the_first_of_what_the_called_overload_gives_javascript),
        (nameof(A_call_giving_no_constant_path_names_no_module), A_call_giving_no_constant_path_names_no_module));

    private void The_module_is_the_first_of_what_the_called_overload_gives_javascript() => Cases.Each<string>(
    [
        "js.InvokeAsync<IJSObjectReference>(\"import\", \"./b.js\")",
        "js.InvokeAsync<IJSObjectReference>(\"import\", cancel, \"./b.js\")",
        "js.InvokeAsync<IJSObjectReference>(\"import\", new object?[] { \"./b.js\" })",
        "js.InvokeAsync<IJSObjectReference>(\"import\", args: new object?[] { \"./b.js\" })",
        "js.InvokeAsync<IJSObjectReference>(\"import\", cancel, Path)",
        "JSRuntimeExtensions.InvokeAsync<IJSObjectReference>(js, \"import\", cancel, \"./b.js\")",
    ], The_module_is_the_first_of_what_the_called_overload_gives_javascript);

    private void The_module_is_the_first_of_what_the_called_overload_gives_javascript(string call) =>
        Assert.True(PathOf(call) == "./b.js", $"{call} named {PathOf(call) ?? "no module"}, so the calls on what it imports would go to any script declaring their name");

    private void A_call_giving_no_constant_path_names_no_module() => Cases.Each<string>(
    [
        "js.InvokeAsync<IJSObjectReference>(\"import\", chosen)",
        "js.InvokeAsync<IJSObjectReference>(\"import\", cancel, chosen)",
        "js.InvokeAsync<IJSObjectReference>(\"import\", cancel)",
        "js.InvokeAsync<IJSObjectReference>(\"import\", new object?[0])",
    ], A_call_giving_no_constant_path_names_no_module);

    private void A_call_giving_no_constant_path_names_no_module(string call) =>
        Assert.True(PathOf(call) is null, $"{call} named the module {PathOf(call)}");
}
