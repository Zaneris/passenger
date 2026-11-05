using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>The JavaScript a C# or Razor file calls through .NET's JS interop (<see cref="JsInteropSource"/>).</summary>
public sealed class JsInteropCalls : IExtensionWorkspaceMessageHandler<JsInteropRequest, JsInteropFound>
{
    public Task<JsInteropFound> ExecuteAsync(JsInteropRequest message, ExtensionMessageContext context, CancellationToken cancellationToken) =>
        JsInteropSource.CallsAsync(context.Solution, message.Path, cancellationToken);
}

/// <summary>The JavaScript a file's code calls, as Roslyn binds the calls, never by names alone: the constant identifier given
/// to a method of Microsoft.JSInterop's runtimes and object references (InvokeAsync, InvokeVoidAsync, Invoke, their
/// constructors and values), with the module a reference was imported from where the file's code assigns it the
/// "import" of a constant path; and that path itself. A Razor file is read through the C# Razor's generator makes of it,
/// each call placed in the .razor by the generated code's line directives. The answer names the file's projects and those
/// they reference or are referenced by, whose scripts the calls may reach.</summary>
internal static class JsInteropSource
{
    private const string Namespace = "Microsoft.JSInterop";

    private const string Import = JsInteropNames.Import;

    /// <summary>The types whose methods take an identifier of JavaScript.</summary>
    private static readonly HashSet<string> Types =
    [
        "IJSRuntime", "IJSObjectReference", "IJSInProcessRuntime", "IJSInProcessObjectReference", "JSRuntimeExtensions",
        "JSObjectReferenceExtensions", "JSInProcessRuntimeExtensions", "JSInProcessObjectReferenceExtensions", "JSRuntime", "JSInProcessRuntime",
    ];

    public static async Task<JsInteropFound> CallsAsync(Solution solution, string path, CancellationToken cancel)
    {
        List<JsInteropCall> calls = [];
        HashSet<(int Line, int Character)> seen = [];
        HashSet<string> projects = new(PathNames.Comparer);
        List<string> ordered = [];
        bool razor = RazorFiles.Is(path);
        foreach ((Document document, Project project) in await LocalizationSource.DocumentsOfAsync(solution, path, razor, cancel).ConfigureAwait(false))
        {
            foreach (Project near in Near(solution, project))
            {
                if (near.FilePath is { } file && projects.Add(file))
                {
                    ordered.Add(file);
                }
            }
            if (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root || !MayCall(root)
                || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
            {
                continue;
            }
            SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
            List<(InvocationExpressionSyntax Call, ExpressionSyntax Identifier, string Name, IMethodSymbol Method)> found = [.. Interop(root, model, cancel)];
            Dictionary<ISymbol, string> modules = new(SymbolEqualityComparer.Default);
            foreach ((InvocationExpressionSyntax call, ExpressionSyntax identifier, string name, IMethodSymbol method) in found)
            {
                if (name == Import && JsInteropImport.ModulePath(call, method, model, cancel) is ({ } given, { } module))
                {
                    Add(given, Import, module);
                    if (AssignedTo(call, model, cancel) is { } holder)
                    {
                        modules[holder] = module;
                    }
                }
            }
            foreach ((InvocationExpressionSyntax call, ExpressionSyntax identifier, string name, IMethodSymbol method) in found)
            {
                if (name == Import)
                {
                    continue;
                }
                string? module = Receiver(call, method) is { } receiver && model.GetSymbolInfo(receiver, cancel).Symbol is { } held
                    && modules.TryGetValue(held, out string? imported) ? imported : null;
                Add(identifier, name, module);
            }

            void Add(ExpressionSyntax where, string identifier, string? module)
            {
                Place at;
                Place end;
                if (razor)
                {
                    FileLinePositionSpan mapped = root.SyntaxTree.GetMappedLineSpan(where.Span, cancel);
                    if (!mapped.HasMappedPath || !SamePath(mapped.Path, path))
                    {
                        return;
                    }
                    (at, end) = (new Place(mapped.StartLinePosition.Line, mapped.StartLinePosition.Character), new Place(mapped.EndLinePosition.Line, mapped.EndLinePosition.Character));
                }
                else
                {
                    (at, end) = (PlaceAt(text, where.Span.Start), PlaceAt(text, where.Span.End));
                }
                if (seen.Add((at.Line, at.Character)))
                {
                    calls.Add(new JsInteropCall(identifier, at, end, module));
                }
            }
        }
        return new JsInteropFound(calls, ordered);
    }

    /// <summary>A project, then those it references, then those that reference it: an app's client and its host share scripts.</summary>
    private static IEnumerable<Project> Near(Solution solution, Project project)
    {
        yield return project;
        foreach (ProjectReference reference in project.ProjectReferences)
        {
            if (solution.GetProject(reference.ProjectId) is { } referenced)
            {
                yield return referenced;
            }
        }
        foreach (Project other in solution.Projects)
        {
            if (other.ProjectReferences.Any(r => r.ProjectId == project.Id))
            {
                yield return other;
            }
        }
    }

    /// <summary>Whether a file's code may call JavaScript at all, read before any binding.</summary>
    private static bool MayCall(SyntaxNode root) =>
        root.DescendantTokens().Any(static t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText is "InvokeAsync" or "InvokeVoidAsync" or "Invoke" or "InvokeVoid"
            or "InvokeConstructorAsync" or "InvokeConstructor" or "GetValueAsync" or "GetValue" or "SetValueAsync" or "SetValue" or "InvokeNew" or "InvokeNewAsync");

    /// <summary>Each call of JS interop with a constant identifier: the call, the identifier's expression, its value and the method.</summary>
    private static IEnumerable<(InvocationExpressionSyntax, ExpressionSyntax, string, IMethodSymbol)> Interop(SyntaxNode root, SemanticModel model, CancellationToken cancel)
    {
        foreach (InvocationExpressionSyntax call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!MayCall(call.Expression) || model.GetSymbolInfo(call, cancel).Symbol is not IMethodSymbol method || !IsInterop(method)
                || Argument(call, method, "identifier") is not { } identifier || Constant(identifier, model, cancel) is not { } name)
            {
                continue;
            }
            yield return (call, identifier, name, method);
        }
    }

    /// <summary>Whether a method is one of JS interop's that takes an identifier of JavaScript.</summary>
    private static bool IsInterop(IMethodSymbol method)
    {
        INamedTypeSymbol type = (method.ReducedFrom ?? method).ContainingType;
        return type.ContainingNamespace?.ToDisplayString() == Namespace && Types.Contains(type.Name)
            && (method.ReducedFrom ?? method).Parameters.Any(p => p.Name == "identifier" && p.Type.SpecialType == SpecialType.System_String);
    }

    /// <summary>The field, property, local or parameter an "import" call's module is assigned or initialized to, through its
    /// await, parentheses or a cast.</summary>
    private static ISymbol? AssignedTo(InvocationExpressionSyntax call, SemanticModel model, CancellationToken cancel)
    {
        SyntaxNode node = call;
        while (node.Parent is AwaitExpressionSyntax or ParenthesizedExpressionSyntax or CastExpressionSyntax
            || node.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AsTask" or "ConfigureAwait" } && node.Parent.Parent is InvocationExpressionSyntax)
        {
            node = node.Parent is MemberAccessExpressionSyntax ? node.Parent.Parent! : node.Parent;
        }
        return node.Parent switch
        {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } => model.GetDeclaredSymbol(declarator, cancel),
            EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax property } => model.GetDeclaredSymbol(property, cancel),
            AssignmentExpressionSyntax assignment when assignment.Right == node => model.GetSymbolInfo(assignment.Left, cancel).Symbol,
            _ => null,
        };
    }
}
